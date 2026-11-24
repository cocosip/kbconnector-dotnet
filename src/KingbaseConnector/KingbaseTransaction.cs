using System.Data;
using System.Data.Common;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal enum KingbaseTransactionOperation { None, Begin, Commit, Rollback, Save, RollbackTo, Release }

/// <summary>A local transaction owned by one open physical connection.</summary>
public sealed class KingbaseTransaction : DbTransaction
{
    readonly KingbaseConnection _owner;
    int _disposed;
    int _outcomeUnknown;
    internal int SavepointNameByteLimit { get; set; }
    internal KingbaseTransaction(KingbaseConnection owner, IsolationLevel isolationLevel) { _owner = owner; IsolationLevel = isolationLevel; }
    /// <summary>The isolation requested at Begin, with Unspecified normalized to ReadCommitted.</summary>
    public override IsolationLevel IsolationLevel { get; }
    protected override DbConnection? DbConnection => _owner.OwnsTransaction(this) ? _owner : null;
    public new KingbaseConnection? Connection => (KingbaseConnection?)DbConnection;
    /// <summary>True when COMMIT was interrupted without a confirmed outcome, including cancellation.</summary>
    public bool OutcomeUnknown => Volatile.Read(ref _outcomeUnknown) != 0;
    internal void MarkOutcomeUnknown() => Volatile.Write(ref _outcomeUnknown, 1);
    internal static bool ValidateAcknowledgment(KingbaseTransactionOperation operation,
        IReadOnlyList<PgTextResult> results, PgTransactionStatus status)
    {
        var tag = results.Count == 1 && results[0].Columns.Count == 0 && results[0].Rows.Count == 0 ? results[0].CommandTag : null;
        if (operation == KingbaseTransactionOperation.Commit && tag == "ROLLBACK" && status == PgTransactionStatus.Idle) return true;
        var expected = operation switch
        {
            KingbaseTransactionOperation.Begin => "BEGIN", KingbaseTransactionOperation.Commit => "COMMIT",
            KingbaseTransactionOperation.Save => "SAVEPOINT", KingbaseTransactionOperation.Release => "RELEASE", _ => "ROLLBACK"
        };
        var terminal = operation is KingbaseTransactionOperation.Commit or KingbaseTransactionOperation.Rollback;
        if (tag != expected || status != (terminal ? PgTransactionStatus.Idle : PgTransactionStatus.InTransaction))
            throw new IOException("Invalid Kingbase transaction acknowledgment.");
        return false;
    }
    public override void Commit() => CompleteAsync(KingbaseTransactionOperation.Commit, CancellationToken.None).GetAwaiter().GetResult();
    public override void Rollback() => CompleteAsync(KingbaseTransactionOperation.Rollback, CancellationToken.None).GetAwaiter().GetResult();
#if !NETSTANDARD2_0
    public override Task CommitAsync(CancellationToken cancellationToken = default) => CompleteAsync(KingbaseTransactionOperation.Commit, cancellationToken);
    public override Task RollbackAsync(CancellationToken cancellationToken = default) => CompleteAsync(KingbaseTransactionOperation.Rollback, cancellationToken);
#else
    public Task CommitAsync(CancellationToken cancellationToken = default) => CompleteAsync(KingbaseTransactionOperation.Commit, cancellationToken);
    public Task RollbackAsync(CancellationToken cancellationToken = default) => CompleteAsync(KingbaseTransactionOperation.Rollback, cancellationToken);
#endif
#if !NETSTANDARD2_0 && !NETSTANDARD2_1
    public override bool SupportsSavepoints => true;
    public override void Save(string savepointName) => SavepointAsync(KingbaseTransactionOperation.Save, savepointName, CancellationToken.None).GetAwaiter().GetResult();
    public override void Rollback(string savepointName) => SavepointAsync(KingbaseTransactionOperation.RollbackTo, savepointName, CancellationToken.None).GetAwaiter().GetResult();
    public override void Release(string savepointName) => SavepointAsync(KingbaseTransactionOperation.Release, savepointName, CancellationToken.None).GetAwaiter().GetResult();
    public override Task SaveAsync(string savepointName, CancellationToken cancellationToken = default) => SavepointAsync(KingbaseTransactionOperation.Save, savepointName, cancellationToken);
    public override Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default) => SavepointAsync(KingbaseTransactionOperation.RollbackTo, savepointName, cancellationToken);
    public override Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default) => SavepointAsync(KingbaseTransactionOperation.Release, savepointName, cancellationToken);
#else
    public bool SupportsSavepoints => true;
    public void Save(string savepointName) => SavepointAsync(KingbaseTransactionOperation.Save, savepointName, CancellationToken.None).GetAwaiter().GetResult();
    public void Rollback(string savepointName) => SavepointAsync(KingbaseTransactionOperation.RollbackTo, savepointName, CancellationToken.None).GetAwaiter().GetResult();
    public void Release(string savepointName) => SavepointAsync(KingbaseTransactionOperation.Release, savepointName, CancellationToken.None).GetAwaiter().GetResult();
    public Task SaveAsync(string savepointName, CancellationToken cancellationToken = default) => SavepointAsync(KingbaseTransactionOperation.Save, savepointName, cancellationToken);
    public Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default) => SavepointAsync(KingbaseTransactionOperation.RollbackTo, savepointName, cancellationToken);
    public Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default) => SavepointAsync(KingbaseTransactionOperation.Release, savepointName, cancellationToken);
#endif
    async Task SavepointAsync(KingbaseTransactionOperation operation, string savepointName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(KingbaseTransaction));
        if (!_owner.OwnsTransaction(this)) throw new InvalidOperationException("The Kingbase transaction is no longer active.");
        if (savepointName is null) throw new ArgumentNullException(nameof(savepointName));
        if (savepointName.Length == 0 || savepointName.Length > 1024 * 1024 ||
            PgFrontendMessages.CStringLength(savepointName) - 1 > SavepointNameByteLimit)
            throw new ArgumentException("The savepoint name exceeds the session identifier limit or is empty.", nameof(savepointName));
        var sql = operation switch
        {
            KingbaseTransactionOperation.Save => "SAVEPOINT", KingbaseTransactionOperation.Release => "RELEASE SAVEPOINT", _ => "ROLLBACK TO SAVEPOINT"
        };
        using var command = new KingbaseCommand(sql + " \"" + savepointName.Replace("\"", "\"\"") + "\"", _owner, this, operation);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    async Task CompleteAsync(KingbaseTransactionOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(KingbaseTransaction));
        using var command = new KingbaseCommand(operation == KingbaseTransactionOperation.Commit ? "COMMIT" : "ROLLBACK", _owner, this, operation);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_owner.OwnsTransaction(this)) return;
        try
        {
            // Cleanup remains bounded even when the application's Command Timeout is unlimited.
            using var command = new KingbaseCommand("ROLLBACK", _owner, this, KingbaseTransactionOperation.Rollback) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch { _owner.CloseTransaction(this); }
    }
    protected override void Dispose(bool disposing)
    {
        try { if (disposing) CleanupAsync().GetAwaiter().GetResult(); }
        finally { base.Dispose(disposing); }
    }
#if !NETSTANDARD2_0
    public override async ValueTask DisposeAsync() => await CleanupAsync().ConfigureAwait(false);
#endif
}

/// <summary>A COMMIT outcome failure. An unknown outcome must never trigger automatic replay.</summary>
public sealed class KingbaseTransactionException : DbException
{
    internal KingbaseTransactionException(bool outcomeUnknown, Exception? inner = null)
        : base(outcomeUnknown ? "The Kingbase COMMIT outcome is unknown; do not automatically replay the transaction."
            : "Kingbase rolled back the transaction instead of committing it.", inner) => OutcomeUnknown = outcomeUnknown;
    public bool OutcomeUnknown { get; }
}
