using System.Data;
using System.Data.Common;

namespace KingbaseConnector.IntegrationTests;

public sealed class KeyInfoLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgKeyInfoReportsBaseNamesKeysIdentityGeneratedAndNullableColumns(bool schemaOnly) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_keyinfo(id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, code varchar(17) NOT NULL UNIQUE, amount numeric(12,3), calc numeric GENERATED ALWAYS AS(amount*2) STORED)", token);
        await Scalar(connection, "INSERT INTO kb_keyinfo(code,amount) VALUES('汉',1.250)", token);
        using var command = new KingbaseCommand("SELECT id AS key_id,code,amount,calc FROM kb_keyinfo WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", 1); await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo | (schemaOnly ? CommandBehavior.SchemaOnly : 0), token))
        {
            var columns = reader.GetColumnSchema(); Assert.Equal(4, columns.Count);
            Assert.Equal("id", columns[0].BaseColumnName); Assert.Equal("kb_keyinfo", columns[0].BaseTableName);
            Assert.NotNull(columns[0].BaseCatalogName); Assert.StartsWith("pg_temp_", columns[0].BaseSchemaName!);
            Assert.True(columns[0].IsKey); Assert.True(columns[0].IsUnique); Assert.True(columns[0].IsAliased);
            Assert.True(columns[0].IsAutoIncrement); Assert.True(columns[0].IsIdentity); Assert.False(columns[0].AllowDBNull);
            Assert.False(columns[1].IsKey); Assert.True(columns[1].IsUnique); Assert.False(columns[1].AllowDBNull); Assert.Equal(17, columns[1].ColumnSize);
            Assert.True(columns[2].AllowDBNull); Assert.Equal(12, columns[2].NumericPrecision); Assert.Equal(3, columns[2].NumericScale);
            Assert.True(columns[3].IsReadOnly); Assert.False(columns[3].IsIdentity);
            using var table = reader.GetSchemaTable(); Assert.NotNull(table);
            Assert.Equal(true, table.Rows[0][SchemaTableColumn.IsKey]); Assert.Equal(false, table.Rows[0][SchemaTableColumn.AllowDBNull]);
            Assert.Equal("id", table.Rows[0][SchemaTableColumn.BaseColumnName]); Assert.Equal(true, table.Rows[3][SchemaTableOptionalColumn.IsReadOnly]);
            Assert.Equal(!schemaOnly, await reader.ReadAsync(token)); if (!schemaOnly) Assert.Equal(1, reader.GetInt32(0));
        }
        Assert.Equal(1, await command.ExecuteScalarAsync(token)); await command.UnprepareAsync(token);
    });

    [LiveFact(writes: true)]
    public Task PgKeyInfoRequiresCompleteImmediateNonNullKeysAndUsesOnlyKeySlots() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_keysets(a int NOT NULL,b int NOT NULL,c int,d int NOT NULL,e int NOT NULL,PRIMARY KEY(a,b),UNIQUE(c),UNIQUE(d) DEFERRABLE INITIALLY DEFERRED)", token);
        await Scalar(connection, "CREATE UNIQUE INDEX ON kb_keysets(e) INCLUDE(c)", token);
        foreach (var (sql, keys) in new[] { ("SELECT a,b,c,d,e FROM kb_keysets", new[] { 0, 1 }), ("SELECT a,c,d FROM kb_keysets", Array.Empty<int>()), ("SELECT e,c FROM kb_keysets", new[] { 0 }) })
        {
            using var command = new KingbaseCommand(sql, connection); using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token);
            var columns = reader.GetColumnSchema();
            for (var i = 0; i < columns.Count; i++) Assert.Equal(keys.Contains(i), columns[i].IsKey);
            var nullable = columns.Single(c => c.BaseColumnName == "c"); Assert.True(nullable.AllowDBNull); Assert.False(nullable.IsUnique);
            var deferred = columns.FirstOrDefault(c => c.BaseColumnName == "d"); if (deferred is not null) Assert.False(deferred.IsUnique);
        }
        await Scalar(connection, "CREATE TEMP TABLE kb_keyrepeated(id int NOT NULL)", token);
        await Scalar(connection, "CREATE UNIQUE INDEX ON kb_keyrepeated(id,id)", token);
        using var repeated = new KingbaseCommand("SELECT id FROM kb_keyrepeated", connection);
        using var repeatedReader = await repeated.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token);
        Assert.False(Assert.Single(repeatedReader.GetColumnSchema()).IsKey);
    });

    [LiveFact(writes: true)]
    public Task PgKeyInfoDoesNotInventResultKeysForJoinsViewsSetReturningExpressionsOrInheritance() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_keyparent(id int PRIMARY KEY)", token);
        await Scalar(connection, "CREATE TEMP TABLE kb_keychild() INHERITS(kb_keyparent)", token);
        await Scalar(connection, "INSERT INTO kb_keyparent VALUES(1)", token); await Scalar(connection, "INSERT INTO kb_keychild VALUES(1)", token);
        await Scalar(connection, "CREATE TEMP VIEW kb_keyview AS SELECT id FROM kb_keyparent", token);
        foreach (var sql in new[] { "SELECT id FROM kb_keyparent", "SELECT p.id FROM kb_keyparent p LEFT JOIN kb_keyparent q ON false", "SELECT id FROM kb_keyview", "SELECT id FROM kb_keyparent ORDER BY generate_series(1,2)" })
        {
            using var command = new KingbaseCommand(sql, connection); using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, token);
            var column = Assert.Single(reader.GetColumnSchema()); Assert.Equal("id", column.BaseColumnName);
            Assert.Null(column.IsKey); Assert.Null(column.IsUnique); Assert.Null(column.AllowDBNull);
        }
        using var only = new KingbaseCommand("SELECT id FROM ONLY kb_keyparent", connection);
        using var single = await only.ExecuteReaderAsync(CommandBehavior.KeyInfo, token); Assert.True(Assert.Single(single.GetColumnSchema()).IsKey);
        Assert.True(single.Read()); Assert.False(single.Read());
        single.Close();
        using var system = new KingbaseCommand("SELECT tableoid FROM ONLY kb_keyparent", connection);
        using var systemReader = await system.ExecuteReaderAsync(CommandBehavior.KeyInfo, token);
        var systemColumn = Assert.Single(systemReader.GetColumnSchema()); Assert.Null(systemColumn.IsExpression); Assert.Null(systemColumn.BaseColumnName);
    });

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }

    [LiveFact(writes: true)]
    public Task PgKeyInfoRespectsColumnVisibilityAndTracksOwnedSequenceDefaults() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_keypermissions(id serial PRIMARY KEY, code text NOT NULL UNIQUE, hidden int)", token);
        using (var command = new KingbaseCommand("SELECT id FROM kb_keypermissions", connection))
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token))
        { var column = Assert.Single(reader.GetColumnSchema()); Assert.True(column.IsAutoIncrement); Assert.False(column.IsIdentity); }
        await Scalar(connection, "ALTER TABLE kb_keypermissions ALTER COLUMN id DROP DEFAULT", token);
        using (var command = new KingbaseCommand("SELECT id FROM kb_keypermissions", connection))
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token)) Assert.False(Assert.Single(reader.GetColumnSchema()).IsAutoIncrement);
        await Scalar(connection, "ALTER TABLE kb_keypermissions ALTER COLUMN id SET DEFAULT currval('kb_keypermissions_id_seq'::regclass)", token);
        using (var command = new KingbaseCommand("SELECT id FROM kb_keypermissions", connection))
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token)) Assert.Null(Assert.Single(reader.GetColumnSchema()).IsAutoIncrement);
        using var transaction = await connection.BeginTransactionAsync(token);
        await Scalar(connection, "GRANT SELECT(code) ON kb_keypermissions TO pg_monitor", token);
        await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        using (var command = new KingbaseCommand("SELECT code FROM kb_keypermissions", connection))
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token))
        { var column = Assert.Single(reader.GetColumnSchema()); Assert.Equal("code", column.BaseColumnName); Assert.True(column.IsKey); }
        using (var command = new KingbaseCommand("SELECT id,code FROM kb_keypermissions", connection))
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token))
        { var columns = reader.GetColumnSchema(); Assert.Null(columns[0].BaseColumnName); Assert.Null(columns[1].IsKey); Assert.Null(columns[1].AllowDBNull); }
        await transaction.RollbackAsync(token);
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact(writes: true)]
    public Task PgKeyInfoBatchKeepsPerResultMetadataAndAffectedCounts() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_keybatch(id int PRIMARY KEY, value text)", token);
        using var batch = connection.CreateBatch(); batch.BatchCommands.Add(new("INSERT INTO kb_keybatch VALUES(1,'one') RETURNING id"));
        batch.BatchCommands.Add(new("SELECT id,value FROM kb_keybatch")); await batch.PrepareAsync(token);
        using (var reader = await batch.ExecuteReaderAsync(CommandBehavior.KeyInfo, token))
        {
            var first = Assert.Single(reader.GetColumnSchema()); Assert.Equal("id", first.BaseColumnName); Assert.Null(first.IsKey);
            Assert.True(reader.Read()); Assert.Equal(1, reader.GetInt32(0)); Assert.True(reader.NextResult());
            var next = reader.GetColumnSchema(); Assert.True(next[0].IsKey); Assert.True(next[1].AllowDBNull);
            Assert.Equal("id", first.BaseColumnName); Assert.Equal(1, reader.RecordsAffected);
        }
        Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => Assert.Equal(KingbaseBatchCommandStatus.Succeeded, item.ExecutionStatus));
    });

    [LiveFact(writes: true)]
    public Task PgComputedFieldSetReturningFunctionsDoNotCreateFalseResultKeys() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_keycomputed(id int PRIMARY KEY)", token);
        await Scalar(connection, "INSERT INTO kb_keycomputed VALUES(1)", token);
        var schema = "kb_keyfunction_" + Guid.NewGuid().ToString("N");
        await Scalar(connection, "CREATE SCHEMA " + schema, token);
        try
        {
            // PostgreSQL deliberately excludes temporary schemas from implicit function lookup.
            await Scalar(connection, "CREATE FUNCTION " + schema + ".kb_expand(pg_temp.kb_keycomputed) RETURNS SETOF int LANGUAGE sql AS 'SELECT generate_series(1,2)'", token);
            await Scalar(connection, "SELECT pg_catalog.set_config('search_path','" + schema + ",pg_temp,pg_catalog',false)", token);
            foreach (var sql in new[] { "SELECT t.id,t.kb_expand FROM kb_keycomputed t", "SELECT t.id FROM kb_keycomputed t ORDER BY t.kb_expand", "SELECT t.id AS kb_expand FROM kb_keycomputed t ORDER BY t.kb_expand", "SELECT t.id FROM kb_keycomputed t ORDER BY t.id + t.kb_expand" })
            {
                using var command = new KingbaseCommand(sql, connection); using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, token);
                Assert.Null(reader.GetColumnSchema()[0].IsKey); var count = 0; while (reader.Read()) { Assert.Equal(1, reader.GetInt32(0)); count++; } Assert.Equal(2, count);
            }
        }
        finally { await Scalar(connection, "DROP SCHEMA " + schema + " CASCADE", token); }
    });

    [LiveFact(writes: true)]
    public Task PgSameNamedUserFunctionDoesNotProveAutoIncrement() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var schema = "kb_keydefault_" + Guid.NewGuid().ToString("N");
        await Scalar(connection, "CREATE SCHEMA " + schema, token);
        try
        {
            await Scalar(connection, "CREATE FUNCTION " + schema + ".nextval(regclass) RETURNS bigint LANGUAGE sql AS 'SELECT 42::bigint'", token);
            await Scalar(connection, "CREATE DOMAIN " + schema + ".regclass AS pg_catalog.regclass", token);
            await Scalar(connection, "SELECT pg_catalog.set_config('search_path','" + schema + ",pg_temp,pg_catalog',false)", token);
            await Scalar(connection, "CREATE TEMP TABLE kb_keydefault(id serial PRIMARY KEY)", token);
            await Scalar(connection, "ALTER TABLE kb_keydefault ALTER COLUMN id SET DEFAULT " + schema + ".nextval('kb_keydefault_id_seq'::pg_catalog.regclass)", token);
            Assert.Equal(42, await Scalar(connection, "INSERT INTO kb_keydefault DEFAULT VALUES RETURNING id", token));
            using var command = new KingbaseCommand("SELECT id FROM kb_keydefault", connection);
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, token);
            Assert.Null(Assert.Single(reader.GetColumnSchema()).IsAutoIncrement);
        }
        finally { await Scalar(connection, "DROP SCHEMA " + schema + " CASCADE", token); }
    });

    [LiveTheory(writes: true)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public Task PgSnapshotIsolationDoesNotPublishPotentiallyStaleCatalogNames(IsolationLevel isolation) => LiveProfile.RunPg(async (settings, token) =>
    {
        var schema = "kb_keyepoch_" + Guid.NewGuid().ToString("N");
        using var connection = new KingbaseConnection(settings.ConnectionString); using var other = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token); await other.OpenAsync(token);
        await Scalar(connection, "CREATE SCHEMA " + schema, token);
        try
        {
            await Scalar(connection, "CREATE TABLE " + schema + ".items(id int PRIMARY KEY)", token);
            await Scalar(connection, "INSERT INTO " + schema + ".items VALUES(1)", token);
            using var transaction = await connection.BeginTransactionAsync(isolation, token);
            Assert.Equal(1, await Scalar(connection, "SELECT 1", token));
            await Scalar(other, "ALTER TABLE " + schema + ".items RENAME COLUMN id TO new_id", token);
            using (var command = new KingbaseCommand("SELECT new_id FROM " + schema + ".items", connection) { Transaction = transaction })
            using (var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, token))
            { var column = Assert.Single(reader.GetColumnSchema()); Assert.Null(column.BaseColumnName); Assert.Null(column.IsKey); Assert.Null(column.AllowDBNull); Assert.True(reader.Read()); Assert.Equal(1, reader.GetInt32(0)); }
            await transaction.RollbackAsync(token);
        }
        finally { await Scalar(other, "DROP SCHEMA " + schema + " CASCADE", token); }
    });
}
