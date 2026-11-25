using System.Diagnostics;
using System.Globalization;

namespace KingbaseConnector.Protocol;

internal sealed class PgPhysicalSession : IDisposable
{
    readonly PgTransport _transport;
    readonly PgQuerySession _queries;
    readonly PgSessionResetState? _resetState;
    readonly int _cleanupTimeout;
    int _disposed;
    int _resetting;
    int _maxIdentifierLength;

    PgPhysicalSession(PgTransport transport, PgQuerySession queries, PgStartupResult startup,
        PgIdentityResult identity, PgTypeCatalog catalog, PgSessionResetState? resetState, int cleanupTimeout)
    {
        _transport = transport;
        _queries = queries;
        Startup = startup;
        Identity = identity;
        Catalog = catalog;
        _resetState = resetState;
        _cleanupTimeout = cleanupTimeout;
    }

    internal PgStartupResult Startup { get; }
    internal PgIdentityResult Identity { get; }
    internal PgTypeCatalog Catalog { get; private set; }
    internal bool IsUsable => Volatile.Read(ref _disposed) == 0 && !_queries.IsDisposed;
    internal PgQuerySession Queries => Volatile.Read(ref _disposed) == 0 ? _queries
        : throw new ObjectDisposedException(nameof(PgPhysicalSession));
    internal void ValidateReportedBaseline() => (_resetState ?? throw new InvalidOperationException("This physical session has no pool baseline.")).ValidateReported(Queries);
    // Called only while the public command owns this physical session's execution gate.
    internal async Task<int> GetMaxIdentifierLengthAsync(CancellationToken token)
    {
        if (_maxIdentifierLength != 0) return _maxIdentifierLength;
        var results = await Queries.ExecuteAsync("SHOW max_identifier_length", token).ConfigureAwait(false);
        var text = results.Count == 1 && results[0].Rows.Count == 1 && results[0].Rows[0].Count == 1 ? results[0].Rows[0][0] : null;
        if (results.Count != 1 || results[0].CommandTag != "SHOW" || results[0].Columns.Count != 1 ||
            text is null || text.Length == 0 || text.Length > 10 || text.Any(c => c is < '0' or > '9') ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0 ||
            Queries.TransactionStatus != PgTransactionStatus.Idle)
        {
            Dispose();
            throw new IOException("Invalid Kingbase identifier length metadata.");
        }
        _maxIdentifierLength = length;
        return length;
    }
    internal static PgPhysicalSession Open(string connectionString, bool prepareForPooling = false) => OpenAsync(connectionString, prepareForPooling: prepareForPooling).GetAwaiter().GetResult();

    internal void Reset() => ResetAsync().GetAwaiter().GetResult();
    internal async Task ResetAsync()
    {
        if (!IsUsable) throw new ObjectDisposedException(nameof(PgPhysicalSession));
        if (_resetState is null) throw new InvalidOperationException("This physical session was not initialized for pooling reset.");
        if (Interlocked.CompareExchange(ref _resetting, 1, 0) != 0) throw new InvalidOperationException("A session reset is already active.");
        var elapsed = Stopwatch.StartNew();
        var budget = (long)_cleanupTimeout * 1000;
        using var deadline = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var timer = WatchDeadlineAsync(budget, elapsed, deadline, stop.Token);
        try
        {
            using var abort = deadline.Token.Register(() => { try { Dispose(); } catch { } });
            var catalog = await _resetState.RestoreAsync(Queries, Identity, deadline.Token).ConfigureAwait(false);
            abort.Dispose();
            if (deadline.IsCancellationRequested || elapsed.ElapsedMilliseconds >= budget) throw new TimeoutException("PG session cleanup timed out.");
            if (!IsUsable) throw new ObjectDisposedException(nameof(PgPhysicalSession));
            Catalog = catalog;
        }
        catch
        {
            try { Dispose(); } catch { }
            if (deadline.IsCancellationRequested || elapsed.ElapsedMilliseconds >= budget) throw new TimeoutException("PG session cleanup timed out.");
            throw;
        }
        finally { stop.Cancel(); await timer.ConfigureAwait(false); Volatile.Write(ref _resetting, 0); }
    }

    internal static async Task<PgPhysicalSession> OpenAsync(string connectionString, CancellationToken cancellationToken = default, bool prepareForPooling = false,
        KingbasePasswordProvider? passwordProvider = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new KingbaseConnectionStringBuilder(connectionString);
        settings.Validate();
        PgPasswordProvider.Validate(settings, passwordProvider);
        if (prepareForPooling && !settings.Pooling) throw new ArgumentException("Pooling reset initialization requires Pooling=true.");
        if (settings.WireProtocol != KingbaseWireProtocol.PostgreSql)
            throw new NotSupportedException("The PG physical session requires PostgreSql wire protocol.");
        var startupMessage = CreateStartupMessage(settings);

        var elapsed = Stopwatch.StartNew();
        var budget = (long)settings.Timeout * 1000;
        using var deadline = new CancellationTokenSource();
        using var stopTimer = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var timer = budget == 0 ? Task.CompletedTask : WatchDeadlineAsync(budget, elapsed, deadline, stopTimer.Token);
        PgTransport? transport = null;
        PgWire? wire = null;
        PgQuerySession? queries = null;
        var password = settings.Password;
        try
        {
            CheckDeadline();
            if (passwordProvider is not null)
                password = await PgPasswordProvider.GetAsync(passwordProvider, settings, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            transport = await PgTransport.ConnectAsync(settings.Host, settings.Port, settings.SslMode, linked.Token, settings.RootCertificate).ConfigureAwait(false);
            using var abort = linked.Token.Register(() => { try { transport.Dispose(); } catch { } });
            CheckDeadline();
            wire = new PgWire(transport.Stream);
            var startup = await PgStartup.RunAsync(wire, startupMessage, settings.Username, password, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            queries = new PgQuerySession(wire, startup, settings.MaxAutoPrepare, settings.AutoPrepareMinUsages);
            var identity = await PgIdentityProbe.RunAsync(queries, settings.CompatibilityMode, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            var catalog = await PgTypeCatalog.LoadAsync(queries, identity, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            var resetState = prepareForPooling ? await PgSessionResetState.CaptureAsync(queries, identity, linked.Token).ConfigureAwait(false) : null;
            // Detach the creation token before the final success boundary.
            abort.Dispose();
            CheckDeadline();
            return new PgPhysicalSession(transport, queries, startup, identity, catalog, resetState, settings.CleanupTimeout);
        }
        catch
        {
            try { if (queries is not null) queries.Dispose(); else wire?.Dispose(); } catch { }
            try { transport?.Dispose(); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            if (budget != 0 && (deadline.IsCancellationRequested || elapsed.ElapsedMilliseconds >= budget))
                throw new TimeoutException("PG session initialization timed out.");
            throw;
        }
        finally
        {
            password = ""; // Returned strings cannot be erased; do not retain them in reusable session/configuration state.
            stopTimer.Cancel();
            await timer.ConfigureAwait(false);
        }

        void CheckDeadline()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget != 0 && (deadline.IsCancellationRequested || elapsed.ElapsedMilliseconds >= budget))
                throw new TimeoutException("PG session initialization timed out.");
        }
    }

    internal static byte[] CreateStartupMessage(KingbaseConnectionStringBuilder settings)
    {
        // Validate aggregate startup size and all credential text before creating a transport or source.
        PgFrontendMessages.PasswordTextLength(settings.Password);
        PgPasswordAuthentication.Md5InputLengths(settings.Username, settings.Password);
        return PgFrontendMessages.Startup(settings.Username, settings.Database,
            settings.ApplicationName, settings.SearchPath, settings.Timezone);
    }

    static async Task WatchDeadlineAsync(long budget, Stopwatch elapsed, CancellationTokenSource deadline, CancellationToken stop)
    {
        try
        {
            long remaining;
            while ((remaining = budget - elapsed.ElapsedMilliseconds) > 0)
                await Task.Delay((int)Math.Min(remaining, int.MaxValue), stop).ConfigureAwait(false);
            // Cancellation callback failure must not replace the initialization error.
            try { deadline.Cancel(); } catch { }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _queries.Dispose(); }
        finally { _transport.Dispose(); }
    }
}
