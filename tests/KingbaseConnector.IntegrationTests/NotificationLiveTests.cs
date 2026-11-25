namespace KingbaseConnector.IntegrationTests;

public sealed class NotificationLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task NotificationsRespectCommitRollbackAndCallbackOwnership(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var listener = new KingbaseConnection(settings.ConnectionString); using var sender = new KingbaseConnection(settings.ConnectionString);
        await listener.OpenAsync(token); await sender.OpenAsync(token); var channel = "kb_live_notify_" + Guid.NewGuid().ToString("N"); var payloads = new List<string>();
        listener.Notification += (_, args) =>
        {
            payloads.Add(args.Payload); using var nested = new KingbaseCommand("SELECT 99", listener);
            Assert.Throws<InvalidOperationException>(() => nested.ExecuteScalar()); Assert.Throws<InvalidOperationException>(() => listener.Wait(0));
        };
        using (var listen = new KingbaseCommand("LISTEN \"" + channel + "\"", listener)) await listen.ExecuteNonQueryAsync(token);
        using var send = SendCommand(sender, channel, "rollback");
        using (var transaction = await sender.BeginTransactionAsync(token))
        { send.Transaction = transaction; await send.ExecuteScalarAsync(token); Assert.False(await listener.WaitAsync(20, token)); await transaction.RollbackAsync(token); }
        Assert.False(await listener.WaitAsync(20, token)); Assert.Empty(payloads);
        using (var transaction = await sender.BeginTransactionAsync(token))
        { send.Transaction = transaction; send.Parameters[1].Value = "commit"; await send.ExecuteScalarAsync(token); Assert.False(await listener.WaitAsync(20, token)); await transaction.CommitAsync(token); }
        Assert.Empty(payloads); Assert.True(await listener.WaitAsync(5000, token)); Assert.Equal(new[] { "commit" }, payloads);
        using var local = await listener.BeginTransactionAsync(token); await Assert.ThrowsAsync<InvalidOperationException>(() => listener.WaitAsync(0, token)); await local.RollbackAsync(token);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PooledBorrowersDiscardQueuedNotificationsAndOldSubscriptions(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1; using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var sender = new KingbaseConnection(new KingbaseConnectionStringBuilder(settings.ConnectionString) { Pooling = false }.ConnectionString); await sender.OpenAsync(token);
        var oldChannel = "kb_old_" + Guid.NewGuid().ToString("N"); var newChannel = "kb_new_" + Guid.NewGuid().ToString("N"); var oldEvents = 0; int pid;
        using (var first = await source.OpenConnectionAsync(token))
        {
            first.Notification += (_, _) => oldEvents++; pid = await Pid(first, token);
            using var listen = new KingbaseCommand("LISTEN \"" + oldChannel + "\"", first); await listen.ExecuteNonQueryAsync(token); Assert.False(await first.WaitAsync(10, token));
            using var send = SendCommand(sender, oldChannel, "queued-old"); await send.ExecuteScalarAsync(token); await first.CloseAsync();
        }
        using var second = await source.OpenConnectionAsync(token); var received = new List<string>(); second.Notification += (_, args) => received.Add(args.Payload);
        Assert.Equal(pid, await Pid(second, token)); Assert.Empty(received); Assert.Equal(0, oldEvents);
        using (var oldSend = SendCommand(sender, oldChannel, "after-unlisten")) await oldSend.ExecuteScalarAsync(token);
        Assert.False(await second.WaitAsync(20, token)); Assert.Empty(received);
        using (var listen = new KingbaseCommand("LISTEN \"" + newChannel + "\"", second)) await listen.ExecuteNonQueryAsync(token);
        using (var newSend = SendCommand(sender, newChannel, "new")) await newSend.ExecuteScalarAsync(token);
        Assert.True(await second.WaitAsync(5000, token)); Assert.Equal(new[] { "new" }, received); Assert.Equal(0, oldEvents);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task WaitCancellationAndEmptyPayloadKeepTheSameListeningSession(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var listener = new KingbaseConnection(settings.ConnectionString); using var sender = new KingbaseConnection(settings.ConnectionString);
        await listener.OpenAsync(token); await sender.OpenAsync(token); var channel = "kb_live_notify_" + Guid.NewGuid().ToString("N"); string? payload = null;
        listener.Notification += (_, args) => payload = args.Payload;
        using (var listen = new KingbaseCommand("LISTEN \"" + channel + "\"", listener)) await listen.ExecuteNonQueryAsync(token);
        var pid = await Pid(listener, token); Assert.False(listener.Wait(0));
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
        { cancel.CancelAfter(20); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.WaitAsync(cancel.Token)); Assert.Equal(cancel.Token, error.CancellationToken); }
        Assert.Equal(pid, await Pid(listener, token));
        using (var send = new KingbaseCommand("NOTIFY \"" + channel + "\"", sender)) await send.ExecuteNonQueryAsync(token);
        Assert.True(listener.Wait(TimeSpan.FromSeconds(2))); Assert.Equal("", payload); Assert.Equal(pid, await Pid(listener, token));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task WaitHandlerFailuresPreserveTheConnectionAfterTheCompleteMessage(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var listener = new KingbaseConnection(settings.ConnectionString); using var sender = new KingbaseConnection(settings.ConnectionString);
        await listener.OpenAsync(token); await sender.OpenAsync(token); var channel = "kb_live_notify_" + Guid.NewGuid().ToString("N"); var failure = new InvalidOperationException("callback");
        listener.Notification += (_, _) => throw failure;
        using (var listen = new KingbaseCommand("LISTEN \"" + channel + "\"", listener)) await listen.ExecuteNonQueryAsync(token);
        using (var send = SendCommand(sender, channel, "fault")) await send.ExecuteScalarAsync(token);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => listener.WaitAsync(5000, token)));
        using var command = new KingbaseCommand("SELECT 42", listener); Assert.Equal(42, await command.ExecuteScalarAsync(token)); Assert.False(await listener.WaitAsync(10, token));
    });

    static KingbaseCommand SendCommand(KingbaseConnection sender, string channel, string payload)
    {
        var command = new KingbaseCommand("SELECT pg_catalog.pg_notify(:channel,:payload) IS NULL", sender);
        command.Parameters.AddWithValue("channel", channel); command.Parameters.AddWithValue("payload", payload); return command;
    }
    static async Task<int> Pid(KingbaseConnection connection, CancellationToken token)
    { using var command = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection); return (int)(await command.ExecuteScalarAsync(token))!; }

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task NotificationWaitPreservesSenderPayloadTimeoutAndReuse(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var listener = new KingbaseConnection(settings.ConnectionString); using var sender = new KingbaseConnection(settings.ConnectionString);
        await listener.OpenAsync(token); await sender.OpenAsync(token); var channel = "kb_live_notify_" + Guid.NewGuid().ToString("N");
        var received = new List<(int Pid, string Channel, string Payload)>();
        listener.Notification += (_, args) => received.Add((args.ProcessId, args.Channel, args.Payload));
        using (var listen = new KingbaseCommand("LISTEN \"" + channel + "\"", listener)) await listen.ExecuteNonQueryAsync(token);
        using var pid = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", sender); var expectedPid = (int)(await pid.ExecuteScalarAsync(token))!;
        using var send = new KingbaseCommand("SELECT pg_catalog.pg_notify(:channel,:payload) IS NULL", sender);
        send.Parameters.AddWithValue("channel", channel); send.Parameters.AddWithValue("payload", "通知😀 quote ' slash \\ "); await send.ExecuteScalarAsync(token);
        Assert.True(await listener.WaitAsync(5000, token)); Assert.Single(received);
        Assert.Equal((expectedPid, channel, "通知😀 quote ' slash \\ "), received[0]);
        Assert.False(await listener.WaitAsync(20, token));
        using var reuse = new KingbaseCommand("SELECT 42", listener); Assert.Equal(42, await reuse.ExecuteScalarAsync(token));
    });
}
