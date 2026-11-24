using System.Text;
using System.Net;
using System.Net.Sockets;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public class PgQuerySessionTests
{
    [Fact]
    public async Task FragmentedTextResultsPreserveMetadataNullEmptyAndUnicodeWithoutOidAssumptions()
    {
        var stream = new QueryStream(Join(
            Description("column"), Row("应用"), Row(null), Row(""), Complete("SELECT 3"), Ready('T')));
        using var session = Session(stream);
        var results = await session.ExecuteAsync("SELECT private_value");
        var result = Assert.Single(results);
        var column = Assert.Single(result.Columns);
        Assert.Equal("column", column.Name);
        Assert.Equal(0xf0000001u, column.TableOid);
        Assert.Equal(-2, column.AttributeNumber);
        Assert.Equal(0xfffffffeu, column.TypeOid);
        Assert.Equal(-1, column.TypeSize);
        Assert.Equal(-7, column.TypeModifier);
        Assert.Equal("应用", result.Rows[0][0]);
        Assert.Null(result.Rows[1][0]);
        Assert.Equal("", result.Rows[2][0]);
        Assert.Equal("SELECT 3", result.CommandTag);
        Assert.Equal(PgTransactionStatus.InTransaction, session.TransactionStatus);
        Assert.Equal(stream.InputLength, stream.BytesRead);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Equal(new byte[] { (byte)'Q', 0, 0, 0, 25 }.Concat(Encoding.ASCII.GetBytes("SELECT private_value\0")), Assert.Single(stream.Writes));
        Assert.Throws<NotSupportedException>(() => ((IList<PgTextResult>)results).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PgColumnDescription>)result.Columns).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyList<string?>>)result.Rows).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string?>)result.Rows[0])[0] = "changed");
    }

    [Fact]
    public void SyncAndRepeatedQueriesKeepSeparateStatementAndEmptyResults()
    {
        var stream = new QueryStream(Join(Complete("UPDATE 2"), Description("empty"), Complete("SELECT 0"),
            Frame('T', 0, 0), Frame('D', 0, 0), Complete("SELECT 1"), Ready('I'), Frame('I'), Ready('I')));
        using var session = Session(stream);
        var results = session.Execute("UPDATE t SET n=2; SELECT empty; SELECT");
        Assert.Equal(3, results.Count);
        Assert.Empty(results[0].Columns);
        Assert.Empty(results[0].Rows);
        Assert.Equal("UPDATE 2", results[0].CommandTag);
        Assert.Single(results[1].Columns);
        Assert.Empty(results[1].Rows);
        Assert.Empty(results[2].Columns);
        Assert.Empty(Assert.Single(results[2].Rows));
        var empty = Assert.Single(session.Execute(""));
        Assert.Null(empty.CommandTag);
        Assert.Empty(empty.Columns);
        Assert.Empty(empty.Rows);
        Assert.Equal(2, stream.Writes.Count);
    }

    [Fact]
    public async Task ServerErrorDrainsToReadyBeforeThrowingAndAllowsTheNextQuery()
    {
        var first = Join(Description("first"), Row("partial"), Complete("SELECT 1"), Description("second"),
            Error(), Notice(), Status("standard_conforming_strings", "off"), Ready('E'));
        var stream = new QueryStream(Join(first, Complete("ROLLBACK"), Ready('I')));
        using var session = Session(stream);
        var error = await Assert.ThrowsAsync<PgQueryException>(() => session.ExecuteAsync("private-sql"));
        Assert.Equal("22012", error.ServerError.SqlState);
        Assert.Equal("private-message", error.ServerError.MessageText);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(first.Length, stream.BytesRead);
        Assert.Equal(PgTransactionStatus.FailedTransaction, session.TransactionStatus);
        Assert.Equal("off", session.Parameters["standard_conforming_strings"]);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Equal("ROLLBACK", Assert.Single(await session.ExecuteAsync("ROLLBACK")).CommandTag);
        Assert.Equal(PgTransactionStatus.Idle, session.TransactionStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorMayInterruptTheWaitForReadyEvenAfterACompletedOrEmptyResult(bool empty)
    {
        var first = Join(empty ? Frame('I') : Complete("UPDATE 1"), Error(), Ready('E'));
        var stream = new QueryStream(Join(first, Complete("ROLLBACK"), Ready('I')));
        using var session = Session(stream);
        var error = await Assert.ThrowsAsync<PgQueryException>(() => session.ExecuteAsync(empty ? "" : "UPDATE"));
        Assert.Equal("22012", error.ServerError.SqlState);
        Assert.Equal(first.Length, stream.BytesRead);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Equal("ROLLBACK", Assert.Single(await session.ExecuteAsync("ROLLBACK")).CommandTag);
    }

    [Fact]
    public async Task NoticesAndParameterChangesAreProcessedWithoutBecomingResults()
    {
        var initial = new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["x"] = "initial" };
        var stream = new QueryStream(Join(Notice(), Status("x", "one"), Description("x"), Notice(),
            Status("x", "two"), Row("value"), Complete("SELECT 1"), Notice(), Ready('I')));
        using var session = new PgQuerySession(new PgWire(stream), new PgStartupResult(initial, new PgBackendKey(1, 2)));
        initial["x"] = "external change";
        Assert.Equal("initial", session.Parameters["x"]);
        var result = Assert.Single(await session.ExecuteAsync("SHOW x"));
        Assert.Equal("value", result.Rows[0][0]);
        Assert.Equal("two", session.Parameters["x"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)session.Parameters).Clear());
    }

    [Fact]
    public async Task RetainedParameterCountIsBoundedAcrossQueries()
    {
        var first = Join(Enumerable.Range(0, 255).Select(i => Status("p" + i, "value"))
            .Concat(new[] { Complete("SELECT 0"), Ready('I') }).ToArray());
        var stream = new QueryStream(Join(first, Status("p255", "value"), Complete("SELECT 0"), Ready('I')));
        using var session = Session(stream);
        await session.ExecuteAsync("first");
        Assert.Equal(256, session.Parameters.Count);
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("second"));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task RetainedParameterBytesHaveABudgetAcrossQueriesAndReplacingAValueReleasesItsBudget()
    {
        var overhead = Encoding.UTF8.GetByteCount("client_encoding\0UTF8\0p\0\0");
        var exact = new string('x', 1024 * 1024 - overhead);
        var stream = new QueryStream(Join(Status("p", exact), Complete("SELECT 0"), Ready('I'),
            Status("p", ""), Complete("SELECT 0"), Ready('I'),
            Status("p", exact), Complete("SELECT 0"), Ready('I'),
            Status("q", ""), Complete("SELECT 0"), Ready('I'))) { FragmentSize = 64 * 1024 };
        using var session = Session(stream);
        await session.ExecuteAsync("first");
        Assert.Equal(exact, session.Parameters["p"]);
        await session.ExecuteAsync("shrink");
        Assert.Equal("", session.Parameters["p"]);
        await session.ExecuteAsync("replace");
        Assert.Equal(exact, session.Parameters["p"]);
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("overflow"));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionAndErrorsWaitForReadyAndRejectConcurrentExchanges(bool serverError)
    {
        var stream = new QueryStream(serverError ? Error() : Complete("UPDATE 1")) { BlockAtEnd = true };
        using var session = Session(stream);
        using var cancel = new CancellationTokenSource();
        var pending = session.ExecuteAsync("UPDATE t", cancel.Token);
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("second query"));
        Assert.Single(stream.Writes);
        Assert.Equal(0, stream.DisposeCount);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, stream.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync("next"));
    }

    public static IEnumerable<object[]> InvalidSequences()
    {
        yield return new object[] { Join(Ready('I')) };
        yield return new object[] { Join(Row("no description")) };
        yield return new object[] { Join(Description("a"), Description("b")) };
        yield return new object[] { Join(Description("a"), Ready('I')) };
        yield return new object[] { Join(Description("a"), Frame('I')) };
        yield return new object[] { Join(Frame('I', 1)) };
        yield return new object[] { Join(Frame('I'), Frame('I'), Ready('I')) };
        yield return new object[] { Join(Complete("UPDATE 1"), Frame('I'), Ready('I')) };
        yield return new object[] { Join(Frame('I'), Complete("UPDATE 1"), Ready('I')) };
        yield return new object[] { Join(Frame('I'), Description("a"), Complete("SELECT 0"), Ready('I')) };
        yield return new object[] { Join(Complete("")) };
        yield return new object[] { Join(Frame('C', Encoding.ASCII.GetBytes("UPDATE 1"))) };
        yield return new object[] { Join(Frame('C', Encoding.ASCII.GetBytes("UPDATE 1\0x"))) };
        yield return new object[] { Join(Frame('C', 0xff, 0)) };
        yield return new object[] { Join(Description("a"), Frame('D', 0, 0)) };
        yield return new object[] { Join(Description("a"), Frame('D', 0, 1, 0xff, 0xff, 0xff, 0xfe)) };
        yield return new object[] { Join(Description("a"), Frame('D', 0, 1, 0x7f, 0xff, 0xff, 0xff)) };
        yield return new object[] { Join(Description("a"), Frame('D', 0, 1, 0, 0, 0, 1, 0xff)) };
        yield return new object[] { Join(Description("a"), Frame('D', 0, 1, 0xff, 0xff, 0xff, 0xff, 0)) };
        yield return new object[] { Join(Frame('T', DescriptionPayload("a").Concat(new byte[] { 0 }).ToArray())) };
        var binaryFormat = DescriptionPayload("a");
        binaryFormat[^1] = 1;
        yield return new object[] { Join(Frame('T', binaryFormat)) };
        var invalidName = DescriptionPayload("a");
        invalidName[2] = 0xff;
        yield return new object[] { Join(Frame('T', invalidName)) };
        yield return new object[] { Join(Frame('T', 0xff, 0xff)) };
        yield return new object[] { Join(Error(), Complete("UPDATE 1")) };
        yield return new object[] { Join(Error(), Error()) };
        yield return new object[] { Join(Error(), Frame('D', 0, 0)) };
        yield return new object[] { Join(Complete("UPDATE 1"), Ready('X')) };
        yield return new object[] { Join(Status("client_encoding", "LATIN1")) };
        yield return new object[] { Join(Frame('S', 0)) };
        yield return new object[] { Join(Frame('N', 0)) };
        yield return new object[] { Join(Frame('R', 0, 0, 0, 0)) };
        yield return new object[] { Join(Frame('K', new byte[8])) };
        yield return new object[] { Join(Frame('A', new byte[8])) };
        yield return new object[] { Join(Frame('G', 0, 0, 0)) };
        yield return new object[] { Join(Frame('H', 0, 0, 0)) };
        yield return new object[] { Join(Frame('W', 0, 0, 0)) };
    }

    [Theory]
    [MemberData(nameof(InvalidSequences))]
    public async Task MalformedOrUnsupportedRepliesRetireTheWire(byte[] input)
    {
        var stream = new QueryStream(input);
        using var session = Session(stream);
        var error = await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("private-sql"));
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Equal(1, stream.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync("next"));
        Assert.Single(stream.Writes);
    }

    [Fact]
    public async Task EveryTruncatedDescriptionAndRowPayloadIsRejected()
    {
        var description = DescriptionPayload("应用");
        var row = Row("应用").Skip(5).ToArray();
        foreach (var (code, payload) in new[] { ('T', description), ('D', row) })
        for (var length = 0; length < payload.Length; length++)
        {
            var prefix = code == 'D' ? Description("应用") : Array.Empty<byte>();
            var stream = new QueryStream(Join(prefix, Frame(code, payload.Take(length).ToArray())));
            using var session = Session(stream);
            await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("SELECT"));
            Assert.Equal(1, stream.DisposeCount);
        }
    }

    [Fact]
    public async Task PreCancellationAndInvalidSqlDoNotWriteOrRetireTheSession()
    {
        var stream = new QueryStream(Join(Complete("SELECT 0"), Ready('I')));
        using var session = Session(stream);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync("SELECT", cancel.Token));
        foreach (var sql in new[] { "private\0sql", new string((char)0xd800, 1), new string('x', 1024 * 1024) })
            await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAsync(sql));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.ExecuteAsync(null!));
        Assert.Empty(stream.Writes);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Single(await session.ExecuteAsync("SELECT"));
    }

    [Fact]
    public async Task SqlAtItsExactUtf8ByteLimitIsAllowed()
    {
        var stream = new QueryStream(Join(Complete("SELECT 0"), Ready('I')));
        using var session = Session(stream);
        await session.ExecuteAsync(new string('x', 1024 * 1024 - 1));
        Assert.Equal(1024 * 1024 + 5, Assert.Single(stream.Writes).Length);
    }

    [Fact]
    public async Task OversizedBackendHeaderFailsBeforeReadingItsAbsentBody()
    {
        var stream = new QueryStream(new byte[] { (byte)'T', 0, 0x10, 0, 5 }) { BlockAtEnd = true };
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("SELECT").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(5, stream.BytesRead);
        Assert.False(stream.ReadBlocked.Task.IsCompleted);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AggregateBackendBudgetAcceptsExactBoundaryAndRejectsTheNextByte(bool exceeds)
    {
        var description = Description("a");
        var complete = Complete("SELECT 1");
        var ready = Ready('I');
        var fixedBytes = description.Length - 5 + 6 + complete.Length - 5 + ready.Length - 5;
        var value = new string('x', 1024 * 1024 - fixedBytes + (exceeds ? 1 : 0));
        var stream = new QueryStream(Join(description, Row(value), complete, ready)) { FragmentSize = 64 * 1024 };
        using var session = Session(stream);
        if (exceeds)
        {
            await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("SELECT"));
            Assert.Equal(stream.InputLength - 1, stream.BytesRead);
            Assert.Equal(1, stream.DisposeCount);
        }
        else
            Assert.Equal(value, Assert.Single(await session.ExecuteAsync("SELECT")).Rows[0][0]);
    }

    [Theory]
    [InlineData("frames", false)]
    [InlineData("frames", true)]
    [InlineData("results", false)]
    [InlineData("results", true)]
    [InlineData("rows", false)]
    [InlineData("rows", true)]
    [InlineData("columns", false)]
    [InlineData("columns", true)]
    public async Task StructuralBudgetsAcceptExactBoundariesAndRetireAboveThem(string budget, bool exceeds)
    {
        var extra = exceeds ? 1 : 0;
        var frames = budget switch
        {
            "frames" => Enumerable.Repeat(Notice(), 4094 + extra).Concat(new[] { Complete("UPDATE 1"), Ready('I') }),
            "results" => Enumerable.Repeat(Complete("UPDATE 1"), 256 + extra).Concat(new[] { Ready('I') }),
            "rows" => new[] { Frame('T', 0, 0) }.Concat(Enumerable.Repeat(Frame('D', 0, 0), 1024 + extra)).Concat(new[] { Complete("SELECT 1024"), Ready('I') }),
            _ => new[] { Frame('T', Count(1024 + extra).Concat(Enumerable.Repeat(DescriptionPayload("").Skip(2).ToArray(), 1024 + extra).SelectMany(x => x)).ToArray()), Complete("SELECT 0"), Ready('I') }
        };
        var stream = new QueryStream(Join(frames.ToArray())) { FragmentSize = 64 * 1024 };
        using var session = Session(stream);
        if (exceeds)
        {
            await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync("SELECT"));
            Assert.Equal(1, stream.DisposeCount);
        }
        else
        {
            var results = await session.ExecuteAsync("SELECT");
            Assert.NotEmpty(results);
            Assert.Equal(0, stream.DisposeCount);
            if (budget == "columns") Assert.Equal(1024, results[0].Columns.Count);
            if (budget == "rows") Assert.Equal(1024, results[0].Rows.Count);
            if (budget == "results") Assert.Equal(256, results.Count);
        }
    }

    [Fact]
    public async Task WriteFailureAndMidReplyEofRetireExactlyOnce()
    {
        foreach (var failWrite in new[] { false, true })
        {
            var stream = new QueryStream(new byte[] { (byte)'C', 0, 0 }) { FailWrite = failWrite };
            var session = Session(stream);
            await Assert.ThrowsAnyAsync<IOException>(() => session.ExecuteAsync("UPDATE"));
            session.Dispose();
            session.Dispose();
            Assert.Equal(1, stream.DisposeCount);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync("next"));
        }
    }

    [Fact]
    public async Task DisposalDuringAnExchangeUnblocksReadAndForbidsReuse()
    {
        var stream = new QueryStream(Array.Empty<byte>()) { BlockAtEnd = true };
        var session = Session(stream);
        var pending = session.ExecuteAsync("SELECT");
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        session.Dispose();
        Assert.Equal(1, stream.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync("next"));
    }

    [Fact]
    public async Task LoopbackTransportStartupAndQueryExchangeUseTheSameOwnedSocket()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var peer = ServeAsync();
        try
        {
            using var transport = await PgTransport.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                KingbaseSslMode.Disable, token);
            var wire = new PgWire(transport.Stream);
            var startup = await PgStartup.RunAsync(wire, "user", "db", "password", token);
            using var session = new PgQuerySession(wire, startup);
            Assert.Equal("value", Assert.Single(await session.ExecuteAsync("SHOW example", token)).Rows[0][0]);
            session.Dispose();
        }
        finally { await peer; }

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var stream = client.GetStream();
            var startup = new byte[52];
            await stream.ReadExactlyAsync(startup, token);
            Assert.Equal(new byte[] { 0, 0, 0, 52, 0, 3, 0, 0 }.Concat(
                Encoding.ASCII.GetBytes("user\0user\0database\0db\0client_encoding\0UTF8\0\0")), startup);
            await stream.WriteAsync(Join(Frame('R', 0, 0, 0, 0), Status("client_encoding", "UTF8"),
                Frame('K', 0, 0, 0, 1, 0, 0, 0, 2), Ready('I')), token);
            var expected = Frame('Q', Encoding.ASCII.GetBytes("SHOW example\0"));
            var query = new byte[expected.Length];
            await stream.ReadExactlyAsync(query, token);
            Assert.Equal(expected, query);
            await stream.WriteAsync(Join(Description("x"), Row("value"), Complete("SELECT 1"), Ready('I')), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        }
    }

    static PgQuerySession Session(QueryStream stream) => new(new PgWire(stream),
        new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
    static byte[] Description(string name) => Frame('T', DescriptionPayload(name));
    static byte[] DescriptionPayload(string name) => Join(Count(1), Encoding.UTF8.GetBytes(name + "\0"),
        Int32(unchecked((int)0xf0000001)), Count(unchecked((ushort)-2)), Int32(unchecked((int)0xfffffffe)),
        Count(ushort.MaxValue), Int32(-7), Count(0));
    static byte[] Row(string? value) => Frame('D', value is null ? Join(Count(1), Int32(-1)) :
        Join(Count(1), Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value)));
    static byte[] Complete(string tag) => Frame('C', Encoding.UTF8.GetBytes(tag + "\0"));
    static byte[] Ready(char status) => Frame('Z', (byte)status);
    static byte[] Status(string name, string value) => Frame('S', Encoding.UTF8.GetBytes(name + "\0" + value + "\0"));
    static byte[] Error() => Frame('E', Encoding.ASCII.GetBytes("SERROR\0C22012\0Mprivate-message\0Dprivate-detail\0\0"));
    static byte[] Notice() => Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mnotice\0\0"));
    static byte[] Count(int value) => new[] { (byte)(value >> 8), (byte)value };
    static byte[] Int32(int value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    static byte[] Join(params byte[][] pieces) => pieces.SelectMany(x => x).ToArray();
    static byte[] Frame(char code, params byte[] payload) => Join(new[] { (byte)code }, Int32(payload.Length + 4), payload);

}
