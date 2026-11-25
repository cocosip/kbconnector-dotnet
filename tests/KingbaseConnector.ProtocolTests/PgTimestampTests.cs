using KingbaseConnector.Protocol;
using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgTimestampTests
{
    static readonly Task<PgTypeCatalog> Catalog = PgTextCodecsTests.LoadCatalog();
    const uint TimestampOid = 7018, TimestampTzOid = 7019;
    [Fact]
    public async Task TimestampPreservesCalendarClockMicrosecondsAndUnspecifiedKind()
    {
        var codecs = new PgTextCodecs(await Catalog);
        var expected = new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(1234560);
        var value = Assert.IsType<DateTime>(codecs.Decode(TimestampOid, "2024-02-29 12:34:56.123456", "ISO, MDY"));
        Assert.Equal(expected, value); Assert.Equal(DateTimeKind.Unspecified, value.Kind);
        Assert.Equal("2024-02-29 12:34:56.123456", codecs.Encode(TimestampOid, expected));
    }

    [Theory]
    [InlineData("ISO, MDY")]
    [InlineData("ISO, DMY")]
    [InlineData("ISO, YMD")]
    public async Task StableDefaultKindProviderTypesAndInstantNormalizationUseActualCatalog(string style)
    {
        var codecs = new PgTextCodecs(await Catalog);
        var utc = Assert.IsType<DateTime>(codecs.Decode(TimestampTzOid, "2024-03-01 00:00:00.000001+01:00:01", style));
        Assert.Equal(new DateTime(2024, 2, 29, 22, 59, 59).AddTicks(10), utc); Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(typeof(DateTime), codecs.GetFieldType(TimestampTzOid));
        Assert.Equal(typeof(KingbaseTimestampTz), codecs.GetProviderSpecificFieldType(TimestampTzOid));
        Assert.Equal(typeof(KingbaseTimestamp), codecs.GetProviderSpecificFieldType(TimestampOid));
        foreach (var oid in new[] { TimestampOid, TimestampTzOid })
        {
            var suffix = oid == TimestampTzOid ? "+00" : "";
            foreach (var text in new[] { "infinity", "-infinity", "0001-02-29 12:34:56" + suffix + " BC", "10000-01-01 00:00:00" + suffix })
            {
                Assert.Throws<OverflowException>(() => codecs.Decode(oid, text, style));
                var value = codecs.DecodeProviderSpecific(oid, text, style); Assert.Equal(text, codecs.Encode(oid, value));
            }
            Assert.Same(DBNull.Value, codecs.Decode(oid, null)); Assert.Same(DBNull.Value, codecs.DecodeProviderSpecific(oid, null));
            Assert.Null(codecs.Encode(oid, DBNull.Value));
        }
        var other = new PgTextCodecs(await PgTextCodecsTests.LoadCatalog(8100));
        Assert.Throws<NotSupportedException>(() => other.Decode(TimestampOid, null));
        Assert.Equal(DateTime.MinValue, other.Decode(8118, "0001-01-01 00:00:00", style));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SQL, DMY")]
    [InlineData("German, DMY")]
    [InlineData("ISO")]
    [InlineData("private-style")]
    public async Task TimestampResultsNeedConfirmedIsoStyleButNullAndInputDoNot(string? style)
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var oid in new[] { TimestampOid, TimestampTzOid })
        {
            foreach (var read in new Func<object>[] { () => codecs.Decode(oid, "infinity", style), () => codecs.DecodeProviderSpecific(oid, "infinity", style) })
            {
                var error = Assert.Throws<NotSupportedException>(read); Assert.Null(error.InnerException); Assert.DoesNotContain("private", error.Message);
            }
            Assert.Same(DBNull.Value, codecs.Decode(oid, null, style));
            Assert.Equal("infinity", codecs.Encode(oid, oid == TimestampOid ? KingbaseTimestamp.PositiveInfinity : KingbaseTimestampTz.PositiveInfinity));
        }
    }

    [Fact]
    public async Task TimestampBindingsInferKindsAndOffsetsAndKeepExplicitDateSeparate()
    {
        var catalog = await Catalog; var codecs = new PgTextCodecs(catalog);
        var clock = new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(1234560);
        foreach (var p in new[] { new KingbaseParameter("p", clock), new KingbaseParameter("p", clock) { DbType = DbType.DateTime2 },
            new KingbaseParameter("p", KingbaseTimestamp.FromDateTime(clock)) { DataTypeName = "pg_catalog.timestamp" } })
        {
            var bound = Bind(p, catalog); Assert.Equal(TimestampOid, bound.TypeOid);
            Assert.Equal("2024-02-29 12:34:56.123456", codecs.Encode(bound.TypeOid, bound.Value));
        }
        foreach (var value in new object[] { DateTime.SpecifyKind(clock, DateTimeKind.Utc),
            new DateTimeOffset(clock.AddHours(2), TimeSpan.FromHours(2)), KingbaseTimestampTz.Parse("2024-02-29 12:34:56.123456+00") })
        {
            var p = new KingbaseParameter("p", value); var bound = Bind(p, catalog);
            Assert.Equal(TimestampTzOid, bound.TypeOid); Assert.Equal(DbType.DateTimeOffset, p.DbType);
            Assert.Equal("2024-02-29 12:34:56.123456+00", codecs.Encode(bound.TypeOid, bound.Value));
        }
        Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DateTime.SpecifyKind(clock, DateTimeKind.Local)), catalog));
        Assert.Equal(7015u, Bind(new KingbaseParameter("p", clock.Date) { DbType = DbType.Date }, catalog).TypeOid);
        foreach (var type in new[] { KingbaseDbType.Timestamp, KingbaseDbType.TimestampTz })
        {
            var oid = type == KingbaseDbType.Timestamp ? TimestampOid : TimestampTzOid;
            Assert.Equal(oid, Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type }, catalog).TypeOid);
            Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type, DbType = DbType.String }, catalog));
            Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type, Precision = 6 }, catalog));
            Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type, Scale = 6 }, catalog));
            Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = type, Size = 8 }, catalog));
        }
        foreach (var pair in new[] { (DbType.DateTime, TimestampOid), (DbType.DateTime2, TimestampOid), (DbType.DateTimeOffset, TimestampTzOid) })
            Assert.Equal(pair.Item2, Bind(new KingbaseParameter("p", DBNull.Value) { DbType = pair.Item1 }, catalog).TypeOid);
        Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", clock.AddTicks(1)), catalog));
        foreach (var value in new object[] { "private-time", DateTime.SpecifyKind(clock, DateTimeKind.Utc), new DateTimeOffset(clock, TimeSpan.Zero), default(KingbaseTimestampTz) })
            Assert.Throws<InvalidCastException>(() => codecs.Encode(TimestampOid, value));
        foreach (var value in new object[] { "private-time", clock, DateTime.SpecifyKind(clock, DateTimeKind.Local), default(KingbaseTimestamp) })
            Assert.Throws<InvalidCastException>(() => codecs.Encode(TimestampTzOid, value));
        foreach (var name in new[] { "timestamp", "timestamptz" })
        {
            var wrong = new PgTextCodecs(await PgTextCodecsTests.LoadCatalog(change: t => t.Name != name ? t :
                new PgTypeDescription(t.Oid, t.NamespaceOid, t.Name, 12, t.Kind, t.Category, 0, 0, 0, -1)));
            Assert.Throws<NotSupportedException>(() => wrong.Encode(name == "timestamp" ? TimestampOid : TimestampTzOid, null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedReadersUseTheirOwnIsoConfirmationAndPreserveProviderRecovery(bool afterCompletion)
    {
        var change = Status("DateStyle", "SQL, DMY");
        var input = Join(Status("DateStyle", "ISO, MDY"), Description(1, TimestampTzOid, 8), Row(["2024-01-01 01:00:00+01"]),
            afterCompletion ? Join(Complete(), change) : Join(change, Complete()), Description(1, TimestampTzOid, 8), Row(["01/01/2024 01:00:00 UTC"]), Complete(), Ready());
        using var session = new PgQuerySession(new PgWire(new QueryStream(input)), new PgStartupResult(
            new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        using var reader = new KingbaseDataReader(await session.ExecuteAsync("SELECT first; SELECT second"), await Catalog);
        Assert.True(reader.Read()); Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), reader.GetDateTime(0));
        Assert.Equal(DateTimeKind.Utc, reader.GetDateTime(0).Kind); Assert.Equal(TimeSpan.Zero, reader.GetFieldValue<DateTimeOffset>(0).Offset);
        Assert.True(reader.NextResult()); Assert.True(reader.Read());
        Assert.Throws<NotSupportedException>(() => reader.GetFieldValue<KingbaseTimestampTz>(0));
    }

    [Fact]
    public async Task ReaderDefaultTypesNeverChangeForNonClrOrNullValuesAndCastsNeverDropOffsets()
    {
        using var reader = new KingbaseDataReader([new PgTextResult([
            new PgColumnDescription("ts", 0, 0, TimestampOid, 8, -1), new PgColumnDescription("tz", 0, 0, TimestampTzOid, 8, -1)],
            [["10000-01-01 00:00:00", "0001-01-01 00:00:00+01"], ["infinity", "-infinity"], [null, null]], "SELECT 3", "ISO, MDY")], await Catalog);
        Assert.Equal(typeof(DateTime), reader.GetColumnSchema()[0].DataType); Assert.Equal(typeof(DateTime), reader.GetFieldType(1));
        for (var i = 0; i < 2; i++)
        {
            Assert.True(reader.Read()); Assert.Throws<OverflowException>(() => reader.GetValue(0)); Assert.Throws<OverflowException>(() => reader.GetDateTime(1));
            Assert.Throws<OverflowException>(() => reader.GetFieldValue<DateTimeOffset>(1));
            Assert.IsType<KingbaseTimestamp>(reader.GetProviderSpecificValue(0)); Assert.IsType<KingbaseTimestampTz>(reader.GetProviderSpecificValue(1));
            Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<DateTimeOffset>(0));
            Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<KingbaseTimestamp>(1));
            var values = new object[2]; Assert.Equal(2, reader.GetProviderSpecificValues(values));
        }
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.Same(DBNull.Value, reader.GetProviderSpecificValue(1));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<DateTimeOffset>(1));
    }

    static PgQueryParameter Bind(KingbaseParameter parameter, PgTypeCatalog catalog)
    {
        var parameters = new KingbaseParameterCollection(); parameters.Add(parameter);
        try { return Assert.Single(PgParameterBindings.Create("SELECT @p", parameters, catalog, KingbaseCompatibilityMode.PostgreSql, true).Parameters); }
        finally { parameters.Clear(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicPreparedTimestampRejectsKindAndExtraTicksBeforeIoAndKeepsProviderRecovery(bool zoned)
    {
        using var listener = PublicExecutionTests.Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var oid = zoned ? 9011u : 9010u;
        var response = zoned ? "0001-01-01 00:00:00+01" : "0001-02-29 12:34:56.123456 BC";
        var expected = zoned ? "0001-12-31 23:00:00+00 BC" : response;
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
                Assert.Contains(expected, Encoding.UTF8.GetString(execute[0]));
                await stream.WriteAsync(Join(Status("DateStyle", "ISO, MDY"), Status("TimeZone", "America/New_York"),
                    Frame('2'), Description(1, oid, 8), Row([response]), Complete(), Status("TimeZone", "UTC"), Ready()), guard.Token);
            }
            await PublicPreparedTests.ReadFrames(stream, "CS", guard.Token); await stream.WriteAsync(Join(Frame('3'), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.Add(new KingbaseParameter("p", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Local))
            { KingbaseDbType = zoned ? KingbaseDbType.TimestampTz : KingbaseDbType.Timestamp });
        await Assert.ThrowsAsync<InvalidCastException>(() => command.PrepareAsync(guard.Token));
        command.Parameters[0].Value = DateTime.SpecifyKind(DateTime.MinValue.AddTicks(1), zoned ? DateTimeKind.Utc : DateTimeKind.Unspecified);
        await Assert.ThrowsAsync<ArgumentException>(() => command.PrepareAsync(guard.Token));
        command.Parameters[0].Value = zoned ? KingbaseTimestampTz.Parse(expected) : KingbaseTimestamp.Parse(expected);
        await command.PrepareAsync(guard.Token);
        for (var i = 0; i < 2; i++)
        {
            using var reader = await command.ExecuteReaderAsync(guard.Token); Assert.True(reader.Read());
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
            Assert.Equal(expected, reader.GetProviderSpecificValue(0).ToString());
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.UnprepareAsync(guard.Token));
        }
        await command.UnprepareAsync(guard.Token); connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
