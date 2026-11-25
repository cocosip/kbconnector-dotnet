using KingbaseConnector.Protocol;
using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgIntervalTests
{
    const uint IntervalOid = 7020;
    static readonly Task<PgTypeCatalog> Catalog = PgTextCodecsTests.LoadCatalog();
    [Fact]
    public async Task IntervalHasStableLosslessProviderMetadata()
    {
        var codecs = new PgTextCodecs(await PgTextCodecsTests.LoadCatalog());
        Assert.Equal("KingbaseInterval", codecs.GetFieldType(7020).Name);
        Assert.Same(codecs.GetFieldType(7020), codecs.GetProviderSpecificFieldType(7020));
        Assert.Same(DBNull.Value, codecs.Decode(7020, null));
    }

    [Theory]
    [InlineData("postgres", "1 year 2 mons 3 days 04:05:06.123456", 14, 3, 14706123456L)]
    [InlineData("kingbase", "1 year 2 mons 3 days 04:05:06.123456", 14, 3, 14706123456L)]
    [InlineData("postgres", "-1 years -2 mons +3 days -00:00:00.000001", -14, 3, -1L)]
    [InlineData("postgres", "00:00:00", 0, 0, 0L)]
    [InlineData("postgres", "24:00:00", 0, 0, 86400000000L)]
    [InlineData("postgres", "1 day", 0, 1, 0L)]
    [InlineData("postgres", "-178956970 years -8 mons -2147483648 days -2562047788:00:54.775808", int.MinValue, int.MinValue, long.MinValue)]
    [InlineData("postgres_verbose", "@ 1 year 2 mons 3 days 4 hours 5 mins 6.123456 secs", 14, 3, 14706123456L)]
    [InlineData("kingbase_verbose", "@ 1 year 2 mons 3 days 4 hours 5 mins 6.123456 secs", 14, 3, 14706123456L)]
    [InlineData("postgres_verbose", "@ 1 year 2 mons 3 days 4 hours 5 mins 6.000001 secs ago", -14, -3, -14706000001L)]
    [InlineData("postgres_verbose", "@ 1 year 2 mons -3 days 0.000001 secs ago", -14, 3, -1L)]
    [InlineData("postgres_verbose", "@ 0", 0, 0, 0L)]
    [InlineData("postgres_verbose", "@ 2562047788 hours 54.775808 secs ago", 0, 0, long.MinValue)]
    [InlineData("postgres_verbose", "@ 178956970 years 8 mons 2147483648 days ago", int.MinValue, int.MinValue, 0L)]
    [InlineData("sql_standard", "+1-2 +3 +4:05:06.123456", 14, 3, 14706123456L)]
    [InlineData("sql_standard", "-1-2 +3 -0:00:00.000001", -14, 3, -1L)]
    [InlineData("sql_standard", "-3 4:05:06.000001", 0, -3, -14706000001L)]
    [InlineData("sql_standard", "3 0:00:00", 0, 3, 0L)]
    [InlineData("sql_standard", "-0-2", -2, 0, 0L)]
    [InlineData("sql_standard", "0:00:00.000001", 0, 0, 1L)]
    [InlineData("sql_standard", "0", 0, 0, 0L)]
    [InlineData("sql_standard", "+178956970-7 +2147483647 +2562047788:00:54.775807", int.MaxValue, int.MaxValue, long.MaxValue)]
    [InlineData("iso_8601", "P1Y2M3DT4H5M6.123456S", 14, 3, 14706123456L)]
    [InlineData("iso_8601", "P-1Y-2M3DT-0.000001S", -14, 3, -1L)]
    [InlineData("iso_8601", "PT0S", 0, 0, 0L)]
    public async Task EveryConfirmedOutputStylePreservesIndependentSignedComponents(string style, string text, int months, int days, long microseconds)
    {
        var codecs = new PgTextCodecs(await Catalog); var expected = new KingbaseInterval(months, days, microseconds);
        Assert.Equal(expected, codecs.Decode(IntervalOid, text, intervalStyle: style));
        Assert.Equal(expected, codecs.DecodeProviderSpecific(IntervalOid, text, intervalStyle: style));
        Assert.Equal(expected.ToString(), codecs.Encode(IntervalOid, expected));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("POSTGRES")] [InlineData("unsupported")]
    public async Task MissingOrUnsupportedStyleRejectsNonNullWithoutChangingMetadata(string? style)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Throws<NotSupportedException>(() => codecs.Decode(IntervalOid, "00:00:00", intervalStyle: style));
        Assert.Same(DBNull.Value, codecs.DecodeProviderSpecific(IntervalOid, null, intervalStyle: style));
        Assert.Equal(typeof(KingbaseInterval), codecs.GetFieldType(IntervalOid));
    }

    [Theory]
    [InlineData("postgres", "private-invalid")]
    [InlineData("postgres", "1 day 2 days")] [InlineData("postgres", "1 mon 2 years")]
    [InlineData("postgres", "1 day  01:00:00")] [InlineData("postgres", "01:00:00 ago")]
    [InlineData("postgres", "01:60:00")] [InlineData("postgres", "00:00:60")]
    [InlineData("postgres", "00:00:00.0000001")] [InlineData("postgres", "2562047788:00:54.775808")]
    [InlineData("postgres", "-2562047788:00:54.775809")]
    [InlineData("postgres_verbose", "1 day")] [InlineData("postgres_verbose", "@ 1 day ago private")]
    [InlineData("postgres_verbose", "@ 0.0000001 secs")]
    [InlineData("postgres_verbose", "@ -1 day ago")]
    [InlineData("kingbase_verbose", "@ -2147483648 days ago")]
    [InlineData("kingbase_verbose", "@ -2147483648 hours ago")]
    [InlineData("sql_standard", "+1-12 +0 +0:00:00")]
    [InlineData("sql_standard", "1-2 3 4:00:00")] [InlineData("sql_standard", "1-2 3")]
    [InlineData("sql_standard", "0:00:-1")] [InlineData("sql_standard", " 0")]
    [InlineData("iso_8601", "00:00:00")] [InlineData("iso_8601", "infinity")]
    public async Task MalformedOrOutOfRangeResultsRejectWithoutEchoOrInnerException(string style, string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        var error = Assert.Throws<FormatException>(() => codecs.Decode(IntervalOid, text, intervalStyle: style));
        Assert.Equal("Invalid or out-of-range PG INTERVAL text.", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task CatalogIdentityBindingInferenceAndTimeSelectionRemainExact()
    {
        var catalog = await Catalog; var codecs = new PgTextCodecs(catalog);
        foreach (var parameter in new[]
        {
            new KingbaseParameter("p", new KingbaseInterval(1, -2, -1)),
            new KingbaseParameter("p", TimeSpan.FromTicks(-10)),
            new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Interval },
            new KingbaseParameter("p", DBNull.Value) { DataTypeName = "pg_catalog.interval" }
        })
        {
            Assert.Equal(IntervalOid, Bind(parameter, catalog).TypeOid);
            Assert.Equal(parameter.DataTypeName is null ? DbType.Object : DbType.String, parameter.DbType);
        }
        Assert.Equal(KingbaseDbType.Interval, new KingbaseParameter("p", TimeSpan.Zero).KingbaseDbType);
        Assert.Equal(7016u, Bind(new KingbaseParameter("p", TimeSpan.FromTicks(10)) { DbType = DbType.Time }, catalog).TypeOid);
        Assert.Equal("PT-0.000001S", codecs.Encode(IntervalOid, TimeSpan.FromTicks(-10)));
        Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", TimeSpan.FromTicks(1)), catalog));
        foreach (var value in new object[] { "PT1S", 1, DateTime.MinValue, default(KingbaseTimeTz) })
            Assert.Throws<InvalidCastException>(() => codecs.Encode(IntervalOid, value));
        Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Interval, DbType = DbType.Time }, catalog));
        Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Interval, Scale = 0 }, catalog));
        Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Interval, Size = 1 }, catalog));
        Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", TimeSpan.Zero) { DbType = DbType.Object }, catalog));
        var wrong = new PgTextCodecs(await PgTextCodecsTests.LoadCatalog(change: t => t.Name == "interval" ?
            new PgTypeDescription(t.Oid, t.NamespaceOid, t.Name, 8, t.Kind, t.Category, 0, 0, 0, -1) : t));
        Assert.Throws<NotSupportedException>(() => wrong.Encode(IntervalOid, null));
        var shifted = new PgTextCodecs(await PgTextCodecsTests.LoadCatalog(first: 8100));
        Assert.Equal(default(KingbaseInterval), shifted.Decode(8120, "PT0S", intervalStyle: "iso_8601"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BufferedResultsRetainTheirIntervalStyleBeforeAndAfterCompletion(bool afterCompletion)
    {
        var change = Status("IntervalStyle", "postgres_verbose");
        var input = Join(Status("IntervalStyle", "postgres"), Description(1, IntervalOid, 16), Row(["1 mon -2 days -00:00:00.000001"]),
            afterCompletion ? Join(Complete(), change) : Join(change, Complete()),
            Description(1, IntervalOid, 16), Row(["@ 1 mon -2 days -0.000001 secs"]), Complete(), Ready());
        using var session = Session(input);
        using var reader = new KingbaseDataReader(await session.ExecuteAsync("SELECT first; SELECT second"), await Catalog);
        Assert.True(reader.Read()); Assert.Equal(new KingbaseInterval(1, -2, -1), reader.GetValue(0));
        Assert.True(reader.NextResult()); Assert.True(reader.Read()); Assert.Equal(new KingbaseInterval(1, -2, -1), reader.GetValue(0));
    }

    [Fact]
    public async Task MixedIntervalStylesRejectAndDoNotDamageIndependentDateConfirmation()
    {
        var mixed = Join(Status("IntervalStyle", "postgres"), Description(1, IntervalOid, 16), Row(["00:00:00"]),
            Status("IntervalStyle", "iso_8601"), Row(["PT0S"]), Status("IntervalStyle", "postgres"), Row(["00:00:00"]), Complete(), Ready());
        using (var session = Session(mixed))
        using (var reader = new KingbaseDataReader(await session.ExecuteAsync("SELECT mixed"), await Catalog))
        {
            Assert.True(reader.Read()); Assert.Throws<NotSupportedException>(() => reader.GetValue(0));
            Assert.Throws<NotSupportedException>(() => reader.GetProviderSpecificValue(0));
        }
        var date = Join(Status("DateStyle", "ISO, MDY"), Status("IntervalStyle", "postgres"), Description(1, 7015, 4), Row(["2024-01-02"]),
            Status("IntervalStyle", "iso_8601"), Row(["2024-01-03"]), Complete(), Ready());
        using (var session = Session(date))
        using (var reader = new KingbaseDataReader(await session.ExecuteAsync("SELECT dates"), await Catalog))
        {
            Assert.True(reader.Read()); Assert.Equal(new DateTime(2024, 1, 2), reader.GetDateTime(0));
        }
        var interval = Join(Status("IntervalStyle", "iso_8601"), Description(1, IntervalOid, 16), Row(["PT0S"]),
            Status("DateStyle", "SQL, DMY"), Row(["PT0S"]), Complete(), Ready());
        using (var session = Session(interval))
        using (var reader = new KingbaseDataReader(await session.ExecuteAsync("SELECT intervals"), await Catalog))
        {
            Assert.True(reader.Read()); Assert.Equal(default(KingbaseInterval), reader.GetValue(0));
        }
    }

    [Fact]
    public async Task ReaderKeepsStableMetadataAndProviderRecoveryAfterExplicitTimeSpanFailures()
    {
        using var reader = new KingbaseDataReader([new PgTextResult([new("i", 0, 0, IntervalOid, 16, -1)],
            [["P1M"], ["PT2562047788H54.775807S"], ["P1DT-0.000001S"], [null]], "SELECT 4", intervalStyle: "iso_8601")], await Catalog);
        Assert.Equal(typeof(KingbaseInterval), reader.GetColumnSchema()[0].DataType);
        Assert.Equal(typeof(KingbaseInterval), reader.GetProviderSpecificFieldType(0));
        Assert.True(reader.Read()); Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeSpan>(0));
        Assert.Equal(new KingbaseInterval(1, 0, 0), reader.GetProviderSpecificValue(0));
        Assert.True(reader.Read()); Assert.Throws<OverflowException>(() => reader.GetFieldValue<TimeSpan>(0));
        Assert.Equal(long.MaxValue, reader.GetFieldValue<KingbaseInterval>(0).Microseconds);
        Assert.True(reader.Read()); Assert.Equal(TimeSpan.FromTicks(863999999990), reader.GetFieldValue<TimeSpan>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeOnly>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.Same(DBNull.Value, reader.GetValue(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeSpan>(0));
    }

    [Fact]
    public async Task PublicPreparedIntervalRejectsPrecisionBeforeIoAndRetainsReaderOwnership()
    {
        using var listener = PublicExecutionTests.Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            var prepare = await PublicPreparedTests.ReadFrames(stream, "PS", guard.Token);
            Assert.Equal(9012u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(prepare[0].AsSpan(prepare[0].Length - 4)));
            await stream.WriteAsync(Join(Frame('1'), Ready()), guard.Token);
            for (var index = 0; index < 2; index++)
            {
                var execute = await PublicPreparedTests.ReadFrames(stream, "BDES", guard.Token);
                Assert.Contains("P1M-2DT-0.000001S", Encoding.UTF8.GetString(execute[0]));
                await stream.WriteAsync(Join(Status("IntervalStyle", "postgres_verbose"), Frame('2'), Description(1, 9012, 16),
                    Row(["@ 1 mon -2 days -0.000001 secs"]), Complete(), Status("IntervalStyle", "iso_8601"), Ready()), guard.Token);
            }
            await PublicPreparedTests.ReadFrames(stream, "CS", guard.Token); await stream.WriteAsync(Join(Frame('3'), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.Add(new KingbaseParameter("p", TimeSpan.FromTicks(1)) { KingbaseDbType = KingbaseDbType.Interval });
        await Assert.ThrowsAsync<ArgumentException>(() => command.PrepareAsync(guard.Token));
        command.Parameters[0].Value = "private-invalid";
        await Assert.ThrowsAsync<InvalidCastException>(() => command.PrepareAsync(guard.Token));
        command.Parameters[0].Value = new KingbaseInterval(1, -2, -1); await command.PrepareAsync(guard.Token);
        for (var index = 0; index < 2; index++)
        {
            using var reader = await command.ExecuteReaderAsync(guard.Token); Assert.True(reader.Read());
            Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeSpan>(0));
            Assert.Equal(new KingbaseInterval(1, -2, -1), reader.GetValue(0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(guard.Token));
        }
        await command.UnprepareAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    static PgQuerySession Session(byte[] input) => new(new PgWire(new QueryStream(input)),
        new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
    static PgQueryParameter Bind(KingbaseParameter parameter, PgTypeCatalog catalog)
    {
        var parameters = new KingbaseParameterCollection(); parameters.Add(parameter);
        try { return Assert.Single(PgParameterBindings.Create("SELECT @p", parameters, catalog, KingbaseCompatibilityMode.PostgreSql, true).Parameters); }
        finally { parameters.Clear(); }
    }
}
