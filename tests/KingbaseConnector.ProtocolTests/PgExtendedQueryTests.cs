using System.Net;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgExtendedQueryTests
{
    const uint IntOid = uint.MaxValue;
    const uint TextOid = 7004;
    const uint BytesOid = 7005;
    const uint NumericOid = 7006;
    static readonly Task<PgTypeCatalog> Types = CatalogAsync();

    [Fact]
    public async Task ExactFramesKeepSqlAndValuesSeparateWithActualOidsAndTypedNulls()
    {
        var text = "secret'; SELECT 9; -- 😀";
        var frames = PgExtendedQuery.Encode("SELECT $1,$2,$3,$4,$5", await Types,
            [new(IntOid, 123), new(TextOid, text), new(BytesOid, new byte[] { 0, 255 }), new(IntOid, DBNull.Value),
                new(NumericOid, KingbaseNumeric.Parse("123456789012345678901234567890.00100"))]);
        Assert.Equal(5, frames.Length);
        Assert.Equal(Frame('P', Join([0], Encoding.UTF8.GetBytes("SELECT $1,$2,$3,$4,$5\0"), [0, 5],
            Int32(-1), Int32((int)TextOid), Int32((int)BytesOid), Int32(-1), Int32((int)NumericOid))), frames[0]);
        var expectedValues = new string?[] { "123", text, "\\x00ff", null, "123456789012345678901234567890.00100" };
        Assert.Equal(Frame('B', Join([0, 0, 0, 0, 0, 5], Join(expectedValues.Select(value => value is null ?
            Int32(-1) : Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray()), [0, 0])), frames[1]);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(frames[0]), StringComparison.Ordinal);
        Assert.Equal(new byte[] { (byte)'D', 0, 0, 0, 6, (byte)'P', 0 }, frames[2]);
        Assert.Equal(new byte[] { (byte)'E', 0, 0, 0, 9, 0, 0, 0, 0, 0 }, frames[3]);
        Assert.Equal(new byte[] { (byte)'S', 0, 0, 0, 4 }, frames[4]);
    }

    [Fact]
    public async Task FragmentedExchangeReturnsTypedRawRowsAndClearsOutgoingBuffers()
    {
        var stream = new QueryStream(Success());
        using var session = Session(stream);
        var result = Assert.Single(await session.ExecuteExtendedAsync("SELECT $1", await Types, [new(IntOid, 42)]));
        Assert.Equal("42", Assert.Single(result.Rows)[0]);
        Assert.Equal(IntOid, Assert.Single(result.Columns).TypeOid);
        Assert.Equal(42, new PgTextCodecs(await Types).Decode(result.Columns[0].TypeOid, result.Rows[0][0]));
        Assert.Equal("SELECT 1", result.CommandTag);
        Assert.Equal(5, stream.Writes.Count);
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task SyncNonqueryEmptyQueryAndSimpleExtendedReuseShareOneOwner()
    {
        var stream = new QueryStream(Join(Prefix(), Frame('n'), Complete(), Ready(),
            Complete(), Ready(), Prefix(), Frame('n'), Frame('I'), Ready()));
        using var session = Session(stream);
        var nonquery = Assert.Single(session.ExecuteExtended("UPDATE", await Types, []));
        Assert.Empty(nonquery.Columns);
        Assert.Empty(nonquery.Rows);
        Assert.Single(session.Execute("SELECT simple"));
        var empty = Assert.Single(session.ExecuteExtended("", await Types, []));
        Assert.Null(empty.CommandTag);
        Assert.Equal(11, stream.Writes.Count);
        Assert.Equal(0, stream.DisposeCount);
    }

    public static TheoryData<byte[]> InvalidFlows => new()
    {
        Frame('2'), Frame('1', 0), Join(Frame('1'), Frame('1')), Join(Frame('1'), Frame('2', 0)),
        Description(1), Join(Frame('1'), Description(1)), Join(Prefix(), Frame('n', 0)),
        Join(Prefix(), Frame('n'), Frame('n')), Join(Prefix(), Complete()), Join(Prefix(), Frame('n'), Row(["42"])),
        Join(Prefix(), Description(1), Frame('n')), Join(Prefix(), Frame('n'), Frame('s')),
        Join(Prefix(), Frame('n'), Complete(), Complete()), Join(Prefix(), Frame('n'), Complete(), Description(1)),
        Join(Prefix(), Ready()), Join(Prefix(), Frame('n'), Ready()), Join(Prefix(), Frame('t')),
        Join(Prefix(), Frame('n'), Frame('I', 0)), Join(Prefix(), Description(1), Frame('I')),
        Join(Prefix(), Frame('G'))
    };

    [Theory]
    [MemberData(nameof(InvalidFlows))]
    public async Task InvalidOrUnsupportedPhasesRetireTheSession(byte[] bytes)
    {
        var stream = new QueryStream(Join(bytes, Ready()));
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteExtendedAsync("SELECT $1", Types.Result, [new(IntOid, 1)]));
        Assert.Equal(1, stream.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync("SELECT"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ServerErrorsAtEveryStageDrainAndAllowSimpleOrExtendedReuse(int stage)
    {
        var phases = new[] { Frame('1'), Frame('2'), Description(1, IntOid, 4), Complete() };
        var first = Join(Join(phases.Take(stage).ToArray()), Error(), Status("x", "updated"), Notice(), Ready());
        var stream = new QueryStream(Join(first, Success(), Complete(), Ready()));
        using var session = Session(stream);
        var exception = await Assert.ThrowsAsync<PgQueryException>(() => session.ExecuteExtendedAsync("private-sql", Types.Result, []));
        Assert.DoesNotContain("private", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal("22012", exception.ServerError.SqlState);
        Assert.Equal(first.Length, stream.BytesRead);
        Assert.Equal("updated", session.Parameters["x"]);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Single(await session.ExecuteExtendedAsync("SELECT $1", await Types, [new(IntOid, 42)]));
        Assert.Single(await session.ExecuteAsync("simple"));
        Assert.Equal(11, stream.Writes.Count);
    }

    [Fact]
    public async Task NoticeAndParametersCanInterruptEveryNormalPhase()
    {
        var input = Join(Status("x", "before"), Frame('1'), Notice(), Frame('2'), Notice(),
            Description(1, IntOid, 4), Status("x", "middle"), Row(["42"]), Notice(), Complete(), Status("x", "after"), Ready());
        using var session = Session(new QueryStream(input));
        Assert.Single(await session.ExecuteExtendedAsync("SELECT $1", await Types, [new(IntOid, 42)]));
        Assert.Equal("after", session.Parameters["x"]);
    }

    [Fact]
    public async Task LocalValidationAndPreCancellationPerformNoIoAndPreserveSession()
    {
        var stream = new QueryStream(Success());
        using var session = Session(stream);
        var catalog = await Types;
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.ExecuteExtendedAsync("SELECT", null!, []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.ExecuteExtendedAsync("SELECT", catalog, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteExtendedAsync("bad\0sql", catalog, []));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ExecuteExtendedAsync("SELECT $1", catalog, [new(0, null)]));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ExecuteExtendedAsync("SELECT $1", catalog, [new(1, null)]));
        await Assert.ThrowsAsync<InvalidCastException>(() => session.ExecuteExtendedAsync("SELECT $1", catalog, [new(IntOid, 1L)]));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteExtendedAsync("SELECT", catalog, [null!]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteExtendedAsync("SELECT", catalog, [], cancelled.Token));
        Assert.Empty(stream.Writes);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Single(await session.ExecuteExtendedAsync("SELECT $1", catalog, [new(IntOid, 42)]));
    }

    [Fact]
    public async Task AggregateOutboundAndParameterBoundsAreCheckedBeforeAnyWrite()
    {
        var catalog = await Types;
        var limitSql = new string(' ', 1024 * 1024 - 44);
        var exact = PgExtendedQuery.Encode(limitSql, catalog, []);
        Assert.Equal(1024 * 1024, exact.Sum(frame => frame.Length));
        var stream = new QueryStream(Success());
        using var session = Session(stream);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteExtendedAsync(limitSql + "x", catalog, []));
        var parameters = Enumerable.Range(0, 1025).Select(_ => new PgQueryParameter(IntOid, null)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteExtendedAsync("SELECT", catalog, parameters));
        Assert.Equal(5, PgExtendedQuery.Encode("SELECT", catalog, parameters[..1024]).Length);
        var unicode = new string('界', 180000);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteExtendedAsync("SELECT $1,$2", catalog,
            [new(TextOid, unicode), new(TextOid, unicode)]));
        Assert.Empty(stream.Writes);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWritesAndIncompleteErrorDrainingRetireAndClearFrames(bool failWrite)
    {
        var stream = new QueryStream(Error()) { FailWrite = failWrite };
        using var session = Session(stream);
        await Assert.ThrowsAnyAsync<IOException>(() => session.ExecuteExtendedAsync("SELECT $1", Types.Result, [new(TextOid, "secret")]));
        Assert.Equal(1, stream.DisposeCount);
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    public async Task OnlyExecutionCapableWriteAttemptsImplyUncertainSqlOutcome(int failure, long attempts)
    {
        var stream = new QueryStream([]) { FailWriteNumber = failure };
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteExtendedAsync("SELECT $1", Types.Result, [new(IntOid, 42)]));
        Assert.Equal(attempts, session.ExecutionWriteAttempts);
    }

    [Fact]
    public async Task FailedBindWriteClearsParameterBufferAndDoesNotSendLaterFrames()
    {
        var stream = new QueryStream([]) { FailWriteNumber = 2 };
        using var session = Session(stream);
        await Assert.ThrowsAsync<IOException>(() => session.ExecuteExtendedAsync("SELECT $1", Types.Result, [new(TextOid, "private-value")]));
        Assert.Equal(2, stream.Writes.Count);
        Assert.Contains("private-value", Encoding.UTF8.GetString(stream.Writes[1]), StringComparison.Ordinal);
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task ActiveExchangeRejectsBothKindsAndCancellationRetiresWithoutReplay()
    {
        var stream = new QueryStream(Prefix()) { BlockAtEnd = true };
        using var session = Session(stream);
        using var cancellation = new CancellationTokenSource();
        var active = session.ExecuteExtendedAsync("SELECT $1", await Types, [new(IntOid, 42)], cancellation.Token);
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("simple"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteExtendedAsync("SELECT", Types.Result, []));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(5, stream.Writes.Count);
        Assert.All(stream.BorrowedWriteBuffers, bytes => Assert.All(bytes, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task TcpPhysicalOwnerRunsCatalogBoundExtendedExchangeOnTheSameConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = socket.GetStream();
            await ReadStartupAsync(stream, timeout.Token);
            await stream.WriteAsync(StartupResponse(), timeout.Token);
            Assert.Equal("SELECT pg_catalog.version();", await ReadQueryAsync(stream, timeout.Token));
            await stream.WriteAsync(Scalar("KingbaseES V009R001C001B0001"), timeout.Token);
            Assert.Contains("sys_catalog.sys_type", await ReadQueryAsync(stream, timeout.Token), StringComparison.Ordinal);
            await stream.WriteAsync(Catalog(Definitions()), timeout.Token);
            foreach (var code in new[] { 'P', 'B', 'D', 'E', 'S' })
            {
                var header = new byte[5];
                await stream.ReadExactlyAsync(header, timeout.Token);
                Assert.Equal((byte)code, header[0]);
                var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
                Assert.InRange(length, 4, 1024);
                var payload = new byte[length - 4];
                await stream.ReadExactlyAsync(payload, timeout.Token);
                if (code == 'P') Assert.DoesNotContain("private-value", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
                if (code == 'B') Assert.Contains("private-value", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
            }
            await stream.WriteAsync(Success(), timeout.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
        }, timeout.Token);
        using (var physical = await PgPhysicalSession.OpenAsync($"Host=127.0.0.1;Port={endpoint.Port};Database=test;Username=user;SslMode=Disable", timeout.Token))
        {
            var result = Assert.Single(await physical.Queries.ExecuteExtendedAsync("SELECT $1", physical.Catalog,
                [new(TextOid, "private-value")], timeout.Token));
            Assert.Equal("42", result.Rows[0][0]);
        }
        await peer;
    }

    static byte[] Prefix() => Join(Frame('1'), Frame('2'));
    static byte[] Success() => Join(Prefix(), Description(1, IntOid, 4), Row(["42"]), Complete(), Ready());
    static byte[] Error() => Frame('E', Encoding.ASCII.GetBytes("SERROR\0C22012\0Mprivate-value\0\0"));
    static byte[] Notice() => Frame('N', Encoding.ASCII.GetBytes("SNOTICE\0C00000\0Mprivate-notice\0\0"));
    static PgQuerySession Session(QueryStream stream) => new(new PgWire(stream), new PgStartupResult(
        new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
    static async Task<PgTypeCatalog> CatalogAsync()
    {
        using var session = Session(new QueryStream(Catalog(Definitions())));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql,
            KingbaseCompatibilityMode.PostgreSql, null, null));
    }
    static IEnumerable<PgTypeDescription> Definitions() => new (string Name, short Size)[] { ("bool", 1), ("int2", 2), ("int4", 4),
        ("int8", 8), ("text", -1), ("bytea", -1), ("numeric", -1) }.Select((type, i) => new PgTypeDescription(
            type.Name == "int4" ? IntOid : (uint)(7000 + i), 90, type.Name, type.Size, 'b', 'U', 0, 0, 0, -1));
}
