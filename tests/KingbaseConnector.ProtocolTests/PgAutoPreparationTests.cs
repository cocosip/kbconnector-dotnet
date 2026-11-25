using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgAutoPreparationTests
{
    const uint IntOid = uint.MaxValue;
    static byte[] Prepared() => Join(Frame('1'), Ready());
    static byte[] Closed() => Join(Frame('3'), Ready());
    static byte[] Named() => Join(Frame('2'), Description(1, IntOid, 4), Row(["42"]), Complete(), Ready());
    static byte[] Unnamed() => Join(Frame('1'), Named());
    static byte[] Error(string state) => Join(Frame('E', Encoding.ASCII.GetBytes("SERROR\0C" + state + "\0Mprivate-error\0\0")), Ready());
    static PgQuerySession Session(QueryStream stream, int maximum = 2, int minimum = 2) => new(new PgWire(stream), new PgStartupResult(
        new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["standard_conforming_strings"] = "on" }, new PgBackendKey(1, 2)), maximum, minimum);
    static async Task<(IReadOnlyList<PgTextResult> Results, PgPreparedStatement? Statement)> Run(PgQuerySession session, string sql) =>
        await session.ExecuteAutoPreparedAsync(sql, await PgPreparedStatementTests.Types, []);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task DisabledOrUnreachedThresholdKeepsOrdinaryBinding(int maximum)
    {
        var stream = new QueryStream(Join(Unnamed(), Unnamed()));
        using var session = Session(stream, maximum, int.MaxValue);
        Assert.Null((await Run(session, "SELECT 42")).Statement);
        Assert.Null((await Run(session, "SELECT 42")).Statement);
        Assert.Equal("PBDESPBDES", Codes(stream));
        Assert.All(stream.BorrowedWriteBuffers, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task FailedUnnamedExchangeDoesNotAdvanceThreshold()
    {
        var stream = new QueryStream(Join(Error("22012"), Unnamed(), Prepared(), Named()));
        using var session = Session(stream);
        await Assert.ThrowsAsync<PgQueryException>(() => Run(session, "SELECT 42"));
        Assert.Null((await Run(session, "SELECT 42")).Statement);
        Assert.NotNull((await Run(session, "SELECT 42")).Statement);
        Assert.Equal("PBDESPBDESPSBDES", Codes(stream));
    }

    [Fact]
    public async Task AutomaticLruClosesLeastRecentlyExecutedAutomaticHandle()
    {
        var stream = new QueryStream(Join(Prepared(), Named(), Prepared(), Named(), Named(), Closed(), Prepared(), Named()));
        using var session = Session(stream, minimum: 1);
        var first = (await Run(session, "SELECT 1")).Statement!;
        var second = (await Run(session, "SELECT 2")).Statement!;
        Assert.Same(first, (await Run(session, "SELECT 1")).Statement);
        var third = (await Run(session, "SELECT 3")).Statement!;
        Assert.True(first.IsValid); Assert.False(second.IsValid); Assert.True(third.IsValid);
        Assert.Contains(stream.Writes, frame => frame.SequenceEqual(Frame('C', Encoding.ASCII.GetBytes("Skbp_2\0"))));
    }

    [Fact]
    public async Task ExplicitPreparationPromotesAutomaticOwnershipAndUsesOneNameSequence()
    {
        var stream = new QueryStream(Join(Prepared(), Prepared(), Named(), Prepared(), Named(), Closed(), Prepared(), Named()));
        using var session = Session(stream, maximum: 1, minimum: 1);
        var catalog = await PgPreparedStatementTests.Types;
        var explicitFirst = await session.PrepareAsync("SELECT 1", catalog, []);
        var promoted = (await Run(session, "SELECT 2")).Statement!;
        Assert.Same(promoted, await session.PrepareAsync("SELECT 2", catalog, []));
        var automatic = (await Run(session, "SELECT 3")).Statement!;
        var replacement = (await Run(session, "SELECT 4")).Statement!;
        Assert.True(explicitFirst.IsValid); Assert.True(promoted.IsValid); Assert.False(automatic.IsValid);
        Assert.Equal("kbp_1", explicitFirst.Name); Assert.Equal("kbp_2", promoted.Name); Assert.Equal("kbp_4", replacement.Name);
        Assert.Contains(stream.Writes, frame => frame.SequenceEqual(Frame('C', Encoding.ASCII.GetBytes("Skbp_3\0"))));
    }

    [Fact]
    public async Task CandidateCountEvictsOldestUnpreparedSignatureBeforeThreshold()
    {
        var stream = new QueryStream(Join(Join(Enumerable.Range(0, 130).Select(_ => Unnamed()).ToArray()), Prepared(), Named()));
        using var session = Session(stream);
        for (var i = 0; i <= 128; i++) Assert.Null((await Run(session, "SELECT " + i)).Statement);
        Assert.Null((await Run(session, "SELECT 0")).Statement); // Its old usage was evicted.
        Assert.NotNull((await Run(session, "SELECT 0")).Statement);
    }

    [Fact]
    public async Task CandidateByteBudgetEvictsLargeSqlWithoutRetainingItsUsage()
    {
        var stream = new QueryStream(Join(Unnamed(), Unnamed(), Unnamed(), Prepared(), Named()));
        using var session = Session(stream);
        var first = "SELECT 1 /*" + new string('x', 600000) + "*/";
        var second = "SELECT 2 /*" + new string('y', 500000) + "*/";
        Assert.Null((await Run(session, first)).Statement);
        Assert.Null((await Run(session, second)).Statement);
        Assert.Null((await Run(session, first)).Statement);
        Assert.NotNull((await Run(session, first)).Statement);
    }

    [Theory]
    [InlineData("DISCARD ALL")]
    [InlineData("DEALLOCATE ALL")]
    [InlineData("DEALLOCATE")]
    public async Task ResourceClearingTagsDiscardCandidateUsages(string tag)
    {
        var stream = new QueryStream(Join(Unnamed(), Frame('C', Encoding.ASCII.GetBytes(tag + "\0")), Ready(), Unnamed(), Prepared(), Named()));
        using var session = Session(stream);
        Assert.Null((await Run(session, "SELECT 42")).Statement);
        await session.ExecuteAsync(tag);
        Assert.Null((await Run(session, "SELECT 42")).Statement);
        Assert.NotNull((await Run(session, "SELECT 42")).Statement);
    }

    [Theory]
    [InlineData("26000")]
    [InlineData("0A000")]
    public async Task InvalidPlanErrorIsNotReplayedAndRestartsUsageCounting(string state)
    {
        var stream = new QueryStream(Join(Unnamed(), Prepared(), Named(), Error(state), Unnamed(), Closed(), Prepared(), Named()));
        using var session = Session(stream);
        await Run(session, "SELECT 42");
        var old = (await Run(session, "SELECT 42")).Statement!;
        var error = await Assert.ThrowsAsync<PgQueryException>(() => Run(session, "SELECT 42"));
        Assert.Equal(state, error.ServerError.SqlState); Assert.False(old.IsValid);
        Assert.Equal("PBDESPSBDESBDES", Codes(stream));
        Assert.Null((await Run(session, "SELECT 42")).Statement);
        Assert.NotSame(old, (await Run(session, "SELECT 42")).Statement);
    }

    [Fact]
    public async Task SharedUnprepareRestartsCandidateUsageCounting()
    {
        var stream = new QueryStream(Join(Unnamed(), Prepared(), Named(), Closed(), Unnamed(), Prepared(), Named()));
        using var session = Session(stream);
        await Run(session, "SELECT 42");
        var old = (await Run(session, "SELECT 42")).Statement!;
        await session.UnprepareAsync(old);
        Assert.Null((await Run(session, "SELECT 42")).Statement);
        Assert.NotSame(old, (await Run(session, "SELECT 42")).Statement);
    }

    [Fact]
    public async Task ActualOidAndLexicalStatusEachOwnTheirUsageCount()
    {
        var stream = new QueryStream(Join(Unnamed(), Unnamed(), Unnamed(), Prepared(), Named(),
            Status("standard_conforming_strings", "off"), Complete(), Ready(), Unnamed()));
        using var session = Session(stream);
        var catalog = await PgPreparedStatementTests.Types;
        async Task<PgPreparedStatement?> Bind(uint oid, object value) =>
            (await session.ExecuteAutoPreparedAsync("SELECT $1", catalog, [new(oid, value)])).Statement;
        Assert.Null(await Bind(IntOid, 42));
        Assert.Null(await Bind(7104, "42"));
        Assert.Null(await Bind(7103, 42L));
        Assert.NotNull(await Bind(IntOid, 43));
        await session.ExecuteAsync("SET standard_conforming_strings=off");
        Assert.Null(await Bind(IntOid, 44));
    }

    [Fact]
    public async Task LocalEncodingFailurePrecedesEvictionAndPreparation()
    {
        var stream = new QueryStream(Join(Prepared(), Named(), Named()));
        using var session = Session(stream, maximum: 1, minimum: 1);
        var old = (await Run(session, "SELECT 42")).Statement!;
        var writes = stream.Writes.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAutoPreparedAsync("SELECT $1", PgPreparedStatementTests.Types.Result,
            [new(7104, new string('x', 1024 * 1024))]));
        Assert.Equal(writes, stream.Writes.Count); Assert.True(old.IsValid);
        Assert.Same(old, (await Run(session, "SELECT 42")).Statement);
    }

    [Fact]
    public async Task OneGateSpansAutomaticPreparationAndBlocksOtherCacheUsers()
    {
        var stream = new QueryStream([]) { BlockAtEnd = true };
        using var session = Session(stream, minimum: 1);
        using var cancel = new CancellationTokenSource();
        var pending = session.ExecuteAutoPreparedAsync("SELECT 42", await PgPreparedStatementTests.Types, [], cancel.Token);
        Assert.Equal("PS", Codes(stream));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(session, "SELECT 43"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrepareAsync("SELECT 44", PgPreparedStatementTests.Types.Result, []));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(session.IsDisposed); Assert.Equal("PS", Codes(stream));
    }

    static string Codes(QueryStream stream) => new(stream.Writes.Select(frame => (char)frame[0]).ToArray());
}
