using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public class PgProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerReadPayloadLimitIsEnforcedBeforeReadingTheBody(bool async)
    {
        var stream = new FragmentedStream(new byte[] { (byte)'D', 0, 0, 0, 7, 1, 2, 3 });
        using var wire = new PgWire(stream);
        if (async) await Assert.ThrowsAsync<IOException>(() => wire.ReadMessageAsync(maxPayloadLength: 2));
        else Assert.Throws<IOException>(() => wire.ReadMessage(maxPayloadLength: 2));
        Assert.Equal(5, stream.BytesRead);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerReadLimitAllowsExactAndZeroPayloadsAndInvalidArgumentsDoNotConsumeTheStream(bool async)
    {
        var stream = new FragmentedStream(new byte[] { (byte)'D', 0, 0, 0, 7, 1, 2, 3, (byte)'S', 0, 0, 0, 4 });
        using var wire = new PgWire(stream);
        if (async) await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wire.ReadMessageAsync(maxPayloadLength: -1));
        else Assert.Throws<ArgumentOutOfRangeException>(() => wire.ReadMessage(maxPayloadLength: -1));
        Assert.Equal(0, stream.BytesRead);
        var first = async ? await wire.ReadMessageAsync(maxPayloadLength: 3) : wire.ReadMessage(maxPayloadLength: 3);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Payload);
        var second = async ? await wire.ReadMessageAsync(maxPayloadLength: 0) : wire.ReadMessage(maxPayloadLength: 0);
        Assert.Empty(second.Payload);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FragmentedAndConsecutiveMessagesKeepTheirBoundaries(bool async)
    {
        var first = new byte[] { (byte)'D', 0, 0, 0, 7, 1, 2, 3 };
        var second = new byte[] { (byte)'Z', 0, 0, 0, 5, (byte)'I' };
        var stream = new FragmentedStream(first.Concat(second).ToArray());
        using var wire = new PgWire(stream);
        var message = async ? await wire.ReadMessageAsync() : wire.ReadMessage();
        Assert.Equal((byte)'D', message.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.Payload);
        message = async ? await wire.ReadMessageAsync() : wire.ReadMessage();
        Assert.Equal((byte)'Z', message.Code);
        Assert.Equal(PgTransactionStatus.Idle, PgBackendMessages.ParseReadyForQuery(message.Payload));
        Assert.Equal(first.Length + second.Length, stream.BytesRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task TruncatedMessagesRetireTheStreamSynchronouslyAndAsynchronously(int available)
    {
        var bytes = new byte[] { (byte)'D', 0, 0, 0, 7, 1, 2, 3 };
        foreach (var async in new[] { false, true })
        {
            var stream = new FragmentedStream(bytes[..available]);
            using var wire = new PgWire(stream);
            if (async) await Assert.ThrowsAsync<EndOfStreamException>(() => wire.ReadMessageAsync());
            else Assert.Throws<EndOfStreamException>(() => wire.ReadMessage());
            Assert.Equal(1, stream.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => wire.ReadMessage());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(9)]
    public async Task MaliciousLengthsAreRejectedBeforeAnyPayloadReadOrAllocation(int length)
    {
        foreach (var async in new[] { false, true })
        {
            var bytes = new byte[9];
            bytes[0] = (byte)'D';
            PgBinary.WriteInt32(bytes, 1, length);
            var stream = new FragmentedStream(bytes);
            using var wire = new PgWire(stream, maxMessageLength: 8);
            if (async) await Assert.ThrowsAsync<IOException>(() => wire.ReadMessageAsync());
            else Assert.Throws<IOException>(() => wire.ReadMessage());
            Assert.Equal(5, stream.BytesRead);
            Assert.Equal(1, stream.DisposeCount);
        }
    }

    [Fact]
    public async Task CancellationBeforeIoPreservesTheStreamAndMidReadCancellationRetiresIt()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stream = new FragmentedStream(new byte[] { (byte)'Z', 0, 0, 0, 5, (byte)'I' });
        using (var wire = new PgWire(stream))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wire.ReadMessageAsync(cancelled.Token));
            Assert.Equal(0, stream.BytesRead);
            Assert.Equal(0, stream.DisposeCount);
            Assert.Equal((byte)'Z', wire.ReadMessage().Code);
        }
        var blocked = new BlockingReadStream();
        using var blockedWire = new PgWire(blocked);
        using var cancellation = new CancellationTokenSource();
        var pending = blockedWire.ReadMessageAsync(cancellation.Token);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => blockedWire.ReadMessage());
        Assert.Throws<InvalidOperationException>(() => blockedWire.WriteMessage(PgFrontendMessages.Sync()));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, blocked.DisposeCount);
    }

    [Fact]
    public async Task WriteFailuresRetireExactlyOnceAndSuccessfulWritesPreserveBytes()
    {
        foreach (var async in new[] { false, true })
        {
            var frame = PgFrontendMessages.Query("select '数据库'");
            var stream = new FragmentedStream(Array.Empty<byte>());
            using (var wire = new PgWire(stream))
            {
                if (async) await wire.WriteMessageAsync(frame);
                else wire.WriteMessage(frame);
                Assert.Equal(frame, stream.Written);
            }
            Assert.Equal(1, stream.DisposeCount);

            var failing = new FailingWriteStream();
            using var brokenWire = new PgWire(failing);
            if (async) await Assert.ThrowsAsync<IOException>(() => brokenWire.WriteMessageAsync(frame));
            else Assert.Throws<IOException>(() => brokenWire.WriteMessage(frame));
            Assert.Equal(frame[..2], failing.Written);
            Assert.Equal(1, failing.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => brokenWire.WriteMessage(frame));
        }
    }

    [Fact]
    public void FrontendMessagesUseNetworkOrderAndCorrectLengthConventions()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 }, PgFrontendMessages.SslRequest());
        Assert.Equal(new byte[] { 0, 0, 0, 16, 4, 210, 22, 46, 1, 2, 3, 4, 255, 255, 255, 255 },
            PgFrontendMessages.CancelRequest(0x01020304, -1));
        Assert.Equal(new byte[] { (byte)'S', 0, 0, 0, 4 }, PgFrontendMessages.Sync());
        Assert.Equal(new byte[] { (byte)'X', 0, 0, 0, 4 }, PgFrontendMessages.Terminate());
        var query = PgFrontendMessages.Query("数据库");
        Assert.Equal((byte)'Q', query[0]);
        Assert.Equal(query.Length - 1, PgBinary.ReadInt32(query, 1));
        Assert.Equal(0, query[^1]);
        Assert.Equal("数据库", Encoding.UTF8.GetString(query, 5, query.Length - 6));
        var startup = PgFrontendMessages.Startup("app", "db");
        Assert.Equal(startup.Length, PgBinary.ReadInt32(startup, 0));
        Assert.Equal(196608, PgBinary.ReadInt32(startup, 4));
        Assert.Equal("user\0app\0database\0db\0client_encoding\0UTF8\0\0",
            Encoding.UTF8.GetString(startup, 8, startup.Length - 8));
    }

    [Theory]
    [InlineData("select\0secret")]
    public void InvalidFrontendStringsAreRejectedWithoutEchoingValues(string text)
    {
        var exception = Assert.Throws<ArgumentException>(() => PgFrontendMessages.Query(text));
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Throws<ArgumentException>(() => PgFrontendMessages.Startup(text, "db"));
    }

    [Fact]
    public void UnpairedSurrogatesAreRejectedBeforeEncoding()
    {
        // Attribute serialization replaces unpaired surrogates, so construct one at runtime.
        var text = new string(new[] { (char)0xd800 });
        Assert.Throws<ArgumentException>(() => PgFrontendMessages.Query(text));
        Assert.Throws<ArgumentException>(() => PgFrontendMessages.Startup(text, "db"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task EmptyAndMaximumAllowedFramesPassWithoutReadingTheNextFrame(int length)
    {
        var bytes = new byte[length + 1];
        bytes[0] = (byte)'D';
        PgBinary.WriteInt32(bytes, 1, length);
        var stream = new FragmentedStream(bytes);
        using var wire = new PgWire(stream, maxMessageLength: 8);
        var message = await wire.ReadMessageAsync();
        Assert.Equal(length - 4, message.Payload.Length);
        Assert.Equal(length + 1, stream.BytesRead);
    }

    [Theory]
    [InlineData((byte)'I', 0)]
    [InlineData((byte)'T', 1)]
    [InlineData((byte)'E', 2)]
    public void ReadyForQueryPreservesTransactionState(byte value, int expected) =>
        Assert.Equal((PgTransactionStatus)expected, PgBackendMessages.ParseReadyForQuery(new[] { value }));

    [Fact]
    public void StructuredBackendFieldsAreStrictAndErrorsDoNotPrintBusinessData()
    {
        var error = PgBackendMessages.ParseError(Encoding.UTF8.GetBytes(
            "SERROR\0VERROR\0C23505\0Mduplicate secret-data\0Dsecret-detail\0Hsecret-hint\0zextension\0\0"));
        Assert.Equal("23505", error.SqlState);
        Assert.Equal("duplicate secret-data", error.MessageText);
        Assert.Equal("secret-detail", error.Detail);
        Assert.Equal("secret-hint", error.Hint);
        Assert.Equal("extension", error.Fields[(byte)'z']);
        Assert.DoesNotContain("secret", error.ToString());
        var status = PgBackendMessages.ParseParameterStatus(Encoding.UTF8.GetBytes("client_encoding\0UTF8\0"));
        Assert.Equal("client_encoding", status.Key);
        Assert.Equal("UTF8", status.Value);
        var key = PgBackendMessages.ParseBackendKeyData(new byte[] { 1, 2, 3, 4, 255, 255, 255, 255 });
        Assert.Equal(0x01020304, key.ProcessId);
        Assert.Equal(-1, key.SecretKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    [InlineData("SERROR\0Msecret\0\0")]
    [InlineData("SERROR\0Cbad\0Msecret\0\0")]
    [InlineData("SERROR\0Cabcde\0Msecret\0\0")]
    [InlineData("SERROR\0C23505\0Msecret\0")]
    [InlineData("SERROR\0C23505\0Msecret\0\0trailing")]
    [InlineData("SERROR\0C23505\0C23505\0Msecret\0\0")]
    public void MalformedErrorFieldsAreRejectedWithSafeDiagnostics(string text)
    {
        var exception = Assert.Throws<IOException>(() => PgBackendMessages.ParseError(Encoding.UTF8.GetBytes(text)));
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void BackendParsersRejectTruncationTrailingBytesAndInvalidUtf8()
    {
        Assert.Throws<IOException>(() => PgBackendMessages.ParseReadyForQuery(Array.Empty<byte>()));
        Assert.Throws<IOException>(() => PgBackendMessages.ParseReadyForQuery(new byte[] { (byte)'I', 0 }));
        Assert.Throws<IOException>(() => PgBackendMessages.ParseReadyForQuery(new byte[] { (byte)'?' }));
        Assert.Throws<IOException>(() => PgBackendMessages.ParseBackendKeyData(new byte[7]));
        Assert.Throws<IOException>(() => PgBackendMessages.ParseBackendKeyData(new byte[9]));
        Assert.Throws<IOException>(() => PgBackendMessages.ParseParameterStatus(Encoding.UTF8.GetBytes("name\0value")));
        Assert.Throws<IOException>(() => PgBackendMessages.ParseParameterStatus(Encoding.UTF8.GetBytes("name\0value\0x")));
        Assert.Throws<IOException>(() => PgBackendMessages.ParseParameterStatus(new byte[] { 0xff, 0, 0 }));
    }

    internal class FragmentedStream(byte[] bytes) : Stream
    {
        readonly MemoryStream input = new(bytes);
        readonly MemoryStream output = new();
        public int BytesRead { get; private set; }
        public int DisposeCount { get; private set; }
        public byte[] Written => output.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = input.Read(buffer, offset, Math.Min(1, count));
            BytesRead += read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            input.Dispose();
            output.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class BlockingReadStream() : FragmentedStream(Array.Empty<byte>())
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    sealed class FailingWriteStream() : FragmentedStream(Array.Empty<byte>())
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, Math.Min(2, count));
            throw new IOException("Write failed.");
        }
    }
}
