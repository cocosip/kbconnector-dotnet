using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class CatalogIdentifierLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task SessionIdentityUsesTheCatalogNameType(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT current_user, current_schema", connection);
        using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal("name", reader.GetDataTypeName(0));
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.True(reader.Read());
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(0)));
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(1)));
        Assert.Equal(reader.GetString(0), reader.GetValue(0));
        Assert.Equal(reader.GetString(0), reader.GetProviderSpecificValue(0));
        Assert.False(reader.Read());
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task OrdinaryCatalogQueriesReadNameAndInternalCharColumns(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);

        using (var command = new KingbaseCommand(
            "SELECT relname, relkind, xmin, cmin FROM pg_catalog.pg_class WHERE relname = 'pg_class'", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.Equal("name", reader.GetDataTypeName(0));
            Assert.Equal("char", reader.GetDataTypeName(1));
            Assert.Equal("xid", reader.GetDataTypeName(2));
            Assert.Equal("cid", reader.GetDataTypeName(3));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.Equal(typeof(string), reader.GetFieldType(1));
            Assert.Equal(typeof(uint), reader.GetFieldType(2));
            Assert.Equal(typeof(uint), reader.GetFieldType(3));
            Assert.True(reader.Read());
            Assert.Equal("pg_class", reader.GetString(0));
            Assert.Equal(1, reader.GetString(1).Length);
            Assert.Equal(reader.GetString(1), reader.GetValue(1));
            Assert.Throws<InvalidCastException>(() => reader.GetChar(1));
            Assert.Equal(reader.GetFieldValue<uint>(2), reader.GetValue(2));
            Assert.Equal(reader.GetFieldValue<uint>(3), reader.GetValue(3));
            Assert.False(reader.Read());
        }

        foreach (var sql in new[]
        {
            "SELECT datname FROM pg_catalog.pg_database WHERE datname = current_database()",
            "SELECT nspname FROM pg_catalog.pg_namespace WHERE nspname = 'pg_catalog'",
            "SELECT proname, prokind FROM pg_catalog.pg_proc WHERE proname = 'now' LIMIT 1",
            "SELECT typname, typtype, typcategory, typdelim FROM pg_catalog.pg_type WHERE typname = 'int4'"
        })
        {
            using var command = new KingbaseCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(reader.Read(), sql);
            Assert.All(Enumerable.Range(0, reader.FieldCount), ordinal =>
            {
                Assert.Equal(typeof(string), reader.GetFieldType(ordinal));
                Assert.False(string.IsNullOrEmpty(reader.GetString(ordinal)), sql);
            });
            Assert.False(reader.Read(), sql);
        }

        // "char" columns whose value is zero render as the empty string, not as NULL or a space.
        using (var command = new KingbaseCommand(
            "SELECT attname, attidentity, attgenerated FROM pg_catalog.pg_attribute WHERE attnum > 0 LIMIT 3", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.Equal("char", reader.GetDataTypeName(1));
            Assert.Equal("char", reader.GetDataTypeName(2));
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                Assert.False(string.IsNullOrEmpty(reader.GetString(0)));
                Assert.Equal("", reader.GetString(1));
                Assert.Equal("", reader.GetString(2));
                Assert.False(reader.IsDBNull(1));
            }
            Assert.Equal(3, rows);
        }
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task OidAliasTypesKeepTheirServerTextForm(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var command = new KingbaseCommand(
            "SELECT 'pg_class'::pg_catalog.regclass, 'now'::pg_catalog.regproc, 'integer'::pg_catalog.regtype, 'pg_catalog'::pg_catalog.regnamespace", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.Equal("regclass", reader.GetDataTypeName(0));
            Assert.Equal("regproc", reader.GetDataTypeName(1));
            Assert.Equal("regtype", reader.GetDataTypeName(2));
            Assert.Equal("regnamespace", reader.GetDataTypeName(3));
            Assert.True(reader.Read());
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                Assert.Equal(typeof(string), reader.GetFieldType(ordinal));
                Assert.False(string.IsNullOrWhiteSpace(reader.GetString(ordinal)));
            }
            Assert.False(reader.Read());
        }
        using var name = new KingbaseCommand("SELECT 'pg_class'::pg_catalog.regclass::pg_catalog.name", connection);
        Assert.IsType<string>(await name.ExecuteScalarAsync(token));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task IdentifierParametersUseExplicitCatalogTypesAndRejectInvalidInputLocally(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var name = new KingbaseCommand("SELECT relname FROM pg_catalog.pg_class WHERE relname = :name", connection);
        name.Parameters.Add(new KingbaseParameter("name", "pg_class") { DataTypeName = "pg_catalog.name" });
        await name.PrepareAsync(token);
        Assert.Equal("pg_class", await name.ExecuteScalarAsync(token));
        name.Parameters[0].Value = DBNull.Value;
        using (var reader = await name.ExecuteReaderAsync(token)) Assert.False(reader.Read());

        using var kind = new KingbaseCommand("SELECT relname FROM pg_catalog.pg_class WHERE relkind = :kind LIMIT 1", connection);
        kind.Parameters.Add(new KingbaseParameter("kind", "v") { DataTypeName = "pg_catalog.char" });
        await kind.PrepareAsync(token);
        Assert.IsType<string>(await kind.ExecuteScalarAsync(token));
        kind.Parameters[0].Value = "vv";
        await Assert.ThrowsAsync<FormatException>(() => kind.ExecuteScalarAsync(token));
        kind.Parameters[0].Value = "";
        using (var reader = await kind.ExecuteReaderAsync(token)) Assert.False(reader.Read());
        kind.Parameters[0].Value = DBNull.Value;
        using (var reader = await kind.ExecuteReaderAsync(token)) Assert.False(reader.Read());

        using var transactionId = new KingbaseCommand("SELECT relname FROM pg_catalog.pg_class WHERE xmin::pg_catalog.text = :x::pg_catalog.text LIMIT 1", connection);
        transactionId.Parameters.Add(new KingbaseParameter("x", 1u) { DataTypeName = "pg_catalog.xid" });
        await transactionId.PrepareAsync(token);
        using (var reader = await transactionId.ExecuteReaderAsync(token))
            while (reader.Read()) Assert.IsType<string>(reader.GetValue(0));

        // An explicit DataTypeName may be constrained by the CLR-compatible DbType because these codecs have no provider enum.
        using var constrained = new KingbaseCommand("SELECT :n::pg_catalog.name::pg_catalog.text", connection);
        var nameParameter = new KingbaseParameter("n", "pg_class") { DataTypeName = "pg_catalog.name", DbType = DbType.String };
        constrained.Parameters.Add(nameParameter);
        Assert.Equal("pg_class", await constrained.ExecuteScalarAsync(token));
        nameParameter.DbType = DbType.Int32;
        await Assert.ThrowsAsync<ArgumentException>(() => constrained.ExecuteScalarAsync(token));
        nameParameter.DbType = DbType.String;
        Assert.Equal("pg_class", await constrained.ExecuteScalarAsync(token));

        using var afterwards = new KingbaseCommand("SELECT current_user", connection);
        Assert.False(string.IsNullOrWhiteSpace((string)(await afterwards.ExecuteScalarAsync(token))!));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task IdentifierColumnsStreamAndKeepNullDistinct(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var command = new KingbaseCommand(
            "SELECT relname, NULL::pg_catalog.name, relkind::pg_catalog.text FROM pg_catalog.pg_class WHERE relname = 'pg_class'", connection))
        {
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            Assert.Equal("name", reader.GetDataTypeName(0));
            Assert.True(reader.Read());
            Assert.Equal("pg_class", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.False(reader.IsDBNull(2));
            Assert.False(reader.Read());
        }
        using (var command = new KingbaseCommand(
            "SELECT parameter_name FROM information_schema.parameters WHERE specific_schema = 'pg_catalog' LIMIT 5", connection))
        {
            using var reader = await command.ExecuteReaderAsync(token);
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            while (reader.Read()) if (!reader.IsDBNull(0)) Assert.IsType<string>(reader.GetValue(0));
        }
    });
}
