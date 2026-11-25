using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class CompatibilityPoolLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, false)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, true)]
    [InlineData(KingbaseCompatibilityMode.Oracle, false)]
    [InlineData(KingbaseCompatibilityMode.Oracle, true)]
    [InlineData(KingbaseCompatibilityMode.MySql, false)]
    [InlineData(KingbaseCompatibilityMode.MySql, true)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, false)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, true)]
    public Task DirectAndPrivatePoolsReuseCleanPhysicalSession(KingbaseCompatibilityMode mode, bool direct) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MinPoolSize = 0; settings.MaxPoolSize = 1; settings.ConnectionIdleLifetime = 0;
        settings.CompatibilityMode = direct ? KingbaseCompatibilityMode.Auto : mode;
        settings.ApplicationName = "kb_e53_" + mode + "_" + direct + "';--\\";
        settings.SearchPath = "\"kb_汉\", pg_catalog, public"; settings.Timezone = "Asia/Shanghai";
        using var source = direct ? null : new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var first = direct ? new KingbaseConnection(settings.ConnectionString) : source!.CreateConnection();
        try
        {
            await first.OpenAsync(token);
            var pid = await Scalar(first, "SELECT pg_catalog.pg_backend_pid()", token);
            var baseline = await State(first, token);
            Assert.Equal(settings.ApplicationName, baseline[0]); Assert.Equal(settings.SearchPath, baseline[1]); Assert.Equal(settings.Timezone, baseline[2]);
            using var prepared = new KingbaseCommand("SELECT :p", first); prepared.Parameters.AddWithValue("p", 42);
            await prepared.PrepareAsync(token); Assert.Equal(42, await prepared.ExecuteScalarAsync(token));
            await Execute(first, "CREATE TEMP TABLE kb_live_pool_dirty(id pg_catalog.int4)", token);
            await Execute(first, "LISTEN kb_e53_probe", token);
            await Scalar(first, "SELECT 1 FROM pg_catalog.pg_advisory_lock(214531,53)", token);
            foreach (var (name, value) in new[] { ("application_name", "kb_dirty"), ("search_path", "pg_catalog"),
                ("TimeZone", "UTC"), ("standard_conforming_strings", "off"), ("DateStyle", "ISO, DMY"),
                ("IntervalStyle", "iso_8601"), ("default_transaction_isolation", "serializable"),
                ("default_transaction_read_only", "on"), ("default_transaction_deferrable", "on") })
            {
                using var change = new KingbaseCommand("SELECT pg_catalog.set_config(:name,:value,false)", first);
                change.Parameters.AddWithValue("name", name); change.Parameters.AddWithValue("value", value);
                Assert.Equal(value, await change.ExecuteScalarAsync(token));
            }
            if (mode is KingbaseCompatibilityMode.Oracle or KingbaseCompatibilityMode.MySql)
            {
                var value = Equals(baseline[9], "ONLY_FULL_GROUP_BY") ? "" : "ONLY_FULL_GROUP_BY";
                using var change = new KingbaseCommand("SELECT pg_catalog.set_config('sql_mode',:value,false)", first);
                change.Parameters.AddWithValue("value", value); await change.ExecuteScalarAsync(token);
                Assert.NotEqual(baseline[9], await Scalar(first, "SELECT pg_catalog.current_setting('sql_mode')::pg_catalog.text", token));
            }
            using var transaction = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, readOnly: false, token);
            using (var dirty = new KingbaseCommand("INSERT INTO kb_live_pool_dirty VALUES(1)", first) { Transaction = transaction })
                Assert.Equal(1, await dirty.ExecuteNonQueryAsync(token));
            using (var error = new KingbaseCommand("SELECT pg_catalog.kb_fixture_missing_function()", first) { Transaction = transaction })
                Assert.Equal("42883", (await Assert.ThrowsAsync<KingbaseException>(() => error.ExecuteScalarAsync(token))).SqlState);
            await first.CloseAsync(); Assert.Null(transaction.Connection);
            using var second = direct ? new KingbaseConnection(settings.ConnectionString) : source!.CreateConnection();
            await second.OpenAsync(token);
            Assert.Equal(pid, await Scalar(second, "SELECT pg_catalog.pg_backend_pid()", token));
            Assert.Equal(baseline, await State(second, token));
            Assert.True(Assert.IsType<bool>(await Scalar(second, "SELECT pg_catalog.to_regclass('pg_temp.kb_live_pool_dirty') IS NULL", token)));
            Assert.Equal(0L, await Scalar(second, "SELECT CAST(count(*) AS pg_catalog.int8) FROM pg_catalog.pg_prepared_statements", token));
            Assert.Equal(0L, await Scalar(second, "SELECT CAST(count(*) AS pg_catalog.int8) FROM pg_catalog.pg_listening_channels()", token));
            Assert.False(Assert.IsType<bool>(await Scalar(second, "SELECT pg_catalog.pg_advisory_unlock(214531,53)", token)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync(token));
            prepared.Connection = second; prepared.Parameters[0].Value = 43;
            Assert.Equal(43, await prepared.ExecuteScalarAsync(token));
            await prepared.PrepareAsync(token); Assert.Equal(43, await prepared.ExecuteScalarAsync(token));
            await second.CloseAsync();
        }
        finally { if (direct) KingbaseConnection.ClearPool(first); }
    });

    static async Task<object?[]> State(KingbaseConnection connection, CancellationToken token)
    {
        var names = new[] { "application_name", "search_path", "TimeZone", "standard_conforming_strings", "DateStyle", "IntervalStyle",
            "default_transaction_isolation", "default_transaction_read_only", "default_transaction_deferrable", "sql_mode" };
        using var command = new KingbaseCommand("SELECT " + string.Join(",", names.Select(name => "CAST(pg_catalog.current_setting('" + name + "',true) AS pg_catalog.text)")), connection);
        using var reader = await command.ExecuteReaderAsync(token); Assert.True(reader.Read());
        var values = new object?[names.Length]; reader.GetValues(values!); return values;
    }
    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }
    static async Task Execute(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); await command.ExecuteNonQueryAsync(token); }
}
