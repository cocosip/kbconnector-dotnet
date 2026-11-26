using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class EnumSetLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task CatalogEnumAndSetRowsAreReadableAsStrings(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand(
            "SELECT t.typname, t.typtype, t.typcategory, t.typlen FROM pg_catalog.pg_type t " +
            "WHERE t.typnamespace = 'information_schema'::pg_catalog.regnamespace AND t.typtype IN ('e','s') ORDER BY t.typname", connection);
        using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            Assert.False(string.IsNullOrEmpty(reader.GetString(0)));
            Assert.Contains(reader.GetString(1), new[] { "e", "s" });
            Assert.Contains(reader.GetString(2), new[] { "E", "M" });
            Assert.True(reader.GetInt16(3) > 0);
        }
        // Only the MySQL-compatible profile defines the information_schema enum/SET types on the tested servers.
        Assert.Equal(mode == KingbaseCompatibilityMode.MySql, rows > 0);
    });

    [LiveFact]
    public Task MySqlInformationSchemaColumnsExposeEnumAndSetValues() => LiveProfile.Run(KingbaseCompatibilityMode.MySql, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand(
            "SELECT ROUTINE_TYPE, SQL_DATA_ACCESS, SECURITY_TYPE, SQL_MODE, ROUTINE_NAME FROM information_schema.routines WHERE ROUTINE_NAME = 'now' LIMIT 1", connection);
        using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal("routine_type_enum", reader.GetDataTypeName(0));
        Assert.Equal("sql_data_access_enum", reader.GetDataTypeName(1));
        Assert.Equal("security_type_enum", reader.GetDataTypeName(2));
        Assert.Equal("sql_mode_set", reader.GetDataTypeName(3));
        Assert.All(new[] { 0, 1, 2, 3 }, ordinal => Assert.Equal(typeof(string), reader.GetFieldType(ordinal)));
        Assert.True(reader.Read());
        Assert.Equal("FUNCTION", reader.GetString(0));
        Assert.Equal("MODIFIES SQL DATA", reader.GetString(1));
        Assert.Equal("INVOKER", reader.GetString(2));
        Assert.Contains("STRICT_TRANS_TABLES", reader.GetString(3).Split(','));
        Assert.Equal("now", reader.GetString(4));
        Assert.Equal(reader.GetString(0), reader.GetProviderSpecificValue(0));
        Assert.False(reader.Read());
    });

    [LiveFact]
    public Task MySqlEnumAndSetKeepTheirEmptyLabelAndTypedNull() => LiveProfile.Run(KingbaseCompatibilityMode.MySql, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var command = new KingbaseCommand(
            "SELECT :key::information_schema.column_key_enum::pg_catalog.text AS value", connection))
        {
            command.Parameters.Add(new KingbaseParameter("key", "") { DataTypeName = "information_schema.column_key_enum" });
            await command.PrepareAsync(token);
            Assert.Equal("", await command.ExecuteScalarAsync(token));
            command.Parameters[0].Value = "PRI";
            Assert.Equal("PRI", await command.ExecuteScalarAsync(token));
            command.Parameters[0].Value = "NOT_A_KEY";
            var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
            Assert.Equal("22P02", error.SqlState);
        }
        using (var command = new KingbaseCommand(
            "SELECT NULL::information_schema.column_key_enum AS missing, NULL::information_schema.sql_mode_set AS modes", connection))
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.Equal("column_key_enum", reader.GetDataTypeName(0));
            Assert.Equal("sql_mode_set", reader.GetDataTypeName(1));
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(1) && reader.GetValue(1) is DBNull);
            Assert.False(reader.Read());
        }
    });

    [LiveFact]
    public Task MySqlEnumAndSetParametersUseTheirCatalogTypes() => LiveProfile.Run(KingbaseCompatibilityMode.MySql, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using (var command = new KingbaseCommand(
            "SELECT ROUTINE_NAME FROM information_schema.routines WHERE ROUTINE_TYPE = :kind::information_schema.routine_type_enum LIMIT 1", connection))
        {
            var parameter = new KingbaseParameter("kind", "FUNCTION") { DataTypeName = "information_schema.routine_type_enum" };
            command.Parameters.Add(parameter);
            await command.PrepareAsync(token);
            Assert.IsType<string>(await command.ExecuteScalarAsync(token));
            // The server owns the label vocabulary: an unknown label fails as a data exception, not client-side.
            parameter.Value = "NOT_A_LABEL";
            var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
            Assert.Equal("22P02", error.SqlState);
            parameter.Value = "PROCEDURE";
            using (var reader = await command.ExecuteReaderAsync(token))
                while (reader.Read()) Assert.IsType<string>(reader.GetValue(0));
        }
        using (var command = new KingbaseCommand(
            "SELECT count(*) FROM information_schema.routines WHERE SQL_MODE = :modes::information_schema.sql_mode_set", connection))
        {
            command.Parameters.Add(new KingbaseParameter("modes",
                "ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION")
            { DataTypeName = "information_schema.sql_mode_set" });
            await command.PrepareAsync(token);
            Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) > 0);
        }
    });

    [LiveFact]
    public Task MySqlSequentialEnumColumnsStreamTheirLabels() => LiveProfile.Run(KingbaseCompatibilityMode.MySql, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand(
            "SELECT COLUMN_KEY, TABLE_NAME FROM information_schema.columns ORDER BY TABLE_NAME, COLUMN_NAME LIMIT 8", connection);
        command.CommandTimeout = 30;
        await command.PrepareAsync(token);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        Assert.Equal("column_key_enum", reader.GetDataTypeName(0));
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            Assert.False(reader.IsDBNull(0));
            Assert.Contains(reader.GetString(0), new[] { "PRI", "UNI", "MUL" });
            Assert.False(string.IsNullOrEmpty(reader.GetString(1)));
        }
        Assert.True(rows > 0);
    });
}
