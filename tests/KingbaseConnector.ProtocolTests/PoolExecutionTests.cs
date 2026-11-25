using System.Data;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PgSessionResetTests;

namespace KingbaseConnector.ProtocolTests;

public sealed partial class PoolExecutionTests
{
    internal static KingbaseConnectionStringBuilder Configuration(TcpListener listener)
    {
        var settings = PoolSettings(listener); settings.MaxPoolSize = 1; settings.ConnectionIdleLifetime = 0;
        return settings;
    }
    internal static async Task InitializePool(Stream stream, CancellationToken token, string[]? baseline = null, bool readStartup = true)
    { await Initialize(stream, token, readStartup); await Capture(stream, token, State(baseline)); }
    internal static async Task Reset(Stream stream, CancellationToken token, char previous = 'I', string[]? baseline = null)
    {
        if (previous != 'I')
        {
            Assert.Equal("ROLLBACK", await ReadQueryAsync(stream, token)); await stream.WriteAsync(Control("ROLLBACK"), token);
        }
        Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, token)); await stream.WriteAsync(Control("DISCARD ALL"), token);
        await RefreshCatalog(stream, token); await Restore(stream, 12004, token); await Capture(stream, token, State(baseline)); await Identity(stream, token);
    }
    internal static async Task Ping(Stream stream, CancellationToken token)
    { Assert.Equal("SELECT 1", await ReadQueryAsync(stream, token)); await stream.WriteAsync(Scalar("1"), token); }
    internal static async Task Query(Stream stream, string sql, uint oid, CancellationToken token, char status = 'I')
    {
        var frames = await ReadExtended(stream, token);
        Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes(sql + "\0"), [0, 0])), frames[0]);
        await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, oid, 4), Row(["7"]), Complete(), Frame('Z', [(byte)status])), token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateSourceReusesOneResetSocketAcrossLogicalConnections(bool sync)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await InitializePool(stream, guard.Token);
            for (var borrower = 0; borrower < 2; borrower++)
            {
                if (borrower != 0) await Ping(stream, guard.Token);
                await Query(stream, "SELECT 7", borrower == 0 ? uint.MaxValue : 12002, guard.Token);
                await Reset(stream, guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        for (var borrower = 0; borrower < 2; borrower++)
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
            using var connection = sync ? source.OpenConnection() : await source.OpenConnectionAsync(opening.Token);
            opening.Cancel(); // Initialization/idle-validation tokens must be detached before public use.
            using var command = new KingbaseCommand("SELECT 7", connection);
            Assert.Equal(7, await command.ExecuteScalarAsync(guard.Token));
            if (sync) connection.Close(); else await connection.CloseAsync();
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        source.Dispose(); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task MaximumOnePreservesFifoAndRemovesCanceledWaiters()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await InitializePool(stream, guard.Token);
            await Reset(stream, guard.Token);
            await Ping(stream, guard.Token); await Query(stream, "SELECT second", 12002, guard.Token); await Reset(stream, guard.Token);
            await Ping(stream, guard.Token); await Query(stream, "SELECT third", 12002, guard.Token); await Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token);
        using var canceled = new CancellationTokenSource();
        using var skipped = source.CreateConnection(); var skippedOpen = skipped.OpenAsync(canceled.Token);
        using var second = source.CreateConnection(); var secondOpen = second.OpenAsync(guard.Token);
        using var third = source.CreateConnection(); var thirdOpen = third.OpenAsync(guard.Token);
        Assert.False(secondOpen.IsCompleted); Assert.False(thirdOpen.IsCompleted);
        canceled.Cancel();
        Assert.Equal(canceled.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skippedOpen)).CancellationToken);
        Assert.Equal(ConnectionState.Closed, skipped.State);
        await first.CloseAsync(); await secondOpen.WaitAsync(guard.Token); Assert.False(thirdOpen.IsCompleted);
        using (var query = new KingbaseCommand("SELECT second", second)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await second.CloseAsync(); await thirdOpen.WaitAsync(guard.Token);
        using (var query = new KingbaseCommand("SELECT third", third)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await third.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task PoolWaitTimeoutIsDistinctAndDoesNotOpenAnExtraSocket()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); settings.Timeout = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token);
        using var waiter = source.CreateConnection();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => waiter.OpenAsync(guard.Token));
        Assert.Contains("pool wait", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-password", error.ToString()); Assert.Equal(ConnectionState.Closed, waiter.State);
        Assert.False(listener.Pending());
        await first.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ClearDoesNotAbortTheActiveLeaseOrEvadeCapacity()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using (var first = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = first.GetStream(); await InitializePool(stream, guard.Token);
                await Query(stream, "SELECT still-active", uint.MaxValue, guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var next = await listener.AcceptTcpClientAsync(guard.Token);
            using var nextStream = next.GetStream(); await InitializePool(nextStream, guard.Token); await Reset(nextStream, guard.Token);
            Assert.Equal(0, await nextStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var firstConnection = await source.OpenConnectionAsync(guard.Token);
        using var waiting = source.CreateConnection(); var nextOpen = waiting.OpenAsync(guard.Token);
        source.ClearPool(); source.ClearPool(); Assert.False(nextOpen.IsCompleted); Assert.False(listener.Pending());
        using (var query = new KingbaseCommand("SELECT still-active", firstConnection)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await firstConnection.CloseAsync(); await nextOpen.WaitAsync(guard.Token);
        await waiting.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task SourceDisposalWakesWaitersAndLetsExistingLeaseFinish()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await InitializePool(stream, guard.Token);
            await Query(stream, "SELECT active", uint.MaxValue, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var active = await source.OpenConnectionAsync(guard.Token);
        using var waiting = source.CreateConnection(); var opening = waiting.OpenAsync(guard.Token);
        source.Dispose(); await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);
        Assert.Equal(ConnectionState.Open, active.State);
        using (var query = new KingbaseCommand("SELECT active", active)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await active.CloseAsync(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task FailedResetClosesTheSocketAndFreesCapacityForANewSession()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using (var first = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = first.GetStream(); await InitializePool(stream, guard.Token);
                Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token));
                await stream.WriteAsync(Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C42501\0Mprivate-error\0\0")), Ready()), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var next = await listener.AcceptTcpClientAsync(guard.Token);
            using var nextStream = next.GetStream(); await InitializePool(nextStream, guard.Token); await Reset(nextStream, guard.Token);
            Assert.Equal(0, await nextStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = await source.OpenConnectionAsync(guard.Token);
        await connection.CloseAsync(); await connection.OpenAsync(guard.Token);
        await connection.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }
}
