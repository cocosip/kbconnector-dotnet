using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.TransactionExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class TransactionModeTests
{
    public static IEnumerable<object[]> IsolationRequests()
    {
        foreach (var sync in new[] { true, false })
        {
            yield return [IsolationLevel.Unspecified, "BEGIN ISOLATION LEVEL READ COMMITTED", IsolationLevel.ReadCommitted, sync];
            yield return [IsolationLevel.ReadCommitted, "BEGIN ISOLATION LEVEL READ COMMITTED", IsolationLevel.ReadCommitted, sync];
            yield return [IsolationLevel.RepeatableRead, "BEGIN ISOLATION LEVEL REPEATABLE READ", IsolationLevel.RepeatableRead, sync];
            yield return [IsolationLevel.Serializable, "BEGIN ISOLATION LEVEL SERIALIZABLE", IsolationLevel.Serializable, sync];
        }
    }
    static async Task Expect(NetworkStream stream, string sql, byte[] response, CancellationToken token)
    {
        var frames = await ReadExtended(stream, token);
        Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes(sql + "\0"), [0, 0])), frames[0]);
        await stream.WriteAsync(response, token);
    }
    static byte[] Error(string code, char status) => Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C" + code + "\0Mprivate-error\0\0")),
        Frame('Z', [(byte)status]));

    [Fact]
    public async Task DefaultLiteralKeepsTheExistingTokenOverloadWithoutForcingReadWrite()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, default);
        Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [MemberData(nameof(IsolationRequests))]
    public async Task BclBeginPreservesTheRequestedIsolationWithoutSubstitution(IsolationLevel requested, string sql, IsolationLevel expected, bool sync)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using DbConnection connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            await Expect(stream, sql, Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = sync ? connection.BeginTransaction(requested) : await connection.BeginTransactionAsync(requested, guard.Token);
        Assert.Equal(expected, transaction.IsolationLevel); Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync(guard.Token);
        Assert.Equal(expected, transaction.IsolationLevel); Assert.Null(transaction.Connection);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    public static IEnumerable<object[]> AccessModeRequests()
    {
        foreach (var isolation in IsolationRequests())
        {
            yield return [isolation[0], isolation[1], isolation[2], isolation[3], null!];
            yield return [isolation[0], isolation[1] + ", READ ONLY", isolation[2], isolation[3], true];
            yield return [isolation[0], isolation[1] + ", READ WRITE", isolation[2], isolation[3], false];
        }
    }

    [Theory]
    [MemberData(nameof(AccessModeRequests))]
    public async Task TypedBeginAppliesAccessModeOnlyToTheCurrentTransaction(IsolationLevel requested, string sql, IsolationLevel expected, bool sync, bool? readOnly)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            await Expect(stream, sql, Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "SAVEPOINT \"s\"", Result("SAVEPOINT", 'T'), guard.Token);
            await Expect(stream, "COMMIT", Result("COMMIT", 'I'), guard.Token);
            // Default API must neither retain the last isolation nor force READ WRITE.
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = sync
            ? readOnly.HasValue ? connection.BeginTransaction(requested, readOnly.Value) : connection.BeginTransaction(requested)
            : readOnly.HasValue ? await connection.BeginTransactionAsync(requested, readOnly.Value, guard.Token) : await connection.BeginTransactionAsync(requested, guard.Token);
        Assert.Equal(expected, transaction.IsolationLevel); Assert.Same(connection, transaction.Connection);
        await transaction.SaveAsync("s", guard.Token); await transaction.CommitAsync(guard.Token);
        using var next = await connection.BeginTransactionAsync(guard.Token);
        Assert.Equal(IsolationLevel.ReadCommitted, next.IsolationLevel); Assert.Equal(expected, transaction.IsolationLevel);
        transaction.Dispose(); Assert.Same(connection, next.Connection);
        await next.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadUncommitted)]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.Chaos)]
    [InlineData((IsolationLevel)123)]
    public async Task ExplicitAccessModeDoesNotBypassLocalIsolationValidation(IsolationLevel requested)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await SavepointExecutionTests.Limit(stream, guard.Token); // No earlier metadata or BEGIN may be sent.
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        foreach (var readOnly in new[] { true, false })
        {
            if (requested == (IsolationLevel)123)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => connection.BeginTransaction(requested, readOnly));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await connection.BeginTransactionAsync(requested, readOnly, guard.Token));
            }
            else
            {
                Assert.Throws<NotSupportedException>(() => connection.BeginTransaction(requested, readOnly));
                await Assert.ThrowsAsync<NotSupportedException>(async () => await connection.BeginTransactionAsync(requested, readOnly, guard.Token));
            }
        }
        using var transaction = await connection.BeginTransactionAsync(guard.Token);
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ReadOnlyWriteRejectionKeepsOwnerUntilExplicitRollbackWithoutAClientSqlFilter()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL SERIALIZABLE, READ ONLY", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "UPDATE t SET x=1", Error("25006", 'E'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL READ COMMITTED, READ WRITE", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "UPDATE t SET x=1", Result("UPDATE 1", 'T'), guard.Token);
            await Expect(stream, "COMMIT", Result("COMMIT", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, readOnly: true, guard.Token);
        using var command = new KingbaseCommand("UPDATE t SET x=1", connection) { Transaction = transaction };
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteNonQueryAsync(guard.Token));
        Assert.Equal("25006", error.SqlState); Assert.DoesNotContain("private", error.ToString());
        Assert.Same(connection, transaction.Connection); Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);
        await transaction.RollbackAsync(guard.Token);
        using var writable = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, readOnly: false, guard.Token);
        command.Transaction = writable;
        Assert.Equal(1, await command.ExecuteNonQueryAsync(guard.Token));
        await writable.CommitAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task RejectedIsolationRequestDoesNotFallBackAndCanBeExplicitlyRetried()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL SERIALIZABLE, READ WRITE", Error("0A000", 'I'), guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL REPEATABLE READ, READ ONLY", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        Assert.Equal("0A000", (await Assert.ThrowsAsync<KingbaseException>(async () =>
            await connection.BeginTransactionAsync(IsolationLevel.Serializable, false, guard.Token))).SqlState);
        Assert.Equal(ConnectionState.Open, connection.State);
        using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, true, guard.Token);
        Assert.Equal(IsolationLevel.RepeatableRead, transaction.IsolationLevel);
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task ExplicitReadModeUsesExistingCancellationAndReaderOwnershipGate()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token);
            await Expect(stream, "SELECT 1", Value("1"), guard.Token);
            await SavepointExecutionTests.Limit(stream, guard.Token);
            await Expect(stream, "BEGIN ISOLATION LEVEL REPEATABLE READ, READ ONLY", Result("BEGIN", 'T'), guard.Token);
            await Expect(stream, "ROLLBACK", Result("ROLLBACK", 'I'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connection.BeginTransactionAsync(IsolationLevel.Serializable, true, canceled.Token));
        using var command = new KingbaseCommand("SELECT 1", connection);
        using var reader = await command.ExecuteReaderAsync(guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connection.BeginTransactionAsync(IsolationLevel.Serializable, false, guard.Token));
        reader.Close();
        using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, true, guard.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connection.BeginTransactionAsync(IsolationLevel.Serializable, false, guard.Token));
        await transaction.RollbackAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("eof")]
    [InlineData("bad-ack")]
    [InlineData("failed-rejection")]
    public async Task InterruptedOrUnusableBeginIsRetiredWithoutPublishingAnOwnerOrRetrying(string failure)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var settings = Settings(listener); settings.CommandTimeout = failure == "timeout" ? 1 : 0;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream(); await Initialize(stream, guard.Token); await SavepointExecutionTests.Limit(stream, guard.Token);
            var frames = await ReadExtended(stream, guard.Token);
            Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes("BEGIN ISOLATION LEVEL SERIALIZABLE, READ ONLY\0"), [0, 0])), frames[0]);
            received.SetResult();
            if (failure == "eof") return;
            if (failure == "bad-ack") await stream.WriteAsync(Result("BEGIN", 'I'), guard.Token);
            if (failure == "failed-rejection") await stream.WriteAsync(Error("40001", 'E'), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        var execution = connection.BeginTransactionAsync(IsolationLevel.Serializable, true, caller.Token).AsTask();
        await received.Task.WaitAsync(guard.Token);
        if (failure == "cancel")
        {
            caller.Cancel();
            Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution)).CancellationToken);
        }
        else if (failure == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => execution);
        else if (failure == "failed-rejection") Assert.Equal("40001", (await Assert.ThrowsAsync<KingbaseException>(() => execution)).SqlState);
        else await Assert.ThrowsAnyAsync<IOException>(() => execution);
        Assert.Equal(ConnectionState.Broken, connection.State);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.BeginTransactionAsync(guard.Token));
        await peer.WaitAsync(guard.Token); Assert.False(listener.Pending());
    }
}
