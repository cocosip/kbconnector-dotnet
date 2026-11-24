using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PgSessionResetTests;

namespace KingbaseConnector.ProtocolTests;

public sealed partial class PoolExecutionTests
{
    [Fact]
    public async Task ChangeDatabaseReturnsTheOriginalLeaseToItsOriginalPool()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var configuration = Configuration(listener).ConnectionString;
        var otherBaseline = Baseline(); otherBaseline[0] = "other";
        var peer = Task.Run(async () =>
        {
            using var original = await listener.AcceptTcpClientAsync(guard.Token);
            using var originalStream = original.GetStream(); await InitializePool(originalStream, guard.Token);
            await Reset(originalStream, guard.Token);
            using var other = await listener.AcceptTcpClientAsync(guard.Token);
            using var otherStream = other.GetStream();
            Assert.Contains("database\0other\0", Encoding.UTF8.GetString(await ReadStartupAsync(otherStream, guard.Token)));
            await InitializePool(otherStream, guard.Token, otherBaseline, readStartup: false);
            await Ping(originalStream, guard.Token); await Query(originalStream, "SELECT original", 12002, guard.Token);
            await Reset(originalStream, guard.Token); await Reset(otherStream, guard.Token, baseline: otherBaseline);
            Assert.Equal(0, await originalStream.ReadAsync(new byte[1], guard.Token));
            Assert.Equal(0, await otherStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = new KingbaseConnection(configuration); await connection.OpenAsync(guard.Token);
        connection.ChangeDatabase("other"); Assert.Equal("other", connection.Database);
        using var sameDatabase = new KingbaseConnection(configuration); await sameDatabase.OpenAsync(guard.Token);
        using (var command = new KingbaseCommand("SELECT original", sameDatabase)) Assert.Equal(7, await command.ExecuteScalarAsync(guard.Token));
        await sameDatabase.CloseAsync(); await connection.CloseAsync(); KingbaseConnection.ClearAllPools();
        await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task TwentyConcurrentBorrowersShareExactlyTwoPhysicalSessions()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var settings = Configuration(listener); settings.MaxPoolSize = 2;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var opened = 0; var queries = 0;
        var bothOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Serve()
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await InitializePool(stream, guard.Token);
            var first = true; var code = new byte[1];
            while (await stream.ReadAsync(code, guard.Token) != 0)
            {
                if (code[0] == 'Q')
                {
                    // The idle validation query is the only Simple query between borrowers.
                    var rest = new byte[13]; await stream.ReadExactlyAsync(rest, guard.Token);
                    Assert.Equal(Join(Int32(13), Encoding.ASCII.GetBytes("SELECT 1\0")), rest);
                    await stream.WriteAsync(Scalar("1"), guard.Token); continue;
                }
                Assert.Equal((byte)'P', code[0]);
                var frames = await ReadExtended(stream, guard.Token, parseCodeConsumed: true);
                Assert.Equal(Frame('P', Join([0], Encoding.ASCII.GetBytes("SELECT 7\0"), [0, 0])), frames[0]);
                await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, first ? uint.MaxValue : 12002, 4), Row(["7"]), Complete(), Ready()), guard.Token);
                Interlocked.Increment(ref queries); first = false; await Reset(stream, guard.Token);
            }
        }
        var peers = Task.WhenAll(Serve(), Serve());
        var borrowers = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var connection = await source.OpenConnectionAsync(guard.Token);
            if (Interlocked.Increment(ref opened) == 2) bothOpen.TrySetResult();
            await bothOpen.Task.WaitAsync(guard.Token);
            using var command = new KingbaseCommand("SELECT 7", connection);
            Assert.Equal(7, await command.ExecuteScalarAsync(guard.Token)); await connection.CloseAsync();
            Assert.Equal(ConnectionState.Closed, connection.State);
        }).ToArray();
        await Task.WhenAll(borrowers).WaitAsync(guard.Token);
        Assert.Equal(20, queries); Assert.False(listener.Pending()); source.Dispose(); await peers.WaitAsync(guard.Token);
    }
}
