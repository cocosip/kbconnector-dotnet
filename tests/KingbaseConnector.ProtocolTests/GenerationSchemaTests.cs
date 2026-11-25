using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class GenerationSchemaTests
{
    [Fact]
    public async Task SequenceDirectoryAndRestrictionsRejectExcessBeforeIo()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var directory = connection.GetSchema(); Assert.Equal(18, directory.Rows.Count);
            var entry = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == "Sequences");
            Assert.Equal(3, entry[1]); Assert.Equal(3, entry[2]);
            using var restrictions = connection.GetSchema("Restrictions"); Assert.Equal(51, restrictions.Rows.Count);
            var rows = restrictions.Rows.Cast<DataRow>().Where(row => (string)row[0] == "Sequences").ToArray();
            Assert.Equal(new[] { "Catalog", "Schema", "Sequence" }, rows.Select(row => (string)row[1]));
            Assert.Equal(new[] { 1, 2, 3 }, rows.Select(row => (int)row[3])); Assert.All(rows, row => Assert.Equal(DBNull.Value, row[2]));
            Assert.Throws<ArgumentException>(() => connection.GetSchema("Sequences", [null, null, null, "private-payload"]));
            await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync("Sequences", [null, null, null, "private-payload"], token));
        });
    }

    [Fact]
    public void SequenceQueriesIncludeIdentitySequencesAndNeverReadMutableState()
    {
        using var connection = new KingbaseConnection();
        using var command = KingbaseObjectSchema.Command(KingbaseObjectSchema.Find("Sequences")!, ["db", "schema", "literal'"], connection, null);
        Assert.Contains("FROM pg_catalog.pg_sequence", command.CommandText);
        Assert.Contains("pg_catalog.has_sequence_privilege", command.CommandText);
        Assert.Contains("NOT pg_catalog.pg_is_other_temp_schema(r.relnamespace)", command.CommandText);
        Assert.Contains("OPERATOR(pg_catalog.*)", command.CommandText);
        Assert.DoesNotContain("information_schema.sequences", command.CommandText);
        Assert.DoesNotContain("nextval", command.CommandText); Assert.DoesNotContain("last_value", command.CommandText);
        Assert.DoesNotContain("literal'", command.CommandText); Assert.DoesNotContain(" = ", command.CommandText);
        Assert.Equal(3, command.Parameters.Count);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("nulls")]
    [InlineData("empty")]
    [InlineData("literal")]
    [InlineData("sparse")]
    public async Task SequenceRestrictionsKeepExactLiteralAndNullSemantics(string kind)
    {
        string?[]? restrictions = kind switch
        {
            "omitted" => null, "nulls" => [null, null, null], "empty" => ["", "", ""],
            "sparse" => [null, null, "汉😀"], _ => ["db", "MiXeD汉%_'", "Sequence_%' OR 1=1 --"]
        };
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = await connection.GetSchemaAsync("Sequences", restrictions, token); Assert.Empty(table.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(restrictions?.Where(value => value is not null).ToArray() ?? [], ObjectSchemaTests.Parameters(request[1]));
            Assert.DoesNotContain(" LIKE ", sql); Assert.DoesNotContain("OR 1=1", sql);
            Assert.Contains("ORDER BY sequence_catalog::pg_catalog.text COLLATE pg_catalog.\"C\", sequence_schema", sql);
            await stream.WriteAsync(Result("Sequences", []), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Sequences", false)]
    [InlineData("Sequences", true)]
    [InlineData("Columns", false)]
    [InlineData("Columns", true)]
    public async Task SignedSequenceAndIdentityFactsKeepTheirTypedShapes(string name, bool async)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = async ? await connection.GetSchemaAsync(name.ToLowerInvariant(), [null, "MiXeD汉%_'"], token) :
                connection.GetSchema(name, [null, "MiXeD汉%_'"]);
            Assert.Equal(name, table.TableName); Assert.Equal(CultureInfo.InvariantCulture, table.Locale);
            Assert.Equal(Fields(name), table.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType)));
            var row = Assert.Single(table.Rows.Cast<DataRow>());
            if (name == "Sequences")
            {
                Assert.Equal(-2147483649L, row["START_VALUE"]); Assert.Equal(long.MinValue, row["MINIMUM_VALUE"]);
                Assert.Equal(-1L, row["MAXIMUM_VALUE"]); Assert.Equal(-2L, row["INCREMENT"]);
                Assert.True((bool)row["CYCLE_OPTION"]); Assert.Equal(17L, row["CACHE_SIZE"]);
            }
            else
            {
                Assert.Equal(DBNull.Value, row["COLUMN_DEFAULT"]); Assert.True((bool)row["IS_IDENTITY"]);
                Assert.Equal("ALWAYS", row["IDENTITY_GENERATION"]); Assert.Equal(-2147483649L, row["IDENTITY_START"]);
                Assert.Equal(-2L, row["IDENTITY_INCREMENT"]); Assert.Equal(-1L, row["IDENTITY_MAXIMUM"]);
                Assert.Equal(long.MinValue, row["IDENTITY_MINIMUM"]); Assert.True((bool)row["IDENTITY_CYCLE"]);
                Assert.Equal("NEVER", row["IS_GENERATED"]); Assert.Equal(DBNull.Value, row["GENERATION_EXPRESSION"]);
            }
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token);
            var sql = Encoding.UTF8.GetString(request[0]);
            Assert.DoesNotContain("MiXeD汉%_'", sql);
            Assert.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $1", sql);
            Assert.Contains("MiXeD汉%_'", Encoding.UTF8.GetString(request[1]));
            await stream.WriteAsync(Result(name), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("default")]
    [InlineData("nextval")]
    [InlineData("generated")]
    [InlineData("identity")]
    public async Task DefaultsAreNotInferredAsIdentityOrGeneratedAttributes(string kind)
    {
        var values = Values("Columns");
        values[25] = kind == "identity" ? "YES" : "NO";
        values[26] = kind == "identity" ? "BY DEFAULT" : null;
        for (var i = 27; i <= 31; i++) values[i] = kind == "identity" ? (i == 31 ? "NO" : "9223372036854775807") : null;
        values[5] = kind == "default" ? "42" : kind == "nextval" ? "nextval('seq'::regclass)" : null;
        values[32] = kind == "generated" ? "ALWAYS" : "NEVER"; values[33] = kind == "generated" ? "(a + 1)" : null;
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = connection.GetSchema("Columns"); var row = Assert.Single(table.Rows.Cast<DataRow>());
            Assert.Equal(kind == "identity", row["IS_IDENTITY"]); Assert.Equal(values[5] ?? (object)DBNull.Value, row["COLUMN_DEFAULT"]);
            Assert.Equal(values[32], row["IS_GENERATED"]); Assert.Equal(values[33] ?? (object)DBNull.Value, row["GENERATION_EXPRESSION"]);
            Assert.Equal(kind == "identity" ? "BY DEFAULT" : DBNull.Value, row["IDENTITY_GENERATION"]);
            Assert.Equal(kind == "identity" ? false : DBNull.Value, row["IDENTITY_CYCLE"]);
            Assert.Equal(kind == "identity" ? long.MaxValue : DBNull.Value, row["IDENTITY_START"]);
            table.Rows.Clear(); using var fresh = await connection.GetSchemaAsync("Columns", token); Assert.Single(fresh.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            for (var i = 0; i < 2; i++)
            {
                var request = await ReadExtended(stream, token);
                Assert.Contains("CASE WHEN is_identity::pg_catalog.text OPERATOR(pg_catalog.=) 'YES' THEN identity_cycle END", Encoding.UTF8.GetString(request[0]));
                await stream.WriteAsync(Result("Columns", [values]), token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Sequences", "null")]
    [InlineData("Sequences", "duplicate")]
    [InlineData("Sequences", "flag")]
    [InlineData("Sequences", "overflow")]
    [InlineData("Sequences", "zero-increment")]
    [InlineData("Sequences", "zero-cache")]
    [InlineData("Sequences", "negative-cache")]
    [InlineData("Sequences", "permission")]
    [InlineData("Columns", "identity-flag")]
    [InlineData("Columns", "cycle-flag")]
    [InlineData("Columns", "identity-number")]
    [InlineData("Columns", "identity-zero-increment")]
    [InlineData("Columns", "negative-length")]
    [InlineData("Columns", "null-generated")]
    public async Task InvalidGenerationFactsRejectSafelyAndRecover(string name, string kind)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            if (kind == "permission")
            {
                var error = await Assert.ThrowsAsync<KingbaseException>(() => connection.GetSchemaAsync(name, token));
                Assert.Equal("42501", error.SqlState); Assert.DoesNotContain("private-payload", error.ToString());
            }
            else
            {
                var error = await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync(name, token));
                Assert.Null(error.InnerException); Assert.DoesNotContain("private-payload", error.ToString());
            }
            Assert.Equal(ConnectionState.Open, connection.State);
            using var recovered = await connection.GetSchemaAsync(name, token); Assert.Single(recovered.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token); var values = Values(name);
            if (kind == "null") values[0] = null;
            if (kind == "flag") values[11] = "private-payload";
            if (kind == "overflow") values[7] = "9223372036854775808";
            if (kind == "zero-increment") values[10] = "0";
            if (kind == "zero-cache") values[12] = "0";
            if (kind == "negative-cache") values[12] = "-1";
            if (kind == "identity-flag") values[25] = "private-payload";
            if (kind == "cycle-flag") values[31] = "private-payload";
            if (kind == "identity-number") values[27] = "private-payload";
            if (kind == "identity-zero-increment") values[28] = "0";
            if (kind == "negative-length") values[9] = "-1";
            if (kind == "null-generated") values[32] = null;
            var response = kind == "permission" ? Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()) :
                Result(name, kind == "duplicate" ? [values, values] : [values]);
            await stream.WriteAsync(response, token);
            await ReadExtended(stream, token); await stream.WriteAsync(Result(name), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Fact]
    public async Task OversizedSequenceCatalogRetiresWithoutTruncation()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync("Sequences", token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            await stream.WriteAsync(Result("Sequences", Enumerable.Range(0, 1025).Select(_ => Values("Sequences")).ToArray()), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        });
    }

    internal static (string Name, Type Type)[] Fields(string name) => name == "Sequences" ?
        [("SEQUENCE_CATALOG", typeof(string)), ("SEQUENCE_SCHEMA", typeof(string)), ("SEQUENCE_NAME", typeof(string)), ("DATA_TYPE", typeof(string)),
            ("NUMERIC_PRECISION", typeof(int)), ("NUMERIC_PRECISION_RADIX", typeof(int)), ("NUMERIC_SCALE", typeof(int)),
            ("START_VALUE", typeof(long)), ("MINIMUM_VALUE", typeof(long)), ("MAXIMUM_VALUE", typeof(long)), ("INCREMENT", typeof(long)),
            ("CYCLE_OPTION", typeof(bool)), ("CACHE_SIZE", typeof(long))] :
        [.. ObjectSchemaTests.Fields("Columns").Take(25), ("IS_IDENTITY", typeof(bool)), ("IDENTITY_GENERATION", typeof(string)),
            ("IDENTITY_START", typeof(long)), ("IDENTITY_INCREMENT", typeof(long)), ("IDENTITY_MAXIMUM", typeof(long)), ("IDENTITY_MINIMUM", typeof(long)),
            ("IDENTITY_CYCLE", typeof(bool)), ("IS_GENERATED", typeof(string)), ("GENERATION_EXPRESSION", typeof(string))];

    internal static string?[] Values(string name) => name == "Sequences" ?
        ["db", "MiXeD汉%_'", "sequence", "bigint", "64", "2", "0", "-2147483649", "-9223372036854775808", "-1", "-2", "YES", "17"] :
        [.. ObjectSchemaTests.Values("Columns").Take(25), "YES", "ALWAYS", "-2147483649", "-2", "-1", "-9223372036854775808", "YES", "NEVER", null];

    internal static byte[] Result(string name, string?[][]? rows = null) => Join(Frame('1'), Frame('2'), Header(name),
        Join((rows ?? [Values(name)]).Select(ObjectRow).ToArray()), Complete(), Ready());

    static byte[] Header(string name) => Frame('T', Join(Count(Fields(name).Length), Join(Fields(name).Select(field =>
        Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6], Int32(field.Type == typeof(int) ? -1 : field.Type == typeof(long) ? 9003 : 9004),
            Count(field.Type == typeof(int) ? 4 : field.Type == typeof(long) ? 8 : -1), Int32(-1), Count(0))).ToArray())));
    static byte[] ObjectRow(string?[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) :
        Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
}
