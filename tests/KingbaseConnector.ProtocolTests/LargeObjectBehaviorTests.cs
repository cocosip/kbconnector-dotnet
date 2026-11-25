using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.LargeObjectExecutionTests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class LargeObjectBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOpenCallbackClosesTheUnpublishedDescriptorOrRetiresFailedCleanup(bool cleanupFails)
    {
        var failure = new InvalidOperationException("callback");
        await Run(async (connection, transaction, token) =>
        {
            var calls = 0; connection.Notice += (_, _) => { calls++; throw failure; };
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token))); Assert.Equal(1, calls);
            Assert.Equal(cleanupFails ? ConnectionState.Broken : ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("7", "int4", NoticeTests.Notice()), token);
            await Function(stream, "lo_close", ["7"], cleanupFails ? Error("42501", 'E') : Reply("0", "int4", NoticeTests.Notice()), token);
        }, rollback: !cleanupFails);
    }

    [Fact]
    public async Task FailedCreateCallbackDeletesItsUnpublishedObjectBeforeReturningTheFailure()
    {
        var failure = new InvalidOperationException("callback");
        await Run(async (connection, transaction, token) =>
        {
            var calls = 0; connection.Notice += (_, _) => { calls++; throw failure; }; var manager = new KingbaseLargeObjectManager(connection);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(cancellationToken: token))); Assert.Equal(1, calls); Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_create", ["0"], Reply("4294967295", "oid", NoticeTests.Notice()), token);
            await Function(stream, "lo_unlink", ["4294967295"], Reply("1", "int4", NoticeTests.Notice()), token);
        });
    }

    [Fact]
    public async Task LengthRestoresItsCursorBeforeRethrowingACallbackFailure()
    {
        await Run(async (connection, transaction, token) =>
        {
            using var handle = await new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token); var failure = new InvalidOperationException("callback");
            connection.Notice += (_, _) => { Assert.Throws<InvalidOperationException>(() => _ = handle.Position); Assert.Throws<InvalidOperationException>(() => handle.Dispose()); throw failure; };
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => _ = handle.Length)); Assert.Equal(5, handle.Position); Assert.True(handle.CanRead);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token);
            await Function(stream, "lo_tell64", ["1"], Reply("5", "int8"), token); await Function(stream, "lo_lseek64", ["1", "0", "2"], Reply("12", "int8", NoticeTests.Notice()), token);
            await Function(stream, "lo_lseek64", ["1", "5", "0"], Reply("5", "int8"), token); await Function(stream, "lo_tell64", ["1"], Reply("5", "int8"), token);
            await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesContinueOnlyAcknowledgedBytesInBoundedChunksAndKeepTheirEffectsAfterCallbackFailure(bool callbackFails)
    {
        var data = Enumerable.Range(0, 65539).Select(i => (byte)(i % 251)).ToArray(); var failure = new InvalidOperationException("callback");
        await Run(async (connection, transaction, token) =>
        {
            using var handle = await new KingbaseLargeObjectManager(connection).OpenReadWriteAsync(123, token); if (callbackFails) connection.Notification += (_, _) => throw failure;
            if (callbackFails) Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => handle.WriteAsync(data, 0, data.Length, token)));
            else await handle.WriteAsync(data, 0, data.Length, token);
            Assert.Equal(data.Length, handle.Position); var output = new byte[70000]; Assert.Equal(3, await handle.ReadAsync(output, 2, output.Length - 2, token)); Assert.Equal(new byte[] { 7, 8, 9 }, output.Skip(2).Take(3));
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token);
            await Function(stream, "lowrite", ["1", "\\x" + Convert.ToHexString(data.AsSpan(0, 65536)).ToLowerInvariant()], Reply("60000", "int4", callbackFails ? NotificationTests.Notify(42, "lob", "") : null), token);
            await Function(stream, "lowrite", ["1", "\\x" + Convert.ToHexString(data.AsSpan(60000)).ToLowerInvariant()], Reply("5539", "int4"), token);
            await Function(stream, "lo_tell64", ["1"], Reply(data.Length.ToString(), "int8"), token); await Function(stream, "loread", ["1", "65536"], Reply("\\x070809", "bytea"), token);
            await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    [Fact]
    public async Task LocalArgumentAndPreCancellationFailuresLeaveTheDescriptorUsableWithoutIo()
    {
        await Run(async (connection, transaction, token) =>
        {
            var manager = new KingbaseLargeObjectManager(connection); using var handle = await manager.OpenReadAsync(123, token); using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.ReadAsync(new byte[1], 0, 1, canceled.Token)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CreateAsync(cancellationToken: canceled.Token));
            Assert.Throws<NotSupportedException>(() => handle.WriteByte(1)); Assert.Throws<ArgumentOutOfRangeException>(() => handle.SetLength(-1));
            await Assert.ThrowsAsync<IOException>(() => handle.SeekAsync(-1, SeekOrigin.Begin, token)); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => handle.SeekAsync(0, (SeekOrigin)99, token));
            Assert.Equal(0, await handle.ReadAsync(Array.Empty<byte>(), 0, 0, token)); Assert.Equal(7, handle.Position);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "262144"], Reply("1", "int4"), token); await Function(stream, "lo_tell64", ["1"], Reply("7", "int8"), token); await Function(stream, "lo_close", ["1"], Reply("0", "int4"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlinkInvalidatesOnlyItsOwnOidAndOldCloseCannotTargetAReusedDescriptor(bool callbackFails)
    {
        var failure = new InvalidOperationException("unlink callback");
        await Run(async (connection, transaction, token) =>
        {
            var manager = new KingbaseLargeObjectManager(connection); var first = await manager.OpenReadWriteAsync(123, token); using var other = await manager.OpenReadWriteAsync(124, token);
            if (callbackFails) connection.Notice += (_, _) => throw failure;
            if (callbackFails) Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UnlinkAsync(123, token))); else await manager.UnlinkAsync(123, token);
            Assert.False(first.CanRead); Assert.True(other.CanRead); first.Dispose(); Assert.Equal(7, other.Position);
        }, async (stream, token) =>
        {
            await Function(stream, "lo_open", ["123", "393216"], Reply("1", "int4"), token); await Function(stream, "lo_open", ["124", "393216"], Reply("2", "int4"), token);
            await Function(stream, "lo_unlink", ["123"], Reply("1", "int4", callbackFails ? NoticeTests.Notice() : null), token); await Function(stream, "lo_tell64", ["2"], Reply("7", "int8"), token);
            await Function(stream, "lo_close", ["2"], Reply("0", "int4"), token);
        });
    }
}
