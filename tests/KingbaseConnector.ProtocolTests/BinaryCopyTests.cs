using System.Data;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class BinaryCopyTests
{
    [Theory]
    [InlineData("bool", KingbaseDbType.Boolean)]
    [InlineData("int2", KingbaseDbType.Smallint)]
    [InlineData("int4", KingbaseDbType.Integer)]
    [InlineData("int8", KingbaseDbType.Bigint)]
    [InlineData("text", KingbaseDbType.Text)]
    [InlineData("bytea", KingbaseDbType.Bytea)]
    public async Task UnsupportedCatalogLayoutsCannotSelectBinaryCodecs(string name, KingbaseDbType selector)
    {
        var rows = NativeScalarTests.BaseRows();
        rows.Single(row => row[3] == name)[4] = "3";
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql, baseRows: rows);
        Assert.Throws<NotSupportedException>(() => PgBinaryCopyCodec.Resolve(catalog, [selector]));
    }

    [Fact]
    public async Task PublicTypedRowsMatchLiteralHeaderAndAcknowledgeServerTail()
    {
        byte[] header = [75,66,67,79,80,89,10,255,13,10,0,0,0,0,0,0,0,0,0];
        var expected = Join(header, [0,2], Int32(4), [128,0,0,0], Int32(3), [231,149,140],
            [0,2], Int32(-1), Int32(0), [255,255]);
        await Run(async (connection, token) =>
        {
            using (var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token))
            {
                await input.WriteRowAsync(new object?[] { int.MinValue, "界" }, token);
                await input.WriteRowAsync(new object?[] { DBNull.Value, "" }, token);
                Assert.Null((ulong?)input.RowsCopied); Assert.Equal(2UL, (ulong)await input.CompleteAsync(token));
            }
            using (var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token))
            {
                Assert.Equal(new object[] { int.MinValue, "界" }, await output.ReadRowAsync(token));
                Assert.Equal(new object[] { DBNull.Value, "" }, await output.ReadRowAsync(token));
                Assert.Null((object?)await output.ReadRowAsync(token)); Assert.Equal(2UL, (ulong?)output.RowsCopied);
            }
            using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await next.ExecuteScalarAsync(token));
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token);
            using var data = new MemoryStream();
            while (true) { var frame = await Frontend(stream, token); if (frame.Code == 'c') break; Assert.Equal('d', frame.Code); data.Write(frame.Payload); }
            Assert.Equal(expected, data.ToArray()); await stream.WriteAsync(Tail(2), token);
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, true), token);
            foreach (var b in expected) await stream.WriteAsync(Frame('d', [b]), token);
            await stream.WriteAsync(Join(Frame('c'), Tail(2)), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
        });
    }
    [Fact]
    public void BinaryCopyCoreEncodesAndDecodesLiteralNetworkValues()
    {
        var cases = new (string Name, object Value, byte[] Bytes)[] {
            ("bool", true, [1]), ("int2", (short)-32768, [128,0]),
            ("int4", -2147483648, [128,0,0,0]), ("int8", long.MinValue, [128,0,0,0,0,0,0,0]),
            ("oid", uint.MaxValue, [255,255,255,255]), ("float4", 1.5f, [63,192,0,0]),
            ("float8", -2.5d, [192,4,0,0,0,0,0,0]),
            ("text", "界😀", [231,149,140,240,159,152,128]), ("bytea", new byte[] {0,255}, [0,255]),
            ("uuid", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), [0,17,34,51,68,85,102,119,136,153,170,187,204,221,238,255])
        };
        foreach (var item in cases)
        {
            Assert.Equal(item.Bytes, PgBinaryCopyCodec.Encode(item.Name, item.Value, 1048576));
            var actual = PgBinaryCopyCodec.Decode(item.Name, item.Bytes);
            if (item.Value is byte[] bytes) Assert.Equal(bytes, Assert.IsType<byte[]>(actual)); else Assert.Equal(item.Value, actual);
        }
        Assert.Null(PgBinaryCopyCodec.Encode("text", DBNull.Value, 1048576));
        Assert.Equal(Array.Empty<byte>(), PgBinaryCopyCodec.Encode("text", "", 1048576));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("critical")]
    [InlineData("oid-flag")]
    [InlineData("extension")]
    [InlineData("missing-trailer")]
    [InlineData("missing-field")]
    [InlineData("row-count")]
    [InlineData("negative-length")]
    [InlineData("huge-length")]
    [InlineData("bad-width")]
    [InlineData("bad-unicode")]
    [InlineData("extra-data")]
    [InlineData("tail-count")]
    public async Task MalformedTypedExportRetiresBeforeItCanReleaseItsConnection(string fault)
    {
        var header = BinaryHeader(); var trailer = new byte[] { 255,255 };
        var payload = fault switch {
            "missing-trailer" => header,
            "missing-field" => Join(header, [0,2], Int32(4), [0]),
            "row-count" => Join(header, [0,1], trailer),
            "negative-length" => Join(header, [0,2], Int32(-2)),
            "huge-length" => Join(header, [0,2], Int32(int.MaxValue)),
            "bad-width" => Join(header, [0,2], Int32(1), [1]),
            "bad-unicode" => Join(header, [0,2], Int32(4), Int32(42), Int32(1), [255]),
            "extra-data" => Join(header, trailer, [0]),
            _ => Join(header, trailer) };
        if (fault == "signature") payload[0] = 0;
        if (fault == "critical") payload[11] = 1;
        if (fault == "oid-flag") payload[12] = 1;
        if (fault == "extension") payload[15] = 1;
        await Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token);
                while (await output.ReadRowAsync(token) is not null) { }
            });
            Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d', payload), Frame('c'), Tail(fault == "tail-count" ? 1UL : 0UL)), token);
        });
    }

    [Fact]
    public Task InvalidOutgoingRowsAndPreCancelledCallsDoNotWritePartialRows() => Run(async (connection, token) =>
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new[] { KingbaseDbType.Numeric }, token));
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new[] { KingbaseDbType.Uuid }, token));
        using var input = connection.BeginBinaryImport("COPY items FROM STDIN (FORMAT BINARY)", KingbaseDbType.Integer, KingbaseDbType.Text);
        Assert.Throws<ArgumentException>(() => input.WriteRow(42));
        Assert.Throws<InvalidCastException>(() => input.WriteRow(42L, "text"));
        Assert.Throws<InvalidCastException>(() => input.WriteRow(42, "bad\0text"));
        Assert.Throws<System.Text.EncoderFallbackException>(() => input.WriteRow(42, "\ud800"));
        Assert.Throws<ArgumentException>(() => input.WriteRow(42, new string('x', 1024 * 1024)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input.WriteRowAsync([42, "cancelled"], cancelled.Token));
        input.WriteRow(42, "ok"); Assert.Equal(1UL, input.Complete());
        Assert.Equal(1UL, input.Complete()); using var next = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, next.ExecuteScalar());
    }, async (stream, token) =>
    {
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token);
        Assert.Equal(Join(BinaryHeader(), [0,2], Int32(4), Int32(42), Int32(2), [111,107], [255,255]), await ReadImport(stream, token));
        await stream.WriteAsync(Tail(1), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    });

    [Fact]
    public Task NoncriticalFlagsAndBoundedExtensionsAreSkippedWithoutHidingEof() => Run(async (connection, token) =>
    {
        using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token);
        Assert.Null(output.ReadRow()); Assert.Equal(0UL, output.RowsCopied); connection.Close(); Assert.Null(await output.ReadRowAsync(token));
    }, async (stream, token) =>
    {
        var header = BinaryHeader(); header[14] = 1; header[18] = 3;
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d', Join(header, [7,8,9,255,255])), Frame('c'), Tail(0)), token);
    });

    internal static byte[] BinaryHeader() => [75,66,67,79,80,89,10,255,13,10,0,0,0,0,0,0,0,0,0];
    internal static async Task<byte[]> ReadImport(Stream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        while (true) { var frame = await Frontend(stream, token); if (frame.Code == 'c') return data.ToArray(); Assert.Equal('d', frame.Code); data.Write(frame.Payload); }
    }
}
