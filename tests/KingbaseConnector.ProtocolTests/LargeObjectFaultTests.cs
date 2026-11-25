using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.LargeObjectExecutionTests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class LargeObjectFaultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveReaderAndCallbackReentryCannotDisposeOrPoisonALargeObjectStream(bool callback)
    {
        await Run(async (connection, transaction, token) =>
        {
            using var handle = await new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token);
            if (callback)
            {
                connection.Notice += (_, _) => { Assert.Throws<InvalidOperationException>(() => handle.Dispose()); Assert.Throws<InvalidOperationException>(() => _ = handle.Position); };
                Assert.Equal(7, handle.Position);
            }
            else
            {
                using var command = new KingbaseCommand("SELECT 42", connection) { Transaction = transaction }; using (var reader = await command.ExecuteReaderAsync(token))
                { Assert.Throws<InvalidOperationException>(() => handle.Dispose()); Assert.Throws<InvalidOperationException>(() => _ = handle.Position); Assert.True(reader.Read()); Assert.Equal(42, reader.GetInt32(0)); }
            }
            Assert.True(handle.CanRead); Assert.Equal(8, handle.Position);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token);
            if (callback) await Function(stream, "lo_tell64", ["1"], Reply("7", "int8", NoticeTests.Notice()), token);
            else { var command = await Request(stream, token); Assert.Equal("SELECT 42", command.Sql); await stream.WriteAsync(Reply("42", "int4"), token); }
            await Function(stream, "lo_tell64", ["1"], Reply("8", "int8"), token); await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedReadsRetireDescriptorsAndPreserveTheCallerOrDeadlineCause(bool timeout)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, transaction, token) =>
        {
            using var handle = await new KingbaseLargeObjectManager(connection).OpenReadAsync(123, token); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var reading = handle.ReadAsync(new byte[1], 0, 1, cancel.Token); await started.Task.WaitAsync(token);
            if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => reading);
            else { cancel.Cancel(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading); Assert.Equal(cancel.Token, error.CancellationToken); }
            Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(handle.CanRead); Assert.Null(transaction.Connection);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "262144"], Reply("1", "int4"), token); var read = await Request(stream, token); Assert.Contains("pg_catalog.loread(", read.Sql); started.SetResult();
        }, rollback: false, commandTimeout: timeout ? 1 : 0);
    }

    [Fact]
    public async Task OpenFailureCleanupIsBoundedIndependentlyOfUnlimitedApplicationCommands()
    {
        var failure = new InvalidOperationException("callback");
        await Run(async (connection, transaction, token) =>
        {
            connection.Notice += (_, _) => throw failure; Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => new KingbaseLargeObjectManager(connection).OpenReadAsync(123, token)));
            Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "262144"], Reply("1", "int4", NoticeTests.Notice()), token); var close = await Request(stream, token); Assert.Contains("pg_catalog.lo_close(", close.Sql);
        }, rollback: false, commandTimeout: 0, cleanupTimeout: 1);
    }

    [Theory]
    [InlineData("zero-write")]
    [InlineData("oversized-read")]
    [InlineData("negative-position")]
    [InlineData("close-status")]
    public async Task ImpossibleAcknowledgmentsRetireTheSessionRatherThanTrustingTheDescriptor(string kind)
    {
        await Run(async (connection, transaction, token) =>
        {
            var handle = await new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token);
            if (kind == "zero-write") await Assert.ThrowsAsync<IOException>(() => handle.WriteAsync(new byte[1], 0, 1, token));
            else if (kind == "oversized-read") await Assert.ThrowsAsync<IOException>(() => handle.ReadAsync(new byte[1], 0, 1, token));
            else if (kind == "negative-position") Assert.Throws<IOException>(() => _ = handle.Position);
            else await Assert.ThrowsAsync<IOException>(async () => await handle.DisposeAsync());
            Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection); Assert.False(handle.CanRead); handle.Dispose();
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token);
            var request = await Request(stream, token); var result = kind switch { "zero-write" => Reply("0", "int4"), "oversized-read" => Reply("\\x0102", "bytea"), "negative-position" => Reply("-1", "int8"), _ => Reply("1", "int4") };
            await stream.WriteAsync(result, token);
        }, rollback: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainRawSavepointControlsRetireForBothCommandAndBatch(bool batch)
    {
        await Run(async (connection, transaction, token) =>
        {
            using var handle = await new KingbaseLargeObjectManager(connection).OpenReadAsync(123, token);
            connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Broken) throw new InvalidOperationException("secondary state handler"); };
            if (batch) { using var commands = new KingbaseBatch(connection) { Transaction = transaction }; commands.BatchCommands.Add(new("SAVEPOINT raw")); var error = await Assert.ThrowsAsync<KingbaseException>(() => commands.ExecuteNonQueryAsync(token)); Assert.Equal("40003", error.SqlState); Assert.Same(commands.BatchCommands[0], error.BatchCommand); }
            else { using var command = new KingbaseCommand("SAVEPOINT raw", connection) { Transaction = transaction }; var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteNonQueryAsync(token)); Assert.Equal("40003", error.SqlState); }
            Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(handle.CanSeek);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "262144"], Reply("1", "int4"), token); var request = await Request(stream, token); Assert.Equal("SAVEPOINT raw", request.Sql);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Frame('n'), Frame('C', Encoding.ASCII.GetBytes("SAVEPOINT\0")), Error("40003")), token);
        }, rollback: false);
    }

    [Fact]
    public async Task CancellationOfANoIoOperationCannotRollBackItsTransaction()
    {
        await Run(async (connection, transaction, token) =>
        {
            using var handle = await new KingbaseLargeObjectManager(connection).OpenReadAsync(123, token); using var cancel = new CancellationTokenSource();
            var lease = (KingbaseLargeObjectHandle)typeof(KingbaseLargeObjectStream).GetField("_handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(handle)!;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.LargeObjectOperationAsync(lease, operation => { cancel.Cancel(); return Task.FromResult(0); }, cancel.Token));
            Assert.Equal(ConnectionState.Open, connection.State); Assert.Same(connection, transaction.Connection); Assert.Equal(7, handle.Position);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "262144"], Reply("1", "int4"), token); await Function(stream, "lo_tell64", ["1"], Reply("7", "int8"), token); await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    [Fact]
    public async Task DisposingInAFailedTransactionDefersClosingASurvivingDescriptorUntilRecovery()
    {
        await Run(async (connection, transaction, token) =>
        {
            var manager = new KingbaseLargeObjectManager(connection); var parent = await manager.OpenReadAsync(123, token); await transaction.SaveAsync("s", token);
            using (var error = new KingbaseCommand("SELECT failed", connection) { Transaction = transaction }) await Assert.ThrowsAsync<KingbaseException>(() => error.ExecuteScalarAsync(token));
            parent.Dispose(); Assert.False(parent.CanRead); await transaction.RollbackAsync("s", token);
            using var replacement = await manager.OpenReadAsync(124, token); parent.Dispose(); Assert.Equal(7, replacement.Position);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "262144"], Reply("1", "int4"), token); await Control(stream, "SAVEPOINT", token);
            await Request(stream, token); await stream.WriteAsync(Error("22012", 'E'), token); await Control(stream, "ROLLBACK", token);
            await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token); await Function(stream, "lo_open", ["124", "262144"], Reply("1", "int4"), token);
            await Function(stream, "lo_tell64", ["1"], Reply("7", "int8"), token); await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    [Fact]
    public async Task DeferredDescriptorCleanupUsesItsOwnDeadlineWithUnlimitedApplicationCommands()
    {
        await Run(async (connection, transaction, token) =>
        {
            var manager = new KingbaseLargeObjectManager(connection); var parent = await manager.OpenReadAsync(123, token); await transaction.SaveAsync("s", token);
            using (var error = new KingbaseCommand("SELECT failed", connection) { Transaction = transaction }) await Assert.ThrowsAsync<KingbaseException>(() => error.ExecuteScalarAsync(token));
            parent.Dispose(); await transaction.RollbackAsync("s", token);
            await Assert.ThrowsAsync<TimeoutException>(() => manager.OpenReadAsync(124, token)); Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(transaction.Connection);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "262144"], Reply("1", "int4"), token); await Control(stream, "SAVEPOINT", token);
            await Request(stream, token); await stream.WriteAsync(Error("22012", 'E'), token); await Control(stream, "ROLLBACK", token);
            var request = await Request(stream, token); Assert.Contains("pg_catalog.lo_close(", request.Sql);
        }, rollback: false, commandTimeout: 0, cleanupTimeout: 1);
    }
}
