using System.Data;
using System.Buffers.Binary;
using System.Text;
using System.Xml;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgXmlTests
{
    const uint XmlOid = uint.MaxValue;
    static KingbaseDbType XmlType => Enum.Parse<KingbaseDbType>("Xml");

    [Fact]
    public void ExplicitDbTypeXmlSelectsTheXmlProviderType()
    {
        var parameter = new KingbaseParameter("p", "<root />") { DbType = DbType.Xml };
        Assert.Equal("Xml", parameter.KingbaseDbType.ToString());
        Assert.Equal(DbType.Xml, parameter.DbType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<root a=\"&amp;\">汉字😀</root>")]
    [InlineData("<first/>text<second/>")]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-16\"?><root>汉😀</root>")]
    [InlineData("<!DOCTYPE root [<!ENTITY e SYSTEM 'file:///nonexistent-kb-xml-test'>]><root>&e;</root>")]
    [InlineData("<invalid-unclosed>")]
    public async Task XmlTextIsPreservedWithoutParsingValidationOrEncodingConversion(string text)
    {
        var catalog = await Catalog(); var codecs = new PgTextCodecs(catalog);
        Assert.Equal(typeof(string), codecs.GetFieldType(XmlOid));
        Assert.Equal(typeof(string), codecs.GetProviderSpecificFieldType(XmlOid));
        Assert.Equal(text, codecs.Encode(XmlOid, text)); Assert.Equal(text, codecs.Decode(XmlOid, text));
        Assert.Equal(text, codecs.DecodeProviderSpecific(XmlOid, text));
        var frames = PgExtendedQuery.Encode("SELECT $1", catalog, [new(XmlOid, text)]);
        Assert.Equal(XmlOid, BinaryPrimitives.ReadUInt32BigEndian(frames[0].AsSpan(frames[0].Length - 4)));
        Assert.Equal(Encoding.UTF8.GetBytes(text), frames[1].AsSpan(15, frames[1].Length - 17).ToArray());
        using var reader = Reader(catalog, text);
        Assert.Equal("xml", reader.GetDataTypeName(0)); Assert.Equal(typeof(string), reader.GetColumnSchema()[0].DataType);
        Assert.True(reader.Read()); Assert.Equal(text, reader.GetString(0));
        Assert.Equal(text.Length, reader.GetChars(0, 0, null, 0, 0));
        using var textReader = reader.GetTextReader(0); reader.Close();
        Assert.Equal(text, await textReader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("dbtype")]
    [InlineData("name")]
    [InlineData("qualified")]
    [InlineData("all")]
    public async Task ExplicitSelectorsBindActualXmlOidAndTypedNull(string selector)
    {
        var catalog = await Catalog();
        foreach (var value in new object?[] { null, DBNull.Value, "<root/>" })
        {
            var p = new KingbaseParameter("p", value);
            if (selector is "provider" or "all") p.KingbaseDbType = XmlType;
            if (selector is "dbtype" or "all") p.DbType = DbType.Xml;
            if (selector is "name" or "all") p.DataTypeName = "xml";
            if (selector == "qualified") p.DataTypeName = "pg_catalog.xml";
            Assert.Equal(XmlOid, Assert.Single(Bind(p, catalog).Parameters).TypeOid);
        }
        Assert.Equal(DbType.Xml, new KingbaseParameter { KingbaseDbType = XmlType }.DbType);
        var stringConstraint = new KingbaseParameter("p", "<r/>") { DataTypeName = "xml", DbType = DbType.String };
        Assert.Equal(XmlOid, Assert.Single(Bind(stringConstraint, catalog).Parameters).TypeOid);
        Assert.Equal(DbType.String, new KingbaseParameter("p", "<r/>").DbType);
        Assert.NotEqual(XmlOid, Assert.Single(Bind(new("p", "<r/>"), catalog).Parameters).TypeOid);
    }

    [Fact]
    public async Task NullMetadataIsStableAndNullTextAccessThrows()
    {
        var catalog = await Catalog(); var codecs = new PgTextCodecs(catalog);
        Assert.Null(codecs.Encode(XmlOid, DBNull.Value)); Assert.Same(DBNull.Value, codecs.Decode(XmlOid, null));
        using var reader = Reader(catalog, null); Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0));
        Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Same(DBNull.Value, reader.GetProviderSpecificValue(0));
        Assert.Throws<InvalidCastException>(() => reader.GetTextReader(0));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("provider")]
    [InlineData("dbtype")]
    public async Task ConflictingSelectorsRejectBeforeEncoding(string kind)
    {
        var catalog = await Catalog(); var p = new KingbaseParameter("p", "<r/>") { DataTypeName = "xml" };
        if (kind == "name") { p.DataTypeName = "text"; p.KingbaseDbType = XmlType; }
        if (kind == "provider") p.KingbaseDbType = KingbaseDbType.Json;
        if (kind == "dbtype") { p.DataTypeName = "text"; p.DbType = DbType.Xml; }
        Assert.Throws<ArgumentException>(() => Bind(p, catalog));
    }

    [Fact]
    public async Task MissingOptionalTypeAndStructuredClrValuesRejectExplicitly()
    {
        var missing = await Catalog(includeXml: false);
        Assert.Throws<NotSupportedException>(() => Bind(new("p", DBNull.Value) { DbType = DbType.Xml }, missing));
        var catalog = await Catalog(); var document = new XmlDocument(); document.LoadXml("<r/>");
        using var input = new StringReader("<r/>");
        using var xmlReader = XmlReader.Create(new StringReader("<r/>"));
        foreach (var value in new object[] { document, input, xmlReader, new byte[] { 60, 62 }, 42 })
            Assert.Throws<InvalidCastException>(() => Bind(new("p", value) { DbType = DbType.Xml }, catalog));
        var p = new KingbaseParameter("p", "<r/>") { DbType = DbType.Xml, DataTypeName = "xml", KingbaseDbType = XmlType };
        p.ResetDbType(); Assert.Null(p.DataTypeName); Assert.Equal(KingbaseDbType.Text, p.KingbaseDbType);
    }

    [Theory]
    [InlineData(16, 'b', 0U, 0U)]
    [InlineData(-1, 'd', 0U, 0U)]
    [InlineData(-1, 'b', 5U, 0U)]
    [InlineData(-1, 'b', 0U, 5U)]
    public async Task LayoutGuardsAlsoApplyToTypedNull(short size, char kind, uint baseOid, uint elementOid)
    {
        var catalog = await Catalog(xml: new(XmlOid, 90, "xml", size, kind, 'U', baseOid, elementOid, 0, -1));
        var codecs = new PgTextCodecs(catalog);
        Assert.Throws<NotSupportedException>(() => codecs.Encode(XmlOid, null));
        Assert.Throws<NotSupportedException>(() => codecs.Decode(XmlOid, null));
        Assert.Throws<NotSupportedException>(() => Reader(catalog, null));
    }

    [Theory]
    [InlineData("nul", typeof(FormatException))]
    [InlineData("utf16", typeof(FormatException))]
    [InlineData("size", typeof(ArgumentException))]
    [InlineData("precision", typeof(NotSupportedException))]
    [InlineData("bytes", typeof(ArgumentOutOfRangeException))]
    public async Task ExistingTextAndParameterBoundsRemainEffective(string kind, Type expectedException)
    {
        var catalog = await Catalog();
        var p = new KingbaseParameter("p", "<r/>") { DbType = DbType.Xml };
        if (kind == "nul") p.Value = "private\0payload";
        if (kind == "utf16") p.Value = "private\ud800payload";
        if (kind == "size") p.Size = 3;
        if (kind == "precision") p.Precision = 1;
        if (kind == "bytes") p.Value = new string('界', 349526);
        var error = Assert.Throws(expectedException, () => Bind(p, catalog));
        Assert.DoesNotContain("private", error.ToString()); Assert.Null(error.InnerException);
        var large = "<r>" + new string('界', 90000) + "</r>";
        Assert.Equal(large, Assert.Single(Bind(new("p", large) { DbType = DbType.Xml }, catalog).Parameters).Value);
    }

    static PgParameterBindings Bind(KingbaseParameter p, PgTypeCatalog catalog)
    {
        var collection = new KingbaseParameterCollection(); collection.Add(p);
        try { return PgParameterBindings.Create("SELECT @p", collection, catalog, KingbaseCompatibilityMode.PostgreSql, true); }
        finally { collection.Clear(); }
    }
    static KingbaseDataReader Reader(PgTypeCatalog catalog, string? text) => new(
        [new PgTextResult([new("xml_value", 0, 0, XmlOid, -1, -1)], [new string?[] { text }], "SELECT 1")], catalog);
    static async Task<PgTypeCatalog> Catalog(bool includeXml = true, PgTypeDescription? xml = null)
    {
        var definitions = new (string Name, short Size)[] { ("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1), ("json", -1) }
            .Select((type, index) => new PgTypeDescription((uint)(6500 + index), 90, type.Name, type.Size, 'b', 'U', 0, 0, 0, -1));
        if (includeXml) definitions = definitions.Append(xml ?? new(XmlOid, 90, "xml", -1, 'b', 'U', 0, 0, 0, -1));
        using var session = new PgQuerySession(new PgWire(new QueryStream(PgTestPeer.Catalog(definitions))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, null, null));
    }
}
