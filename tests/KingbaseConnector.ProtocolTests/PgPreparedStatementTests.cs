using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgPreparedStatementTests
{
    const uint IntOid = uint.MaxValue;
    const uint TextOid = 7104;
    static readonly Task<PgTypeCatalog> Types = LoadTypes();
    static byte[] Prepared() => Join(Frame('1'), Ready());
    static byte[] Closed() => Join(Frame('3'), Ready());
    static byte[] Value(string value = "42") => Join(Frame('2'), Description(1, IntOid, 4), Row([value]), Complete(), Ready());
    static byte[] Error(string state = "42601") => Frame('E', Encoding.ASCII.GetBytes("SERROR\0C" + state + "\0Mprivate-error\0\0"));

    [Fact]
    public async Task NamedParseBindAndCloseUseExactFramesAndClearBuffers()
    {
        var stream = new QueryStream(Join(Prepared(), Value(), Value("43"), Closed(), Complete(), Ready()));
        using var session = Session(stream);
        var catalog = await Types;
        var statement = await session.PrepareAsync("SELECT $1", catalog, [IntOid]);
        Assert.True(statement.IsValid);
        var name = Encoding.ASCII.GetBytes(statement.Name + "\0");
        Assert.Equal(Frame('P', Join(name, Encoding.UTF8.GetBytes("SELECT $1\0"), [0, 1], Int32(-1))), stream.Writes[0]);
        Assert.Equal(Frame('S'), stream.Writes[1]);
        Assert.Same(statement, await session.PrepareAsync("SELECT $1", catalog, [IntOid]));
        Assert.Equal(2, stream.Writes.Count);
        var result = Assert.Single(await session.ExecutePreparedAsync(statement, catalog, [new(IntOid, 42)]));
        Assert.Equal("42", result.Rows[0][0]);
        Assert.Equal(Frame('B', Join([0], name, [0, 0, 0, 1], Int32(2), Encoding.ASCII.GetBytes("42"), [0, 0])), stream.Writes[2]);
        Assert.Equal(Frame('D', [(byte)'P', 0]), stream.Writes[3]);
        Assert.Equal(Frame('E', [0, 0, 0, 0, 0]), stream.Writes[4]);
        Assert.Equal(Frame('S'), stream.Writes[5]);
        Assert.Equal("43", Assert.Single(await session.ExecutePreparedAsync(statement, catalog, [new(IntOid, 43)])).Rows[0][0]);
        await session.UnprepareAsync(statement);
        Assert.False(statement.IsValid);
        Assert.Equal(Frame('C', Join([(byte)'S'], name)), stream.Writes[10]);
        Assert.Equal(Frame('S'), stream.Writes[11]);
        await session.UnprepareAsync(statement);
        Assert.Equal(12, stream.Writes.Count);
        Assert.Single(await session.ExecuteAsync("SELECT 1"));
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task IdentityIncludesExactSqlOrderedOidsAndLexicalStatus()
    {
        var stream = new QueryStream(Join(Prepared(), Prepared(), Prepared(),
            Status("standard_conforming_strings", "off"), Complete(), Ready(), Prepared(), Value()));
        using var session = Session(stream);
        var catalog = await Types;
        uint[] oids = [IntOid];
        var first = await session.PrepareAsync("SELECT $1", catalog, oids);
        oids[0] = TextOid;
        var otherType = await session.PrepareAsync("SELECT $1", catalog, oids);
        var otherSql = await session.PrepareAsync("select $1", catalog, [IntOid]);
        Assert.NotSame(first, otherType);
        Assert.NotSame(first, otherSql);
        await session.ExecuteAsync("SET standard_conforming_strings=off");
        var before = stream.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecutePreparedAsync(first, catalog, [new(IntOid, 42)]));
        Assert.Equal(before, stream.Writes.Count);
        var current = await session.PrepareAsync("SELECT $1", catalog, [IntOid]);
        Assert.NotSame(first, current);
        Assert.Single(await session.ExecutePreparedAsync(current, catalog, [new(IntOid, 42)]));
    }

    [Fact]
    public async Task CountLimitEvictsLeastRecentlyUsedWithCloseBeforeParse()
    {
        var stream = new QueryStream(Join(Join(Enumerable.Range(0, 64).Select(_ => Prepared()).ToArray()), Closed(), Prepared()));
        using var session = Session(stream);
        var catalog = await Types;
        var handles = new List<PgPreparedStatement>();
        for (var i = 0; i < 64; i++) handles.Add(await session.PrepareAsync("SELECT " + i, catalog, []));
        Assert.Same(handles[0], await session.PrepareAsync("SELECT 0", catalog, []));
        var next = await session.PrepareAsync("SELECT 64", catalog, []);
        Assert.True(handles[0].IsValid);
        Assert.False(handles[1].IsValid);
        Assert.True(next.IsValid);
        Assert.Equal(Frame('C', Encoding.ASCII.GetBytes("S" + handles[1].Name + "\0")), stream.Writes[128]);
        Assert.Equal((byte)'P', stream.Writes[130][0]);
        var before = stream.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecutePreparedAsync(handles[1], catalog, []));
        Assert.Equal(before, stream.Writes.Count);
    }

    [Fact]
    public async Task RetainedByteLimitEvictsBeforeCountLimit()
    {
        var stream = new QueryStream(Join(Prepared(), Closed(), Prepared()));
        using var session = Session(stream);
        var first = await session.PrepareAsync(new string('x', 600000), await Types, []);
        var next = await session.PrepareAsync(new string('y', 500000), await Types, []);
        Assert.False(first.IsValid);
        Assert.True(next.IsValid);
        Assert.Equal(new byte[] { (byte)'P', (byte)'S', (byte)'C', (byte)'S', (byte)'P', (byte)'S' }, stream.Writes.Select(x => x[0]));
    }

    [Fact]
    public async Task LocalSignatureAndValueErrorsAndForeignHandlesDoNotWrite()
    {
        var stream = new QueryStream(Join(Prepared(), Value()));
        using var session = Session(stream);
        using var other = Session(new QueryStream([]));
        var catalog = await Types;
        var statement = await session.PrepareAsync("SELECT $1", catalog, [IntOid]);
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.PrepareAsync("SELECT", catalog, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.PrepareAsync("SELECT", null!, []));
        await Assert.ThrowsAsync<ArgumentException>(() => session.PrepareAsync("bad\0sql", catalog, []));
        await Assert.ThrowsAsync<ArgumentException>(() => session.PrepareAsync(new string('x', 1024 * 1024), catalog, []));
        await Assert.ThrowsAsync<ArgumentException>(() => session.PrepareAsync("SELECT", catalog, Enumerable.Repeat(IntOid, 1025).ToArray()));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.PrepareAsync("SELECT $1", catalog, [0]));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecutePreparedAsync(statement, catalog, []));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecutePreparedAsync(statement, catalog, [new(TextOid, "private-value")]));
        await Assert.ThrowsAsync<InvalidCastException>(() => session.ExecutePreparedAsync(statement, catalog, [new(IntOid, 1L)]));
        await Assert.ThrowsAsync<ArgumentException>(() => other.ExecutePreparedAsync(statement, catalog, [new(IntOid, 42)]));
        await Assert.ThrowsAsync<ArgumentException>(() => other.UnprepareAsync(statement));
        Assert.Equal(2, stream.Writes.Count);
        Assert.Single(await session.ExecutePreparedAsync(statement, catalog, [new(IntOid, 42)]));
    }

    [Fact]
    public async Task TypedNullAndEmptyQueryUseTheRetainedStatement()
    {
        var stream = new QueryStream(Join(Prepared(), Frame('2'), Frame('n'), Frame('I'), Ready()));
        using var session = Session(stream);
        var statement = await session.PrepareAsync("", await Types, [TextOid]);
        var result = Assert.Single(await session.ExecutePreparedAsync(statement, await Types, [new(TextOid, null)]));
        Assert.Null(result.CommandTag);
        Assert.Equal(Frame('B', Join([0], Encoding.ASCII.GetBytes(statement.Name + "\0"), [0, 0, 0, 1], Int32(-1), [0, 0])), stream.Writes[2]);
    }

    [Fact]
    public async Task PrepareServerErrorDrainsAndPreservesSafeSessionReuse()
    {
        var stream = new QueryStream(Join(Error(), Status("x", "updated"), Ready(), Prepared(), Value()));
        using var session = Session(stream);
        var error = await Assert.ThrowsAsync<PgQueryException>(() => session.PrepareAsync("private-sql", Types.Result, []));
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("updated", session.Parameters["x"]);
        var statement = await session.PrepareAsync("SELECT 1", await Types, []);
        Assert.Single(await session.ExecutePreparedAsync(statement, await Types, []));
        Assert.Equal(0, stream.DisposeCount);
    }

    [Theory]
    [InlineData("26000")]
    [InlineData("0A000")]
    public async Task MissingOrChangedPlanInvalidatesWithoutReplayAndCanBeReprepared(string state)
    {
        var stream = new QueryStream(Join(Prepared(), Error(state), Ready(), Closed(), Prepared(), Value()));
        using var session = Session(stream);
        var statement = await session.PrepareAsync("SELECT 42", await Types, []);
        var error = await Assert.ThrowsAsync<PgQueryException>(() => session.ExecutePreparedAsync(statement, Types.Result, []));
        Assert.Equal(state, error.ServerError.SqlState);
        Assert.False(statement.IsValid);
        Assert.Equal(6, stream.Writes.Count);
        var replacement = await session.PrepareAsync("SELECT 42", await Types, []);
        Assert.NotEqual(statement.Name, replacement.Name);
        Assert.Equal((byte)'C', stream.Writes[6][0]);
        Assert.Single(await session.ExecutePreparedAsync(replacement, await Types, []));
    }

    [Fact]
    public async Task OrdinaryExecutionErrorRetainsHandleAndFailedTransactionStatus()
    {
        var stream = new QueryStream(Join(Prepared(), Error("22012"), Frame('Z', [(byte)'E']), Value()));
        using var session = Session(stream);
        var statement = await session.PrepareAsync("SELECT 42", await Types, []);
        await Assert.ThrowsAsync<PgQueryException>(() => session.ExecutePreparedAsync(statement, Types.Result, []));
        Assert.Equal(PgTransactionStatus.FailedTransaction, session.TransactionStatus);
        Assert.True(statement.IsValid);
        Assert.Single(await session.ExecutePreparedAsync(statement, await Types, []));
    }

    [Fact]
    public async Task MissingStatementCloseIsIdempotentButOtherCloseErrorsRetire()
    {
        foreach (var state in new[] { "26000", "XX000" })
        {
            var stream = new QueryStream(Join(Prepared(), Error(state), Ready(), Complete(), Ready()));
            using var session = Session(stream);
            var statement = await session.PrepareAsync("SELECT 1", await Types, []);
            if (state == "26000")
            {
                await session.UnprepareAsync(statement);
                Assert.Single(await session.ExecuteAsync("SELECT 1"));
                Assert.Equal(0, stream.DisposeCount);
            }
            else
            {
                await Assert.ThrowsAsync<PgQueryException>(() => session.UnprepareAsync(statement));
                Assert.True(session.IsDisposed);
            }
            Assert.False(statement.IsValid);
        }
    }

    public static TheoryData<bool, byte[]> InvalidControls => new()
    {
        { true, Ready() }, { true, Frame('1', 0) }, { true, Join(Frame('1'), Frame('1')) },
        { true, Frame('2') }, { true, Description(1) }, { true, Join(Frame('1'), Error()) },
        { false, Ready() }, { false, Frame('3', 0) }, { false, Frame('1') }, { false, Join(Frame('3'), Frame('3')) }
    };

    [Theory]
    [MemberData(nameof(InvalidControls))]
    public async Task MalformedControlSequencesRetire(bool preparing, byte[] invalid)
    {
        var stream = new QueryStream(Join(preparing ? [] : Prepared(), invalid, Ready()));
        using var session = Session(stream);
        if (preparing) await Assert.ThrowsAsync<IOException>(() => session.PrepareAsync("SELECT 1", Types.Result, []));
        else
        {
            var statement = await session.PrepareAsync("SELECT 1", await Types, []);
            await Assert.ThrowsAsync<IOException>(() => session.UnprepareAsync(statement));
            Assert.False(statement.IsValid);
        }
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("execute")]
    [InlineData("close")]
    public async Task CancellationAndConcurrencyUseOneGateAndRetireIncompleteExchange(string stage)
    {
        var stream = new QueryStream(stage == "prepare" ? [] : Prepared()) { BlockAtEnd = true };
        using var session = Session(stream);
        using var cancellation = new CancellationTokenSource();
        var catalog = await Types;
        var statement = stage == "prepare" ? null : await session.PrepareAsync("SELECT 42", catalog, []);
        Task active = stage switch
        {
            "prepare" => session.PrepareAsync("SELECT 1", catalog, [], cancellation.Token),
            "execute" => session.ExecutePreparedAsync(statement!, catalog, [], cancellation.Token),
            _ => session.UnprepareAsync(statement!, cancellation.Token)
        };
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("SELECT 1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrepareAsync("SELECT 1", Types.Result, []));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.True(session.IsDisposed);
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        if (statement is not null) Assert.False(statement.IsValid);
    }

    [Fact]
    public async Task PreCancellationPreservesHandlesAndExplicitInvalidationRevokesAll()
    {
        var stream = new QueryStream(Join(Prepared(), Prepared()));
        using var session = Session(stream);
        var first = await session.PrepareAsync("SELECT 1", await Types, []);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrepareAsync("SELECT 2", Types.Result, [], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecutePreparedAsync(first, Types.Result, [], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.UnprepareAsync(first, cancellation.Token));
        Assert.True(first.IsValid);
        Assert.Equal(2, stream.Writes.Count);
        session.InvalidatePreparedStatements();
        Assert.False(first.IsValid);
        var next = await session.PrepareAsync("SELECT 1", await Types, []);
        Assert.NotEqual(first.Name, next.Name);
        session.Dispose();
        Assert.False(next.IsValid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedPreparationWritesRetireAndClearAllFrames(int write)
    {
        var stream = new QueryStream([]) { FailWriteNumber = write };
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => session.PrepareAsync("private-sql", Types.Result, []));
        Assert.Equal(write, stream.Writes.Count);
        Assert.Equal(1, stream.DisposeCount);
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task PhysicalResetRevokesNamedHandlesBeforeSameSocketUsesFreshCatalog()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var names = new List<string>();
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = client.GetStream();
            await PoolExecutionTests.InitializePool(stream, guard.Token);
            for (var borrower = 0; borrower < 2; borrower++)
            {
                var parse = await ReadNamedFrames(stream, "PS", guard.Token);
                var offset = 5;
                names.Add(PgBinary.ReadCString(parse[0], ref offset));
                Assert.Equal("SELECT $1", PgBinary.ReadCString(parse[0], ref offset));
                Assert.Equal(new byte[] { 0, 1 }, parse[0][offset..(offset + 2)]);
                Assert.Equal(Int32(borrower == 0 ? -1 : 12002), parse[0][(offset + 2)..]);
                await stream.WriteAsync(Prepared(), guard.Token);
                var execution = await ReadNamedFrames(stream, "BDES", guard.Token);
                Assert.Contains(names[borrower] + "\0", Encoding.ASCII.GetString(execution[0]));
                var oid = borrower == 0 ? uint.MaxValue : 12002;
                await stream.WriteAsync(Join(Frame('2'), Description(1, oid, 4), Row(["42"]), Complete(), Ready()), guard.Token);
                if (borrower == 0) await PoolExecutionTests.Reset(stream, guard.Token);
            }
            Assert.NotEqual(names[0], names[1]);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        using var physical = await PgPhysicalSession.OpenAsync(PgSessionResetTests.PoolSettings(listener).ConnectionString,
            guard.Token, prepareForPooling: true);
        var old = await physical.Queries.PrepareAsync("SELECT $1", physical.Catalog, [IntOid], guard.Token);
        Assert.Single(await physical.Queries.ExecutePreparedAsync(old, physical.Catalog, [new(IntOid, 42)], guard.Token));
        await physical.ResetAsync();
        Assert.False(old.IsValid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => physical.Queries.ExecutePreparedAsync(old, physical.Catalog, [new(IntOid, 42)], guard.Token));
        var fresh = await physical.Queries.PrepareAsync("SELECT $1", physical.Catalog, [12002], guard.Token);
        Assert.Single(await physical.Queries.ExecutePreparedAsync(fresh, physical.Catalog, [new(12002, 42)], guard.Token));
        physical.Dispose();
        Assert.False(fresh.IsValid);
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("DISCARD ALL")]
    [InlineData("DEALLOCATE ALL")]
    public async Task AcknowledgedApplicationResetOrDeallocationRevokesCache(string tag)
    {
        var stream = new QueryStream(Join(Prepared(), PgSessionResetTests.Control(tag), Prepared()));
        using var session = Session(stream);
        var old = await session.PrepareAsync("SELECT 1", await Types, []);
        await session.ExecuteAsync(tag);
        Assert.False(old.IsValid);
        var fresh = await session.PrepareAsync("SELECT 1", await Types, []);
        Assert.NotEqual(old.Name, fresh.Name);
        Assert.Equal(5, stream.Writes.Count); // Server reset already closed the old statement.
    }

    [Fact]
    public async Task SingleDeallocationKeepsOtherServerHandlesTrackedUntilClosed()
    {
        var stream = new QueryStream(Join(Prepared(), Prepared(), PgSessionResetTests.Control("DEALLOCATE"),
            Closed(), Error("26000"), Ready(), Prepared()));
        using var session = Session(stream);
        var first = await session.PrepareAsync("SELECT 1", await Types, []);
        var second = await session.PrepareAsync("SELECT 2", await Types, []);
        await session.ExecuteAsync("DEALLOCATE " + first.Name);
        Assert.False(first.IsValid);
        Assert.False(second.IsValid); // The command tag omits which name was removed.
        await session.UnprepareAsync(second);
        Assert.Equal(7, stream.Writes.Count);
        Assert.Equal(Frame('C', Encoding.ASCII.GetBytes("S" + second.Name + "\0")), stream.Writes[5]);
        var replacement = await session.PrepareAsync("SELECT 1", await Types, []);
        Assert.Equal(Frame('C', Encoding.ASCII.GetBytes("S" + first.Name + "\0")), stream.Writes[7]);
        Assert.NotEqual(first.Name, replacement.Name);
        Assert.True(replacement.IsValid);
        Assert.Equal(11, stream.Writes.Count);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task OutboundLimitsIncludeStatementNamesAndBoundValuesBeforeIo()
    {
        var catalog = await Types;
        var name = "kbp_1";
        var sql = new string('x', 1024 * 1024 - 14 - name.Length);
        Assert.Equal(1024 * 1024, PgExtendedQuery.EncodePrepare(sql, [], name).Sum(f => f.Length));
        Assert.Throws<ArgumentException>(() => PgExtendedQuery.EncodePrepare(sql + "x", [], name));
        var text = new string('x', 1024 * 1024 - 39 - name.Length);
        Assert.Equal(1024 * 1024, PgExtendedQuery.EncodePrepared(name, catalog, [new(TextOid, text)]).Sum(f => f.Length));
        Assert.Throws<ArgumentException>(() => PgExtendedQuery.EncodePrepared(name, catalog, [new(TextOid, text + "x")]));
        var stream = new QueryStream(Join(Prepared(), Value()));
        using var session = Session(stream);
        var statement = await session.PrepareAsync("SELECT $1", catalog, [TextOid]);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecutePreparedAsync(statement, catalog,
            [new(TextOid, new string('界', 349520))]));
        Assert.Equal(2, stream.Writes.Count);
        Assert.True(statement.IsValid);
        Assert.Single(await session.ExecutePreparedAsync(statement, catalog, [new(TextOid, "small")]));
    }

    [Fact]
    public async Task DisposalDuringPreparationRevokesEverythingAndDoesNotPublish()
    {
        var stream = new QueryStream(Join(Prepared(), Frame('1'))) { BlockAtEnd = true };
        using var session = Session(stream);
        var previous = await session.PrepareAsync("SELECT 1", await Types, []);
        var pending = session.PrepareAsync("SELECT 2", await Types, []);
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.False(previous.IsValid);
        Assert.True(session.IsDisposed);
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task EvictionAndParseShareOneGateAndOneCancellationBudget()
    {
        var stream = new QueryStream(Join(Enumerable.Range(0, 64).Select(_ => Prepared()).ToArray())) { BlockAtEnd = true };
        using var session = Session(stream);
        var catalog = await Types;
        var handles = new List<PgPreparedStatement>();
        for (var i = 0; i < 64; i++) handles.Add(await session.PrepareAsync("SELECT " + i, catalog, []));
        using var cancellation = new CancellationTokenSource();
        var pending = session.PrepareAsync("SELECT 64", catalog, [], cancellation.Token);
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal((byte)'C', stream.Writes[128][0]);
        Assert.Equal(130, stream.Writes.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecutePreparedAsync(handles[0], catalog, []));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.All(handles, handle => Assert.False(handle.IsValid));
        Assert.Equal(130, stream.Writes.Count);
    }

    public static TheoryData<byte[]> InvalidExecutions => new()
    {
        Frame('1'), Ready(), Frame('2', 0), Description(1), Join(Frame('2'), Frame('2')),
        Join(Frame('2'), Frame('n'), Frame('s')), Join(Frame('2'), Frame('n'), Complete(), Complete()),
        Join(Frame('2'), Frame('n'), Frame('G')), Join(Frame('2'), Frame('t'))
    };

    [Theory]
    [MemberData(nameof(InvalidExecutions))]
    public async Task MalformedNamedExecutionsRetireHandles(byte[] invalid)
    {
        var stream = new QueryStream(Join(Prepared(), invalid, Ready()));
        using var session = Session(stream);
        var statement = await session.PrepareAsync("SELECT 1", await Types, []);
        await Assert.ThrowsAsync<IOException>(() => session.ExecutePreparedAsync(statement, Types.Result, []));
        Assert.False(statement.IsValid);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(6, stream.Writes.Count);
    }

    [Fact]
    public async Task OrderedSignatureAndMaximumParameterCountRemainExact()
    {
        var stream = new QueryStream(Join(Prepared(), Prepared(), Prepared(), Value()));
        using var session = Session(stream);
        var catalog = await Types;
        var first = await session.PrepareAsync("SELECT $1,$2", catalog, [IntOid, TextOid]);
        var reversed = await session.PrepareAsync("SELECT $1,$2", catalog, [TextOid, IntOid]);
        Assert.NotSame(first, reversed);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecutePreparedAsync(first, catalog, [new(TextOid, "x"), new(IntOid, 1)]));
        var oids = Enumerable.Repeat(IntOid, 1024).ToArray();
        var maximum = await session.PrepareAsync("SELECT $1", catalog, oids);
        Assert.Equal(oids, maximum.TypeOids);
        Assert.Single(await session.ExecutePreparedAsync(maximum, catalog, oids.Select(oid => new PgQueryParameter(oid, null)).ToArray()));
        Assert.Equal(10, stream.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationBoundsIncomingPayloadAndMessageCount(bool oversized)
    {
        var notice = Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0M" + (oversized ? new string('x', 1024 * 1024) : "x") + "\0\0"));
        var input = Join(Frame('1'), Join(Enumerable.Repeat(notice, oversized ? 1 : 4096).ToArray()), Ready());
        var stream = new QueryStream(input) { FragmentSize = 4096 };
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => session.PrepareAsync("SELECT 1", Types.Result, []));
        Assert.True(session.IsDisposed);
        Assert.Equal(2, stream.Writes.Count);
    }

    static async Task<byte[][]> ReadNamedFrames(Stream stream, string codes, CancellationToken token)
    {
        var frames = new List<byte[]>();
        foreach (var code in codes)
        {
            var header = new byte[5];
            await stream.ReadExactlyAsync(header, token);
            Assert.Equal((byte)code, header[0]);
            var size = PgBinary.ReadInt32(header, 1);
            Assert.InRange(size, 4, 4096);
            var frame = new byte[size + 1];
            header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(5), token);
            frames.Add(frame);
        }
        return frames.ToArray();
    }

    static PgQuerySession Session(QueryStream stream) => new(new PgWire(stream), new PgStartupResult(
        new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["standard_conforming_strings"] = "on" }, new PgBackendKey(1, 2)));

    static async Task<PgTypeCatalog> LoadTypes()
    {
        (string Name, short Size)[] types = [("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1), ("numeric", -1)];
        var definitions = types.Select((t, i) => new PgTypeDescription(t.Name == "int4" ? IntOid : (uint)(7100 + i),
            90, t.Name, t.Size, 'b', 'U', 0, 0, 0, -1));
        using var session = Session(new QueryStream(Catalog(definitions)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql,
            KingbaseCompatibilityMode.PostgreSql, null, null));
    }
}
