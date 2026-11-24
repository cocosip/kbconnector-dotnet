using System.Data;
using System.Data.Common;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class DataSourceExecutionTests
{
    [Fact]
    public async Task DisposalInOpenNotificationPreservesThePublishedSession()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Settings(listener).ConnectionString).Build();
        using var connection = source.CreateConnection();
        connection.StateChange += (_, e) => { if (e.CurrentState == ConnectionState.Open) source.Dispose(); };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("1"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        Assert.Equal(ConnectionState.Open, connection.State);
        using var command = new KingbaseCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("typed")]
    [InlineData("bcl")]
    public async Task SourceDisposalPreservesOpenConnectionsAndReaderButRejectsReopen(string path)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener);
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            for (var i = 0; i < 2; i++)
            {
                await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var connection = path switch
        {
            "sync" => source.OpenConnection(),
            "typed" => await source.OpenConnectionAsync(guard.Token),
            _ => (KingbaseConnection)await ((DbDataSource)source).OpenConnectionAsync(guard.Token)
        };
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("other"));
        Assert.Equal("db", connection.Database);
        using var command = new KingbaseCommand("SELECT 42", connection);
        using var reader = await command.ExecuteReaderAsync(guard.Token);
        source.Dispose(); await source.DisposeAsync();
        Assert.True(reader.Read()); Assert.Equal(42, reader.GetInt32(0));
        reader.Close();
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(ConnectionState.Open, connection.State);
        connection.Close();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.OpenAsync(guard.Token));
        Assert.Equal(ConnectionState.Closed, connection.State);
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingSourceCancelsAnOpeningConnection(bool bcl)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Settings(listener).ConnectionString).Build();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await ReadStartupAsync(stream, guard.Token); received.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var opening = bcl ? ((DbDataSource)source).OpenConnectionAsync(guard.Token).AsTask() : OpenTyped();
        await received.Task.WaitAsync(guard.Token);
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);
        await peer.WaitAsync(guard.Token);
        async Task<DbConnection> OpenTyped() => await source.OpenConnectionAsync(guard.Token);
    }

    [Fact]
    public async Task SourceCanRetryAfterFailedOpenAndInitializationStillUsesPrivateCredentials()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = new KingbaseDataSourceBuilder(Settings(listener).ConnectionString).Build();
        var peer = Task.Run(async () =>
        {
            using (var failed = await listener.AcceptTcpClientAsync(guard.Token))
            {
                using var stream = failed.GetStream(); await ReadStartupAsync(stream, guard.Token);
                await stream.WriteAsync(Frame('E', Encoding.UTF8.GetBytes("SFATAL\0C28P01\0Mprivate-failure\0\0")), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var retry = client.GetStream(); await ReadStartupAsync(retry, guard.Token);
            await retry.WriteAsync(Frame('R', Int32(3)), guard.Token);
            var header = new byte[5]; await retry.ReadExactlyAsync(header, guard.Token); Assert.Equal((byte)'p', header[0]);
            var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
            var password = new byte[length - 4]; await retry.ReadExactlyAsync(password, guard.Token);
            Assert.Equal("private-password\0", Encoding.UTF8.GetString(password));
            await Initialize(retry, guard.Token, readStartup: false);
            Assert.Equal(0, await retry.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await Assert.ThrowsAsync<KingbaseException>(() => source.OpenConnectionAsync(guard.Token).AsTask());
        using var connection = await source.OpenConnectionAsync(guard.Token);
        Assert.DoesNotContain("private-password", source.ConnectionString);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
