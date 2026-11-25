using System.Data;
using System.Data.Common;
using System.Text;
using static KingbaseConnector.ProtocolTests.PgTestPeer;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;
using static KingbaseConnector.ProtocolTests.PublicPreparedTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class KeyInfoTests
{
    internal static readonly string[] Fields = ["table_oid", "attribute_number", "type_oid", "type_modifier", "catalog_name", "schema_name", "table_name", "column_name", "not_null", "identity_kind", "generated_kind", "relation_kind", "has_subclasses", "auto_increment", "key_columns", "single_unique", "attribute_version", "table_version", "isolation_level"];
    internal static byte[] Origin(uint table = uint.MaxValue, short attribute = 1, string name = "alias") => Frame('T', Join([0, 1], Encoding.UTF8.GetBytes(name + '\0'), Int32(unchecked((int)table)), [(byte)(attribute >> 8), (byte)attribute], Int32(-1), [0, 4], Int32(-1), [0, 0]));
    internal static byte[] Metadata(params string?[][] rows) => Join(Frame('1'), Frame('2'), Frame('T', Join([0, (byte)Fields.Length], Join(Fields.Select(name =>
        Join(Encoding.UTF8.GetBytes(name + '\0'), new byte[6], Int32(9004), [255, 255], Int32(-1), [0, 0])).ToArray()))),
        Join(rows.Select(values => Frame('D', Join([0, (byte)values.Length], Join(values.Select(value => value is null ? Int32(-1) : Join(Int32(Encoding.UTF8.GetByteCount(value)), Encoding.UTF8.GetBytes(value))).ToArray())))).ToArray()), Frame('C', Encoding.UTF8.GetBytes("SELECT " + rows.Length + "\0")), Ready());
    internal static string?[] Facts() => ["4294967295", "1", "4294967295", "-1", "app", "public", "items", "id", "true", "a", "", "r", "false", "true", "1", "true", "attribute-version", "table-version", "read committed"];
    internal static async Task Preflight(Stream stream, CancellationToken token, byte[]? response = null)
    {
        await ReadFrames(stream, "PDS", token); await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), Origin(), Ready()), token);
        await ReadExtended(stream, token); await stream.WriteAsync(response ?? Metadata(Facts()), token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicKeyInfoUsesBoundOriginsAndSharesCatalogFactsAcrossBothSchemaApis(bool sync, bool schemaOnly)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await Preflight(stream, guard.Token);
            if (schemaOnly)
            {
                await ReadFrames(stream, "PDS", guard.Token);
                await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), Origin(), Ready()), guard.Token);
            }
            else { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Origin(), Row(["7"]), Complete(), Ready()), guard.Token); }
            var lookup = await ReadExtended(stream, guard.Token);
            Assert.Contains("json_to_recordset", Encoding.UTF8.GetString(lookup[0]));
            Assert.DoesNotContain("4294967295", Encoding.UTF8.GetString(lookup[0]));
            Assert.Contains("4294967295", Encoding.UTF8.GetString(lookup[1]));
            await stream.WriteAsync(Metadata(Facts()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT id AS alias FROM public.items", connection);
        var behavior = CommandBehavior.KeyInfo | (schemaOnly ? CommandBehavior.SchemaOnly : 0);
        using var reader = sync ? command.ExecuteReader(behavior) : await command.ExecuteReaderAsync(behavior, guard.Token);
        var column = Assert.Single(reader.GetColumnSchema());
        Assert.Equal("app", column.BaseCatalogName); Assert.Equal("public", column.BaseSchemaName);
        Assert.Equal("items", column.BaseTableName); Assert.Equal("id", column.BaseColumnName);
        Assert.True(column.IsAliased); Assert.True(column.IsKey); Assert.True(column.IsUnique);
        Assert.True(column.IsAutoIncrement); Assert.True(column.IsIdentity); Assert.True(column.IsReadOnly);
        Assert.False(column.IsExpression); Assert.False(column.IsHidden); Assert.False(column.AllowDBNull);
        using var table = reader.GetSchemaTable();
        Assert.Equal("id", table.Rows[0][SchemaTableColumn.BaseColumnName]);
        Assert.Equal("public", table.Rows[0][SchemaTableColumn.BaseSchemaName]);
        Assert.Equal(true, table.Rows[0][SchemaTableColumn.IsKey]);
        Assert.Equal(false, table.Rows[0][SchemaTableColumn.AllowDBNull]);
        Assert.Equal(true, table.Rows[0][SchemaTableOptionalColumn.IsAutoIncrement]);
        Assert.Equal(!schemaOnly, reader.Read());
        if (!schemaOnly) Assert.Equal(7, reader.GetInt32(0));
        reader.Close(); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed-type")]
    [InlineData("changed-modifier")]
    [InlineData("join")]
    [InlineData("view")]
    [InlineData("inheritance")]
    public async Task MissingChangedOrUnprovenFactsDoNotBecomeFalseResultGuarantees(string scenario)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            var facts = Facts();
            if (scenario == "changed-type") facts[2] = "9004";
            if (scenario == "changed-modifier") facts[3] = "42";
            if (scenario == "inheritance") facts[12] = "true";
            await Preflight(stream, guard.Token, scenario == "missing" ? Metadata() : Metadata(facts));
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Origin(), Row(["7"]), Complete(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(scenario == "missing" ? Metadata() : Metadata(facts), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token);
        var sql = scenario == "join" ? "SELECT t.id FROM public.items t JOIN public.items u ON true" : scenario == "view" ? "SELECT id FROM public.a_view" : "SELECT id FROM public.items";
        using var command = new KingbaseCommand(sql, connection); using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, guard.Token);
        var column = Assert.Single(reader.GetColumnSchema()); Assert.Null(column.IsKey); Assert.Null(column.AllowDBNull); Assert.Null(column.IsUnique);
        Assert.Equal(scenario is "missing" or "changed-type" or "changed-modifier" ? null : "id", column.BaseColumnName);
        Assert.True(reader.Read()); Assert.Equal(7, reader.GetInt32(0)); reader.Close(); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("bad-bool")]
    [InlineData("empty-name")]
    [InlineData("unexpected-origin")]
    [InlineData("bad-type")]
    [InlineData("unprojected-key")]
    [InlineData("duplicate-key")]
    [InlineData("nullable-key")]
    [InlineData("identity-without-auto")]
    [InlineData("bad-generated")]
    [InlineData("duplicate-row")]
    [InlineData("nul-name")]
    [InlineData("contradictory-key")]
    public async Task MalformedMetadataRetiresTheSession(string scenario)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await Preflight(stream, guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Origin(), Row(["7"]), Complete(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); var facts = Facts();
            switch (scenario)
            {
                case "bad-bool": facts[8] = "unknown"; break;
                case "empty-name": facts[6] = ""; break;
                case "unexpected-origin": facts[0] = "1234"; break;
                case "bad-type": facts[2] = "0"; break;
                case "unprojected-key": facts[14] = "2"; break;
                case "duplicate-key": facts[14] = "1,1"; break;
                case "nullable-key": facts[8] = "false"; break;
                case "identity-without-auto": facts[13] = "false"; break;
                case "bad-generated": facts[10] = "unknown"; break;
                case "nul-name": facts[7] = "id\0invalid"; break;
                case "contradictory-key": facts[15] = "false"; break;
            }
            await stream.WriteAsync(scenario == "duplicate-row" ? Metadata(facts, facts) : Metadata(facts), guard.Token);
            await Retired(stream, guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT id FROM public.items", connection);
        await Assert.ThrowsAsync<IOException>(() => command.ExecuteReaderAsync(CommandBehavior.KeyInfo, guard.Token));
        Assert.Equal(ConnectionState.Broken, connection.State); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchMetadataErrorKeepsTheOriginalExecutionAcknowledgment(bool schemaOnly)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await Preflight(stream, guard.Token);
            if (schemaOnly)
            {
                await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), Origin(), Ready()), guard.Token);
            }
            else
            {
                await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Origin(), Row(["7"]), Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0")), Ready()), guard.Token);
            }
            await ReadExtended(stream, guard.Token);
            await stream.WriteAsync(Join(Frame('E', Encoding.UTF8.GetBytes("SERROR\0C42501\0Mprivate-denied\0\0")), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new("UPDATE public.items SET id=7 RETURNING id")); batch.BatchCommands.Add(new("DELETE FROM public.items"));
        var error = await Assert.ThrowsAsync<KingbaseException>(() => batch.ExecuteReaderAsync(CommandBehavior.KeyInfo | (schemaOnly ? CommandBehavior.SchemaOnly : 0), guard.Token));
        Assert.Equal("42501", error.SqlState); Assert.Same(batch.BatchCommands[0], error.BatchCommand); Assert.Equal(0, batch.FailedCommandIndex);
        var first = (KingbaseBatchCommand)batch.BatchCommands[0];
        Assert.Equal(schemaOnly ? KingbaseBatchCommandStatus.NotExecuted : KingbaseBatchCommandStatus.Succeeded, first.ExecutionStatus);
        Assert.Equal(schemaOnly ? -1 : 1, first.RecordsAffected);
        Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, ((KingbaseBatchCommand)batch.BatchCommands[1]).ExecutionStatus);
        using var query = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await query.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataCancellationRetiresSocketWithoutLosingOriginalBatchOutcome(bool schemaOnly)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await Preflight(stream, guard.Token);
            if (schemaOnly) { await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), Origin(), Ready()), guard.Token); }
            else { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Origin(), Row(["7"]), Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0")), Ready()), guard.Token); }
            await ReadExtended(stream, guard.Token); reached.SetResult(); await Retired(stream, guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var batch = connection.CreateBatch(); batch.BatchCommands.Add(new("UPDATE public.items SET id=7 RETURNING id"));
        var operation = batch.ExecuteReaderAsync(CommandBehavior.KeyInfo | (schemaOnly ? CommandBehavior.SchemaOnly : 0), caller.Token);
        await reached.Task.WaitAsync(guard.Token); caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var first = (KingbaseBatchCommand)batch.BatchCommands[0]; Assert.Equal(schemaOnly ? -1 : 1, first.RecordsAffected);
        Assert.Equal(schemaOnly ? KingbaseBatchCommandStatus.NotExecuted : KingbaseBatchCommandStatus.Succeeded, first.ExecutionStatus);
        Assert.Equal(ConnectionState.Broken, connection.State); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    static async Task Retired(Stream stream, CancellationToken token)
    { try { Assert.Equal(0, await stream.ReadAsync(new byte[1], token)); } catch (IOException) { } }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogChangesAfterOriginalCommandRejectTheReaderWithoutRetryOrLostAcknowledgment(bool schemaOnly)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await Preflight(stream, guard.Token);
            if (schemaOnly) { await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), Origin(), Ready()), guard.Token); }
            else { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Origin(), Row(["7"]), Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0")), Ready()), guard.Token); }
            await ReadExtended(stream, guard.Token); var facts = Facts(); facts[16] = "new-version"; await stream.WriteAsync(Metadata(facts), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Value("42"), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var batch = connection.CreateBatch(); batch.BatchCommands.Add(new("UPDATE public.items SET id=7 RETURNING id"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteReaderAsync(CommandBehavior.KeyInfo | (schemaOnly ? CommandBehavior.SchemaOnly : 0), guard.Token));
        Assert.Contains("not retried", error.Message); var item = (KingbaseBatchCommand)batch.BatchCommands[0];
        Assert.Equal(schemaOnly ? -1 : 1, item.RecordsAffected); Assert.Equal(schemaOnly ? KingbaseBatchCommandStatus.NotExecuted : KingbaseBatchCommandStatus.Succeeded, item.ExecutionStatus);
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task CancelledCatalogPreflightDoesNotClassifyAnUnsentUpdateAsOutcomeUnknown()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var caller = new CancellationTokenSource();
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), Origin(), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); reached.SetResult(); await Retired(stream, guard.Token);
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var batch = connection.CreateBatch(); batch.BatchCommands.Add(new("UPDATE public.items SET id=7 RETURNING id"));
        var operation = batch.ExecuteReaderAsync(CommandBehavior.KeyInfo, caller.Token); await reached.Task.WaitAsync(guard.Token); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var item = (KingbaseBatchCommand)batch.BatchCommands[0]; Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, item.ExecutionStatus); Assert.Equal(-1, item.RecordsAffected);
        connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemOrOriginlessColumnsDoNotInventExpressionClassification(bool system)
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), Origin(system ? uint.MaxValue : 0, system ? (short)-6 : (short)0), Ready()), guard.Token);
            await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), Origin(system ? uint.MaxValue : 0, system ? (short)-6 : (short)0), Row(["7"]), Complete(), Ready()), guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var command = new KingbaseCommand("SELECT 7", connection);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, guard.Token);
        var column = Assert.Single(reader.GetColumnSchema()); Assert.Null(column.IsExpression); Assert.Null(column.IsKey); Assert.Null(column.BaseColumnName);
        reader.Close(); connection.Close(); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task BatchByteBudgetIncludesCatalogStringsAndSharedTableSignatures()
    {
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var connection = new KingbaseConnection(Settings(listener).ConnectionString);
        var origin = Frame('T', Join([0, 1], Encoding.UTF8.GetBytes("alias\0"), Int32(-1), [0, 1], Int32(9004), [255, 255], Int32(-1), [0, 0]));
        var facts = Facts(); facts[2] = "9004"; facts[4] = new string('c', 63); facts[9] = ""; facts[13] = "false"; facts[16] = new string('a', 100); facts[17] = new string('t', 100);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream(); await Initialize(stream, guard.Token);
            for (var statement = 0; statement < 2; statement++)
                for (var phase = 0; phase < 2; phase++)
                {
                    if (phase == 0) { await ReadFrames(stream, "PDS", guard.Token); await stream.WriteAsync(Join(Frame('1'), SchemaOnlyTests.Parameters(), origin, Ready()), guard.Token); }
                    else { await ReadExtended(stream, guard.Token); await stream.WriteAsync(Join(Frame('1'), Frame('2'), origin, Row([new string('v', 524150)]), Frame('C', Encoding.UTF8.GetBytes("UPDATE 1\0")), Ready()), guard.Token); }
                    await ReadExtended(stream, guard.Token); await stream.WriteAsync(Metadata(facts), guard.Token);
                }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await connection.OpenAsync(guard.Token); using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new("UPDATE public.items SET id='one' RETURNING id")); batch.BatchCommands.Add(new("UPDATE public.items SET id='two' RETURNING id"));
        await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteReaderAsync(CommandBehavior.KeyInfo, guard.Token));
        Assert.Equal(1, batch.FailedCommandIndex);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => { Assert.Equal(KingbaseBatchCommandStatus.Succeeded, item.ExecutionStatus); Assert.Equal(1, item.RecordsAffected); });
        connection.Close(); await peer.WaitAsync(guard.Token);
    }
}
