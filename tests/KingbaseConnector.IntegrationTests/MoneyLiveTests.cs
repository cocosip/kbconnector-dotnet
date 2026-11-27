using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

// T29 LIVE: pg_catalog.money and SQL Server mode's native sys.money.
public sealed class MoneyLiveTests
{
    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task MoneyColumnsReadAsDecimals(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_money_t(id int, m pg_catalog.money)", token);
        await Execute(connection, "INSERT INTO kb_money_t VALUES(1, CAST('1234.56' AS pg_catalog.money))", token);
        await Execute(connection, "INSERT INTO kb_money_t VALUES(2, CAST('-12.50' AS pg_catalog.money))", token);
        await Execute(connection, "INSERT INTO kb_money_t(id) VALUES(3)", token);
        await using var command = new KingbaseCommand("SELECT m, pg_typeof(m)::text FROM kb_money_t ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(1234.56m, reader.GetDecimal(0));
        Assert.EndsWith("money", reader.GetString(1), StringComparison.Ordinal);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(-12.5m, reader.GetDecimal(0));
        Assert.True(await reader.ReadAsync(token));
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync(token));
    });

    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task MoneyParametersRoundTrip(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_money_p(id int, m pg_catalog.money)", token);
        await using (var insert = new KingbaseCommand("INSERT INTO kb_money_p VALUES(:id, :m)", connection))
        {
            insert.Parameters.Add(new KingbaseParameter("id", 1));
            insert.Parameters.Add(new KingbaseParameter("m", 1234.56m) { KingbaseDbType = KingbaseDbType.Money });
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        }
        await using (var negative = new KingbaseCommand("INSERT INTO kb_money_p VALUES(:id, :m)", connection))
        {
            negative.Parameters.Add(new KingbaseParameter("id", 2));
            negative.Parameters.Add(new KingbaseParameter("m", -0.01m) { KingbaseDbType = KingbaseDbType.Money });
            await negative.ExecuteNonQueryAsync(token);
        }
        await using var check = new KingbaseCommand("SELECT id, m, CAST(m AS text) FROM kb_money_p ORDER BY id", connection);
        await using var reader = await check.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(1234.56m, reader.GetDecimal(1));
        Assert.Equal("$1,234.56", reader.GetString(2));
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(-0.01m, reader.GetDecimal(1));
        Assert.Equal("-$0.01", reader.GetString(2));
        Assert.False(await reader.ReadAsync(token));
    });

    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task MoneyBinaryCopyRoundTrips(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_money_copy(id int, m pg_catalog.money)", token);
        await Execute(connection, "INSERT INTO kb_money_copy VALUES(1, CAST('1234.56' AS pg_catalog.money))", token);
        await using (var export = await connection.BeginBinaryExportAsync(
            "COPY (SELECT id, m FROM kb_money_copy ORDER BY id) TO STDOUT (FORMAT binary)",
            [KingbaseDbType.Integer, KingbaseDbType.Money], token))
        {
            var row = await export.ReadRowAsync(token);
            Assert.NotNull(row);
            Assert.Equal(1, row![0]);
            Assert.Equal(1234.56m, row[1]);
            Assert.Null(await export.ReadRowAsync(token));
        }
        await Execute(connection, "CREATE TEMPORARY TABLE kb_money_import(id int, m pg_catalog.money)", token);
        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY kb_money_import(id, m) FROM STDIN (FORMAT binary)",
            [KingbaseDbType.Integer, KingbaseDbType.Money], token))
        {
            await import.WriteRowAsync([1, -12.5m], token);
            await import.WriteRowAsync([2, 0m], token);
            await import.WriteRowAsync([3, null], token);
            Assert.Equal(3UL, await import.CompleteAsync(token));
        }
        await using var check = new KingbaseCommand("SELECT m FROM kb_money_import ORDER BY id", connection);
        await using var reader = await check.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(-12.5m, reader.GetDecimal(0));
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(0m, reader.GetDecimal(0));
        Assert.True(await reader.ReadAsync(token));
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync(token));
    });

    [LiveTheory(true)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task SqlServerMoneyKeepsFourDecimals(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        await using var connection = await Open(settings, token);
        await Execute(connection, "CREATE TEMPORARY TABLE kb_money_native(id int, m money)", token);
        await Execute(connection, "INSERT INTO kb_money_native VALUES(1, CAST('1234.5678' AS money))", token);
        await using (var read = new KingbaseCommand("SELECT m, pg_typeof(m)::text FROM kb_money_native", connection))
        {
            await using var reader = await read.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(1234.5678m, reader.GetDecimal(0));
            Assert.Equal("money", reader.GetString(1));
        }
        await using (var export = await connection.BeginBinaryExportAsync(
            "COPY (SELECT m FROM kb_money_native) TO STDOUT (FORMAT binary)", [KingbaseDbType.SqlServerMoney], token))
        {
            var row = await export.ReadRowAsync(token);
            Assert.NotNull(row);
            Assert.Equal(1234.5678m, row![0]);
            Assert.Null(await export.ReadRowAsync(token));
        }
        await Execute(connection, "CREATE TEMPORARY TABLE kb_money_native_import(m money)", token);
        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY kb_money_native_import(m) FROM STDIN (FORMAT binary)", [KingbaseDbType.SqlServerMoney], token))
        {
            await import.WriteRowAsync([-1.5m], token);
            Assert.Equal(1UL, await import.CompleteAsync(token));
        }
        await using var check = new KingbaseCommand("SELECT m FROM kb_money_native_import", connection);
        Assert.Equal(-1.5m, Convert.ToDecimal(await check.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture));
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
