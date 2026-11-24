using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.UnitTests;

public sealed class TransactionTests
{
    [Theory]
    [InlineData("commit and chain", true)]
    [InlineData("\vCOMMIT AND CHAIN", true)]
    [InlineData("ROLLBACK\vTO SAVEPOINT s", false)]
    [InlineData("-- x\n END WORK", true)]
    [InlineData("/* a /* b */ c */ ABORT", true)]
    [InlineData("ROLLBACK TRANSACTION /* x */ AND CHAIN", true)]
    [InlineData("rollback /* x */ to savepoint s", false)]
    [InlineData("rollback work to s", false)]
    [InlineData("rollback transaction to savepoint s", false)]
    [InlineData("SELECT 'COMMIT'", false)]
    [InlineData("SELECT $$ROLLBACK$$", false)]
    [InlineData("SELECT \"COMMIT\"", false)]
    [InlineData("SELECT 1; -- COMMIT", false)]
    public void TransactionBoundaryUsesTheValidatedLeadingWords(string sql, bool endsTransaction) =>
        Assert.Equal(endsTransaction, PgParameterPlan.Create(sql, [], KingbaseCompatibilityMode.PostgreSql, true).EndsTransaction);

    [Fact]
    public async Task TypedTransactionsRejectClosedDisposedAndPrecanceledConnectionsLocally()
    {
        using var connection = new KingbaseConnection();
        Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.BeginTransactionAsync());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection.BeginTransactionAsync(canceled.Token));
        connection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => connection.BeginTransaction(IsolationLevel.ReadCommitted));
    }
}
