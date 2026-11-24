using System.Data;
using System.Data.Common;
using KingbaseConnector.Protocol;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif
namespace KingbaseConnector;
/// <summary>Owns one unpooled PG session and its active command or buffered Reader.</summary>
public sealed class KingbaseConnection : DbConnection
{
    readonly object _sync = new();
    KingbaseConnectionStringBuilder _settings = new();
    PgPhysicalSession? _session;
    CancellationTokenSource? _opening;
    KingbaseCommand? _command;
    CancellationTokenSource? _operationCancel;
    KingbaseDataReader? _reader;
    ConnectionState _state;
    long _generation;
    bool _disposed;
    bool _opened;
    public KingbaseConnection() { }
    public KingbaseConnection(string connectionString) => ConnectionString = connectionString;
#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public override string ConnectionString
    {
        get { lock (_sync) return _opened ? _settings.GetRedactedConnectionString() : _settings.ConnectionString; }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_state != ConnectionState.Closed) throw new InvalidOperationException("Close the Kingbase connection before changing its configuration.");
                _settings = new KingbaseConnectionStringBuilder(value ?? "");
                _opened = false;
            }
        }
    }
    public override string Database { get { lock (_sync) return _settings.Database; } }
    public override string DataSource { get { lock (_sync) return _settings.Host; } }
    public override int ConnectionTimeout { get { lock (_sync) return _settings.Timeout; } }
    internal int DefaultCommandTimeout { get { lock (_sync) return _settings.CommandTimeout; } }
    public override string ServerVersion { get { lock (_sync) return OpenSession().Identity.Version.RawText; } }
    public override ConnectionState State { get { lock (_sync) return _state; } }
    protected override DbProviderFactory DbProviderFactory => KingbaseFactory.Instance;
    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource opening;
        string connectionString;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_state != ConnectionState.Closed) throw new InvalidOperationException("The Kingbase connection is not closed.");
            _settings.Validate();
            if (_settings.Pooling) throw new NotSupportedException("This execution slice requires Pooling=false; built-in pooling is not yet implemented.");
            if (System.Transactions.Transaction.Current is not null) throw new NotSupportedException("Automatic system transaction enlistment is not yet implemented.");
            connectionString = _settings.ConnectionString;
            opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _opening = opening;
            _generation++;
            _state = ConnectionState.Connecting;
        }
        PgPhysicalSession? physical = null;
        try
        {
            Notify(ConnectionState.Closed, ConnectionState.Connecting);
            physical = await PgPhysicalSession.OpenAsync(connectionString, opening.Token).ConfigureAwait(false);
            if (physical.Identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
                throw new NotSupportedException("The first public execution slice requires PostgreSql compatibility mode.");
            if (!physical.Queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8")
                throw new NotSupportedException("The Kingbase session no longer reports UTF8 encoding.");
            lock (_sync)
            {
                opening.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_opening, opening) || _state != ConnectionState.Connecting)
                    throw new OperationCanceledException("Kingbase opening was closed.", opening.Token);
                _session = physical;
                physical = null;
                _state = ConnectionState.Open;
                _opened = true;
            }
            Notify(ConnectionState.Connecting, ConnectionState.Open);
        }
        catch (Exception error)
        {
            try { CloseCore(disposing: false, failedOpening: opening); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            if (error is PgStartupException startup) throw new KingbaseException(startup.ServerError);
            if (error is PgQueryException query) throw new KingbaseException(query.ServerError);
            throw;
        }
        finally
        {
            physical?.Dispose();
            lock (_sync) { if (ReferenceEquals(_opening, opening)) _opening = null; }
            opening.Dispose();
        }
    }
    public override void Close() => CloseCore(disposing: false);
    void CloseCore(bool disposing, CancellationTokenSource? failedOpening = null,
        KingbaseDataReader? expectedReader = null, PgPhysicalSession? expectedSession = null, long expectedGeneration = 0)
    {
        PgPhysicalSession? physical;
        CancellationTokenSource? opening;
        CancellationTokenSource? operationCancel;
        KingbaseDataReader? reader;
        ConnectionState previous;
        lock (_sync)
        {
            if (failedOpening is not null && !ReferenceEquals(_opening, failedOpening)) return;
            if (expectedReader is not null && (!ReferenceEquals(_reader, expectedReader) ||
                !ReferenceEquals(_session, expectedSession) || _generation != expectedGeneration)) return;
            if (disposing) _disposed = true;
            previous = _state;
            physical = _session; _session = null;
            opening = _opening; _opening = null;
            _command = null;
            operationCancel = _operationCancel; _operationCancel = null;
            reader = _reader; _reader = null;
            _generation++;
            _state = ConnectionState.Closed;
        }
        try
        {
            try { opening?.Cancel(); } catch (ObjectDisposedException) { }
            try { operationCancel?.Cancel(); } catch (ObjectDisposedException) { }
            reader?.Close();
        }
        finally { physical?.Dispose(); Notify(previous, ConnectionState.Closed); }
    }
    public override void ChangeDatabase(string databaseName)
    {
        lock (_sync)
        {
            OpenSession();
            if (_command is not null || _reader is not null) throw ActiveOperation();
            var settings = new KingbaseConnectionStringBuilder(_settings.ConnectionString) { Database = databaseName };
            settings.Validate();
            _settings = settings;
        }
        Close();
        Open();
    }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Local transactions are not yet implemented.");
    public override void EnlistTransaction(System.Transactions.Transaction? transaction) =>
        throw new NotSupportedException("System transaction enlistment is not yet implemented.");
    protected override DbCommand CreateDbCommand() { lock (_sync) { ThrowIfDisposed(); return new KingbaseCommand("", this); } }
    public new KingbaseCommand CreateCommand() => (KingbaseCommand)CreateDbCommand();

    internal (PgPhysicalSession Session, long Generation) BeginExecution(KingbaseCommand command, CancellationTokenSource operationCancel)
    {
        lock (_sync)
        {
            var physical = OpenSession();
            if (_command is not null || _reader is not null) throw ActiveOperation();
            _command = command;
            _operationCancel = operationCancel;
            return (physical, _generation);
        }
    }
    internal void PublishReader(KingbaseCommand command, PgPhysicalSession session, long generation, KingbaseDataReader reader)
    {
        lock (_sync)
        {
            if (_state != ConnectionState.Open || !ReferenceEquals(_session, session) || _generation != generation || !ReferenceEquals(_command, command))
                throw new InvalidOperationException("The Kingbase command no longer owns its connection.");
            _command = null;
            _operationCancel = null;
            _reader = reader;
        }
    }
    internal void EndExecution(KingbaseCommand command, PgPhysicalSession session, long generation)
    {
        lock (_sync)
            if (ReferenceEquals(_session, session) && _generation == generation && ReferenceEquals(_command, command))
            {
                _command = null;
                _operationCancel = null;
            }
    }
    internal void ReleaseReader(KingbaseDataReader reader, PgPhysicalSession session, long generation, bool closeConnection)
    {
        if (closeConnection)
        {
            CloseCore(disposing: false, expectedReader: reader, expectedSession: session, expectedGeneration: generation);
            return;
        }
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || _generation != generation || !ReferenceEquals(_reader, reader)) return;
            _reader = null;
        }
    }
    internal void RetireIfBroken(PgPhysicalSession session)
    {
        ConnectionState previous;
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || session.IsUsable) return;
            previous = _state;
            _state = ConnectionState.Broken;
            _session = null;
            _command = null;
            _operationCancel = null;
        }
        try { session.Dispose(); }
        finally { Notify(previous, ConnectionState.Broken); }
    }
    PgPhysicalSession OpenSession()
    {
        ThrowIfDisposed();
        if (_state != ConnectionState.Open || _session is null) throw new InvalidOperationException("The Kingbase connection is not open.");
        return _session;
    }
    void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(KingbaseConnection)); }
    static InvalidOperationException ActiveOperation() => new("A Kingbase command or Reader already owns this connection.");
    void Notify(ConnectionState previous, ConnectionState current) { if (previous != current) OnStateChange(new StateChangeEventArgs(previous, current)); }
    protected override void Dispose(bool disposing) { try { if (disposing) CloseCore(disposing: true); } finally { base.Dispose(disposing); } }
#if !NETSTANDARD2_0
    public override Task CloseAsync() { Close(); return Task.CompletedTask; }
    public override ValueTask DisposeAsync() { Dispose(); return default; }
#endif
}
