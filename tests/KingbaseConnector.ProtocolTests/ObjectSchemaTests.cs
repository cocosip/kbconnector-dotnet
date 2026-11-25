using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class ObjectSchemaTests
{
    [Fact]
    public void SchemaDefinitionQueriesActualSchemataView()
    {
        using var connection = new KingbaseConnection();
        using var command = KingbaseObjectSchema.Command(KingbaseObjectSchema.Find("Schemas")!, null, connection, null);
        Assert.Contains("FROM information_schema.schemata ORDER BY", command.CommandText);
    }

    [Theory]
    [InlineData("Schemas")]
    [InlineData("Tables")]
    [InlineData("Views")]
    [InlineData("Columns")]
    public void RestrictionOperatorsAreSystemQualified(string name)
    {
        using var connection = new KingbaseConnection();
        using var command = KingbaseObjectSchema.Command(KingbaseObjectSchema.Find(name)!, ["db"], connection, null);
        Assert.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) @r0", command.CommandText);
    }

    [Theory]
    [InlineData("unknown-oid")]
    [InlineData("wire-size")]
    [InlineData("same-clr")]
    [InlineData("swapped-labels")]
    public async Task MalformedHeadersRejectBeforeReaderConstructionAndRecover(string kind)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await ReadStartupAsync(stream, guard.Token);
            await stream.WriteAsync(Join(StartupResponse(), Status("standard_conforming_strings", "on")), guard.Token);
            await ReadQueryAsync(stream, guard.Token); await stream.WriteAsync(Scalar("KingbaseES V009R001C001B0001"), guard.Token);
            await ReadQueryAsync(stream, guard.Token);
            var layouts = new (string Name, short Size)[] { ("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1), ("varchar", -1) };
            await stream.WriteAsync(Catalog(layouts.Select((layout, i) => new PgTypeDescription(layout.Name == "int4" ? uint.MaxValue : (uint)(9000 + i), 90,
                layout.Name, layout.Size, 'b', 'U', 0, 0, 0, -1))), guard.Token);
            await ReadExtended(stream, guard.Token);
            var fields = Fields("Schemas");
            if (kind == "swapped-labels") (fields[0], fields[1]) = (fields[1], fields[0]);
            var description = Frame('T', Join(Count(3), Join(fields.Select(field => Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6],
                Int32(kind == "unknown-oid" ? 9999 : kind == "same-clr" ? 9006 : 9004), Count(kind == "wire-size" ? 4 : -1), Int32(-1), Count(0))).ToArray())));
            await stream.WriteAsync(Join(Frame('1'), Frame('2'), description, ObjectRow(Values("Schemas")), Complete(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Result("Schemas"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        });
        try
        {
            await connection.OpenAsync(guard.Token);
            var error = await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync("Schemas", guard.Token));
            Assert.Null(error.InnerException); Assert.DoesNotContain("9999", error.ToString());
            Assert.Equal(ConnectionState.Open, connection.State);
            using var recovered = await connection.GetSchemaAsync("Schemas", guard.Token); Assert.Single(recovered.Rows.Cast<DataRow>());
        }
        finally { connection.Close(); }
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("Schemas", 2, 2)]
    [InlineData("Tables", 4, 3)]
    [InlineData("Views", 3, 3)]
    [InlineData("Columns", 4, 4)]
    public async Task DirectoryRestrictionsAndInvalidArgumentsAreConsistent(string name, int count, int parts)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var directory = connection.GetSchema();
            var entry = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == name);
            Assert.Equal(count, entry[1]); Assert.Equal(parts, entry[2]);
            using var restrictions = await connection.GetSchemaAsync("Restrictions", token);
            var descriptors = restrictions.Rows.Cast<DataRow>().Where(row => (string)row[0] == name).ToArray();
            Assert.Equal(count, descriptors.Length);
            Assert.Equal(Enumerable.Range(1, count), descriptors.Select(row => (int)row[3]));
            Assert.Equal(name == "Schemas" ? new[] { "Catalog", "Schema" } : name == "Tables" ? new[] { "Catalog", "Schema", "Table", "TableType" } :
                name == "Views" ? new[] { "Catalog", "Schema", "Table" } : new[] { "Catalog", "Schema", "Table", "Column" }, descriptors.Select(row => (string)row[1]));
            Assert.All(descriptors, row => Assert.Equal(DBNull.Value, row[2]));
            var excess = Enumerable.Repeat<string?>("private-payload", count + 1).ToArray();
            var error = Assert.Throws<ArgumentException>(() => connection.GetSchema(name, excess));
            Assert.DoesNotContain("private-payload", error.ToString());
            await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync(name, excess, token));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync(name, [], canceled.Token));
            Assert.Equal(canceled.Token, cancellation.CancellationToken);
        });
    }

    [Theory]
    [InlineData("Schemas", false)]
    [InlineData("Schemas", true)]
    [InlineData("Tables", false)]
    [InlineData("Tables", true)]
    [InlineData("Views", false)]
    [InlineData("Views", true)]
    [InlineData("Columns", false)]
    [InlineData("Columns", true)]
    public async Task PublicObjectCollectionsUseBoundExactRestrictionsAndFixedShapes(string name, bool async)
    {
        var restrictions = name == "Schemas" ? new[] { "db", "MiXeD汉%_'\"" } : new[] { "db", "MiXeD汉%_'\"", "TaBlE" };
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = async ? await connection.GetSchemaAsync(name.ToLowerInvariant(), restrictions, token) : connection.GetSchema(name, restrictions);
            Assert.Equal(name, table.TableName); Assert.Equal(CultureInfo.InvariantCulture, table.Locale);
            Assert.Single(table.Rows.Cast<DataRow>());
            Assert.Equal(Fields(name), table.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType)));
            Assert.Equal("db", table.Rows[0][0]); Assert.Equal(restrictions[1], table.Rows[0][1]);
            if (name == "Columns")
            {
                Assert.Equal(7, table.Rows[0]["ORDINAL_POSITION"]);
                Assert.False((bool)table.Rows[0]["IS_NULLABLE"]);
                Assert.Equal(2147483648L, table.Rows[0]["CHARACTER_OCTET_LENGTH"]);
                Assert.Equal(-3, table.Rows[0]["NUMERIC_SCALE"]);
                Assert.Equal(DBNull.Value, table.Rows[0]["COLUMN_DEFAULT"]);
            }
            if (name == "Views")
            {
                Assert.Equal(DBNull.Value, table.Rows[0]["VIEW_DEFINITION"]);
                Assert.True((bool)table.Rows[0]["IS_UPDATABLE"]);
            }
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token);
            var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Contains("FROM information_schema." + (name == "Schemas" ? "schemata" : name.ToLowerInvariant()), sql);
            Assert.Contains("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $1", sql);
            Assert.DoesNotContain(restrictions[1], sql);
            Assert.Contains(restrictions[1], Encoding.UTF8.GetString(request[1]));
            await stream.WriteAsync(Result(name), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("nulls")]
    [InlineData("empty")]
    [InlineData("literal")]
    [InlineData("sparse")]
    public async Task RestrictionsPreserveNullEmptyAndLiteralValues(string kind)
    {
        string?[]? restrictions = kind switch
        {
            "omitted" => null,
            "nulls" => [null, null, null, null],
            "empty" => ["", "", "", ""],
            "sparse" => [null, "汉😀", null, "BASE TABLE"],
            _ => ["db", "MiXeD_%;'\" OR 1=1 --", "TaBlE", "VIEW"]
        };
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = await connection.GetSchemaAsync("Tables", restrictions, token);
            Assert.Empty(table.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token);
            var sql = Encoding.UTF8.GetString(request[0]);
            var expected = restrictions?.Where(value => value is not null).ToArray() ?? [];
            Assert.Equal(expected, Parameters(request[1]));
            Assert.Equal(expected.Length, sql.Split("COLLATE pg_catalog.\"C\" OPERATOR(pg_catalog.=) $", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain(" LIKE ", sql); Assert.DoesNotContain("OR 1=1", sql);
            Assert.Contains("ORDER BY table_catalog::pg_catalog.text COLLATE pg_catalog.\"C\", table_schema", sql);
            await stream.WriteAsync(Result("Tables", []), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Fact]
    public async Task RestrictionsAreCapturedBeforeAwaitAndTablesAreFresh()
    {
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string?[] values = [null, "original"];
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            var pending = connection.GetSchemaAsync("Schemas", values, token);
            await captured.Task.WaitAsync(token); values[1] = "changed"; release.SetResult();
            using var first = await pending; first.Rows.Clear();
            using var fresh = await connection.GetSchemaAsync("Schemas", values, token);
            Assert.Single(fresh.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); Assert.Equal(new[] { "original" }, Parameters(request[1]));
            captured.SetResult(); await release.Task.WaitAsync(token); await stream.WriteAsync(Result("Schemas"), token);
            request = await ReadExtended(stream, token); Assert.Equal(new[] { "changed" }, Parameters(request[1]));
            await stream.WriteAsync(Result("Schemas"), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("null-id", "Schemas")]
    [InlineData("empty-id", "Tables")]
    [InlineData("nul-id", "Tables")]
    [InlineData("duplicate", "Schemas")]
    [InlineData("negative-length", "Columns")]
    [InlineData("zero-ordinal", "Columns")]
    [InlineData("bad-number", "Columns")]
    [InlineData("bad-flag", "Views")]
    [InlineData("null-flag", "Columns")]
    [InlineData("shape", "Tables")]
    [InlineData("wire-type", "Columns")]
    [InlineData("permission", "Tables")]
    public async Task SynchronizedInvalidMetadataAndPermissionErrorsAreSafeAndRecoverable(string kind, string name)
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
                Assert.DoesNotContain("private-payload", error.ToString()); Assert.Null(error.InnerException);
            }
            Assert.Equal(ConnectionState.Open, connection.State);
            using var recovered = await connection.GetSchemaAsync(name, token); Assert.Single(recovered.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            var values = Values(name);
            switch (kind)
            {
                case "null-id": values[0] = null; break;
                case "empty-id": values[2] = ""; break;
                case "nul-id": values[2] = "private-payload\0"; break;
                case "negative-length": values[8] = "-1"; break;
                case "zero-ordinal": values[4] = "0"; break;
                case "bad-number": values[4] = "private-payload"; break;
                case "bad-flag": values[5] = "private-payload"; break;
                case "null-flag": values[6] = null; break;
            }
            var response = kind switch
            {
                "permission" => Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()),
                "shape" => Result("Schemas"),
                "wire-type" => Join(Frame('1'), Frame('2'), Description(values.Length, 9004, -1), ObjectRow(values), Complete(), Ready()),
                "duplicate" => Result(name, [values, values]),
                _ => Result(name, [values])
            };
            await stream.WriteAsync(response, token);
            await ReadExtended(stream, token); await stream.WriteAsync(Result(name), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task InterruptedCatalogQueriesRetireWithoutReplay(string reason)
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
        await connection.OpenAsync(guard.Token); var pending = connection.GetSchemaAsync("Columns", [null, "public"], caller.Token);
        await received.Task.WaitAsync(guard.Token);
        Assert.Throws<InvalidOperationException>(() => connection.GetSchema("Schemas"));
        if (reason == "caller") caller.Cancel(); else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task OversizedCatalogRetiresWithoutTruncating()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync("Schemas", token));
            Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            await stream.WriteAsync(Result("Schemas", Enumerable.Range(0, 1025).Select(i => new string?[] { "db", "schema" + i, "owner" }).ToArray()), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        });
    }

    internal static string?[] Parameters(byte[] bind)
    {
        var offset = 5;
        while (bind[offset++] != 0) { }
        while (bind[offset++] != 0) { }
        var formats = BinaryPrimitives.ReadUInt16BigEndian(bind.AsSpan(offset)); offset += 2 + 2 * formats;
        var count = BinaryPrimitives.ReadUInt16BigEndian(bind.AsSpan(offset)); offset += 2;
        var values = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bind.AsSpan(offset)); offset += 4;
            if (length == -1) continue;
            values[i] = Encoding.UTF8.GetString(bind, offset, length); offset += length;
        }
        return values;
    }

    internal static (string Name, Type Type)[] Fields(string name) => name switch
    {
        "Schemas" => [("CATALOG_NAME", typeof(string)), ("SCHEMA_NAME", typeof(string)), ("SCHEMA_OWNER", typeof(string))],
        "Tables" => [("TABLE_CATALOG", typeof(string)), ("TABLE_SCHEMA", typeof(string)), ("TABLE_NAME", typeof(string)), ("TABLE_TYPE", typeof(string))],
        "Views" => [("TABLE_CATALOG", typeof(string)), ("TABLE_SCHEMA", typeof(string)), ("TABLE_NAME", typeof(string)), ("VIEW_DEFINITION", typeof(string)), ("CHECK_OPTION", typeof(string)), ("IS_UPDATABLE", typeof(bool))],
        _ => [("TABLE_CATALOG", typeof(string)), ("TABLE_SCHEMA", typeof(string)), ("TABLE_NAME", typeof(string)), ("COLUMN_NAME", typeof(string)),
            ("ORDINAL_POSITION", typeof(int)), ("COLUMN_DEFAULT", typeof(string)), ("IS_NULLABLE", typeof(bool)), ("DATA_TYPE", typeof(string)),
            ("CHARACTER_MAXIMUM_LENGTH", typeof(long)), ("CHARACTER_OCTET_LENGTH", typeof(long)), ("NUMERIC_PRECISION", typeof(int)),
            ("NUMERIC_PRECISION_RADIX", typeof(int)), ("NUMERIC_SCALE", typeof(int)), ("DATETIME_PRECISION", typeof(int)),
            ("INTERVAL_TYPE", typeof(string)), ("INTERVAL_PRECISION", typeof(int)), ("UDT_CATALOG", typeof(string)), ("UDT_SCHEMA", typeof(string)),
            ("UDT_NAME", typeof(string)), ("DOMAIN_CATALOG", typeof(string)), ("DOMAIN_SCHEMA", typeof(string)), ("DOMAIN_NAME", typeof(string)),
            ("COLLATION_CATALOG", typeof(string)), ("COLLATION_SCHEMA", typeof(string)), ("COLLATION_NAME", typeof(string)),
            ("IS_IDENTITY", typeof(bool)), ("IDENTITY_GENERATION", typeof(string)), ("IDENTITY_START", typeof(long)), ("IDENTITY_INCREMENT", typeof(long)),
            ("IDENTITY_MAXIMUM", typeof(long)), ("IDENTITY_MINIMUM", typeof(long)), ("IDENTITY_CYCLE", typeof(bool)),
            ("IS_GENERATED", typeof(string)), ("GENERATION_EXPRESSION", typeof(string))]
    };

    internal static string?[] Values(string name) => name switch
    {
        "Schemas" => ["db", "MiXeD汉%_'\"", "owner"],
        "Tables" => ["db", "MiXeD汉%_'\"", "TaBlE", "BASE TABLE"],
        "Views" => ["db", "MiXeD汉%_'\"", "TaBlE", null, "NONE", "YES"],
        _ => ["db", "MiXeD汉%_'\"", "TaBlE", "Column", "7", null, "NO", "numeric", null, "2147483648", "12", "10", "-3", null, null, null,
            "db", "pg_catalog", "numeric", null, null, null, null, null, null, "NO", null, null, null, null, null, null, "NEVER", null]
    };

    internal static byte[] Result(string name, string?[][]? rows = null) => Join(Frame('1'), Frame('2'), ObjectDescription(name),
        Join((rows ?? [Values(name)]).Select(ObjectRow).ToArray()), Complete(), Ready());

    static byte[] ObjectDescription(string name) => Frame('T', Join(Count(Fields(name).Length), Join(Fields(name).Select((field, i) =>
    {
        var (oid, size) = field.Type == typeof(int) ? (uint.MaxValue, 4) : field.Type == typeof(long) ? (9003U, 8) : (9004U, -1);
        return Join(Encoding.UTF8.GetBytes(field.Name + '\0'), new byte[6], Int32(unchecked((int)oid)), Count(size), Int32(-1), Count(0));
    }).ToArray())));

    static byte[] ObjectRow(string?[] values) => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) :
        Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())));
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
}
