using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class NativeTemporalTests
{
    public static IEnumerable<object[]> Values()
    {
        var date = new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Unspecified);
        yield return [KingbaseCompatibilityMode.Oracle, "date", "OracleDate", date.AddHours(13).AddMinutes(14).AddSeconds(15), "2024-02-29 13:14:15", DbType.DateTime];
        yield return [KingbaseCompatibilityMode.SqlServer, "date", "SqlServerDate", date, "2024-02-29", DbType.Date];
        yield return [KingbaseCompatibilityMode.SqlServer, "datetime", "SqlServerDateTime", date.AddHours(13).AddMilliseconds(123), "2024-02-29 13:00:00.123", DbType.DateTime];
        yield return [KingbaseCompatibilityMode.SqlServer, "datetime2", "SqlServerDateTime2", date.AddHours(13).AddTicks(1234567), "2024-02-29 13:00:00.1234567", DbType.DateTime2];
        yield return [KingbaseCompatibilityMode.SqlServer, "smalldatetime", "SqlServerSmallDateTime", date.AddHours(13).AddMinutes(14), "2024-02-29 13:14:00", DbType.DateTime];
        yield return [KingbaseCompatibilityMode.SqlServer, "time", "SqlServerTime", TimeSpan.FromTicks(TimeSpan.TicksPerDay - 1), "23:59:59.9999999", DbType.Time];
        yield return [KingbaseCompatibilityMode.MySql, "date", "MySqlDate", date, "2024-02-29", DbType.Date];
        yield return [KingbaseCompatibilityMode.MySql, "datetime", "MySqlDateTime", date.AddHours(13).AddTicks(1234560), "2024-02-29 13:00:00.123456", DbType.DateTime];
        yield return [KingbaseCompatibilityMode.MySql, "timestamp", "MySqlTimestamp", date.AddHours(13).AddTicks(1234560), "2024-02-29 13:00:00.123456", DbType.DateTime];
        yield return [KingbaseCompatibilityMode.MySql, "time", "MySqlTime", -TimeSpan.FromHours(25) - TimeSpan.FromTicks(10), "-25:00:00.000001", DbType.Time];
        yield return [KingbaseCompatibilityMode.MySql, "year", "MySqlYear", 1901, "1901", DbType.Int32];
    }

    [Theory]
    [MemberData(nameof(Values))]
    public async Task NativeValuesBindReadAndKeepTypedNull(KingbaseCompatibilityMode mode, string name, string selector, object value, string text, DbType dbType)
    {
        var catalog = await Load(mode); var type = catalog.ByName["sys." + name]; var codecs = new PgTextCodecs(catalog);
        Assert.Equal(value.GetType(), codecs.GetFieldType(type.Oid)); Assert.Equal(text, codecs.Encode(type.Oid, value));
        Assert.Equal(value, codecs.Decode(type.Oid, text, "ISO, MDY"));
        Assert.True(Enum.TryParse<KingbaseDbType>(selector, out var provider)); Assert.True((int)provider > (int)KingbaseDbType.Varbit);
        var parameter = new KingbaseParameter("p", value) { KingbaseDbType = provider, DbType = dbType };
        Assert.Equal(type.Oid, Bind(catalog, mode, parameter).Parameters[0].TypeOid);
        parameter.Value = DBNull.Value; Assert.Equal(type.Oid, Bind(catalog, mode, parameter).Parameters[0].TypeOid);
        Assert.Same(DBNull.Value, codecs.Decode(type.Oid, null)); Assert.Null(codecs.Encode(type.Oid, null));
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "date")]
    [InlineData(KingbaseCompatibilityMode.MySql, "date")]
    [InlineData(KingbaseCompatibilityMode.MySql, "datetime")]
    [InlineData(KingbaseCompatibilityMode.MySql, "timestamp")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "datetime2")]
    public async Task NativeCalendarResultsNeedTheirOwnConfirmedIsoSnapshot(KingbaseCompatibilityMode mode, string name)
    {
        var catalog = await Load(mode); var type = catalog.ByName["sys." + name]; var codecs = new PgTextCodecs(catalog);
        foreach (var style in new string?[] { null, "SQL, DMY", "unknown" }) Assert.Throws<NotSupportedException>(() => codecs.Decode(type.Oid, "2024-02-29 13:14:15", style));
        Assert.Same(DBNull.Value, codecs.Decode(type.Oid, null));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("base")]
    [InlineData("category")]
    [InlineData("input")]
    [InlineData("owner")]
    public async Task OracleDateRequiresTheProvenDomainAndActualTimestampBase(string mutation)
    {
        var rows = Rows(KingbaseCompatibilityMode.Oracle); var date = rows.Single(row => row[3] == "date");
        if (mutation == "kind") date[5] = "b";
        if (mutation == "base") date[8] = "1114";
        if (mutation == "category") date[6] = "S";
        if (mutation == "input") date[12] = "private_schema.ora_date_in";
        if (mutation == "owner") date[15] = "false";
        var catalog = await Load(KingbaseCompatibilityMode.Oracle, rows); Assert.False(catalog.ByName.ContainsKey("sys.date"));
        Assert.True(catalog.ByName.ContainsKey("pg_catalog.timestamp"));
    }

    [Fact]
    public async Task LogicalModesAndTemporalPrecisionCannotBeBypassedByNullOrClrValues()
    {
        foreach (var mode in new[] { KingbaseCompatibilityMode.Oracle, KingbaseCompatibilityMode.MySql, KingbaseCompatibilityMode.SqlServer })
        {
            var catalog = await Load(mode);
            foreach (var provider in new[] { KingbaseDbType.OracleDate, KingbaseDbType.MySqlDate, KingbaseDbType.SqlServerDate })
            {
                var expected = provider == KingbaseDbType.OracleDate ? KingbaseCompatibilityMode.Oracle : provider == KingbaseDbType.MySqlDate ? KingbaseCompatibilityMode.MySql : KingbaseCompatibilityMode.SqlServer;
                if (mode != expected) Assert.Throws<NotSupportedException>(() => Bind(catalog, mode, new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = provider }));
            }
            var selected = mode == KingbaseCompatibilityMode.Oracle ? KingbaseDbType.OracleDate : mode == KingbaseCompatibilityMode.MySql ? KingbaseDbType.MySqlDateTime : KingbaseDbType.SqlServerDateTime2;
            Assert.Throws<ArgumentException>(() => Bind(catalog, mode, new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = selected, DbType = DbType.DateTimeOffset }));
            Assert.Throws<InvalidCastException>(() => Bind(catalog, mode, new KingbaseParameter("p", DateTime.SpecifyKind(new DateTime(2024, 2, 29), DateTimeKind.Utc)) { KingbaseDbType = selected }));
        }
        var oracle = await Load(KingbaseCompatibilityMode.Oracle); Assert.Throws<ArgumentException>(() => Bind(oracle, KingbaseCompatibilityMode.Oracle,
            new KingbaseParameter("p", new DateTime(2024, 2, 29).AddTicks(10)) { KingbaseDbType = KingbaseDbType.OracleDate }));
        var sql = await Load(KingbaseCompatibilityMode.SqlServer); Assert.Throws<ArgumentException>(() => Bind(sql, KingbaseCompatibilityMode.SqlServer,
            new KingbaseParameter("p", new DateTime(2024, 2, 29).AddTicks(1)) { KingbaseDbType = KingbaseDbType.SqlServerDateTime }));
    }

    [Theory]
    [InlineData("mysql_time", "-838:59:59", -30203990000000L)]
    [InlineData("mysql_time", "838:59:59", 30203990000000L)]
    [InlineData("mysql_time", "838:59:58.999999", 30203989999990L)]
    [InlineData("sqlserver_time", "00:00:00.0000001", 1L)]
    public async Task NativeClockRangeAndSignedFractionAreExact(string codec, string text, long ticks)
    {
        var mode = codec == "mysql_time" ? KingbaseCompatibilityMode.MySql : KingbaseCompatibilityMode.SqlServer;
        var catalog = await Load(mode); var type = catalog.ByName["sys.time"]; var codecs = new PgTextCodecs(catalog);
        Assert.Equal(TimeSpan.FromTicks(ticks), codecs.Decode(type.Oid, text)); Assert.Equal(text, codecs.Encode(type.Oid, TimeSpan.FromTicks(ticks)));
        if (mode == KingbaseCompatibilityMode.MySql)
        {
            foreach (var invalid in new[] { "839:00:00", "838:59:59.000001", "+25:00:00", "-25:00:00.0000001", "-25:00:60", "25:00:00 private" }) Assert.Throws<FormatException>(() => codecs.Decode(type.Oid, invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => codecs.Encode(type.Oid, TimeSpan.MaxValue));
        }
    }

    [Theory]
    [InlineData("0000-00-00")]
    [InlineData("2024-02-31")]
    [InlineData("10000-01-01")]
    public async Task ZeroAndNonClrMySqlValuesRemainAvailableThroughProviderGetters(string text)
    {
        var catalog = await Load(KingbaseCompatibilityMode.MySql); var type = catalog.ByName["sys.date"];
        using var reader = new KingbaseDataReader(new[] { new PgTextResult(new[] { new PgColumnDescription("d", 0, 0, type.Oid, 4, -1) }, new[] { new string?[] { text } }, "SELECT 1", "ISO, MDY") }, catalog);
        Assert.True(reader.Read()); Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.Equal(typeof(KingbaseMySqlDate), reader.GetProviderSpecificFieldType(0));
        Assert.Throws<OverflowException>(() => reader.GetDateTime(0)); Assert.Equal(text, reader.GetFieldValue<KingbaseMySqlDate>(0).ToString());
        Assert.Equal(text, reader.GetProviderSpecificValue(0).ToString());
    }

    [Fact]
    public async Task SqlServerDateCannotSilentlyAcceptNonSqlServerCalendarValues()
    {
        var catalog = await Load(KingbaseCompatibilityMode.SqlServer);
        Assert.Throws<OverflowException>(() => Bind(catalog, KingbaseCompatibilityMode.SqlServer, new KingbaseParameter("p", KingbaseDate.PositiveInfinity) { KingbaseDbType = KingbaseDbType.SqlServerDate }));
        Assert.Throws<OverflowException>(() => Bind(catalog, KingbaseCompatibilityMode.SqlServer, new KingbaseParameter("p", new KingbaseDate(-1, 1, 1)) { KingbaseDbType = KingbaseDbType.SqlServerDate }));
        var codecs = new PgTextCodecs(catalog);
        foreach (var invalid in new[] { "infinity", "-infinity", "0001-01-01 BC", "10000-01-01" })
            Assert.Throws<OverflowException>(() => codecs.DecodeProviderSpecific(catalog.ByName["sys.date"].Oid, invalid, "ISO, MDY"));
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.MySql, KingbaseDbType.MySqlDate)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, KingbaseDbType.SqlServerDate)]
    public async Task NativeDateOnlyValuesCanBeReadAndBoundWithoutChangingTheCalendar(KingbaseCompatibilityMode mode, KingbaseDbType provider)
    {
        var catalog = await Load(mode); var type = catalog.ByName["sys.date"]; var value = new DateOnly(2024, 2, 29);
        var binding = Bind(catalog, mode, new KingbaseParameter("p", value) { KingbaseDbType = provider });
        Assert.Equal("2024-02-29", new PgTextCodecs(catalog).Encode(type.Oid, binding.Parameters[0].Value));
        using var reader = new KingbaseDataReader(new[] { new PgTextResult(new[] { new PgColumnDescription("d", 0, 0, type.Oid, 4, -1) }, new[] { new string?[] { "2024-02-29" } }, "SELECT 1", "ISO, MDY") }, catalog);
        Assert.True(reader.Read()); Assert.Equal(value, reader.GetFieldValue<DateOnly>(0));
    }

    [Theory]
    [InlineData("time", "12:34:56.")]
    [InlineData("datetime", "2024-02-29 12:34:56.")]
    [InlineData("datetime2", "2024-02-29 12:34:56.")]
    [InlineData("smalldatetime", "2024-02-29 12:34:00.")]
    public async Task NativeSqlServerFractionRequiresAnAsciiDigitAfterTheSeparator(string name, string text)
    {
        var catalog = await Load(KingbaseCompatibilityMode.SqlServer); var codecs = new PgTextCodecs(catalog);
        Assert.Throws<FormatException>(() => codecs.Decode(catalog.ByName["sys." + name].Oid, text, "ISO, MDY"));
    }

    internal static PgParameterBindings Bind(PgTypeCatalog catalog, KingbaseCompatibilityMode mode, KingbaseParameter value) =>
        PgParameterBindings.Create("SELECT :p", new KingbaseParameterCollection { value.Snapshot() }, catalog, mode, true);
    internal static Task<PgTypeCatalog> Load(KingbaseCompatibilityMode mode, List<string?[]>? rows = null)
    {
        var pg = NativeScalarTests.BaseRows();
        foreach (var (name, size) in new[] { ("date", "4"), ("time", "8"), ("timestamp", "8") }) pg.Add(["" + (10000 + pg.Count), "90", "pg_catalog", name, size, "b", "D", "t", "0", "0", "0", "-1"]);
        return NativeScalarTests.Load(mode, rows ?? Rows(mode), pg);
    }
    internal static List<string?[]> Rows(KingbaseCompatibilityMode mode)
    {
        var rows = NativeScalarTests.NativeRows(mode);
        if (mode == KingbaseCompatibilityMode.Oracle) Add("date", "8", "D", "pg_catalog.ora_date_in", "sys.ora_date_out", "d", "10010");
        if (mode == KingbaseCompatibilityMode.SqlServer)
            foreach (var name in new[] { "date", "datetime", "datetime2", "smalldatetime", "time" }) Add(name, name == "date" ? "4" : "8", "D", "sys.sqlserver_" + name + "_in", "sys.sqlserver_" + name + "_out");
        if (mode == KingbaseCompatibilityMode.MySql)
        {
            foreach (var name in new[] { "date", "datetime", "timestamp", "time" }) Add(name, name == "date" ? "4" : "8", "D", "sys.mysql_" + name + "_in", "sys.mysql_" + name + "_out");
            Add("year", "4", "N", "pg_catalog.domain_in", "pg_catalog.int4out", "d", "10002");
        }
        return rows;
        void Add(string name, string size, string category, string input, string output, string kind = "b", string baseOid = "0") =>
            rows.Add(["" + (30000 + rows.Count), "91", "sys", name, size, kind, category, "t", baseOid, "0", "0", "-1", input, output, "t", "t"]);
    }
}
