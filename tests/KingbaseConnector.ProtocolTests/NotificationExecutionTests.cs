using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.NotificationTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class NotificationExecutionTests
{
    sealed class CallbackFailure : Exception { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbacksRunWhileCommandOwnsTheConnectionAndFailuresDrainToReady(bool throwing)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        connection.Notification += (sender, args) =>
        {
            Assert.Same(connection, sender); Assert.Equal(42, args.ProcessId); Assert.Equal("通知😀", args.Payload); calls++;
            using var nested = new KingbaseCommand("SELECT 99", connection); Assert.Throws<InvalidOperationException>(() => nested.ExecuteScalar());
            Assert.Throws<InvalidOperationException>(() => connection.Wait(0)); Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
            Assert.DoesNotContain(args.Payload, args.ToString()); notified.SetResult(); if (throwing) throw new CallbackFailure();
        };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Notify(42, "ch", "通知😀"), guard.Token);
            await notified.Task.WaitAsync(guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("43"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection);
        if (throwing) await Assert.ThrowsAsync<CallbackFailure>(() => command.ExecuteScalarAsync(guard.Token)); else Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        Assert.Equal(1, calls); Assert.Equal(ConnectionState.Open, connection.State); command.CommandText = "SELECT 43"; Assert.Equal(43, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task IdleTimeoutCancellationAndPollResumeAnIncompleteNotificationThenReuseTheSocket(int split)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var remainder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        connection.Notification += (_, args) => { Assert.Equal("payload", args.Payload); calls++; };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            var frame = Notify(42, "ch", "payload"); await stream.WriteAsync(frame.AsMemory(0, split), guard.Token); partial.SetResult();
            await remainder.Task.WaitAsync(guard.Token); await stream.WriteAsync(frame.AsMemory(split), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); await partial.Task.WaitAsync(guard.Token);
        Assert.False(await connection.WaitAsync(20, guard.Token)); Assert.False(connection.Wait(TimeSpan.Zero)); Assert.Equal(ConnectionState.Open, connection.State);
        using var cancel = new CancellationTokenSource(20); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.WaitAsync(cancel.Token)); Assert.Equal(cancel.Token, error.CancellationToken);
        remainder.SetResult(); Assert.True(await connection.WaitAsync(TimeSpan.FromSeconds(2), guard.Token)); Assert.Equal(1, calls);
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token)); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseAbortsWaitOwnershipAndUnexpectedEofBreaksTheConnection(bool close)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); waiting.SetResult();
            await release.Task.WaitAsync(guard.Token); if (close) Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); await waiting.Task.WaitAsync(guard.Token); var operation = connection.WaitAsync(-1, guard.Token);
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
        if (close) { connection.Close(); release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(ConnectionState.Closed, connection.State); }
        else { release.SetResult(); await Assert.ThrowsAnyAsync<IOException>(() => operation); Assert.Equal(ConnectionState.Broken, connection.State); }
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task HandlerFailureAfterBatchAcknowledgmentPreservesSucceededStatuses()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Notify(42, "ch", ""), Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("UPDATE 1\0")), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        connection.Notification += (_, _) => throw new CallbackFailure(); await connection.OpenAsync(guard.Token);
        using var batch = new KingbaseBatch(connection); batch.BatchCommands.Add(new("UPDATE public.items SET v=1"));
        await Assert.ThrowsAsync<CallbackFailure>(() => batch.ExecuteNonQueryAsync(guard.Token)); Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(KingbaseBatchCommandStatus.Succeeded, batch.BatchCommands[0].ExecutionStatus); Assert.Null(batch.FailedCommandIndex); Assert.Equal(ConnectionState.Open, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailureDuringBeginRollsBackBeforeReturningAnUnusableTransactionHandle(bool cleanupFails)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var calls = 0; connection.Notification += (_, _) => { if (++calls == 1) throw new CallbackFailure(); };
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Notify(42, "ch", ""), TransactionExecutionTests.Result("BEGIN", 'T')), guard.Token);
            if (cleanupFails)
            {
                await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C42501\0Mrollback-denied\0\0")), Frame('Z', [(byte)'E'])), guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token)); return;
            }
            foreach (var result in new[] { ("ROLLBACK", 'I'), ("BEGIN", 'T'), ("ROLLBACK", 'I') })
            { var request = await ReadExtended(stream, guard.Token); Assert.Contains(result.Item1, Encoding.UTF8.GetString(request[0])); await stream.WriteAsync(TransactionExecutionTests.Result(result.Item1, result.Item2), guard.Token); }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); await Assert.ThrowsAsync<CallbackFailure>(async () => await connection.BeginTransactionAsync(guard.Token));
        if (cleanupFails) { Assert.Equal(ConnectionState.Closed, connection.State); await peer.WaitAsync(guard.Token); return; }
        Assert.Equal(ConnectionState.Open, connection.State);
        using var next = await connection.BeginTransactionAsync(guard.Token); await next.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task DefiniteRollbackTakesPrecedenceOverAnUnrelatedNotificationHandlerFailure()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        connection.Notification += (_, _) => throw new CallbackFailure();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(TransactionExecutionTests.Result("BEGIN", 'T'), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Notify(42, "ch", ""), TransactionExecutionTests.Result("ROLLBACK", 'I')), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var transaction = await connection.BeginTransactionAsync(guard.Token);
        var error = await Assert.ThrowsAsync<KingbaseTransactionException>(() => transaction.CommitAsync(guard.Token)); Assert.False(error.OutcomeUnknown); Assert.Null(transaction.Connection);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task HandlerFailureDuringFinalKeyInfoEnrichmentCannotBeClearedByReaderPublication()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        connection.Notification += (_, _) => throw new CallbackFailure();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token); await KeyInfoTests.Preflight(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), KeyInfoTests.Origin(), Row(["7"]), Complete(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Notify(42, "ch", ""), KeyInfoTests.Metadata(KeyInfoTests.Facts())), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT id AS alias FROM public.items", connection);
        var error = await Record.ExceptionAsync(async () => { using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, guard.Token); }); Assert.IsType<CallbackFailure>(error);
        command.CommandText = "SELECT 42"; Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token)); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task InvalidResultSchemaTakesPrecedenceOverAnEarlierCallbackFailure()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        connection.Notification += (_, _) => throw new CallbackFailure();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Notify(42, "ch", ""), Frame('1'), Frame('2'), Description(1, 1234567), Row(["unmapped"]), Complete(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token); Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 42", connection);
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteScalarAsync(guard.Token)); Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
