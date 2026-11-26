using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class BinaryCopyLiveTests
{
    static readonly KingbaseDbType[] Types = [KingbaseDbType.Boolean, KingbaseDbType.Smallint, KingbaseDbType.Integer,
        KingbaseDbType.Bigint, KingbaseDbType.Oid, KingbaseDbType.Real, KingbaseDbType.Double, KingbaseDbType.Text,
        KingbaseDbType.Varchar, KingbaseDbType.Char, KingbaseDbType.Bytea, KingbaseDbType.Uuid];
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task CoreRowsRoundTripWithIndependentTextReaderVerification(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_typed_copy (b pg_catalog.bool,s pg_catalog.int2,i pg_catalog.int4,l pg_catalog.int8,o pg_catalog.oid,r pg_catalog.float4,d pg_catalog.float8,t pg_catalog.text,v pg_catalog.varchar(40),c pg_catalog.bpchar(4),a pg_catalog.bytea,u pg_catalog.uuid)", connection)) await create.ExecuteNonQueryAsync(token);
        var bytes = Enumerable.Range(0, 65539).Select(i => (byte)(i % 251)).ToArray();
        var uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        using (var input = await connection.BeginBinaryImportAsync("COPY kb_typed_copy FROM STDIN (FORMAT BINARY)", Types, token))
        {
            await input.WriteRowAsync([true, short.MinValue, int.MinValue, long.MinValue, uint.MaxValue, -0f, double.PositiveInfinity, "界😀\ufeff", "unicode界", "ab", bytes, uuid], token);
            input.WriteRow(false, short.MaxValue, int.MaxValue, long.MaxValue, 0u, float.NaN, double.NegativeInfinity, "", "", "", Array.Empty<byte>(), Guid.Empty);
            await input.WriteRowAsync(Enumerable.Repeat<object?>(DBNull.Value, Types.Length).ToArray(), token);
            Assert.Null(input.RowsCopied); Assert.Equal(3UL, await input.CompleteAsync(token));
        }
        var expected = new List<object[]>();
        using (var command = new KingbaseCommand("SELECT * FROM kb_typed_copy ORDER BY i NULLS LAST", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) { var row = new object[Types.Length]; reader.GetValues(row); expected.Add(row); }
        Assert.Equal(3, expected.Count); Assert.Equal(int.MinValue, expected[0][2]); Assert.Equal(long.MinValue, expected[0][3]);
        Assert.Equal(uint.MaxValue, expected[0][4]); Assert.Equal(uuid, expected[0][11]); Assert.Equal(bytes, Assert.IsType<byte[]>(expected[0][10]));
        using var output = await connection.BeginBinaryExportAsync("COPY (SELECT * FROM kb_typed_copy ORDER BY i NULLS LAST) TO STDOUT (FORMAT BINARY)", Types, token);
        foreach (var row in expected) AssertRow(row, Assert.IsType<object[]>(await output.ReadRowAsync(token)));
        Assert.Null(await output.ReadRowAsync(token)); Assert.Equal(3UL, output.RowsCopied);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ServerGeneratedValuesVerifyBinaryFormatsWithoutUsingTheImporter(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var output = await connection.BeginBinaryExportAsync("COPY (SELECT true::pg_catalog.bool,(-32768)::pg_catalog.int2,(-2147483648)::pg_catalog.int4,(-9223372036854775808)::pg_catalog.int8,'4294967295'::pg_catalog.text::pg_catalog.oid,1.5::pg_catalog.float4,(-2.5)::pg_catalog.float8,'文字😀'::pg_catalog.text,'abc'::pg_catalog.varchar,'ab'::pg_catalog.bpchar(4),NULL::pg_catalog.bytea,'00112233-4455-6677-8899-aabbccddeeff'::pg_catalog.uuid) TO STDOUT (FORMAT BINARY)", Types, token);
        AssertRow([true, short.MinValue, int.MinValue, long.MinValue, uint.MaxValue, 1.5f, -2.5d, "文字😀", "abc", mode == KingbaseCompatibilityMode.MySql ? "ab" : "ab  ", DBNull.Value, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")], Assert.IsType<object[]>(await output.ReadRowAsync(token)));
        Assert.Null(await output.ReadRowAsync(token)); Assert.Equal(1UL, output.RowsCopied);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, false)]
    [InlineData(KingbaseCompatibilityMode.Oracle, false)]
    [InlineData(KingbaseCompatibilityMode.MySql, false)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, false)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, true)]
    [InlineData(KingbaseCompatibilityMode.Oracle, true)]
    [InlineData(KingbaseCompatibilityMode.MySql, true)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, true)]
    public Task UncompletedImportCancelsAtomicallyAndPreservesOuterTransactionOwnership(KingbaseCompatibilityMode mode, bool transactional) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_typed_cancel (i pg_catalog.int4,t pg_catalog.text)", connection)) await create.ExecuteNonQueryAsync(token);
        using var transaction = transactional ? await connection.BeginTransactionAsync(token) : null;
        var input = await connection.BeginBinaryImportAsync("COPY kb_typed_cancel FROM STDIN (FORMAT BINARY)", new[] {KingbaseDbType.Integer, KingbaseDbType.Text}, token);
        await input.WriteRowAsync([42, "pending"], token); await input.DisposeAsync(); Assert.Null(input.RowsCopied);
        Assert.Equal(ConnectionState.Open, connection.State);
        if (transaction is not null) { Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token); }
        using var count = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM kb_typed_cancel", connection); Assert.Equal(0, await count.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task LargeCumulativeCopyAndStaleDisposalKeepPooledBorrowersIsolated(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1; using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        KingbaseBinaryExporter? previous = null; var text = new string('界', 20000); int? pid = null;
        for (var lease = 0; lease < 2; lease++)
        {
            using var connection = await source.OpenConnectionAsync(token);
            using (var identity = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection))
            { var actual = Assert.IsType<int>(await identity.ExecuteScalarAsync(token)); if (pid.HasValue) Assert.Equal(pid, actual); pid = actual; }
            using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_typed_pool (i pg_catalog.int4,t pg_catalog.text)", connection)) await create.ExecuteNonQueryAsync(token);
            using (var input = await connection.BeginBinaryImportAsync("COPY kb_typed_pool FROM STDIN (FORMAT BINARY)", new[] {KingbaseDbType.Integer, KingbaseDbType.Text}, token))
            {
                previous?.Dispose(); for (var i = 0; i < 24; i++) await input.WriteRowAsync([i, text], token); Assert.Equal(24UL, await input.CompleteAsync(token));
            }
            previous = await connection.BeginBinaryExportAsync("COPY (SELECT * FROM kb_typed_pool ORDER BY i) TO STDOUT (FORMAT BINARY)", new[] {KingbaseDbType.Integer, KingbaseDbType.Text}, token);
            for (var i = 0; i < 24; i++) AssertRow([i, text], Assert.IsType<object[]>(await previous.ReadRowAsync(token)));
            Assert.Null(await previous.ReadRowAsync(token)); Assert.Equal(24UL, previous.RowsCopied);
        }
        previous!.Dispose();
    });
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task FilteredImportReportsInsertedRowsAndPreservesTheConnection(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_typed_filter (i pg_catalog.int4,t pg_catalog.text)", connection)) await create.ExecuteNonQueryAsync(token);
        using (var input = await connection.BeginBinaryImportAsync("COPY kb_typed_filter FROM STDIN (FORMAT BINARY) WHERE i > 0", new[] { KingbaseDbType.Integer, KingbaseDbType.Text }, token))
        {
            input.WriteRow(-1, "filtered"); input.WriteRow(42, "kept");
            Assert.Equal(1UL, await input.CompleteAsync(token)); Assert.Equal(1UL, input.RowsCopied);
        }
        Assert.Equal(ConnectionState.Open, connection.State);
        using var verify = new KingbaseCommand("SELECT i FROM kb_typed_filter", connection);
        using var reader = await verify.ExecuteReaderAsync(token); Assert.True(await reader.ReadAsync(token));
        Assert.Equal(42, reader.GetInt32(0)); Assert.False(await reader.ReadAsync(token));
    });

    static void AssertRow(object[] expected, object[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            if (expected[i] is byte[] bytes) Assert.Equal(bytes, Assert.IsType<byte[]>(actual[i])); else Assert.Equal(expected[i], actual[i]);
    }
}
