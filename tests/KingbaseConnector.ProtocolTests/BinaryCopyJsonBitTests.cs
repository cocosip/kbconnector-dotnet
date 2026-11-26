using System.Collections;
using System.Data;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.BinaryCopyTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class BinaryCopyJsonBitTests
{
    public static IEnumerable<object[]> Literals()
    {
        yield return ["json", "{ \"x\": 1.00 }", "7B202278223A20312E3030207D"];
        yield return ["jsonb", "{\"x\": 1}", "017B2278223A20317D"];
        yield return ["jsonb", "\"界😀\"", "0122E7958CF09F988022"];
        yield return ["json", "", ""];
        yield return ["jsonb", "", "01"];
        foreach (var name in new[] {"bit", "varbit"})
        {
            yield return [name, Bits(""), "00000000"];
            yield return [name, Bits("0"), "0000000100"];
            yield return [name, Bits("1"), "0000000180"];
            yield return [name, Bits("01001011"), "000000084B"];
            yield return [name, Bits("001011001"), "000000092C80"];
        }
    }

    [Theory]
    [MemberData(nameof(Literals))]
    public void EncodeAndDecodeMatchIndependentNetworkLiterals(string name, object value, string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(bytes, PgBinaryCopyCodec.Encode(name, value, 1024));
        EqualValue(value, PgBinaryCopyCodec.Decode(name, bytes));
        Assert.Null(PgBinaryCopyCodec.Encode(name, DBNull.Value, 0));
        Assert.Null(PgBinaryCopyCodec.Encode(name, null, 0));
        if (bytes.Length > 0) Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode(name, value, bytes.Length - 1));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("jsonb")]
    public void JsonUsesExactStringAndStrictUnicodeWithoutParsingSyntax(string name)
    {
        Assert.Throws<InvalidCastException>(() => PgBinaryCopyCodec.Encode(name, 42, 100));
        Assert.Throws<InvalidCastException>(() => PgBinaryCopyCodec.Encode(name, "x\0y", 100));
        Assert.Throws<EncoderFallbackException>(() => PgBinaryCopyCodec.Encode(name, "\ud800", 100));
        EqualValue("invalid json", PgBinaryCopyCodec.Decode(name, PgBinaryCopyCodec.Encode(name, "invalid json", 100)!));
        var limit = new string('x', PgBinaryCopyCodec.MaxRowBytes - (name == "jsonb" ? 1 : 0));
        Assert.Equal(PgBinaryCopyCodec.MaxRowBytes, PgBinaryCopyCodec.Encode(name, limit, PgBinaryCopyCodec.MaxRowBytes)!.Length);
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode(name, limit + "x", PgBinaryCopyCodec.MaxRowBytes));
    }

    [Theory]
    [InlineData("json", "FF")]
    [InlineData("json", "00")]
    [InlineData("jsonb", "")]
    [InlineData("jsonb", "00")]
    [InlineData("jsonb", "02")]
    [InlineData("jsonb", "01FF")]
    [InlineData("jsonb", "0100")]
    [InlineData("bit", "")]
    [InlineData("bit", "FFFFFFFF")]
    [InlineData("bit", "7FFFFFFF")]
    [InlineData("bit", "0000000101")]
    [InlineData("bit", "000000080000")]
    [InlineData("varbit", "0000000100FF")]
    [InlineData("varbit", "000000090001")]
    public void MalformedInputRejectsWithoutReturningPartialValues(string name, string hex) =>
        Assert.Throws<IOException>(() => PgBinaryCopyCodec.Decode(name, Convert.FromHexString(hex)));

    [Theory]
    [InlineData("bit")]
    [InlineData("varbit")]
    public void BitLengthAndSnapshotsStayBounded(string name)
    {
        var bits = new BitArray(PgBinaryCopyCodec.MaxRowBytes); bits[bits.Length - 1] = true;
        var bytes = PgBinaryCopyCodec.Encode(name, bits, PgBinaryCopyCodec.MaxRowBytes)!;
        bits[bits.Length - 1] = false;
        var output = Assert.IsType<BitArray>(PgBinaryCopyCodec.Decode(name, bytes));
        Assert.Equal(bits.Length, output.Length); Assert.True(output[output.Length - 1]); Assert.False(output[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => PgBinaryCopyCodec.Encode(name, new BitArray(PgBinaryCopyCodec.MaxRowBytes + 1), PgBinaryCopyCodec.MaxRowBytes));
        Assert.Throws<InvalidCastException>(() => PgBinaryCopyCodec.Encode(name, new byte[] {1}, 10));
        var oversized = new byte[4 + (PgBinaryCopyCodec.MaxRowBytes + 8) / 8];
        Int32(PgBinaryCopyCodec.MaxRowBytes + 1).CopyTo(oversized, 0);
        Assert.Throws<OverflowException>(() => PgBinaryCopyCodec.Decode(name, oversized));
        oversized[oversized.Length - 1] = 1;
        Assert.Throws<IOException>(() => PgBinaryCopyCodec.Decode(name, oversized));
    }

    public static IEnumerable<object[]> CatalogCases() => new[] {
        ("json", KingbaseDbType.Json), ("jsonb", KingbaseDbType.Jsonb), ("bit", KingbaseDbType.Bit), ("varbit", KingbaseDbType.Varbit) }
        .SelectMany(type => (type.Item1 is "bit" or "varbit" ? new[] { "valid", "missing", "width", "kind", "base", "element", "category" } :
            new[] { "valid", "missing", "width", "kind", "base", "element" }).Select(fault => new object[] {type.Item1, type.Item2, fault}));

    [Theory]
    [MemberData(nameof(CatalogCases))]
    public async Task ActualQualifiedCatalogBaseLayoutIsRequired(string name, KingbaseDbType selector, string fault)
    {
        var rows = NativeScalarTests.BaseRows();
        string?[] row = ["17000", "90", "pg_catalog", name, "-1", "b", name is "bit" or "varbit" ? "V" : "U", "t", "0", "0", "0", "-1"];
        if (fault == "width") row[4] = "4";
        if (fault == "kind") row[5] = "d";
        if (fault == "base") row[8] = "10002";
        if (fault == "element") row[9] = "10002";
        if (fault == "category") row[6] = "U";
        if (fault != "missing") rows.Add(row);
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql, baseRows: rows);
        if (fault == "valid") Assert.Equal(new[] {name}, PgBinaryCopyCodec.Resolve(catalog, [selector]));
        else Assert.Throws<NotSupportedException>(() => PgBinaryCopyCodec.Resolve(catalog, [selector]));
    }

    [Fact]
    public Task PublicRowsPreserveLiteralsAndUnsentInvalidRowsAcrossFragmentedCopy() => Run(async (connection, token) =>
    {
        KingbaseDbType[] types = [KingbaseDbType.Json, KingbaseDbType.Jsonb, KingbaseDbType.Bit, KingbaseDbType.Varbit];
        var bits = Bits("001011001");
        using (var input = await connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", types, token))
        {
            Assert.Throws<InvalidCastException>(() => input.WriteRow("{}", "\0", bits, bits));
            Assert.Throws<InvalidCastException>(() => input.WriteRow("{}", "[]", bits, "010"));
            input.WriteRow("{}", "[]", bits, Bits("")); bits.SetAll(true);
            await input.WriteRowAsync([DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value], token);
            Assert.Equal(2UL, await input.CompleteAsync(token));
        }
        using (var output = connection.BeginBinaryExport("COPY items TO STDOUT (FORMAT BINARY)", types))
        {
            var row = (await output.ReadRowAsync(token))!;
            Assert.Equal("{}", row[0]); Assert.Equal("[]", row[1]); EqualValue(Bits("001011001"), row[2]); EqualValue(Bits(""), row[3]);
            Assert.All(output.ReadRow()!, value => Assert.IsType<DBNull>(value));
            Assert.Null(output.ReadRow()); Assert.Equal(2UL, output.RowsCopied);
        }
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
    }, async (stream, token) =>
    {
        var literal = Join(BinaryHeader(), [0,4], Int32(2), [123,125], Int32(3), [1,91,93], Int32(6), [0,0,0,9,44,128], Int32(4), [0,0,0,0],
            [0,4], Int32(-1), Int32(-1), Int32(-1), Int32(-1), [255,255]);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true, 4), token);
        Assert.Equal(literal, await ReadImport(stream, token)); await stream.WriteAsync(Tail(2), token);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, true, 4), token);
        foreach (var b in literal) await stream.WriteAsync(Frame('d', [b]), token);
        await stream.WriteAsync(Join(Frame('c'), Tail(2)), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    }, catalogTypes: CatalogTypes());

    [Theory]
    [InlineData(KingbaseDbType.Json, "FF")]
    [InlineData(KingbaseDbType.Jsonb, "027B7D")]
    [InlineData(KingbaseDbType.Bit, "0000000101")]
    [InlineData(KingbaseDbType.Varbit, "7FFFFFFF")]
    public Task MalformedLaterRowRetiresExportAndKeepsUnknownCount(KingbaseDbType type, string hex) => Run(async (connection, token) =>
    {
        using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", [type], token);
        Assert.IsType<DBNull>(output.ReadRow()![0]);
        await Assert.ThrowsAsync<IOException>(() => output.ReadRowAsync(token));
        Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(output.RowsCopied);
    }, async (stream, token) =>
    {
        var bad = Convert.FromHexString(hex); await ReadQueryAsync(stream, token);
        await stream.WriteAsync(Join(Response(false, true, 1), Frame('d', Join(BinaryHeader(), [0,1], Int32(-1), [0,1], Int32(bad.Length), bad, [255,255])), Frame('c'), Tail(2)), token);
    }, catalogTypes: CatalogTypes());

    [Theory]
    [InlineData(KingbaseDbType.Bit, false)]
    [InlineData(KingbaseDbType.Varbit, false)]
    [InlineData(KingbaseDbType.Bit, true)]
    [InlineData(KingbaseDbType.Varbit, true)]
    public Task OversizedBitValuesClassifyMalformedPaddingAndRetireBeforeAllocation(KingbaseDbType type, bool malformed) => Run(async (connection, token) =>
    {
        using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", [type], token);
        Assert.IsType<DBNull>(output.ReadRow()![0]);
        if (malformed) await Assert.ThrowsAsync<IOException>(() => output.ReadRowAsync(token));
        else await Assert.ThrowsAsync<OverflowException>(() => output.ReadRowAsync(token));
        Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(output.RowsCopied);
    }, async (stream, token) =>
    {
        var bytes = new byte[4 + (PgBinaryCopyCodec.MaxRowBytes + 8) / 8];
        Int32(PgBinaryCopyCodec.MaxRowBytes + 1).CopyTo(bytes, 0);
        if (malformed) bytes[bytes.Length - 1] = 1;
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true, 1), Frame('d', Join(BinaryHeader(),
            [0,1], Int32(-1), [0,1], Int32(bytes.Length), bytes, [255,255])), Frame('c'), Tail(2)), token);
    }, catalogTypes: CatalogTypes());

    static IEnumerable<PgTypeDescription> CatalogTypes() => new[] { ("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1),
        ("json", -1), ("jsonb", -1), ("bit", -1), ("varbit", -1) }.Select((t, i) => new PgTypeDescription(i == 2 ? uint.MaxValue : (uint)(17000 + i), 90, t.Item1, (short)t.Item2, 'b', t.Item1 is "bit" or "varbit" ? 'V' : 'U', 0, 0, 0, -1));

    internal static BitArray Bits(string value) => new(value.Select(c => c == '1').ToArray());
    internal static void EqualValue(object expected, object actual)
    {
        if (expected is BitArray bits) Assert.Equal(bits.Cast<bool>().ToArray(), Assert.IsType<BitArray>(actual).Cast<bool>().ToArray());
        else Assert.Equal(expected, actual);
    }
}
