using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

public sealed class LiveProfileTests
{
    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task BootstrapDiscoversModeAndActualBuiltinCatalog(KingbaseCompatibilityMode mode) =>
        LiveProfile.Run(mode, async (settings, token) =>
        {
            using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, token);
            Assert.Equal(mode, session.Identity.EffectiveMode);
            Assert.Equal(mode, session.Identity.ReportedMode);
            Assert.Equal(9, session.Identity.Version.Major);
            Assert.Equal("UTF8", session.Startup.Parameters["client_encoding"]);
            foreach (var name in new[] { "bool", "int2", "int4", "int8", "text", "bytea" })
            {
                var type = session.Catalog.ByName["pg_catalog." + name];
                Assert.Equal("pg_catalog", type.Namespace);
                Assert.NotEqual(0u, type.Oid);
            }
            Assert.Equal("1", Assert.Single(await session.Queries.ExecuteAsync("SELECT 1", token)).Rows[0][0]);
        });

    [LiveFact]
    public Task PgPublicParametersAndTypedNullRoundTrip() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT @i AS i,@t AS t,@n AS n,@b AS b,@f AS f,@d AS d", connection);
        var text = "Kingbase 中文 ' " + (char)92;
        command.Parameters.Add(new KingbaseParameter("i", 42));
        command.Parameters.Add(new KingbaseParameter("t", text));
        command.Parameters.Add(new KingbaseParameter("n", DBNull.Value) { DataTypeName = "pg_catalog.text" });
        command.Parameters.Add(new KingbaseParameter("b", new byte[] { 0, 1, 127, 255 }));
        command.Parameters.Add(new KingbaseParameter("f", true));
        command.Parameters.Add(new KingbaseParameter("d", 123.450m));
        using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(42, reader.GetInt32(0));
        Assert.Equal(text, reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(new byte[] { 0, 1, 127, 255 }, reader.GetFieldValue<byte[]>(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal(123.450m, reader.GetDecimal(5));
        Assert.False(await reader.ReadAsync(token));
    });

    [LiveFact]
    public Task PgSynchronizedQueryErrorLeavesConnectionUsable() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT 1/0", connection);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
        Assert.Equal("22012", error.SqlState);
        command.CommandText = "SELECT 42";
        Assert.Equal(42, await command.ExecuteScalarAsync(token));
    });

    [LiveFact]
    public Task PgInternalNamedStatementExecutesAndCloses() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, token);
        var oid = session.Catalog.ByName["pg_catalog.int4"].Oid;
        var statement = await session.Queries.PrepareAsync("SELECT $1", session.Catalog, [oid], token);
        foreach (var value in new[] { 42, 43 })
        {
            var results = await session.Queries.ExecutePreparedAsync(statement, session.Catalog, [new(oid, value)], token);
            Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(results).Rows[0][0]);
        }
        await session.Queries.UnprepareAsync(statement, token);
        Assert.False(statement.IsValid);
        Assert.Equal("1", Assert.Single(await session.Queries.ExecuteAsync("SELECT 1", token)).Rows[0][0]);
    });

    [LiveFact]
    public Task PgPublicPrepareValuesSharedUnprepareAndLogicalReopen() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var first = new KingbaseCommand("SELECT @p", connection);
        first.Parameters.AddWithValue("p", 42);
        using var second = new KingbaseCommand("SELECT @p", connection);
        second.Parameters.AddWithValue("p", 43);
        first.Prepare(); await second.PrepareAsync(token);
        Assert.Equal(42, first.ExecuteScalar());
        first.Parameters[0].Value = 44;
        Assert.Equal(44, await first.ExecuteScalarAsync(token));
        await first.UnprepareAsync(token);
        Assert.Equal(43, await second.ExecuteScalarAsync(token));
        await second.PrepareAsync(token);
        second.Unprepare(); second.Unprepare();
        first.Parameters[0].Value = DBNull.Value;
        first.Parameters[0].KingbaseDbType = KingbaseDbType.Integer;
        await first.PrepareAsync(token);
        Assert.Same(DBNull.Value, await first.ExecuteScalarAsync(token));
        connection.Close(); await connection.OpenAsync(token);
        first.Parameters[0].Value = 45;
        Assert.Equal(45, await first.ExecuteScalarAsync(token));
        await first.UnprepareAsync(token);
    });

    [LiveFact(writes: true)]
    public Task PgPublicPreparedSchemaErrorAndTransactionRecovery() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_live_prepared (id int)", token);
        using var command = new KingbaseCommand("SELECT id FROM kb_live_prepared", connection);
        await command.PrepareAsync(token);
        Assert.Null(await command.ExecuteScalarAsync(token));
        await Scalar(connection, "ALTER TABLE kb_live_prepared ALTER COLUMN id TYPE bigint", token);
        var schemaError = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
        Assert.Equal("0A000", schemaError.SqlState);
        // Recovery is an explicit caller action; the failed Execute is never replayed.
        await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token)) Assert.Equal(typeof(long), reader.GetFieldType(0));
        await command.UnprepareAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token);
        command.CommandText = "SELECT 1/0"; command.Transaction = transaction;
        await command.PrepareAsync(token);
        var error = await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token));
        Assert.Equal("22012", error.SqlState);
        Assert.Same(connection, transaction.Connection);
        await command.UnprepareAsync(token);
        await transaction.RollbackAsync(token);
        Assert.Equal(42, await Scalar(connection, "SELECT 42", token));
    });

    [LiveFact]
    public Task PgPoolReusesPhysicalSessionAndRestoresSettings() => LiveProfile.RunPg(async (settings, token) =>
    {
        settings.Pooling = true;
        settings.MinPoolSize = 0;
        settings.MaxPoolSize = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        int firstPid;
        object? baseline;
        using (var first = await source.OpenConnectionAsync(token))
        {
            firstPid = Assert.IsType<int>(await Scalar(first, "SELECT pg_catalog.pg_backend_pid()", token));
            baseline = await Scalar(first, "SHOW application_name", token);
            await Scalar(first, "SELECT pg_catalog.set_config('application_name','kb-live-dirty',false)", token);
            Assert.Equal("kb-live-dirty", await Scalar(first, "SHOW application_name", token));
        }
        using var second = await source.OpenConnectionAsync(token);
        Assert.Equal(firstPid, await Scalar(second, "SELECT pg_catalog.pg_backend_pid()", token));
        Assert.Equal(baseline, await Scalar(second, "SHOW application_name", token));
    });

    [LiveTheory(writes: true)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public Task PgTransactionCommitRollbackAndSavepoint(IsolationLevel isolation) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_live_transaction (id int)", connection))
            await create.ExecuteNonQueryAsync(token);
        using (var transaction = await connection.BeginTransactionAsync(isolation, token))
        {
            using var insert = new KingbaseCommand("INSERT INTO kb_live_transaction VALUES (1)", connection) { Transaction = transaction };
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
            await transaction.SaveAsync("kb_live_save", token);
            insert.CommandText = "INSERT INTO kb_live_transaction VALUES (2)";
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
            await transaction.RollbackAsync("kb_live_save", token);
            await transaction.ReleaseAsync("kb_live_save", token);
            await transaction.CommitAsync(token);
        }
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_live_transaction", token));
        using (var transaction = await connection.BeginTransactionAsync(isolation, token))
        {
            using var delete = new KingbaseCommand("DELETE FROM kb_live_transaction", connection) { Transaction = transaction };
            Assert.Equal(1, await delete.ExecuteNonQueryAsync(token));
            await transaction.RollbackAsync(token);
        }
        Assert.Equal(1, await Scalar(connection, "SELECT id FROM kb_live_transaction", token));
        // The unpooled physical connection owns the temporary table and removes it on disposal.
    });

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    {
        using var command = new KingbaseCommand(sql, connection);
        return await command.ExecuteScalarAsync(token);
    }
}
