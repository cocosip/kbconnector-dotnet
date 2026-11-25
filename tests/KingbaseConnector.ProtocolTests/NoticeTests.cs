using System.Data;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class NoticeTests
{
    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactAssetPublicNoticeProbe(KingbaseCompatibilityMode mode) => KingbaseConnector.Tests.NotificationAssetProbe.RunAsync(mode, notices: true);

    [Fact]
    public void PublicStructuredNoticeEventExists()
    {
        var notice = typeof(KingbaseConnection).GetEvent("Notice"); Assert.NotNull(notice);
        var args = notice.EventHandlerType!.GenericTypeArguments.Single();
        var diagnostic = args.GetProperty("Notice"); Assert.NotNull(diagnostic);
        foreach (var name in new[] { "SqlState", "Severity", "InvariantSeverity", "MessageText", "Detail", "Hint", "Fields" })
            Assert.NotNull(diagnostic.PropertyType.GetProperty(name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandNoticesPreserveFieldsGateReentryDrainAndPermitReuse(bool throwing)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var callback = new InvalidOperationException("notice handler"); var calls = 0; KingbaseNotice? captured = null;
        connection.Notice += (sender, args) =>
        {
            Assert.Same(connection, sender); captured = args.Notice; calls++;
            Assert.Equal("01000", captured.SqlState); Assert.Equal("警告", captured.Severity); Assert.Equal("WARNING", captured.InvariantSeverity);
            Assert.Equal("private-通知😀", captured.MessageText); Assert.Equal("private-detail", captured.Detail); Assert.Equal("private-hint", captured.Hint); Assert.Equal("extension", captured.Fields['z']);
            Assert.DoesNotContain("private", captured.ToString()); Assert.DoesNotContain("private", args.ToString());
            Assert.Throws<NotSupportedException>(() => ((IDictionary<char, string>)captured.Fields).Add('q', "changed"));
            using var nested = new KingbaseCommand("SELECT 99", connection); Assert.Throws<InvalidOperationException>(() => nested.ExecuteScalar());
            Assert.Throws<InvalidOperationException>(() => connection.Wait(0)); Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
            if (throwing) throw callback;
        };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Notice(), Value("42")), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("43"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection);
        if (throwing) Assert.Same(callback, await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(guard.Token)));
        else Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(1, calls); Assert.NotNull(captured); Assert.Equal(ConnectionState.Open, connection.State);
        command.CommandText = "SELECT 43"; Assert.Equal(43, await command.ExecuteScalarAsync(guard.Token)); Assert.Equal("private-detail", captured.Detail);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleNoticesDoNotCountAsNotificationsAndHandlerFailureStopsTheWait(bool throwing)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var callback = new InvalidOperationException("notice handler"); var calls = 0;
        connection.Notice += (_, _) => { calls++; if (throwing) throw callback; };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await stream.WriteAsync(Notice(), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        if (throwing) Assert.Same(callback, await Assert.ThrowsAsync<InvalidOperationException>(() => connection.WaitAsync(-1, guard.Token)));
        else Assert.False(await connection.WaitAsync(50, guard.Token));
        Assert.Equal(1, calls); Assert.Equal(ConnectionState.Open, connection.State);
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token)); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerErrorPrecedesCallbackFailureIncludingNoticesAfterErrorResponse(bool noticeAfterError)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString); var calls = 0;
        connection.Notice += (_, _) => { calls++; throw new InvalidOperationException("notice callback"); };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await ReadExtended(stream, guard.Token);
            var error = Frame('E', Encoding.ASCII.GetBytes("SERROR\0C22012\0Mprivate-error\0\0"));
            await stream.WriteAsync(Join(noticeAfterError ? error : Notice(), noticeAfterError ? Notice() : error, Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection);
        var failure = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(guard.Token)); Assert.Equal("22012", failure.SqlState); Assert.Equal(1, calls);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token)); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("SNOTICE\0C00000\0Mfirst\0Mduplicate\0\0")]
    [InlineData("SNOTICE\0Cbad\0Mmessage\0\0")]
    [InlineData("SNOTICE\0C00000\0Munterminated")]
    public async Task MalformedNoticesRetireTheSession(string payload)
    {
        using var session = NotificationTests.Session(new QueryStream(Join(Frame('N', Encoding.UTF8.GetBytes(payload)), Scalar("42", 23, 4))));
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("SELECT 42")); Assert.True(session.IsDisposed);
    }

    [Theory]
    [InlineData("SNOTICE\0C00000\0Mmessage\0\0", "NOTICE", null)]
    [InlineData("VWARNING\0C01000\0Mmessage\0\0", "WARNING", "WARNING")]
    public void OptionalAndLocalizedNoticeFieldsRemainDistinct(string payload, string severity, string? invariantSeverity)
    {
        var notice = new KingbaseNotice(PgBackendMessages.ParseError(Encoding.UTF8.GetBytes(payload)));
        Assert.Equal(severity, notice.Severity); Assert.Equal(invariantSeverity, notice.InvariantSeverity); Assert.Null(notice.Detail); Assert.Null(notice.Hint);
        Assert.DoesNotContain("message", notice.ToString()); Assert.Equal("message", notice.Fields['M']);
    }

    [Fact]
    public async Task MalformedNoticeUtf8BreaksTheSession()
    {
        using var session = NotificationTests.Session(new QueryStream(Frame('N', Join(Encoding.ASCII.GetBytes("SNOTICE\0C00000\0M"), [0xFF, 0, 0]))));
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("SELECT 42")); Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task ChangedSessionIdentityIsRejectedBeforeCallingNoticeHandlers()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString); var calls = 0;
        connection.Notice += (_, _) => calls++;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Status("database_mode", "mysql"), Notice(), Value("42")), guard.Token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); }
            catch (IOException error) when (error.InnerException is System.Net.Sockets.SocketException socket && socket.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted) { }
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection);
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteScalarAsync(guard.Token)); Assert.Equal(0, calls); Assert.Equal(ConnectionState.Broken, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheFirstCallbackFailureWinsAcrossNoticeAndNotification(bool noticeFirst)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var first = new InvalidOperationException("notice"); var second = new InvalidOperationException("notification"); var notices = 0; var notifications = 0;
        connection.Notice += (_, _) => { notices++; throw first; }; connection.Notification += (_, _) => { notifications++; throw second; };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await ReadExtended(stream, guard.Token);
            var notification = NotificationTests.Notify(42, "ch", "payload"); await stream.WriteAsync(Join(noticeFirst ? Notice() : notification, noticeFirst ? notification : Notice(), Value("42")), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection);
        Assert.Same(noticeFirst ? first : second, await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(guard.Token)));
        Assert.Equal(1, notices); Assert.Equal(1, notifications); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SchemaRowValidationPrecedesCallbackFailureWithoutLosingOwnership(bool invalid, bool notice)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var callback = new InvalidOperationException("callback"); Action handler = () => throw callback;
        if (notice) connection.Notice += (_, _) => handler(); else connection.Notification += (_, _) => handler();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(notice ? Notice() : NotificationTests.Notify(42, "ch", ""), Frame('1'), Frame('2'), Description(1, 9004, -1), Row(["select"]), invalid ? Row(["SELECT"]) : [], Complete(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        if (invalid) await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync("ReservedWords", guard.Token));
        else Assert.Same(callback, await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetSchemaAsync("ReservedWords", guard.Token)));
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token)); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScalarConversionPrecedesCallbackFailuresForOrdinaryAndBatchExecution(bool batch, bool notice)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        if (notice) connection.Notice += (_, _) => throw new InvalidOperationException("notice"); else connection.Notification += (_, _) => throw new InvalidOperationException("notification");
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(notice ? Notice() : NotificationTests.Notify(42, "ch", ""), Frame('1'), Frame('2'), Description(1, 9006, -1), Row(["1234567890123456789012345678901234567890"]), Complete(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        if (batch) { using var commands = new KingbaseBatch(connection); commands.BatchCommands.Add(new("SELECT big_numeric")); await Assert.ThrowsAsync<OverflowException>(() => commands.ExecuteScalarAsync(guard.Token)); }
        else { using var command = new KingbaseCommand("SELECT big_numeric", connection); await Assert.ThrowsAsync<OverflowException>(() => command.ExecuteScalarAsync(guard.Token)); }
        using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(guard.Token)); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    internal static byte[] Notice() => Frame('N', Encoding.UTF8.GetBytes("S警告\0VWARNING\0C01000\0Mprivate-通知😀\0Dprivate-detail\0Hprivate-hint\0zextension\0\0"));
}
