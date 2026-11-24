using System.Diagnostics;
using System.Globalization;

namespace KingbaseConnector.Protocol;

internal sealed class PgPhysicalSession : IDisposable
{
    readonly PgTransport _transport;
    readonly PgQuerySession _queries;
    int _disposed;
    int _maxIdentifierLength;

    PgPhysicalSession(PgTransport transport, PgQuerySession queries, PgStartupResult startup,
        PgIdentityResult identity, PgTypeCatalog catalog)
    {
        _transport = transport;
        _queries = queries;
        Startup = startup;
        Identity = identity;
        Catalog = catalog;
    }

    internal PgStartupResult Startup { get; }
    internal PgIdentityResult Identity { get; }
    internal PgTypeCatalog Catalog { get; }
    internal bool IsUsable => Volatile.Read(ref _disposed) == 0 && !_queries.IsDisposed;
    internal PgQuerySession Queries => Volatile.Read(ref _disposed) == 0 ? _queries
        : throw new ObjectDisposedException(nameof(PgPhysicalSession));
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
    internal static PgPhysicalSession Open(string connectionString) => OpenAsync(connectionString).GetAwaiter().GetResult();

    internal static async Task<PgPhysicalSession> OpenAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new KingbaseConnectionStringBuilder(connectionString);
        settings.Validate();
        if (settings.WireProtocol != KingbaseWireProtocol.PostgreSql)
            throw new NotSupportedException("The PG physical session requires PostgreSql wire protocol.");
        if (settings.RootCertificate.Length != 0)
            throw new NotSupportedException("Custom root certificates are not yet supported by the PG transport.");
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
        try
        {
            CheckDeadline();
            transport = await PgTransport.ConnectAsync(settings.Host, settings.Port, settings.SslMode, linked.Token).ConfigureAwait(false);
            using var abort = linked.Token.Register(() => { try { transport.Dispose(); } catch { } });
            CheckDeadline();
            wire = new PgWire(transport.Stream);
            var startup = await PgStartup.RunAsync(wire, startupMessage, settings.Username, settings.Password, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            queries = new PgQuerySession(wire, startup);
            var identity = await PgIdentityProbe.RunAsync(queries, settings.CompatibilityMode, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            var catalog = await PgTypeCatalog.LoadAsync(queries, identity, linked.Token).ConfigureAwait(false);
            // Detach the creation token before the final success boundary.
            abort.Dispose();
            CheckDeadline();
            return new PgPhysicalSession(transport, queries, startup, identity, catalog);
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
