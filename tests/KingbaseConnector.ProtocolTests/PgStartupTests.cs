using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public class PgStartupTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task StartupWaitsForReadyAndClearsTheRequestedPasswordFrame(int algorithm)
    {
        var sequence = new List<byte[]>();
        if (algorithm != 0) sequence.Add(Auth(algorithm));
        sequence.Add(Auth(0));
        sequence.Add(Status("client_encoding", "UTF8"));
        sequence.Add(Status("server_version", "14.0"));
        sequence.Add(Status("server_version", "14.1"));
        sequence.Add(Key());
        sequence.Add(Frame('Z', (byte)'I'));
        var stream = new StartupStream(Join(sequence));
        using var wire = new PgWire(stream);
        var result = await PgStartup.RunAsync(wire, "user", "db", "password");
        Assert.Equal("UTF8", result.Parameters["client_encoding"]);
        Assert.Equal("14.1", result.Parameters["server_version"]);
        Assert.Equal(1234, result.BackendKey.ProcessId);
        Assert.Equal(5678, result.BackendKey.SecretKey);
        Assert.Equal(stream.InputLength, stream.BytesRead);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Equal(algorithm == 0 ? 1 : 2, stream.Writes.Count);
        Assert.Equal(new byte[] { 0, 0, 0, 52, 0, 3, 0, 0 }.Concat(Encoding.ASCII.GetBytes("user\0user\0database\0db\0client_encoding\0UTF8\0\0")), stream.Writes[0]);
        if (algorithm == 3)
            Assert.Equal(new byte[] { (byte)'p', 0, 0, 0, 13 }.Concat(Encoding.ASCII.GetBytes("password\0")), stream.Writes[1]);
        if (algorithm == 5)
            Assert.Equal(new byte[] { (byte)'p', 0, 0, 0, 40 }.Concat(Encoding.ASCII.GetBytes("md5a3576f1ae039b8996bc4fc2720f9c71a\0")), stream.Writes[1]);
        if (algorithm != 0) Assert.All(stream.WriteBuffers[1], value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task NoticesMayArriveDuringAuthenticationAndStartup()
    {
        var stream = new StartupStream(Join(new[] { Notice(), Auth(3), Notice(), Auth(0), Notice(), Status("client_encoding", "UTF8"), Key(), Frame('Z', (byte)'I') }));
        using var wire = new PgWire(stream);
        await PgStartup.RunAsync(wire, "user", "db", "password");
        Assert.Equal(2, stream.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerErrorsBeforeOrAfterAuthenticationHaveSafeDiagnosticsAndRetireTheWire(bool authenticated)
    {
        var frames = new List<byte[]>();
        if (authenticated) frames.Add(Auth(0));
        frames.Add(Frame('E', Encoding.ASCII.GetBytes("SFATAL\0C28P01\0Mprivate-password\0Dprivate-detail\0\0")));
        var stream = new StartupStream(Join(frames));
        using var wire = new PgWire(stream);
        var error = await Assert.ThrowsAsync<PgStartupException>(() => PgStartup.RunAsync(wire, "user", "db", "private-password"));
        Assert.Equal("28P01", error.ServerError.SqlState);
        Assert.Equal("private-password", error.ServerError.MessageText);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => wire.ReadMessage());
    }

    [Fact]
    public async Task WrongOrderMissingStateAndUnexpectedMessagesCannotProduceAUsableSession()
    {
        var sequences = new[]
        {
            new[] { Status("client_encoding", "UTF8") },
            new[] { Key() },
            new[] { Frame('Z', (byte)'I') },
            new[] { Auth(3), Status("client_encoding", "UTF8") },
            new[] { Auth(0), Auth(0) },
            new[] { Auth(0), Key(), Key() },
            new[] { Auth(0), Key(), Frame('Z', (byte)'I') },
            new[] { Auth(0), Status("client_encoding", "LATIN1"), Key(), Frame('Z', (byte)'I') },
            new[] { Auth(0), Status("client_encoding", "UTF8"), Frame('Z', (byte)'I') },
            new[] { Auth(0), Status("client_encoding", "UTF8"), Key(), Frame('Z', (byte)'T') },
            new[] { Auth(0), Frame('D', 0, 0) },
            new[] { Auth(0), Frame('K', 1, 2) },
            new[] { Frame('N', 0) }
        };
        foreach (var sequence in sequences)
        {
            var stream = new StartupStream(Join(sequence));
            using var wire = new PgWire(stream);
            await Assert.ThrowsAsync<IOException>(() => PgStartup.RunAsync(wire, "user", "db", "password"));
            Assert.Equal(1, stream.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => wire.ReadMessage());
        }
    }

    [Fact]
    public async Task UnsupportedAuthenticationRetiresTheWireAndNeverSendsAPassword()
    {
        var stream = new StartupStream(Auth(10));
        using var wire = new PgWire(stream);
        await Assert.ThrowsAsync<NotSupportedException>(() => PgStartup.RunAsync(wire, "user", "db", "password"));
        Assert.Single(stream.Writes);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task FailedPasswordWriteStillClearsThePasswordFrameAndRetiresTheWire()
    {
        var stream = new StartupStream(Auth(3)) { FailPasswordWrite = true };
        using var wire = new PgWire(stream);
        await Assert.ThrowsAsync<IOException>(() => PgStartup.RunAsync(wire, "user", "db", "password"));
        Assert.All(stream.WriteBuffers[1], value => Assert.Equal(0, value));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task PreCancellationPreservesTheWireButCancellationAfterStartupRetiresIt()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stream = new StartupStream(Array.Empty<byte>());
        using (var wire = new PgWire(stream))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PgStartup.RunAsync(wire, "user", "db", "password", cancelled.Token));
            Assert.Empty(stream.Writes);
            Assert.Equal(0, stream.DisposeCount);
        }
        using var pending = new CancellationTokenSource();
        var waiting = new StartupStream(Array.Empty<byte>()) { BlockAtEnd = true };
        using var blockedWire = new PgWire(waiting);
        var startup = PgStartup.RunAsync(blockedWire, "user", "db", "password", pending.Token);
        await waiting.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Single(waiting.Writes);
        Assert.Equal(1, waiting.DisposeCount);
    }

    [Fact]
    public async Task OversizedStartupPayloadIsRejectedFromItsHeaderWithoutWaitingOrAllocatingTheBody()
    {
        // Generic PG framing permits this size, but the startup budget must reject its header.
        var length = 2 * 1024 * 1024 + 4;
        var header = new byte[] { (byte)'S', (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length };
        var stream = new StartupStream(Join(new[] { Auth(0), header })) { BlockAtEnd = true };
        using var wire = new PgWire(stream);
        await Assert.ThrowsAsync<IOException>(() => PgStartup.RunAsync(wire, "user", "db", "password").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(14, stream.BytesRead);
        Assert.False(stream.ReadBlocked.Task.IsCompleted);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task StartupBoundsBothFrameCountAndAggregatePayloadMemory()
    {
        var sequences = new[]
        {
            new[] { Auth(0) }.Concat(Enumerable.Repeat(Status("x", "y"), 256)),
            new[] { Auth(0), Status("x", new string('a', 600_000)), Status("x", new string('b', 600_000)) }
        };
        foreach (var sequence in sequences)
        {
            var stream = new StartupStream(Join(sequence)) { FragmentSize = 64 * 1024 };
            using var wire = new PgWire(stream);
            await Assert.ThrowsAsync<IOException>(() => PgStartup.RunAsync(wire, "user", "db", "password"));
            Assert.Equal(1, stream.DisposeCount);
        }
    }

    static byte[] Auth(int algorithm) => algorithm == 5 ? Frame('R', 0, 0, 0, 5, 1, 2, 3, 4) : Frame('R', 0, 0, 0, (byte)algorithm);
    static byte[] Status(string name, string value) => Frame('S', Encoding.UTF8.GetBytes(name + "\0" + value + "\0"));
    static byte[] Key() => Frame('K', 0, 0, 4, 210, 0, 0, 22, 46);
    static byte[] Notice() => Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mmessage\0\0"));
    static byte[] Join(IEnumerable<byte[]> frames) => frames.SelectMany(frame => frame).ToArray();
    static byte[] Frame(char code, params byte[] payload)
    {
        var length = payload.Length + 4;
        return new byte[] { (byte)code, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length }.Concat(payload).ToArray();
    }

    sealed class StartupStream(byte[] input) : Stream
    {
        int _position;
        public int InputLength => input.Length;
        public int BytesRead => _position;
        public int DisposeCount { get; private set; }
        public int FragmentSize { get; init; } = 1;
        public bool FailPasswordWrite { get; init; }
        public bool BlockAtEnd { get; init; }
        public List<byte[]> Writes { get; } = new();
        public List<byte[]> WriteBuffers { get; } = new();
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, input.Length - _position), FragmentSize);
            Buffer.BlockCopy(input, _position, buffer, offset, read);
            _position += read;
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_position == input.Length && BlockAtEnd)
            {
                ReadBlocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return Read(buffer, offset, count);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes.Add(buffer.Skip(offset).Take(count).ToArray());
            WriteBuffers.Add(buffer);
            if (FailPasswordWrite && buffer[offset] == (byte)'p') throw new IOException("Failed test write.");
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
