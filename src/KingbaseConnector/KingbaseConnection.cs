using System.Data;
using System.Data.Common;
using KingbaseConnector.Protocol;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif
namespace KingbaseConnector;
/// <summary>Owns one PG physical lease and its active command or buffered Reader.</summary>
public sealed class KingbaseConnection : DbConnection
{
    readonly object _sync = new();
    readonly KingbaseDataSource? _owner;
    KingbaseConnectionStringBuilder _settings = new();
    PgPhysicalSession? _session;
    PgSessionPool.Lease? _lease;
    TaskCompletionSource<object?>? _closing;
    CancellationTokenSource? _opening;
    KingbaseCommand? _command;
    CancellationTokenSource? _operationCancel;
    KingbaseDataReader? _reader;
    KingbaseTransaction? _transaction;
    ConnectionState _state;
    long _generation;
    bool _disposed;
    bool _opened;
    public KingbaseConnection() { }
    public KingbaseConnection(string connectionString) => ConnectionString = connectionString;
    public static void ClearPool(KingbaseConnection connection)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        PgSessionPool? pool;
        KingbaseConnectionStringBuilder settings;
        lock (connection._sync)
        {
            if (!connection._settings.Pooling) return;
            pool = connection._lease?.Pool ?? connection._owner?.Pool;
            settings = new KingbaseConnectionStringBuilder(connection._settings.ConnectionString);
        }
        if (pool is not null) pool.Clear(); else PgPoolRegistry.Clear(settings);
    }
    public static void ClearAllPools() => PgPoolRegistry.ClearAll();
    internal KingbaseConnection(string connectionString, KingbaseDataSource owner)
    {
        _settings = new KingbaseConnectionStringBuilder(connectionString);
        _owner = owner;
        _opened = true;
    }
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
                if (_owner is not null) throw new InvalidOperationException("A DataSource connection has immutable configuration; create another source.");
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
        using var opening = _owner?.CreateOpening(cancellationToken) ?? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string connectionString;
        KingbaseConnectionStringBuilder snapshot;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_closing is not null) throw new InvalidOperationException("Kingbase connection cleanup is still active.");
            if (_state != ConnectionState.Closed) throw new InvalidOperationException("The Kingbase connection is not closed.");
            KingbaseDataSourceBuilder.ValidateExecutionSettings(_settings);
            if (System.Transactions.Transaction.Current is not null) throw new NotSupportedException("Automatic system transaction enlistment is not yet implemented.");
            connectionString = _settings.ConnectionString;
            snapshot = new KingbaseConnectionStringBuilder(connectionString);
            _opening = opening;
            _generation++;
            _state = ConnectionState.Connecting;
        }
        PgPhysicalSession? physical = null;
        PgSessionPool.Lease? lease = null;
        try
        {
            Notify(ConnectionState.Closed, ConnectionState.Connecting);
            if (snapshot.Pooling)
            {
                lease = await (_owner?.Pool is { } pool ? pool.RentAsync(opening.Token) : PgPoolRegistry.RentAsync(snapshot, opening.Token)).ConfigureAwait(false);
                physical = lease.Session;
            }
            else physical = await PgPhysicalSession.OpenAsync(connectionString, opening.Token, passwordProvider: _owner?.PasswordProvider).ConfigureAwait(false);
            if (physical.Identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
                throw new NotSupportedException("The first public execution slice requires PostgreSql compatibility mode.");
            if (!physical.Queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8")
                throw new NotSupportedException("The Kingbase session no longer reports UTF8 encoding.");
            if (_owner is null) Publish(); else _owner.PublishOpen(Publish);
            Notify(ConnectionState.Connecting, ConnectionState.Open);

            void Publish()
            {
                lock (_sync)
                {
                    opening.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_opening, opening) || _state != ConnectionState.Connecting)
                        throw new OperationCanceledException("Kingbase opening was closed.", opening.Token);
                    _session = physical;
                    _lease = lease; lease = null;
                    physical = null;
                    _state = ConnectionState.Open;
                    _opened = true;
                }
            }
        }
        catch (Exception error)
        {
            try { await CloseCoreAsync(disposing: false, failedOpening: opening).ConfigureAwait(false); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            _owner?.EnsureAvailable();
            if (error is PgStartupException startup) throw new KingbaseException(startup.ServerError);
            if (error is PgQueryException query) throw new KingbaseException(query.ServerError);
            throw;
        }
        finally
        {
            if (lease is not null) await lease.ReturnAsync(canReset: false).ConfigureAwait(false);
            else physical?.Dispose();
            lock (_sync) { if (ReferenceEquals(_opening, opening)) _opening = null; }
        }
    }
    public override void Close() => CloseCore(disposing: false);
    void CloseCore(bool disposing, CancellationTokenSource? failedOpening = null,
        KingbaseDataReader? expectedReader = null, PgPhysicalSession? expectedSession = null, long expectedGeneration = 0,
        KingbaseTransaction? expectedTransaction = null) =>
        CloseCoreAsync(disposing, failedOpening, expectedReader, expectedSession, expectedGeneration, expectedTransaction).GetAwaiter().GetResult();
    Task CloseCoreAsync(bool disposing, CancellationTokenSource? failedOpening = null,
        KingbaseDataReader? expectedReader = null, PgPhysicalSession? expectedSession = null, long expectedGeneration = 0,
        KingbaseTransaction? expectedTransaction = null)
    {
        PgPhysicalSession? physical;
        PgSessionPool.Lease? lease;
        CancellationTokenSource? opening;
        CancellationTokenSource? operationCancel;
        KingbaseDataReader? reader;
        ConnectionState previous;
        bool hadCommand;
        TaskCompletionSource<object?> completion;
        lock (_sync)
        {
            if (failedOpening is not null && !ReferenceEquals(_opening, failedOpening)) return Task.CompletedTask;
            if (expectedTransaction is not null && !ReferenceEquals(_transaction, expectedTransaction)) return Task.CompletedTask;
            if (expectedReader is not null && (!ReferenceEquals(_reader, expectedReader) ||
                !ReferenceEquals(_session, expectedSession) || _generation != expectedGeneration)) return Task.CompletedTask;
            if (disposing) _disposed = true;
            if (_closing is not null) return _closing.Task;
            if (_state == ConnectionState.Closed && _opening is null) return Task.CompletedTask;
            completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _closing = completion;
            previous = _state;
            physical = _session; _session = null;
            lease = _lease; _lease = null;
            opening = _opening; _opening = null;
            hadCommand = _command is not null;
            _command = null;
            _transaction = null;
            operationCancel = _operationCancel; _operationCancel = null;
            reader = _reader; _reader = null;
            _generation++;
            _state = ConnectionState.Closed;
        }
        _ = CompleteClose();
        return completion.Task;

        async Task CompleteClose()
        {
            Exception? failure = null;
            var reusable = !hadCommand;
            try
            {
                try { opening?.Cancel(); } catch (ObjectDisposedException) { }
                try { operationCancel?.Cancel(); } catch (ObjectDisposedException) { }
                reader?.Close();
            }
            catch (Exception error) { reusable = false; failure = error; }
            try
            {
                if (lease is not null) await lease.ReturnAsync(reusable).ConfigureAwait(false);
                else physical?.Dispose();
            }
            catch (Exception error) { failure ??= error; }
            lock (_sync) { if (ReferenceEquals(_closing, completion)) _closing = null; }
            try { Notify(previous, ConnectionState.Closed); } catch (Exception error) { failure ??= error; }
            if (failure is null) completion.TrySetResult(null); else completion.TrySetException(failure);
        }
    }
    public override void ChangeDatabase(string databaseName)
    {
        lock (_sync)
        {
            OpenSession();
            if (_owner is not null) throw new InvalidOperationException("A DataSource connection has immutable configuration; create another source.");
            if (_command is not null || _reader is not null) throw ActiveOperation();
            if (_transaction is not null || _session!.Queries.TransactionStatus != PgTransactionStatus.Idle)
                throw new InvalidOperationException("Cannot change databases while a transaction is active.");
            var settings = new KingbaseConnectionStringBuilder(_settings.ConnectionString) { Database = databaseName };
            settings.Validate();
            _settings = settings;
        }
        Close();
        Open();
    }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => BeginLocalTransactionAsync(isolationLevel, CancellationToken.None).GetAwaiter().GetResult();
    public new KingbaseTransaction BeginTransaction() => (KingbaseTransaction)BeginDbTransaction(IsolationLevel.Unspecified);
    public new KingbaseTransaction BeginTransaction(IsolationLevel isolationLevel) => (KingbaseTransaction)BeginDbTransaction(isolationLevel);
    /// <summary>Begins a local transaction with an explicit transaction-scoped access mode.</summary>
    public KingbaseTransaction BeginTransaction(IsolationLevel isolationLevel, bool readOnly) =>
        BeginLocalTransactionAsync(isolationLevel, CancellationToken.None, readOnly).GetAwaiter().GetResult();
#if !NETSTANDARD2_0
    public ValueTask<KingbaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, bool readOnly, CancellationToken cancellationToken = default) =>
        new(BeginLocalTransactionAsync(isolationLevel, cancellationToken, readOnly));
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        await BeginLocalTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
    public new ValueTask<KingbaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        new(BeginLocalTransactionAsync(IsolationLevel.Unspecified, cancellationToken));
    public new ValueTask<KingbaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default) =>
        new(BeginLocalTransactionAsync(isolationLevel, cancellationToken));
#else
    public Task<KingbaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, bool readOnly, CancellationToken cancellationToken = default) =>
        BeginLocalTransactionAsync(isolationLevel, cancellationToken, readOnly);
    public Task<KingbaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        BeginLocalTransactionAsync(IsolationLevel.Unspecified, cancellationToken);
    public Task<KingbaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default) =>
        BeginLocalTransactionAsync(isolationLevel, cancellationToken);
#endif
    async Task<KingbaseTransaction> BeginLocalTransactionAsync(IsolationLevel isolationLevel, CancellationToken token, bool? readOnly = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(typeof(IsolationLevel), isolationLevel)) throw new ArgumentOutOfRangeException(nameof(isolationLevel));
        if (isolationLevel == IsolationLevel.Unspecified) isolationLevel = IsolationLevel.ReadCommitted;
        var isolation = isolationLevel switch
        {
            IsolationLevel.ReadCommitted => "READ COMMITTED",
            IsolationLevel.RepeatableRead => "REPEATABLE READ",
            IsolationLevel.Serializable => "SERIALIZABLE",
            _ => throw new NotSupportedException("The requested isolation level has no exact supported Kingbase PG mapping.")
        };
        var transaction = new KingbaseTransaction(this, isolationLevel);
        var sql = "BEGIN ISOLATION LEVEL " + isolation + (readOnly switch { true => ", READ ONLY", false => ", READ WRITE", _ => "" });
        using var command = new KingbaseCommand(sql, this, transaction, KingbaseTransactionOperation.Begin);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return transaction;
    }
    internal bool OwnsTransaction(KingbaseTransaction transaction) { lock (_sync) return ReferenceEquals(_transaction, transaction); }
    internal void CloseTransaction(KingbaseTransaction transaction) => CloseCore(disposing: false, expectedTransaction: transaction);
    public override void EnlistTransaction(System.Transactions.Transaction? transaction) =>
        throw new NotSupportedException("System transaction enlistment is not yet implemented.");
    protected override DbCommand CreateDbCommand() { lock (_sync) { ThrowIfDisposed(); return new KingbaseCommand("", this); } }
    public new KingbaseCommand CreateCommand() => (KingbaseCommand)CreateDbCommand();

    internal (PgPhysicalSession Session, long Generation) BeginExecution(KingbaseCommand command, CancellationTokenSource operationCancel,
        KingbaseTransaction? transaction = null, KingbaseTransactionOperation operation = KingbaseTransactionOperation.None)
    {
        lock (_sync)
        {
            var physical = OpenSession();
            if (_command is not null || _reader is not null) throw ActiveOperation();
            if (operation == KingbaseTransactionOperation.Begin)
            {
                if (_transaction is not null || physical.Queries.TransactionStatus != PgTransactionStatus.Idle)
                    throw new InvalidOperationException("A transaction is already active on this Kingbase connection.");
                _transaction = transaction;
            }
            else if ((transaction is not null && !ReferenceEquals(transaction, _transaction)) ||
                (operation != KingbaseTransactionOperation.None && transaction is null))
                throw new InvalidOperationException("The command transaction is not the active transaction on this connection.");
            _command = command;
            _operationCancel = operationCancel;
            return (physical, _generation);
        }
    }
    internal void ObserveTransaction(KingbaseCommand command, PgPhysicalSession session, long generation,
        KingbaseTransactionOperation operation, bool completed, PgTransactionStatus status, bool endsTransaction)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || _generation != generation || !ReferenceEquals(_command, command))
            {
                if (completed && operation is KingbaseTransactionOperation.Commit or KingbaseTransactionOperation.Rollback) return;
                throw new InvalidOperationException("The Kingbase command no longer owns its connection.");
            }
            if (status == PgTransactionStatus.Idle || (completed && endsTransaction)) _transaction = null;
            if (operation == KingbaseTransactionOperation.Begin && !completed)
            {
                _transaction = null;
                if (status != PgTransactionStatus.Idle) session.Dispose();
            }
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
        PgSessionPool.Lease? lease;
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session) || session.IsUsable) return;
            previous = _state;
            _state = ConnectionState.Broken;
            _session = null;
            lease = _lease; _lease = null;
            _transaction = null;
            _command = null;
            _operationCancel = null;
        }
        try { if (lease is not null) lease.ReturnAsync(canReset: false).GetAwaiter().GetResult(); else session.Dispose(); }
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
    public override Task CloseAsync() => CloseCoreAsync(disposing: false);
    public override ValueTask DisposeAsync() => new(CloseCoreAsync(disposing: true));
#else
    public Task CloseAsync() => CloseCoreAsync(disposing: false);
    public Task DisposeAsync() => CloseCoreAsync(disposing: true);
#endif
}
