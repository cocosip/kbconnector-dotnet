using System.Data;
using System.Data.Common;

namespace KingbaseConnector.IntegrationTests;

public sealed class CommandBuilderLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgBuilderGeneratesQuotedAliasedCrudWithNullsAndGeneratedColumns(bool columnNames) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE \"kb_Builder\"\"汉\"(\"Key\" int PRIMARY KEY,\"Val汉\" text,\"Calc\" int GENERATED ALWAYS AS (\"Key\"+1) STORED)", token);
        await Scalar(connection, "INSERT INTO \"kb_Builder\"\"汉\"(\"Key\",\"Val汉\") VALUES(1,'old'),(2,'delete')", token);
        if (!columnNames) await Scalar(connection, "SET standard_conforming_strings=off", token);
        using var select = new KingbaseCommand("SELECT \"Key\" AS \"key alias\",\"Val汉\" AS \"value alias\",\"Calc\" AS calculated FROM \"kb_Builder\"\"汉\" ORDER BY \"Key\"", connection);
        using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
        using var table = new DataTable(); Assert.Equal(2, adapter.Fill(table));
        var insert = builder.GetInsertCommand(columnNames); var update = builder.GetUpdateCommand(columnNames); var delete = builder.GetDeleteCommand(columnNames);
        Assert.Same(connection, update.Connection); Assert.DoesNotContain("\"Calc\"", insert.CommandText); Assert.DoesNotContain("\"Calc\" =", update.CommandText.Split(" WHERE ")[0]);
        Assert.All(insert.Parameters.Cast<KingbaseParameter>(), parameter => Assert.StartsWith("pg_catalog.", parameter.DataTypeName!));
        table.Rows[0]["value alias"] = DBNull.Value; table.Rows[1].Delete(); var added = table.NewRow(); added["key alias"] = 3; added["value alias"] = "new"; table.Rows.Add(added);
        Assert.Equal(3, adapter.Update(table));
        using var oracle = new KingbaseCommand("SELECT \"Key\",\"Val汉\",\"Calc\" FROM \"kb_Builder\"\"汉\" ORDER BY \"Key\"", connection); using var reader = await oracle.ExecuteReaderAsync(token);
        Assert.True(reader.Read()); Assert.Equal(1, reader.GetInt32(0)); Assert.True(reader.IsDBNull(1)); Assert.Equal(2, reader.GetInt32(2));
        Assert.True(reader.Read()); Assert.Equal(3, reader.GetInt32(0)); Assert.Equal("new", reader.GetString(1)); Assert.Equal(4, reader.GetInt32(2)); Assert.False(reader.Read());
    });

    [LiveFact(writes: true)]
    public Task PgBuilderPreservesOriginalValuesForOptimisticConcurrency() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_conflict(id int PRIMARY KEY,value text)", token); await Scalar(connection, "INSERT INTO kb_builder_conflict VALUES(1,'old')", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_builder_conflict", connection); using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
        using var table = new DataTable(); Assert.Equal(1, adapter.Fill(table)); table.Rows[0]["value"] = "client";
        await Scalar(connection, "UPDATE kb_builder_conflict SET value='server'", token);
        Assert.Throws<DBConcurrencyException>(() => adapter.Update(table)); Assert.Equal(DataRowState.Modified, table.Rows[0].RowState);
        Assert.Equal("server", await Scalar(connection, "SELECT value FROM kb_builder_conflict", token));
    });

    [LiveFact(writes: true)]
    public Task PgBuilderRejectsUnknownComplexMetadataAndMissingUpdateKeys() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_guard(id int PRIMARY KEY,value text)", token);
        foreach (var sql in new[] { "SELECT t.id,t.value FROM kb_builder_guard t JOIN kb_builder_guard u ON true", "SELECT id,value FROM kb_builder_guard ORDER BY generate_series(1,2)" })
        {
            using var select = new KingbaseCommand(sql, connection); using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
            Assert.Throws<InvalidOperationException>(() => builder.GetInsertCommand());
        }
        using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token))
        {
            using var select = new KingbaseCommand("SELECT id,value FROM kb_builder_guard", connection) { Transaction = transaction }; using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
            Assert.Throws<InvalidOperationException>(() => builder.GetInsertCommand()); await transaction.RollbackAsync(token);
        }
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_nokey(id int,value text)", token);
        using var noKeySelect = new KingbaseCommand("SELECT id,value FROM kb_builder_nokey", connection); using var noKeyAdapter = new KingbaseDataAdapter(noKeySelect); using var noKeyBuilder = new KingbaseCommandBuilder(noKeyAdapter);
        Assert.NotNull(noKeyBuilder.GetInsertCommand()); Assert.Throws<InvalidOperationException>(() => noKeyBuilder.GetUpdateCommand()); Assert.Throws<InvalidOperationException>(() => noKeyBuilder.GetDeleteCommand());
    });

    [LiveFact(writes: true)]
    public Task PgBuilderUsesExactCatalogTypesAndExcludesNonComparableLongValuesFromConcurrency() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_types(id serial PRIMARY KEY,n numeric(12,3),v varchar(17),b bytea,u uuid,d date,t time,z timetz,ts timestamp,tz timestamptz,iv interval,x xml,j json,jb jsonb)", token);
        await Scalar(connection, "INSERT INTO kb_builder_types DEFAULT VALUES", token);
        using var select = new KingbaseCommand("SELECT * FROM kb_builder_types", connection); using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
        using var table = new DataTable(); adapter.FillSchema(table, SchemaType.Source); table.Columns["tz"]!.DateTimeMode = DataSetDateTime.Utc; Assert.Equal(1, adapter.Fill(table));
        var values = new object[] { 1.250m, "汉", new byte[] { 1, 2, 3 }, Guid.Parse("a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11"), new DateTime(2024, 2, 29), TimeSpan.FromTicks(1234560), new KingbaseTimeTz(TimeSpan.FromHours(12), TimeSpan.FromSeconds(60)), new DateTime(2024, 2, 29, 1, 2, 3, DateTimeKind.Unspecified), new DateTime(2024, 2, 29, 1, 2, 3, DateTimeKind.Utc), new KingbaseInterval(1, 2, 3), "<r/>", "{\"n\":1}", "{\"n\":1}" };
        for (var i = 0; i < values.Length; i++) table.Rows[0][i + 1] = values[i];
        var update = builder.GetUpdateCommand(); var where = update.CommandText.Split(" WHERE ")[1];
        Assert.DoesNotContain("\"x\" =", where); Assert.DoesNotContain("\"j\" =", where); Assert.Contains("\"jb\" =", where);
        Assert.DoesNotContain("\"id\" =", update.CommandText.Split(" WHERE ")[0]); Assert.Equal(1, adapter.Update(table));
        using var oracle = new DataTable(); Assert.Equal(1, adapter.Fill(oracle));
        values[12] = "{\"n\": 1}"; // JSONB renders its canonical server text independently of input whitespace.
        for (var i = 0; i < values.Length; i++) Assert.Equal(values[i], oracle.Rows[0][i + 1]);
        for (var i = 1; i < table.Columns.Count; i++) table.Rows[0][i] = DBNull.Value;
        Assert.Equal(1, adapter.Update(table)); Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_builder_types WHERE n IS NULL AND x IS NULL AND j IS NULL AND jb IS NULL", token));
    });

    [LiveFact(writes: true)]
    public Task PgBuilderKeepsCompositeKeysOriginalNullFlagsAndExplicitTransaction() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_composite(a int,b int,value text,PRIMARY KEY(a,b))", token); await Scalar(connection, "INSERT INTO kb_builder_composite VALUES(1,1,NULL),(1,2,'same')", token);
        using var transaction = await connection.BeginTransactionAsync(token);
        using var select = new KingbaseCommand("SELECT a,b,value FROM kb_builder_composite ORDER BY a,b", connection) { Transaction = transaction }; using var adapter = new KingbaseDataAdapter(select) { AcceptChangesDuringUpdate = false }; using var builder = new KingbaseCommandBuilder(adapter);
        using var table = new DataTable(); Assert.Equal(2, adapter.Fill(table)); Assert.Same(transaction, builder.GetUpdateCommand().Transaction);
        table.Rows[0]["value"] = "new"; table.Rows[1].Delete(); table.Rows.Add(2, 1, DBNull.Value); Assert.Equal(3, adapter.Update(table));
        Assert.Equal("new", await Scalar(connection, "SELECT value FROM kb_builder_composite WHERE a=1 AND b=1", token)); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_builder_composite WHERE a=1 AND b=2", token));
        await transaction.RollbackAsync(token);
        Assert.Same(DBNull.Value, await Scalar(connection, "SELECT value FROM kb_builder_composite WHERE a=1 AND b=1", token)); Assert.Equal("same", await Scalar(connection, "SELECT value FROM kb_builder_composite WHERE a=1 AND b=2", token)); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_builder_composite WHERE a=2", token));
    });

    [LiveFact(writes: true)]
    public Task PgBuilderRefreshesSchemaAndMovesOrDetachesItsRowHandler() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_refresh(id int PRIMARY KEY,value text)", token); await Scalar(connection, "INSERT INTO kb_builder_refresh VALUES(1,'old')", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_builder_refresh", connection); using var first = new KingbaseDataAdapter(select); using var second = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(first);
        Assert.Equal("pg_catalog.text", builder.GetInsertCommand().Parameters.Cast<KingbaseParameter>().Single(p => p.SourceColumn == "value").DataTypeName);
        await Scalar(connection, "ALTER TABLE kb_builder_refresh ALTER COLUMN value TYPE varchar(17)", token); builder.RefreshSchema();
        Assert.Equal("pg_catalog.varchar", builder.GetInsertCommand().Parameters.Cast<KingbaseParameter>().Single(p => p.SourceColumn == "value").DataTypeName);
        builder.DataAdapter = second; using var table = new DataTable(); Assert.Equal(1, second.Fill(table)); table.Rows[0]["value"] = "new";
        Assert.Throws<InvalidOperationException>(() => first.Update(table)); table.Rows[0].ClearErrors(); Assert.Equal(1, second.Update(table));
        builder.Dispose(); table.Rows[0]["value"] = "after"; Assert.Throws<InvalidOperationException>(() => second.Update(table)); Assert.Equal("new", await Scalar(connection, "SELECT value FROM kb_builder_refresh", token));
    });

    [LiveFact(writes: true)]
    public Task PgBuilderRestoresIdentifierTokensOnceAndSeparatesCaseDistinctSourceNames() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE \"kbc_b0\"(\"kbc_b1\" int PRIMARY KEY,\"value\" text,\"VALUE\" text,\"v\"\";--\" text)", token);
        await Scalar(connection, "INSERT INTO \"kbc_b0\" VALUES(1,'lower','upper','quoted')", token);
        using var select = new KingbaseCommand("SELECT * FROM \"kbc_b0\"", connection); using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
        using var table = new DataTable(); Assert.Equal(1, adapter.Fill(table)); builder.GetUpdateCommand(true);
        table.Rows[0]["value"] = "changed"; table.Rows[0]["v\";--"] = "safe"; Assert.Equal(1, adapter.Update(table));
        Assert.Equal("changed", await Scalar(connection, "SELECT \"value\" FROM \"kbc_b0\"", token)); Assert.Equal("upper", await Scalar(connection, "SELECT \"VALUE\" FROM \"kbc_b0\"", token)); Assert.Equal("safe", await Scalar(connection, "SELECT \"v\"\";--\" FROM \"kbc_b0\"", token));
    });

    [LiveFact(writes: true)]
    public Task PgBuilderRejectsOnlyAndRepeatedBaseColumnsBeforeAnyWrite() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_parent(id int PRIMARY KEY,value text)", token); await Scalar(connection, "CREATE TEMP TABLE kb_builder_child() INHERITS(kb_builder_parent)", token);
        await Scalar(connection, "INSERT INTO kb_builder_parent VALUES(1,'same')", token); await Scalar(connection, "INSERT INTO kb_builder_child VALUES(1,'same')", token);
        foreach (var sql in new[] { "SELECT id,value FROM ONLY kb_builder_parent", "SELECT id,value AS first,value AS second FROM ONLY kb_builder_parent" })
        {
            using var select = new KingbaseCommand(sql, connection); using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
            Assert.Throws<NotSupportedException>(() => builder.GetUpdateCommand());
        }
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_duplicate(id int PRIMARY KEY,value text)", token);
        using var duplicate = new KingbaseCommand("SELECT id,value AS first,value AS second FROM kb_builder_duplicate", connection); using var duplicateAdapter = new KingbaseDataAdapter(duplicate); using var duplicateBuilder = new KingbaseCommandBuilder(duplicateAdapter);
        Assert.Throws<NotSupportedException>(() => duplicateBuilder.GetInsertCommand());
        Assert.Equal(2L, await Scalar(connection, "SELECT count(*) FROM kb_builder_parent WHERE value='same'", token));
    });

    [LiveFact(writes: true)]
    public Task PgFailedGenerationCannotChangeLaterCallerAuthoredSql() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_failure(id int PRIMARY KEY,value text)", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_builder_failure", connection); using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
        var saved = builder.GetUpdateCommand(); using var table = new DataTable(); table.Columns.Add("id", typeof(int)); table.Rows.Add(1); table.AcceptChanges(); table.Rows[0]["id"] = 2;
        Assert.Throws<InvalidOperationException>(() => adapter.Update(table));
        const string sql = "SELECT '\"kbc_b0\"'"; saved.CommandText = sql; saved.Parameters.Clear(); Assert.Equal(sql, saved.CommandText); Assert.Equal("\"kbc_b0\"", await saved.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData("汉")]
    [InlineData("IsNull_h")]
    [InlineData("a_name_that_is_longer_than_the_bcl_generated_parameter_name_limit")]
    public Task PgBuilderNamedFallbackAndBaseTypedRefreshHandleWiderProjections(string alias) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_wider(id int PRIMARY KEY,value text)", token); await Scalar(connection, "INSERT INTO kb_builder_wider VALUES(1,'old')", token);
        using var select = new KingbaseCommand("SELECT id AS \"" + alias + "\" FROM kb_builder_wider", connection); using var adapter = new KingbaseDataAdapter(select); using DbCommandBuilder builder = new KingbaseCommandBuilder(adapter);
        Assert.DoesNotContain("@@", builder.GetInsertCommand(true).CommandText);
        select.CommandText = "SELECT id AS \"" + alias + "\",value FROM kb_builder_wider"; builder.RefreshSchema(); Assert.NotNull(builder.GetUpdateCommand());
        using var table = new DataTable(); Assert.Equal(1, adapter.Fill(table)); table.Rows[0]["value"] = "new"; Assert.Equal(1, adapter.Update(table)); Assert.Equal("new", await Scalar(connection, "SELECT value FROM kb_builder_wider", token));
    });

    [LiveTheory(writes: true)]
    [InlineData("COLLATE \"C\"")]
    [InlineData("text_pattern_ops")]
    public Task PgBuilderRequiresKeyComparisonSemanticsToMatchColumnEquality(string indexClause) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_keysemantics(id text NOT NULL,value text)", token); await Scalar(connection, "CREATE UNIQUE INDEX ON kb_builder_keysemantics(id " + indexClause + ")", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_builder_keysemantics", connection); using var adapter = new KingbaseDataAdapter(select); using var builder = new KingbaseCommandBuilder(adapter);
        Assert.Throws<InvalidOperationException>(() => builder.GetUpdateCommand());
    });

    [LiveFact(writes: true)]
    public Task PgBuilderCanReadItsOwnQuotedIdentifiers() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE \"kb_builder_self\"\"quoted\"(id int PRIMARY KEY,value text)", token); await Scalar(connection, "INSERT INTO \"kb_builder_self\"\"quoted\" VALUES(1,'old')", token);
        using var builder = new KingbaseCommandBuilder();
        using var select = new KingbaseCommand("SELECT " + builder.QuoteIdentifier("id") + "," + builder.QuoteIdentifier("value") + " FROM " + builder.QuoteIdentifier("kb_builder_self\"quoted"), connection);
        using var adapter = new KingbaseDataAdapter(select); builder.DataAdapter = adapter;
        using var table = new DataTable(); Assert.Equal(1, adapter.Fill(table)); table.Rows[0]["value"] = "new"; Assert.Equal(1, adapter.Update(table)); Assert.Equal("new", await Scalar(connection, "SELECT value FROM \"kb_builder_self\"\"quoted\"", token));
    });

    [LiveFact(writes: true)]
    public Task PgBuilderKeepsOrdinalParameterModeAcrossBaseTypedRefresh() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_builder_ordinal(id int PRIMARY KEY,value text)", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_builder_ordinal", connection); using var adapter = new KingbaseDataAdapter(select); using DbCommandBuilder builder = new KingbaseCommandBuilder(adapter);
        var first = builder.GetInsertCommand().Parameters.Cast<KingbaseParameter>().Select(p => p.ParameterName).ToArray();
        builder.RefreshSchema(); var second = builder.GetInsertCommand().Parameters.Cast<KingbaseParameter>().Select(p => p.ParameterName).ToArray();
        Assert.Equal(first, second); Assert.All(second, name => Assert.Matches("^p[0-9]+$", name));
    });

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }
}
