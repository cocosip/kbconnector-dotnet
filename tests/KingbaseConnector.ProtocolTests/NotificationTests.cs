using System.Data;
using System.Reflection;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class NotificationTests
{
    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ExactAssetPublicNotificationProbe(KingbaseCompatibilityMode mode) => KingbaseConnector.Tests.NotificationAssetProbe.RunAsync(mode);

    [Fact]
    public void PublicNotificationAndWaitContractExistsOnTheConnection()
    {
        var type = typeof(KingbaseConnection); var notification = type.GetEvent("Notification"); Assert.NotNull(notification);
        var args = notification.EventHandlerType!.GenericTypeArguments.Single();
        foreach (var name in new[] { "ProcessId", "Channel", "Payload" }) Assert.NotNull(args.GetProperty(name));
        Assert.Equal(typeof(bool), type.GetMethod("Wait", [typeof(int)])!.ReturnType);
        Assert.Equal(typeof(Task<bool>), type.GetMethod("WaitAsync", [typeof(int), typeof(CancellationToken)])!.ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryMessagesAcceptInterleavedNotificationsIncludingSynchronizedErrorTails(bool error)
    {
        var tail = error ? Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C22012\0Mprivate-error\0\0")), Notify(42, "ch", "通知😀"), Ready()) :
            Join(Notify(42, "ch", "通知😀"), Scalar("42", 23, 4));
        using var session = Session(new QueryStream(tail));
        if (error) Assert.Equal("22012", (await Assert.ThrowsAsync<PgQueryException>(() => session.ExecuteAsync("SELECT 42"))).ServerError.SqlState);
        else Assert.Equal("42", Assert.Single(await session.ExecuteAsync("SELECT 42")).Rows[0][0]);
        Assert.False(session.IsDisposed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(12)]
    public async Task TimedIdleReadRetainsPartialFramesAndPermitsTheNextCommandWrite(int split)
    {
        using var stream = new FeedStream(); using var wire = new PgWire(stream); var frame = Notify(42, "ch", "payload");
        var pending = TimedRead(wire, 35, CancellationToken.None);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stream.Feed(frame[..split]);
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(0, stream.DisposeCount);
        wire.WriteMessage(PgFrontendMessages.Query("SELECT 1")); Assert.Single(stream.Writes);
        stream.Feed(frame[split..]); var result = await wire.ReadMessageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((byte)'A', result.Code); Assert.Equal(frame[5..], result.Payload); Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task CanceledIdleReadCanResumeTheSameFrameWithoutDuplicatingAnUnderlyingRead()
    {
        using var stream = new FeedStream(); using var wire = new PgWire(stream); using var cancel = new CancellationTokenSource();
        var frame = Notify(42, "ch", "payload"); var pending = TimedRead(wire, -1, cancel.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stream.Feed(frame[..6]); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(0, stream.DisposeCount);
        stream.Feed(frame[6..]); Assert.NotNull(await TimedRead(wire, 1000, CancellationToken.None)); Assert.Equal(1, stream.MaximumConcurrentReads);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("channel")]
    [InlineData("utf8")]
    [InlineData("tail")]
    [InlineData("missing")]
    public async Task MalformedNotificationsRetireWithoutExposingPayload(string shape)
    {
        var payload = Notify(shape == "pid" ? 0 : 42, shape == "channel" ? "" : "ch", "private-payload")[5..];
        if (shape == "utf8") payload[4] = 255;
        if (shape == "tail") payload = Join(payload, [(byte)'x']);
        if (shape == "missing") payload = payload[..^1];
        using var session = Session(new QueryStream(Join(Frame('A', payload), Scalar("42", 23, 4))));
        var error = await Assert.ThrowsAnyAsync<IOException>(() => session.ExecuteAsync("SELECT 42")); Assert.True(session.IsDisposed); Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task ResumedFramesRespectTheNewConsumerBudgetAndIdleFloodsAreBounded()
    {
        using var stream = new FeedStream(); using var wire = new PgWire(stream); var frame = Notify(42, "ch", "payload");
        Assert.Null(await TimedRead(wire, 0, CancellationToken.None)); stream.Feed(frame);
        await Assert.ThrowsAsync<IOException>(() => wire.ReadMessageAsync(maxPayloadLength: 3)); Assert.Equal(1, stream.DisposeCount);
        var notice = Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mnotice\0\0"));
        using var session = Session(new QueryStream(Join(Enumerable.Repeat(notice, 4097).ToArray())));
        await Assert.ThrowsAsync<IOException>(() => session.WaitForNotificationAsync(-1, CancellationToken.None, () => { })); Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task WaitTimeoutValidationAndCancellationHappenBeforeConnectionIo()
    {
        using var connection = new KingbaseConnection();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.WaitAsync(-2));
        Assert.Throws<ArgumentOutOfRangeException>(() => connection.Wait(TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => connection.Wait(TimeSpan.FromMilliseconds((double)int.MaxValue + 1)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.WaitAsync(0, cancel.Token)); Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void StaleNotificationScopesCannotClearTheCurrentFailure()
    {
        using var connection = new KingbaseConnection(); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(KingbaseConnection).GetField("_generation", flags)!.SetValue(connection, 2L);
        var failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(new InvalidOperationException("callback"));
        var field = typeof(KingbaseConnection).GetField("_notificationFailure", flags)!; field.SetValue(connection, failure);
        connection.ThrowNotificationFailure(null!, 1); Assert.Same(failure, field.GetValue(connection));
    }

    [Fact]
    public async Task ANewWaitUsesItsOwnBudgetForAPreviouslyUnfilledHeader()
    {
        using var stream = new FeedStream(); using var wire = new PgWire(stream);
        Assert.Null(await TimedRead(wire, 0, CancellationToken.None, 1)); stream.Feed(Notify(42, "ch", "payload"));
        Assert.NotNull(await TimedRead(wire, 1000, CancellationToken.None)); Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task AFrameCompletedAfterTheDeadlineStaysPendingForTheNextWait()
    {
        using var stream = new SlowFrameStream(Notify(42, "ch", "payload")); using var wire = new PgWire(stream);
        Assert.Null(await TimedRead(wire, 10, CancellationToken.None));
        Assert.NotNull(await TimedRead(wire, 1000, CancellationToken.None)); Assert.True(stream.CanRead);
    }

    sealed class SlowFrameStream(byte[] bytes) : MemoryStream(bytes)
    {
        bool _first = true;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { if (_first) { _first = false; Thread.Sleep(40); } return base.ReadAsync(buffer, offset, count, cancellationToken); }
    }

    internal static Task<PgMessage?> TimedRead(PgWire wire, int timeout, CancellationToken token, int limit = 1024 * 1024)
    {
        var method = typeof(PgWire).GetMethod("ReadMessageWithTimeoutAsync", BindingFlags.Instance | BindingFlags.NonPublic); Assert.NotNull(method);
        return (Task<PgMessage?>)method.Invoke(wire, [timeout, token, limit])!;
    }
    internal static byte[] Notify(int pid, string channel, string payload) => Frame('A', Join(Int32(pid), Encoding.UTF8.GetBytes(channel + '\0' + payload + '\0')));
    internal static PgQuerySession Session(Stream stream) => new(new PgWire(stream), new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));

    internal sealed class FeedStream : Stream
    {
        readonly Queue<byte> _bytes = new(); readonly SemaphoreSlim _available = new(0); bool _closed; int _reading;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount { get; private set; }
        internal int MaximumConcurrentReads { get; private set; }
        internal List<byte[]> Writes { get; } = [];
        internal void Feed(byte[] bytes) { lock (_bytes) foreach (var value in bytes) _bytes.Enqueue(value); _available.Release(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, Interlocked.Increment(ref _reading)); Started.TrySetResult();
            try
            {
                while (true)
                {
                    lock (_bytes)
                    {
                        if (_bytes.Count != 0) { var size = Math.Min(count, _bytes.Count); for (var i = 0; i < size; i++) buffer[offset + i] = _bytes.Dequeue(); return size; }
                        if (_closed) return 0;
                    }
                    await _available.WaitAsync(cancellationToken);
                }
            }
            finally { Interlocked.Decrement(ref _reading); }
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) { lock (Writes) Writes.Add(buffer.AsSpan(offset, count).ToArray()); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { lock (_bytes) { if (_closed) return; _closed = true; DisposeCount++; } _available.Release(); base.Dispose(disposing); }
    }
}
