using System.Data;
using System.Data.Common;

namespace KingbaseConnector;

/// <summary>Executes statements sequentially under one timeout, without an implicit transaction or retry.</summary>
public sealed class KingbaseBatch
#if NET6_0_OR_GREATER
    : DbBatch
#else
    : IDisposable
#if !NETSTANDARD2_0
    , IAsyncDisposable
#endif
#endif
{
    readonly KingbaseCommand _runner;
    readonly KingbaseBatchCommandCollection _commands;
    public KingbaseBatch() : this(null) { }
    public KingbaseBatch(KingbaseConnection? connection, KingbaseTransaction? transaction = null)
    {
        _runner = new KingbaseCommand(this);
        _commands = new(this);
        _runner.Connection = connection;
        _runner.Transaction = transaction;
    }
    internal object SyncRoot => _runner.SyncRoot;
    internal void EnsureEditable() => _runner.EnsureEditable();
    internal void SetSharedDeadline(System.Diagnostics.Stopwatch elapsed, long budget) => _runner.SharedDeadline = (elapsed, budget);
    /// <summary>The zero-based command at which the last operation stopped, or null on success.</summary>
    public int? FailedCommandIndex { get; internal set; }
#if NET6_0_OR_GREATER
    protected override DbBatchCommandCollection DbBatchCommands => _commands;
    public new KingbaseBatchCommandCollection BatchCommands => _commands;
    protected override DbConnection? DbConnection { get => _runner.Connection; set { if (value is not null and not KingbaseConnection) throw new ArgumentException("A Kingbase batch requires a Kingbase connection."); _runner.Connection = (KingbaseConnection?)value; } }
    protected override DbTransaction? DbTransaction { get => _runner.Transaction; set { if (value is not null and not KingbaseTransaction) throw new ArgumentException("A Kingbase batch requires a Kingbase transaction."); _runner.Transaction = (KingbaseTransaction?)value; } }
    public new KingbaseConnection? Connection { get => _runner.Connection; set => _runner.Connection = value; }
    public new KingbaseTransaction? Transaction { get => _runner.Transaction; set => _runner.Transaction = value; }
    protected override DbBatchCommand CreateDbBatchCommand() => new KingbaseBatchCommand();
    public new KingbaseBatchCommand CreateBatchCommand() => new();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _runner.ExecuteReader(behavior);
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => await _runner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
#else
    public KingbaseBatchCommandCollection BatchCommands => _commands;
    public KingbaseConnection? Connection { get => _runner.Connection; set => _runner.Connection = value; }
    public KingbaseTransaction? Transaction { get => _runner.Transaction; set => _runner.Transaction = value; }
    public KingbaseBatchCommand CreateBatchCommand() => new();
#endif
    public
#if NET6_0_OR_GREATER
        override
#endif
        int Timeout { get => _runner.CommandTimeout; set => _runner.CommandTimeout = value; }
    public
#if NET6_0_OR_GREATER
        override
#endif
        void Cancel() => _runner.Cancel();
    public
#if NET6_0_OR_GREATER
        override
#endif
        void Prepare() => _runner.Prepare();
    public
#if NET6_0_OR_GREATER
        override
#endif
        Task PrepareAsync(CancellationToken cancellationToken = default) => _runner.PrepareAsync(cancellationToken);
    public
#if NET6_0_OR_GREATER
        override
#endif
        int ExecuteNonQuery() => _runner.ExecuteNonQuery();
    public
#if NET6_0_OR_GREATER
        override
#endif
        Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) => _runner.ExecuteNonQueryAsync(cancellationToken);
    public
#if NET6_0_OR_GREATER
        override
#endif
        object? ExecuteScalar() => _runner.ExecuteScalar();
    public
#if NET6_0_OR_GREATER
        override
#endif
        Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default) => _runner.ExecuteScalarAsync(cancellationToken);
    public
#if NET6_0_OR_GREATER
        new
#endif
        KingbaseDataReader ExecuteReader(CommandBehavior behavior = CommandBehavior.Default) => _runner.ExecuteReader(behavior);
    public
#if NET6_0_OR_GREATER
        new
#endif
        Task<KingbaseDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default) => _runner.ExecuteReaderAsync(cancellationToken);
    public
#if NET6_0_OR_GREATER
        new
#endif
        Task<KingbaseDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken = default) => _runner.ExecuteReaderAsync(behavior, cancellationToken);
    public
#if NET6_0_OR_GREATER
        override
#endif
        void Dispose()
    {
        lock (SyncRoot)
        {
            _runner.Dispose();
            foreach (var command in _commands.Snapshot()) command.Prepared = null;
        }
    }
#if NET6_0_OR_GREATER
    public override ValueTask DisposeAsync() { Dispose(); return default; }
#elif !NETSTANDARD2_0
    public ValueTask DisposeAsync() { Dispose(); return default; }
#else
    public Task DisposeAsync() { Dispose(); return Task.CompletedTask; }
#endif
}
