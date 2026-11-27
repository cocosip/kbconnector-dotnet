using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

// T27: an explicit catalog reload makes a dropped and recreated type visible without reopening the connection.
public sealed class ReloadTypesLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ReloadTypesSeesARecreatedType(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            await Execute(connection, "DROP TABLE IF EXISTS t27_reload", token);
            await Execute(connection, "DROP DOMAIN IF EXISTS kb_t27_dom", token);
            try
            {
                await Execute(connection, "CREATE DOMAIN kb_t27_dom AS pg_catalog.int4", token);
                await connection.ReloadTypesAsync(token);
                var name = await Qualify(connection, "kb_t27_dom", token);
                Assert.Equal(7, await RoundTrip(connection, name, 7, token));
                // Recreate the domain on a wider base type: only a re-read catalog can encode the int8 value.
                await Execute(connection, "DROP TABLE IF EXISTS t27_reload", token);
                await Execute(connection, "DROP DOMAIN IF EXISTS kb_t27_dom", token);
                await Execute(connection, "CREATE DOMAIN kb_t27_dom AS pg_catalog.int8", token);
                // The stale catalog still reports int4, so the wide value cannot even be encoded yet.
                await Assert.ThrowsAsync<InvalidCastException>(() => RoundTrip(connection, name, 5_000_000_000L, token));
                await connection.ReloadTypesAsync(token);
                Assert.Equal(5_000_000_000L, await RoundTrip(connection, name, 5_000_000_000L, token));
                // The same connection keeps working after the reload.
                await using var probe = new KingbaseCommand("SELECT pg_catalog.count(*) FROM pg_catalog.pg_type", connection);
                Assert.IsType<long>(await probe.ExecuteScalarAsync(token));
            }
            finally
            {
                await Execute(connection, "DROP TABLE IF EXISTS t27_reload", token);
                await Execute(connection, "DROP DOMAIN IF EXISTS kb_t27_dom", token);
            }
        });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ReloadTypesRequiresAnIdleConnection(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            await using var command = new KingbaseCommand("SELECT 1", connection);
            await using (var reader = await command.ExecuteReaderAsync(token))
                await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ReloadTypesAsync(token));
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ReloadTypesAsync(token));
                await transaction.RollbackAsync(token);
            }
            await connection.ReloadTypesAsync(token);
            Assert.Equal(ConnectionState.Open, connection.State);
        });

    // Inserts one value through a parameter typed as the domain and reads it back through the base codec.
    static async Task<object?> RoundTrip(KingbaseConnection connection, string typeName, object value, CancellationToken token)
    {
        await Execute(connection, "DROP TABLE IF EXISTS t27_reload", token);
        await Execute(connection, "CREATE TEMPORARY TABLE t27_reload (d kb_t27_dom)", token);
        await using (var insert = new KingbaseCommand("INSERT INTO t27_reload (d) VALUES (:p)", connection))
        {
            insert.Parameters.Add(new KingbaseParameter("p", value) { DataTypeName = typeName });
            await insert.ExecuteNonQueryAsync(token);
        }
        await using var select = new KingbaseCommand("SELECT d FROM t27_reload", connection);
        return await select.ExecuteScalarAsync(token);
    }

    // The catalog keys a user type by its schema, so ask the server for the real qualified name.
    static async Task<string> Qualify(KingbaseConnection connection, string typeName, CancellationToken token)
    {
        await using var command = new KingbaseCommand(
            "SELECT n.nspname, t.typname FROM sys_catalog.sys_type AS t " +
            "JOIN sys_catalog.sys_namespace AS n ON n.oid = t.typnamespace WHERE t.typname = :name", connection);
        command.Parameters.Add(new KingbaseParameter("name", typeName) { DataTypeName = "pg_catalog.name" });
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return reader.GetString(0) + "." + reader.GetString(1);
    }

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
