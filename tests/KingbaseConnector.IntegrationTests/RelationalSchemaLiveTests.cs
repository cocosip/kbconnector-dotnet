using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class RelationalSchemaLiveTests
{
    [LiveFact(writes: true)]
    public Task PgRelationalCollectionsExcludeOtherSessionsTemporaryTables() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var owner = new KingbaseConnection(settings.ConnectionString); await owner.OpenAsync(token);
        await Scalar(owner, "CREATE TEMP TABLE kb_rel_other_session(a int PRIMARY KEY,b int UNIQUE,CONSTRAINT kb_rel_other_session_fk FOREIGN KEY(b) REFERENCES kb_rel_other_session(a))", token);
        var schema = await TemporarySchema(owner, token);
        using var observer = new KingbaseConnection(settings.ConnectionString); await observer.OpenAsync(token);
        Assert.Equal(0, await Scalar(observer, "SELECT count(*)::pg_catalog.int4 FROM information_schema.tables WHERE " +
            "table_schema::pg_catalog.text OPERATOR(pg_catalog.=) @schema AND table_name::pg_catalog.text OPERATOR(pg_catalog.=) @table", token,
            ("schema", schema), ("table", "kb_rel_other_session")));
        using var tables = observer.GetSchema("Tables", [null, schema, "kb_rel_other_session"]); Assert.Empty(tables.Rows.Cast<DataRow>());
        var counts = new List<int>();
        foreach (var name in new[] { "PrimaryKeys", "UniqueKeys", "ForeignKeys", "ForeignKeyColumns", "Indexes", "IndexColumns" })
        {
            using var visibleToOwner = await owner.GetSchemaAsync(name, [null, schema, "kb_rel_other_session"], token);
            Assert.NotEmpty(visibleToOwner.Rows.Cast<DataRow>());
            using var hiddenFromObserver = await observer.GetSchemaAsync(name, [null, schema, "kb_rel_other_session"], token);
            counts.Add(hiddenFromObserver.Rows.Count);
        }
        Assert.All(counts, count => Assert.Equal(0, count));
    });

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgCompositeKeysAndReorderedReferencesKeepOidIdentity(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        const string parent = "kb_rel_%_'汉\"Parent";
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE " + Quote(parent) + " (a int, b int, CONSTRAINT kb_rel_pk PRIMARY KEY(b,a), CONSTRAINT kb_rel_uq UNIQUE(a,b))", token);
        await Scalar(connection, "CREATE TEMP TABLE kb_rel_child (x int, y int, CONSTRAINT kb_rel_fk FOREIGN KEY(y,x) REFERENCES " +
            Quote(parent) + "(a,b) MATCH FULL ON UPDATE CASCADE ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED)", token);
        await Scalar(connection, "CREATE TEMP TABLE kb_rel_other (x int, y int, CONSTRAINT kb_rel_fk FOREIGN KEY(x,y) REFERENCES " + Quote(parent) + "(b,a) ON DELETE RESTRICT)", token);
        var schema = await TemporarySchema(connection, token);
        using var transaction = await connection.BeginTransactionAsync(token);
        using var primary = await Schema("PrimaryKeys", [connection.Database, schema, parent, "kb_rel_pk"]);
        Assert.Equal(new[] { "b", "a" }, Strings(primary, "COLUMN_NAME")); Assert.Equal(new[] { 1, 2 }, Ordinals(primary));
        Assert.All(primary.Rows.Cast<DataRow>(), row => { Assert.False((bool)row["IS_DEFERRABLE"]); Assert.False((bool)row["INITIALLY_DEFERRED"]); });
        using var unique = await Schema("UniqueKeys", [null, schema, parent]);
        Assert.Equal(new[] { "a", "b" }, Strings(unique, "COLUMN_NAME")); Assert.Equal(new[] { 1, 2 }, Ordinals(unique));
        using var foreign = await Schema("ForeignKeys", [null, schema, "kb_rel_child", "kb_rel_fk"]);
        var fk = Assert.Single(foreign.Rows.Cast<DataRow>());
        Assert.Equal(parent, fk["REFERENCED_TABLE_NAME"]); Assert.Equal(schema, fk["REFERENCED_TABLE_SCHEMA"]);
        Assert.Equal("FULL", fk["MATCH_OPTION"]); Assert.Equal("CASCADE", fk["UPDATE_RULE"]); Assert.Equal("SET NULL", fk["DELETE_RULE"]);
        Assert.True((bool)fk["IS_DEFERRABLE"]); Assert.True((bool)fk["INITIALLY_DEFERRED"]); Assert.True((bool)fk["IS_VALIDATED"]);
        var referencedIndex = await Scalar(connection, "SELECT x.relname::pg_catalog.text FROM pg_catalog.pg_constraint c " +
            "JOIN pg_catalog.pg_class r ON r.oid OPERATOR(pg_catalog.=) c.conrelid " +
            "JOIN pg_catalog.pg_namespace n ON n.oid OPERATOR(pg_catalog.=) r.relnamespace " +
            "JOIN pg_catalog.pg_class x ON x.oid OPERATOR(pg_catalog.=) c.conindid " +
            "WHERE n.nspname::pg_catalog.text OPERATOR(pg_catalog.=) @schema AND r.relname::pg_catalog.text OPERATOR(pg_catalog.=) @table " +
            "AND c.conname::pg_catalog.text OPERATOR(pg_catalog.=) @constraint", token, ("schema", schema), ("table", "kb_rel_child"), ("constraint", "kb_rel_fk"));
        Assert.Equal(referencedIndex, fk["REFERENCED_INDEX_NAME"]);
        using var pairs = await Schema("ForeignKeyColumns", [null, schema, "kb_rel_child", "kb_rel_fk"]);
        Assert.Equal(new[] { "y", "x" }, Strings(pairs, "COLUMN_NAME")); Assert.Equal(new[] { "a", "b" }, Strings(pairs, "REFERENCED_COLUMN_NAME"));
        Assert.Equal(new[] { 1, 2 }, Ordinals(pairs));
        using var other = await Schema("ForeignKeys", [null, schema, "kb_rel_other", "kb_rel_fk"]);
        Assert.Equal("RESTRICT", other.Rows[0]["DELETE_RULE"]); Assert.Equal("NO ACTION", other.Rows[0]["UPDATE_RULE"]);
        using var otherPairs = await Schema("ForeignKeyColumns", [null, schema, "kb_rel_other", "kb_rel_fk", "x"]);
        Assert.Single(otherPairs.Rows.Cast<DataRow>()); Assert.Equal("b", otherPairs.Rows[0]["REFERENCED_COLUMN_NAME"]);
        using var absent = await Schema("PrimaryKeys", [null, schema, "kb_rel_%"]); Assert.Empty(absent.Rows.Cast<DataRow>());
        using var empty = await Schema("PrimaryKeys", [null, schema, parent, ""]); Assert.Empty(empty.Rows.Cast<DataRow>());
        Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token);
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        Task<DataTable> Schema(string name, string?[] restrictions) => async ? connection.GetSchemaAsync(name, restrictions, token) : Task.FromResult(connection.GetSchema(name, restrictions));
    });

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgExpressionPartialIncludedAndHashIndexesReportOnlyKnownFacts(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        const string index = "kb_rel_%_'汉\"Index";
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_rel_indexes (a int, b text, extra int, flag int)", token);
        await Scalar(connection, "CREATE UNIQUE INDEX " + Quote(index) + " ON kb_rel_indexes (b COLLATE pg_catalog.\"C\" pg_catalog.text_pattern_ops DESC NULLS FIRST, a ASC NULLS LAST) INCLUDE(extra) WHERE flag > 0", token);
        await Scalar(connection, "CREATE INDEX kb_rel_expression ON kb_rel_indexes ((pg_catalog.lower(b)) DESC) INCLUDE(extra) WHERE a IS NOT NULL", token);
        await Scalar(connection, "CREATE INDEX kb_rel_hash ON kb_rel_indexes USING hash(a)", token);
        var schema = await TemporarySchema(connection, token);
        using var transaction = await connection.BeginTransactionAsync(token);
        using var indexes = await Schema("Indexes", [connection.Database, schema, "kb_rel_indexes"]);
        Assert.Equal(3, indexes.Rows.Count);
        var composite = Assert.Single(indexes.Rows.Cast<DataRow>(), row => (string)row["INDEX_NAME"] == index);
        Assert.True((bool)composite["IS_UNIQUE"]); Assert.False((bool)composite["IS_PRIMARY"]);
        Assert.True((bool)composite["IS_VALID"]); Assert.True((bool)composite["IS_READY"]); Assert.True((bool)composite["IS_LIVE"]);
        Assert.Equal("btree", composite["INDEX_TYPE"]); Assert.Equal(2, composite["KEY_COLUMN_COUNT"]); Assert.Equal(3, composite["COLUMN_COUNT"]);
        Assert.Contains("flag", (string)composite["PREDICATE"]); Assert.Equal(DBNull.Value, composite["EXPRESSION"]);
        using var columns = await Schema("IndexColumns", [null, schema, "kb_rel_indexes", index]);
        Assert.Equal(new[] { "b", "a", "extra" }, Strings(columns, "COLUMN_NAME")); Assert.Equal(new[] { 1, 2, 3 }, Ordinals(columns));
        Assert.True((bool)columns.Rows[0]["IS_DESCENDING"]); Assert.True((bool)columns.Rows[0]["NULLS_FIRST"]);
        Assert.False((bool)columns.Rows[1]["IS_DESCENDING"]); Assert.False((bool)columns.Rows[1]["NULLS_FIRST"]);
        Assert.Equal("C", columns.Rows[0]["COLLATION_NAME"]); Assert.Equal("pg_catalog", columns.Rows[0]["COLLATION_SCHEMA"]);
        Assert.Equal("text_pattern_ops", columns.Rows[0]["OPERATOR_CLASS_NAME"]);
        Assert.True((bool)columns.Rows[2]["IS_INCLUDED"]);
        foreach (var field in new[] { "COLUMN_EXPRESSION", "IS_DESCENDING", "NULLS_FIRST", "COLLATION_NAME", "OPERATOR_CLASS_NAME" })
            Assert.Equal(DBNull.Value, columns.Rows[2][field]);
        using var expression = await Schema("IndexColumns", [null, schema, "kb_rel_indexes", "kb_rel_expression"]);
        Assert.Equal(DBNull.Value, expression.Rows[0]["COLUMN_NAME"]); Assert.Contains("lower", (string)expression.Rows[0]["COLUMN_EXPRESSION"]);
        Assert.Equal("extra", expression.Rows[1]["COLUMN_NAME"]); Assert.True((bool)expression.Rows[1]["IS_INCLUDED"]);
        const string oracle = "SELECT pg_catalog.pg_get_indexdef(i.indexrelid,1,true)::pg_catalog.text FROM pg_catalog.pg_index i " +
            "JOIN pg_catalog.pg_class x ON x.oid OPERATOR(pg_catalog.=) i.indexrelid " +
            "JOIN pg_catalog.pg_namespace n ON n.oid OPERATOR(pg_catalog.=) x.relnamespace " +
            "WHERE n.nspname::pg_catalog.text OPERATOR(pg_catalog.=) @schema AND x.relname::pg_catalog.text OPERATOR(pg_catalog.=) @index";
        Assert.Equal(await Scalar(connection, oracle, token, ("schema", schema), ("index", "kb_rel_expression")), expression.Rows[0]["COLUMN_EXPRESSION"]);
        using var hash = await Schema("IndexColumns", [null, schema, "kb_rel_indexes", "kb_rel_hash"]);
        Assert.Equal("a", hash.Rows[0]["COLUMN_NAME"]); Assert.Equal(DBNull.Value, hash.Rows[0]["IS_DESCENDING"]); Assert.Equal(DBNull.Value, hash.Rows[0]["NULLS_FIRST"]);
        using var selected = await Schema("IndexColumns", [null, schema, "kb_rel_indexes", index, "extra"]); Assert.Single(selected.Rows.Cast<DataRow>());
        using var absent = await Schema("Indexes", [null, schema, "kb_rel_indexes", "kb_rel_%"]); Assert.Empty(absent.Rows.Cast<DataRow>());
        using var wrongCase = await Schema("IndexColumns", [null, schema, "kb_rel_indexes", index, "B"]); Assert.Empty(wrongCase.Rows.Cast<DataRow>());
        Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token);
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        Task<DataTable> Schema(string name, string?[] restrictions) => async ? connection.GetSchemaAsync(name, restrictions, token) : Task.FromResult(connection.GetSchema(name, restrictions));
    });

    [LiveFact(writes: true)]
    public Task PgForeignKeysCanReferenceStandaloneUniqueIndexes() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_rel_standalone(a int,b int)", token);
        await Scalar(connection, "CREATE UNIQUE INDEX kb_rel_standalone_index ON kb_rel_standalone(b,a)", token);
        await Scalar(connection, "CREATE TEMP TABLE kb_rel_reference(x int,y int,CONSTRAINT kb_rel_standalone_fk FOREIGN KEY(x,y) REFERENCES kb_rel_standalone(a,b))", token);
        var schema = await TemporarySchema(connection, token);
        using var foreign = await connection.GetSchemaAsync("ForeignKeys", [null, schema, "kb_rel_reference"], token);
        Assert.Single(foreign.Rows.Cast<DataRow>()); Assert.Equal("kb_rel_standalone_index", foreign.Rows[0]["REFERENCED_INDEX_NAME"]);
        using var pairs = await connection.GetSchemaAsync("ForeignKeyColumns", [null, schema, "kb_rel_reference"], token);
        Assert.Equal(new[] { "a", "b" }, Strings(pairs, "REFERENCED_COLUMN_NAME")); Assert.Equal(new[] { 1, 2 }, Ordinals(pairs));
        using var noConstraint = await connection.GetSchemaAsync("UniqueKeys", [null, schema, "kb_rel_standalone"], token);
        Assert.Empty(noConstraint.Rows.Cast<DataRow>());
    });

    [LiveFact(writes: true)]
    public Task PgRelationalCollectionsRespectTransactionLocalRoleVisibility() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_rel_private(a int PRIMARY KEY,b int UNIQUE,CONSTRAINT kb_rel_private_fk FOREIGN KEY(b) REFERENCES kb_rel_private(a))", token);
        var schema = await TemporarySchema(connection, token);
        var names = new[] { "PrimaryKeys", "UniqueKeys", "ForeignKeys", "ForeignKeyColumns", "Indexes", "IndexColumns" };
        foreach (var name in names)
        {
            using var visible = connection.GetSchema(name, [null, schema, "kb_rel_private"]); Assert.NotEmpty(visible.Rows.Cast<DataRow>());
        }
        using var transaction = await connection.BeginTransactionAsync(token);
        await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        foreach (var name in names)
        {
            using var hidden = await connection.GetSchemaAsync(name, [null, schema, "kb_rel_private"], token); Assert.Empty(hidden.Rows.Cast<DataRow>());
        }
        var denied = await Assert.ThrowsAsync<KingbaseException>(() => Scalar(connection, "SELECT a FROM pg_temp.kb_rel_private", token));
        Assert.Equal("42501", denied.SqlState); await transaction.RollbackAsync(token);
        using var recovered = await connection.GetSchemaAsync("PrimaryKeys", [null, schema, "kb_rel_private"], token); Assert.Single(recovered.Rows.Cast<DataRow>());
    });

    static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    static IEnumerable<string> Strings(DataTable table, string name) => table.Rows.Cast<DataRow>().Select(row => (string)row[name]);
    static IEnumerable<int> Ordinals(DataTable table) => table.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]);
    static async Task<string> TemporarySchema(KingbaseConnection connection, CancellationToken token) =>
        Assert.IsType<string>(await Scalar(connection, "SELECT nspname::pg_catalog.text FROM pg_catalog.pg_namespace WHERE oid OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema()", token));
    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        using var command = new KingbaseCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return await command.ExecuteScalarAsync(token);
    }
}
