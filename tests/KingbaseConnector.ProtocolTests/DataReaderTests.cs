using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;

namespace KingbaseConnector.ProtocolTests;

public sealed class DataReaderTests
{
    static readonly (string Name, short Size)[] Types = [("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8),
        ("oid", 4), ("float4", 4), ("float8", 8), ("text", -1), ("bytea", -1), ("uuid", 16), ("numeric", -1)];
    static readonly Task<PgTypeCatalog> CatalogTask = LoadCatalog();
    static uint Oid(string name) => name == "uuid" ? uint.MaxValue : 8000 + (uint)Array.FindIndex(Types, t => t.Name == name);
    static PgColumnDescription Column(string name, string type = "int4") => new(name, 0, 0, Oid(type), Types.Single(t => t.Name == type).Size, -1);
    static PgTextResult Result(PgColumnDescription[] columns, string?[][] rows, string? tag = "SELECT 1") => new(columns, rows, tag);
    static async Task<KingbaseDataReader> Reader(params PgTextResult[] results) => new(results, await CatalogTask);

    [Fact]
    public async Task CursorHasRowsAndMultipleResultTransitionsIncludeNonrowResults()
    {
        using DbDataReader r = await Reader(Result([Column("n")], [["1"], ["2"]], "SELECT 2"),
            Result([], [], "UPDATE 3"), Result([Column("empty")], [], "SELECT 0"));
        Assert.False(r.IsClosed);
        Assert.Equal(0, r.Depth);
        Assert.Equal(1, r.FieldCount);
        Assert.Equal(1, r.VisibleFieldCount);
        Assert.True(r.HasRows);
        Assert.Throws<InvalidOperationException>(() => r.GetValue(0));
        Assert.True(r.Read());
        Assert.Equal(1, r.GetInt32(0));
        Assert.True(r.Read());
        Assert.Equal(2, r["n"]);
        Assert.False(r.Read());
        Assert.False(r.Read());
        Assert.True(r.HasRows);
        Assert.Throws<InvalidOperationException>(() => r.GetValue(0));
        Assert.True(r.NextResult());
        Assert.Equal(0, r.FieldCount);
        Assert.False(r.HasRows);
        Assert.False(r.Read());
        Assert.True(r.NextResult());
        Assert.Equal("empty", r.GetName(0));
        Assert.False(r.Read());
        Assert.False(r.NextResult());
        Assert.False(r.NextResult());
        Assert.Equal(0, r.FieldCount);
        Assert.False(r.HasRows);
        Assert.False(r.Read());
        Assert.Equal(3, r.RecordsAffected);
        r.Close();
        Assert.True(r.IsClosed);
        Assert.Equal(3, r.RecordsAffected);
        Assert.Throws<InvalidOperationException>(() => r.Read());
        Assert.Throws<InvalidOperationException>(() => r.GetName(0));
        Assert.Throws<InvalidOperationException>(() => _ = r.FieldCount);
        r.Close();
    }

    [Fact]
    public async Task EmptyExchangeAndEmptyQueryRemainDistinctFromClosedReader()
    {
        using var empty = await Reader();
        Assert.Equal(0, empty.FieldCount);
        Assert.False(empty.Read());
        Assert.False(empty.NextResult());
        Assert.Equal(-1, empty.RecordsAffected);
        using var query = await Reader(Result([], [], null));
        Assert.False(query.HasRows);
        Assert.Equal(-1, query.RecordsAffected);
        Assert.False(query.NextResult());
    }

    [Fact]
    public async Task TypedValuesNullAndProviderSpecificNumericNeverCoerceOrRound()
    {
        var names = new[] { "bool", "int2", "int4", "int8", "oid", "float4", "float8", "text", "bytea", "uuid", "numeric", "numeric", "text" };
        var id = Guid.NewGuid();
        using var r = await Reader(Result(names.Select((n, i) => Column("c" + i, n)).ToArray(),
            [["t", "-2", "3", "4", "4294967295", "1.25", "2.5", "文😀", "\\x00ff", id.ToString(), "1.2300", "123456789012345678901234567890.00100", null]]));
        Assert.True(r.Read());
        Assert.True(r.GetBoolean(0));
        Assert.Equal((short)-2, r.GetInt16(1));
        Assert.Equal(3, r.GetInt32(2));
        Assert.Equal(4L, r.GetInt64(3));
        Assert.Equal(uint.MaxValue, r.GetFieldValue<uint>(4));
        Assert.Equal(1.25f, r.GetFloat(5));
        Assert.Equal(2.5d, r.GetDouble(6));
        Assert.Equal("文😀", r.GetString(7));
        Assert.Equal(new byte[] { 0, 255 }, r.GetFieldValue<byte[]>(8));
        Assert.Equal(id, r.GetGuid(9));
        Assert.Equal(decimal.GetBits(1.2300m), decimal.GetBits(r.GetDecimal(10)));
        Assert.Equal(typeof(decimal), r.GetFieldType(11));
        Assert.Equal(typeof(KingbaseNumeric), r.GetProviderSpecificFieldType(11));
        Assert.Throws<OverflowException>(() => r.GetValue(11));
        var number = r.GetFieldValue<KingbaseNumeric>(11);
        Assert.Equal("123456789012345678901234567890.00100", number.ToString());
        Assert.Equal(number, r.GetProviderSpecificValue(11));
        Assert.True(r.IsDBNull(12));
        Assert.Same(DBNull.Value, r[12]);
        Assert.Same(DBNull.Value, r.GetProviderSpecificValue(12));
        Assert.Throws<InvalidCastException>(() => r.GetString(12));
        Assert.Throws<InvalidCastException>(() => r.GetInt64(2));
        Assert.Throws<InvalidCastException>(() => r.GetChar(7));
        Assert.Throws<InvalidCastException>(() => r.GetDateTime(2));
        Assert.Throws<InvalidCastException>(() => r.GetByte(8));
        var copy = r.GetFieldValue<byte[]>(8);
        copy[0] = 99;
        Assert.Equal((byte)0, r.GetFieldValue<byte[]>(8)[0]);
    }

    [Fact]
    public async Task OrdinalsPreferExactNamesThenFirstCaseInsensitiveMatchWithSafeErrors()
    {
        using var r = await Reader(Result([Column("name"), Column("NAME"), Column("name"), Column("é"), Column("e")], []));
        Assert.Equal(1, r.GetOrdinal("NAME"));
        Assert.Equal(0, r.GetOrdinal("NaMe"));
        Assert.Equal(0, r.GetOrdinal("name"));
        Assert.Equal(3, r.GetOrdinal("é"));
        Assert.Throws<ArgumentNullException>(() => r.GetOrdinal(null!));
        var error = Assert.Throws<IndexOutOfRangeException>(() => r.GetOrdinal("private-name"));
        Assert.DoesNotContain("private-name", error.ToString());
        Assert.Throws<IndexOutOfRangeException>(() => r.GetName(-1));
        Assert.Throws<IndexOutOfRangeException>(() => r.GetName(5));
    }

    [Fact]
    public async Task BulkAccessHonorsDestinationSizeAndProviderValues()
    {
        using var r = await Reader(Result([Column("a"), Column("b", "text"), Column("c", "numeric")], [["1", null, "2.300"]]));
        Assert.Throws<InvalidOperationException>(() => r.GetValues([]));
        Assert.True(r.Read());
        Assert.Equal(0, r.GetValues([]));
        var shortArray = new object[1];
        Assert.Equal(1, r.GetValues(shortArray));
        Assert.Equal(1, shortArray[0]);
        var values = new object[4];
        values[3] = "sentinel";
        Assert.Equal(3, r.GetValues(values));
        Assert.Equal(new object[] { 1, DBNull.Value, 2.300m, "sentinel" }, values);
        Assert.Equal(3, r.GetProviderSpecificValues(values));
        Assert.IsType<KingbaseNumeric>(values[2]);
        Assert.Throws<ArgumentNullException>(() => r.GetValues(null!));
    }

    [Fact]
    public async Task BytesCharsAndIndependentStreamsUseBoundedOffsets()
    {
        using var r = await Reader(Result([Column("b", "bytea"), Column("s", "text")], [["\\x000102ff", "a😀z"]]));
        Assert.True(r.Read());
        Assert.Equal(4, r.GetBytes(0, 0, null, 0, 0));
        Assert.Equal(4, r.GetChars(1, 0, null, 0, 0));
        var bytes = new byte[] { 9, 9, 9, 9 };
        Assert.Equal(2, r.GetBytes(0, 2, bytes, 1, 3));
        Assert.Equal(new byte[] { 9, 2, 255, 9 }, bytes);
        var chars = new char[4];
        Assert.Equal(2, r.GetChars(1, 1, chars, 1, 2));
        Assert.Equal("😀", new string(chars, 1, 2));
        Assert.Equal(0, r.GetBytes(0, long.MaxValue, bytes, 0, 1));
        Assert.Equal(0, r.GetChars(1, long.MaxValue, chars, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.GetBytes(0, -1, bytes, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.GetChars(1, 0, chars, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.GetBytes(0, 0, bytes, 0, -1));
        Assert.Throws<ArgumentException>(() => r.GetBytes(0, 0, bytes, 3, 2));
        Assert.Throws<InvalidCastException>(() => r.GetBytes(1, 0, null, 0, 0));
        using var stream = r.GetStream(0);
        using var text = r.GetTextReader(1);
        r.Close();
        Assert.Equal(0, stream.ReadByte());
        Assert.Equal("a😀z", text.ReadToEnd());
    }

    [Fact]
    public async Task SchemaReportsOnlyKnownFactsAndPreservesUnsignedTypeIdentity()
    {
        using var r = await Reader(Result([new PgColumnDescription("value", uint.MaxValue, 3, Oid("int4"), 4, 42)], []));
        IDbColumnSchemaGenerator schema = r;
        var column = Assert.Single(schema.GetColumnSchema());
        Assert.Equal("value", column.ColumnName);
        Assert.Equal(0, column.ColumnOrdinal);
        Assert.Equal(typeof(int), column.DataType);
        Assert.Equal("int4", column.DataTypeName);
        Assert.Null(column.AllowDBNull);
        Assert.Null(column.IsKey);
        Assert.Null(column.IsReadOnly);
        Assert.Null(column.ColumnSize);
        Assert.Null(column.NumericPrecision);
        Assert.Null(column.BaseTableName);
        using var table = r.GetSchemaTable();
        var row = Assert.Single(table.Rows.Cast<DataRow>());
        Assert.Equal("value", row[SchemaTableColumn.ColumnName]);
        Assert.Equal(typeof(int), row[SchemaTableColumn.DataType]);
        Assert.Equal(Oid("int4"), row["DataTypeOID"]);
        Assert.Equal(uint.MaxValue, row["BaseTableOID"]);
        Assert.Equal((short)3, row["ColumnAttributeNumber"]);
        Assert.Equal(42, row["TypeModifier"]);
        Assert.Same(DBNull.Value, row[SchemaTableColumn.AllowDBNull]);
        Assert.Same(DBNull.Value, row[SchemaTableColumn.IsKey]);
        row[SchemaTableColumn.ColumnName] = "mutated";
        Assert.Equal("value", r.GetName(0));
    }

    [Fact]
    public async Task UnknownOrInconsistentTypeLayoutsRejectBeforeReaderExposure()
    {
        var catalog = await CatalogTask;
        Assert.Throws<NotSupportedException>(() => new KingbaseDataReader([Result([new("x", 0, 0, uint.MaxValue, 4, -1)], [[null]])], catalog));
        Assert.Throws<NotSupportedException>(() => new KingbaseDataReader([Result([new("x", 0, 0, 12345, 4, -1)], [[null]])], catalog));
    }

    [Fact]
    public async Task SchemaChangesWithResultsAndRetainsProviderNumericAndUnsignedOids()
    {
        using DbDataReader r = await Reader(Result([Column("n", "numeric")], []), Result([Column("id", "uuid")], []));
        using (var table = r.GetSchemaTable()!)
        {
            Assert.Equal(typeof(decimal), table.Rows[0][SchemaTableColumn.DataType]);
            Assert.Equal(typeof(KingbaseNumeric), table.Rows[0]["ProviderSpecificDataType"]);
        }
        Assert.True(r.NextResult());
        Assert.Equal(typeof(Guid), r.GetColumnSchema()[0].DataType);
        using var nextTable = r.GetSchemaTable()!;
        Assert.Equal(uint.MaxValue, nextTable.Rows[0]["DataTypeOID"]);
        Assert.Equal("id", nextTable.Rows[0][SchemaTableColumn.ColumnName]);
        Assert.False(r.NextResult());
        Assert.Empty(r.GetColumnSchema());
        using var emptyTable = r.GetSchemaTable()!;
        Assert.Empty(emptyTable.Rows);
        r.Close();
        Assert.Throws<InvalidOperationException>(() => r.GetColumnSchema());
        Assert.Throws<InvalidOperationException>(() => r.GetSchemaTable());
    }

    [Fact]
    public async Task ConversionFailureDoesNotAdvanceBufferedRowAndCancellationPrecedesDecoding()
    {
        using var r = await Reader(Result([Column("n")], [["private-invalid-number"], ["7"]]));
        Assert.True(r.Read());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.GetFieldValueAsync<int>(0, cts.Token));
        var error = Assert.Throws<FormatException>(() => r.GetInt32(0));
        Assert.DoesNotContain("private-invalid-number", error.ToString());
        Assert.False(r.IsDBNull(0));
        Assert.True(r.Read());
        Assert.Equal(7, r.GetInt32(0));
    }

    [Theory]
    [InlineData("SELECT 5", -1)]
    [InlineData("CREATE TABLE", -1)]
    [InlineData("BEGIN", -1)]
    [InlineData("INSERT 0 0", 0)]
    [InlineData("INSERT 4294967295 4", 4)]
    [InlineData("UPDATE 5", 5)]
    [InlineData("DELETE 3", 3)]
    [InlineData("MERGE 7", 7)]
    public async Task AffectedCountsUseOnlyDmlTags(string tag, int count)
    {
        using var r = await Reader(Result([], [], tag));
        Assert.Equal(count, r.RecordsAffected);
    }

    [Theory]
    [InlineData("UPDATE -1")]
    [InlineData("INSERT 0")]
    [InlineData("INSERT x 1")]
    [InlineData("DELETE 1 trailing")]
    public async Task MalformedRecognizedCountsFailSafely(string tag)
    {
        var e = await Assert.ThrowsAsync<FormatException>(() => Reader(Result([], [], tag)));
        Assert.DoesNotContain(tag, e.Message);
    }

    [Fact]
    public async Task AffectedCountsIncludeReturningAndDetectArithmeticOverflow()
    {
        using var r = await Reader(Result([Column("id")], [["1"]], "INSERT 0 1"), Result([], [], "UPDATE 2"), Result([], [], "SELECT 5"));
        Assert.Equal(3, r.RecordsAffected);
        await Assert.ThrowsAsync<OverflowException>(() => Reader(Result([], [], "UPDATE 2147483648")));
        await Assert.ThrowsAsync<OverflowException>(() => Reader(Result([], [], "UPDATE 2147483647"), Result([], [], "DELETE 1")));
    }

    [Fact]
    public async Task AsyncCancellationPreservesCursorAndDisposalClosesIdempotently()
    {
        var r = await Reader(Result([Column("n")], [["1"], ["2"]]), Result([Column("m")], [["3"]]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.ReadAsync(cts.Token));
        Assert.Equal(cts.Token, e.CancellationToken);
        Assert.True(await r.ReadAsync());
        Assert.Equal(1, await r.GetFieldValueAsync<int>(0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.NextResultAsync(cts.Token));
        Assert.Equal(1, r.GetInt32(0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.IsDBNullAsync(0, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.GetFieldValueAsync<int>(0, cts.Token));
        Assert.True(await r.ReadAsync());
        Assert.Equal(2, r.GetInt32(0));
        Assert.False(await r.IsDBNullAsync(0));
        Assert.True(await r.NextResultAsync());
        Assert.True(await r.ReadAsync());
        Assert.Equal(3, r.GetInt32(0));
        await r.CloseAsync();
        await r.DisposeAsync();
        r.Dispose();
        Assert.True(r.IsClosed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.ReadAsync());
    }

    [Fact]
    public async Task DbEnumeratorVisitsValuesWithoutClosingReader()
    {
        using var r = await Reader(Result([Column("n")], [["1"], ["2"]]));
        var rows = r.Cast<DbDataRecord>().Select(record => record.GetInt32(0)).ToArray();
        Assert.Equal(new[] { 1, 2 }, rows);
        Assert.False(r.IsClosed);
    }

    [Fact]
    public async Task CompletedWireResultComposesWithReaderAndKeepsSessionReusable()
    {
        var input = Join(Description(1, Oid("int4"), 4), Row(["42"]), Complete(), Ready(), Scalar("43", Oid("int4"), 4));
        var stream = new QueryStream(input);
        using var session = new PgQuerySession(new PgWire(stream), Startup());
        using (var r = new KingbaseDataReader(await session.ExecuteAsync("SELECT 42"), await CatalogTask))
        {
            Assert.True(r.Read());
            Assert.Equal(42, r.GetInt32(0));
        }
        using var next = new KingbaseDataReader(await session.ExecuteAsync("SELECT 43"), await CatalogTask);
        Assert.True(next.Read());
        Assert.Equal(43, next.GetInt32(0));
        Assert.Equal(0, stream.DisposeCount);
    }

    static PgStartupResult Startup() => new(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2));
    static async Task<PgTypeCatalog> LoadCatalog()
    {
        var definitions = Types.Select(t => new PgTypeDescription(Oid(t.Name), 90, t.Name, t.Size, 'b', 'U', 0, 0, 0, -1));
        using var session = new PgQuerySession(new PgWire(new QueryStream(Catalog(definitions))), Startup());
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        return await PgTypeCatalog.LoadAsync(session, new PgIdentityResult(version!, "14", KingbaseCompatibilityMode.PostgreSql,
            KingbaseCompatibilityMode.PostgreSql, null, null));
    }
}
