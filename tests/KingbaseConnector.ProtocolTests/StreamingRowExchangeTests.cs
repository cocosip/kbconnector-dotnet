using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class StreamingRowExchangeTests
{
    [Fact]
    public async Task LocalStreamingPlanEncodingCannotReuseThePreviousUncertainOutcome()
    {
        using var input = new QueryStream(Join(Frame('C', Encoding.ASCII.GetBytes("SELECT 0\0")), Error("40003"), Ready())); using var session = Session(input);
        await Assert.ThrowsAsync<PgQueryException>(() => session.ExecuteAsync("SELECT 0")); Assert.NotNull(session.UncertainError); var writes = session.WriteAttempts;
        await Assert.ThrowsAsync<ArgumentException>(() => Begin(session, "SAVEPOINT s /*" + new string('x', 1024 * 1024 - 17) + "*/"));
        Assert.Equal(writes, session.WriteAttempts); Assert.Null(session.CommandRejection); Assert.Null(session.UncertainError); Assert.False(session.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALocalSavepointFailureCannotRetireATransactionUsingThePreviousCommandsError(bool bindingFailure)
    {
        var requests = 0;
        await LargeObjectExecutionTests.Run(async (connection, transaction, token) =>
        {
            using (var first = new KingbaseCommand("SELECT before", connection) { Transaction = transaction })
            { var error = await Assert.ThrowsAsync<KingbaseException>(() => first.ExecuteScalarAsync(token)); Assert.Equal("40003", error.SqlState); }
            using var local = new KingbaseCommand(bindingFailure ? "SAVEPOINT s" : "SAVEPOINT s /*" + new string('x', 1024 * 1024 - 17) + "*/", connection) { Transaction = transaction };
            if (bindingFailure) { local.Parameters.Add(new KingbaseParameter("unused", 1) { Direction = System.Data.ParameterDirection.Output }); await Assert.ThrowsAsync<ArgumentException>(() => local.ExecuteNonQueryAsync(token)); }
            else await Assert.ThrowsAsync<ArgumentException>(() => local.ExecuteNonQueryAsync(token));
            Assert.Equal(1, requests); Assert.Equal(System.Data.ConnectionState.Open, connection.State); Assert.Same(connection, transaction.Connection);
        }, async (stream, token) =>
        {
            var request = await LargeObjectExecutionTests.Request(stream, token); Assert.Equal("SELECT before", request.Sql); Interlocked.Increment(ref requests);
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), Description(1, LargeObjectExecutionTests.Oid("int4"), 4), Row(["1"]), Complete(), LargeObjectExecutionTests.Error("40003")), token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPreparedStreamsValidateSqlWithoutRequiringAutomaticPreparation(bool mismatchedSql)
    {
        using var input = new QueryStream(Join(Frame('1'), Ready(), Frame('2'), Description(1, 10004, -1), Data("original"), Tail("SELECT 1"))); using var session = Session(input);
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql); var statement = await session.PrepareAsync("SELECT 1", catalog, Array.Empty<uint>()); var writes = input.Writes.Count;
        if (mismatchedSql)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => session.BeginRowsAsync("SELECT 2", catalog, Array.Empty<PgQueryParameter>(), statement: statement));
            Assert.Equal(writes, input.Writes.Count); Assert.True(statement.IsValid); Assert.False(session.IsDisposed);
        }
        else
        {
            using var rows = await session.BeginRowsAsync("SELECT 1", catalog, Array.Empty<PgQueryParameter>(), statement: statement);
            Assert.True(await rows.ReadRowAsync(CancellationToken.None)); Assert.False(await rows.ReadRowAsync(CancellationToken.None)); Assert.Equal((byte)'B', input.Writes[2][0]);
        }
    }

    [Fact]
    public async Task StreamingUsesAnExistingNamedStatementWhenAutomaticPreparationIsEnabled()
    {
        using var input = new QueryStream(Join(Frame('1'), Ready(), Frame('2'), Description(1, 10004, -1), Data("value"), Tail("SELECT 1")));
        using var session = new PgQuerySession(new PgWire(input), Startup(), maxAutoPrepare: 1, autoPrepareMinUsages: 2);
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql); var statement = await session.PrepareAsync("SELECT 1", catalog, Array.Empty<uint>());
        using var rows = await Begin(session); Assert.True(await rows.Read()); Assert.False(await rows.Read()); Assert.True(statement.IsValid);
        Assert.Equal((byte)'B', input.Writes[2][0]); Assert.Contains(statement.Name, Encoding.UTF8.GetString(input.Writes[2]));
    }

    [Fact]
    public async Task OnlySuccessfulStreamedStatementsCountTowardTheAutomaticPreparationThreshold()
    {
        using var input = new QueryStream(Join(Start(1), Data("failed"), Error("22012"), Ready(), Start(1), Data("success"), Tail("SELECT 1"), Frame('1'), Ready(), Frame('2'), Description(1, 10004, -1), Data("prepared"), Tail("SELECT 1")));
        using var session = new PgQuerySession(new PgWire(input), Startup(), maxAutoPrepare: 1, autoPrepareMinUsages: 2);
        using (var failed = await Begin(session)) { Assert.True(await failed.Read()); await Assert.ThrowsAsync<PgQueryException>(() => failed.Read()); }
        using (var success = await Begin(session)) { Assert.True(await success.Read()); Assert.False(await success.Read()); }
        using var prepared = await Begin(session); Assert.True(await prepared.Read()); Assert.False(await prepared.Read());
        Assert.Equal(16, input.Writes.Count); Assert.Equal((byte)'P', input.Writes[10][0]); Assert.Equal((byte)'B', input.Writes[12][0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedStreamFailuresInvalidateTheirHandleWithoutReplayingTheStatement(bool late)
    {
        using var input = new QueryStream(Join(Frame('1'), Ready(), late ? Join(Frame('2'), Description(1, 10004, -1), Data("partial")) : [], Error(late ? "0A000" : "26000"), Ready()));
        using var session = new PgQuerySession(new PgWire(input), Startup(), maxAutoPrepare: 1); var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql);
        var statement = await session.PrepareAsync("SELECT 1", catalog, Array.Empty<uint>()); PgQueryException error;
        if (late) { using var rows = await Begin(session); Assert.True(await rows.Read()); error = await Assert.ThrowsAsync<PgQueryException>(() => rows.Read()); }
        else error = await Assert.ThrowsAsync<PgQueryException>(() => Begin(session));
        Assert.Equal(late ? "0A000" : "26000", error.ServerError.SqlState); Assert.False(statement.IsValid); Assert.False(session.IsDisposed); Assert.Equal(6, input.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroColumnRowsRequireAnExactEmptyFieldArea(bool trailing)
    {
        using var input = new QueryStream(Join(Start(0), Frame('D', trailing ? Join(Count(0), new byte[1]) : Count(0)), Tail("SELECT 1"))); using var session = Session(input); using var rows = await Begin(session);
        if (trailing) { await Assert.ThrowsAsync<IOException>(() => rows.Read()); Assert.True(session.IsDisposed); }
        else { Assert.True(await rows.Read()); Assert.False(await rows.Read()); Assert.True(rows.Synchronized); Assert.False(session.IsDisposed); }
    }

    [Fact]
    public async Task ExhaustedAggregateControlBudgetRejectsTheNextHeaderBeforeWaitingForItsBody()
    {
        var notice = Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0M" + new string('x', PgWire.MaxBufferedPayloadLength - 128) + "\0\0"));
        using var input = new QueryStream(Join(notice, Start(1), new byte[] { (byte)'N' }, Int32(260))) { FragmentSize = 16384, BlockAtEnd = true }; using var session = Session(input); using var rows = await Begin(session);
        var reading = rows.Read();
        try { await Assert.ThrowsAnyAsync<IOException>(() => reading.WaitAsync(TimeSpan.FromSeconds(2))); Assert.True(session.IsDisposed); }
        finally { session.Dispose(); try { await reading; } catch { } }
    }

    [Fact]
    public async Task UncertainTerminationRetiresBeforePublishingTheQueryGate()
    {
        PgQuerySession? owner = null; Task? probe = null;
        using var input = new ClosingStream(new QueryStream(Join(Start(1), Frame('C', Encoding.ASCII.GetBytes("SELECT 0\0")), Error("40003"), Ready())), () => probe ??= owner!.ExecuteAsync("SELECT must_not_start"));
        using var session = Session(input); owner = session; using var rows = await Begin(session);
        var error = await Assert.ThrowsAsync<PgQueryException>(() => rows.Read()); Assert.Equal("40003", error.ServerError.SqlState); Assert.NotNull(probe);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe); Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task LocalFrameValidationDoesNotRetireBeforeTheFirstWriteAttempt()
    {
        using var input = new QueryStream(Tail("SELECT 0")); using var session = Session(input); using var rows = new PgQuerySession.RowExchange(session, null);
        await Assert.ThrowsAsync<ArgumentException>(() => rows.StartAsync([Array.Empty<byte>()], CancellationToken.None)); rows.Dispose();
        Assert.Equal(0, session.WriteAttempts); Assert.False(session.IsDisposed); Assert.Single(await session.ExecuteAsync("SELECT 0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondaryStreamCloseFailuresCannotReplaceTheOriginalProtocolFailure(bool afterDescription)
    {
        using var input = new ClosingStream(new QueryStream(afterDescription ? Join(Start(1), Frame('D', Count(0))) : Frame('T', Count(0))), () => throw new InvalidOperationException("secondary close failure"));
        using var session = Session(input);
        if (afterDescription) { using var rows = await Begin(session); await Assert.ThrowsAnyAsync<IOException>(() => rows.Read()); }
        else await Assert.ThrowsAnyAsync<IOException>(() => Begin(session));
        Assert.True(session.IsDisposed);
    }

    [Theory]
    [InlineData("missing-headers")]
    [InlineData("reserved-next-header")]
    [InlineData("last-field-tail")]
    [InlineData("last-null-tail")]
    public async Task ImpossibleFieldLayoutsRejectBeforeExposingAnyAlreadyUntrustworthyValue(string kind)
    {
        var columns = kind is "missing-headers" or "reserved-next-header" ? 2 : 1;
        var body = kind switch { "missing-headers" => Join(Count(2), new byte[7]), "reserved-next-header" => Join(Count(2), Int32(2), new byte[4]), "last-null-tail" => Join(Count(1), Int32(-1), new byte[1]), _ => Join(Count(1), Int32(1), new byte[2]) };
        using var input = new QueryStream(Join(Start(columns), Frame('D', body), Tail("SELECT 1"))); using var session = Session(input); using var rows = await Begin(session);
        if (kind == "missing-headers") await Assert.ThrowsAsync<IOException>(() => rows.Read());
        else { Assert.True(await rows.Read()); await Assert.ThrowsAsync<IOException>(() => rows.Column(0)); }
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task DescriptionReturnsBeforeRowsAndForwardColumnsKeepNullEmptyAndFrameBoundaries()
    {
        var start = Start(3); var text = new string('x', 2 * 1024 * 1024 + 1);
        using var input = new QueryStream(Join(start, Data(text, null, "end"), Data("next", "", "tail"), Tail("SELECT 2"))) { FragmentSize = 8191 };
        using var session = Session(input); using var rows = await Begin(session); Assert.Equal(start.Length, input.BytesRead); Assert.Equal(3, rows.Header.Columns.Count); Assert.Empty(rows.Header.Rows);
        Assert.True(await rows.Read()); Assert.Equal(text.Length, await rows.Column(0)); var buffer = new byte[16384]; long received = 0;
        while (true) { var count = await rows.Bytes(buffer); if (count == 0) break; received += count; Assert.True(buffer.AsSpan(0, count).IndexOfAnyExcept((byte)'x') < 0); }
        Assert.Equal(text.Length, received); Assert.Equal(-1, await rows.Column(1)); Assert.Equal(0, await rows.Bytes(buffer)); Assert.Equal(3, await rows.Column(2)); Assert.Equal(3, await rows.Bytes(buffer)); Assert.Equal("end", Encoding.UTF8.GetString(buffer, 0, 3));
        var position = input.BytesRead; await Assert.ThrowsAsync<InvalidOperationException>(() => rows.Column(0)); Assert.Equal(position, input.BytesRead); Assert.False(session.IsDisposed);
        Assert.True(await rows.Read()); Assert.Equal(0, await rows.Column(1)); Assert.Equal(0, await rows.Bytes(buffer));
        Assert.False(await rows.Read()); Assert.True(rows.Synchronized); Assert.Equal("SELECT 2", rows.Header.CommandTag); Assert.Equal(PgTransactionStatus.Idle, session.TransactionStatus);
    }

    [Fact]
    public async Task ManyRowsDoNotUseTheBufferedRowOrAggregateByteLimitAndHoldTheExchangeGate()
    {
        using var input = new QueryStream(Join(Start(1), Join(Enumerable.Repeat(Data(new string('x', 1024)), 1300).ToArray()), Tail("SELECT 1300"))) { FragmentSize = 16384 };
        using var session = Session(input); using var rows = await Begin(session); var writes = input.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("SELECT 99")); Assert.Equal(writes, input.Writes.Count);
        var count = 0; while (await rows.Read()) { Assert.Empty(rows.Header.Rows); count++; } Assert.Equal(1300, count); Assert.True(rows.Synchronized); Assert.False(session.IsDisposed);
    }

    [Theory]
    [InlineData("wrong-count")]
    [InlineData("negative-length")]
    [InlineData("oversized-length")]
    [InlineData("missing-length")]
    [InlineData("trailing-data")]
    public async Task MalformedRowsRetireWithoutReadingIntoTheFollowingControl(string kind)
    {
        var body = kind switch { "wrong-count" => Count(2), "negative-length" => Join(Count(1), Int32(-2)), "oversized-length" => Join(Count(1), Int32(int.MaxValue)), "missing-length" => Join(Count(1), new byte[3]), _ => Join(Count(1), Int32(1), new byte[] { 65, 66 }) };
        using var input = new QueryStream(Join(Start(1), Frame('D', body), Tail("SELECT 1"))); using var session = Session(input); using var rows = await Begin(session);
        await Assert.ThrowsAnyAsync<IOException>(async () => { while (await rows.Read()) await rows.Column(0); }); Assert.True(session.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronizedServerErrorsPreserveSqlStateAndLeaveTheNextExchangeUsable(bool late)
    {
        using var input = new QueryStream(Join(late ? Join(Start(1), Data("partial")) : [], Error("22012"), Ready(), Tail("SELECT 0"))) { FragmentSize = 4096 };
        using var session = Session(input); PgQueryException error;
        if (late) { using var rows = await Begin(session); Assert.True(await rows.Read()); error = await Assert.ThrowsAsync<PgQueryException>(() => rows.Read()); Assert.True(rows.Synchronized); }
        else error = await Assert.ThrowsAsync<PgQueryException>(() => Begin(session));
        Assert.Equal("22012", error.ServerError.SqlState); Assert.Same(error.ServerError, session.CommandRejection); Assert.False(session.IsDisposed); Assert.Single(await session.ExecuteAsync("SELECT 0"));
    }

    [Theory]
    [InlineData("40003")]
    [InlineData("08006")]
    public async Task UncertainAndConnectionErrorsRetireWithTheirOriginalSqlState(string state)
    {
        using var input = new QueryStream(Join(Start(1), Frame('C', Encoding.ASCII.GetBytes("SELECT 0\0")), Error(state), Ready())); using var session = Session(input); using var rows = await Begin(session);
        var error = await Assert.ThrowsAsync<PgQueryException>(() => rows.Read()); Assert.Equal(state, error.ServerError.SqlState); Assert.True(session.IsDisposed); Assert.Null(rows.Header.CommandTag);
    }

    [Fact]
    public async Task NoDataCommandsCompleteBeforePublicationAndEmptyQueriesStayDistinct()
    {
        using var input = new QueryStream(Join(Frame('1'), Frame('2'), Frame('n'), Tail("UPDATE 3"), Frame('1'), Frame('2'), Frame('n'), Frame('I'), Ready())); using var session = Session(input);
        using (var rows = await Begin(session)) { Assert.True(rows.Synchronized); Assert.Empty(rows.Header.Columns); Assert.Equal("UPDATE 3", rows.Header.CommandTag); Assert.False(await rows.Read()); }
        using var empty = await Begin(session, sql: ""); Assert.True(empty.Synchronized); Assert.Null(empty.Header.CommandTag); Assert.False(await empty.Read()); Assert.False(session.IsDisposed);
    }

    [Fact]
    public async Task RowStylesAndEventsFollowControlsWithoutCountingRowsAsControlTraffic()
    {
        using var input = new QueryStream(Join(Start(1), Status("DateStyle", "ISO, DMY"), Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mfixture\0\0")), Frame('A', Join(Int32(1), Encoding.UTF8.GetBytes("channel\0payload\0"))), Data("value"), Tail("SELECT 1")));
        using var session = Session(input); var notices = 0; var notifications = 0; session.NoticeHandler = _ => notices++; session.NotificationHandler = _ => notifications++;
        using var rows = await Begin(session); Assert.True(await rows.Read()); Assert.Equal("ISO, DMY", rows.Header.DateStyle); Assert.Equal(1, notices); Assert.Equal(1, notifications); Assert.False(await rows.Read());
    }

    [Fact]
    public async Task PreCanceledCallsDoNotAdvanceTheRowAndLateDisposedOwnersCannotCloseAnotherExchange()
    {
        using var input = new QueryStream(Join(Start(1), Data("abc"), Tail("SELECT 1"), Tail("SELECT 0"))); using var session = Session(input); using var rows = await Begin(session); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var before = input.BytesRead; await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rows.Read(cancel.Token)); Assert.Equal(before, input.BytesRead); Assert.False(session.IsDisposed);
        Assert.True(await rows.Read()); Assert.Equal(3, await rows.Column(0)); before = input.BytesRead; await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rows.Bytes(new byte[1], cancel.Token)); Assert.Equal(before, input.BytesRead);
        Assert.False(await rows.Read()); Assert.Single(await session.ExecuteAsync("SELECT 0")); rows.Dispose(); Assert.False(session.IsDisposed);
    }

    static PgStartupResult Startup() => new(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2));
    static PgQuerySession Session(Stream input) => new(new PgWire(input), Startup());
    static byte[] Start(int columns) => Join(Frame('1'), Frame('2'), Description(columns, 10004, -1));
    static byte[] Data(params string?[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) : Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Tail(string tag) => Join(Frame('C', Encoding.ASCII.GetBytes(tag + '\0')), Ready());
    static byte[] Error(string state) => Frame('E', Encoding.ASCII.GetBytes("SERROR\0C" + state + "\0Mprivate-fixture-error\0\0"));
    static byte[] Status(string key, string value) => Frame('S', Encoding.UTF8.GetBytes(key + '\0' + value + '\0'));
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
    static async Task<Rows> Begin(PgQuerySession session, string sql = "SELECT 1")
    {
        return new Rows(await session.BeginRowsAsync(sql, await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql), Array.Empty<PgQueryParameter>()));
    }
    sealed class Rows(PgQuerySession.RowExchange value) : IDisposable
    {
        internal PgTextResult Header => value.Header;
        internal bool Synchronized => value.IsSynchronized;
        internal Task<bool> Read(CancellationToken token = default) => value.ReadRowAsync(token);
        internal Task<int> Column(int ordinal) => value.SelectColumnAsync(ordinal, CancellationToken.None);
        internal Task<int> Bytes(byte[] buffer, CancellationToken token = default) => value.ReadColumnAsync(buffer, 0, buffer.Length, token);
        public void Dispose() => value.Dispose();
    }
    sealed class ClosingStream(QueryStream input, Action closed) : Stream
    {
        int _closed;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => input.ReadAsync(buffer, offset, count, token);
        public override void Write(byte[] buffer, int offset, int count) => input.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => input.WriteAsync(buffer, offset, count, token);
        protected override void Dispose(bool disposing) { if (disposing && Interlocked.Exchange(ref _closed, 1) == 0) { closed(); input.Dispose(); } base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
