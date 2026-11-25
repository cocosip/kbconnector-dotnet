using System.Data;
using System.Data.Common;
using System.Diagnostics;
using KingbaseConnector.Protocol;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif
namespace KingbaseConnector;
/// <summary>Executes bounded, catalog-bound single-statement PG text commands.</summary>
public sealed partial class KingbaseCommand : DbCommand
{
    readonly object _sync = new();
    readonly KingbaseParameterCollection _parameters = new();
    readonly KingbaseBatch? _batch;
    internal object SyncRoot => _sync;
    internal Action<IReadOnlyList<PgTextResult>, PgTypeCatalog>? ValidateResultSchema { get; set; }
    internal (Stopwatch Elapsed, long Budget)? SharedDeadline { get; set; }
    internal KingbaseCommand(KingbaseBatch batch) => _batch = batch;
    KingbaseConnection? _connection;
    KingbaseTransaction? _transaction;
    readonly KingbaseTransactionOperation _transactionOperation;
    string _text = "";
    int? _timeout;
    CommandType _type = CommandType.Text;
    UpdateRowSource _updatedRowSource = UpdateRowSource.Both;
    bool _designTimeVisible = true;
    bool _executing;
    bool _disposed;
    CancellationTokenSource? _cancel;
    PgPreparedStatement? _prepared;
    long _preparedGeneration;
    enum Operation { Execute, Prepare, Unprepare }
    public KingbaseCommand() { }
    public KingbaseCommand(string? text, KingbaseConnection? connection) { CommandText = text!; Connection = connection; }
    internal KingbaseCommand(string text, KingbaseConnection connection, KingbaseTransaction transaction, KingbaseTransactionOperation operation)
        : this(text, connection) { _transaction = transaction; _transactionOperation = operation; }
#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public override string CommandText { get { lock (_sync) return _text; } set { lock (_sync) { EnsureEditable(); if (_text != (value ?? "")) _prepared = null; _text = value ?? ""; } } }
    public override int CommandTimeout
    {
        get { lock (_sync) return _timeout ?? _connection?.DefaultCommandTimeout ?? 30; }
        set { lock (_sync) { EnsureEditable(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _timeout = value; } }
    }
    public override CommandType CommandType
    {
        get { lock (_sync) return _type; }
        set { lock (_sync) { EnsureEditable(); if (!Enum.IsDefined(typeof(CommandType), value)) throw new ArgumentOutOfRangeException(nameof(value)); if (_type != value) _prepared = null; _type = value; } }
    }
    public override UpdateRowSource UpdatedRowSource
    {
        get { lock (_sync) return _updatedRowSource; }
        set { lock (_sync) { EnsureEditable(); if (!Enum.IsDefined(typeof(UpdateRowSource), value)) throw new ArgumentOutOfRangeException(nameof(value)); _updatedRowSource = value; } }
    }
    public override bool DesignTimeVisible { get { lock (_sync) return _designTimeVisible; } set { lock (_sync) { EnsureEditable(); _designTimeVisible = value; } } }
    protected override DbConnection? DbConnection
    {
        get { lock (_sync) return _connection; }
        set
        {
            lock (_sync)
            {
                EnsureEditable();
                if (value is not null and not KingbaseConnection) throw new ArgumentException("A Kingbase command requires a Kingbase connection.");
                if (!ReferenceEquals(_connection, value))
                {
                    _prepared = null;
                    if (_batch is not null) foreach (var item in _batch.BatchCommands.Snapshot()) item.Prepared = null;
                }
                _connection = (KingbaseConnection?)value;
            }
        }
    }
    public new KingbaseConnection? Connection { get => (KingbaseConnection?)DbConnection; set => DbConnection = value; }
    protected override DbTransaction? DbTransaction
    {
        get { lock (_sync) return _transaction; }
        set { lock (_sync) { EnsureEditable(); if (value is not null and not KingbaseTransaction) throw new ArgumentException("A Kingbase command requires a Kingbase transaction."); _transaction = (KingbaseTransaction?)value; } }
    }
    public new KingbaseTransaction? Transaction { get => (KingbaseTransaction?)DbTransaction; set => DbTransaction = value; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    public new KingbaseParameterCollection Parameters => (KingbaseParameterCollection)DbParameterCollection;
    public override void Cancel() { lock (_sync) _cancel?.Cancel(); }
    public override void Prepare() => PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
#if !NETSTANDARD2_0
    public override Task PrepareAsync(CancellationToken cancellationToken = default) => OperationCoreAsync(Operation.Prepare, CommandBehavior.Default, cancellationToken);
#else
    public Task PrepareAsync(CancellationToken cancellationToken = default) => OperationCoreAsync(Operation.Prepare, CommandBehavior.Default, cancellationToken);
#endif
    /// <summary>Closes this command's attached shared physical statement, if any.</summary>
    public void Unprepare() => UnprepareAsync(CancellationToken.None).GetAwaiter().GetResult();
    /// <summary>Closes this command's attached shared physical statement, if any.</summary>
    public Task UnprepareAsync(CancellationToken cancellationToken = default) => OperationCoreAsync(Operation.Unprepare, CommandBehavior.Default, cancellationToken);
    public override int ExecuteNonQuery() { using var reader = ExecuteReader(); return reader.RecordsAffected; }
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        using var reader = await ExecuteCoreAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        return reader.RecordsAffected;
    }
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        using var reader = (await OperationCoreAsync(Operation.Execute, CommandBehavior.Default, cancellationToken, scalar: true).ConfigureAwait(false))!;
        return Scalar(reader);
    }
    static object? Scalar(KingbaseDataReader reader)
    {
        do { if (reader.FieldCount > 0) return reader.Read() ? reader.GetValue(0) : null; } while (reader.NextResult());
        return null;
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => ExecuteCoreAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        await ExecuteCoreAsync(behavior, cancellationToken).ConfigureAwait(false);
    protected override DbParameter CreateDbParameter() => new KingbaseParameter();
    public new KingbaseParameter CreateParameter() => new();
    public new KingbaseDataReader ExecuteReader() => (KingbaseDataReader)ExecuteDbDataReader(CommandBehavior.Default);
    public new KingbaseDataReader ExecuteReader(CommandBehavior behavior) => (KingbaseDataReader)ExecuteDbDataReader(behavior);
    public new Task<KingbaseDataReader> ExecuteReaderAsync() => ExecuteCoreAsync(CommandBehavior.Default, CancellationToken.None);
    public new Task<KingbaseDataReader> ExecuteReaderAsync(CancellationToken cancellationToken) => ExecuteCoreAsync(CommandBehavior.Default, cancellationToken);
    public new Task<KingbaseDataReader> ExecuteReaderAsync(CommandBehavior behavior) => ExecuteCoreAsync(behavior, CancellationToken.None);
    public new Task<KingbaseDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => ExecuteCoreAsync(behavior, cancellationToken);

    async Task<KingbaseDataReader> ExecuteCoreAsync(CommandBehavior behavior, CancellationToken caller) =>
        (await OperationCoreAsync(Operation.Execute, behavior, caller).ConfigureAwait(false))!;

    async Task<KingbaseDataReader?> OperationCoreAsync(Operation operation, CommandBehavior behavior, CancellationToken caller, bool scalar = false)
    {
        if (_batch is null) caller.ThrowIfCancellationRequested();
        KingbaseConnection connection;
        KingbaseTransaction? transaction;
        string text;
        int timeout;
        CancellationTokenSource cancel;
        PgPreparedStatement? prepared;
        long preparedGeneration;
        KingbaseBatchCommand[]? batchCommands;
        lock (_sync)
        {
            EnsureEditable();
            batchCommands = _batch?.BatchCommands.Snapshot();
            if (batchCommands is not null)
            {
                _batch!.FailedCommandIndex = null;
                if (operation == Operation.Execute)
                    foreach (var item in batchCommands) { item.ExecutionStatus = KingbaseBatchCommandStatus.NotExecuted; item.Affected = -1; }
            }
            caller.ThrowIfCancellationRequested();
            if (operation == Operation.Unprepare && (_prepared is null || _connection is null ||
                !_connection.OwnsPreparedStatement(_prepared.Owner, _preparedGeneration)))
            {
                _prepared = null;
                return null;
            }
            const CommandBehavior known = CommandBehavior.SingleResult | CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo |
                CommandBehavior.SingleRow | CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection;
            if ((behavior & ~known) != 0) throw new ArgumentOutOfRangeException(nameof(behavior));
            if ((behavior & CommandBehavior.SequentialAccess) != 0)
                throw new NotSupportedException("This CommandBehavior requires an unimplemented execution contract.");
            if (operation != Operation.Unprepare && _batch is null)
            {
                if (_type != CommandType.Text) throw new NotSupportedException("Only Text commands are implemented.");
                if (string.IsNullOrWhiteSpace(_text)) throw new InvalidOperationException("Kingbase CommandText is empty.");
            }
            connection = _connection ?? throw new InvalidOperationException("The Kingbase command has no connection.");
            transaction = _transaction;
            text = _text;
            timeout = _timeout ?? connection.DefaultCommandTimeout;
            prepared = _prepared;
            preparedGeneration = _preparedGeneration;
            if (batchCommands is not null)
            {
                if (batchCommands.Length == 0) throw new InvalidOperationException("The Kingbase batch has no commands.");
            }
            cancel = new CancellationTokenSource();
            _cancel = cancel;
            _executing = true;
        }
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, cancel.Token, deadline.Token);
        var timer = timeout == 0 ? Task.CompletedTask : WatchDeadline((long)timeout * 1000, elapsed, deadline, stop.Token);
        PgPhysicalSession? session = null;
        long generation = 0;
        KingbaseDataReader? reader = null;
        var acknowledged = false;
        PgQuerySession? queries = null;
        long writeAttempts = 0;
        try
        {
            (session, generation) = connection.BeginExecution(this, cancel, transaction, _transactionOperation);
            queries = session.Queries;
            writeAttempts = queries.WriteAttempts;
            CheckMode();
            if (batchCommands is not null)
            {
                reader = await ExecuteBatchOwnedAsync(batchCommands, operation, behavior, connection, session, generation,
                    transaction, scalar, linked.Token, CheckMode, CheckCancellation).ConfigureAwait(false);
                acknowledged = true;
                return reader;
            }
            if (operation == Operation.Unprepare)
            {
                lock (_sync) _prepared = null;
                CheckCancellation();
                if (prepared is not null && ReferenceEquals(prepared.Owner, queries) && preparedGeneration == generation)
                    await queries.UnprepareAsync(prepared, linked.Token).ConfigureAwait(false);
                CheckMode();
                connection.ObserveTransaction(this, session, generation, _transactionOperation, true, queries.TransactionStatus, false);
                acknowledged = true;
                CheckCancellation();
                return null;
            }
            if (_transactionOperation == KingbaseTransactionOperation.Begin)
                transaction!.SavepointNameByteLimit = await session.GetMaxIdentifierLengthAsync(linked.Token).ConfigureAwait(false);
            bool? conforming = queries.Parameters.TryGetValue("standard_conforming_strings", out var status)
                ? status switch { "on" => true, "off" => false, _ => null } : null;
            CheckMode();
            var bindings = PgParameterBindings.Create(text, _parameters, session.Catalog, session.Identity.EffectiveMode, conforming);
            CheckCancellation();
            if (operation == Operation.Prepare)
            {
                lock (_sync) _prepared = null;
                var statement = await queries.PrepareAsync(bindings.Sql, session.Catalog,
                    bindings.Parameters.Select(p => p.TypeOid).ToArray(), linked.Token, CheckMode).ConfigureAwait(false);
                CheckMode();
                connection.ObserveTransaction(this, session, generation, _transactionOperation, true, queries.TransactionStatus, false);
                acknowledged = true;
                CheckCancellation();
                lock (_sync) if (!_disposed) { _prepared = statement; _preparedGeneration = generation; }
                return null;
            }
            var usePrepared = prepared is not null && prepared.IsValid && ReferenceEquals(prepared.Owner, queries) &&
                preparedGeneration == generation && prepared.Sql == bindings.Sql && prepared.LexicalStatus == status &&
                prepared.TypeOids.SequenceEqual(bindings.Parameters.Select(p => p.TypeOid));
            var schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
            IReadOnlyList<PgTextResult>? metadataBefore = null;
            if ((behavior & CommandBehavior.KeyInfo) != 0)
            {
                var discovery = await queries.DescribeAsync(bindings.Sql, session.Catalog, bindings.Parameters.Select(p => p.TypeOid).ToArray(), linked.Token).ConfigureAwait(false);
                metadataBefore = await EnrichMetadataAsync(connection, session, generation, discovery, bindings.Sql, linked.Token, CheckMode).ConfigureAwait(false);
                CheckCancellation();
            }
            if (!schemaOnly && !usePrepared) lock (_sync) _prepared = null;
            IReadOnlyList<PgTextResult> results;
            if (schemaOnly)
                results = await queries.DescribeAsync(bindings.Sql, session.Catalog, bindings.Parameters.Select(p => p.TypeOid).ToArray(), linked.Token).ConfigureAwait(false);
            else if (usePrepared)
                results = await queries.ExecutePreparedAsync(prepared!, session.Catalog, bindings.Parameters, linked.Token).ConfigureAwait(false);
            else if (_transactionOperation == KingbaseTransactionOperation.None)
            {
                var automatic = await queries.ExecuteAutoPreparedAsync(bindings.Sql, session.Catalog, bindings.Parameters, linked.Token, CheckMode,
                    statement =>
                    {
                        lock (_sync) if (!_disposed) { _prepared = statement; _preparedGeneration = generation; }
                    }).ConfigureAwait(false);
                results = automatic.Results;
            }
            else results = await queries.ExecuteExtendedAsync(bindings.Sql, session.Catalog, bindings.Parameters, linked.Token).ConfigureAwait(false);
            var rolledBack = false;
            if (_transactionOperation != KingbaseTransactionOperation.None)
            {
                try { rolledBack = KingbaseTransaction.ValidateAcknowledgment(_transactionOperation, results, queries.TransactionStatus); }
                catch { session.Dispose(); throw; }
                acknowledged = true;
            }
            CheckMode();
            connection.ObserveTransaction(this, session, generation, _transactionOperation, true, queries.TransactionStatus, !schemaOnly && bindings.EndsTransaction);
            acknowledged = true;
            if (rolledBack) throw new KingbaseTransactionException(outcomeUnknown: false);
            if (_transactionOperation is not KingbaseTransactionOperation.None and not KingbaseTransactionOperation.Begin)
                return new KingbaseDataReader(results, session.Catalog);
            if (_transactionOperation == KingbaseTransactionOperation.None) CheckCancellation();
            ValidateResultSchema?.Invoke(results, session.Catalog);
            if ((behavior & CommandBehavior.KeyInfo) != 0)
            {
                results = await EnrichMetadataAsync(connection, session, generation, results, bindings.Sql, linked.Token, CheckMode).ConfigureAwait(false);
                PgColumnMetadata.VerifyStable(metadataBefore!, results);
                CheckCancellation();
            }
            var capturedSession = session;
            reader = new KingbaseDataReader(results, session.Catalog, behavior,
                r => connection.ReleaseReader(r, capturedSession, generation, (behavior & CommandBehavior.CloseConnection) != 0));
            connection.PublishReader(this, session, generation, reader);
            return reader;

            void CheckMode()
            {
                try
                {
                    if (!queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8" ||
                        (queries.Parameters.TryGetValue("database_mode", out var mode) && PgIdentityProbe.ParseMode(mode) != session.Identity.EffectiveMode))
                        throw new NotSupportedException("The Kingbase session identity or encoding changed.");
                }
                catch (NotSupportedException)
                {
                    session.Dispose();
                    throw;
                }
            }
        }
        catch (Exception error)
        {
            reader?.Close();
            if (session is not null && session.IsUsable && !acknowledged && error is not KingbaseTransactionException)
            {
                try { connection.ObserveTransaction(this, session, generation, _transactionOperation, false, queries!.TransactionStatus, false); }
                catch { session.Dispose(); }
            }
            if (session is not null) connection.RetireIfBroken(session);
            if (_transactionOperation == KingbaseTransactionOperation.Commit && !acknowledged && session is not null &&
                queries is not null && queries.WriteAttempts != writeAttempts)
            {
                if (queries.CommandRejection is { } rejection)
                {
                    caller.ThrowIfCancellationRequested();
                    throw new KingbaseException(rejection);
                }
                if (!session.IsUsable || queries.UncertainError is not null)
                {
                    transaction!.MarkOutcomeUnknown();
                    caller.ThrowIfCancellationRequested();
                    throw new KingbaseTransactionException(outcomeUnknown: true, error);
                }
            }
            if (_transactionOperation != KingbaseTransactionOperation.None && acknowledged) throw;
            CheckCancellation();
            if (error is PgQueryException query) throw new KingbaseException(query.ServerError,
                _batch?.FailedCommandIndex is { } index ? batchCommands![index] : null);
            throw;
        }
        finally
        {
            if (session is not null) connection.EndExecution(this, session, generation);
            stop.Cancel();
            await timer.ConfigureAwait(false);
            lock (_sync) { _cancel = null; _executing = false; }
            cancel.Dispose();
        }

        void CheckCancellation()
        {
            caller.ThrowIfCancellationRequested();
            if (SharedDeadline is { } shared && shared.Elapsed.ElapsedMilliseconds >= shared.Budget)
                throw new TimeoutException("The Kingbase shared command deadline elapsed; execution may have reached the server.");
            if (timeout != 0 && (deadline.IsCancellationRequested || elapsed.ElapsedMilliseconds >= (long)timeout * 1000))
                throw new TimeoutException("The Kingbase command timed out; execution may have reached the server.");
            cancel.Token.ThrowIfCancellationRequested();
        }
    }
    internal static async Task WatchDeadline(long budget, Stopwatch elapsed, CancellationTokenSource deadline, CancellationToken stop)
    {
        try
        {
            long remaining;
            while ((remaining = budget - elapsed.ElapsedMilliseconds) > 0)
                await Task.Delay((int)Math.Min(remaining, int.MaxValue), stop).ConfigureAwait(false);
            try { deadline.Cancel(); } catch { }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    internal void EnsureEditable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KingbaseCommand));
        if (_executing) throw new InvalidOperationException("The Kingbase command is executing.");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_sync) { _disposed = true; _prepared = null; _cancel?.Cancel(); }
        base.Dispose(disposing);
    }
}
