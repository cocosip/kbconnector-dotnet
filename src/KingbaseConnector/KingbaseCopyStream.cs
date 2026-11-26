using System.Diagnostics;
#if !NETSTANDARD2_0
using System.Runtime.InteropServices;
#endif
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>Owns a client COPY exchange. Raw binary import requires explicit Complete.</summary>
public sealed class KingbaseCopyStream : Stream
{
    readonly KingbaseConnection _connection;
    readonly PgPhysicalSession _session;
    readonly long _generation;
    readonly KingbaseCommand _marker;
    readonly CancellationTokenSource _lifetime;
    readonly CancellationToken _lifetimeToken;
    readonly CancellationToken _caller;
    readonly int _timeout;
    readonly int _cleanupTimeout;
    readonly Stopwatch _elapsed = Stopwatch.StartNew();
    readonly CancellationTokenSource _stopTimer = new();
    readonly Task _timer;
    readonly CancellationTokenRegistration _abort;
    PgQuerySession.CopyExchange? _exchange;
    int _busy;
    int _finished;
    int _timedOut;
    bool _disposed;
    bool _completed;

    internal KingbaseCopyStream(KingbaseConnection connection, PgPhysicalSession session, long generation,
        KingbaseCommand marker, CancellationTokenSource lifetime, CancellationToken caller, int timeout, int cleanupTimeout)
    {
        _connection = connection; _session = session; _generation = generation; _marker = marker; _lifetime = lifetime;
        _lifetimeToken = lifetime.Token; _caller = caller; _timeout = timeout; _cleanupTimeout = cleanupTimeout;
        _abort = _lifetimeToken.Register(Abort);
        _timer = timeout == 0 ? Task.CompletedTask : WatchApplicationDeadlineAsync();
        _ = _timer.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    async Task WatchApplicationDeadlineAsync()
    {
        using var deadline = new CancellationTokenSource();
        using var registration = deadline.Token.Register(() => { Volatile.Write(ref _timedOut, 1); _lifetime.Cancel(); });
        await KingbaseCommand.WatchDeadline((long)_timeout * 1000, _elapsed, deadline, _stopTimer.Token).ConfigureAwait(false);
    }

    public bool IsImport => _exchange?.IsImport == true;
    public bool IsBinary => _exchange?.IsBinary == true;
    public int ColumnCount => _exchange?.ColumnCount ?? 0;
    public ulong? RowsCopied => _completed ? _exchange?.RowsCopied : null;
    internal Action<ulong>? CompletionValidator { get; set; }
    internal bool HasFinished => Volatile.Read(ref _finished) != 0;
    public override bool CanRead => !_disposed && _finished == 0 && !IsImport && _session.IsUsable;
    public override bool CanWrite => !_disposed && _finished == 0 && IsImport && _session.IsUsable;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    internal async Task StartAsync(string sql, bool binary, bool? import)
    {
        try
        {
            _exchange = _session.Queries.BeginCopy(() => Validate());
            await _exchange.StartAsync(sql, _lifetimeToken).ConfigureAwait(false);
            if (_exchange.IsBinary != binary || (import.HasValue && _exchange.IsImport != import.Value))
            {
                await CancelCoreAsync(CancellationToken.None, deliverCallbacks: false).ConfigureAwait(false);
                throw new NotSupportedException("The server COPY direction or format does not match the requested API.");
            }
        }
        catch (Exception error) { throw Failure(error, _caller); }
    }

    void Validate()
    {
        var queries = _session.Queries;
        if (!queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8" ||
            (queries.Parameters.TryGetValue("database_mode", out var mode) && PgIdentityProbe.ParseMode(mode) != _session.Identity.EffectiveMode))
            throw new NotSupportedException("The COPY session identity or encoding changed.");
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count); cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) throw new ObjectDisposedException(nameof(KingbaseCopyStream));
        if (_completed && !IsImport) return 0;
        Enter(input: false);
        try
        {
            if (count == 0) return 0;
            int size;
            using (var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken))
            using (call.Token.Register(Abort))
            {
                size = await _exchange!.ReadAsync(buffer, offset, count, call.Token).ConfigureAwait(false);
                if (size != 0) call.Token.ThrowIfCancellationRequested();
            }
            if (size == 0) CompleteAcknowledged();
            return size;
        }
        catch (Exception error) { throw Failure(error, cancellationToken); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count); cancellationToken.ThrowIfCancellationRequested(); Enter(input: true);
        try
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
            using var abort = call.Token.Register(Abort);
            await _exchange!.WriteAsync(buffer, offset, count, call.Token).ConfigureAwait(false); call.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { throw Failure(error, cancellationToken); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public ulong Complete() => CompleteAsync().GetAwaiter().GetResult();
    public async Task<ulong> CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectCallbackReentry();
        if (_completed) return RowsCopied!.Value;
        Enter(input: true);
        try
        {
            using (var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken))
            using (call.Token.Register(Abort))
                await _exchange!.CompleteAsync(call.Token).ConfigureAwait(false);
            CompleteAcknowledged(); return RowsCopied!.Value;
        }
        catch (Exception error) { throw Failure(error, cancellationToken); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Cancel() => CancelAsync().GetAwaiter().GetResult();
    public Task CancelAsync(CancellationToken cancellationToken = default) => CancelCoreAsync(cancellationToken, deliverCallbacks: true);
    async Task CancelCoreAsync(CancellationToken cancellationToken, bool deliverCallbacks)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectCallbackReentry();
        if (Volatile.Read(ref _finished) != 0) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A COPY stream operation is already active; cancel its token or close the connection to abort I/O.");
        try
        {
            if (Volatile.Read(ref _finished) != 0) return;
            _stopTimer.Cancel();
            if (_exchange is null || !_exchange.IsImport) { Abort(); return; }
            if (!_session.IsUsable)
            {
                _exchange.ThrowIfReceiveEnded();
                throw new IOException("The COPY input session ended before cancellation was acknowledged.");
            }
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var stop = new CancellationTokenSource();
            var timer = KingbaseCommand.WatchDeadline((long)_cleanupTimeout * 1000, Stopwatch.StartNew(), cleanup, stop.Token);
            using var abort = cleanup.Token.Register(Abort);
            try { await _exchange.CancelAsync(cleanup.Token).ConfigureAwait(false); Observe(); if (deliverCallbacks) _connection.ThrowCallbackFailure(_session, _generation); }
            catch (Exception) when (cleanup.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("The COPY cleanup deadline elapsed; the physical session was retired."); }
            finally { stop.Cancel(); await timer.ConfigureAwait(false); }
        }
        catch (Exception error) { throw Failure(error, cancellationToken); }
        finally { Finish(); Volatile.Write(ref _busy, 0); }
    }

    void Enter(bool input)
    {
        RejectCallbackReentry();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A COPY stream operation is already active.");
        try
        {
            if (_disposed || Volatile.Read(ref _finished) != 0) throw new ObjectDisposedException(nameof(KingbaseCopyStream));
            if (input != IsImport) throw new NotSupportedException("This COPY stream does not support that direction.");
        }
        catch { Volatile.Write(ref _busy, 0); throw; }
    }
    internal void RejectCallbackReentry()
    {
        if (_connection.IsInsideCallback) throw new InvalidOperationException("A COPY callback cannot reenter its COPY resource.");
    }

    internal void CheckBufferedAccess()
    {
        RejectCallbackReentry();
        if (_disposed) throw new ObjectDisposedException(nameof(KingbaseCopyStream));
        if (_completed && !IsImport) return;
        try
        {
            _lifetimeToken.ThrowIfCancellationRequested();
            if (HasFinished) throw new ObjectDisposedException(nameof(KingbaseCopyStream));
            if (!_session.IsUsable)
            {
                _exchange?.ThrowIfReceiveEnded();
                throw new IOException("The COPY session is no longer usable.");
            }
            _connection.ValidateReaderOwner(_marker, _session, _generation, null);
        }
        catch (Exception error) { throw Failure(error, CancellationToken.None); }
    }

    void CompleteAcknowledged()
    {
        try { CompletionValidator?.Invoke(_exchange!.RowsCopied!.Value); }
        catch { Abort(); throw; }
        _completed = true; Observe();
        try { _connection.ThrowCallbackFailure(_session, _generation); }
        finally { Finish(); }
    }

    void Observe() => _connection.ObserveTransaction(_marker, _session, _generation, KingbaseTransactionOperation.None, true,
        _session.Queries.TransactionStatus, false);

    Exception Failure(Exception error, CancellationToken token)
    {
        if (Volatile.Read(ref _finished) == 0)
        {
            if (_exchange?.IsSynchronized != true) Abort();
            else if (_session.IsUsable) { try { Observe(); } catch { Abort(); } }
            Finish();
        }
        if (token.IsCancellationRequested) return new OperationCanceledException(token);
        if (_caller.IsCancellationRequested) return new OperationCanceledException(_caller);
        if (!_completed && Volatile.Read(ref _timedOut) != 0)
            return new TimeoutException("The Kingbase COPY deadline elapsed; completion may be unknown.");
        if (_lifetimeToken.IsCancellationRequested) return new OperationCanceledException(_lifetimeToken);
        return error is PgQueryException query ? new KingbaseException(query.ServerError) : error;
    }

    internal void Abort()
    {
        try { _connection.AbortExecution(_marker, _session, _generation); } catch { }
    }

    void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        CompletionValidator = null;
        _exchange?.Release(); _abort.Dispose(); _stopTimer.Cancel();
        _connection.EndExecution(_marker, _session, _generation); _connection.RetireIfBroken(_session);
        _marker.Dispose(); _lifetime.Dispose(); _stopTimer.Dispose();
    }

    static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
    }
    public override void Flush() { if (_disposed) throw new ObjectDisposedException(nameof(KingbaseCopyStream)); }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
#if !NETSTANDARD2_0
    public override int Read(Span<byte> buffer)
    {
        var temporary = new byte[Math.Min(buffer.Length, 65536)];
        try { var count = Read(temporary, 0, temporary.Length); temporary.AsSpan(0, count).CopyTo(buffer); return count; }
        finally { Array.Clear(temporary, 0, temporary.Length); }
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Enter(input: true); var temporary = new byte[Math.Min(buffer.Length, 65536)];
        try
        {
            using var abort = _lifetimeToken.Register(Abort);
            while (!buffer.IsEmpty)
            {
                var size = Math.Min(buffer.Length, temporary.Length); buffer.Slice(0, size).CopyTo(temporary);
                _exchange!.WriteAsync(temporary, 0, size, _lifetimeToken).GetAwaiter().GetResult(); buffer = buffer.Slice(size);
            }
            _lifetimeToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) { throw Failure(error, CancellationToken.None); }
        finally { Array.Clear(temporary, 0, temporary.Length); Volatile.Write(ref _busy, 0); }
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var array) ? new(ReadAsync(array.Array!, array.Offset, array.Count, cancellationToken)) : new(ReadMemoryAsync(buffer, cancellationToken));
    async Task<int> ReadMemoryAsync(Memory<byte> buffer, CancellationToken token)
    {
        var temporary = new byte[Math.Min(buffer.Length, 65536)];
        try { var count = await ReadAsync(temporary, 0, temporary.Length, token).ConfigureAwait(false); temporary.AsMemory(0, count).CopyTo(buffer); return count; }
        finally { Array.Clear(temporary, 0, temporary.Length); }
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        MemoryMarshal.TryGetArray(buffer, out var array) ? new(WriteAsync(array.Array!, array.Offset, array.Count, cancellationToken)) : new(WriteMemoryAsync(buffer, cancellationToken));
    async Task WriteMemoryAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Enter(input: true); var temporary = new byte[Math.Min(buffer.Length, 65536)];
        try
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeToken);
            using var abort = call.Token.Register(Abort);
            while (!buffer.IsEmpty)
            {
                var size = Math.Min(buffer.Length, temporary.Length); buffer.Slice(0, size).CopyTo(temporary);
                await _exchange!.WriteAsync(temporary, 0, size, call.Token).ConfigureAwait(false); buffer = buffer.Slice(size);
            }
            call.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { throw Failure(error, token); }
        finally { Array.Clear(temporary, 0, temporary.Length); Volatile.Write(ref _busy, 0); }
    }
#endif
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) RejectCallbackReentry();
        if (disposing && !_disposed) { try { Cancel(); } finally { if (HasFinished) _disposed = true; } }
        base.Dispose(disposing);
    }
#if !NETSTANDARD2_0
    public override async ValueTask DisposeAsync() { RejectCallbackReentry(); try { await CancelAsync().ConfigureAwait(false); } finally { if (HasFinished) _disposed = true; GC.SuppressFinalize(this); } }
#else
    public async Task DisposeAsync() { RejectCallbackReentry(); try { await CancelAsync().ConfigureAwait(false); } finally { if (HasFinished) _disposed = true; } }
#endif
}
