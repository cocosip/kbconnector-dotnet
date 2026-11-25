using KingbaseConnector.Protocol;
using System.Data;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgDateTests
{
    static readonly Task<PgTypeCatalog> Catalog = PgTextCodecsTests.LoadCatalog();
    const uint DateOid = 7015;
    [Fact]
    public async Task ExplicitDateEncodesAnUnspecifiedMidnightWithoutChangingItsCalendarDay()
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal("2024-02-29", codecs.Encode(DateOid, new DateTime(2024, 2, 29)));
        Assert.Equal(typeof(DateTime), codecs.GetFieldType(DateOid));
    }

    [Theory]
    [InlineData("ISO, MDY")]
    [InlineData("ISO, DMY")]
    [InlineData("ISO, YMD")]
    public async Task StableDefaultAndProviderMappingsUseVerifiedFormatAndActualOid(string style)
    {
        var codecs = new PgTextCodecs(await Catalog);
        Assert.Equal(new DateTime(2024, 2, 29), codecs.Decode(DateOid, "2024-02-29", style));
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)codecs.Decode(DateOid, "2024-02-29", style)).Kind);
        Assert.Equal(typeof(KingbaseDate), codecs.GetProviderSpecificFieldType(DateOid));
        foreach (var text in new[] { "0001-01-01 BC", "10000-01-01", "infinity", "-infinity" })
        {
            Assert.Throws<OverflowException>(() => codecs.Decode(DateOid, text, style));
            var value = Assert.IsType<KingbaseDate>(codecs.DecodeProviderSpecific(DateOid, text, style));
            Assert.Equal(text, codecs.Encode(DateOid, value));
        }
        Assert.Equal("2024-02-29", codecs.Encode(DateOid, new DateOnly(2024, 2, 29)));
        Assert.Same(DBNull.Value, codecs.Decode(DateOid, null));
        Assert.Same(DBNull.Value, codecs.DecodeProviderSpecific(DateOid, null));
        Assert.Null(codecs.Encode(DateOid, DBNull.Value));
        Assert.Throws<InvalidCastException>(() => codecs.Encode(DateOid, "2024-02-29"));
        var other = new PgTextCodecs(await PgTextCodecsTests.LoadCatalog(8100));
        Assert.Throws<NotSupportedException>(() => other.Decode(DateOid, "2024-02-29", style));
        Assert.Equal(new DateTime(2024, 2, 29), other.Decode(8115, "2024-02-29", style));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SQL, MDY")]
    [InlineData("Postgres, DMY")]
    [InlineData("German, DMY")]
    [InlineData("ISO")]
    [InlineData("private-format")]
    public async Task UnknownOrUnsupportedOutputStylesAreNotGuessed(string? style)
    {
        var codecs = new PgTextCodecs(await Catalog);
        foreach (var read in new Func<object>[] { () => codecs.Decode(DateOid, "2024-02-29", style),
            () => codecs.DecodeProviderSpecific(DateOid, "infinity", style) })
        {
            var error = Assert.Throws<NotSupportedException>(read);
            Assert.DoesNotContain("private", error.Message); Assert.Null(error.InnerException);
        }
        // Typed NULL and input do not interpret date output or depend on server display style.
        Assert.Same(DBNull.Value, codecs.Decode(DateOid, null, style));
        Assert.Equal("2024-02-29", codecs.Encode(DateOid, new KingbaseDate(2024, 2, 29)));
    }

    [Theory]
    [InlineData("02/03/2024")]
    [InlineData("03.02.2024")]
    [InlineData("02-03-2024")]
    [InlineData("2024-02-30")]
    [InlineData("private-date")]
    public async Task UnsupportedOrMalformedDateTextFailsWithoutPayloadOrInnerException(string text)
    {
        var codecs = new PgTextCodecs(await Catalog);
        var error = Assert.Throws<FormatException>(() => codecs.DecodeProviderSpecific(DateOid, text, "ISO, MDY"));
        Assert.DoesNotContain(text, error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task DateBindingsSupportExplicitConstraintsInferenceNullAndExactClrValidation()
    {
        var catalog = await Catalog;
        foreach (var parameter in new[] {
            new KingbaseParameter("p", new DateTime(2024, 2, 29)) { DbType = DbType.Date },
            new KingbaseParameter("p", new KingbaseDate(2024, 2, 29)),
            new KingbaseParameter("p", new DateOnly(2024, 2, 29)),
            new KingbaseParameter("p", new KingbaseDate(2024, 2, 29)) { DataTypeName = "pg_catalog.date", KingbaseDbType = KingbaseDbType.Date, DbType = DbType.Date } })
        {
            var value = parameter.Value;
            var binding = Bind(parameter, catalog);
            Assert.Equal(DateOid, binding.TypeOid);
            Assert.Equal("2024-02-29", new PgTextCodecs(catalog).Encode(binding.TypeOid, binding.Value));
            Assert.Same(value, parameter.Value);
            Assert.Equal(DbType.Date, parameter.DbType);
        }
        Assert.Null(new PgTextCodecs(catalog).Encode(DateOid, Bind(new KingbaseParameter("p", DBNull.Value) { KingbaseDbType = KingbaseDbType.Date }, catalog).Value));
        Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", new DateTime(2024, 1, 1)), catalog));
        Assert.Throws<InvalidCastException>(() => Bind(new KingbaseParameter("p", DateTime.SpecifyKind(new DateTime(2024, 1, 1), DateTimeKind.Utc)) { DbType = DbType.Date }, catalog));
        Assert.Throws<InvalidCastException>(() => Bind(new KingbaseParameter("p", new DateTime(2024, 1, 1).AddTicks(1)) { DbType = DbType.Date }, catalog));
        Assert.Throws<ArgumentException>(() => Bind(new KingbaseParameter("p", new KingbaseDate(2024, 1, 1)) { DbType = DbType.String, KingbaseDbType = KingbaseDbType.Date }, catalog));
        Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { DbType = DbType.Date, Size = 1 }, catalog));
        Assert.Throws<NotSupportedException>(() => Bind(new KingbaseParameter("p", DBNull.Value) { DbType = DbType.Date, Scale = 1 }, catalog));
        var wrong = await PgTextCodecsTests.LoadCatalog(change: t => t.Name != "date" ? t :
            new PgTypeDescription(t.Oid, t.NamespaceOid, t.Name, 8, t.Kind, t.Category, 0, 0, 0, -1));
        Assert.Throws<NotSupportedException>(() => new PgTextCodecs(wrong).Encode(DateOid, null));
    }

    static PgQueryParameter Bind(KingbaseParameter p, PgTypeCatalog catalog)
    {
        var collection = new KingbaseParameterCollection(); collection.Add(p);
        try { return Assert.Single(PgParameterBindings.Create("SELECT @p", collection, catalog, KingbaseCompatibilityMode.PostgreSql, true).Parameters); }
        finally { collection.Clear(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedResultsKeepTheirOwnFormatEvenAfterLaterStatusChanges(bool statusAfterCompletion)
    {
        var change = Status("DateStyle", "SQL, DMY");
        var input = Join(Status("DateStyle", "ISO, MDY"), Description(1, DateOid, 4), Row(["2024-02-29"]),
            statusAfterCompletion ? Join(Complete(), change) : Join(change, Complete()),
            Description(1, DateOid, 4), Row(["29/02/2024"]), Complete(), Ready());
        using var session = Session(new QueryStream(input));
        var results = await session.ExecuteAsync("SELECT first; SELECT second");
        Assert.Equal("SQL, DMY", session.Parameters["DateStyle"]);
        using var reader = new KingbaseDataReader(results, await Catalog);
        Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.Equal(typeof(KingbaseDate), reader.GetProviderSpecificFieldType(0));
        Assert.True(reader.Read());
        Assert.Equal(new DateTime(2024, 2, 29), reader.GetDateTime(0));
        Assert.Equal(new DateOnly(2024, 2, 29), reader.GetFieldValue<DateOnly>(0));
        Assert.Equal("2024-02-29", reader.GetFieldValue<KingbaseDate>(0).ToString());
        Assert.Equal(typeof(DateTime), reader.GetColumnSchema()[0].DataType);
        Assert.True(reader.NextResult()); Assert.True(reader.Read());
        Assert.Throws<NotSupportedException>(() => reader.GetValue(0));
        Assert.Throws<NotSupportedException>(() => reader.GetProviderSpecificValue(0));
    }

    [Fact]
    public async Task ConflictingFormatsInsideOneRowsetRejectDatesWithoutGuessing()
    {
        var input = Join(Status("DateStyle", "ISO, MDY"), Description(1, DateOid, 4), Row(["2024-02-29"]),
            Status("DateStyle", "SQL, MDY"), Row(["02/29/2024"]), Complete(), Ready());
        using var session = Session(new QueryStream(input));
        using var reader = new KingbaseDataReader(await session.ExecuteAsync("SELECT date_column"), await Catalog);
        while (reader.Read()) Assert.Throws<NotSupportedException>(() => reader.GetFieldValue<KingbaseDate>(0));
    }

    [Fact]
    public async Task DateReaderPreservesProviderFallbackAndTypedNull()
    {
        var column = new PgColumnDescription("d", 0, 0, DateOid, 4, -1);
        using var reader = new KingbaseDataReader([new PgTextResult([column], [["0001-01-01 BC"], ["infinity"], [null]], "SELECT 3", "ISO, MDY")], await Catalog);
        foreach (var text in new[] { "0001-01-01 BC", "infinity" })
        {
            Assert.True(reader.Read()); Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
            Assert.Equal(text, reader.GetFieldValue<KingbaseDate>(0).ToString());
            var values = new object[1]; Assert.Equal(1, reader.GetProviderSpecificValues(values));
            Assert.Equal(text, Assert.IsType<KingbaseDate>(values[0]).ToString());
        }
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.Same(DBNull.Value, reader.GetValue(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<DateOnly>(0));
    }

    [Fact]
    public async Task PublicDateBindingValidatesBeforeIoAndKeepsReaderAndConnectionOwnership()
    {
        using var listener = PublicExecutionTests.Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(PublicExecutionTests.Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await PublicExecutionTests.Initialize(stream, guard.Token);
            var frames = await PublicExecutionTests.ReadExtended(stream, guard.Token);
            Assert.Contains("2024-02-29", Encoding.UTF8.GetString(frames[1]));
            Assert.Equal(9007, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frames[0].AsSpan(frames[0].Length - 4)));
            await stream.WriteAsync(Join(Status("DateStyle", "ISO, DMY"), Frame('1'), Frame('2'), Description(1, 9007, 4), Row(["2024-02-29"]), Complete(), Ready()), guard.Token);
            await PublicExecutionTests.ReadExtended(stream, guard.Token);
            await stream.WriteAsync(PublicExecutionTests.Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT @p", connection);
        command.Parameters.Add(new KingbaseParameter("p", new DateTime(2024, 2, 29).AddHours(1)) { DbType = DbType.Date });
        await Assert.ThrowsAsync<InvalidCastException>(() => command.ExecuteScalarAsync(guard.Token));
        command.Parameters[0].Value = new DateOnly(2024, 2, 29);
        using (var reader = await command.ExecuteReaderAsync(guard.Token))
        {
            Assert.True(reader.Read()); Assert.Equal(new DateTime(2024, 2, 29), reader.GetDateTime(0));
            Assert.Equal(new DateOnly(2024, 2, 29), reader.GetFieldValue<DateOnly>(0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteScalarAsync(guard.Token));
        }
        command.Parameters.Clear(); command.CommandText = "SELECT 42";
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    static PgQuerySession Session(QueryStream stream) => new(new PgWire(stream), new PgStartupResult(
        new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
}
