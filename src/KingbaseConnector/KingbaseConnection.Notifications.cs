using System.Runtime.ExceptionServices;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

public sealed partial class KingbaseConnection
{
    ExceptionDispatchInfo? _callbackFailure;
    [ThreadStatic] static KingbaseConnection? _callbackContext;
    internal bool IsInsideCallback => ReferenceEquals(_callbackContext, this);
    public event EventHandler<KingbaseNotificationEventArgs>? Notification;
    public event EventHandler<KingbaseNoticeEventArgs>? Notice;

    /// <summary>Waits for a notification; -1 is infinite, zero polls. No SQL is sent.</summary>
    public bool Wait(int timeoutMilliseconds = -1) => WaitAsync(timeoutMilliseconds).GetAwaiter().GetResult();
    public bool Wait(TimeSpan timeout) => Wait(NotificationTimeout(timeout));
    public Task<bool> WaitAsync(CancellationToken cancellationToken) => WaitAsync(-1, cancellationToken);
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => WaitAsync(NotificationTimeout(timeout), cancellationToken);

    public async Task<bool> WaitAsync(int timeoutMilliseconds = -1, CancellationToken cancellationToken = default)
    {
        if (timeoutMilliseconds < -1) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var marker = new KingbaseCommand();
        PgPhysicalSession session; long generation;
        lock (_sync)
        {
            var physical = OpenSession();
            if (_command is not null || _reader is not null) throw ActiveOperation();
            if (_transaction is not null || physical.Queries.TransactionStatus != PgTransactionStatus.Idle)
                throw new InvalidOperationException("Notification waiting requires an idle connection outside a transaction.");
            (session, generation) = BeginExecution(marker, operation);
        }
        try
        {
            var received = await session.Queries.WaitForNotificationAsync(timeoutMilliseconds, operation.Token, () => ValidateCallbackSession(session),
                () => HasCallbackFailure(session, generation)).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            ThrowCallbackFailure(session, generation);
            return received;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (PgQueryException error) { throw new KingbaseException(error.ServerError); }
        finally { EndExecution(marker, session, generation); RetireIfBroken(session); }
    }

    static int NotificationTimeout(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return -1;
        if (timeout.Ticks < 0 || timeout.Ticks % TimeSpan.TicksPerMillisecond != 0 || timeout.Ticks / TimeSpan.TicksPerMillisecond > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Notification timeout requires exact milliseconds within Int32 or -1ms.");
        return (int)(timeout.Ticks / TimeSpan.TicksPerMillisecond);
    }

    static void ValidateCallbackSession(PgPhysicalSession session)
    {
        var queries = session.Queries;
        if (!queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8" ||
            (queries.Parameters.TryGetValue("database_mode", out var mode) && PgIdentityProbe.ParseMode(mode) != session.Identity.EffectiveMode))
        { session.Dispose(); throw new NotSupportedException("The callback session identity or encoding changed."); }
    }

    void DispatchNotification(PgPhysicalSession session, long generation, KingbaseNotificationEventArgs notification) =>
        DispatchCallback(session, generation, Notification, notification);

    void DispatchNotice(PgPhysicalSession session, long generation, KingbaseNotice notice) =>
        DispatchCallback(session, generation, Notice, new KingbaseNoticeEventArgs(notice));

    void DispatchCallback<T>(PgPhysicalSession session, long generation, EventHandler<T>? handler, T args) where T : EventArgs
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || _generation != generation || _command is null) return;
            ValidateCallbackSession(session);
        }
        var previous = _callbackContext; _callbackContext = this;
        try { handler?.Invoke(this, args); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            lock (_sync) if (ReferenceEquals(_session, session) && _generation == generation) _callbackFailure ??= ExceptionDispatchInfo.Capture(error);
        }
        finally { _callbackContext = previous; }
    }

    internal bool HasCallbackFailure(PgPhysicalSession session, long generation)
    {
        lock (_sync) return ReferenceEquals(_session, session) && _generation == generation && _callbackFailure is not null;
    }

    internal void ThrowCallbackFailure(PgPhysicalSession session, long generation)
    {
        ExceptionDispatchInfo? failure;
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || _generation != generation) return;
            failure = _callbackFailure;
            _callbackFailure = null;
        }
        failure?.Throw();
    }
}
