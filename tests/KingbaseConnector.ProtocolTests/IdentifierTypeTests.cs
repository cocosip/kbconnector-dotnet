using System.Data;
using KingbaseConnector;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class IdentifierTypeTests
{
    // The first entries mirror a required base set; identifiers follow at stable offsets.
    static readonly (string Name, short Size, uint Element, char Category)[] Layouts =
    [
        ("bool", 1, 0, 'B'), ("int2", 2, 0, 'N'), ("int4", 4, 0, 'N'), ("int8", 8, 0, 'N'),
        ("text", -1, 0, 'S'), ("bytea", -1, 0, 'U'),
        ("name", 64, 6023, 'S'), ("char", 1, 0, 'S'),
        ("regclass", 4, 0, 'N'), ("regproc", 4, 0, 'N'), ("regprocedure", 4, 0, 'N'), ("regoper", 4, 0, 'N'),
        ("regoperator", 4, 0, 'N'), ("regtype", 4, 0, 'N'), ("regconfig", 4, 0, 'N'), ("regdictionary", 4, 0, 'N'),
        ("regnamespace", 4, 0, 'N'), ("regrole", 4, 0, 'N'),
        ("xid", 4, 0, 'U'), ("cid", 4, 0, 'U')
    ];
    static readonly string[] IdentifierNames = ["name", "char", "regclass", "regproc", "regprocedure", "regoper",
        "regoperator", "regtype", "regconfig", "regdictionary", "regnamespace", "regrole", "xid", "cid"];
    const uint First = 6016;
    static readonly Task<PgTypeCatalog> Catalog = Load();
    static uint Oid(string name) => First + (uint)Array.FindIndex(Layouts, item => item.Name == name);

    public static TheoryData<string, string, Type> Values => new()
    {
        { "name", "pg_catalog", typeof(string) },
        { "name", "some_user", typeof(string) },
        { "char", "v", typeof(string) },
        { "char", "f", typeof(string) },
        { "char", ",", typeof(string) },
        { "regclass", "pg_class", typeof(string) },
        { "regproc", "now", typeof(string) },
        { "regtype", "integer", typeof(string) },
        { "regrole", "system", typeof(string) },
        { "xid", "0", typeof(uint) },
        { "xid", "4294967295", typeof(uint) },
        { "cid", "42", typeof(uint) }
    };

    [Theory]
    [MemberData(nameof(Values))]
    public async Task IdentifierResultsKeepStableClrTypesAndRoundTrip(string name, string text, Type clrType)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal(clrType, codecs.GetFieldType(Oid(name)));
        Assert.Equal(clrType, codecs.GetProviderSpecificFieldType(Oid(name)));
        var value = codecs.Decode(Oid(name), text);
        Assert.Equal(clrType, value.GetType());
        Assert.Equal(text, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(text, Convert.ToString(codecs.DecodeProviderSpecific(Oid(name), text), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(text, codecs.Encode(Oid(name), value));
        Assert.Equal(text, Convert.ToString(codecs.DecodeStreaming(Oid(name), text, null, null, false), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("char")]
    [InlineData("regclass")]
    [InlineData("xid")]
    [InlineData("cid")]
    public async Task NullsAndUnicodeStayUnchangedForIdentifierTypes(string name)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Same(DBNull.Value, codecs.Decode(Oid(name), null));
        Assert.Same(DBNull.Value, codecs.DecodeStreaming(Oid(name), null, null, null, false));
        Assert.Null(codecs.Encode(Oid(name), null));
        Assert.Null(codecs.Encode(Oid(name), DBNull.Value));
        if (name is not ("char" or "xid" or "cid"))
        {
            const string unicode = "表名 ñ";
            Assert.Equal(unicode, codecs.Decode(Oid(name), unicode));
            Assert.Equal(unicode, codecs.Encode(Oid(name), unicode));
        }
    }

    [Fact]
    public async Task ExplicitDbTypeConstrainsIdentifierParametersByValueShape()
    {
        var name = new KingbaseParameter("p", "pg_class") { DataTypeName = "pg_catalog.name", DbType = DbType.String };
        Assert.Equal(Oid("name"), Assert.Single((await Bind(name)).Parameters).TypeOid);
        name.DbType = DbType.StringFixedLength;
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(name));
        name.DbType = DbType.Int32;
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(name));
        var transaction = new KingbaseParameter("p", 7u) { DataTypeName = "pg_catalog.xid", DbType = DbType.UInt32 };
        Assert.Equal(Oid("xid"), Assert.Single((await Bind(transaction)).Parameters).TypeOid);
        transaction.DbType = DbType.String;
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(transaction));
        var kind = new KingbaseParameter("p", "v") { DataTypeName = "pg_catalog.char", DbType = DbType.StringFixedLength };
        Assert.Equal(Oid("char"), Assert.Single((await Bind(kind)).Parameters).TypeOid);
    }

    static async Task<PgParameterBindings> Bind(KingbaseParameter parameter)
    {
        var collection = new KingbaseParameterCollection();
        collection.Add(parameter);
        try
        {
            return PgParameterBindings.Create("SELECT :p", collection, await Catalog, KingbaseCompatibilityMode.PostgreSql, true);
        }
        finally { collection.Clear(); }
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("\u00e9")]
    public async Task InternalCharRejectsAnythingButEmptyOrOneAsciiCharacter(string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Throws<FormatException>(() => codecs.Decode(Oid("char"), text));
        Assert.Throws<FormatException>(() => codecs.DecodeStreaming(Oid("char"), text, null, null, false));
        Assert.Throws<FormatException>(() => codecs.Encode(Oid("char"), text));
    }

    [Fact]
    public async Task ZeroValuedInternalCharIsTheEmptyString()
    {
        // pg_attribute.attidentity/attgenerated use "char" with value 0, whose text form is empty.
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal("", codecs.Decode(Oid("char"), ""));
        Assert.Equal("", codecs.DecodeStreaming(Oid("char"), "", null, null, false));
        Assert.Equal("", codecs.DecodeProviderSpecific(Oid("char"), ""));
        Assert.Equal("", codecs.Encode(Oid("char"), ""));
    }

    [Fact]
    public async Task EveryAddedCatalogNameResolvesThroughItsCodec()
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var name in IdentifierNames)
        {
            Assert.Contains(name, PgTypeCatalog.Names);
            Assert.NotNull(codecs.GetFieldType(Oid(name)));
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("4294967296")]
    public async Task TransactionIdentifiersRejectInvalidText(string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var name in new[] { "xid", "cid" })
        {
            var error = Assert.ThrowsAny<Exception>(() => codecs.Decode(Oid(name), text));
            Assert.True(error is FormatException or OverflowException);
            Assert.Throws<InvalidCastException>(() => codecs.Encode(Oid(name), text));
        }
    }

    [Fact]
    public async Task KingbaseNameLayoutWithCharElementStillDecodes()
    {
        // KingbaseES reports pg_catalog.name with element type pg_catalog."char" and category S, not as an array.
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal(6023u, (await Catalog).ByName["pg_catalog.name"].ElementTypeOid);
        Assert.Equal("pg_catalog", codecs.Decode(Oid("name"), "pg_catalog"));
    }

    [Fact]
    public async Task ArrayCategoryIsTheOnlyArraySignal()
    {
        var catalog = await Load(change: type => type.Name != "regclass" ? type : new PgTypeDescription(
            type.Oid, type.NamespaceOid, type.Name, type.Size, type.Kind, 'A', 0, 0, 0, -1));
        var codecs = new PgTextCodecs(catalog);
        Assert.Throws<NotSupportedException>(() => codecs.GetFieldType(Oid("regclass")));
        Assert.Throws<NotSupportedException>(() => codecs.Decode(Oid("regclass"), "pg_class"));
    }

    [Fact]
    public async Task IdentifierColumnsExposeCatalogNamesAndStreamedValues()
    {
        var catalog = await Catalog;
        var nameOid = Oid("name");
        var charOid = Oid("char");
        var xidOid = Oid("xid");
        using var reader = new KingbaseDataReader(
            [new PgTextResult([new("relname", 0, 0, nameOid, 64, -1), new("relkind", 0, 0, charOid, 1, -1), new("xmin", 0, 0, xidOid, 4, -1)],
                [new string?[] { "pg_class", "r", "7" }, new string?[] { null, "v", null }], "SELECT 3")], catalog);
        Assert.Equal("name", reader.GetDataTypeName(0));
        Assert.Equal("char", reader.GetDataTypeName(1));
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal(typeof(uint), reader.GetFieldType(2));
        Assert.Equal("name", reader.GetColumnSchema()[0].DataTypeName);
        Assert.True(reader.Read());
        Assert.Equal("pg_class", reader.GetString(0));
        Assert.Equal("r", reader.GetString(1));
        Assert.Throws<InvalidCastException>(() => reader.GetChar(1));
        Assert.Equal(7u, reader.GetFieldValue<uint>(2));
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("v", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.False(reader.Read());
    }

    static async Task<PgTypeCatalog> Load(Func<PgTypeDescription, PgTypeDescription>? change = null)
    {
        var definitions = Layouts.Select((layout, index) => change?.Invoke(new PgTypeDescription(
            First + (uint)index, 90, layout.Name, layout.Size, 'b', layout.Category, 0, layout.Element, 0, -1)) ??
            new PgTypeDescription(First + (uint)index, 90, layout.Name, layout.Size, 'b', layout.Category, 0, layout.Element, 0, -1));
        using var session = new PgQuerySession(new PgWire(new QueryStream(PgTestPeer.Catalog(definitions))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14",
            KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, null, null));
    }
}

