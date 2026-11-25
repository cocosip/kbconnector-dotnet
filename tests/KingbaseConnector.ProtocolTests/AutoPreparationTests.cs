using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicPreparedTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class AutoPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstFailedAutomaticExecutionStillOwnsHandleForUnprepare(bool cachedByOtherCommand)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener); settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 2;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("10"), guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT 10/$1", guard.Token, uint.MaxValue);
            await ReadFrames(stream, "BDES", guard.Token);
            if (cachedByOtherCommand)
            {
                await stream.WriteAsync(PreparedValue("10"), guard.Token);
                await ReadFrames(stream, "BDES", guard.Token);
            }
            await stream.WriteAsync(Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C22012\0Mprivate-error\0\0")), Ready()), guard.Token);
            await ExpectClose(stream, "kbp_1", guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("10"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var first = new KingbaseCommand("SELECT 10/@p", connection); first.Parameters.AddWithValue("p", 1);
        using var second = new KingbaseCommand("SELECT 10/@p", connection); second.Parameters.AddWithValue("p", 0);
        Assert.Equal(10, await first.ExecuteScalarAsync(guard.Token));
        if (cachedByOtherCommand) Assert.Equal(10, await first.ExecuteScalarAsync(guard.Token));
        var failing = cachedByOtherCommand ? second : first; failing.Parameters[0].Value = 0;
        Assert.Equal("22012", (await Assert.ThrowsAsync<KingbaseException>(() => failing.ExecuteScalarAsync(guard.Token))).SqlState);
        await failing.UnprepareAsync(guard.Token);
        failing.Parameters[0].Value = 1;
        Assert.Equal(10, await failing.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeChangeBetweenAutomaticExchangesRetiresBeforeFurtherWrites(bool eviction)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener); settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            if (eviction)
            {
                await ExpectPrepare(stream, "kbp_1", "SELECT 1", guard.Token);
                await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("1"), guard.Token);
            }
            await ReadFrames(stream, eviction ? "CS" : "PS", guard.Token);
            await stream.WriteAsync(Join(Status("database_mode", "oracle"), Frame(eviction ? '3' : '1'), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 1", connection);
        if (eviction) { Assert.Equal(1, await command.ExecuteScalarAsync(guard.Token)); command.CommandText = "SELECT 2"; }
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ThresholdUsesSuccessfulExecutionsAcrossCommandsAndFreshValues(int minimum)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener);
        settings["Max Auto Prepare"] = 2; settings["Auto Prepare Min Usages"] = minimum;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            for (var i = 1; i < minimum; i++)
            { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value(i.ToString()), guard.Token); }
            await ExpectPrepare(stream, "kbp_1", "SELECT $1", guard.Token, uint.MaxValue);
            for (var i = minimum; i <= minimum + 1; i++)
            {
                var frames = await ReadFrames(stream, "BDES", guard.Token);
                Assert.Contains("\0kbp_1\0", Encoding.UTF8.GetString(frames[0]));
                Assert.Contains(i.ToString(), Encoding.UTF8.GetString(frames[0]));
                await stream.WriteAsync(PreparedValue(i.ToString()), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        for (var i = 1; i <= minimum + 1; i++)
        {
            using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.AddWithValue("p", i);
            Assert.Equal(i, await command.ExecuteScalarAsync(guard.Token));
        }
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task AutomaticEvictionAndParseShareOneCommandDeadline()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener); settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT 1", guard.Token);
            await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("1"), guard.Token);
            await ReadFrames(stream, "CS", guard.Token);
            await Task.Delay(600, guard.Token); await stream.WriteAsync(Join(Frame('3'), Ready()), guard.Token);
            await ReadFrames(stream, "PS", guard.Token);
            await Task.Delay(600, guard.Token);
            // Each response would fit a separately reset one-second timeout; the combined operation must expire.
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 1", connection) { CommandTimeout = 1 };
        Assert.Equal(1, await command.ExecuteScalarAsync(guard.Token));
        command.CommandText = "SELECT 2";
        await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task PoolResetRestartsAutomaticThresholdAndUsesRefreshedCatalog()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = PoolExecutionTests.Configuration(listener); settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 2;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await PoolExecutionTests.InitializePool(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
            await ExpectPrepare(stream, "kbp_1", "SELECT $1", guard.Token, uint.MaxValue);
            await ReadFrames(stream, "BDES", guard.Token); await stream.WriteAsync(PreparedValue("42"), guard.Token);
            await PoolExecutionTests.Reset(stream, guard.Token); await PoolExecutionTests.Ping(stream, guard.Token);
            var frames = await ReadExtended(stream, guard.Token); Assert.Equal(Int32(12002), frames[0][^4..]);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, 12002, 4), Row(["42"]), Complete(), Ready()), guard.Token);
            await ExpectPrepare(stream, "kbp_2", "SELECT $1", guard.Token, 12002);
            await ReadFrames(stream, "BDES", guard.Token);
            await stream.WriteAsync(Join(Frame('2'), Description(1, 12002, 4), Row(["42"]), Complete(), Ready()), guard.Token);
            await PoolExecutionTests.Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = await source.OpenConnectionAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT :p", connection); command.Parameters.AddWithValue("p", 42);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        await connection.CloseAsync(); await connection.OpenAsync(guard.Token);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        await connection.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ManagedTransactionControlsRemainUnnamedWhileOrdinaryQueriesPrepare()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener); settings.MaxAutoPrepare = 1; settings.AutoPrepareMinUsages = 1;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await SavepointExecutionTests.Limit(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(TransactionExecutionTests.Result("BEGIN", 'T'), guard.Token);
            await ReadFrames(stream, "PS", guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('Z', [(byte)'T'])), guard.Token);
            await ReadFrames(stream, "BDES", guard.Token);
            await stream.WriteAsync(Join(Frame('2'), Description(1, uint.MaxValue, 4), Row(["42"]), Complete(), Frame('Z', [(byte)'T'])), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(TransactionExecutionTests.Result("COMMIT", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 42", connection) { Transaction = transaction };
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        await transaction.CommitAsync(guard.Token);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
