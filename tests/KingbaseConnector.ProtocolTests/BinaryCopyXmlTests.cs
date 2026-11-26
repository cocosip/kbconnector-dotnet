using System.Data;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.BinaryCopyTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class BinaryCopyXmlTests
{
    public static IEnumerable<object[]> Inputs()
    {
        yield return ["<x>界😀é</x>", "<x>界😀é</x>"];
        yield return ["", ""];
        yield return ["\ufeff<x/>", "<x/>"];
        yield return ["<x/>text<y/>", "<x/>text<y/>"];
        yield return ["<?xml version='1.0'?>\n<x/>", "<?xml version='1.0'?>\n<x/>"];
        foreach (var encoding in new[] { "UTF-8", "UTF-16", "ISO-8859-1", "unknown-encoding" })
        foreach (var quote in new[] { "'", "\"" })
        {
            var prefix = $"<?xml version={quote}1.0{quote}";
            yield return [$"{prefix} encoding={quote}{encoding}{quote} standalone='yes'?><x>界é</x>", $"{prefix} standalone='yes'?><x>界é</x>"];
        }
        yield return ["\ufeff<?xml\tversion = '1.1'\r\nencoding \t= \"UTF-16\" \tstandalone = \"no\" \t?><x/>", "<?xml\tversion = '1.1' \tstandalone = \"no\" \t?><x/>"];
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void InputUsesUnicodeAndNormalizesOnlyEncodingMetadata(string input, string normalized)
    {
        var bytes = Encoding.UTF8.GetBytes(normalized);
        Assert.Equal(bytes, PgBinaryCopyCodec.Encode("xml", input, bytes.Length));
        Assert.Equal(normalized, PgBinaryCopyCodec.Decode("xml", bytes));
        if (bytes.Length > 0) Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("xml", input, bytes.Length - 1));
        Assert.Null(PgBinaryCopyCodec.Encode("xml", null, 0));
        Assert.Null(PgBinaryCopyCodec.Encode("xml", DBNull.Value, 0));
    }

    [Theory]
    [InlineData("<?xml encoding='UTF-16'?><x/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-16\"?><x/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-16' extra='x'?><x/>")]
    [InlineData("<?xml version='1.0'encoding='UTF-16'?><x/>")]
    [InlineData("<?xml version='1.0' encoding='UTF 16'?><x/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-16' encoding='UTF-8'?><x/>")]
    [InlineData(" <?xml version='1.0' encoding='UTF-16'?><x/>")]
    [InlineData("<x a=\"encoding='UTF-16'\"/>")]
    [InlineData("<?xml-stylesheet encoding='UTF-16'?><x/>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///not-read'>]><x>&e;</x>")]
    [InlineData("<?xml version='1.0' standalone='yes' encoding='UTF-16'?><x/>")]
    public void BodyAndMalformedDeclarationsStayServerOwned(string text) =>
        Assert.Equal(Encoding.UTF8.GetBytes(text), PgBinaryCopyCodec.Encode("xml", text, 4096));

    [Fact]
    public void ExactStringUnicodeAndNormalizedRowBudgetAreRequired()
    {
        Assert.Throws<InvalidCastException>(() => PgBinaryCopyCodec.Encode("xml", 42, 10));
        Assert.Throws<InvalidCastException>(() => PgBinaryCopyCodec.Encode("xml", "x\0y", 10));
        Assert.Throws<EncoderFallbackException>(() => PgBinaryCopyCodec.Encode("xml", "\ud800", 10));
        var limit = new string('x', PgBinaryCopyCodec.MaxRowBytes);
        Assert.Equal(PgBinaryCopyCodec.MaxRowBytes, PgBinaryCopyCodec.Encode("xml", "\ufeff" + limit, PgBinaryCopyCodec.MaxRowBytes)!.Length);
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("xml", limit + "x", PgBinaryCopyCodec.MaxRowBytes));
    }

    [Theory]
    [InlineData("\ufeff")]
    [InlineData("<?xml version='1.0' encoding='UTF-16'?>")]
    [InlineData("\ufeff<?xml version='1.0' encoding='UTF-16'?>")]
    public void RejectedOversizedInputDoesNotAllocateNormalizedCopies(string prefix)
    {
        var text = prefix + new string('x', 8 * PgBinaryCopyCodec.MaxRowBytes);
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("xml", text, 1));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("xml", text, 1));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536);
    }

    [Theory]
    [InlineData("\ufeff\ufeff<x/>", "\ufeff<x/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-16' standalone='maybe'?><x/>", "<?xml version='1.0' encoding='UTF-16' standalone='maybe'?><x/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-16'", "<?xml version='1.0' encoding='UTF-16'")]
    public void NormalizationDoesNotRepairOtherInput(string input, string expected) =>
        Assert.Equal(Encoding.UTF8.GetBytes(expected), PgBinaryCopyCodec.Encode("xml", input, 4096));

    [Theory]
    [InlineData("FF")]
    [InlineData("00")]
    [InlineData("3C783EEDA0803C2F783E")]
    public void MalformedUtf8AndNulNeverBecomeXmlStrings(string hex) =>
        Assert.Throws<IOException>(() => PgBinaryCopyCodec.Decode("xml", Convert.FromHexString(hex)));

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("width")]
    [InlineData("kind")]
    [InlineData("base")]
    [InlineData("element")]
    public async Task QualifiedCatalogBaseLayoutIsRequired(string fault)
    {
        var rows = NativeScalarTests.BaseRows();
        string?[] row = ["17000", "90", "pg_catalog", "xml", "-1", "b", "U", "t", "0", "0", "0", "-1"];
        if (fault == "width") row[4] = "4";
        if (fault == "kind") row[5] = "d";
        if (fault == "base") row[8] = "10002";
        if (fault == "element") row[9] = "10002";
        if (fault != "missing") rows.Add(row);
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql, baseRows: rows);
        if (fault == "valid") Assert.Equal(new[] {"xml"}, PgBinaryCopyCodec.Resolve(catalog, [KingbaseDbType.Xml]));
        else Assert.Throws<NotSupportedException>(() => PgBinaryCopyCodec.Resolve(catalog, [KingbaseDbType.Xml]));
    }

    [Fact]
    public Task PublicFragmentedRowsValidateBeforeDispatchAndPreserveNullAndEmpty() => Run(async (connection, token) =>
    {
        using (var input = connection.BeginBinaryImport("COPY items FROM STDIN (FORMAT BINARY)", KingbaseDbType.Xml))
        {
            Assert.Throws<InvalidCastException>(() => input.WriteRow("\0"));
            Assert.Throws<InvalidCastException>(() => input.WriteRow(42));
            input.WriteRow("\ufeff<?xml version='1.0' encoding='UTF-16'?><x>界</x>");
            await input.WriteRowAsync([""], token); input.WriteRow(DBNull.Value); Assert.Equal(3UL, await input.CompleteAsync(token));
        }
        using (var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", [KingbaseDbType.Xml], token))
        {
            Assert.Equal("<?xml version='1.0'?><x>界</x>", output.ReadRow()![0]);
            Assert.Equal("", (await output.ReadRowAsync(token))![0]); Assert.IsType<DBNull>(output.ReadRow()![0]);
            Assert.Null(output.ReadRow()); Assert.Equal(3UL, output.RowsCopied);
        }
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
    }, async (stream, token) =>
    {
        var bytes = Encoding.UTF8.GetBytes("<?xml version='1.0'?><x>界</x>");
        var literal = Join(BinaryHeader(), [0,1], Int32(bytes.Length), bytes, [0,1], Int32(0), [0,1], Int32(-1), [255,255]);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true, 1), token);
        Assert.Equal(literal, await ReadImport(stream, token)); await stream.WriteAsync(Tail(3), token);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, true, 1), token);
        foreach (var b in literal) await stream.WriteAsync(Frame('d', [b]), token);
        await stream.WriteAsync(Join(Frame('c'), Tail(3)), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    }, catalogTypes: CatalogTypes());

    [Theory]
    [InlineData("FF")]
    [InlineData("00")]
    public Task MalformedLaterRowRetiresUnfinishedExport(string hex) => Run(async (connection, token) =>
    {
        using var output = connection.BeginBinaryExport("COPY items TO STDOUT (FORMAT BINARY)", KingbaseDbType.Xml);
        Assert.IsType<DBNull>(output.ReadRow()![0]); await Assert.ThrowsAsync<IOException>(() => output.ReadRowAsync(token));
        Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(output.RowsCopied);
    }, async (stream, token) =>
    {
        var bad = Convert.FromHexString(hex); await ReadQueryAsync(stream, token);
        await stream.WriteAsync(Join(Response(false, true, 1), Frame('d', Join(BinaryHeader(), [0,1], Int32(-1), [0,1], Int32(bad.Length), bad, [255,255])), Frame('c'), Tail(2)), token);
    }, catalogTypes: CatalogTypes());

    static IEnumerable<PgTypeDescription> CatalogTypes() => new[] { ("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1), ("xml", -1) }
        .Select((t, i) => new PgTypeDescription(i == 2 ? uint.MaxValue : (uint)(17000 + i), 90, t.Item1, (short)t.Item2, 'b', 'U', 0, 0, 0, -1));
}
