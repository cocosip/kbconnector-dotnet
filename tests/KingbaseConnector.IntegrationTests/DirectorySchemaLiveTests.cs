using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class DirectorySchemaLiveTests
{
    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgDirectoriesMatchPublicCatalogFactsAndExactNames(bool async) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var directory = await connection.GetSchemaAsync(token); Assert.Equal(29, directory.Rows.Count);
        using var restrictions = connection.GetSchema("Restrictions"); Assert.Equal(87, restrictions.Rows.Count);
        foreach (var alias in new[] { "Schemata", "PrimaryKey" })
        {
            Assert.Single(directory.Rows.Cast<DataRow>(), row => (string)row[0] == alias);
            Assert.Equal(alias == "Schemata" ? 2 : 4, restrictions.Rows.Cast<DataRow>().Count(row => (string)row[0] == alias));
        }
        foreach (var name in new[] { "Databases", "Users" })
        {
            using var facts = await Schema(name, null); var expected = await Oracle(connection, name, token);
            Assert.NotEmpty(expected); Assert.Equal(expected, facts.Rows.Cast<DataRow>().Select(row => row.ItemArray).ToArray());
            Assert.DoesNotContain(facts.Columns.Cast<DataColumn>(), column => column.ColumnName.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                column.ColumnName.Contains("CONFIG", StringComparison.OrdinalIgnoreCase) || column.ColumnName.Contains("ACL", StringComparison.OrdinalIgnoreCase));
            var selectedName = name == "Databases" ? connection.Database : Assert.IsType<string>(await Scalar(connection, "SELECT session_user::pg_catalog.text", token));
            using var selected = await Schema(name.ToLowerInvariant(), [selectedName]); Assert.Equal(name, selected.TableName);
            Assert.Equal(selectedName, Assert.Single(selected.Rows.Cast<DataRow>())[0]);
            using var nullFilter = await Schema(name, [null]); Assert.Equal(expected.Length, nullFilter.Rows.Count);
            foreach (var missing in new[] { "", "kb_absent_%_'汉😀 OR 1=1 --" })
            { using var empty = await Schema(name, [missing]); Assert.Empty(empty.Rows.Cast<DataRow>()); }
            var alternative = selectedName.ToUpperInvariant() == selectedName ? selectedName.ToLowerInvariant() : selectedName.ToUpperInvariant();
            if (alternative != selectedName)
            {
                using var exactCase = await Schema(name, [alternative]);
                Assert.Equal(expected.Count(row => (string)row[0]! == alternative), exactCase.Rows.Count);
            }
            facts.Rows.Clear(); using var fresh = await Schema(name, null); Assert.Equal(expected.Length, fresh.Rows.Count);
        }
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
        Task<DataTable> Schema(string name, string?[]? filters) => async ? connection.GetSchemaAsync(name, filters, token) : Task.FromResult(connection.GetSchema(name, filters));
    });

    [LiveFact(writes: true)]
    public Task PgDirectoriesRemainPublicUnderRestrictedRoleAndRollback() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        var before = Assert.IsType<string>(await Scalar(connection, "SELECT current_user::pg_catalog.text", token));
        using var transaction = await connection.BeginTransactionAsync(token);
        await Scalar(connection, "SET LOCAL ROLE pg_monitor", token);
        foreach (var name in new[] { "Databases", "Users" })
        {
            using var facts = await connection.GetSchemaAsync(name, token);
            Assert.Equal(await Oracle(connection, name, token), facts.Rows.Cast<DataRow>().Select(row => row.ItemArray).ToArray());
            if (name == "Users") Assert.DoesNotContain(facts.Rows.Cast<DataRow>(), row => (string)row[0] == "pg_monitor");
        }
        var denied = await Assert.ThrowsAsync<KingbaseException>(() => Scalar(connection, "SELECT 1 FROM pg_catalog.pg_authid LIMIT 1", token));
        Assert.Equal("42501", denied.SqlState); await transaction.RollbackAsync(token);
        Assert.Equal(before, await Scalar(connection, "SELECT current_user::pg_catalog.text", token));
        using var recovered = connection.GetSchema("Users", [before]); Assert.Single(recovered.Rows.Cast<DataRow>());
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    static async Task<object?[][]> Oracle(KingbaseConnection connection, string name, CancellationToken token)
    {
        var sql = name == "Databases" ?
            "SELECT d.datname::pg_catalog.text, r.rolname::pg_catalog.text, pg_catalog.pg_encoding_to_char(d.encoding)::pg_catalog.text, " +
            "d.datcollate::pg_catalog.text, d.datctype::pg_catalog.text, d.datistemplate, d.datallowconn, d.datconnlimit, " +
            "pg_catalog.has_database_privilege(d.oid,'CONNECT') FROM pg_catalog.pg_database d JOIN pg_catalog.pg_roles r " +
            "ON r.oid OPERATOR(pg_catalog.=) d.datdba ORDER BY d.datname::pg_catalog.text COLLATE pg_catalog.\"C\"" :
            "SELECT rolname::pg_catalog.text, rolsuper, rolinherit, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolconnlimit " +
            "FROM pg_catalog.pg_roles WHERE rolcanlogin ORDER BY rolname::pg_catalog.text COLLATE pg_catalog.\"C\"";
        using var command = new KingbaseCommand(sql, connection); using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<object?[]>();
        while (await reader.ReadAsync(token)) { var row = new object[reader.FieldCount]; reader.GetValues(row); result.Add(row); }
        return result.ToArray();
    }

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }
}
