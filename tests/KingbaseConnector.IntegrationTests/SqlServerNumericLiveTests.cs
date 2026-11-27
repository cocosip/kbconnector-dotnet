using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

// T06: SQL Server mode must behave like the PG mode's numeric family rather than implying SqlTypes classes.
public sealed class SqlServerNumericLiveTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PrecisionAndSignStayExact(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            Assert.Equal(1234567890123456789012345678m, await Scalar(connection, "SELECT CAST('1234567890123456789012345678' AS pg_catalog.numeric(28,0))", token));
            Assert.Equal(-123456789012345678901234567.8m, await Scalar(connection, "SELECT CAST('-123456789012345678901234567.8' AS pg_catalog.numeric(29,1))", token));
            Assert.Equal(-0.01m, await Scalar(connection, "SELECT CAST('-0.01' AS pg_catalog.numeric(10,2))", token));
            // The bare spellings must resolve to the same catalog type in this mode, not to a shadowing alias.
            Assert.Equal("numeric", await Scalar(connection, "SELECT pg_catalog.pg_typeof(1::numeric)::text", token));
            Assert.Equal("numeric", await Scalar(connection, "SELECT pg_catalog.pg_typeof(1::decimal(10,2))::text", token));
            await using var bare = new KingbaseCommand("SELECT CAST('1.25' AS numeric(10,2))", connection);
            await using var reader = await bare.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(typeof(decimal), reader.GetFieldType(0));
            Assert.Equal(1.25m, reader.GetValue(0));
        });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ValuesOutsideTheClientRangeFailExplicitly(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            await Assert.ThrowsAsync<OverflowException>(() => Scalar(connection, "SELECT CAST('12345678901234567890123456789012345678' AS pg_catalog.numeric(38,0))", token));
            await Assert.ThrowsAsync<OverflowException>(() => Scalar(connection, "SELECT CAST('0.00000000000000000000000000000000000001' AS pg_catalog.numeric(38,38))", token));
            // The buffered result is already drained, so this only proves the failure did not poison the session.
            Assert.Equal(1m, await Scalar(connection, "SELECT CAST(1 AS pg_catalog.numeric)", token));
        });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ColumnSchemaReportsPrecisionScaleAndMoneyType(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            try
            {
                var money = mode == KingbaseCompatibilityMode.SqlServer ? "sys.money" : "pg_catalog.money";
                await Execute(connection, $"CREATE TEMPORARY TABLE t06_numeric (d pg_catalog.numeric(10,2), w pg_catalog.numeric(38,10), m {money})", token);
                await using var command = new KingbaseCommand("SELECT d, w, m FROM t06_numeric", connection);
                await using var reader = await command.ExecuteReaderAsync(token);
                var columns = reader.GetColumnSchema();
                Assert.Equal(3, columns.Count);
                Assert.Equal(typeof(decimal), columns[0].DataType);
                Assert.Equal((short?)10, columns[0].NumericPrecision);
                Assert.Equal((short?)2, columns[0].NumericScale);
                Assert.Equal((short?)38, columns[1].NumericPrecision);
                Assert.Equal((short?)10, columns[1].NumericScale);
                Assert.Equal(typeof(decimal), columns[2].DataType);
                // The alias used to declare the column decides the reported name, so both spellings stay observable.
                Assert.Equal(mode == KingbaseCompatibilityMode.SqlServer ? "sys.money" : "money", columns[2].DataTypeName);
            }
            finally { await Execute(connection, "DROP TABLE IF EXISTS t06_numeric", token); }
        });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task SqlServerMoneyKeepsItsOwnScaleAndBounds(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            await using var connection = await Open(settings, token);
            // sys.money stores ten-thousandths, so the extremes keep four fraction digits.
            Assert.Equal(-922337203685477.5808m, await Scalar(connection, "SELECT CAST('922337203685477.5808'::pg_catalog.numeric * -1 AS sys.money)", token));
            Assert.Equal(922337203685477.5807m, await Scalar(connection, "SELECT CAST('922337203685477.5807' AS sys.money)", token));
            try
            {
                await Execute(connection, "CREATE TEMPORARY TABLE t06_money (m sys.money)", token);
                foreach (var value in new[] { 0.0001m, -0.0001m, 1.5m, -922337203685477.5807m, 922337203685477.5807m })
                {
                    await using var insert = new KingbaseCommand("INSERT INTO t06_money (m) VALUES (:p)", connection);
                    insert.Parameters.Add(new KingbaseParameter("p", value) { DataTypeName = "sys.money" });
                    Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
                    await using var select = new KingbaseCommand("SELECT m FROM t06_money", connection);
                    Assert.Equal(value, await select.ExecuteScalarAsync(token));
                    await Execute(connection, "TRUNCATE TABLE t06_money", token);
                }
                // One ten-thousandth beyond the largest money value is rejected by the server text conversion.
                await using var overflow = new KingbaseCommand("INSERT INTO t06_money (m) VALUES (:p)", connection);
                overflow.Parameters.Add(new KingbaseParameter("p", 922337203685477.5808m) { DataTypeName = "sys.money" });
                var failure = await Assert.ThrowsAsync<KingbaseException>(() => overflow.ExecuteNonQueryAsync(token));
                Assert.Equal("22003", failure.SqlState);
            }
            finally { await Execute(connection, "DROP TABLE IF EXISTS t06_money", token); }
        });

    static async Task<KingbaseConnection> Open(KingbaseConnectionStringBuilder settings, CancellationToken token)
    {
        var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        return connection;
    }

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    {
        await using var command = new KingbaseCommand(sql, connection);
        return await command.ExecuteScalarAsync(token);
    }

    static async Task Execute(KingbaseConnection connection, string sql, CancellationToken token)
    {
        await using var command = new KingbaseCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
