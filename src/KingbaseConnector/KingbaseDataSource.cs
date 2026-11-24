using System.Data.Common;

namespace KingbaseConnector;

/// <summary>Owns immutable connection configuration and the lifetime of future unpooled opens.</summary>
#if NET8_0_OR_GREATER
public sealed class KingbaseDataSource : DbDataSource
#elif NETSTANDARD2_0
public sealed class KingbaseDataSource : IDisposable
#else
public sealed class KingbaseDataSource : IDisposable, IAsyncDisposable
#endif
{
    readonly object _sync = new();
    readonly string _configuration;
    readonly string _redacted;
    readonly CancellationTokenSource _closing = new();
    bool _disposed;

    internal KingbaseDataSource(string configuration)
    {
        _configuration = configuration;
        _redacted = new KingbaseConnectionStringBuilder(configuration).GetRedactedConnectionString();
    }
#if NET8_0_OR_GREATER
    public override string ConnectionString => _redacted;
    protected override DbConnection CreateDbConnection() => CreateOwnedConnection();
    protected override DbCommand CreateDbCommand(string? commandText = null) =>
        throw new NotSupportedException("Direct DataSource commands are not yet implemented; create a connection first.");
    public new KingbaseConnection CreateConnection() => CreateOwnedConnection();
    public new KingbaseConnection OpenConnection()
#else
    public string ConnectionString => _redacted;
    public KingbaseConnection CreateConnection() => CreateOwnedConnection();
    public KingbaseConnection OpenConnection()
#endif
    {
        var connection = CreateOwnedConnection();
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

#if NET8_0_OR_GREATER
    public new async ValueTask<KingbaseConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
#else
    public async Task<KingbaseConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
#endif
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = CreateOwnedConnection();
        try { await connection.OpenAsync(cancellationToken).ConfigureAwait(false); return connection; }
        catch { connection.Dispose(); throw; }
    }

    KingbaseConnection CreateOwnedConnection()
    {
        lock (_sync)
        {
            EnsureAvailable();
            return new KingbaseConnection(_configuration, this);
        }
    }

    internal CancellationTokenSource CreateOpening(CancellationToken caller)
    {
        lock (_sync)
        {
            EnsureAvailable();
            return CancellationTokenSource.CreateLinkedTokenSource(caller, _closing.Token);
        }
    }

    internal void PublishOpen(Action publish)
    {
        lock (_sync) { EnsureAvailable(); publish(); }
    }

    internal void EnsureAvailable()
    {
        lock (_sync) if (_disposed) throw new ObjectDisposedException(nameof(KingbaseDataSource));
    }

    void DisposeCore()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Opening registrations may still unwind; keep their managed cancellation source alive.
        try { _closing.Cancel(); } catch (AggregateException) { }
    }

#if NET8_0_OR_GREATER
    protected override void Dispose(bool disposing) { if (disposing) DisposeCore(); base.Dispose(disposing); }
    protected override ValueTask DisposeAsyncCore() { DisposeCore(); return default; }
#else
    public void Dispose() => DisposeCore();
#if !NETSTANDARD2_0
    public ValueTask DisposeAsync() { DisposeCore(); return default; }
#endif
#endif
}
