using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class DirectorySchemaTests
{
    [Theory]
    [InlineData("Databases", false)]
    [InlineData("Databases", true)]
    [InlineData("Users", false)]
    [InlineData("Users", true)]
    public async Task DirectoryFactsUseFixedShapesAndBoundNames(string name, bool async)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = async ? await connection.GetSchemaAsync(name.ToLowerInvariant(), ["MiXeD汉%_'"], token) :
                connection.GetSchema(name, ["MiXeD汉%_'"]);
            Assert.Equal(name, table.TableName); Assert.Equal(CultureInfo.InvariantCulture, table.Locale);
            Assert.Equal(Fields(name), table.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType)));
            var row = Assert.Single(table.Rows.Cast<DataRow>());
            Assert.Equal("MiXeD汉%_'", row[0]); Assert.Equal(-1, row["CONNECTION_LIMIT"]);
            if (name == "Databases")
            {
                Assert.True((bool)row["IS_TEMPLATE"]); Assert.False((bool)row["ALLOWS_CONNECTIONS"]);
                Assert.True((bool)row["HAS_CONNECT_PRIVILEGE"]); Assert.Equal("UTF8", row["ENCODING"]);
            }
            else
            {
                Assert.False((bool)row["IS_SUPERUSER"]); Assert.True((bool)row["INHERITS_PRIVILEGES"]);
                Assert.True((bool)row["CAN_CREATE_DATABASE"]); Assert.False((bool)row["CAN_CREATE_ROLE"]);
            }
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(new[] { "MiXeD汉%_'" }, ObjectSchemaTests.Parameters(request[1])); Assert.DoesNotContain("MiXeD", sql);
            Assert.Contains(name == "Databases" ? "pg_catalog.pg_database" : "pg_catalog.pg_roles", sql);
            Assert.DoesNotContain("pg_authid", sql); Assert.DoesNotContain("rolpassword", sql); Assert.DoesNotContain("rolconfig", sql);
            await stream.WriteAsync(Result(name), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Schemata", "Schemas", 2, 2)]
    [InlineData("PrimaryKey", "PrimaryKeys", 4, 5)]
    public async Task LegacyAliasesAreDiscoverableWithCanonicalRestrictions(string alias, string canonical, int count, int parts)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var directory = await connection.GetSchemaAsync(token);
            var entry = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == alias);
            Assert.Equal(count, entry[1]); Assert.Equal(parts, entry[2]);
            using var restrictions = connection.GetSchema("Restrictions");
            var aliases = restrictions.Rows.Cast<DataRow>().Where(row => (string)row[0] == alias).ToArray();
            var originals = restrictions.Rows.Cast<DataRow>().Where(row => (string)row[0] == canonical).ToArray();
            Assert.Equal(count, aliases.Length);
            Assert.Equal(originals.Select(row => row.ItemArray.Skip(1)), aliases.Select(row => row.ItemArray.Skip(1)));
            foreach (var name in new[] { "Databases", "Users" })
            {
                var item = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == name);
                Assert.Equal(1, item[1]); Assert.Equal(1, item[2]);
                var filter = Assert.Single(restrictions.Rows.Cast<DataRow>(), row => (string)row[0] == name);
                Assert.Equal(name == "Databases" ? "Database" : "User", filter[1]); Assert.Equal(1, filter[3]);
                Assert.Throws<ArgumentException>(() => connection.GetSchema(name, [null, null]));
                await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync(name, [null, null], token));
            }
        });
    }

    [Theory]
    [InlineData("Databases", "-2")]
    [InlineData("Databases", "-1")]
    [InlineData("Databases", "0")]
    [InlineData("Databases", "2147483647")]
    [InlineData("Users", "-1")]
    [InlineData("Users", "0")]
    [InlineData("Users", "2147483647")]
    public async Task ConnectionLimitsPreserveSignedSentinelsAndFreshTables(string name, string limit)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var first = connection.GetSchema(name); Assert.Equal(int.Parse(limit, CultureInfo.InvariantCulture), first.Rows[0]["CONNECTION_LIMIT"]);
            first.Rows.Clear(); using var fresh = await connection.GetSchemaAsync(name, token); Assert.Single(fresh.Rows.Cast<DataRow>());
            Assert.Equal(int.Parse(limit, CultureInfo.InvariantCulture), fresh.Rows[0]["CONNECTION_LIMIT"]);
        }, async (stream, token) =>
        {
            var values = Values(name); values[7] = limit;
            for (var i = 0; i < 2; i++) { await ReadExtended(stream, token); await stream.WriteAsync(Result(name, [values]), token); }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Databases", "omitted")]
    [InlineData("Databases", "null")]
    [InlineData("Databases", "empty")]
    [InlineData("Databases", "literal")]
    [InlineData("Users", "omitted")]
    [InlineData("Users", "null")]
    [InlineData("Users", "empty")]
    [InlineData("Users", "literal")]
    public async Task RestrictionsKeepExactNullEmptyUnicodeAndLiteralSemantics(string name, string kind)
    {
        string?[]? filters = kind switch { "omitted" => null, "null" => [null], "empty" => [""], _ => ["汉😀_%' OR 1=1 --"] };
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var empty = await connection.GetSchemaAsync(name, filters, token); Assert.Empty(empty.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(filters?.Where(value => value is not null).ToArray() ?? [], ObjectSchemaTests.Parameters(request[1]));
            Assert.Contains("COLLATE pg_catalog.\"C\"", sql); Assert.DoesNotContain(" LIKE ", sql); Assert.DoesNotContain("OR 1=1", sql);
            Assert.DoesNotContain(" = ", sql); Assert.DoesNotContain("rolvaliduntil", sql); Assert.DoesNotContain("datacl", sql);
            if (name == "Users") Assert.Contains("WHERE r.rolcanlogin", sql);
            else
            {
                Assert.Contains("pg_catalog.has_database_privilege(d.oid,'CONNECT')", sql);
                Assert.DoesNotContain("WHERE d.datallowconn", sql); Assert.DoesNotContain("WHERE pg_catalog.has_database_privilege", sql);
            }
            await stream.WriteAsync(Result(name, []), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("Databases", "name")]
    [InlineData("Databases", "empty-name")]
    [InlineData("Databases", "required-text")]
    [InlineData("Databases", "flag")]
    [InlineData("Databases", "null-flag")]
    [InlineData("Databases", "null-limit")]
    [InlineData("Databases", "negative-limit")]
    [InlineData("Databases", "overflow-limit")]
    [InlineData("Databases", "duplicate")]
    [InlineData("Databases", "alias")]
    [InlineData("Databases", "oid")]
    [InlineData("Databases", "size")]
    [InlineData("Databases", "permission")]
    [InlineData("Users", "name")]
    [InlineData("Users", "empty-name")]
    [InlineData("Users", "flag")]
    [InlineData("Users", "null-flag")]
    [InlineData("Users", "null-limit")]
    [InlineData("Users", "negative-limit")]
    [InlineData("Users", "overflow-limit")]
    [InlineData("Users", "duplicate")]
    [InlineData("Users", "alias")]
    [InlineData("Users", "oid")]
    [InlineData("Users", "size")]
    [InlineData("Users", "permission")]
    public async Task InvalidDirectoryFactsAndDeniedAccessRecoverWithoutLeakingPayload(string name, string fault)
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
            await ReadExtended(stream, token); var values = Values(name);
            if (fault == "name") values[0] = null;
            if (fault == "empty-name") values[0] = "";
            if (fault == "required-text") values[1] = null;
            if (fault == "flag") values[name == "Databases" ? 5 : 1] = "private-payload";
            if (fault == "null-flag") values[name == "Databases" ? 5 : 1] = null;
            if (fault == "null-limit") values[7] = null;
            if (fault == "negative-limit") values[7] = name == "Databases" ? "-3" : "-2";
            if (fault == "overflow-limit") values[7] = "2147483648";
            await stream.WriteAsync(fault == "permission" ? Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()) :
                Result(name, fault == "duplicate" ? [values, values] : [values], fault), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Result(name), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task InterruptedDirectoryRetrievalRetiresWithoutReplay(string reason)
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
        await connection.OpenAsync(guard.Token); var pending = connection.GetSchemaAsync("Users", ["user"], caller.Token);
        await received.Task.WaitAsync(guard.Token); Assert.Throws<InvalidOperationException>(() => connection.GetSchema("Databases"));
        if (reason == "caller") caller.Cancel(); else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("Databases")]
    [InlineData("Users")]
    public async Task OversizedDirectoriesRetireWithoutTruncation(string name)
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

    [Fact]
    public void ProjectionQuotesCatalogFieldNamesThatAreSqlKeywords()
    {
        using var connection = new KingbaseConnection();
        using var command = KingbaseObjectSchema.Command(KingbaseObjectSchema.Find("Databases")!, null, connection, null);
        Assert.Contains("\"collation\"::pg_catalog.text AS \"COLLATION\"", command.CommandText);
    }

    internal static (string Name, Type Type)[] Fields(string name) => name == "Databases" ?
        [("DATABASE_NAME", typeof(string)), ("OWNER", typeof(string)), ("ENCODING", typeof(string)), ("COLLATION", typeof(string)), ("CTYPE", typeof(string)),
            ("IS_TEMPLATE", typeof(bool)), ("ALLOWS_CONNECTIONS", typeof(bool)), ("CONNECTION_LIMIT", typeof(int)), ("HAS_CONNECT_PRIVILEGE", typeof(bool))] :
        [("USER_NAME", typeof(string)), ("IS_SUPERUSER", typeof(bool)), ("INHERITS_PRIVILEGES", typeof(bool)), ("CAN_CREATE_ROLE", typeof(bool)),
            ("CAN_CREATE_DATABASE", typeof(bool)), ("CAN_REPLICATE", typeof(bool)), ("BYPASSES_ROW_SECURITY", typeof(bool)), ("CONNECTION_LIMIT", typeof(int))];
    internal static string?[] Values(string name) => name == "Databases" ?
        ["MiXeD汉%_'", "owner", "UTF8", "C", "C", "YES", "NO", "-1", "YES"] : ["MiXeD汉%_'", "NO", "YES", "NO", "YES", "NO", "NO", "-1"];
    internal static byte[] Result(string name, string?[][]? rows = null, string? fault = null) => Join(Frame('1'), Frame('2'),
        Frame('T', Join(Count(Fields(name).Length), Join(Fields(name).Select((field, i) => Join(
            Encoding.UTF8.GetBytes((i == 0 && fault == "alias" ? "private-payload" : field.Name) + '\0'), new byte[6],
            Int32(i == 0 && fault == "oid" ? 987654 : field.Type == typeof(int) ? -1 : 9004),
            Count(i == 0 && fault == "size" ? 4 : field.Type == typeof(int) ? 4 : -1), Int32(-1), Count(0))).ToArray()))),
        Join((rows ?? [Values(name)]).Select(values => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) :
            Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())))).ToArray()), Complete(), Ready());
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
}
