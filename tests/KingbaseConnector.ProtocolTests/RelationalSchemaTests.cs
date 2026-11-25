using System.Data;
using System.Text;
using System.Net.Sockets;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class RelationalSchemaTests
{
    [Theory]
    [InlineData("PrimaryKeys", 4, 5)]
    [InlineData("UniqueKeys", 4, 5)]
    [InlineData("ForeignKeys", 4, 4)]
    [InlineData("ForeignKeyColumns", 5, 5)]
    [InlineData("Indexes", 4, 3)]
    [InlineData("IndexColumns", 5, 4)]
    public async Task DirectoryAndRestrictionPreflightAreConsistent(string name, int count, int parts)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var directory = connection.GetSchema(); Assert.Equal(25, directory.Rows.Count);
            var row = Assert.Single(directory.Rows.Cast<DataRow>(), item => (string)item[0] == name);
            Assert.Equal(count, row[1]); Assert.Equal(parts, row[2]);
            using var restrictions = await connection.GetSchemaAsync("Restrictions", token);
            Assert.Equal(72, restrictions.Rows.Count);
            var entries = restrictions.Rows.Cast<DataRow>().Where(item => (string)item[0] == name).ToArray();
            var names = name.StartsWith("Index", StringComparison.Ordinal) ? new[] { "Catalog", "Schema", "Table", "Index", "Column" } :
                ["Catalog", "Schema", "Table", "Constraint", "Column"];
            Assert.Equal(names.Take(count), entries.Select(item => (string)item[1]));
            Assert.Equal(Enumerable.Range(1, count), entries.Select(item => (int)item[3]));
            Assert.All(entries, item => Assert.Equal(DBNull.Value, item[2]));
            var tooMany = Enumerable.Repeat<string?>("private-payload", count + 1).ToArray();
            var error = Assert.Throws<ArgumentException>(() => connection.GetSchema(name, tooMany));
            Assert.DoesNotContain("private-payload", error.ToString());
            await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync(name, tooMany, token));
        });
    }

    [Theory]
    [InlineData("PrimaryKeys")]
    [InlineData("UniqueKeys")]
    [InlineData("ForeignKeys")]
    [InlineData("ForeignKeyColumns")]
    [InlineData("Indexes")]
    [InlineData("IndexColumns")]
    public void CatalogQueriesUseActualOidJoinsAndSystemOperators(string name)
    {
        using var connection = new KingbaseConnection();
        var definition = KingbaseObjectSchema.Find(name)!;
        using var command = KingbaseObjectSchema.Command(definition, ["db", "schema", "table", "constraint'"], connection, null);
        Assert.DoesNotContain("constraint'", command.CommandText);
        Assert.DoesNotContain(" = ", command.CommandText);
        Assert.DoesNotContain(" + ", command.CommandText);
        Assert.DoesNotContain(" - ", command.CommandText);
        Assert.Contains("pg_catalog.pg_class", command.CommandText);
        Assert.Contains("pg_catalog.has_any_column_privilege", command.CommandText);
        Assert.Contains("NOT pg_catalog.pg_is_other_temp_schema(r.relnamespace)", command.CommandText);
        Assert.Equal(Enumerable.Range(0, 4).Select(i => "r" + i), command.Parameters.Cast<KingbaseParameter>().Select(item => item.ParameterName));
        Assert.All(command.Parameters.Cast<KingbaseParameter>(), parameter => Assert.Equal(KingbaseDbType.Text, parameter.KingbaseDbType));
        if (name is "PrimaryKeys" or "UniqueKeys" or "ForeignKeyColumns") Assert.Contains("pg_catalog.array_lower(c.conkey,1)", command.CommandText);
        if (name == "ForeignKeyColumns") Assert.Contains("pg_catalog.array_lower(c.confkey,1)", command.CommandText);
        if (name == "IndexColumns") Assert.Contains("pg_catalog.array_lower(i.indkey,1)", command.CommandText);
    }

    [Theory]
    [InlineData("PrimaryKeys")]
    [InlineData("UniqueKeys")]
    [InlineData("ForeignKeyColumns")]
    [InlineData("IndexColumns")]
    public async Task OrderedCompositeRowsRetainNumericOrdinalsAndFreshTables(string name)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = await connection.GetSchemaAsync(name, token);
            Assert.Equal(new[] { 2, 10 }, table.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
            table.Rows.Clear(); using var fresh = connection.GetSchema(name); Assert.Equal(2, fresh.Rows.Count);
        }, async (stream, token) =>
        {
            var second = Values(name); var ordinal = Array.FindIndex(Fields(name), field => field.Name == "ORDINAL_POSITION");
            second[ordinal] = "10";
            if (name != "IndexColumns") second[4] = "other_column";
            for (var i = 0; i < 2; i++)
            {
                await ReadExtended(stream, token); await stream.WriteAsync(Result(name, [Values(name), second]), token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("included")]
    [InlineData("non-btree")]
    [InlineData("plain")]
    public async Task UnknownIndexColumnFactsStayNull(string kind)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = await connection.GetSchemaAsync("IndexColumns", token);
            Assert.Equal("column", table.Rows[0]["COLUMN_NAME"]); Assert.Equal(DBNull.Value, table.Rows[0]["COLUMN_EXPRESSION"]);
            Assert.Equal(kind == "included", table.Rows[0]["IS_INCLUDED"]);
            if (kind != "plain")
            {
                Assert.Equal(DBNull.Value, table.Rows[0]["IS_DESCENDING"]); Assert.Equal(DBNull.Value, table.Rows[0]["NULLS_FIRST"]);
            }
            foreach (var column in Fields("IndexColumns").Skip(12)) Assert.Equal(DBNull.Value, table.Rows[0][column.Name]);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            var values = Values("IndexColumns"); values[7] = "column"; values[8] = null; values[9] = kind == "included" ? "YES" : "NO";
            if (kind != "plain") values[10] = values[11] = null;
            for (var i = 12; i < values.Length; i++) values[i] = null;
            await stream.WriteAsync(Result("IndexColumns", [values]), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("duplicate", "IndexColumns")]
    [InlineData("duplicate", "ForeignKeyColumns")]
    [InlineData("null-name", "PrimaryKeys")]
    [InlineData("null-flag", "Indexes")]
    [InlineData("bad-flag", "IndexColumns")]
    [InlineData("ordinal", "ForeignKeyColumns")]
    [InlineData("negative", "Indexes")]
    [InlineData("number", "IndexColumns")]
    [InlineData("header", "Indexes")]
    [InlineData("permission", "ForeignKeys")]
    public async Task SynchronizedMalformedMetadataAndErrorsAllowRecovery(string kind, string name)
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
            if (kind == "null-name") values[0] = null;
            if (kind == "null-flag") values[7] = null;
            if (kind == "bad-flag") values[10] = "private-payload";
            if (kind == "ordinal") values[5] = "0";
            if (kind == "negative") values[13] = "-1";
            if (kind == "number") values[3] = "private-payload";
            var response = kind == "permission" ? Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()) :
                kind == "header" ? Result("ForeignKeys") : Result(name, kind == "duplicate" ? [values, values] : [values]);
            await stream.WriteAsync(response, token);
            await ReadExtended(stream, token); await stream.WriteAsync(Result(name), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task InterruptedRelationalCatalogRetiresWithoutReplay(string reason)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource(); var settings = Settings(listener); settings.CommandTimeout = reason == "timeout" ? 1 : 0;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await Initialize(stream, guard.Token); await ReadExtended(stream, guard.Token); received.SetResult();
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        });
        await connection.OpenAsync(guard.Token); var pending = connection.GetSchemaAsync("IndexColumns", [null, "schema"], caller.Token);
        await received.Task.WaitAsync(guard.Token);
        Assert.Throws<InvalidOperationException>(() => connection.GetSchema("ForeignKeys"));
        if (reason == "caller") caller.Cancel(); else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task OversizedRelationalCatalogRetiresWithoutTruncation()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync("IndexColumns", token));
            Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            await stream.WriteAsync(Result("IndexColumns", Enumerable.Range(0, 1025).Select(_ => Values("IndexColumns")).ToArray()), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        });
    }

    [Theory]
    [InlineData("PrimaryKeys", false)]
    [InlineData("PrimaryKeys", true)]
    [InlineData("UniqueKeys", false)]
    [InlineData("UniqueKeys", true)]
    [InlineData("ForeignKeys", false)]
    [InlineData("ForeignKeys", true)]
    [InlineData("ForeignKeyColumns", false)]
    [InlineData("ForeignKeyColumns", true)]
    [InlineData("Indexes", false)]
    [InlineData("Indexes", true)]
    [InlineData("IndexColumns", false)]
    [InlineData("IndexColumns", true)]
    public async Task CollectionsHaveFixedTypedFactsAndBoundRestrictions(string name, bool async)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var schema = async ? await connection.GetSchemaAsync(name, [null, "MiXeD汉%_'"], token) :
                connection.GetSchema(name, [null, "MiXeD汉%_'"]);
            Assert.Equal(name, schema.TableName);
            Assert.Equal(Fields(name).Select(field => field.Name), schema.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
            Assert.Equal(Fields(name).Select(field => field.Type), schema.Columns.Cast<DataColumn>().Select(column => column.DataType));
            Assert.Single(schema.Rows.Cast<DataRow>());
            for (var i = 0; i < Fields(name).Length; i++)
            {
                var expected = Values(name)[i];
                if (expected is null) Assert.Equal(DBNull.Value, schema.Rows[0][i]);
                else if (Fields(name)[i].Type == typeof(bool)) Assert.Equal(expected == "YES", schema.Rows[0][i]);
                else if (Fields(name)[i].Type == typeof(int)) Assert.Equal(int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), schema.Rows[0][i]);
                else Assert.Equal(expected, schema.Rows[0][i]);
            }
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token);
            var sql = Encoding.UTF8.GetString(request[0]);
            Assert.DoesNotContain("MiXeD汉%_'", sql);
            Assert.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $1", sql);
            Assert.Contains("MiXeD汉%_'", Encoding.UTF8.GetString(request[1]));
            if (name.EndsWith("Columns", StringComparison.Ordinal) || name.EndsWith("Keys", StringComparison.Ordinal) && name != "ForeignKeys")
                Assert.EndsWith("ordinal_position", sql.Substring(sql.IndexOf("ORDER BY ", StringComparison.Ordinal)).Split('\0')[0]);
            await stream.WriteAsync(Result(name), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    internal static (string Name, Type Type)[] Fields(string name)
    {
        var names = name switch
        {
            "PrimaryKeys" or "UniqueKeys" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME COLUMN_NAME ORDINAL_POSITION IS_DEFERRABLE INITIALLY_DEFERRED",
            "ForeignKeys" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME REFERENCED_TABLE_CATALOG REFERENCED_TABLE_SCHEMA REFERENCED_TABLE_NAME REFERENCED_INDEX_NAME MATCH_OPTION UPDATE_RULE DELETE_RULE IS_DEFERRABLE INITIALLY_DEFERRED IS_VALIDATED",
            "ForeignKeyColumns" => "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_NAME COLUMN_NAME ORDINAL_POSITION REFERENCED_TABLE_CATALOG REFERENCED_TABLE_SCHEMA REFERENCED_TABLE_NAME REFERENCED_COLUMN_NAME",
            "Indexes" => "INDEX_CATALOG INDEX_SCHEMA INDEX_NAME TABLE_CATALOG TABLE_SCHEMA TABLE_NAME INDEX_TYPE IS_UNIQUE IS_PRIMARY IS_VALID IS_READY IS_LIVE IS_CLUSTERED KEY_COLUMN_COUNT COLUMN_COUNT PREDICATE EXPRESSION DEFINITION",
            _ => "INDEX_CATALOG INDEX_SCHEMA INDEX_NAME ORDINAL_POSITION TABLE_CATALOG TABLE_SCHEMA TABLE_NAME COLUMN_NAME COLUMN_EXPRESSION IS_INCLUDED IS_DESCENDING NULLS_FIRST COLLATION_CATALOG COLLATION_SCHEMA COLLATION_NAME OPERATOR_CLASS_CATALOG OPERATOR_CLASS_SCHEMA OPERATOR_CLASS_NAME"
        };
        return names.Split(' ').Select(field => (field, field is "ORDINAL_POSITION" or "KEY_COLUMN_COUNT" or "COLUMN_COUNT" ? typeof(int) :
            field.StartsWith("IS_", StringComparison.Ordinal) || field is "INITIALLY_DEFERRED" or "NULLS_FIRST" ? typeof(bool) : typeof(string))).ToArray();
    }

    internal static string?[] Values(string name) => name switch
    {
        "PrimaryKeys" or "UniqueKeys" => ["db", "MiXeD汉%_'", "table", "key", "column", "2", "YES", "NO"],
        "ForeignKeys" => ["db", "MiXeD汉%_'", "table", "fk", "db", "target", "parent", "unique_index", "SIMPLE", "CASCADE", "SET NULL", "YES", "NO", "YES"],
        "ForeignKeyColumns" => ["db", "MiXeD汉%_'", "table", "fk", "column", "2", "db", "target", "parent", "target_column"],
        "Indexes" => ["db", "MiXeD汉%_'", "index", "db", "MiXeD汉%_'", "table", "btree", "YES", "NO", "YES", "YES", "YES", "NO", "1", "2", "flag > 0", "lower(name)", "CREATE INDEX ..."],
        _ => ["db", "MiXeD汉%_'", "index", "2", "db", "MiXeD汉%_'", "table", null, "lower(name)", "NO", "YES", "NO", "db", "pg_catalog", "C", "db", "pg_catalog", "text_ops"]
    };

    internal static byte[] Result(string name, string?[][]? rows = null) => Join(Frame('1'), Frame('2'), Header(name),
        Join((rows ?? [Values(name)]).Select(Row).ToArray()), Complete(), Ready());

    static byte[] Header(string name) => Frame('T', Join(Count(Fields(name).Length), Join(Fields(name).Select(field =>
        Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6], Int32(field.Type == typeof(int) ? -1 : 9004),
            Count(field.Type == typeof(int) ? 4 : -1), Int32(-1), Count(0))).ToArray())));

    static byte[] Row(string?[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) :
        Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
}
