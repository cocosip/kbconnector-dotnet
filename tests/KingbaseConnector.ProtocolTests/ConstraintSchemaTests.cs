using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class ConstraintSchemaTests
{
    [Theory]
    [InlineData("Constraints", 4, 4)]
    [InlineData("ConstraintColumns", 5, 5)]
    [InlineData("Schemata", 2, 2)]
    [InlineData("PrimaryKey", 4, 5)]
    public async Task CanonicalDirectoryAndAliasValidationStayConsistent(string name, int count, int parts)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var directory = connection.GetSchema(); Assert.Equal(29, directory.Rows.Count);
            var canonical = name == "Schemata" ? "Schemas" : name == "PrimaryKey" ? "PrimaryKeys" : name;
            var entry = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == canonical);
            Assert.Equal(count, entry[1]); Assert.Equal(parts, entry[2]);
            using var restrictions = connection.GetSchema("Restrictions"); Assert.Equal(87, restrictions.Rows.Count);
            var rows = restrictions.Rows.Cast<DataRow>().Where(row => (string)row[0] == canonical).ToArray();
            Assert.Equal(count, rows.Length); Assert.Equal(Enumerable.Range(1, count), rows.Select(row => (int)row[3]));
            Assert.All(rows, row => Assert.Equal(DBNull.Value, row[2]));
            Assert.Throws<ArgumentException>(() => connection.GetSchema(name, new string?[count + 1]));
            await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync(name, new string?[count + 1], token));
        });
    }

    [Theory]
    [InlineData("Constraints", false)]
    [InlineData("Constraints", true)]
    [InlineData("ConstraintColumns", false)]
    [InlineData("ConstraintColumns", true)]
    public async Task GeneralConstraintFactsUseFixedTypesAndExactRestrictions(string name, bool async)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = async ? await connection.GetSchemaAsync(name.ToLowerInvariant(), [null, "MiXeD汉%_'"], token) :
                connection.GetSchema(name, [null, "MiXeD汉%_'"]);
            Assert.Equal(name, table.TableName); Assert.Equal(CultureInfo.InvariantCulture, table.Locale);
            Assert.Equal(Fields(name), table.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType)));
            var row = Assert.Single(table.Rows.Cast<DataRow>());
            Assert.Equal("EXCLUSION", row["CONSTRAINT_TYPE"]); Assert.Equal("x", row["CONSTRAINT_TYPE_CODE"]);
            if (name == "Constraints")
            {
                Assert.True((bool)row["IS_DEFERRABLE"]); Assert.False((bool)row["INITIALLY_DEFERRED"]);
                Assert.False((bool)row["IS_VALIDATED"]); Assert.True((bool)row["NO_INHERIT"]);
                Assert.Equal("EXCLUDE USING btree (abs(a) WITH =, b WITH =)", row["DEFINITION"]);
            }
            else { Assert.Equal(2, row["ORDINAL_POSITION"]); Assert.Equal("b", row["COLUMN_NAME"]); }
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(new[] { "MiXeD汉%_'" }, ObjectSchemaTests.Parameters(request[1]));
            Assert.DoesNotContain("MiXeD", sql); Assert.Contains("pg_catalog.pg_constraint", sql);
            Assert.Contains("pg_catalog.pg_is_other_temp_schema", sql);
            await stream.WriteAsync(Result(name), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("sChEmAtA", "Schemas", false)]
    [InlineData("Schemata", "Schemas", true)]
    [InlineData("pRiMaRyKeY", "PrimaryKeys", false)]
    [InlineData("PrimaryKey", "PrimaryKeys", true)]
    public async Task AliasesReturnCanonicalTablesAndUseCanonicalBinding(string alias, string canonical, bool async)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = async ? await connection.GetSchemaAsync(alias, [null, "MiXeD汉%_'"], token) : connection.GetSchema(alias, [null, "MiXeD汉%_'"]);
            Assert.Equal(canonical, table.TableName); Assert.Single(table.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token);
            Assert.Equal(new[] { "MiXeD汉%_'" }, ObjectSchemaTests.Parameters(request[1]));
            await stream.WriteAsync(canonical == "Schemas" ? ObjectSchemaTests.Result(canonical) : RelationalSchemaTests.Result(canonical), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Constraints", "omitted")]
    [InlineData("Constraints", "nulls")]
    [InlineData("Constraints", "empty")]
    [InlineData("Constraints", "sparse")]
    [InlineData("Constraints", "literal")]
    [InlineData("ConstraintColumns", "omitted")]
    [InlineData("ConstraintColumns", "nulls")]
    [InlineData("ConstraintColumns", "empty")]
    [InlineData("ConstraintColumns", "sparse")]
    [InlineData("ConstraintColumns", "literal")]
    public async Task FiltersKeepLiteralEmptySparseAndCaseSensitiveValues(string name, string kind)
    {
        string?[]? values = kind switch
        {
            "omitted" => null, "nulls" => new string?[name == "Constraints" ? 4 : 5],
            "empty" => name == "Constraints" ? ["", "", "", ""] : ["", "", "", "", ""],
            "sparse" => name == "Constraints" ? [null, null, null, "汉😀"] : [null, null, null, null, "汉😀"],
            _ => name == "Constraints" ? ["db", "Mixed", "t_%'", "c' OR 1=1 --"] : ["db", "Mixed", "t_%'", "c", "a' OR 1=1 --"]
        };
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = await connection.GetSchemaAsync(name, values, token); Assert.Empty(table.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(values?.Where(value => value is not null).ToArray() ?? [], ObjectSchemaTests.Parameters(request[1]));
            Assert.DoesNotContain(" LIKE ", sql); Assert.DoesNotContain("OR 1=1", sql);
            Assert.Contains("COLLATE pg_catalog.\"C\"", sql); Assert.DoesNotContain(" = ", sql);
            if (name == "ConstraintColumns")
            {
                Assert.Contains("pg_catalog.array_lower(c.conkey,1) OPERATOR(pg_catalog.+) k.position OPERATOR(pg_catalog.-) 1", sql);
                Assert.Contains("a.attnum OPERATOR(pg_catalog.>) 0 AND NOT a.attisdropped", sql);
                Assert.Contains("constraint_name::pg_catalog.text COLLATE pg_catalog.\"C\", ordinal_position", sql);
            }
            else Assert.Contains("pg_catalog.pg_get_constraintdef(c.oid,true)", sql);
            await stream.WriteAsync(Result(name, []), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Constraints")]
    [InlineData("ConstraintColumns")]
    public async Task UnknownKindsKeepRawCodesAndUnavailableFriendlyTypes(string name)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = await connection.GetSchemaAsync(name, token);
            var row = Assert.Single(table.Rows.Cast<DataRow>()); Assert.Equal("z", row["CONSTRAINT_TYPE_CODE"]);
            Assert.Equal(DBNull.Value, row["CONSTRAINT_TYPE"]);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token); var values = Values(name); values[name == "Constraints" ? 4 : 6] = "z";
            values[name == "Constraints" ? 5 : 7] = null;
            await stream.WriteAsync(Result(name, [values]), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Fact]
    public async Task ColumnSlotsPreserveRepeatedColumnsNumericPositionsAndFreshTables()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var first = connection.GetSchema("ConstraintColumns");
            Assert.Equal(new[] { 2, 12 }, first.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
            Assert.All(first.Rows.Cast<DataRow>(), row => Assert.Equal("b", row["COLUMN_NAME"])); first.Rows.Clear();
            using var fresh = await connection.GetSchemaAsync("ConstraintColumns", token); Assert.Equal(2, fresh.Rows.Count);
            Assert.Equal(CultureInfo.InvariantCulture, fresh.Locale);
        }, async (stream, token) =>
        {
            var other = Values("ConstraintColumns"); other[4] = "12";
            for (var i = 0; i < 2; i++) { await ReadExtended(stream, token); await stream.WriteAsync(Result("ConstraintColumns", [Values("ConstraintColumns"), other]), token); }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Constraints", "missing-name")]
    [InlineData("Constraints", "empty-definition")]
    [InlineData("Constraints", "missing-code")]
    [InlineData("Constraints", "flag")]
    [InlineData("Constraints", "duplicate")]
    [InlineData("Constraints", "alias")]
    [InlineData("Constraints", "oid")]
    [InlineData("Constraints", "size")]
    [InlineData("Constraints", "permission")]
    [InlineData("ConstraintColumns", "missing-name")]
    [InlineData("ConstraintColumns", "missing-code")]
    [InlineData("ConstraintColumns", "zero-ordinal")]
    [InlineData("ConstraintColumns", "negative-ordinal")]
    [InlineData("ConstraintColumns", "overflow-ordinal")]
    [InlineData("ConstraintColumns", "duplicate")]
    [InlineData("ConstraintColumns", "alias")]
    [InlineData("ConstraintColumns", "oid")]
    [InlineData("ConstraintColumns", "size")]
    [InlineData("ConstraintColumns", "permission")]
    public async Task InvalidFactsAndPermissionErrorsKeepSynchronizedRecovery(string name, string kind)
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
            if (kind == "missing-name") values[3] = null;
            if (kind == "empty-definition") values[10] = "";
            if (kind == "missing-code") values[name == "Constraints" ? 4 : 6] = null;
            if (kind == "flag") values[6] = "private-payload";
            if (kind == "zero-ordinal") values[4] = "0";
            if (kind == "negative-ordinal") values[4] = "-1";
            if (kind == "overflow-ordinal") values[4] = "2147483648";
            var response = kind == "permission" ? Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()) :
                Join(Frame('1'), Frame('2'), Header(name, kind), Row(values), kind == "duplicate" ? Row(values) : [], Complete(), Ready());
            await stream.WriteAsync(response, token); await ReadExtended(stream, token); await stream.WriteAsync(Result(name), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task InterruptedConstraintRetrievalRetiresWithoutReplay(string reason)
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
        await connection.OpenAsync(guard.Token); var pending = connection.GetSchemaAsync("ConstraintColumns", [null, "schema"], caller.Token);
        await received.Task.WaitAsync(guard.Token); Assert.Throws<InvalidOperationException>(() => connection.GetSchema("PrimaryKey"));
        if (reason == "caller") caller.Cancel(); else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task OversizedConstraintsRetireWithoutTruncation()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync("Constraints", token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token); await stream.WriteAsync(Result("Constraints", Enumerable.Range(0, 1025).Select(_ => Values("Constraints")).ToArray()), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        });
    }

    internal static (string Name, Type Type)[] Fields(string name) => name == "Constraints" ?
        [("TABLE_CATALOG", typeof(string)), ("TABLE_SCHEMA", typeof(string)), ("TABLE_NAME", typeof(string)), ("CONSTRAINT_NAME", typeof(string)),
            ("CONSTRAINT_TYPE_CODE", typeof(string)), ("CONSTRAINT_TYPE", typeof(string)), ("IS_DEFERRABLE", typeof(bool)), ("INITIALLY_DEFERRED", typeof(bool)),
            ("IS_VALIDATED", typeof(bool)), ("NO_INHERIT", typeof(bool)), ("DEFINITION", typeof(string))] :
        [("TABLE_CATALOG", typeof(string)), ("TABLE_SCHEMA", typeof(string)), ("TABLE_NAME", typeof(string)), ("CONSTRAINT_NAME", typeof(string)),
            ("ORDINAL_POSITION", typeof(int)), ("COLUMN_NAME", typeof(string)), ("CONSTRAINT_TYPE_CODE", typeof(string)), ("CONSTRAINT_TYPE", typeof(string))];

    internal static string?[] Values(string name) => name == "Constraints" ?
        ["db", "MiXeD汉%_'", "table", "constraint", "x", "EXCLUSION", "YES", "NO", "NO", "YES", "EXCLUDE USING btree (abs(a) WITH =, b WITH =)"] :
        ["db", "MiXeD汉%_'", "table", "constraint", "2", "b", "x", "EXCLUSION"];

    internal static byte[] Result(string name, string?[][]? rows = null) => Join(Frame('1'), Frame('2'), Header(name),
        Join((rows ?? [Values(name)]).Select(Row).ToArray()), Complete(), Ready());
    static byte[] Header(string name, string? kind = null) => Frame('T', Join(Count(Fields(name).Length), Join(Fields(name).Select((field, i) =>
        Join(Encoding.UTF8.GetBytes((i == 0 && kind == "alias" ? "private-payload" : field.Name) + '\0'), new byte[6], Int32(i == 0 && kind == "oid" ? 987654 : field.Type == typeof(int) ? -1 : 9004),
            Count(i == 0 && kind == "size" ? 4 : field.Type == typeof(int) ? 4 : -1), Int32(-1), Count(0))).ToArray())));
    static byte[] Row(string?[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) :
        Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
}
