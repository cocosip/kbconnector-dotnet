using System.Data;
using System.Diagnostics;
using System.Globalization;

namespace KingbaseConnector.IntegrationTests;

/// <summary>E71 LIVE acceptance: a caller cancellation cancels the statement, drains to ReadyForQuery and keeps the session.</summary>
public sealed class GracefulCancelLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task CallerCancellationStopsTheStatementAndKeepsTheBackendSession(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = new KingbaseConnection(settings.ConnectionString);
            await connection.OpenAsync(token);
            var pid = await BackendPidAsync(connection, token);
            await CancelSleepAsync(connection, token);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(pid, await BackendPidAsync(connection, token));
            Assert.Equal(42, await ScalarAsync(connection, "SELECT 42", token));
        });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PreparedExecutionCancellationAlsoDrains(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = new KingbaseConnection(settings.ConnectionString);
            await connection.OpenAsync(token);
            using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
            await command.PrepareAsync(token);
            await CancelCommandAsync(command, token);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(42, await ScalarAsync(connection, "SELECT 42", token));
        });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task CancellationInsideATransactionLeavesAFailedTransactionThatRollbackRecovers(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = new KingbaseConnection(settings.ConnectionString);
            await connection.OpenAsync(token);
            using var transaction = await connection.BeginTransactionAsync(token);
            using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection)
            {
                CommandTimeout = 0,
                Transaction = transaction
            };
            await CancelCommandAsync(command, token);
            Assert.Equal(ConnectionState.Open, connection.State);
            // The drained ReadyForQuery reported a failed transaction, so a new statement is rejected until rollback.
            using var rejected = new KingbaseCommand("SELECT 1", connection) { Transaction = transaction };
            Assert.Equal("25P02", (await Assert.ThrowsAsync<KingbaseException>(() => rejected.ExecuteScalarAsync(token))).SqlState);
            await transaction.RollbackAsync(token);
            Assert.Equal(42, await ScalarAsync(connection, "SELECT 42", token));
        });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PooledBorrowerCancellationDoesNotDisturbTheSiblingBorrower(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            settings.Pooling = true;
            settings.MinPoolSize = 0;
            settings.MaxPoolSize = 2;
            settings.ConnectionIdleLifetime = 0;
            using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
            using var canceled = source.CreateConnection();
            using var sibling = source.CreateConnection();
            await canceled.OpenAsync(token);
            await sibling.OpenAsync(token);
            var siblingPid = await BackendPidAsync(sibling, token);
            await CancelSleepAsync(canceled, token);
            Assert.Equal(ConnectionState.Open, canceled.State);
            Assert.Equal(siblingPid, await BackendPidAsync(sibling, token));
            Assert.Equal(42, await ScalarAsync(sibling, "SELECT 42", token));
            Assert.NotEqual(0L, await BackendPidAsync(canceled, token));
        });

    static async Task CancelSleepAsync(KingbaseConnection connection, CancellationToken token)
    {
        using var command = new KingbaseCommand("SELECT pg_sleep(8)", connection) { CommandTimeout = 0 };
        await CancelCommandAsync(command, token);
    }

    static async Task CancelCommandAsync(KingbaseCommand command, CancellationToken token)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        caller.CancelAfter(TimeSpan.FromMilliseconds(400));
        var watch = Stopwatch.StartNew();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(caller.Token));
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
    }

    static async Task<long> BackendPidAsync(KingbaseConnection connection, CancellationToken token)
    {
        using var command = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    static async Task<int> ScalarAsync(KingbaseConnection connection, string sql, CancellationToken token)
    {
        using var command = new KingbaseCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }
}
