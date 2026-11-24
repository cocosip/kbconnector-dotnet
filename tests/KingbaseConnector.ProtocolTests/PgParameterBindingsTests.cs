using System.Data;
using System.Globalization;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgParameterBindingsTests
{
    static readonly (string Name, short Size)[] Types = [("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8),
        ("oid", 4), ("float4", 4), ("float8", 8), ("numeric", -1), ("text", -1), ("varchar", -1), ("bpchar", -1),
        ("bytea", -1), ("uuid", 16), ("json", -1), ("jsonb", -1)];
    static readonly Task<PgTypeCatalog> CatalogTask = LoadCatalogAsync();
    static uint Oid(string name) => (uint)(18000 + Array.FindIndex(Types, type => type.Name == name));
    static async Task<PgParameterBindings> Bind(KingbaseParameter parameter) =>
        await Bind("SELECT :p", [parameter]);
    static async Task<PgParameterBindings> Bind(string sql, KingbaseParameter[] parameters)
    {
        var collection = new KingbaseParameterCollection();
        collection.AddRange(parameters);
        try { return PgParameterBindings.Create(sql, collection, await CatalogTask, KingbaseCompatibilityMode.PostgreSql, true); }
        finally { collection.Clear(); }
    }

    public static IEnumerable<object[]> Inputs()
    {
        yield return [true, "bool", "t"];
        yield return [(short)-1, "int2", "-1"];
        yield return [42, "int4", "42"];
        yield return [long.MaxValue, "int8", "9223372036854775807"];
        yield return [uint.MaxValue, "oid", "4294967295"];
        yield return [1.25f, "float4", "1.25"];
        yield return [1.25d, "float8", "1.25"];
        yield return [1.2300m, "numeric", "1.2300"];
        yield return [KingbaseNumeric.Parse("123456789012345678901234567890.00100"), "numeric", "123456789012345678901234567890.00100"];
        yield return [" Unicode 😀 ", "text", " Unicode 😀 "];
        yield return [new byte[] { 0, 255 }, "bytea", "\\x00ff"];
        yield return [Guid.Parse("12345678-1234-1234-1234-123456789abc"), "uuid", "12345678-1234-1234-1234-123456789abc"];
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task ExactClrInferenceUsesActualCatalogOidAndPreservesValue(object value, string type, string text)
    {
        var original = new KingbaseParameter("p", value);
        var binding = await Bind(original);
        var parameter = Assert.Single(binding.Parameters);
        Assert.Equal("SELECT $1", binding.Sql);
        Assert.Equal(Oid(type), parameter.TypeOid);
        Assert.Equal(text, new PgTextCodecs(await CatalogTask).Encode(parameter.TypeOid, parameter.Value));
        Assert.Same(value, original.Value);
        Assert.Null(original.DataTypeName);
    }

    [Theory]
    [InlineData(KingbaseDbType.Text, "text")]
    [InlineData(KingbaseDbType.Varchar, "varchar")]
    [InlineData(KingbaseDbType.Char, "bpchar")]
    [InlineData(KingbaseDbType.Json, "json")]
    [InlineData(KingbaseDbType.Jsonb, "jsonb")]
    public async Task ExplicitStringFamiliesRemainDistinctActualCatalogTypes(KingbaseDbType type, string name)
    {
        var binding = await Bind(new KingbaseParameter("p", "{}") { KingbaseDbType = type, DbType = DbType.String });
        Assert.Equal(Oid(name), Assert.Single(binding.Parameters).TypeOid);
    }

    [Fact]
    public async Task NamedPlanAndMutableBytesProduceIndependentSendSnapshot()
    {
        byte[] bytes = [0, 255];
        var p = new KingbaseParameter("bytes", bytes);
        var n = new KingbaseParameter("number", 42);
        var bound = await Bind("SELECT :number,:bytes,:number", [p, n]);
        bytes[0] = 99;
        p.Value = new byte[] { 88 };
        p.ParameterName = "changed";
        n.Value = 7;
        Assert.Equal("SELECT $1,$2,$1", bound.Sql);
        Assert.Equal(42, bound.Parameters[0].Value);
        Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(bound.Parameters[1].Value));
        var frames = PgExtendedQuery.Encode(bound.Sql, await CatalogTask, bound.Parameters);
        Assert.Contains("\\x00ff", Encoding.UTF8.GetString(frames[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("99", Encoding.UTF8.GetString(frames[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicInputModelComposesWithExtendedExchangeWithoutLiteralInterpolation()
    {
        var bound = await Bind("SELECT :n,:text,:n", [new KingbaseParameter("text", "private'; --value"), new KingbaseParameter("n", 42)]);
        var stream = new QueryStream(Join(Frame('1'), Frame('2'), Description(1, Oid("int4"), 4), Row(["42"]), Complete(), Ready()));
        using var session = new PgQuerySession(new PgWire(stream), new PgStartupResult(
            new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        var result = Assert.Single(await session.ExecuteExtendedAsync(bound.Sql, await CatalogTask, bound.Parameters));
        Assert.Equal(42, new PgTextCodecs(await CatalogTask).Decode(result.Columns[0].TypeOid, result.Rows[0][0]));
        Assert.Contains("SELECT $1,$2,$1", Encoding.UTF8.GetString(stream.Writes[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(stream.Writes[0]), StringComparison.Ordinal);
        Assert.Contains("private'; --value", Encoding.UTF8.GetString(stream.Writes[1]), StringComparison.Ordinal);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task TypedNullAndConsistentSelectorsResolveBeforeClrInference()
    {
        var p = new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Integer, DataTypeName = "sys_catalog.int4", DbType = DbType.Int32 };
        Assert.Equal(Oid("int4"), Assert.Single((await Bind(p)).Parameters).TypeOid);
        Assert.Same(DBNull.Value, p.Value);
        Assert.Null(Assert.Single((await Bind(new KingbaseParameter("p", null) { KingbaseDbType = KingbaseDbType.Jsonb })).Parameters).Value);
        Assert.Equal(Oid("int8"), Assert.Single((await Bind(new KingbaseParameter("p", null) { DbType = DbType.Int64 })).Parameters).TypeOid);
        Assert.Equal(Oid("numeric"), Assert.Single((await Bind(new KingbaseParameter("p", null) { DataTypeName = "numeric" })).Parameters).TypeOid);
    }

    [Fact]
    public async Task ResetDbTypeClearsConflictsWithoutMutatingValue()
    {
        var p = new KingbaseParameter("p", 42) { KingbaseDbType = KingbaseDbType.Bigint, DataTypeName = "int4", DbType = DbType.Int16 };
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(p));
        p.ResetDbType();
        Assert.Equal(Oid("int4"), Assert.Single((await Bind(p)).Parameters).TypeOid);
        Assert.Equal(42, p.Value);
    }

    [Theory]
    [InlineData(ParameterDirection.Output)]
    [InlineData(ParameterDirection.InputOutput)]
    [InlineData(ParameterDirection.ReturnValue)]
    public async Task OutputDirectionsRequireTheirOwnExecutionContract(ParameterDirection direction) =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", 42) { Direction = direction }));

    [Fact]
    public async Task UntypedNullUnknownClrUnsupportedDbTypeAndExplicitMismatchReject()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(new KingbaseParameter("p", null)));
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(new KingbaseParameter("p", DBNull.Value)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", new object())));
        await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", DateTime.UtcNow)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", 1) { DbType = DbType.Byte }));
        await Assert.ThrowsAsync<InvalidCastException>(() => Bind(new KingbaseParameter("p", 42) { KingbaseDbType = KingbaseDbType.Bigint }));
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(new KingbaseParameter("p", 42) { KingbaseDbType = KingbaseDbType.Integer, DbType = DbType.Int64 }));
    }

    [Theory]
    [InlineData("private-schema.secret-type")]
    [InlineData("int4; DROP TABLE secret")]
    [InlineData("\"int4\"")]
    [InlineData("INT4")]
    [InlineData("")]
    public async Task UnknownOrNoncanonicalTypeNamesRejectWithoutEchoingNames(string name)
    {
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", 1) { DataTypeName = name }));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task SizeValidatesWholeInputWithoutTruncatingStringsOrBytes()
    {
        var p = new KingbaseParameter("p", "😀") { Size = 2 };
        Assert.Equal("😀", Assert.Single((await Bind(p)).Parameters).Value);
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(new KingbaseParameter("p", "😀") { Size = 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Bind(new KingbaseParameter("p", new byte[] { 0, 1 }) { Size = 1 }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", 42) { Size = 4 }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", 42) { Precision = 2 }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Bind(new KingbaseParameter("p", 42) { Scale = 0 }));
    }

    [Fact]
    public async Task OversizedBytesRejectBeforeCloningCallerBuffer()
    {
        var catalog = await CatalogTask;
        var c = new KingbaseParameterCollection();
        var input = new byte[4 * 1024 * 1024];
        c.AddWithValue("p", input);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentOutOfRangeException>(() => PgParameterBindings.Create("SELECT :p", c, catalog,
            KingbaseCompatibilityMode.PostgreSql, true));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, input.Length / 2);
        Assert.Same(input, c[0].Value);
    }

    [Fact]
    public async Task AggregateInputTextIsBoundedBeforeRetainingMutableCopies() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Bind("SELECT :a,:b", [
            new KingbaseParameter("a", new string('a', 600000)), new KingbaseParameter("b", new string('b', 600000))]));

    [Theory]
    [InlineData("123.4500", 5, 2)]
    [InlineData("0.001", 3, 3)]
    [InlineData("0.00", 2, 2)]
    [InlineData("12.000", 2, 0)]
    [InlineData("-999.99", 5, 2)]
    public async Task NumericConstraintsAcceptOnlyExactRepresentabilityWithoutChangingValue(string text, byte precision, byte scale)
    {
        var value = KingbaseNumeric.Parse(text);
        var p = new KingbaseParameter("p", value) { Precision = precision, Scale = scale };
        var parameter = Assert.Single((await Bind(p)).Parameters);
        Assert.Equal(text, new PgTextCodecs(await CatalogTask).Encode(parameter.TypeOid, parameter.Value));
        Assert.Equal(value, p.Value);
    }

    [Theory]
    [InlineData("123.456", 5, 2)]
    [InlineData("1000", 5, 2)]
    [InlineData("0.001", 2, 2)]
    [InlineData("1.1", 3, 0)]
    [InlineData("Infinity", 3, 1)]
    [InlineData("NaN", 3, 1)]
    public async Task NumericConstraintsNeverRoundOrOverflow(string text, byte precision, byte scale) =>
        await Assert.ThrowsAsync<OverflowException>(() => Bind(new KingbaseParameter("p", KingbaseNumeric.Parse(text)) { Precision = precision, Scale = scale }));

    [Fact]
    public async Task NumericMetadataAndCultureDoNotImplyServerTypmodOrMutateDecimal()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var p = new KingbaseParameter("p", 1.2300m) { Scale = 2, Precision = 3 };
            var value = Assert.Single((await Bind(p)).Parameters).Value;
            Assert.Equal(decimal.GetBits(1.2300m), decimal.GetBits((decimal)value!));
            await Assert.ThrowsAsync<ArgumentException>(() => Bind(new KingbaseParameter("p", null) { KingbaseDbType = KingbaseDbType.Numeric, Scale = 3, Precision = 2 }));
            Assert.Equal(Oid("numeric"), Assert.Single((await Bind(new KingbaseParameter("p", 1.234m) { Precision = 4 })).Parameters).TypeOid);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    static async Task<PgTypeCatalog> LoadCatalogAsync()
    {
        var definitions = Types.Select(type => new PgTypeDescription(Oid(type.Name), 90, type.Name, type.Size, 'b', 'U', 0, 0, 0, -1));
        using var session = new PgQuerySession(new PgWire(new QueryStream(Catalog(definitions))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql,
            KingbaseCompatibilityMode.PostgreSql, null, null));
    }
}
