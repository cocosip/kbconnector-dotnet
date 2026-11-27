using System.Globalization;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

// T27: catalogued types without a codec keep the explicit text path; binary formats and uncatalogued OIDs stay rejected.
public sealed class UnknownTypeTests
{
    [Fact]
    public async Task CataloguedTypesWithoutCodecsUseTheExplicitTextPath()
    {
        var catalog = await Load(namespaceOid: "90", Row(600, "90", "pg_catalog", "point", "16", "b", "G"));
        var codecs = new PgTextCodecs(catalog);
        Assert.Equal(typeof(string), codecs.GetFieldType(600));
        Assert.Equal(typeof(string), codecs.GetProviderSpecificFieldType(600));
        Assert.Equal("(1,2)", codecs.Decode(600, "(1,2)"));
        Assert.Same(DBNull.Value, codecs.Decode(600, null));
        Assert.Equal("(1,2)", codecs.Encode(600, "(1,2)"));
        Assert.Throws<FormatException>(() => codecs.Decode(600, "(1,\0)"));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(600, 1));
    }

    [Fact]
    public async Task DomainsUseTheirCataloguedBaseCodec()
    {
        var catalog = await Load(namespaceOid: "90",
            Row(700, "90", "pg_catalog", "posint", "4", "d", "N", baseTypeOid: 10002),
            Row(701, "90", "pg_catalog", "pointdom", "16", "d", "G", baseTypeOid: 600),
            Row(702, "90", "pg_catalog", "orphandom", "4", "d", "N", baseTypeOid: 99999),
            Row(600, "90", "pg_catalog", "point", "16", "b", "G"));
        var codecs = new PgTextCodecs(catalog);
        // int4 shares its base codec, so the domain keeps the integer contract instead of degrading to text.
        Assert.Equal(typeof(int), codecs.GetFieldType(700));
        var decoded = codecs.Decode(700, "42");
        Assert.IsType<int>(decoded);
        Assert.Equal(42, (int)decoded);
        Assert.Equal("42", codecs.Encode(700, 42));
        Assert.Equal(typeof(string), codecs.GetFieldType(701));
        Assert.Equal("(1,2)", codecs.Decode(701, "(1,2)"));
        // A domain whose base is absent from the catalog cannot be interpreted safely.
        Assert.Throws<NotSupportedException>(() => codecs.GetFieldType(702));
    }

    [Fact]
    public async Task ArraysAndUncataloguedOidsStayUnsupported()
    {
        var catalog = await Load(namespaceOid: "90",
            Row(600, "90", "pg_catalog", "point", "16", "b", "G"),
            Row(601, "90", "pg_catalog", "_point", "16", "b", "A", elementOid: 600));
        var codecs = new PgTextCodecs(catalog);
        Assert.Throws<NotSupportedException>(() => codecs.GetFieldType(601));
        Assert.Throws<NotSupportedException>(() => codecs.GetFieldType(12345));
        Assert.Throws<NotSupportedException>(() => PgBinaryCopyCodec.Decode("point", new byte[8]));
    }

    [Fact]
    public async Task ReaderExposesUnknownColumnsAsText()
    {
        var catalog = await Load(namespaceOid: "90", Row(600, "90", "pg_catalog", "point", "16", "b", "G"));
        using var reader = new KingbaseDataReader(
            [new PgTextResult([new PgColumnDescription("p", 0, 0, 600, 16, -1)], [["(1,2)"], [null]], "SELECT 2")], catalog);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetProviderSpecificFieldType(0));
        Assert.Equal("point", reader.GetDataTypeName(0));
        Assert.Equal("point", reader.GetColumnSchema()[0].DataTypeName);
        Assert.Equal(typeof(string), reader.GetColumnSchema()[0].DataType);
        Assert.Equal("(1,2)", reader.GetValue(0));
        Assert.Equal("(1,2)", reader.GetString(0));
        Assert.Throws<InvalidCastException>(() => reader.GetInt32(0));
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Same(DBNull.Value, reader.GetValue(0));
    }

    [Fact]
    public async Task ParameterBindingSendsTheTextForm()
    {
        var catalog = await Load(namespaceOid: "90", Row(600, "90", "pg_catalog", "point", "16", "b", "G"));
        var bound = PgParameterBindings.Create("SELECT :p", new KingbaseParameterCollection
        {
            new KingbaseParameter("p", "(1,2)") { DataTypeName = "pg_catalog.point" }
        }, catalog, KingbaseCompatibilityMode.PostgreSql, true);
        Assert.Equal("(1,2)", bound.Parameters[0].Value);
        Assert.Throws<InvalidCastException>(() => PgParameterBindings.Create("SELECT :p", new KingbaseParameterCollection
        {
            new KingbaseParameter("p", 1) { DataTypeName = "pg_catalog.point" }
        }, catalog, KingbaseCompatibilityMode.PostgreSql, true));
    }

    [Fact]
    public async Task NativeDiscoveryRefinesTheTextEntryOfTheSameOid()
    {
        var native = NativeScalarTests.NativeRows(KingbaseCompatibilityMode.MySql);
        var uint4 = native.Single(row => row[3] == "uint4");
        var baseRows = NativeScalarTests.BaseRows();
        baseRows.Add(uint4.Take(12).ToArray());
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.MySql, nativeRows: native, baseRows: baseRows);
        var oid = uint.Parse(uint4[0]!, CultureInfo.InvariantCulture);
        Assert.Equal("uint4", catalog.ByOid[oid].CodecName);
        Assert.Equal(typeof(uint), new PgTextCodecs(catalog).GetFieldType(oid));
    }

    [Fact]
    public async Task DiscoveredRowsWithoutAMatchingLayoutNeverPublish()
    {
        var native = NativeScalarTests.NativeRows(KingbaseCompatibilityMode.MySql);
        var uint4 = native.Single(row => row[3] == "uint4");
        var baseRows = NativeScalarTests.BaseRows();
        baseRows.Add(uint4.Take(12).ToArray());
        uint4[4] = "9";
        await Assert.ThrowsAsync<IOException>(() =>
            NativeScalarTests.Load(KingbaseCompatibilityMode.MySql, nativeRows: native, baseRows: baseRows));
    }

    [Fact]
    public async Task UnprovenNativeDomainsKeepTheTextPath()
    {
        var rows = NativeScalarTests.BaseRows();
        rows.Add(Row(5050, "91", "sys", "date", "8", "d", "D", baseTypeOid: 10002));
        // Oracle-style LOB domains live in pg_catalog, so the native-alias rule must cover them as well.
        rows.Add(Row(5052, "90", "pg_catalog", "blob", "-1", "d", "U", baseTypeOid: 10005));
        var sysCatalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.Oracle, nativeRows: [], baseRows: rows);
        Assert.Equal(PgTextCodecs.PlaceholderText, sysCatalog.ByOid[5050].CodecName);
        Assert.Equal(typeof(string), new PgTextCodecs(sysCatalog).GetFieldType(5050));
        Assert.Equal(PgTextCodecs.PlaceholderText, sysCatalog.ByOid[5052].CodecName);
        Assert.Equal(typeof(string), new PgTextCodecs(sysCatalog).GetFieldType(5052));
        rows.Add(Row(5051, "92", "public", "posint", "4", "d", "N", baseTypeOid: 10002));
        var userCatalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql, baseRows: rows);
        Assert.Equal(typeof(int), new PgTextCodecs(userCatalog).GetFieldType(5051));
    }

    // A single catalog response carrying the built-in rows plus the rows under test.
    static async Task<PgTypeCatalog> Load(string namespaceOid, params string?[][] rows)
    {
        var all = NativeScalarTests.BaseRows();
        foreach (var row in rows) all.Add(row);
        return await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql, baseRows: all);
    }

    static string?[] Row(int oid, string ns, string schema, string name, string size, string kind, string category,
        uint baseTypeOid = 0, uint elementOid = 0) =>
    [
        oid.ToString(CultureInfo.InvariantCulture), ns, schema, name, size, kind, category, "t",
        baseTypeOid.ToString(CultureInfo.InvariantCulture), elementOid.ToString(CultureInfo.InvariantCulture), "0", "-1"
    ];
}
