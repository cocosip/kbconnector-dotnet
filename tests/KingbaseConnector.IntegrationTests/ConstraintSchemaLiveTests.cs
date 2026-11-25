using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class ConstraintSchemaLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgGeneralConstraintsKeepDefinitionsDependenciesAndAliases(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        const string table = "kb_cons_%_'汉\"Table", check = "check_%_'汉\"Name";
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE " + Quote(table) + "(a int NOT NULL,b int,c int,CONSTRAINT kb_cons_pk PRIMARY KEY(a,b)," +
            "CONSTRAINT kb_cons_u UNIQUE(b,c),CONSTRAINT " + Quote(check) + " CHECK(a+b>c) NO INHERIT," +
            "CONSTRAINT kb_cons_constant CHECK(true),CONSTRAINT kb_cons_fk FOREIGN KEY(b,c) REFERENCES " + Quote(table) + "(a,b) DEFERRABLE INITIALLY DEFERRED)", token);
        await Scalar(connection, "ALTER TABLE " + Quote(table) + " ADD CONSTRAINT kb_cons_unvalidated CHECK(c>0) NOT VALID", token);
        await Scalar(connection, "ALTER TABLE " + Quote(table) + " ADD CONSTRAINT kb_cons_exclusion EXCLUDE USING btree " +
            "((pg_catalog.abs(a)) WITH =,b WITH =,b WITH =) WHERE (c>0)", token);
        await Scalar(connection, "CREATE TEMP TABLE kb_cons_same(a int,CONSTRAINT " + Quote(check) + " CHECK(true))", token);
        var schema = await TemporarySchema(connection, token);
        using var constraints = await Schema("Constraints", [null, schema, table]); Assert.Equal(7, constraints.Rows.Count);
        Assert.Equal(await Scalar(connection, "SELECT count(*)::pg_catalog.int4 FROM pg_catalog.pg_constraint WHERE conrelid " +
            "OPERATOR(pg_catalog.=) @relation::pg_catalog.regclass", token, ("relation", "pg_temp." + Quote(table))), constraints.Rows.Count);
        var rows = constraints.Rows.Cast<DataRow>().ToDictionary(row => (string)row["CONSTRAINT_NAME"]);
        foreach (var (name, code, type) in new[] { (check, "c", "CHECK"), ("kb_cons_pk", "p", "PRIMARY KEY"), ("kb_cons_u", "u", "UNIQUE"),
            ("kb_cons_fk", "f", "FOREIGN KEY"), ("kb_cons_constant", "c", "CHECK"), ("kb_cons_unvalidated", "c", "CHECK"), ("kb_cons_exclusion", "x", "EXCLUSION") })
        {
            Assert.Equal(code, rows[name]["CONSTRAINT_TYPE_CODE"]); Assert.Equal(type, rows[name]["CONSTRAINT_TYPE"]);
            Assert.False(string.IsNullOrEmpty((string)rows[name]["DEFINITION"]));
            var definition = await Scalar(connection, "SELECT pg_catalog.pg_get_constraintdef(oid,true)::pg_catalog.text FROM pg_catalog.pg_constraint " +
                "WHERE conrelid OPERATOR(pg_catalog.=) @relation::pg_catalog.regclass AND conname::pg_catalog.text OPERATOR(pg_catalog.=) @name", token,
                ("relation", "pg_temp." + Quote(table)), ("name", name));
            Assert.Equal(definition, rows[name]["DEFINITION"]);
        }
        Assert.True((bool)rows[check]["NO_INHERIT"]); Assert.True((bool)rows[check]["IS_VALIDATED"]);
        Assert.False((bool)rows["kb_cons_unvalidated"]["IS_VALIDATED"]); Assert.False((bool)rows["kb_cons_unvalidated"]["NO_INHERIT"]);
        Assert.True((bool)rows["kb_cons_fk"]["IS_DEFERRABLE"]); Assert.True((bool)rows["kb_cons_fk"]["INITIALLY_DEFERRED"]);
        Assert.Contains("EXCLUDE", (string)rows["kb_cons_exclusion"]["DEFINITION"], StringComparison.OrdinalIgnoreCase);
        using var columns = await Schema("ConstraintColumns", [null, schema, table]); Assert.Equal(12, columns.Rows.Count);
        Assert.Equal(new[] { "a", "b", "c" }, Columns(check).Select(row => (string)row["COLUMN_NAME"]));
        Assert.Equal(new[] { "a", "b" }, Columns("kb_cons_pk").Select(row => (string)row["COLUMN_NAME"]));
        Assert.Equal(new[] { "b", "c" }, Columns("kb_cons_fk").Select(row => (string)row["COLUMN_NAME"]));
        Assert.Empty(Columns("kb_cons_constant"));
        Assert.Equal(new[] { 2, 3 }, Columns("kb_cons_exclusion").Select(row => (int)row["ORDINAL_POSITION"]));
        Assert.All(Columns("kb_cons_exclusion"), row => Assert.Equal("b", row["COLUMN_NAME"]));
        using var selected = await Schema("ConstraintColumns", [connection.Database, schema, table, check, "b"]); Assert.Single(selected.Rows.Cast<DataRow>());
        using var duplicates = await Schema("Constraints", [null, schema, null, check]); Assert.Equal(2, duplicates.Rows.Count);
        using var absent = await Schema("Constraints", [null, schema, table, "check_%"]); Assert.Empty(absent.Rows.Cast<DataRow>());
        using var empty = await Schema("ConstraintColumns", [null, schema, table, ""]); Assert.Empty(empty.Rows.Cast<DataRow>());
        using var wrongCase = await Schema("ConstraintColumns", [null, schema, table, check, "B"]); Assert.Empty(wrongCase.Rows.Cast<DataRow>());
        foreach (var (alias, canonical, filters) in new[] { ("sChEmAtA", "Schemas", new string?[] { null, schema }), ("pRiMaRyKeY", "PrimaryKeys", new string?[] { null, schema, table }) })
        {
            using var aliased = await Schema(alias, filters); using var original = await Schema(canonical, filters);
            Assert.Equal(canonical, aliased.TableName);
            Assert.Equal(original.Rows.Cast<DataRow>().Select(row => row.ItemArray), aliased.Rows.Cast<DataRow>().Select(row => row.ItemArray));
        }
        Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token);
        using var after = await connection.GetSchemaAsync("Constraints", [null, schema, table], token); Assert.Empty(after.Rows.Cast<DataRow>());
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        DataRow[] Columns(string name) => columns.Rows.Cast<DataRow>().Where(row => (string)row["CONSTRAINT_NAME"] == name).ToArray();
        Task<DataTable> Schema(string name, string?[] filters) => async ? connection.GetSchemaAsync(name, filters, token) : Task.FromResult(connection.GetSchema(name, filters));
    });

    [LiveFact(writes: true)]
    public Task PgGeneralConstraintsRespectRestrictedRoleAndRollback() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_cons_private(a int,CONSTRAINT kb_cons_check CHECK(a>0))", token);
        var schema = await TemporarySchema(connection, token);
        using var transaction = await connection.BeginTransactionAsync(token); await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        foreach (var name in new[] { "Constraints", "ConstraintColumns" })
        { using var hidden = await connection.GetSchemaAsync(name, [null, schema, "kb_cons_private"], token); Assert.Empty(hidden.Rows.Cast<DataRow>()); }
        var denied = await Assert.ThrowsAsync<KingbaseException>(() => Scalar(connection, "SELECT a FROM kb_cons_private", token)); Assert.Equal("42501", denied.SqlState);
        await transaction.RollbackAsync(token);
        foreach (var name in new[] { "Constraints", "ConstraintColumns" })
        { using var visible = connection.GetSchema(name, [null, schema, "kb_cons_private"]); Assert.Single(visible.Rows.Cast<DataRow>()); }
    });

    [LiveFact(writes: true)]
    public Task PgColumnOnlyGrantRevealsOwningTableConstraintFacts() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_cons_grant(a int,b int,CONSTRAINT kb_cons_check CHECK(a+b>0))", token);
        var schema = await TemporarySchema(connection, token);
        using var transaction = await connection.BeginTransactionAsync(token);
        await Scalar(connection, "GRANT SELECT(a) ON kb_cons_grant TO pg_monitor", token); await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        using var constraints = await connection.GetSchemaAsync("Constraints", [null, schema, "kb_cons_grant"], token); Assert.Single(constraints.Rows.Cast<DataRow>());
        using var columns = await connection.GetSchemaAsync("ConstraintColumns", [null, schema, "kb_cons_grant"], token);
        Assert.Equal(new[] { "a", "b" }, columns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]));
        await transaction.RollbackAsync(token);
        using var restricted = await connection.BeginTransactionAsync(token); await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        using var hidden = await connection.GetSchemaAsync("Constraints", [null, schema, "kb_cons_grant"], token); Assert.Empty(hidden.Rows.Cast<DataRow>());
        await restricted.RollbackAsync(token);
    });

    [LiveFact(writes: true)]
    public Task PgGeneralConstraintsHideOtherSessionsTemporaryTables() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var owner = new KingbaseConnection(settings.ConnectionString); await owner.OpenAsync(token);
        await Scalar(owner, "CREATE TEMP TABLE kb_cons_other(a int,CONSTRAINT kb_cons_check CHECK(a>0))", token);
        var schema = await TemporarySchema(owner, token);
        using var observer = new KingbaseConnection(settings.ConnectionString); await observer.OpenAsync(token);
        foreach (var name in new[] { "Constraints", "ConstraintColumns" })
        {
            using var visible = await owner.GetSchemaAsync(name, [null, schema, "kb_cons_other"], token); Assert.Single(visible.Rows.Cast<DataRow>());
            using var hidden = await observer.GetSchemaAsync(name, [null, schema, "kb_cons_other"], token); Assert.Empty(hidden.Rows.Cast<DataRow>());
        }
    });

    static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    static async Task<string> TemporarySchema(KingbaseConnection connection, CancellationToken token) => Assert.IsType<string>(await Scalar(connection,
        "SELECT nspname::pg_catalog.text FROM pg_catalog.pg_namespace WHERE oid OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema()", token));
    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        using var command = new KingbaseCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync(token);
    }
}
