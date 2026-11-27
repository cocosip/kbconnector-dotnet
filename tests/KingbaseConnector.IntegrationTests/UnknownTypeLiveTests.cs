using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

// T27: a catalogued type without a codec keeps the explicit text path, including the server's text input.
public sealed class UnknownTypeLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task BuiltInTypesWithoutCodecsReadAsText(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            await using var command = new KingbaseCommand(
                "SELECT '(1,2)'::pg_catalog.point, '192.168.1.1/24'::pg_catalog.inet, NULL::pg_catalog.point", connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.Equal("(1,2)", reader.GetString(0));
            Assert.Equal("point", reader.GetDataTypeName(0));
            Assert.Equal("192.168.1.1/24", reader.GetString(1));
            Assert.Equal("inet", reader.GetDataTypeName(1));
            Assert.True(reader.IsDBNull(2));
            Assert.Same(DBNull.Value, reader.GetValue(2));
            Assert.Throws<InvalidCastException>(() => reader.GetInt32(0));
        });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task UnknownTypeParametersRoundTrip(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            await Execute(connection, "CREATE TEMPORARY TABLE t27_unknown (id pg_catalog.int4, p pg_catalog.point)", token);
            await using var insert = new KingbaseCommand("INSERT INTO t27_unknown (id, p) VALUES (1, :p)", connection);
            insert.Parameters.Add(new KingbaseParameter("p", "(3,4)") { DataTypeName = "pg_catalog.point" });
            await insert.ExecuteNonQueryAsync(token);
            await using var select = new KingbaseCommand(
                "SELECT p::text, p IS NOT NULL FROM t27_unknown WHERE id = 1", connection);
            await using var reader = await select.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal("(3,4)", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
        });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task DomainColumnsReadThroughTheirBaseType(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            // A domain outlives the session, so clear leftovers from an interrupted run before creating it again.
            await Execute(connection, "DROP DOMAIN IF EXISTS kb_t27_posint", token);
            try
            {
                await Execute(connection, "CREATE DOMAIN kb_t27_posint AS pg_catalog.int4", token);
                await Execute(connection, "CREATE TEMPORARY TABLE t27_domain (id pg_catalog.int4, d kb_t27_posint)", token);
                await Execute(connection, "INSERT INTO t27_domain VALUES (1, 42), (2, NULL)", token);
                await using var command = new KingbaseCommand("SELECT d FROM t27_domain ORDER BY id", connection);
                await using var reader = await command.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                // The server reports the base OID for a domain column, so the base codec applies end to end.
                Assert.Equal(typeof(int), reader.GetFieldType(0));
                Assert.Equal(42, reader.GetInt32(0));
                Assert.Throws<InvalidCastException>(() => reader.GetString(0));
                Assert.True(await reader.ReadAsync(token));
                Assert.True(reader.IsDBNull(0));
            }
            finally
            {
                // The domain can only go once the temporary table that uses it is gone.
                await Execute(connection, "DROP TABLE IF EXISTS t27_domain", token);
                await Execute(connection, "DROP DOMAIN IF EXISTS kb_t27_posint", token);
            }
        });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ArrayColumnsStayUnsupported(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            await using var command = new KingbaseCommand("SELECT '{1,2}'::pg_catalog._int4", connection);
            await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync(token));
        });

    static async Task Execute(KingbaseConnection connection, string sql, CancellationToken token)
    {
        await using var command = new KingbaseCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    static async Task<KingbaseConnection> Open(KingbaseConnectionStringBuilder settings, CancellationToken token)
    {
        var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        return connection;
    }
}
