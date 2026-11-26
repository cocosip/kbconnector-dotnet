using System.Data;
using System.Globalization;
using KingbaseConnector;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class EnumAndSetTypeTests
{
    const uint PgNamespace = 90;
    const uint SchemaNamespace = 100;
    const uint First = 7000;

    // Required base names keep the loader's mandatory set satisfied; the enum and SET rows follow.
    static readonly PgTypeDescription[] Types =
    [
        Base("bool", 1, 'B'), Base("int2", 2, 'N'), Base("int4", 4, 'N'), Base("int8", 8, 'N'),
        Base("text", -1, 'S'), Base("bytea", -1, 'U'),
        new(First + 6, SchemaNamespace, "routine_type_enum", 4, 'e', 'E', 0, 0, 0, -1, "information_schema"),
        new(First + 7, SchemaNamespace, "column_key_enum", 4, 'e', 'E', 0, 0, 0, -1, "information_schema"),
        new(First + 8, SchemaNamespace, "sql_mode_set", 16, 's', 'M', 0, 0, 0, -1, "information_schema"),
        new(First + 9, SchemaNamespace, "public_mood", 4, 'e', 'E', 0, 0, 0, -1, "public")
    ];
    static readonly Task<PgTypeCatalog> CatalogTask = Load();
    static uint Oid(string name) => Types.Single(type => type.Name == name).Oid;

    static PgTypeDescription Base(string name, short size, char category) =>
        new(First + (uint)Array.FindIndex(["bool", "int2", "int4", "int8", "text", "bytea"], item => item == name),
            PgNamespace, name, size, 'b', category, 0, 0, 0, -1);

    [Theory]
    [InlineData("routine_type_enum", "FUNCTION")]
    [InlineData("routine_type_enum", "PROCEDURE")]
    [InlineData("column_key_enum", "")]
    [InlineData("column_key_enum", "PRI")]
    [InlineData("public_mood", "very happy, indeed")]
    [InlineData("sql_mode_set", "")]
    [InlineData("sql_mode_set", "STRICT_TRANS_TABLES,NO_ZERO_DATE")]
    public async Task EnumAndSetValuesKeepTheirServerTextForm(string name, string text)
    {
        var codecs = new PgTextCodecs(await CatalogTask);
        Assert.Equal(typeof(string), codecs.GetFieldType(Oid(name)));
        Assert.Equal(typeof(string), codecs.GetProviderSpecificFieldType(Oid(name)));
        Assert.Equal(text, codecs.Decode(Oid(name), text));
        Assert.Equal(text, codecs.DecodeProviderSpecific(Oid(name), text));
        Assert.Equal(text, Convert.ToString(codecs.DecodeStreaming(Oid(name), text, null, null, false), CultureInfo.InvariantCulture));
        Assert.Equal(text, codecs.Encode(Oid(name), text));
        Assert.Same(DBNull.Value, codecs.Decode(Oid(name), null));
        Assert.Null(codecs.Encode(Oid(name), null));
    }

    [Theory]
    [InlineData("routine_type_enum")]
    [InlineData("sql_mode_set")]
    public async Task EnumAndSetRejectMalformedCharacterData(string name)
    {
        var codecs = new PgTextCodecs(await CatalogTask);
        Assert.Throws<FormatException>(() => codecs.Decode(Oid(name), "FUNCTION\0"));
        Assert.Throws<FormatException>(() => codecs.DecodeStreaming(Oid(name), "FUNCTION\0", null, null, false));
        Assert.Throws<FormatException>(() => codecs.Encode(Oid(name), "FUNCTION\0"));
        Assert.Throws<FormatException>(() => codecs.Encode(Oid(name), "\ud800"));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(Oid(name), 1));
    }

    [Theory]
    [InlineData("routine_type_enum", 'N', 0, 0)]
    [InlineData("routine_type_enum", 'E', 23, 0)]
    [InlineData("routine_type_enum", 'E', 0, 23)]
    [InlineData("routine_type_enum", 'A', 0, 0)]
    [InlineData("sql_mode_set", 'E', 0, 0)]
    [InlineData("sql_mode_set", 'M', 23, 0)]
    public async Task CatalogLoadSkipsMislabelledEnumAndSetTypes(string name, char category, uint baseType, uint elementType)
    {
        var catalog = await Load(type => type.Name != name ? type :
            new PgTypeDescription(type.Oid, type.NamespaceOid, type.Name, type.Size, type.Kind, category, baseType, elementType, 0, -1, type.Namespace));
        Assert.DoesNotContain(catalog.ByName.Values, type => type.Name == name);
        Assert.Throws<NotSupportedException>(() => new PgTextCodecs(catalog).GetFieldType(Oid(name)));
    }

    [Fact]
    public async Task MislabelledEnumSizesAreSkipped()
    {
        var catalog = await Load(null,
            new PgTypeDescription(First + 40, SchemaNamespace, "odd_enum", -2, 'e', 'E', 0, 0, 0, -1, "public"));
        Assert.DoesNotContain("public.odd_enum", catalog.ByName.Keys);
        Assert.False(catalog.ByOid.ContainsKey(First + 40));
    }

    [Fact]
    public async Task EnumColumnsExposeTheirCatalogNamesAndReadAsStrings()
    {
        var catalog = await CatalogTask;
        var enumOid = Oid("routine_type_enum");
        using var reader = new KingbaseDataReader(
            [new PgTextResult([new("routine_type", 0, 0, enumOid, 4, -1), new("label", 0, 0, enumOid, 4, -1)],
                [new string?[] { "FUNCTION", "" }, new string?[] { null, "PROCEDURE" }], "SELECT 2")], catalog);
        Assert.Equal("routine_type_enum", reader.GetDataTypeName(0));
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal("information_schema.routine_type_enum", reader.GetColumnSchema()[0].DataTypeName);
        Assert.True(reader.Read());
        Assert.Equal("FUNCTION", reader.GetString(0));
        Assert.Equal("", reader.GetString(1));
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("PROCEDURE", reader.GetValue(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task EnumParametersUseTheirCatalogTypeAndTextValue()
    {
        var catalog = await CatalogTask;
        Assert.Equal(Oid("routine_type_enum"), Assert.Single((await Bind(catalog, new KingbaseParameter("p", "FUNCTION")
            { DataTypeName = "information_schema.routine_type_enum", DbType = DbType.String })).Parameters).TypeOid);
        // The DbType constraint follows the text family for these codecs.
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(catalog, new KingbaseParameter("p", "FUNCTION")
            { DataTypeName = "information_schema.routine_type_enum", DbType = DbType.Int32 }));
        Assert.Equal(Oid("sql_mode_set"), Assert.Single((await Bind(catalog, new KingbaseParameter("p", "A,B")
            { DataTypeName = "information_schema.sql_mode_set", DbType = DbType.String })).Parameters).TypeOid);
        var bound = Assert.Single((await Bind(catalog, new KingbaseParameter("p", "FUNCTION")
            { DataTypeName = "information_schema.routine_type_enum" })).Parameters);
        Assert.Equal(Oid("routine_type_enum"), bound.TypeOid);
        Assert.Equal("FUNCTION", bound.Value);
    }

    static async Task<PgParameterBindings> Bind(PgTypeCatalog catalog, KingbaseParameter parameter)
    {
        var collection = new KingbaseParameterCollection();
        collection.Add(parameter);
        try
        {
            return PgParameterBindings.Create("SELECT :p", collection, catalog, KingbaseCompatibilityMode.MySql, true);
        }
        finally { collection.Clear(); }
    }

    [Fact]
    public async Task CatalogLoadAcceptsEnumRowsAndSkipsInconsistentOnes()
    {
        var catalog = await CatalogTask;
        Assert.Equal(4, catalog.ByName.Keys.Count(key => !key.StartsWith("pg_catalog.", StringComparison.Ordinal)));
        Assert.Same(catalog.ByOid[First + 9], catalog.ByName["public.public_mood"]);
        Assert.Equal((short)16, catalog.ByName["information_schema.sql_mode_set"].Size);
        Assert.Equal('s', catalog.ByName["information_schema.sql_mode_set"].Kind);
        var inconsistent = await Load(type => type.Name != "public_mood" ? type :
            new PgTypeDescription(type.Oid, type.NamespaceOid, type.Name, type.Size, type.Kind, 'S', 0, 0, 0, -1, type.Namespace));
        Assert.DoesNotContain("public.public_mood", inconsistent.ByName.Keys);
        Assert.Equal(3, inconsistent.ByName.Keys.Count(key => !key.StartsWith("pg_catalog.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UnknownDynamicRowsAndBadLayoutsRetireTheSession()
    {
        using var stray = new PgQuerySession(new PgWire(new QueryStream(
            Catalog([.. Types, new PgTypeDescription(First + 20, SchemaNamespace, "user_type", 4, 'r', 'R', 0, 0, 0, -1, "public")]))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        await Assert.ThrowsAsync<IOException>(() => PgTypeCatalog.LoadAsync(stray, Identity()));
        using var zeroSize = new PgQuerySession(new PgWire(new QueryStream(
            Catalog([.. Types, new PgTypeDescription(First + 21, SchemaNamespace, "empty_enum", 0, 'e', 'E', 0, 0, 0, -1, "public")]))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        await Assert.ThrowsAsync<IOException>(() => PgTypeCatalog.LoadAsync(zeroSize, Identity()));
    }

    [Fact]
    public async Task UserTypesNamedLikeBuiltInsDoNotShadowThem()
    {
        // A user enum named text/int4 is its own type; the built-in pg_catalog rows keep their identity.
        var catalog = await Load(null,
            new PgTypeDescription(First + 30, SchemaNamespace, "text", 4, 'e', 'E', 0, 0, 0, -1, "public"),
            new PgTypeDescription(First + 31, SchemaNamespace, "int4", 4, 'e', 'E', 0, 0, 0, -1, "public"));
        Assert.Equal('b', catalog.ByName["pg_catalog.text"].Kind);
        Assert.Equal('e', catalog.ByName["public.text"].Kind);
        Assert.Equal('e', catalog.ByName["public.int4"].Kind);
        var codecs = new PgTextCodecs(catalog);
        Assert.Equal(typeof(string), codecs.GetFieldType(First + 30));
        Assert.Equal("pg_catalog", codecs.Decode(Oid("text"), "pg_catalog"));
        Assert.Equal(typeof(int), codecs.GetFieldType(Oid("int4")));
        Assert.Equal(7, codecs.Decode(Oid("int4"), "7"));
        Assert.Equal("FUNCTION", codecs.Decode(First + 31, "FUNCTION"));
    }

    static async Task<PgTypeCatalog> Load(Func<PgTypeDescription, PgTypeDescription>? change = null,
        params PgTypeDescription[] extra)
    {
        var definitions = Types.Select(type => change?.Invoke(type) ?? type).Concat(extra);
        using var session = new PgQuerySession(new PgWire(new QueryStream(Catalog(definitions))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        return await PgTypeCatalog.LoadAsync(session, Identity());
    }

    static PgIdentityResult Identity()
    {
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql,
            KingbaseCompatibilityMode.PostgreSql, null, null);
    }
}


