using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class UdtSchemaLiveTests
{
    const string Enum = "Mood_%'汉";
    const string Number = "Number_%'汉";
    const string Text = "Text_%'汉";
    const string Inner = "Inner_%'汉";
    const string Outer = "Outer_%'汉";
    const string Check = "Check_%'汉";
    static readonly string[] Collections = ["UserDefinedTypes", "TypeAttributes", "EnumLabels", "DomainConstraints"];

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgUdtMetadataPreservesLabelsDomainFactsAndCompositeSlots(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token); var schema = await Fixtures(connection, token);
        using var directory = await Schema("UserDefinedTypes", [connection.Database, schema]); Assert.Equal(5, directory.Rows.Count);
        Assert.Equal(new[] { Enum, Number, Text, Inner, Outer }.Order(), directory.Rows.Cast<DataRow>().Select(row => (string)row["UDT_NAME"]).Order());
        using (var command = new KingbaseCommand("SELECT t.typname::pg_catalog.text,t.typtype::pg_catalog.text,pg_catalog.pg_get_userbyid(t.typowner)::pg_catalog.text, " +
            "bn.nspname::pg_catalog.text,bt.typname::pg_catalog.text FROM pg_catalog.pg_type t LEFT JOIN pg_catalog.pg_class c ON c.oid OPERATOR(pg_catalog.=) t.typrelid " +
            "LEFT JOIN pg_catalog.pg_type bt ON bt.oid OPERATOR(pg_catalog.=) t.typbasetype LEFT JOIN pg_catalog.pg_namespace bn ON bn.oid OPERATOR(pg_catalog.=) bt.typnamespace " +
            "WHERE t.typnamespace OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema() AND (t.typtype OPERATOR(pg_catalog.=) 'd'::pg_catalog.\"char\" OR " +
            "t.typtype OPERATOR(pg_catalog.=) 'e'::pg_catalog.\"char\" OR c.relkind OPERATOR(pg_catalog.=) 'c'::pg_catalog.\"char\")", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            var count = 0;
            while (reader.Read())
            {
                var row = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row["UDT_NAME"] == reader.GetString(0));
                Assert.Equal(reader.GetString(1), row["TYPE_KIND_CODE"]); Assert.Equal(reader.GetString(2), row["OWNER"]);
                Assert.Equal(reader.GetValue(3), row["BASE_TYPE_SCHEMA"]); Assert.Equal(reader.GetValue(4), row["BASE_TYPE_NAME"]); count++;
            }
            Assert.Equal(directory.Rows.Count, count);
        }
        var domain = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row["UDT_NAME"] == Number);
        Assert.Equal("DOMAIN", domain["TYPE_KIND"]); Assert.Equal("d", domain["TYPE_KIND_CODE"]);
        Assert.Equal("pg_catalog", domain["BASE_TYPE_SCHEMA"]); Assert.Equal("numeric", domain["BASE_TYPE_NAME"]);
        Assert.False((bool)domain["DOMAIN_IS_NULLABLE"]); Assert.Equal(12, domain["NUMERIC_PRECISION"]); Assert.Equal(2, domain["NUMERIC_SCALE"]);
        Assert.Equal(await Scalar(connection, "SELECT typdefault::pg_catalog.text FROM pg_catalog.pg_type WHERE " + TypePredicate(Number), token), domain["DOMAIN_DEFAULT"]);
        var text = Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row["UDT_NAME"] == Text);
        Assert.Equal(8L, text["CHARACTER_MAXIMUM_LENGTH"]); Assert.Equal("C", text["COLLATION_NAME"]); Assert.True((bool)text["DOMAIN_IS_NULLABLE"]);
        foreach (var row in directory.Rows.Cast<DataRow>().Where(row => (string)row["TYPE_KIND_CODE"] != "d"))
            foreach (var field in new[] { "BASE_TYPE_NAME", "DOMAIN_DEFAULT", "DOMAIN_IS_NULLABLE", "NUMERIC_PRECISION", "COLLATION_NAME" }) Assert.Equal(DBNull.Value, row[field]);
        using var labels = await Schema("EnumLabels", [null, schema, Enum]);
        Assert.Equal(new[] { "", "汉😀_%'label", "z", "middle", "a" }, labels.Rows.Cast<DataRow>().Select(row => (string)row["ENUM_LABEL"]));
        Assert.Equal(Enumerable.Range(1, 5), labels.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
        using var emptyLabel = await Schema("EnumLabels", [null, schema, Enum, ""]); Assert.Equal("", Assert.Single(emptyLabel.Rows.Cast<DataRow>())["ENUM_LABEL"]);
        using var allLabels = await Schema("EnumLabels", [null, schema, Enum, null]); Assert.Equal(5, allLabels.Rows.Count);
        using var middleLabel = await Schema("EnumLabels", [null, schema, Enum, "z"]); Assert.Equal(3, Assert.Single(middleLabel.Rows.Cast<DataRow>())["ORDINAL_POSITION"]);
        using var attributes = await Schema("TypeAttributes", [null, schema, Outer]); Assert.Equal(5, attributes.Rows.Count);
        Assert.Equal(new[] { 1, 3, 4, 5, 6 }, attributes.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
        Assert.Equal(new[] { "first", "array", "nested", "domain", "label" }, attributes.Rows.Cast<DataRow>().Select(row => (string)row["ATTRIBUTE_NAME"]));
        Assert.Equal(9, attributes.Rows[0]["NUMERIC_PRECISION"]); Assert.Equal(3, attributes.Rows[0]["NUMERIC_SCALE"]);
        Assert.Equal("_int4", attributes.Rows[1]["ATTRIBUTE_UDT_NAME"]); Assert.Equal(Inner, attributes.Rows[2]["ATTRIBUTE_UDT_NAME"]);
        Assert.Equal(Number, attributes.Rows[3]["ATTRIBUTE_UDT_NAME"]); Assert.False((bool)attributes.Rows[3]["IS_NULLABLE"]);
        Assert.Equal(7L, attributes.Rows[4]["CHARACTER_MAXIMUM_LENGTH"]); Assert.Equal("C", attributes.Rows[4]["COLLATION_NAME"]);
        using var constraints = await Schema("DomainConstraints", [null, schema, Number]); Assert.Equal(2, constraints.Rows.Count);
        var validated = Assert.Single(constraints.Rows.Cast<DataRow>(), row => (string)row["CONSTRAINT_NAME"] == Check);
        Assert.Equal("CHECK", validated["CONSTRAINT_TYPE"]); Assert.True((bool)validated["IS_VALIDATED"]);
        Assert.False((bool)validated["IS_DEFERRABLE"]); Assert.False((bool)validated["INITIALLY_DEFERRED"]);
        Assert.Contains("VALUE", Assert.IsType<string>(validated["DEFINITION"]));
        Assert.False((bool)Assert.Single(constraints.Rows.Cast<DataRow>(), row => (string)row["CONSTRAINT_NAME"] == "pending")["IS_VALIDATED"]);
        using (var command = new KingbaseCommand("SELECT c.conname::pg_catalog.text,pg_catalog.pg_get_constraintdef(c.oid,true)::pg_catalog.text,c.convalidated " +
            "FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_type t ON t.oid OPERATOR(pg_catalog.=) c.contypid WHERE " + TypePredicate(Number), connection))
        using (var reader = await command.ExecuteReaderAsync(token))
            while (reader.Read())
            {
                var row = Assert.Single(constraints.Rows.Cast<DataRow>(), row => (string)row["CONSTRAINT_NAME"] == reader.GetString(0));
                Assert.Equal(reader.GetString(1), row["DEFINITION"]); Assert.Equal(reader.GetBoolean(2), row["IS_VALIDATED"]);
            }
        using var noSynthetic = await Schema("DomainConstraints", [null, schema, Text]); Assert.Empty(noSynthetic.Rows.Cast<DataRow>());
        foreach (var name in Collections)
        {
            foreach (var missing in new[] { "", "kb_absent_%_'汉😀 OR 1=1 --", Outer.ToUpperInvariant() })
            { using var absent = await Schema(name, [null, schema, missing]); Assert.Empty(absent.Rows.Cast<DataRow>()); }
            using var selected = await Schema(name.ToLowerInvariant(), [null, schema]); var count = selected.Rows.Count;
            selected.Rows.Clear(); using var fresh = await Schema(name, [null, schema]); Assert.Equal(count, fresh.Rows.Count);
        }
        await transaction.RollbackAsync(token);
        using var rolledBack = await Schema("UserDefinedTypes", [null, schema]); Assert.Empty(rolledBack.Rows.Cast<DataRow>());
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        Task<DataTable> Schema(string name, string?[] filters) => async ? connection.GetSchemaAsync(name, filters, token) : Task.FromResult(connection.GetSchema(name, filters));
    });

    [LiveFact(writes: true)]
    public Task PgTypeUsageVisibilityKeepsOwnerConstraintDefinitionsPrivate() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token); var schema = await Fixtures(connection, token);
        await Scalar(connection, "REVOKE ALL ON TYPE pg_temp." + Quote(Enum) + ", pg_temp." + Quote(Outer) + " FROM PUBLIC", token);
        await Scalar(connection, "GRANT USAGE ON TYPE pg_temp." + Quote(Enum) + " TO pg_monitor", token);
        await Scalar(connection, "REVOKE ALL ON DOMAIN pg_temp." + Quote(Number) + " FROM PUBLIC", token);
        await Scalar(connection, "GRANT USAGE ON DOMAIN pg_temp." + Quote(Number) + " TO pg_monitor", token);
        await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        using var types = connection.GetSchema("UserDefinedTypes", [null, schema]); Assert.Equal(4, types.Rows.Count);
        Assert.DoesNotContain(types.Rows.Cast<DataRow>(), row => (string)row["UDT_NAME"] == Outer);
        using var hidden = await connection.GetSchemaAsync("TypeAttributes", [null, schema, Outer], token); Assert.Empty(hidden.Rows.Cast<DataRow>());
        using var labels = connection.GetSchema("EnumLabels", [null, schema, Enum]); Assert.Equal(5, labels.Rows.Count);
        using var constraints = await connection.GetSchemaAsync("DomainConstraints", [null, schema, Number], token); Assert.Equal(2, constraints.Rows.Count);
        Assert.All(constraints.Rows.Cast<DataRow>(), row => Assert.Equal(DBNull.Value, row["DEFINITION"]));
        await transaction.RollbackAsync(token); Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact(writes: true)]
    public Task PgUdtMetadataExcludesCommittedOtherSessionTemporaryTypes() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var owner = new KingbaseConnection(settings.ConnectionString); await owner.OpenAsync(token);
        using var transaction = await owner.BeginTransactionAsync(token); var schema = await Fixtures(owner, token); await transaction.CommitAsync(token);
        using var observer = new KingbaseConnection(settings.ConnectionString); await observer.OpenAsync(token);
        Assert.True(Convert.ToInt64(await Scalar(observer, "SELECT pg_catalog.count(*) FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n " +
            "ON n.oid OPERATOR(pg_catalog.=) t.typnamespace WHERE n.nspname::pg_catalog.text OPERATOR(pg_catalog.=) '" + schema.Replace("'", "''") + "'", token)) > 5);
        foreach (var name in Collections)
        { using var hidden = await observer.GetSchemaAsync(name, [null, schema], token); Assert.Empty(hidden.Rows.Cast<DataRow>()); }
    });

    static async Task<string> Fixtures(KingbaseConnection connection, CancellationToken token)
    {
        await Scalar(connection, "CREATE TEMP TABLE kb_udt_anchor (id integer)", token);
        await Scalar(connection, "CREATE TYPE pg_temp." + Quote(Enum) + " AS ENUM ('','z','a')", token);
        await Scalar(connection, "ALTER TYPE pg_temp." + Quote(Enum) + " ADD VALUE '汉😀_%''label' BEFORE 'z'", token);
        await Scalar(connection, "ALTER TYPE pg_temp." + Quote(Enum) + " ADD VALUE 'middle' AFTER 'z'", token);
        await Scalar(connection, "CREATE DOMAIN pg_temp." + Quote(Number) + " AS numeric(12,2) DEFAULT 42 NOT NULL CONSTRAINT " + Quote(Check) + " CHECK (VALUE > 0)", token);
        await Scalar(connection, "ALTER DOMAIN pg_temp." + Quote(Number) + " ADD CONSTRAINT pending CHECK (VALUE < 100) NOT VALID", token);
        await Scalar(connection, "CREATE DOMAIN pg_temp." + Quote(Text) + " AS varchar(8) COLLATE \"C\" DEFAULT 'hi'", token);
        await Scalar(connection, "CREATE TYPE pg_temp." + Quote(Inner) + " AS (name text,val integer)", token);
        await Scalar(connection, "CREATE TYPE pg_temp." + Quote(Outer) + " AS (first numeric(9,3),drop_me text,\"array\" integer[],nested pg_temp." + Quote(Inner) +
            ",\"domain\" pg_temp." + Quote(Number) + ",label varchar(7) COLLATE \"C\")", token);
        await Scalar(connection, "ALTER TYPE pg_temp." + Quote(Outer) + " DROP ATTRIBUTE drop_me", token);
        return Assert.IsType<string>(await Scalar(connection, "SELECT nspname::pg_catalog.text FROM pg_catalog.pg_namespace " +
            "WHERE oid OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema()", token));
    }
    static string TypePredicate(string name) => "typnamespace OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema() AND typname::pg_catalog.text OPERATOR(pg_catalog.=) '" + name.Replace("'", "''") + "'";
    static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }
}
