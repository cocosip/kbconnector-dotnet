using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using KingbaseConnector.Protocol;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class StandardSchemaTests
{
    internal const string KeywordsSql = "SELECT word::text FROM pg_catalog.pg_get_keywords() WHERE catcode = 'R' ORDER BY word";
    static readonly string[] Collections = ["MetaDataCollections", "Restrictions", "DataSourceInformation", "DataTypes", "ReservedWords", "Schemas", "Tables", "Views", "Columns"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardCollectionsExposeFixedShapesAndFreshTables(bool async)
    {
        await Run(async (connection, token) =>
        {
            DbConnection bcl = connection;
            using var collections = async ? await bcl.GetSchemaAsync(token) : bcl.GetSchema();
            Assert.Equal(Collections.Order(), collections.Rows.Cast<DataRow>().Select(row => (string)row["CollectionName"]).Order());
            Columns(collections, ("CollectionName", typeof(string)), ("NumberOfRestrictions", typeof(int)), ("NumberOfIdentifierParts", typeof(int)));
            Assert.All(collections.Rows.Cast<DataRow>().Take(5), row => { Assert.Equal(0, row["NumberOfRestrictions"]); Assert.Equal(0, row["NumberOfIdentifierParts"]); });
            collections.Rows.Clear();
            using var fresh = async ? await bcl.GetSchemaAsync(null!, token) : bcl.GetSchema(null!);
            Assert.Equal(9, fresh.Rows.Count);
            using var restrictions = async ? await bcl.GetSchemaAsync("restrictions", [], token) : bcl.GetSchema("restrictions", []);
            Columns(restrictions, ("CollectionName", typeof(string)), ("RestrictionName", typeof(string)), ("RestrictionDefault", typeof(string)), ("RestrictionNumber", typeof(int)));
            Assert.Equal(13, restrictions.Rows.Count);
            using var source = async ? await bcl.GetSchemaAsync("DataSourceInformation", token) : bcl.GetSchema("DataSourceInformation");
            Assert.Equal(17, source.Columns.Count); Assert.Single(source.Rows.Cast<DataRow>());
            Assert.Equal("KingbaseES", source.Rows[0]["DataSourceProductName"]);
            Assert.Equal(connection.ServerVersion, source.Rows[0]["DataSourceProductVersion"]);
            Assert.Equal(128, source.Rows[0]["ParameterNameMaxLength"]);
            using var types = async ? await bcl.GetSchemaAsync("DataTypes", token) : bcl.GetSchema("DataTypes");
            var integer = Assert.Single(types.Rows.Cast<DataRow>(), row => (string)row["TypeName"] == "int4");
            Assert.Equal(typeof(uint), types.Columns["TypeOid"]!.DataType); Assert.Equal(uint.MaxValue, integer["TypeOid"]);
            Assert.Equal((int)KingbaseDbType.Integer, integer["ProviderDbType"]); Assert.Equal(typeof(int).FullName, integer["DataType"]);
            Assert.Equal(DBNull.Value, integer["ColumnSize"]);
            foreach (var name in new[] { "ReservedWords", "rEsErVeDwOrDs" })
            {
                using var words = async ? await bcl.GetSchemaAsync(name, [], token) : bcl.GetSchema(name, []);
                Columns(words, ("ReservedWord", typeof(string)));
                Assert.Equal(new[] { "select", "where" }, words.Rows.Cast<DataRow>().Select(row => (string)row[0]));
            }
        }, async (stream, token) =>
        {
            for (var i = 0; i < 2; i++)
            {
                var request = await ReadExtended(stream, token);
                Assert.Contains(KeywordsSql + '\0', Encoding.UTF8.GetString(request[0]));
                await stream.WriteAsync(Keywords("select", "where"), token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("MetaDataCollections")]
    [InlineData("Restrictions")]
    [InlineData("DataSourceInformation")]
    [InlineData("DataTypes")]
    [InlineData("ReservedWords")]
    [InlineData("Schemas")]
    [InlineData("Tables")]
    [InlineData("Views")]
    [InlineData("Columns")]
    public async Task ClosedDisposedAndPreCanceledCallsReject(string collection)
    {
        using var connection = new KingbaseConnection();
        Assert.Throws<InvalidOperationException>(() => connection.GetSchema(collection));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetSchemaAsync(collection));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync(collection, canceled.Token));
        connection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => connection.GetSchema(collection));
    }

    [Fact]
    public async Task LocalValidationIsRedactedAndDoesNotWriteAndTablesUseInvariantCulture()
    {
        await Run(async (connection, token) =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                foreach (var name in new[] { "", "private-payload", "Indexes", " DataTypes" })
                {
                    var error = Assert.Throws<ArgumentException>(() => connection.GetSchema(name));
                    Assert.DoesNotContain("private-payload", error.ToString()); Assert.Null(error.InnerException);
                }
                foreach (var name in Collections.Take(5))
                {
                    Assert.Throws<ArgumentException>(() => connection.GetSchema(name, [null]));
                    await Assert.ThrowsAsync<ArgumentException>(() => connection.GetSchemaAsync(name, ["private-payload"], token));
                    using var canceled = new CancellationTokenSource(); canceled.Cancel();
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync(name, canceled.Token));
                    Assert.Equal(canceled.Token, error.CancellationToken);
                }
                using var types = connection.GetSchema("DATATYPES"); Assert.Equal(CultureInfo.InvariantCulture, types.Locale);
                Assert.Equal(25, types.Columns.Count);
                Assert.Equal(typeof(long), types.Columns["ColumnSize"]!.DataType);
                Assert.Equal(typeof(short), types.Columns["MaximumScale"]!.DataType);
                Assert.Equal(typeof(bool), types.Columns["IsNullable"]!.DataType);
                var numeric = Assert.Single(types.Rows.Cast<DataRow>(), row => (string)row["TypeName"] == "numeric");
                Assert.Equal(typeof(decimal).FullName, numeric["DataType"]);
                Assert.Equal(typeof(KingbaseNumeric).FullName, numeric["ProviderSpecificDataType"]);
                var date = Assert.Single(types.Rows.Cast<DataRow>(), row => (string)row["TypeName"] == "date");
                Assert.Equal(typeof(KingbaseDate).FullName, date["ProviderSpecificDataType"]);
                types.Rows.Clear(); using var fresh = connection.GetSchema("DataTypes"); Assert.NotEmpty(fresh.Rows.Cast<DataRow>());
                using var source = connection.GetSchema("DataSourceInformation");
                var info = source.Rows[0]; Assert.Equal(DBNull.Value, info["DataSourceProductVersionNormalized"]);
                Assert.Equal(DBNull.Value, info["SupportedJoinOperators"]); Assert.Equal(DBNull.Value, info["StringLiteralPattern"]);
                var pattern = (string)info["ParameterNamePattern"];
                Assert.Matches("^" + pattern + "$", new string('p', 128));
                Assert.DoesNotMatch("^" + pattern + "$", new string('p', 129));
                Assert.DoesNotMatch("^" + pattern + "$", "汉字");
                Assert.Equal("@p", string.Format(CultureInfo.InvariantCulture, (string)info["ParameterMarkerFormat"], "p"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
    }

    [Fact]
    public async Task AllCollectionsRespectInFlightCommandAndActiveReaderOwnership()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async (connection, token) =>
        {
            using var command = new KingbaseCommand("SELECT 42", connection);
            var executing = command.ExecuteReaderAsync(token);
            await received.Task.WaitAsync(token);
            await RejectAll(); release.SetResult();
            using var reader = await executing;
            Assert.True(reader.Read()); await RejectAll(); reader.Close();
            using var schema = connection.GetSchema(); Assert.Equal(9, schema.Rows.Count);
            async Task RejectAll()
            {
                foreach (var name in Collections)
                {
                    Assert.Throws<InvalidOperationException>(() => connection.GetSchema(name));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetSchemaAsync(name, token));
                }
            }
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token); received.SetResult(); await release.Task.WaitAsync(token);
            await stream.WriteAsync(Value("42"), token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        });
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    public async Task KeywordRetrievalRetiresInterruptedExchangesWithoutReplay(string reason)
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
        await connection.OpenAsync(guard.Token);
        var retrieving = connection.GetSchemaAsync("ReservedWords", caller.Token);
        await received.Task.WaitAsync(guard.Token);
        Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
        if (reason == "caller") caller.Cancel();
        else if (reason == "close") connection.Close();
        if (reason == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => retrieving);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retrieving);
        Assert.Equal(reason == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("length")]
    [InlineData("characters")]
    [InlineData("count")]
    [InlineData("empty")]
    [InlineData("error")]
    [InlineData("null")]
    [InlineData("shape")]
    public async Task InvalidKeywordDataAndServerErrorsReleaseOwnershipAndAllowRecovery(string kind)
    {
        await Run(async (connection, token) =>
        {
            if (kind == "error")
            {
                var error = await Assert.ThrowsAsync<KingbaseException>(() => connection.GetSchemaAsync("ReservedWords", token));
                Assert.Equal("42501", error.SqlState); Assert.DoesNotContain("private-payload", error.Message);
            }
            else
            {
                var error = await Assert.ThrowsAsync<IOException>(() => connection.GetSchemaAsync("ReservedWords", token));
                Assert.DoesNotContain("private-payload", error.Message); Assert.Null(error.InnerException);
            }
            if (kind == "count") { Assert.Equal(ConnectionState.Broken, connection.State); return; }
            Assert.Equal(ConnectionState.Open, connection.State);
            using var words = await connection.GetSchemaAsync("ReservedWords", token); Assert.Single(words.Rows.Cast<DataRow>());
        }, async (stream, token) =>
        {
            await ReadExtended(stream, token);
            var response = kind switch
            {
                "duplicate" => Keywords("select", "SELECT"),
                "length" => Keywords(new string('p', 129)),
                "characters" => Keywords("private-payload"),
                "count" => Keywords(Enumerable.Range(0, 1025).Select(i => "word" + i).ToArray()),
                "empty" => Keywords(""),
                "error" => Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-payload\0\0")), Ready()),
                "null" => Join(Frame('1'), Frame('2'), Description(1, 9004, -1), Frame('D', [0, 1, 255, 255, 255, 255]), Complete(), Ready()),
                _ => Join(Frame('1'), Frame('2'), Description(2, 9004, -1), Row(["select", "private-payload"]), Complete(), Ready())
            };
            await stream.WriteAsync(response, token);
            if (kind != "count") { await ReadExtended(stream, token); await stream.WriteAsync(Keywords("select"), token); }
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); }
            catch (IOException error) when (kind == "count" && error.InnerException is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        });
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("valid")]
    [InlineData("size")]
    [InlineData("domain")]
    [InlineData("array")]
    public async Task DataTypesUsesOnlyPresentSupportedCatalogLayouts(string kind)
    {
        var types = new (string Name, short Size)[] { ("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1) }
            .Select((type, i) => new PgTypeDescription((uint)(100 + i), 90, type.Name, type.Size, 'b', 'U', 0, 0, 0, -1));
        if (kind != "absent") types = types.Append(new(uint.MaxValue, 90, "xml", kind == "size" ? (short)4 : (short)-1,
            kind == "domain" ? 'd' : 'b', 'U', 0, kind == "array" ? 100U : 0U, 0, -1));
        using var session = new PgQuerySession(new PgWire(new QueryStream(Catalog(types))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        var catalog = await PgTypeCatalog.LoadAsync(session, new(version!, "14", KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, null, null));
        using var table = KingbaseSchema.Create("DataTypes", version!, catalog);
        Assert.Equal(kind == "valid" ? 7 : 6, table.Rows.Count);
        if (kind == "valid") Assert.Equal(uint.MaxValue, Assert.Single(table.Rows.Cast<DataRow>(), row => (string)row["TypeName"] == "xml")["TypeOid"]);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("absent")]
    [InlineData("size")]
    [InlineData("date-only")]
    public async Task DataTypesSelectsExactlyOnePresentBestMatchPerClrType(string timestamp)
    {
        var layouts = new (string Name, short Size)[]
        {
            ("bool", 1), ("int2", 2), ("int4", 4), ("int8", 8), ("text", -1), ("bytea", -1),
            ("varchar", -1), ("bpchar", -1), ("date", 4), ("time", 8), ("interval", 16)
        };
        var types = layouts.Select((type, i) => new PgTypeDescription((uint)(100 + i), 90, type.Name, type.Size, 'b', 'U', 0, 0, 0, -1));
        if (timestamp != "date-only") types = types.Append(new(200, 90, "timestamptz", 8, 'b', 'U', 0, 0, 0, -1));
        if (timestamp is "valid" or "size") types = types.Append(new(201, 90, "timestamp", timestamp == "size" ? (short)4 : (short)8, 'b', 'U', 0, 0, 0, -1));
        using var session = new PgQuerySession(new PgWire(new QueryStream(Catalog(types))),
            new PgStartupResult(new Dictionary<string, string> { ["client_encoding"] = "UTF8" }, new PgBackendKey(1, 2)));
        Assert.True(KingbaseProductVersion.TryParse("KingbaseES V009R001C001B0001", out var version));
        var catalog = await PgTypeCatalog.LoadAsync(session, new(version!, "14", KingbaseCompatibilityMode.PostgreSql, KingbaseCompatibilityMode.PostgreSql, null, null));
        using var table = KingbaseSchema.Create("DataTypes", version!, catalog);
        var rows = table.Rows.Cast<DataRow>().ToArray();
        foreach (var group in rows.GroupBy(row => (string)row["DataType"]))
            Assert.Single(group, row => (bool)row["IsBestMatch"]);
        Assert.True((bool)Assert.Single(rows, row => (string)row["TypeName"] == "time")["IsBestMatch"]);
        Assert.True((bool)Assert.Single(rows, row => (string)row["TypeName"] == (timestamp == "valid" ? "timestamp" : "date"))["IsBestMatch"]);
        Assert.True((bool)Assert.Single(rows, row => (string)row["TypeName"] == "text")["IsBestMatch"]);
        Assert.Equal(DBNull.Value, Assert.Single(rows, row => (string)row["TypeName"] == "bpchar")["IsFixedLength"]);
    }

    static void Columns(DataTable table, params (string Name, Type Type)[] columns) =>
        Assert.Equal(columns, table.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType)));

    internal static byte[] Keywords(params string[] words) => Join(Frame('1'), Frame('2'), Description(1, 9004, -1),
        Join(words.Select(word => Row([word])).ToArray()), Complete(), Ready());

    internal static async Task Run(Func<KingbaseConnection, CancellationToken, Task> action,
        Func<NetworkStream, CancellationToken, Task>? server = null)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(guard.Token); using var stream = client.GetStream();
            await Initialize(stream, guard.Token);
            if (server is not null) await server(stream, guard.Token);
            else Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        });
        try { await connection.OpenAsync(guard.Token); await action(connection, guard.Token); }
        finally { connection.Close(); }
        await peer.WaitAsync(guard.Token);
    }
}
