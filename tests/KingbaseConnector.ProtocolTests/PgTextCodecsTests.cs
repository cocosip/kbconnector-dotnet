using System.Globalization;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgTextCodecsTests
{
    static readonly (string Name, short Size)[] Types = [("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8),
        ("oid", 4), ("float4", 4), ("float8", 8), ("text", -1), ("varchar", -1), ("bpchar", -1),
        ("bytea", -1), ("uuid", 16), ("json", -1), ("jsonb", -1), ("numeric", -1), ("date", 4)];
    static readonly Task<PgTypeCatalog> Catalog = LoadCatalog();
    static uint Oid(string name, uint first = 7000) => first + (uint)Array.FindIndex(Types, item => item.Name == name);

    public static TheoryData<string, string, object> Scalars => new()
    {
        { "bool", "t", true }, { "bool", "f", false },
        { "int2", "-32768", short.MinValue }, { "int2", "+32767", short.MaxValue },
        { "int4", "-2147483648", int.MinValue }, { "int4", "2147483647", int.MaxValue },
        { "int8", "-9223372036854775808", long.MinValue }, { "int8", "9223372036854775807", long.MaxValue },
        { "oid", "4294967295", uint.MaxValue }, { "oid", "0", 0U },
        { "float4", "1.25e+2", 125F }, { "float8", "-.125E-2", -.00125D },
        { "text", "", "" }, { "text", "汉字😀; ' \\n", "汉字😀; ' \\n" },
        { "varchar", "  untouched  ", "  untouched  " }, { "bpchar", "pad   ", "pad   " },
        { "bytea", "\\x", Array.Empty<byte>() }, { "bytea", "\\x00ff805C", new byte[] { 0, 255, 128, 92 } },
        { "bytea", "a\\000\\377\\200\\\\'", new byte[] { 97, 0, 255, 128, 92, 39 } },
        { "uuid", "01234567-89AB-CDEF-0123-456789ABCDEF", Guid.Parse("01234567-89ab-cdef-0123-456789abcdef") },
        { "json", "{ \"n\": 1.000, \"s\": \"\\u0061\" }", "{ \"n\": 1.000, \"s\": \"\\u0061\" }" },
        { "jsonb", "[1, 2]", "[1, 2]" }
    };

    [Theory]
    [MemberData(nameof(Scalars))]
    public async Task ScalarResultsAndEncodedValuesKeepExactClrTypes(string name, string text, object expected)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal(expected.GetType(), codecs.GetFieldType(Oid(name)));
        EqualValue(expected, codecs.Decode(Oid(name), text));
        var encoded = codecs.Encode(Oid(name), expected);
        EqualValue(expected, codecs.Decode(Oid(name), encoded));
    }

    [Fact]
    public async Task NullAndEmptyAreDistinctAndFieldTypesDoNotChange()
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var (name, _) in Types.Where(item => item.Name != "date"))
        {
            var type = codecs.GetFieldType(Oid(name));
            Assert.Same(DBNull.Value, codecs.Decode(Oid(name), null));
            Assert.Null(codecs.Encode(Oid(name), null));
            Assert.Null(codecs.Encode(Oid(name), DBNull.Value));
            Assert.Same(type, codecs.GetFieldType(Oid(name)));
        }
        Assert.Equal("", codecs.Decode(Oid("text"), ""));
        Assert.Empty(Assert.IsType<byte[]>(codecs.Decode(Oid("bytea"), "")));
    }

    [Theory]
    [InlineData("bool", "true")]
    [InlineData("bool", "1")]
    [InlineData("bool", "F")]
    [InlineData("int2", " 1")]
    [InlineData("int4", "1 ")]
    [InlineData("int8", "1,000")]
    [InlineData("int4", "1.0")]
    [InlineData("int2", "")]
    [InlineData("oid", "-0")]
    [InlineData("oid", "-1")]
    [InlineData("int2", "１２")]
    [InlineData("float4", "nan")]
    [InlineData("float8", "+Infinity")]
    [InlineData("float4", "1,25")]
    [InlineData("float8", " 1.0")]
    [InlineData("float4", ".")]
    [InlineData("float8", "1e+")]
    [InlineData("float8", "0x1")]
    [InlineData("uuid", "0123456789abcdef0123456789abcdef")]
    [InlineData("uuid", "{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("bytea", "\\x0")]
    [InlineData("bytea", "\\xgg")]
    [InlineData("bytea", "\\x00 ff")]
    [InlineData("bytea", "\\")]
    [InlineData("bytea", "\\12")]
    [InlineData("bytea", "\\400")]
    [InlineData("bytea", "\\078")]
    [InlineData("bytea", "\\q")]
    [InlineData("bytea", "é")]
    public async Task RejectsMalformedCanonicalResultText(string name, string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        await AssertSafeFailure<FormatException>(() => codecs.Decode(Oid(name), text), text);
    }

    [Theory]
    [InlineData("int2", "32768")]
    [InlineData("int2", "-32769")]
    [InlineData("int4", "2147483648")]
    [InlineData("int4", "-2147483649")]
    [InlineData("int8", "9223372036854775808")]
    [InlineData("int8", "-9223372036854775809")]
    [InlineData("oid", "4294967296")]
    [InlineData("float4", "3.5e38")]
    [InlineData("float8", "1.8e308")]
    [InlineData("float4", "1e-999")]
    [InlineData("float8", "-1e-9999")]
    public async Task RejectsFiniteOverflowAndNonzeroUnderflow(string name, string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        await AssertSafeFailure<OverflowException>(() => codecs.Decode(Oid(name), text), text);
    }

    [Fact]
    public async Task FloatingSpecialValuesRoundTripsAndNegativeZeroPreserveBits()
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0F,
            float.Epsilon, float.MinValue, float.MaxValue, 1.0000001F })
        {
            var text = codecs.Encode(Oid("float4"), value)!;
            var result = Assert.IsType<float>(codecs.Decode(Oid("float4"), text));
            Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(result));
        }
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0D,
            double.Epsilon, double.MinValue, double.MaxValue, 1.0000000000000002D })
        {
            var text = codecs.Encode(Oid("float8"), value)!;
            var result = Assert.IsType<double>(codecs.Decode(Oid("float8"), text));
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(result));
        }
        Assert.Equal("NaN", codecs.Encode(Oid("float4"), float.NaN));
        Assert.Equal("Infinity", codecs.Encode(Oid("float8"), double.PositiveInfinity));
        Assert.Equal("-Infinity", codecs.Encode(Oid("float4"), float.NegativeInfinity));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits((float)codecs.Decode(Oid("float4"), "-0.000e5")));
        Assert.Equal(unchecked((long)0x8000000000000000), BitConverter.DoubleToInt64Bits((double)codecs.Decode(Oid("float8"), "-0e-999")));
    }

    [Fact]
    public async Task EncodingUsesExactClrInputsAndDoesNotCoerceValues()
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var (name, wrong) in new (string, object)[] { ("int2", 1), ("int4", 1L), ("int8", "1"),
            ("oid", -1), ("bool", "t"), ("float4", 1D), ("float8", 1M), ("uuid", Guid.Empty.ToString()),
            ("bytea", "00"), ("text", new object()), ("json", new Dictionary<string, int>()) })
            Assert.Throws<InvalidCastException>(() => codecs.Encode(Oid(name), wrong));
    }

    [Fact]
    public async Task CultureDoesNotAffectParsingOrEncoding()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var codecs = new PgTextCodecs(await Catalog);
            Assert.Equal(1234.5D, codecs.Decode(Oid("float8"), "1234.5"));
            Assert.Equal("1234.5", codecs.Encode(Oid("float8"), 1234.5D));
            Assert.Equal("-1234", codecs.Encode(Oid("int8"), -1234L));
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public async Task EveryByteAndBothEscapeFormsDecodeWithoutSqlLiteralUnescaping()
    {
        var codecs = new PgTextCodecs(await Catalog);
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var text = codecs.Encode(Oid("bytea"), bytes)!;
        Assert.Equal("\\x00010203", text[..10]);
        Assert.EndsWith("fcfdfeff", text, StringComparison.Ordinal);
        Assert.Equal(bytes, Assert.IsType<byte[]>(codecs.Decode(Oid("bytea"), text)));
        var octal = string.Concat(bytes.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
        Assert.Equal(bytes, Assert.IsType<byte[]>(codecs.Decode(Oid("bytea"), octal)));
        Assert.Equal(new byte[] { 39, 39, 92 }, codecs.Decode(Oid("bytea"), "''\\\\"));
        var decoded = (byte[])codecs.Decode(Oid("bytea"), text);
        decoded[0] = 255;
        Assert.Equal(0, ((byte[])codecs.Decode(Oid("bytea"), text))[0]);
        Assert.Equal(0, bytes[0]);
    }

    [Fact]
    public async Task ValueBoundsUseStrictUtf8BytesAndPreventHexAmplification()
    {
        var codecs = new PgTextCodecs(await Catalog);
        var limit = new string('a', 1024 * 1024);
        Assert.Equal(limit, codecs.Decode(Oid("text"), limit));
        Assert.Equal(limit, codecs.Encode(Oid("text"), limit));
        var unicode = new string('界', 349525);
        Assert.Equal(unicode, codecs.Encode(Oid("varchar"), unicode));
        Assert.Throws<ArgumentOutOfRangeException>(() => codecs.Encode(Oid("text"), unicode + "界"));
        Assert.Throws<ArgumentOutOfRangeException>(() => codecs.Decode(Oid("text"), limit + "x"));
        var bytes = new byte[(1024 * 1024 - 2) / 2];
        Assert.Equal(1024 * 1024, codecs.Encode(Oid("bytea"), bytes)!.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => codecs.Encode(Oid("bytea"), new byte[bytes.Length + 1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidUnicodeAndNulNeverLeakPayloads(bool invalidSurrogate)
    {
        var text = "secret" + new string(invalidSurrogate ? '\ud800' : '\0', 1) + "payload";
        var codecs = new PgTextCodecs(await Catalog);
        await AssertSafeFailure<FormatException>(() => codecs.Decode(Oid("text"), text), "secret");
        await AssertSafeFailure<FormatException>(() => codecs.Encode(Oid("text"), text), "secret");
    }

    [Fact]
    public async Task UnknownAndDeferredTypesAreRejectedEvenForNull()
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var oid in new[] { 16U, uint.MaxValue, Oid("date") })
        {
            Assert.Throws<NotSupportedException>(() => codecs.GetFieldType(oid));
            Assert.Throws<NotSupportedException>(() => codecs.Decode(oid, null));
            Assert.Throws<NotSupportedException>(() => codecs.Encode(oid, null));
        }
        Assert.Throws<ArgumentNullException>(() => new PgTextCodecs(null!));
    }

    [Theory]
    [InlineData(8, 'b', 0U, 0U)]
    [InlineData(4, 'd', 100U, 0U)]
    [InlineData(4, 'b', 0U, 123U)]
    public async Task CatalogNamesCannotOverrideUnsupportedLayouts(short size, char kind, uint baseOid, uint elementOid)
    {
        var catalog = await LoadCatalog(change: type => type.Name != "float4" ? type : new PgTypeDescription(
            type.Oid, type.NamespaceOid, type.Name, size, kind, type.Category, baseOid, elementOid, 0, -1));
        var codecs = new PgTextCodecs(catalog);
        Assert.Throws<NotSupportedException>(() => codecs.GetFieldType(Oid("float4")));
        Assert.Throws<NotSupportedException>(() => codecs.Decode(Oid("float4"), "1"));
    }

    [Fact]
    public async Task ActualOidsAreSessionLocalAndCanIncludeUintMaxValue()
    {
        var first = new PgTextCodecs(await Catalog);
        var second = new PgTextCodecs(await LoadCatalog(uint.MaxValue - 15));
        Assert.Throws<NotSupportedException>(() => second.GetFieldType(Oid("bool")));
        Assert.Equal(typeof(bool), second.GetFieldType(uint.MaxValue - 15));
        Assert.True((bool)second.Decode(uint.MaxValue - 15, "t"));
        Assert.Equal(typeof(bool), first.GetFieldType(Oid("bool")));
        var largestOid = new PgTextCodecs(await LoadCatalog(change: type => type.Name != "oid" ? type :
            new PgTypeDescription(uint.MaxValue, type.NamespaceOid, type.Name, type.Size, type.Kind, type.Category, 0, 0, 0, -1)));
        Assert.Equal(uint.MaxValue, largestOid.Decode(uint.MaxValue, "4294967295"));
    }

    [Fact]
    public async Task LoadedCatalogResolvesTypedQueryResultWithoutChangingRawText()
    {
        var wire = PgTestPeer.Join(PgTestPeer.Catalog(Descriptions(9000)), PgTestPeer.Scalar("2147483647", Oid("int4", 9000), 4));
        using var session = Session(new QueryStream(wire));
        var catalog = await PgTypeCatalog.LoadAsync(session, Identity());
        var results = await session.ExecuteAsync("SELECT example");
        var codecs = new PgTextCodecs(catalog);
        var column = results[0].Columns[0];
        var raw = results[0].Rows[0][0];
        Assert.Equal("2147483647", raw);
        Assert.Equal(int.MaxValue, codecs.Decode(column.TypeOid, raw));
        Assert.Equal(typeof(int), codecs.GetFieldType(column.TypeOid));
        Assert.Equal("2147483647", results[0].Rows[0][0]);
    }

    [Theory]
    [InlineData("1.2300")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-0.0000000000000000000000000001")]
    public async Task NumericDefaultAndProviderMappingsRemainStable(string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal(typeof(decimal), codecs.GetFieldType(Oid("numeric")));
        Assert.Equal(typeof(KingbaseNumeric), codecs.GetProviderSpecificFieldType(Oid("numeric")));
        var expected = decimal.Parse(text, CultureInfo.InvariantCulture);
        Assert.Equal(expected, Assert.IsType<decimal>(codecs.Decode(Oid("numeric"), text)));
        var provider = Assert.IsType<KingbaseNumeric>(codecs.DecodeProviderSpecific(Oid("numeric"), text));
        Assert.Equal(text, provider.ToString());
        Assert.Equal(text, codecs.Encode(Oid("numeric"), provider));
        Assert.Equal(expected, codecs.Decode(Oid("numeric"), codecs.Encode(Oid("numeric"), expected)));
        Assert.Same(DBNull.Value, codecs.DecodeProviderSpecific(Oid("numeric"), null));
        Assert.Equal(typeof(string), codecs.GetProviderSpecificFieldType(Oid("text")));
        Assert.Equal("raw", codecs.DecodeProviderSpecific(Oid("text"), "raw"));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(Oid("numeric"), 1D));
    }

    [Theory]
    [InlineData("12345678901234567890123456789012345678901234567890.00100")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task NumericPrecisionAndSpecialsNeedExplicitProviderAccess(string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Throws<OverflowException>(() => codecs.Decode(Oid("numeric"), text));
        var provider = Assert.IsType<KingbaseNumeric>(codecs.DecodeProviderSpecific(Oid("numeric"), text));
        Assert.Equal(text, provider.ToString());
        Assert.Equal(text, codecs.Encode(Oid("numeric"), provider));
        Assert.Equal(typeof(decimal), codecs.GetFieldType(Oid("numeric")));
    }

    static void EqualValue(object expected, object actual)
    {
        Assert.IsType(expected.GetType(), actual);
        if (expected is byte[] bytes) Assert.Equal(bytes, (byte[])actual);
        else Assert.Equal(expected, actual);
    }
    static Task AssertSafeFailure<T>(Action action, string privateText) where T : Exception
    {
        var error = Assert.Throws<T>(action);
        Assert.Null(error.InnerException);
        if (privateText.Length > 3) Assert.DoesNotContain(privateText, error.ToString(), StringComparison.Ordinal);
        return Task.CompletedTask;
    }
    static IEnumerable<PgTypeDescription> Descriptions(uint first) => Types.Select((type, i) =>
        new PgTypeDescription(first + (uint)i, 90, type.Name, type.Size, 'b', 'U', 0, 0, 0, -1));
    static async Task<PgTypeCatalog> LoadCatalog(uint first = 7000, Func<PgTypeDescription, PgTypeDescription>? change = null)
    {
        using var session = Session(new QueryStream(PgTestPeer.Catalog(Descriptions(first).Select(type => change?.Invoke(type) ?? type))));
        return await PgTypeCatalog.LoadAsync(session, Identity());
    }
    static PgQuerySession Session(QueryStream stream) => new(new PgWire(stream), new PgStartupResult(
        new Dictionary<string, string> { ["client_encoding"] = "UTF8", ["database_mode"] = "pg" }, new PgBackendKey(1, 2)));
    static PgIdentityResult Identity()
    {
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return new PgIdentityResult(version!, "kernel-14", KingbaseCompatibilityMode.Auto,
            KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, PgModeSource.ParameterStatus);
    }
}
