using System.Reflection;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed partial class PoolExecutionTests
{
    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    [InlineData("source")]
    public async Task IdleValidationAbortsEvenWhenTheStreamIgnoresReadCancellation(string cancel)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Configuration(listener); if (cancel == "timeout") settings.Timeout = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var original = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = original.GetStream(); await InitializePool(stream, guard.Token); await Reset(stream, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            if (cancel == "source") return;
            using var replacement = await listener.AcceptTcpClientAsync(guard.Token);
            using var replacementStream = replacement.GetStream(); await InitializePool(replacementStream, guard.Token);
            await Reset(replacementStream, guard.Token); Assert.Equal(0, await replacementStream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var lease = await source.Pool!.RentAsync(guard.Token); var physical = lease.Session;
        await lease.ReturnAsync(canReset: true);
        // Model older network/TLS hosts without relying on the current runtime's cancellable socket implementation.
        var wire = (PgWire)typeof(PgQuerySession).GetField("_wire", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(physical.Queries)!;
        using var blocked = new QueryStream([]) { BlockAtEnd = true, IgnoreReadCancellation = true };
        typeof(PgWire).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(wire, blocked);
        using var caller = new CancellationTokenSource(); using var connection = source.CreateConnection();
        var opening = connection.OpenAsync(caller.Token);
        try
        {
            await blocked.ReadBlocked.Task.WaitAsync(guard.Token);
            if (cancel == "caller") caller.Cancel();
            else if (cancel == "close") await connection.CloseAsync();
            else if (cancel == "source") source.Dispose();
            var failure = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(opening.IsCompleted, "Validation must abort a pending read even when its Stream ignores cancellation.");
            if (cancel == "timeout") Assert.IsType<TimeoutException>(failure);
            else if (cancel == "source") Assert.IsType<ObjectDisposedException>(failure);
            else Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.False(physical.IsUsable); Assert.True(blocked.DisposeCount > 0);
            if (cancel != "source")
            {
                using var replacement = await source.OpenConnectionAsync(guard.Token); await replacement.CloseAsync(); source.Dispose();
            }
            await peer.WaitAsync(guard.Token);
        }
        finally { blocked.Dispose(); try { await opening; } catch { } }
    }
}
