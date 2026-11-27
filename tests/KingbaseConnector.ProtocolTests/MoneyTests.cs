using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

// T29: pg_catalog.money (int64 cents) and SQL Server mode's sys.money (int64 ten-thousandths).
public sealed class MoneyTests
{
    [Theory]
    [InlineData("$1,234.56", 1234.56)]
    [InlineData("-$12.50", -12.5)]
    [InlineData("$0.00", 0)]
    [InlineData("1234.56", 1234.56)]
    public async Task MoneyTextDecodesTheCurrencyForm(string text, decimal expected) =>
        Assert.Equal(expected, new PgTextCodecs(await LoadAll(KingbaseCompatibilityMode.PostgreSql)).Decode(Oid(await LoadAll(KingbaseCompatibilityMode.PostgreSql), "pg_catalog.money"), text));

    [Fact]
    public async Task MoneyTextCoversTheInt64Range()
    {
        var catalog = await LoadAll(KingbaseCompatibilityMode.PostgreSql);
        var codecs = new PgTextCodecs(catalog);
        var oid = Oid(catalog, "pg_catalog.money");
        Assert.Equal(92233720368547758.07m, codecs.Decode(oid, "$92,233,720,368,547,758.07"));
        Assert.Equal(-92233720368547758.08m, codecs.Decode(oid, "-$92,233,720,368,547,758.08"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$")]
    [InlineData("-$")]
    [InlineData("abc")]
    [InlineData("$ 1")]
    [InlineData("$1.23.4")]
    [InlineData("1,234.56.7")]
    [InlineData("1.234,56")]
    [InlineData("$12,34.56")]
    [InlineData("$1,2345.00")]
    [InlineData("$1.")]
    public async Task MalformedMoneyTextIsRejected(string text)
    {
        var catalog = await LoadAll(KingbaseCompatibilityMode.PostgreSql);
        Assert.Throws<FormatException>(() => new PgTextCodecs(catalog).Decode(Oid(catalog, "pg_catalog.money"), text));
    }

    [Theory]
    [InlineData("1234.5600", 1234.56)]
    [InlineData("-1.5000", -1.5)]
    [InlineData("0.0000", 0)]
    public async Task SqlServerMoneyTextDecodesFourDecimals(string text, decimal expected)
    {
        var catalog = await LoadAll(KingbaseCompatibilityMode.SqlServer);
        Assert.Equal(expected, new PgTextCodecs(catalog).Decode(Oid(catalog, "sys.money"), text));
    }

    [Fact]
    public async Task SqlServerMoneyTextRejectsThePgCurrencyForm()
    {
        var catalog = await LoadAll(KingbaseCompatibilityMode.SqlServer);
        Assert.Throws<FormatException>(() => new PgTextCodecs(catalog).Decode(Oid(catalog, "sys.money"), "$1,234.56"));
    }

    [Fact]
    public async Task MoneyEncodingIsInvariantAndUnformatted()
    {
        var catalog = await LoadAll(KingbaseCompatibilityMode.PostgreSql);
        var codecs = new PgTextCodecs(catalog);
        var oid = Oid(catalog, "pg_catalog.money");
        Assert.Equal("1234.56", codecs.Encode(oid, 1234.56m));
        Assert.Equal("-12.5", codecs.Encode(oid, -12.5m));
        Assert.Equal("1234.5678", codecs.Encode(oid, 1234.5678m));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(oid, 1234.56));
    }

    [Fact]
    public void MoneyBinaryCopyUsesAScaledInt64()
    {
        Assert.Equal("000000000001E240", Convert.ToHexString(PgBinaryCopyCodec.Encode("money", 1234.56m, 1024)!));
        Assert.Equal("0000000000BC6100", Convert.ToHexString(PgBinaryCopyCodec.Encode("sqlserver_money", 1234.56m, 1024)!));
        Assert.Equal(1234.56m, PgBinaryCopyCodec.Decode("money", Convert.FromHexString("000000000001E240")));
        Assert.Equal(1234.56m, PgBinaryCopyCodec.Decode("sqlserver_money", Convert.FromHexString("0000000000BC6100")));
        Assert.Equal("0000000000000001", Convert.ToHexString(PgBinaryCopyCodec.Encode("money", 0.005m, 1024)!));
        Assert.Equal("FFFFFFFFFFFFFFFF", Convert.ToHexString(PgBinaryCopyCodec.Encode("money", -0.005m, 1024)!));
        Assert.Equal(-12.5m, PgBinaryCopyCodec.Decode("money", Convert.FromHexString("FFFFFFFFFFFFFB1E")));
        Assert.Throws<InvalidCastException>(() => PgBinaryCopyCodec.Encode("money", 12, 1024));
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("money", 92233720368547759m, 1024));
        Assert.Throws<IOException>(() => PgBinaryCopyCodec.Decode("money", new byte[4]));
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, KingbaseDbType.Money, "pg_catalog.money")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, KingbaseDbType.Money, "pg_catalog.money")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, KingbaseDbType.SqlServerMoney, "sys.money")]
    public async Task MoneySelectorsResolveToTheirCatalogTypes(KingbaseCompatibilityMode mode, KingbaseDbType selector, string name)
    {
        var catalog = await LoadAll(mode);
        var values = new KingbaseParameterCollection { new KingbaseParameter("p", 1.5m) { KingbaseDbType = selector } };
        var binding = PgParameterBindings.Create("SELECT :p", values, catalog, mode, true);
        Assert.Equal(Oid(catalog, name), binding.Parameters[0].TypeOid);
        Assert.True(KingbaseParameterTypes.Compatible(System.Data.DbType.Decimal, selector));
    }

    [Fact]
    public async Task MoneyBinaryCopyResolvesTheSelectors()
    {
        var catalog = await LoadAll(KingbaseCompatibilityMode.SqlServer);
        Assert.Equal(["money"], PgBinaryCopyCodec.Resolve(catalog, [KingbaseDbType.Money]));
        Assert.Equal(["sqlserver_money"], PgBinaryCopyCodec.Resolve(catalog, [KingbaseDbType.SqlServerMoney]));
        var withoutNative = await LoadAll(KingbaseCompatibilityMode.MySql);
        Assert.Throws<NotSupportedException>(() => PgBinaryCopyCodec.Resolve(withoutNative, [KingbaseDbType.SqlServerMoney]));
    }

    [Fact]
    public void SqlServerMoneyBinaryCopyScalesByTenThousandths()
    {
        Assert.Equal("0000000000000001", Convert.ToHexString(PgBinaryCopyCodec.Encode("sqlserver_money", 0.00005m, 1024)!));
        Assert.Equal(0.0001m, PgBinaryCopyCodec.Decode("sqlserver_money", Convert.FromHexString("0000000000000001")));
        Assert.Equal("8000000000000000", Convert.ToHexString(PgBinaryCopyCodec.Encode("sqlserver_money", -922337203685477.5808m, 1024)!));
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("sqlserver_money", 922337203685478m, 1024));
    }

    [Fact]
    public void MoneyBinaryCopyCoversTheInt64Extremes()
    {
        Assert.Equal("7FFFFFFFFFFFFFFF", Convert.ToHexString(PgBinaryCopyCodec.Encode("money", 92233720368547758.07m, 1024)!));
        Assert.Equal(-92233720368547758.08m, PgBinaryCopyCodec.Decode("money", Convert.FromHexString("8000000000000000")));
        Assert.Equal("8000000000000000", Convert.ToHexString(PgBinaryCopyCodec.Encode("money", -92233720368547758.08m, 1024)!));
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("money", 92233720368547758.08m, 1024));
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("money", -92233720368547758.09m, 1024));
        Assert.Throws<ArgumentException>(() => PgBinaryCopyCodec.Encode("money", decimal.MaxValue, 1024));
    }

    [Fact]
    public async Task StrayNativeMoneyIsIgnoredOutsideSqlServerMode()
    {
        var baseRows = NativeScalarTests.BaseRows();
        baseRows.Add(Fields(790, "90", "pg_catalog", "money", "8", "N"));
        string?[] nativeRow = [.. Fields(5069, "91", "sys", "money", "8", "N"), "sys.sqlserver_money_in", "sys.sqlserver_money_out", "t", "t"];
        var stray = new List<string?[]> { nativeRow };
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.MySql, nativeRows: stray, baseRows: baseRows);
        Assert.True(catalog.ByName.ContainsKey("pg_catalog.money"));
        Assert.False(catalog.ByName.ContainsKey("sys.money"));
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public async Task DataTypesDescribesMoneyOnceWithoutTheNativeSelector(KingbaseCompatibilityMode mode)
    {
        var catalog = await LoadAll(mode);
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        using var table = KingbaseSchema.Create("DataTypes", version!, catalog);
        var rows = table.Rows.Cast<System.Data.DataRow>().Where(row => (string)row["TypeName"] == "money").ToList();
        Assert.Single(rows);
        Assert.Equal((int)KingbaseDbType.Money, (int)rows[0]["ProviderDbType"]);
    }

    static uint Oid(PgTypeCatalog catalog, string name) => catalog.ByName[name].Oid;

    internal static Task<PgTypeCatalog> LoadAll(KingbaseCompatibilityMode mode)
    {
        var baseRows = NativeScalarTests.BaseRows();
        baseRows.Add(Fields(790, "90", "pg_catalog", "money", "8", "N"));
        var nativeRows = new List<string?[]>();
        if (mode == KingbaseCompatibilityMode.SqlServer)
            nativeRows.Add([.. Fields(5069, "91", "sys", "money", "8", "N"), "sys.sqlserver_money_in", "sys.sqlserver_money_out", "t", "t"]);
        return NativeScalarTests.Load(mode, nativeRows: nativeRows, baseRows: baseRows);
    }

    static string?[] Fields(int oid, string ns, string schema, string name, string size, string category) =>
        [oid.ToString(System.Globalization.CultureInfo.InvariantCulture), ns, schema, name, size, "b", category, "t", "0", "0", "0", "-1"];
}
