using System.Data;
using System.Net.Sockets;
using System.Reflection;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed partial class PoolExecutionTests
{
    [Fact]
    public async Task DirectAliasesAndExplicitDefaultsShareOnePoolAndClearReclaimsItsConfiguration()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener);
        var alias = new KingbaseConnectionStringBuilder();
        foreach (string key in settings.Keys.Cast<string>().Reverse()) alias[key] = settings[key];
        alias.Remove("Pooling"); alias["Server"] = settings.Host; alias["User ID"] = settings.Username; alias["Pwd"] = settings.Password;
        alias.Timeout = 15; alias.CommandTimeout = 30; alias.ClientEncoding = "utf-8";
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await InitializePool(stream, guard.Token);
            await Reset(stream, guard.Token); await Ping(stream, guard.Token); await Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = new KingbaseConnection(settings.ConnectionString); await first.OpenAsync(guard.Token);
        var lease = typeof(KingbaseConnection).GetField("_lease", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first)!;
        var pool = lease.GetType().GetProperty("Pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!;
        await first.CloseAsync();
        using var second = new KingbaseConnection(alias.ConnectionString); await second.OpenAsync(guard.Token); await second.CloseAsync();
        KingbaseConnection.ClearPool(second); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
        Assert.Equal("", pool.GetType().GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool));
    }

    [Fact]
    public async Task CredentialsAndPrivateSourcesNeverShareTheDirectPool()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); var rotated = new KingbaseConnectionStringBuilder(settings.ConnectionString) { Password = "rotated-password" };
        using var source1 = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var source2 = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            var sockets = new List<TcpClient>();
            try
            {
                for (var i = 0; i < 4; i++)
                { var socket = await listener.AcceptTcpClientAsync(guard.Token); sockets.Add(socket); await InitializePool(socket.GetStream(), guard.Token); }
                foreach (var socket in sockets)
                { await Reset(socket.GetStream(), guard.Token); Assert.Equal(0, await socket.GetStream().ReadAsync(new byte[1], guard.Token)); }
            }
            finally { foreach (var socket in sockets) socket.Dispose(); }
        }, guard.Token);
        using var direct = new KingbaseConnection(settings.ConnectionString); await direct.OpenAsync(guard.Token);
        using var changed = new KingbaseConnection(rotated.ConnectionString); await changed.OpenAsync(guard.Token);
        using var private1 = await source1.OpenConnectionAsync(guard.Token);
        using var private2 = await source2.OpenConnectionAsync(guard.Token);
        await direct.CloseAsync(); KingbaseConnection.ClearPool(direct);
        await changed.CloseAsync(); KingbaseConnection.ClearPool(changed);
        await private1.CloseAsync(); source1.ClearPool();
        await private2.CloseAsync(); source2.ClearPool(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ResetAndRepeatedClearKeepCapacityUntilPhysicalClosure()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        var resetting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using (var first = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = first.GetStream(); await InitializePool(stream, guard.Token);
                Assert.Equal("DISCARD ALL", await ReadQueryAsync(stream, guard.Token)); resetting.SetResult();
                await release.Task.WaitAsync(guard.Token); await stream.WriteAsync(PgSessionResetTests.Control("DISCARD ALL"), guard.Token);
                await PgSessionResetTests.RefreshCatalog(stream, guard.Token); await PgSessionResetTests.Restore(stream, 12004, guard.Token);
                await PgSessionResetTests.Capture(stream, guard.Token); await PgSessionResetTests.Identity(stream, guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var next = await listener.AcceptTcpClientAsync(guard.Token);
            using var nextStream = next.GetStream(); await InitializePool(nextStream, guard.Token); await Reset(nextStream, guard.Token);
            Assert.Equal(0, await nextStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = await source.OpenConnectionAsync(guard.Token);
        var closing = connection.CloseAsync(); await resetting.Task.WaitAsync(guard.Token);
        Assert.Same(closing, connection.CloseAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenAsync(guard.Token));
        using var waiting = source.CreateConnection(); var opening = waiting.OpenAsync(guard.Token);
        source.ClearPool(); source.ClearPool(); Assert.False(opening.IsCompleted); Assert.False(listener.Pending());
        release.SetResult(); await closing.WaitAsync(guard.Token); await opening.WaitAsync(guard.Token);
        await waiting.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ClearDuringInitializationRetiresOldGenerationWithoutSkippingAnEarlierWaiter()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); settings.Timeout = 0;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using (var old = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = old.GetStream(); accepted.SetResult(); await release.Task.WaitAsync(guard.Token);
                await InitializePool(stream, guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var current = await listener.AcceptTcpClientAsync(guard.Token);
            using var currentStream = current.GetStream(); await InitializePool(currentStream, guard.Token);
            await Reset(currentStream, guard.Token); await Ping(currentStream, guard.Token); await Reset(currentStream, guard.Token);
            Assert.Equal(0, await currentStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = source.CreateConnection(); var oldOpen = first.OpenAsync(guard.Token); await accepted.Task.WaitAsync(guard.Token);
        using var second = source.CreateConnection(); var waiting = second.OpenAsync(guard.Token);
        source.ClearPool(); Assert.False(waiting.IsCompleted); release.SetResult();
        await waiting.WaitAsync(guard.Token); Assert.False(oldOpen.IsCompleted);
        await second.CloseAsync(); await oldOpen.WaitAsync(guard.Token);
        await first.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task CancellationDuringIdleValidationReleasesTheGrantedSlotExactlyOnce()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Configuration(listener).ConnectionString).Build();
        using var caller = new CancellationTokenSource();
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using (var old = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = old.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token);
                Assert.Equal("SELECT 1", await ReadQueryAsync(stream, guard.Token)); validating.SetResult();
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var current = await listener.AcceptTcpClientAsync(guard.Token);
            using var currentStream = current.GetStream(); await InitializePool(currentStream, guard.Token); await Reset(currentStream, guard.Token);
            Assert.Equal(0, await currentStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token); await first.CloseAsync();
        using var canceled = source.CreateConnection(); var opening = canceled.OpenAsync(caller.Token); await validating.Task.WaitAsync(guard.Token);
        using var waiting = source.CreateConnection(); var next = waiting.OpenAsync(guard.Token);
        caller.Cancel(); Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening)).CancellationToken);
        await next.WaitAsync(guard.Token); await waiting.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task OpenBudgetIncludesWaitingBeforeAReplacementConnectionStarts()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); settings.Timeout = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using (var old = await listener.AcceptTcpClientAsync(guard.Token))
            { using var stream = old.GetStream(); await InitializePool(stream, guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }
            using var current = await listener.AcceptTcpClientAsync(guard.Token);
            using var currentStream = current.GetStream(); await ReadStartupAsync(currentStream, guard.Token);
            var closed = currentStream.ReadAsync(new byte[1], guard.Token).AsTask();
            if (await Task.WhenAny(closed, Task.Delay(500, guard.Token)) != closed) await currentStream.WriteAsync(StartupError(), guard.Token);
            Assert.Equal(0, await closed);
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token);
        using var second = source.CreateConnection(); var opening = second.OpenAsync(guard.Token);
        await Task.Delay(700, guard.Token); source.ClearPool(); await first.CloseAsync();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => opening);
        Assert.Contains("pooled opening", error.Message, StringComparison.OrdinalIgnoreCase); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task IdleEligibilityIsCheckedAtRentAndRespectsTheLazyMinimum(int minimum)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); settings.MinPoolSize = minimum; settings.ConnectionIdleLifetime = 1; settings.ConnectionPruningInterval = int.MaxValue;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var old = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = old.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token);
            if (minimum == 1)
            { await Ping(stream, guard.Token); await Reset(stream, guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }
            else
            {
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
                using var current = await listener.AcceptTcpClientAsync(guard.Token);
                using var currentStream = current.GetStream(); await InitializePool(currentStream, guard.Token); await Reset(currentStream, guard.Token);
                Assert.Equal(0, await currentStream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        Assert.False(listener.Pending()); // Minimum is lazy; constructing a source does not pre-open.
        using var first = await source.OpenConnectionAsync(guard.Token); await first.CloseAsync(); await Task.Delay(1100, guard.Token);
        using var second = await source.OpenConnectionAsync(guard.Token); await second.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task TotalLifetimeDoesNotInterruptActiveWorkAndOverridesMinimumOnReturn()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); settings.ConnectionLifetime = 1; settings.MinPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = socket.GetStream(); await InitializePool(stream, guard.Token);
            await Query(stream, "SELECT aged", uint.MaxValue, guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token); await Task.Delay(1100, guard.Token);
        using (var query = new KingbaseCommand("SELECT aged", first)) Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token));
        await first.CloseAsync(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task BackgroundPruningClosesIdleSessionsWithoutAnotherRent()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); settings.ConnectionIdleLifetime = 1; settings.ConnectionPruningInterval = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = socket.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = await source.OpenConnectionAsync(guard.Token); await first.CloseAsync();
        await peer.WaitAsync(guard.Token); // Source is still alive; only the pruning cadence can close the socket.
    }
}
