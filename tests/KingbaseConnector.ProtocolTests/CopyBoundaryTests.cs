using System.Data;
using System.Reflection;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class CopyBoundaryTests
{
    [Theory]
    [InlineData("COPY items TO '/tmp/should-not-write'")]
    [InlineData("COPY items FROM PROGRAM 'untrusted'")]
    [InlineData("COPY (SELECT 'TO STDOUT') TO 'file'")]
    [InlineData("COPY items TO STDIN")]
    [InlineData("COPY items FROM STDOUT")]
    [InlineData("COPY items FROM STDIN; SELECT 1")]
    [InlineData("SELECT 1")]
    public void ClientCopyEndpointsRejectNonstreamingOrInvalidTargets(string sql) =>
        Assert.Throws<ArgumentException>(() => PgParameterPlan.Create(sql, Array.Empty<string?>(), KingbaseCompatibilityMode.PostgreSql, true, clientCopy: true));

    [Theory]
    [InlineData("/* COPY x TO 'file' */ COPY \"FROM\" . \"TO\" (\"STDIN\") FROM /* gap */ STDIN", true)]
    [InlineData("COPY (SELECT 'FROM STDIN', $q$) TO file$q$, 42 /* ) */ FROM items) TO STDOUT (FORMAT CSV)", false)]
    [InlineData("COPY items FROM STDIN WITH (FORMAT CSV, NULL 'TO STDOUT')", true)]
    public void EndpointRecognitionReusesQuotedAndCommentBoundaries(string sql, bool input) =>
        Assert.Equal(input, PgParameterPlan.Create(sql, Array.Empty<string?>(), KingbaseCompatibilityMode.PostgreSql, true, clientCopy: true).CopyIsImport);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledCancelLeavesImportAndBufferedWriterUsable(bool text)
    {
        await Run(async (connection, token) =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            if (text)
            {
                using var writer = await connection.BeginTextImportAsync("COPY items FROM STDIN", token); writer.Write("pending\n");
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.CancelAsync(cancel.Token)); Assert.Equal(1UL, await writer.CompleteAsync(token));
            }
            else
            {
                using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.CancelAsync(cancel.Token)); Assert.Equal(1UL, await copy.CompleteAsync(token));
            }
            Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, !text), token);
            if (text) Assert.Equal('d', (await Frontend(stream, token)).Code); Assert.Equal('c', (await Frontend(stream, token)).Code); await stream.WriteAsync(Tail(1), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServerErrorBetweenCopyCountAndReadyWinsAndClearsTheTentativeCount(bool input)
    {
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync(input ? "COPY items FROM STDIN (FORMAT BINARY)" : "COPY items TO STDOUT (FORMAT BINARY)", token);
            KingbaseException failure;
            if (input) failure = await Assert.ThrowsAsync<KingbaseException>(() => copy.CompleteAsync(token));
            else failure = await Assert.ThrowsAsync<KingbaseException>(() => copy.ReadAsync(new byte[9], 0, 9, token));
            Assert.Equal("42501", failure.SqlState); Assert.Null(copy.RowsCopied); Assert.Equal(ConnectionState.Open, connection.State);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(input, true), token); if (input) Assert.Equal('c', (await Frontend(stream, token)).Code);
            await stream.WriteAsync(Join(input ? [] : Frame('c'), Frame('C', Encoding.ASCII.GetBytes("COPY 9\0")), Error("42501")), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAcknowledgmentDoesNotSwallowNoticeOrNotificationFailures(bool notification)
    {
        await Run(async (connection, token) =>
        {
            var failure = new InvalidOperationException("cancel callback");
            if (notification) connection.Notification += (_, _) => throw failure; else connection.Notice += (_, _) => throw failure;
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => copy.CancelAsync(token))); Assert.Equal(ConnectionState.Open, connection.State);
            using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); Assert.Equal('f', (await Frontend(stream, token)).Code);
            await stream.WriteAsync(Join(notification ? NotificationTests.Notify(42, "ch", "") : NoticeTests.Notice(), Error("57014")), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task AnOldCopyAbortCannotDisposeTheNextCommandsSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY items FROM STDIN (FORMAT BINARY)", token); await copy.CompleteAsync(token);
            using var command = new KingbaseCommand("SELECT 42", connection); var next = command.ExecuteScalarAsync(token); await started.Task.WaitAsync(token);
            typeof(KingbaseCopyStream).GetMethod("Abort", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(copy, null); await copy.CancelAsync(token); release.SetResult();
            Assert.Equal(42, await next); Assert.Equal(ConnectionState.Open, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); await Frontend(stream, token); await stream.WriteAsync(Tail(0), token);
            await ReadExtended(stream, token); started.SetResult(); await release.Task.WaitAsync(token); await stream.WriteAsync(Value("42"), token);
        });
    }

    [Fact]
    public async Task ImportReadsNoticesWhileDataWritesAreBackpressured()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var stream = new BackpressureStream(); using var session = NotificationTests.Session(stream);
        var notices = 0; session.NoticeHandler = _ => { notices++; stream.NoticeRead.TrySetResult(); };
        var copy = session.BeginCopy(() => { }); await copy.StartAsync("COPY items FROM STDIN", budget.Token);
        await copy.WriteAsync(new byte[131073], 0, 131073, budget.Token); await copy.CompleteAsync(budget.Token);
        Assert.Equal(3, notices); Assert.Equal(131073, stream.Written); Assert.True(copy.IsSynchronized);
    }

    sealed class BackpressureStream : Stream
    {
        readonly NotificationTests.FeedStream _incoming = new();
        internal TaskCompletionSource NoticeRead { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Written { get; private set; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => _incoming.ReadAsync(buffer, offset, count, token);
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            switch ((char)buffer[offset])
            {
                case 'Q': _incoming.Feed(Response(true, false)); break;
                case 'd':
                    Written += count - 5; NoticeRead = new(TaskCreationOptions.RunContinuationsAsynchronously); _incoming.Feed(NoticeTests.Notice());
                    await NoticeRead.Task.WaitAsync(token); break;
                case 'c': _incoming.Feed(Tail(3)); break;
                default: throw new IOException("Unexpected pressure-test message.");
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _incoming.Dispose(); base.Dispose(disposing); }
    }
}
