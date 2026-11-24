using System.Data;
using System.Data.Common;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal enum KingbaseTransactionOperation { None, Begin, Commit, Rollback }

/// <summary>A local transaction owned by one open physical connection.</summary>
public sealed class KingbaseTransaction : DbTransaction
{
    readonly KingbaseConnection _owner;
    int _disposed;
    int _outcomeUnknown;
    internal KingbaseTransaction(KingbaseConnection owner) => _owner = owner;
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
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
        var expected = operation switch { KingbaseTransactionOperation.Begin => "BEGIN", KingbaseTransactionOperation.Commit => "COMMIT", _ => "ROLLBACK" };
        if (tag != expected || status != (operation == KingbaseTransactionOperation.Begin ? PgTransactionStatus.InTransaction : PgTransactionStatus.Idle))
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
