using System.Data;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class PasswordProviderExecutionTests
{
    internal static KingbaseDataSourceBuilder Builder(TcpListener listener, bool pooling = false)
    {
        var settings = pooling ? PoolExecutionTests.Configuration(listener) : Settings(listener);
        settings.Remove("Password"); settings.Pooling = pooling;
        return new KingbaseDataSourceBuilder(settings.ConnectionString);
    }
    internal static async Task Login(NetworkStream stream, string password, CancellationToken token)
    {
        await ReadStartupAsync(stream, token); await stream.WriteAsync(Frame('R', Int32(3)), token);
        Assert.Equal(Encoding.UTF8.GetBytes(password + "\0"), await PgScramStartupTests.ReadPassword(stream, token));
        await Initialize(stream, token, readStartup: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DedicatedPublicOpenUsesTheSnapshottedProviderAndImmutableMetadata(bool sync)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = Builder(listener); var calls = 0; var changedCalls = 0;
        builder.PasswordProvider = async (context, token) =>
        {
            Assert.Equal("127.0.0.1", context.Host); Assert.Equal(builder.ConnectionStringBuilder.Port, context.Port);
            Assert.Equal("db", context.Database); Assert.Equal("user", context.Username); Assert.Equal(KingbaseWireProtocol.PostgreSql, context.WireProtocol);
            Assert.True(token.CanBeCanceled); await Task.Yield(); calls++; return "private-dynamic-" + calls;
        };
        using var source = builder.Build(); builder.PasswordProvider = (_, _) => { changedCalls++; return Task.FromResult("wrong"); };
        var peer = Task.Run(async () =>
        {
            for (var index = 1; index <= 2; index++)
            {
                using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
                await Login(stream, "private-dynamic-" + index, guard.Token); await PoolExecutionTests.Query(stream, "SELECT 7", uint.MaxValue, guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        for (var index = 0; index < 2; index++)
        {
            using var connection = sync ? source.OpenConnection() : await source.OpenConnectionAsync(guard.Token);
            Assert.DoesNotContain("private-dynamic", source.ConnectionString + connection.ConnectionString + source.ToString());
            using var query = new KingbaseCommand("SELECT 7", connection); Assert.Equal(7, await query.ExecuteScalarAsync(guard.Token)); await connection.CloseAsync();
        }
        Assert.Equal(2, calls); Assert.Equal(0, changedCalls); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("null-task")]
    [InlineData("null-result")]
    [InlineData("nul")]
    [InlineData("surrogate")]
    [InlineData("unrelated-cancellation")]
    public async Task ProviderErrorsAreSafeAndNeverOpenASocket(string failure)
    {
        using var listener = Listen(); var builder = Builder(listener); builder.ConnectionStringBuilder.Timeout = 1;
        builder.PasswordProvider = (_, _) => failure switch
        {
            "throw" => throw new InvalidOperationException("private-provider-error"), "null-task" => null!,
            "null-result" => Task.FromResult<string>(null!), "nul" => Task.FromResult("private-password\0"),
            "surrogate" => Task.FromResult("private-password" + new string((char)0xD800, 1)),
            _ => Task.FromCanceled<string>(new CancellationToken(true))
        };
        using var source = builder.Build(); using var connection = source.CreateConnection();
        var error = await Assert.ThrowsAsync<KingbasePasswordProviderException>(() => connection.OpenAsync());
        Assert.DoesNotContain("private-", error.ToString()); Assert.Null(error.InnerException);
        Assert.Equal(ConnectionState.Closed, connection.State); Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    [InlineData("source")]
    public async Task CancellationBoundsAnIgnoringProviderAndLateCompletionCannotOpenASocket(string cancel)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Builder(listener); builder.ConnectionStringBuilder.Timeout = 1;
        CancellationToken providerToken = default;
        builder.PasswordProvider = (_, token) => { providerToken = token; entered.SetResult(); return late.Task; };
        using var source = builder.Build(); using var connection = source.CreateConnection(); var opening = connection.OpenAsync(caller.Token);
        await entered.Task.WaitAsync(guard.Token);
        if (cancel == "caller") caller.Cancel();
        else if (cancel == "close") await connection.CloseAsync();
        else if (cancel == "source") source.Dispose();
        var error = await Record.ExceptionAsync(() => opening.WaitAsync(guard.Token));
        if (cancel == "timeout") Assert.IsType<TimeoutException>(error);
        else if (cancel == "source") Assert.IsType<ObjectDisposedException>(error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(providerToken.IsCancellationRequested); Assert.Equal(ConnectionState.Closed, connection.State);
        late.SetResult("private-late-password"); await late.Task; await Task.Yield(); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task SynchronousOpenRunsTheAsyncProviderOutsideTheCallersSynchronizationContext()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = Builder(listener);
        builder.PasswordProvider = async (_, _) => { Assert.Null(SynchronizationContext.Current); await Task.Yield(); return "private-worker"; };
        using var source = builder.Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await Login(stream, "private-worker", guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var context = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(new RejectingContext()); using var connection = source.OpenConnection(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderSupportsScramAndEmptyPasswordsWithoutChangingAuthenticationSelection(bool empty)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = Builder(listener); var calls = 0; var password = empty ? "" : "pencil";
        builder.PasswordProvider = (_, _) => { calls++; return Task.FromResult(password); }; using var source = builder.Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await PgScramStartupTests.Authenticate(stream, guard.Token, password); await Initialize(stream, guard.Token, readStartup: false);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = await source.OpenConnectionAsync(guard.Token); Assert.Equal(1, calls); await connection.CloseAsync(); await peer.WaitAsync(guard.Token);
    }
    sealed class RejectingContext : SynchronizationContext
    { public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Caller context must not be captured."); }

    [Fact]
    public async Task CancellationAlsoBoundsASynchronousBlockingProviderPrefix()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var caller = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Builder(listener);
        builder.PasswordProvider = (_, _) => { entered.SetResult(); release.Wait(guard.Token); exited.SetResult(); return Task.FromResult("private-late"); };
        using var source = builder.Build(); using var connection = source.CreateConnection();
        try
        {
            var opening = connection.OpenAsync(caller.Token); await entered.Task.WaitAsync(guard.Token); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(guard.Token));
            Assert.False(listener.Pending()); release.Set(); await exited.Task.WaitAsync(guard.Token); Assert.Equal(ConnectionState.Closed, connection.State);
        }
        finally { release.Set(); }
    }
}

public sealed partial class PoolExecutionTests
{
    [Fact]
    public async Task IdenticalSourceSettingsNeverMergeTheirCapturedProviderOrPrivatePool()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = PasswordProviderExecutionTests.Builder(listener, pooling: true); var firstCalls = 0; var secondCalls = 0;
        builder.PasswordProvider = (_, _) => { firstCalls++; return Task.FromResult("private-first"); }; using var first = builder.Build();
        builder.PasswordProvider = (_, _) => { secondCalls++; return Task.FromResult("private-second"); }; using var second = builder.Build();
        builder.PasswordProvider = (_, _) => throw new Exception("Builder changes must not replace captured providers.");
        var peer = Task.Run(async () =>
        {
            using var one = await listener.AcceptTcpClientAsync(guard.Token); using var oneStream = one.GetStream();
            await PasswordProviderExecutionTests.Login(oneStream, "private-first", guard.Token); await PgSessionResetTests.Capture(oneStream, guard.Token);
            using var two = await listener.AcceptTcpClientAsync(guard.Token); using var twoStream = two.GetStream();
            await PasswordProviderExecutionTests.Login(twoStream, "private-second", guard.Token); await PgSessionResetTests.Capture(twoStream, guard.Token);
            await Reset(oneStream, guard.Token); Assert.Equal(0, await oneStream.ReadAsync(new byte[1], guard.Token));
            await Reset(twoStream, guard.Token); Assert.Equal(0, await twoStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var firstConnection = await first.OpenConnectionAsync(guard.Token); using var secondConnection = await second.OpenConnectionAsync(guard.Token);
        await firstConnection.CloseAsync(); first.ClearPool(); await secondConnection.CloseAsync(); second.ClearPool();
        Assert.Equal(1, firstCalls); Assert.Equal(1, secondCalls); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task PoolWaitingAndPasswordRetrievalConsumeOneOriginalOpenBudget()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var calls = 0;
        var builder = PasswordProviderExecutionTests.Builder(listener, pooling: true); builder.ConnectionStringBuilder.Timeout = 1;
        builder.PasswordProvider = async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) != 1) await Task.Delay(500, token);
            return "private-token";
        };
        using var source = builder.Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await PasswordProviderExecutionTests.Login(stream, "private-token", guard.Token); await PgSessionResetTests.Capture(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var active = await source.OpenConnectionAsync(guard.Token); using var next = source.CreateConnection(); var opening = next.OpenAsync(guard.Token);
        await Task.Delay(700, guard.Token); source.ClearPool(); await active.CloseAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => opening); Assert.Equal(2, calls);
        Assert.False(listener.Pending()); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ProviderRotationAuthenticatesNewPhysicalSessionsAndSkipsIdleReuseAndReset()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = PasswordProviderExecutionTests.Builder(listener, pooling: true); var calls = 0;
        builder.PasswordProvider = (_, _) => Task.FromResult("private-token-" + Interlocked.Increment(ref calls));
        using var source = builder.Build();
        var peer = Task.Run(async () =>
        {
            for (var physical = 1; physical <= 2; physical++)
            {
                using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
                await PasswordProviderExecutionTests.Login(stream, "private-token-" + physical, guard.Token); await PgSessionResetTests.Capture(stream, guard.Token);
                await Reset(stream, guard.Token); await Ping(stream, guard.Token); await Reset(stream, guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
        }, guard.Token);
        for (var physical = 1; physical <= 2; physical++)
        {
            for (var logical = 0; logical < 2; logical++)
            {
                using var connection = await source.OpenConnectionAsync(guard.Token); Assert.Equal(physical, calls); await connection.CloseAsync();
                Assert.DoesNotContain("private-token", source.ConnectionString + connection.ConnectionString);
            }
            source.ClearPool();
        }
        await peer.WaitAsync(guard.Token); Assert.Equal(2, calls); Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledProviderReturnsCapacityAndCannotAffectTheNextOpening(bool cancel)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var builder = PasswordProviderExecutionTests.Builder(listener, pooling: true);
        builder.PasswordProvider = (_, _) => Interlocked.Increment(ref calls) == 1 ? First() : Task.FromResult("private-next");
        Task<string> First() { entered.SetResult(); return pending.Task; }
        using var source = builder.Build();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await PasswordProviderExecutionTests.Login(stream, "private-next", guard.Token); await PgSessionResetTests.Capture(stream, guard.Token);
            await Query(stream, "SELECT next", uint.MaxValue, guard.Token); await Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var first = source.CreateConnection(); var oldOpening = first.OpenAsync(caller.Token); await entered.Task.WaitAsync(guard.Token);
        using var next = source.CreateConnection(); var nextOpening = next.OpenAsync(guard.Token); Assert.False(nextOpening.IsCompleted); Assert.Equal(1, calls);
        if (cancel) { caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldOpening); }
        else { pending.SetException(new Exception("private-provider-error")); await Assert.ThrowsAsync<KingbasePasswordProviderException>(() => oldOpening); }
        await nextOpening.WaitAsync(guard.Token); Assert.Equal(2, calls);
        if (cancel) pending.SetException(new Exception("private-late-error"));
        using (var command = new KingbaseCommand("SELECT next", next)) Assert.Equal(7, await command.ExecuteScalarAsync(guard.Token));
        await next.CloseAsync(); source.Dispose(); await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }
}
