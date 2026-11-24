using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgPhysicalSessionTests
{
    const string Version = "KingbaseES V009R001C001B0001";

    [Theory]
    [InlineData(false, true, 15)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, int.MaxValue)]
    public async Task ComposesInitializationAndOwnsTheReadySession(bool sync, bool mode, int timeout)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var creation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var peer = ServeSuccessAsync(listener, guard.Token, mode, false);
        var settings = Settings(listener); settings.Timeout = timeout;
        using var session = sync ? await Task.Run(() => PgPhysicalSession.Open(settings.ConnectionString), guard.Token)
            : await PgPhysicalSession.OpenAsync(settings.ConnectionString, creation.Token);
        Assert.Equal(9, session.Identity.Version.Major);
        Assert.Equal("14.0-kernel", session.Identity.StartupVersion);
        Assert.Equal(mode ? PgModeSource.ParameterStatus : PgModeSource.Show, session.Identity.ReportedModeSource);
        Assert.Equal((uint)6002, session.Catalog.ByName["sys_catalog.int4"].Oid);
        Assert.Equal(1, session.Startup.BackendKey.ProcessId);
        creation.Cancel();
        var results = await session.Queries.ExecuteAsync("SELECT application-text", guard.Token);
        Assert.Equal("ready", results[0].Rows[0][0]);
        session.Dispose(); session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Queries);
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task SendsOptionalSettingsAsLiteralStartupParameters()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener);
        settings.ApplicationName = "应用'; SELECT password";
        settings.SearchPath = "\"Odd Schema\", public";
        settings.Timezone = "Asia/Shanghai";
        var peer = ServeSuccessAsync(listener, guard.Token, true, true);
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token);
        await session.Queries.ExecuteAsync("SELECT application-text", guard.Token);
        session.Dispose();
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("ssl")]
    [InlineData("startup")]
    [InlineData("version")]
    [InlineData("mode")]
    [InlineData("catalog")]
    public async Task CallerCancellationAtEveryPhaseClosesSocketAndPreservesOriginalToken(string phase)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = ServeBlockedAsync(listener, phase, blocked, guard.Token);
        var settings = Settings(listener); settings.Timeout = 0;
        if (phase == "ssl") settings.SslMode = KingbaseSslMode.Require;
        var opening = PgPhysicalSession.OpenAsync(settings.ConnectionString, caller.Token);
        await blocked.Task.WaitAsync(guard.Token);
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(caller.Token, error.CancellationToken);
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("ssl")]
    [InlineData("startup")]
    [InlineData("version")]
    [InlineData("mode")]
    [InlineData("catalog")]
    public async Task DeadlineAtEveryPhaseIsATimeoutAndClosesSocket(string phase)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = ServeBlockedAsync(listener, phase, blocked, guard.Token);
        var settings = Settings(listener); settings.Timeout = 1;
        if (phase == "ssl") settings.SslMode = KingbaseSslMode.Require;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token));
        Assert.DoesNotContain("private-password", error.ToString());
        Assert.True(blocked.Task.IsCompleted);
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task OneDeadlineIncludesTimeSpentInEarlierPhases()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream();
            await PgTestPeer.ReadStartupAsync(stream, guard.Token);
            await Task.Delay(700, guard.Token);
            await stream.WriteAsync(PgTestPeer.StartupResponse(), guard.Token);
            Assert.Equal("SELECT sys_catalog.version();", await PgTestPeer.ReadQueryAsync(stream, guard.Token));
            // A fresh one-second query budget would receive a wrong product response at 1.2s.
            var closed = stream.ReadAsync(new byte[1], guard.Token).AsTask();
            if (await Task.WhenAny(closed, Task.Delay(500, guard.Token)) != closed)
                await stream.WriteAsync(PgTestPeer.Scalar("not-Kingbase"), guard.Token);
            Assert.Equal(0, await closed);
        }, guard.Token);
        var settings = Settings(listener); settings.Timeout = 1;
        await Assert.ThrowsAsync<TimeoutException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token));
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("version")]
    [InlineData("catalog")]
    public async Task InitializationFailuresRetireTheSocketWithoutFallbackOrReplay(string phase)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream();
            await PgTestPeer.ReadStartupAsync(stream, guard.Token);
            if (phase == "startup") await stream.WriteAsync(PgTestPeer.StartupError(), guard.Token);
            else
            {
                await stream.WriteAsync(PgTestPeer.StartupResponse(), guard.Token);
                Assert.Equal("SELECT sys_catalog.version();", await PgTestPeer.ReadQueryAsync(stream, guard.Token));
                await stream.WriteAsync(PgTestPeer.Scalar(phase == "version" ? "PostgreSQL 14" : Version), guard.Token);
                if (phase == "catalog")
                {
                    Assert.Contains("FROM sys_catalog.sys_type", await PgTestPeer.ReadQueryAsync(stream, guard.Token));
                    await stream.WriteAsync(PgTestPeer.Scalar("invalid-catalog"), guard.Token);
                }
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => PgPhysicalSession.OpenAsync(Settings(listener).ConnectionString, guard.Token));
        if (phase == "startup") Assert.IsType<PgStartupException>(error);
        else if (phase == "version") Assert.IsType<NotSupportedException>(error);
        else Assert.IsType<IOException>(error);
        Assert.DoesNotContain("private-", error.ToString());
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public async Task AuthenticationInputSizesAreValidatedBeforeConnecting(int allowance)
    {
        using var listener = Listen();
        var settings = Settings(listener);
        listener.Stop();
        settings.Username = new string('u', 100);
        settings.Password = new string('p', PgWire.MaxMessageLength - allowance);
        await Assert.ThrowsAsync<ArgumentException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString));
    }

    [Fact]
    public async Task AggregateStartupSizeIsValidatedBeforeConnecting()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = Settings(listener);
        var field = new string('x', PgWire.MaxMessageLength / 2);
        settings.ApplicationName = field;
        settings.SearchPath = field;
        await Assert.ThrowsAsync<ArgumentException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString, guard.Token));
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task PreCancellationAndUnsupportedOrInvalidSettingsDoNotOpenSocket()
    {
        using var listener = Listen();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var settings = Settings(listener);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString, cancelled.Token));
        settings.WireProtocol = KingbaseWireProtocol.MySql;
        await Assert.ThrowsAsync<NotSupportedException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString));
        settings.WireProtocol = KingbaseWireProtocol.PostgreSql;
        settings.SslMode = KingbaseSslMode.VerifyFull; settings.RootCertificate = "private-certificate";
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString));
        Assert.DoesNotContain("private-", error.ToString());
        await Assert.ThrowsAsync<ArgumentException>(() => PgPhysicalSession.OpenAsync("Host=127.0.0.1"));
        settings.RootCertificate = ""; settings.ApplicationName = new string((char)0xd800, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => PgPhysicalSession.OpenAsync(settings.ConnectionString));
        Assert.False(listener.Pending());
    }

    static async Task ServeSuccessAsync(TcpListener listener, CancellationToken token, bool mode, bool optional)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        var startup = Encoding.UTF8.GetString(await PgTestPeer.ReadStartupAsync(stream, token));
        Assert.Equal("user\0user\0database\0db\0client_encoding\0UTF8\0" + (optional
            ? "application_name\0应用'; SELECT password\0search_path\0\"Odd Schema\", public\0TimeZone\0Asia/Shanghai\0\0" : "\0"), startup);
        await stream.WriteAsync(PgTestPeer.StartupResponse(mode), token);
        Assert.Equal("SELECT sys_catalog.version();", await PgTestPeer.ReadQueryAsync(stream, token));
        await stream.WriteAsync(PgTestPeer.Scalar(Version), token);
        if (!mode)
        {
            Assert.Equal("SHOW database_mode", await PgTestPeer.ReadQueryAsync(stream, token));
            await stream.WriteAsync(PgTestPeer.Scalar("pg"), token);
        }
        Assert.Contains("FROM sys_catalog.sys_type", await PgTestPeer.ReadQueryAsync(stream, token));
        await stream.WriteAsync(PgTestPeer.Catalog(), token);
        Assert.Equal("SELECT application-text", await PgTestPeer.ReadQueryAsync(stream, token));
        await stream.WriteAsync(PgTestPeer.Scalar("ready"), token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
    }

    static async Task ServeBlockedAsync(TcpListener listener, string phase, TaskCompletionSource blocked, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        if (phase == "ssl")
        {
            var request = new byte[8];
            await stream.ReadExactlyAsync(request, token);
            Assert.Equal(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 }, request);
            blocked.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
            return;
        }
        await PgTestPeer.ReadStartupAsync(stream, token);
        if (phase != "startup")
        {
            await stream.WriteAsync(PgTestPeer.StartupResponse(mode: phase != "mode"), token);
            Assert.Equal("SELECT sys_catalog.version();", await PgTestPeer.ReadQueryAsync(stream, token));
            if (phase != "version")
            {
                await stream.WriteAsync(PgTestPeer.Scalar(Version), token);
                var sql = await PgTestPeer.ReadQueryAsync(stream, token);
                if (phase == "mode") Assert.Equal("SHOW database_mode", sql);
                else Assert.Contains("FROM sys_catalog.sys_type", sql);
            }
        }
        blocked.SetResult();
        Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
    }
    static KingbaseConnectionStringBuilder Settings(TcpListener listener) => new()
    {
        Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "user", Database = "db",
        Password = "private-password", SslMode = KingbaseSslMode.Disable
    };
    static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener;
    }
}
