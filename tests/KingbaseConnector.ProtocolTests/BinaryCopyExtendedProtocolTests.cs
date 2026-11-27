using System.Data;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.CopyExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.BinaryCopyTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class BinaryCopyExtendedProtocolTests
{
    public static IEnumerable<object?[]> StatusCases() => new[] { KingbaseDbType.Time, KingbaseDbType.TimeTz, KingbaseDbType.Timestamp, KingbaseDbType.TimestampTz, KingbaseDbType.Interval }
        .SelectMany(type => new string?[] { null, "off", "unknown" }.Select(status => new object?[] { type, status }));

    [Theory]
    [MemberData(nameof(StatusCases))]
    public Task IntegerDatetimeStatusIsRequiredBeforeAnyClockCopyDispatch(KingbaseDbType type, string? status) => Run(async (connection, token) =>
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.BeginBinaryImportAsync("COPY items FROM STDIN (FORMAT BINARY)", new[] {type}, token));
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] {type}, token));
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
    }, async (stream, token) => { await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token); }, integerDatetimes: status);

    [Fact]
    public Task NumericAndDateCopyDoNotNeedIntegerDatetimeStatus() => Run(async (connection, token) =>
    {
        using var input = connection.BeginBinaryImport("COPY items FROM STDIN (FORMAT BINARY)", KingbaseDbType.Numeric, KingbaseDbType.Date);
        Assert.Throws<ArgumentOutOfRangeException>(() => input.WriteRow(new KingbaseNumeric(1, 16384), new KingbaseDate(2000, 1, 1)));
        Assert.Throws<InvalidCastException>(() => input.WriteRow(1.2300m, new DateTime(2000, 1, 1, 1, 0, 0)));
        input.WriteRow(1.2300m, new DateOnly(2000, 1, 1)); Assert.Equal(1UL, await input.CompleteAsync(token));
    }, async (stream, token) =>
    {
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token);
        Assert.Equal(Join(BinaryHeader(), [0,2], Int32(12), Convert.FromHexString("0002000000000004000108FC"), Int32(4), Int32(0), [255,255]), await ReadImport(stream, token));
        await stream.WriteAsync(Tail(1), token);
    });

    [Fact]
    public Task FragmentedClockCopyUsesExactProviderOutputsAndLeavesInvalidRowsUnsent() => Run(async (connection, token) =>
    {
        using (var input = connection.BeginBinaryImport("COPY items FROM STDIN (FORMAT BINARY)", KingbaseDbType.Time, KingbaseDbType.TimestampTz))
        {
            Assert.Throws<ArgumentException>(() => input.WriteRow(TimeSpan.FromTicks(1), DateTime.UtcNow));
            Assert.Throws<InvalidCastException>(() => input.WriteRow(TimeSpan.Zero, new DateTime(2000, 1, 1)));
            input.WriteRow(new TimeOnly(0), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(1UL, await input.CompleteAsync(token));
        }
        using (var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] { KingbaseDbType.Time, KingbaseDbType.TimestampTz }, token))
        {
            Assert.Equal(new object[] { TimeSpan.Zero, KingbaseTimestampTz.Parse("2000-01-01 00:00:00+00") }, output.ReadRow());
            Assert.Null(await output.ReadRowAsync(token)); Assert.Equal(1UL, output.RowsCopied);
        }
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(token));
    }, async (stream, token) =>
    {
        var literal = Join(BinaryHeader(), [0,2], Int32(8), new byte[8], Int32(8), new byte[8], [255,255]);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(true, true), token); Assert.Equal(literal, await ReadImport(stream, token)); await stream.WriteAsync(Tail(1), token);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Response(false, true), token);
        foreach (var b in literal) await stream.WriteAsync(Frame('d', [b]), token);
        await stream.WriteAsync(Join(Frame('c'), Tail(1)), token); await ReadExtended(stream, token); await stream.WriteAsync(Value("42"), token);
    }, integerDatetimes: "on");

    [Theory]
    [InlineData("numeric", KingbaseDbType.Numeric, "00017FFF000000000001", true)]
    [InlineData("numeric", KingbaseDbType.Numeric, "00010000000000002710", false)]
    [InlineData("date", KingbaseDbType.Date, "7FFFFFFE", false)]
    [InlineData("time", KingbaseDbType.Time, "FFFFFFFFFFFFFFFF", false)]
    [InlineData("timetz", KingbaseDbType.TimeTz, "00000000000000010000E100", false)]
    [InlineData("timestamp", KingbaseDbType.Timestamp, "7FFFFFFFFFFFFFFE", false)]
    [InlineData("timestamptz", KingbaseDbType.TimestampTz, "8000000000000001", false)]
    [InlineData("interval", KingbaseDbType.Interval, "0000", false)]
    public Task InvalidOrUnrepresentableValuesRetireUnfinishedExport(string name, KingbaseDbType type, string hex, bool overflow) => Run(async (connection, token) =>
    {
        using var output = await connection.BeginBinaryExportAsync("COPY items TO STDOUT (FORMAT BINARY)", new[] {KingbaseDbType.Integer, type}, token);
        Assert.Equal(42, output.ReadRow()![0]);
        if (overflow) await Assert.ThrowsAsync<OverflowException>(() => output.ReadRowAsync(token));
        else await Assert.ThrowsAsync<IOException>(() => output.ReadRowAsync(token));
        Assert.Equal(ConnectionState.Broken, connection.State); Assert.Null(output.RowsCopied);
    }, async (stream, token) =>
    {
        var good = BinaryCopyExtendedCodecTests.Literals().First(row => (string)row[0] == name);
        var value = Convert.FromHexString((string)good[2]); var bad = Convert.FromHexString(hex);
        await ReadQueryAsync(stream, token); await stream.WriteAsync(Join(Response(false, true), Frame('d', Join(BinaryHeader(),
            [0,2], Int32(4), Int32(42), Int32(value.Length), value, [0,2], Int32(4), Int32(43), Int32(bad.Length), bad, [255,255])), Frame('c'), Tail(2)), token);
    }, integerDatetimes: "on");

    public static IEnumerable<object[]> CatalogCases() => new[] {
        ("numeric", -1, KingbaseDbType.Numeric), ("date", 4, KingbaseDbType.Date), ("time", 8, KingbaseDbType.Time), ("timetz", 12, KingbaseDbType.TimeTz),
        ("timestamp", 8, KingbaseDbType.Timestamp), ("timestamptz", 8, KingbaseDbType.TimestampTz), ("interval", 16, KingbaseDbType.Interval) }
        .SelectMany(type => new[] { "valid", "width", "kind", "base", "element", "schema" }.Select(fault => new object[] {type.Item1, type.Item2, type.Item3, fault}));

    [Theory]
    [MemberData(nameof(CatalogCases))]
    public async Task SelectorsRequireActualPgBaseTypeNamesAndStorageWidths(string name, int size, KingbaseDbType selector, string fault)
    {
        var rows = NativeScalarTests.BaseRows();
        string?[] row = ["17000", "90", "pg_catalog", name, size.ToString(System.Globalization.CultureInfo.InvariantCulture), "b", "U", "t", "0", "0", "0", "-1"];
        if (fault == "width") row[4] = "3";
        if (fault == "kind") row[5] = "d";
        if (fault == "base") row[8] = "10002";
        if (fault == "element") row[9] = "10002";
        if (fault == "schema") { row[1] = "91"; row[2] = "public"; }
        rows.Add(row);
        if (fault == "schema")
        {
            // A same-named user type no longer invalidates the catalog; it is a text-path type, not the built-in one.
            var userCatalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql, baseRows: rows);
            Assert.Equal(typeof(string), new PgTextCodecs(userCatalog).GetFieldType(17000));
            Assert.True(userCatalog.ByName.ContainsKey("public." + name));
            Assert.Throws<NotSupportedException>(() => PgBinaryCopyCodec.Resolve(userCatalog, [selector]));
            return;
        }
        var catalog = await NativeScalarTests.Load(KingbaseCompatibilityMode.PostgreSql, baseRows: rows);
        if (fault == "valid") Assert.Equal(new[] {name}, PgBinaryCopyCodec.Resolve(catalog, [selector]));
        else Assert.Throws<NotSupportedException>(() => PgBinaryCopyCodec.Resolve(catalog, [selector]));
    }
}
