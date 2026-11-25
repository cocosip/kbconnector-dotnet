using KingbaseConnector.Protocol;
using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgTimeTests
{
    static readonly Task<PgTypeCatalog> Catalog = PgTextCodecsTests.LoadCatalog();
    const uint TimeOid = 7016, TimeTzOid = 7017;

    [Fact]
    public async Task TimeUsesTimeSpanWithoutLosingMicrosecondsOrEndOfDay()
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal(typeof(TimeSpan), codecs.GetFieldType(TimeOid));
        Assert.Equal(TimeSpan.FromTicks(10), codecs.Decode(TimeOid, "00:00:00.000001"));
        Assert.Equal(TimeSpan.FromDays(1), codecs.Decode(TimeOid, "24:00:00"));
        Assert.Equal("23:59:59.999999", codecs.Encode(TimeOid, TimeSpan.FromTicks(863999999990)));
    }

    [Theory]
    [InlineData("00:00:00", 0L)]
    [InlineData("12:34:56.123456", 452961234560L)]
    [InlineData("23:59:59.999999", 863999999990L)]
    [InlineData("24:00:00", 864000000000L)]
    public async Task TimeInputAndResultUseIndependentExactTickValues(string text, long ticks)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal(TimeSpan.FromTicks(ticks), codecs.Decode(TimeOid, text));
        Assert.Equal(text, codecs.Encode(TimeOid, TimeSpan.FromTicks(ticks)));
        Assert.Equal(typeof(TimeSpan), codecs.GetProviderSpecificFieldType(TimeOid));
        Assert.Equal(codecs.Decode(TimeOid, text), codecs.DecodeProviderSpecific(TimeOid, text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01:02")]
    [InlineData("1:02:03")]
    [InlineData("01:2:03")]
    [InlineData("01:02:3")]
    [InlineData("24:01:00")]
    [InlineData("24:00:00.000001")]
    [InlineData("00:00:00.0000000")]
    [InlineData("00:00:00.")]
    [InlineData("00:00:60")]
    [InlineData("00:60:00")]
    [InlineData("-01:00:00")]
    [InlineData("25:00:00")]
    [InlineData("00:00:00+00")]
    [InlineData("00:00:00Z")]
    [InlineData("00:00:00 ")]
    [InlineData("00:00:00\0")]
    [InlineData("private-time")]
    public async Task InvalidTimeResultsDoNotCoerceOrExposeInput(string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        var error = Assert.Throws<FormatException>(() => codecs.Decode(TimeOid, text));
        Assert.Null(error.InnerException); if (text.Length != 0) Assert.DoesNotContain(text, error.Message);
    }

    [Fact]
    public async Task ActualCatalogOidAndLayoutGovernBothTemporalTypesEvenForNull()
    {
        var other = new PgTextCodecs(await PgTextCodecsTests.LoadCatalog(8100));
        Assert.Throws<NotSupportedException>(() => other.Decode(TimeOid, null));
        Assert.Equal(TimeSpan.Zero, other.Decode(8116, "00:00:00"));
        Assert.Equal(default(KingbaseTimeTz), other.Decode(8117, "00:00:00+00"));
        foreach (var name in new[] { "time", "timetz" })
        {
            var oid = name == "time" ? TimeOid : TimeTzOid;
            foreach (var shape in new[] { "size", "domain", "element", "base" })
            {
                var catalog = await PgTextCodecsTests.LoadCatalog(change: t => t.Name != name ? t :
                    new PgTypeDescription(t.Oid, t.NamespaceOid, t.Name, shape == "size" ? (short)4 : t.Size,
                        shape == "domain" ? 'd' : 'b', t.Category, shape == "base" ? 1u : 0, shape == "element" ? 1u : 0, 0, -1));
                var codecs = new PgTextCodecs(catalog);
                Assert.Throws<NotSupportedException>(() => codecs.Decode(oid, null));
                Assert.Throws<NotSupportedException>(() => codecs.Encode(oid, null));
            }
        }
    }

    [Fact]
    public async Task ExplicitTimeAndInferredTimeTzBindWithoutPrecisionOrConstraintLoss()
    {
        var catalog = await Catalog; var codecs = new PgTextCodecs(catalog);
        foreach (var p in new[] {
            new KingbaseParameter("p", TimeSpan.FromTicks(10)) { DbType = DbType.Time },
            new KingbaseParameter("p", TimeSpan.FromTicks(10)) { DataTypeName = "pg_catalog.time" },
            new KingbaseParameter("p", new TimeOnly(10L)) })
        {
            var bound = Bind(p, catalog); Assert.Equal(TimeOid, bound.TypeOid);
            Assert.Equal("00:00:00.000001", codecs.Encode(bound.TypeOid, bound.Value));
            Assert.Equal(p.DataTypeName is null ? DbType.Time : DbType.Object, p.DbType);
        }
        foreach (var p in new[] {
            new KingbaseParameter("p", KingbaseTimeTz.Parse("24:00:00-15:59:59")),
            new KingbaseParameter("p", KingbaseTimeTz.Parse("24:00:00-15:59:59")) { DataTypeName = "pg_catalog.timetz", KingbaseDbType = KingbaseDbType.TimeTz, DbType = DbType.Time } })
        {
            var bound = Bind(p, catalog); Assert.Equal(TimeTzOid, bound.TypeOid);
            Assert.Equal("24:00:00-15:59:59", codecs.Encode(bound.TypeOid, bound.Value));
            Assert.Equal(typeof(KingbaseTimeTz), codecs.GetFieldType(bound.TypeOid));
            Assert.Equal(codecs.Decode(bound.TypeOid, "24:00:00-15:59:59"), codecs.DecodeProviderSpecific(bound.TypeOid, "24:00:00-15:59:59"));
        }
        foreach (var type in new[] { KingbaseDbType.Time, KingbaseDbType.TimeTz })
        {
            var bound = Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type }, catalog);
            Assert.Null(codecs.Encode(bound.TypeOid, bound.Value)); Assert.Same(DBNull.Value, codecs.Decode(bound.TypeOid, null));
            Assert.Same(DBNull.Value, codecs.DecodeProviderSpecific(bound.TypeOid, null));
            Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type, DbType = DbType.String }, catalog));
            Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type, Scale = 0 }, catalog));
            Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type, Size = 1 }, catalog));
        }
        Assert.Equal(7020u, Bind(new KingbaseParameter("p", TimeSpan.Zero), catalog).TypeOid);
        foreach (var value in new object[] { "00:00:00", DateTime.MinValue, DateTimeOffset.MinValue, default(KingbaseTimeTz) })
            Assert.Throws<InvalidCastException>(() => codecs.Encode(TimeOid, value));
        foreach (var value in new object[] { "00:00:00+00", TimeSpan.Zero, TimeOnly.MinValue, DateTimeOffset.MinValue })
            Assert.Throws<InvalidCastException>(() => codecs.Encode(TimeTzOid, value));
        foreach (var ticks in new[] { -10L, TimeSpan.TicksPerDay + 10 })
            Assert.Throws<ArgumentOutOfRangeException>(() => codecs.Encode(TimeOid, TimeSpan.FromTicks(ticks)));
        Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", TimeSpan.FromTicks(1)) { DbType = DbType.Time }, catalog));
        Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", new TimeOnly(1L)), catalog));
    }

    [Fact]
    public async Task ReaderKeepsStableTimeMappingsWithEndOfDayNullAndExplicitTimeOnly()
    {
        using var reader = new KingbaseDataReader([new PgTextResult([
            new PgColumnDescription("t", 0, 0, TimeOid, 8, -1), new PgColumnDescription("tz", 0, 0, TimeTzOid, 12, -1)],
            [["12:34:56.123456", "12:34:56.123456+05:30:01"], ["24:00:00", "24:00:00-15:59:59"], [null, null]], "SELECT 3")], await Catalog);
        Assert.Equal(typeof(TimeSpan), reader.GetFieldType(0)); Assert.Equal(typeof(KingbaseTimeTz), reader.GetProviderSpecificFieldType(1));
        Assert.Equal(typeof(KingbaseTimeTz), reader.GetColumnSchema()[1].DataType);
        Assert.True(reader.Read()); Assert.Equal(new TimeOnly(452961234560L), reader.GetFieldValue<TimeOnly>(0));
        Assert.Equal("12:34:56.123456+05:30:01", reader.GetFieldValue<KingbaseTimeTz>(1).ToString());
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeOnly>(1));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeSpan>(1));
        Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
        Assert.True(reader.Read()); Assert.Equal(TimeSpan.FromDays(1), reader.GetValue(0));
        Assert.Throws<OverflowException>(() => reader.GetFieldValue<TimeOnly>(0));
        var values = new object[2]; Assert.Equal(2, reader.GetProviderSpecificValues(values));
        Assert.Equal(TimeSpan.FromDays(1), values[0]); Assert.Equal(KingbaseTimeTz.Parse("24:00:00-15:59:59"), values[1]);
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1));
        Assert.Same(DBNull.Value, reader.GetValue(0)); Assert.Same(DBNull.Value, reader.GetProviderSpecificValue(1));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeOnly>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<KingbaseTimeTz>(1));
    }

    static PgQueryParameter Bind(KingbaseParameter p, PgTypeCatalog catalog)
    {
        var parameters = new KingbaseParameterCollection(); parameters.Add(p);
        try { return Assert.Single(PgParameterBindings.Create("SELECT @p", parameters, catalog, KingbaseCompatibilityMode.PostgreSql, true).Parameters); }
        finally { parameters.Clear(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicPreparedTimeValidatesBeforeIoAndReaderFailureLeavesStatementReusable(bool zoned)
    {
        using var listener = PublicExecutionTests.Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var oid = zoned ? 9009u : 9008u; var size = zoned ? (short)12 : (short)8;
        var text = zoned ? "24:00:00-00:00:01" : "24:00:00";
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            var prepare = await PublicPreparedTests.ReadFrames(stream, "PS", guard.Token);
            Assert.Equal(oid, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(prepare[0].AsSpan(prepare[0].Length - 4)));
            await stream.WriteAsync(Join(Frame('1'), Ready()), guard.Token);
            for (var i = 0; i < 2; i++)
            {
                var execute = await PublicPreparedTests.ReadFrames(stream, "BDES", guard.Token);
                Assert.Contains(text, Encoding.UTF8.GetString(execute[0]));
                await stream.WriteAsync(Join(Status("DateStyle", "SQL, DMY"), Frame('2'), Description(1, oid, size), Row([text]), Complete(), Ready()), guard.Token);
            }
            await PublicPreparedTests.ReadFrames(stream, "CS", guard.Token); await stream.WriteAsync(Join(Frame('3'), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.Add(new KingbaseParameter("p", zoned ? DateTimeOffset.MinValue : TimeSpan.FromTicks(1))
            { KingbaseDbType = zoned ? KingbaseDbType.TimeTz : KingbaseDbType.Time });
        if (zoned) await Assert.ThrowsAsync<InvalidCastException>(() => command.PrepareAsync(guard.Token));
        else await Assert.ThrowsAsync<ArgumentException>(() => command.PrepareAsync(guard.Token));
        command.Parameters[0].Value = zoned ? KingbaseTimeTz.Parse(text) : TimeSpan.FromDays(1);
        await command.PrepareAsync(guard.Token);
        for (var i = 0; i < 2; i++)
        {
            using var reader = await command.ExecuteReaderAsync(guard.Token);
            Assert.True(reader.Read()); Assert.Equal(zoned ? typeof(KingbaseTimeTz) : typeof(TimeSpan), reader.GetFieldType(0));
            if (zoned)
            {
                Assert.Equal(text, reader.GetFieldValue<KingbaseTimeTz>(0).ToString());
                Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<TimeOnly>(0));
            }
            else
            {
                Assert.Equal(TimeSpan.FromDays(1), reader.GetValue(0));
                Assert.Throws<OverflowException>(() => reader.GetFieldValue<TimeOnly>(0));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(guard.Token));
        }
        await command.UnprepareAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
