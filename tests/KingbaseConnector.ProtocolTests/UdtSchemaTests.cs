using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Tests;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class UdtSchemaTests
{
    [Theory]
    [InlineData("UserDefinedTypes", false)]
    [InlineData("UserDefinedTypes", true)]
    [InlineData("TypeAttributes", false)]
    [InlineData("TypeAttributes", true)]
    [InlineData("EnumLabels", false)]
    [InlineData("EnumLabels", true)]
    [InlineData("DomainConstraints", false)]
    [InlineData("DomainConstraints", true)]
    public async Task UdtFactsUseFixedShapesAndPreserveEnumEmptyLabels(string name, bool async)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var table = async ? await connection.GetSchemaAsync(name.ToLowerInvariant(), ["db", "MiXeD汉%_'", "type"], token) :
                connection.GetSchema(name, ["db", "MiXeD汉%_'", "type"]);
            Assert.Equal(name, table.TableName); Assert.Equal(CultureInfo.InvariantCulture, table.Locale);
            Assert.Equal(UdtSchemaFixtures.Fields(name), table.Columns.Cast<DataColumn>().Select(column => (column.ColumnName, column.DataType)));
            var row = Assert.Single(table.Rows.Cast<DataRow>());
            foreach (var (field, i) in UdtSchemaFixtures.Fields(name).Select((field, i) => (field, i)))
            {
                var text = UdtSchemaFixtures.Values(name)[i];
                object expected = text is null ? DBNull.Value : field.Type == typeof(int) ? int.Parse(text, CultureInfo.InvariantCulture) :
                    field.Type == typeof(long) ? long.Parse(text, CultureInfo.InvariantCulture) : field.Type == typeof(bool) ? text == "YES" : text;
                Assert.Equal(expected, row[i]);
            }
        }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(new[] { "db", "MiXeD汉%_'", "type" }, ObjectSchemaTests.Parameters(request[1]));
            Assert.DoesNotContain("MiXeD", sql); Assert.Contains("pg_catalog.pg_type", sql);
            Assert.Contains("NOT pg_catalog.pg_is_other_temp_schema(t.typnamespace)", sql);
            await stream.WriteAsync(Result(name), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Fact]
    public void UdtCatalogQueriesUseSystemOperatorsAndTrustedIdentities()
    {
        using var connection = new KingbaseConnection();
        foreach (var name in UdtSchemaFixtures.Names)
        {
            using var command = KingbaseObjectSchema.Command(KingbaseObjectSchema.Find(name)!, null, connection, null);
            var sql = command.CommandText;
            Assert.DoesNotContain("CASE t.typtype", sql); Assert.DoesNotContain(" IN (", sql); Assert.DoesNotContain(" = ", sql);
            Assert.Contains("pg_catalog.has_type_privilege(t.oid,'USAGE')", sql);
            if (name == "EnumLabels") Assert.Contains("ORDER BY e.enumsortorder", sql);
            if (name == "TypeAttributes") Assert.Contains("NOT a.attisdropped", sql);
            if (name == "UserDefinedTypes") Assert.Contains("c.relkind OPERATOR(pg_catalog.=) 'c'", sql);
            if (name == "DomainConstraints") Assert.Contains("CASE WHEN pg_catalog.pg_has_role(t.typowner,'USAGE') THEN pg_catalog.pg_get_constraintdef", sql);
        }
    }

    [Fact]
    public async Task UdtDirectoryRestrictionsAndValidationStayLocal()
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var directory = connection.GetSchema(); Assert.Equal(29, directory.Rows.Count);
            using var restrictions = connection.GetSchema("Restrictions"); Assert.Equal(87, restrictions.Rows.Count);
            foreach (var name in UdtSchemaFixtures.Names)
            {
                var count = name == "UserDefinedTypes" ? 3 : 4;
                var row = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == name);
                Assert.Equal(count, row[1]); Assert.Equal(count, row[2]);
                var names = new[] { "Catalog", "Schema", "Type", name == "EnumLabels" ? "Label" : name == "TypeAttributes" ? "Attribute" : "Constraint" }.Take(count);
                var rows = restrictions.Rows.Cast<DataRow>().Where(row => (string)row[0] == name).ToArray();
                Assert.Equal(names, rows.Select(row => (string)row[1])); Assert.Equal(Enumerable.Range(1, count), rows.Select(row => (int)row[3]));
                Assert.Throws<ArgumentException>(() => connection.GetSchema(name, new string?[count + 1]));
                await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync(name, new string?[count + 1], token));
            }
        });
    }

    public static IEnumerable<object[]> Filters()
    {
        foreach (var name in UdtSchemaFixtures.Names)
            foreach (var kind in new[] { "omitted", "null", "empty", "sparse", "literal" }) yield return [name, kind];
    }
    [Theory]
    [MemberData(nameof(Filters))]
    public async Task UdtRestrictionsPreserveExactNullEmptyUnicodeAndLiteralSemantics(string name, string kind)
    {
        var count = name == "UserDefinedTypes" ? 3 : 4;
        string?[]? filters = kind switch { "omitted" => null, "null" => new string?[count], "empty" => Enumerable.Repeat<string?>("", count).ToArray(),
            "sparse" => [null, "汉😀_%' OR 1=1 --"], _ => Enumerable.Repeat<string?>("汉😀_%' OR 1=1 --", count).ToArray() };
        await StandardSchemaTests.Run(async (connection, token) =>
        { using var empty = await connection.GetSchemaAsync(name, filters, token); Assert.Empty(empty.Rows.Cast<DataRow>()); }, async (stream, token) =>
        {
            var request = await ReadExtended(stream, token); var sql = Encoding.UTF8.GetString(request[0]);
            Assert.Equal(filters?.Where(value => value is not null).ToArray() ?? [], ObjectSchemaTests.Parameters(request[1]));
            Assert.Contains("COLLATE pg_catalog.\"C\"", sql); Assert.DoesNotContain(" LIKE ", sql); Assert.DoesNotContain("OR 1=1", sql);
            if (name is "TypeAttributes" or "EnumLabels") Assert.Contains("udt_name::pg_catalog.text COLLATE pg_catalog.\"C\", ordinal_position\0", sql);
            await stream.WriteAsync(Result(name, []), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    public static IEnumerable<object[]> BadFacts()
    {
        foreach (var name in UdtSchemaFixtures.Names)
            foreach (var fault in new[] { "name", "empty-name", "duplicate", "alias", "oid", "size", "permission" }) yield return [name, fault];
        foreach (var name in new[] { "TypeAttributes", "EnumLabels" })
            foreach (var fault in new[] { "zero", "overflow" }) yield return [name, fault];
        foreach (var name in new[] { "UserDefinedTypes", "TypeAttributes", "DomainConstraints" }) yield return [name, "flag"];
        foreach (var fault in new[] { "null-label", "nul-label" }) yield return ["EnumLabels", fault];
        foreach (var name in new[] { "UserDefinedTypes", "TypeAttributes" }) yield return [name, "length"];
    }
    [Theory]
    [MemberData(nameof(BadFacts))]
    public async Task MalformedUdtFactsAndDeniedAccessRecoverWithRedactedErrors(string name, string fault)
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
            await ReadExtended(stream, token); var values = UdtSchemaFixtures.Values(name);
            if (fault == "name") values[2] = null;
            if (fault == "empty-name") values[2] = "";
            if (fault == "zero") values[3] = "0";
            if (fault == "overflow") values[3] = "2147483648";
            if (fault == "flag") values[name == "UserDefinedTypes" ? 10 : name == "TypeAttributes" ? 6 : 8] = "private-payload";
            if (fault == "null-label") values[4] = null;
            if (fault == "nul-label") values[4] = "private-payload\0";
            if (fault == "length") values[name == "UserDefinedTypes" ? 11 : 8] = "-1";
            await stream.WriteAsync(fault == "permission" ? Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()) :
                Result(name, fault == "duplicate" ? [values, values] : [values], fault), token);
            await ReadExtended(stream, token); await stream.WriteAsync(Result(name), token); Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("UserDefinedTypes")]
    [InlineData("TypeAttributes")]
    [InlineData("EnumLabels")]
    [InlineData("DomainConstraints")]
    public async Task IdentitySlotsAndFreshOptionalFactsRemainIndependent(string name)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            using var first = connection.GetSchema(name); Assert.Equal(2, first.Rows.Count);
            if (name == "TypeAttributes") Assert.Equal(new[] { 2, 12 }, first.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
            if (name == "EnumLabels") Assert.Equal(new[] { "", "汉😀_%'" }, first.Rows.Cast<DataRow>().Select(row => (string)row["ENUM_LABEL"]));
            if (name == "UserDefinedTypes") Assert.Equal(DBNull.Value, first.Rows[1]["DOMAIN_DEFAULT"]);
            if (name == "DomainConstraints") Assert.Equal(DBNull.Value, first.Rows[1]["DEFINITION"]);
            first.Rows.Clear(); using var fresh = await connection.GetSchemaAsync(name, token); Assert.Equal(2, fresh.Rows.Count);
        }, async (stream, token) =>
        {
            var first = UdtSchemaFixtures.Values(name); var second = UdtSchemaFixtures.Values(name);
            if (name == "TypeAttributes") { second[3] = "12"; second[4] = "last"; }
            if (name == "EnumLabels") { second[3] = "2"; second[4] = "汉😀_%'"; }
            if (name == "UserDefinedTypes") { second[1] = "another"; second[9] = null; }
            if (name == "DomainConstraints") { second[3] = "other"; second[7] = null; second[11] = null; }
            for (var i = 0; i < 2; i++) { await ReadExtended(stream, token); await stream.WriteAsync(Result(name, [first, second]), token); }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task InterruptedUdtRetrievalRetiresWithoutReplay(string reason)
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
        await connection.OpenAsync(guard.Token); var pending = connection.GetSchemaAsync("EnumLabels", ["user"], caller.Token);
        await received.Task.WaitAsync(guard.Token); Assert.Throws<InvalidOperationException>(() => connection.GetSchema("UserDefinedTypes"));
        if (reason == "caller") caller.Cancel(); else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("UserDefinedTypes")]
    [InlineData("TypeAttributes")]
    [InlineData("DomainConstraints")]
    [InlineData("EnumLabels")]
    public async Task OversizedUdtResultsRetireWithoutTruncation(string name)
    {
        await StandardSchemaTests.Run(async (connection, token) =>
        {
            await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync(name, token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            await stream.WriteAsync(Result(name, Enumerable.Range(0, 1025).Select(i =>
            { var values = UdtSchemaFixtures.Values(name); values[0] = "object" + i.ToString(CultureInfo.InvariantCulture); return values; }).ToArray()), token);
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        });
    }

    internal static byte[] Result(string name, string?[][]? rows = null, string? fault = null) => Join(Frame('1'), Frame('2'),
        Frame('T', Join(Count(UdtSchemaFixtures.Fields(name).Length), Join(UdtSchemaFixtures.Fields(name).Select((field, i) => Join(
            Encoding.UTF8.GetBytes((i == 0 && fault == "alias" ? "private-payload" : field.Name) + '\0'), new byte[6],
            Int32(i == 0 && fault == "oid" ? 987654 : field.Type == typeof(int) ? -1 : field.Type == typeof(long) ? 9003 : 9004),
            Count(i == 0 && fault == "size" ? 4 : field.Type == typeof(int) ? 4 : field.Type == typeof(long) ? 8 : -1), Int32(-1), Count(0))).ToArray()))),
        Join((rows ?? [UdtSchemaFixtures.Values(name)]).Select(values => Frame('D', Join(Count(values.Length), Join(values.Select(value => value is null ? Int32(-1) :
            Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())))).ToArray()), Complete(), Ready());
    static byte[] Count(int value) => [(byte)(value >> 8), (byte)value];
}
