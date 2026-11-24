using System.Data;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed partial class PoolExecutionTests
{
    [Fact]
    public async Task BufferedReaderAndTransactionAreRevokedBeforeTheSameConnectionRentsAgain()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = socket.GetStream(); await InitializePool(stream, guard.Token);
            await SavepointExecutionTests.Limit(stream, guard.Token);
            var begin = await ReadExtended(stream, guard.Token);
            Assert.Equal(Frame('P', Join([0], Encoding.ASCII.GetBytes("BEGIN ISOLATION LEVEL READ COMMITTED\0"), [0, 0])), begin[0]);
            await stream.WriteAsync(TransactionExecutionTests.Result("BEGIN", 'T'), guard.Token);
            await Query(stream, "SELECT old-reader", uint.MaxValue, guard.Token, 'T');
            await Reset(stream, guard.Token, 'T'); await Ping(stream, guard.Token);
            await Query(stream, "SELECT new-reader", 12002, guard.Token); await Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = await source.OpenConnectionAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        using var oldCommand = new KingbaseCommand("SELECT old-reader", connection) { Transaction = transaction };
        using var reader = await oldCommand.ExecuteReaderAsync(CommandBehavior.CloseConnection, guard.Token);
        await connection.CloseAsync(); Assert.True(reader.IsClosed); Assert.Null(transaction.Connection);
        await connection.OpenAsync(guard.Token);
        reader.Close(); oldCommand.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldCommand.ExecuteScalarAsync(guard.Token));
        transaction.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync(guard.Token));
        Assert.Equal(ConnectionState.Open, connection.State);
        using (var next = new KingbaseCommand("SELECT new-reader", connection)) Assert.Equal(7, await next.ExecuteScalarAsync(guard.Token));
        await connection.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task CloseDuringExecutionAbortsAndRetiresBeforeAWaitingBorrowerOpens()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using (var old = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = old.GetStream(); await InitializePool(stream, guard.Token);
                var frames = await ReadExtended(stream, guard.Token); Assert.Contains("SELECT blocked", Encoding.UTF8.GetString(frames[0]));
                executing.SetResult(); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var current = await listener.AcceptTcpClientAsync(guard.Token);
            using var currentStream = current.GetStream(); await InitializePool(currentStream, guard.Token);
            await Query(currentStream, "SELECT next", uint.MaxValue, guard.Token); await Reset(currentStream, guard.Token);
            Assert.Equal(0, await currentStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token);
        using var blocked = new KingbaseCommand("SELECT blocked", first); var exchange = blocked.ExecuteScalarAsync(guard.Token);
        await executing.Task.WaitAsync(guard.Token);
        using var next = source.CreateConnection(); var waiting = next.OpenAsync(guard.Token);
        await first.CloseAsync(); await Assert.ThrowsAnyAsync<Exception>(() => exchange); await waiting.WaitAsync(guard.Token);
        blocked.Cancel(); Assert.Equal(ConnectionState.Closed, first.State);
        using (var query = new KingbaseCommand("SELECT next", next)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await next.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task BrokenIdleSocketIsReplacedBeforeOpenReturnsWithoutReplayingApplicationSql()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using (var old = await listener.AcceptTcpClientAsync(guard.Token))
            { using var stream = old.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token); }
            closed.SetResult();
            using var current = await listener.AcceptTcpClientAsync(guard.Token);
            using var currentStream = current.GetStream(); await InitializePool(currentStream, guard.Token);
            await Query(currentStream, "SELECT once", uint.MaxValue, guard.Token); await Reset(currentStream, guard.Token);
            Assert.Equal(0, await currentStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token); await first.CloseAsync(); await closed.Task.WaitAsync(guard.Token);
        using var next = await source.OpenConnectionAsync(guard.Token);
        using (var query = new KingbaseCommand("SELECT once", next)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await next.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ClearAllClosesPrivateIdlePoolsAndRetiresDirectActiveLeasesOnlyOnReturn()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var configuration = Configuration(listener).ConnectionString;
        using var source = new KingbaseDataSourceBuilder(configuration).Build();
        var peer = Task.Run(async () =>
        {
            using var direct = await listener.AcceptTcpClientAsync(guard.Token);
            using var directStream = direct.GetStream(); await InitializePool(directStream, guard.Token);
            using var owned = await listener.AcceptTcpClientAsync(guard.Token);
            using var ownedStream = owned.GetStream(); await InitializePool(ownedStream, guard.Token);
            var active = Task.Run(async () =>
            { await Query(directStream, "SELECT active-after-clear", uint.MaxValue, guard.Token); Assert.Equal(0, await directStream.ReadAsync(new byte[1], guard.Token)); }, guard.Token);
            await Reset(ownedStream, guard.Token); Assert.Equal(0, await ownedStream.ReadAsync(new byte[1], guard.Token));
            await active.WaitAsync(guard.Token);
        }, guard.Token);
        using var directConnection = new KingbaseConnection(configuration); await directConnection.OpenAsync(guard.Token);
        using var privateConnection = await source.OpenConnectionAsync(guard.Token); await privateConnection.CloseAsync();
        KingbaseConnection.ClearAllPools();
        using (var query = new KingbaseCommand("SELECT active-after-clear", directConnection)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await directConnection.CloseAsync(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ClosedStateHandlerCanRentFromThePoolWithoutADeadlock()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = socket.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token);
            await Ping(stream, guard.Token); await Reset(stream, guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token);
        KingbaseConnection? reentrant = null;
        first.StateChange += (_, change) => { if (change.CurrentState == ConnectionState.Closed) reentrant = source.OpenConnection(); };
        await first.CloseAsync().WaitAsync(guard.Token);
        Assert.NotNull(reentrant); await reentrant!.CloseAsync(); reentrant.Dispose();
        source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ClosingAQueuedOpenCannotCloseItsLaterReopenedGeneration()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = socket.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token);
            await Ping(stream, guard.Token); await Reset(stream, guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var active = await source.OpenConnectionAsync(guard.Token);
        using var waiting = source.CreateConnection(); var old = waiting.OpenAsync(guard.Token);
        await waiting.CloseAsync(); var next = waiting.OpenAsync(guard.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        await active.CloseAsync(); await next.WaitAsync(guard.Token);
        Assert.Equal(ConnectionState.Open, waiting.State); await waiting.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task FailedOpeningKeepsCapacityReservedUntilItsTransportIsClosed()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using (var old = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = old.GetStream(); await ReadStartupAsync(stream, guard.Token); accepted.SetResult();
                await release.Task.WaitAsync(guard.Token); await stream.WriteAsync(StartupError(), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var currentStream = socket.GetStream(); await InitializePool(currentStream, guard.Token); await Reset(currentStream, guard.Token);
            Assert.Equal(0, await currentStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = source.CreateConnection(); var failed = first.OpenAsync(guard.Token); await accepted.Task.WaitAsync(guard.Token);
        using var second = source.CreateConnection(); var waiting = second.OpenAsync(guard.Token);
        Assert.False(waiting.IsCompleted); Assert.False(listener.Pending()); release.SetResult();
        Assert.Equal("28000", (await Assert.ThrowsAsync<KingbaseException>(() => failed)).SqlState);
        await waiting.WaitAsync(guard.Token); await second.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }
}
