using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class RoutineSchemaTests
{
    [Fact]
    public void RoutineKindComparisonsUseOnlySystemOperators()
    {
        using var connection = new KingbaseConnection();
        using var command = KingbaseObjectSchema.Command(KingbaseObjectSchema.Find("ProcedureParameters")!, null, connection, null);
        Assert.DoesNotContain(" IN (", command.CommandText);
        Assert.Contains("p.prokind OPERATOR(pg_catalog.=) 'f'", command.CommandText);
        Assert.Contains("p.prokind OPERATOR(pg_catalog.=) 'p'", command.CommandText);
    }

    [Theory]
    [InlineData("Functions", false)]
    [InlineData("Functions", true)]
    [InlineData("Procedures", false)]
    [InlineData("Procedures", true)]
    [InlineData("ProcedureParameters", false)]
    [InlineData("ProcedureParameters", true)]
    public async Task RoutineFactsPreserveOverloadIdentityAndDeclaredSlots(string name, bool async)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = async ? await connection.GetSchemaAsync(name.ToLowerInvariant(), ["db", "MiXeD汉%_'", "routine"], token) :
                connection.GetSchema(name, ["db", "MiXeD汉%_'", "routine"]);
            Assert.Equal(name, table.TableName); Assert.Equal(CultureInfo.InvariantCulture, table.Locale);
            Assert.Equal(Fields(name), table.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType)));
            Assert.Equal(2, table.Rows.Count); Assert.Equal("routine_1", table.Rows[0]["SPECIFIC_NAME"]);
            if (name == "ProcedureParameters")
            {
                Assert.Equal(new[] { 1, 2 }, table.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
                Assert.Equal(DBNull.Value, table.Rows[0]["PARAMETER_NAME"]); Assert.Equal("INOUT", table.Rows[1]["PARAMETER_MODE"]);
                Assert.Equal("ARRAY", table.Rows[0]["DATA_TYPE"]); Assert.True((bool)table.Rows[0]["IS_VARIADIC"]);
            }
            else
            {
                Assert.Equal("routine_2", table.Rows[1]["SPECIFIC_NAME"]);
                Assert.Equal(name == "Functions" ? "FUNCTION" : "PROCEDURE", table.Rows[0]["ROUTINE_TYPE"]);
                Assert.Equal(name == "Functions" ? "numeric" : DBNull.Value, table.Rows[0]["DATA_TYPE"]);
                Assert.Equal(DBNull.Value, table.Rows[0]["NUMERIC_PRECISION"]);
            }
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(new[] { "db", "MiXeD汉%_'", "routine" }, ObjectSchemaTests.Parameters(request[1]));
            Assert.DoesNotContain("MiXeD", sql); Assert.Contains("information_schema.routines", sql);
            Assert.Contains("NOT pg_catalog.pg_is_other_temp_schema(p.pronamespace)", sql);
            Assert.DoesNotContain("routine_definition", sql); Assert.DoesNotContain("prosrc", sql);
            var first = Values(name); var second = Values(name);
            second[name == "ProcedureParameters" ? 4 : 3] = name == "ProcedureParameters" ? "2" : "routine_2";
            if (name == "ProcedureParameters") { second[6] = "INOUT"; second[7] = "NO"; }
            await stream.WriteAsync(Result(name, [first, second]), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }


    [Fact]
    public async Task RoutineDirectoryExposesRestrictionOrderAndRejectsExcessBeforeIo()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var directory = connection.GetSchema(); Assert.Equal(29, directory.Rows.Count);
            using var restrictions = connection.GetSchema("Restrictions"); Assert.Equal(87, restrictions.Rows.Count);
            foreach (var name in new[] { "Functions", "Procedures", "ProcedureParameters" })
            {
                var count = name == "ProcedureParameters" ? 5 : 4;
                var entry = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == name);
                Assert.Equal(count, entry[1]); Assert.Equal(count, entry[2]);
                var rows = restrictions.Rows.Cast<DataRow>().Where(row => (string)row[0] == name).ToArray();
                Assert.Equal(new[] { "Catalog", "Schema", "Routine", "SpecificName", "Parameter" }.Take(count), rows.Select(row => (string)row[1]));
                Assert.Equal(Enumerable.Range(1, count), rows.Select(row => (int)row[3]));
                Assert.Throws<ArgumentException>(() => connection.GetSchema(name, new string?[count + 1]));
                await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync(name, new string?[count + 1], token));
            }
        });
    }

    [Theory]
    [InlineData("Functions", "omitted")]
    [InlineData("Functions", "nulls")]
    [InlineData("Functions", "empty")]
    [InlineData("Functions", "literal")]
    [InlineData("Procedures", "sparse")]
    [InlineData("ProcedureParameters", "sparse")]
    [InlineData("ProcedureParameters", "literal")]
    public async Task RoutineRestrictionsAreBoundExactAndKindQueriesStayQualified(string name, string kind)
    {
        var count = name == "ProcedureParameters" ? 5 : 4;
        string?[]? filters = kind switch { "omitted" => null, "nulls" => new string?[count], "empty" => Enumerable.Repeat<string?>("", count).ToArray(),
            "sparse" => [null, null, "汉😀_%' OR 1=1 --"], _ => Enumerable.Repeat<string?>("汉😀_%' OR 1=1 --", count).ToArray() };
        await StandardSchemaTests.Run(async (connection, token) =>
        { using var empty = await connection.GetSchemaAsync(name, filters, token); Assert.Empty(empty.Rows.Cast<DataRow>()); }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(filters?.Where(value => value is not null).ToArray() ?? [], ObjectSchemaTests.Parameters(request[1]));
            Assert.Contains("COLLATE pg_catalog.\"C\"", sql); Assert.DoesNotContain(" LIKE ", sql); Assert.DoesNotContain("OR 1=1", sql);
            Assert.DoesNotContain(" IN (", sql); Assert.DoesNotContain(" = ", sql); Assert.DoesNotContain("routine_definition", sql);
            if (name == "ProcedureParameters")
            {
                Assert.Contains("information_schema.parameters", sql); Assert.Contains("pg_catalog.array_lower(p.proargmodes,1)", sql);
                Assert.Contains("ORDER BY routine_catalog::pg_catalog.text COLLATE pg_catalog.\"C\", routine_schema", sql);
                Assert.Contains("specific_name::pg_catalog.text COLLATE pg_catalog.\"C\", ordinal_position\0", sql);
            }
            else
            {
                Assert.Contains("p.prokind OPERATOR(pg_catalog.=) '" + (name == "Functions" ? "f" : "p") + "'", sql);
                Assert.Contains("r.type_udt_name AS udt_name", sql);
            }
            await stream.WriteAsync(Result(name, []), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    public static IEnumerable<object[]> InvalidFacts()
    {
        foreach (var name in new[] { "Functions", "Procedures", "ProcedureParameters" })
            foreach (var fault in new[] { "name", "empty", "flag", "null-flag", "negative", "overflow", "length", "duplicate", "alias", "oid", "size", "permission" })
                yield return [name, fault];
    }
    [Theory]
    [MemberData(nameof(InvalidFacts))]
    public async Task InvalidRoutineFactsRecoverWithoutLeakingPayload(string name, string fault)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            if (fault == "permission")
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
            await ReadExtended(stream, token); var values = Values(name); var parameter = name == "ProcedureParameters";
            if (fault == "name") values[3] = null;
            if (fault == "empty") values[3] = "";
            if (fault == "flag") values[parameter ? 7 : 17] = "private-payload";
            if (fault == "null-flag") values[parameter ? 7 : 17] = null;
            if (fault == "negative") values[parameter ? 4 : 18] = parameter ? "0" : "-1";
            if (fault == "overflow") values[parameter ? 4 : 18] = "2147483648";
            if (fault == "length") values[parameter ? 10 : 6] = "-1";
            await stream.WriteAsync(fault == "permission" ? Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()) :
                Result(name, fault == "duplicate" ? [values, values] : [values], fault), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Result(name), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Functions")]
    [InlineData("Procedures")]
    [InlineData("ProcedureParameters")]
    public async Task OptionalUnknownFactsAndZeroArgumentSignaturesRemainHonest(string name)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var first = connection.GetSchema(name); var row = Assert.Single(first.Rows.Cast<DataRow>());
            Assert.Equal(-2, row["NUMERIC_SCALE"]); Assert.Equal(long.MaxValue, row["CHARACTER_OCTET_LENGTH"]);
            if (name == "ProcedureParameters") { Assert.Equal(DBNull.Value, row["PARAMETER_MODE"]); Assert.Equal(DBNull.Value, row["PARAMETER_DEFAULT"]); }
            else { Assert.Equal(0, row["INPUT_PARAMETER_COUNT"]); Assert.Equal(0, row["DEFAULT_PARAMETER_COUNT"]); Assert.Equal(DBNull.Value, row["IS_NULL_CALL"]); }
            first.Rows.Clear(); using var fresh = await connection.GetSchemaAsync(name, token); Assert.Single(fresh.Rows.Cast<DataRow>());
            using var noParameters = connection.GetSchema("ProcedureParameters"); Assert.Empty(noParameters.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            var values = Values(name); var parameter = name == "ProcedureParameters";
            values[parameter ? 14 : 10] = "-2"; values[parameter ? 11 : 7] = "9223372036854775807";
            if (parameter) { values[6] = null; values[21] = null; } else { values[18] = "0"; values[19] = "0"; values[23] = null; }
            for (var i = 0; i < 2; i++) { await ReadExtended(stream, token); await stream.WriteAsync(Result(name, [values]), token); }
            await ReadExtended(stream, token); await stream.WriteAsync(Result("ProcedureParameters", []), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task InterruptedRoutineRetrievalRetiresWithoutReplay(string reason)
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
        await connection.OpenAsync(guard.Token); var pending = connection.GetSchemaAsync("ProcedureParameters", ["user"], caller.Token);
        await received.Task.WaitAsync(guard.Token); Assert.Throws<InvalidOperationException>(() => connection.GetSchema("Functions"));
        if (reason == "caller") caller.Cancel(); else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("Functions")]
    [InlineData("Procedures")]
    [InlineData("ProcedureParameters")]
    public async Task OversizedRoutineResultsRetireWithoutTruncation(string name)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync(name, token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            await stream.WriteAsync(Result(name, Enumerable.Range(0, 1025).Select(i =>
            { var values = Values(name); values[0] = "object" + i.ToString(CultureInfo.InvariantCulture); return values; }).ToArray()), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        });
    }

    internal static (string Name, Type Type)[] Fields(string name) => name == "ProcedureParameters" ?
        [("ROUTINE_CATALOG", typeof(string)), ("ROUTINE_SCHEMA", typeof(string)), ("ROUTINE_NAME", typeof(string)), ("SPECIFIC_NAME", typeof(string)),
            ("ORDINAL_POSITION", typeof(int)), ("PARAMETER_NAME", typeof(string)), ("PARAMETER_MODE", typeof(string)),
            ("IS_VARIADIC", typeof(bool)), ("IS_TABLE_COLUMN", typeof(bool)), ("DATA_TYPE", typeof(string)),
            ("CHARACTER_MAXIMUM_LENGTH", typeof(long)), ("CHARACTER_OCTET_LENGTH", typeof(long)), ("NUMERIC_PRECISION", typeof(int)),
            ("NUMERIC_PRECISION_RADIX", typeof(int)), ("NUMERIC_SCALE", typeof(int)), ("DATETIME_PRECISION", typeof(int)),
            ("INTERVAL_TYPE", typeof(string)), ("INTERVAL_PRECISION", typeof(int)), ("UDT_CATALOG", typeof(string)),
            ("UDT_SCHEMA", typeof(string)), ("UDT_NAME", typeof(string)), ("PARAMETER_DEFAULT", typeof(string))] :
        [("ROUTINE_CATALOG", typeof(string)), ("ROUTINE_SCHEMA", typeof(string)), ("ROUTINE_NAME", typeof(string)), ("SPECIFIC_NAME", typeof(string)),
            ("ROUTINE_TYPE", typeof(string)), ("DATA_TYPE", typeof(string)), ("CHARACTER_MAXIMUM_LENGTH", typeof(long)),
            ("CHARACTER_OCTET_LENGTH", typeof(long)), ("NUMERIC_PRECISION", typeof(int)), ("NUMERIC_PRECISION_RADIX", typeof(int)),
            ("NUMERIC_SCALE", typeof(int)), ("DATETIME_PRECISION", typeof(int)), ("INTERVAL_TYPE", typeof(string)), ("INTERVAL_PRECISION", typeof(int)),
            ("UDT_CATALOG", typeof(string)), ("UDT_SCHEMA", typeof(string)), ("UDT_NAME", typeof(string)), ("RETURNS_SET", typeof(bool)),
            ("INPUT_PARAMETER_COUNT", typeof(int)), ("DEFAULT_PARAMETER_COUNT", typeof(int)), ("EXTERNAL_LANGUAGE", typeof(string)),
            ("IS_DETERMINISTIC", typeof(bool)), ("SECURITY_TYPE", typeof(string)), ("IS_NULL_CALL", typeof(bool))];
    internal static string?[] Values(string name) => name == "ProcedureParameters" ?
        ["db", "MiXeD汉%_'", "routine", "routine_1", "1", null, "IN", "YES", "NO", "ARRAY", null, null, null, null, "-2", null, null, null,
            "db", "pg_catalog", "_int4", "ARRAY[1, 2]"] :
        ["db", "MiXeD汉%_'", "routine", "routine_1", name == "Functions" ? "FUNCTION" : "PROCEDURE", name == "Functions" ? "numeric" : null,
            null, null, null, null, null, null, null, null, name == "Functions" ? "db" : null,
            name == "Functions" ? "pg_catalog" : null, name == "Functions" ? "numeric" : null,
            "NO", "2", "1", "SQL", "NO", "INVOKER", name == "Functions" ? "YES" : null];
    internal static byte[] Result(string name, string?[][]? rows = null, string? fault = null) => Join(Frame('1'), Frame('2'),
        Frame('T', Join(Count(Fields(name).Length), Join(Fields(name).Select((field, i) => Join(
            Encoding.UTF8.GetBytes((i == 0 && fault == "alias" ? "private-payload" : field.Name) + '\0'), new byte[6],
            Int32(i == 0 && fault == "oid" ? 987654 : field.Type == typeof(int) ? -1 : field.Type == typeof(long) ? 9003 : 9004),
            Count(i == 0 && fault == "size" ? 4 : field.Type == typeof(int) ? 4 : field.Type == typeof(long) ? 8 : -1), Int32(-1), Count(0))).ToArray()))),
        Join((rows ?? [Values(name)]).Select(values => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) :
            Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())))).ToArray()), Complete(), Ready());
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
}
