using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.IntegrationTests;

public sealed class CompatibilityExecutionLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.Oracle, 0)]
    [InlineData(KingbaseCompatibilityMode.Oracle, 1)]
    [InlineData(KingbaseCompatibilityMode.Oracle, 2)]
    [InlineData(KingbaseCompatibilityMode.MySql, 0)]
    [InlineData(KingbaseCompatibilityMode.MySql, 1)]
    [InlineData(KingbaseCompatibilityMode.MySql, 2)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, 0)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, 1)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, 2)]
    public Task CachedPgGeneratedWritesRejectNewMode(KingbaseCompatibilityMode mode, int size) => LiveProfile.Run(mode, async (settings, token) =>
        await LiveProfile.RunPg(async (pg, pgToken) =>
        {
            using var connection = new KingbaseConnection(pg.ConnectionString); await connection.OpenAsync(pgToken);
            using var setup = new KingbaseCommand("CREATE TEMP TABLE kb_live_cached_builder(id pg_catalog.int4 PRIMARY KEY,value pg_catalog.text)", connection);
            await setup.ExecuteNonQueryAsync(pgToken);
            setup.CommandText = "INSERT INTO kb_live_cached_builder VALUES(1,'old')"; await setup.ExecuteNonQueryAsync(pgToken);
            using var select = new KingbaseCommand("SELECT id,value FROM kb_live_cached_builder", connection);
            using var adapter = new KingbaseDataAdapter(select) { ContinueUpdateOnError = true };
            using var builder = new KingbaseCommandBuilder(adapter);
            using var table = new DataTable(); adapter.Fill(table);
            var update = builder.GetUpdateCommand(); update.UpdatedRowSource = UpdateRowSource.None; adapter.UpdateCommand = update;
            connection.Close(); connection.ConnectionString = settings.ConnectionString; await connection.OpenAsync(token);
            setup.CommandText = "CREATE TEMP TABLE kb_live_cached_builder(id pg_catalog.int4 PRIMARY KEY,value pg_catalog.text)";
            await setup.ExecuteNonQueryAsync(token);
            setup.CommandText = "INSERT INTO kb_live_cached_builder VALUES(1,'old')"; await setup.ExecuteNonQueryAsync(token);
            if (size == 0) Assert.Contains("PG-generated", Assert.Throws<NotSupportedException>(() => update.ExecuteNonQuery()).Message);
            else
            {
                table.Rows[0]["value"] = "changed"; adapter.UpdateBatchSize = size;
                Assert.Equal(0, adapter.Update(table)); Assert.Contains("PG-generated", table.Rows[0].RowError);
                Assert.Equal(DataRowState.Modified, table.Rows[0].RowState);
            }
            setup.CommandText = "SELECT value FROM kb_live_cached_builder WHERE id=1";
            Assert.Equal("old", await setup.ExecuteScalarAsync(token));
        }));

    [LiveFact]
    public Task InternalMySqlDivisionByZeroReturnsNull() => LiveProfile.Run(KingbaseCompatibilityMode.MySql, async (settings, token) =>
    {
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, token);
        Assert.Null(Assert.Single(await session.Queries.ExecuteAsync("SELECT 1/0", token)).Rows[0][0]);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task InternalStringEscapeSettingFacts(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, token);
        foreach (var on in new[] { true, false })
        {
            await session.Queries.ExecuteAsync("SET standard_conforming_strings=" + (on ? "on" : "off"), token);
            Assert.Equal(on ? "a\\\\b" : "a\\b", Assert.Single(await session.Queries.ExecuteAsync("SELECT 'a\\\\b'", token)).Rows[0][0]);
        }
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "SELECT q'[it's :hidden; @hidden $9]'", "it's :hidden; @hidden $9")]
    [InlineData(KingbaseCompatibilityMode.MySql, "SELECT 42 AS `a:missing;` # :hidden; $9", "42")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "SELECT 42 AS [a:missing;]", "42")]
    public Task InternalDialectQuoteFacts(KingbaseCompatibilityMode mode, string sql, string expected) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var session = await PgPhysicalSession.OpenAsync(settings.ConnectionString, token);
        Assert.Equal(expected, Assert.Single(await session.Queries.ExecuteAsync(sql, token)).Rows[0][0]);
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PublicScalarBindingsPreparationAndRecovery(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.CompatibilityMode = mode;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        using var connection = await source.OpenConnectionAsync(token);
        using var command = new KingbaseCommand("SELECT @i, :t, :n, :b, :d", connection);
        command.Parameters.AddWithValue("i", 42);
        command.Parameters.AddWithValue("t", "中文 ':unbound; @unbound $9");
        command.Parameters.Add("n", KingbaseDbType.Text).Value = DBNull.Value;
        command.Parameters.AddWithValue("b", new byte[] { 0, 1, 128, 255 });
        command.Parameters.AddWithValue("d", 123.450m);
        await command.PrepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(reader.Read());
            Assert.Equal(42, reader.GetInt32(0));
            Assert.Equal(command.Parameters[1].Value, reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.Equal((byte[])command.Parameters[3].Value!, reader.GetFieldValue<byte[]>(3));
            Assert.Equal(123.450m, reader.GetDecimal(4));
            Assert.Throws<InvalidOperationException>(() => new KingbaseCommand("SELECT 1", connection).ExecuteScalar());
        }
        command.Parameters[0].Value = 43;
        Assert.Equal(43, await command.ExecuteScalarAsync(token));
        await command.UnprepareAsync(token);
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token))
        { Assert.Equal(5, reader.FieldCount); Assert.False(reader.Read()); }
        command.Parameters.Clear(); command.CommandText = "SELECT pg_catalog.kb_fixture_missing_function()";
        Assert.Equal("42883", (await Assert.ThrowsAsync<KingbaseException>(() => command.ExecuteScalarAsync(token))).SqlState);
        command.CommandText = "SELECT 44";
        Assert.Equal(44, await command.ExecuteScalarAsync(token));
        if (mode != KingbaseCompatibilityMode.PostgreSql)
        {
            Assert.Throws<NotSupportedException>(() => connection.GetSchema());
            await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync(CommandBehavior.KeyInfo, token));
            Assert.Equal(44, await command.ExecuteScalarAsync(token));
        }
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "SELECT :p, q'[it's :hidden; @hidden $9]'", "it's :hidden; @hidden $9")]
    [InlineData(KingbaseCompatibilityMode.MySql, "SELECT :p AS `a:missing;`, 'ok' # :hidden; $9", "ok")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "SELECT :p AS [a:missing;], CAST('ok' AS pg_catalog.text)", "ok")]
    public Task PublicDialectBodiesDoNotBindHiddenSlots(KingbaseCompatibilityMode mode, string sql, string expected) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand(sql, connection);
        command.Parameters.AddWithValue("p", 42);
        using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(reader.Read()); Assert.Equal(42, reader.GetInt32(0)); Assert.Equal(expected, reader.GetString(1));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task PublicBatchAndExplicitRollback(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.CompatibilityMode = mode;
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        using var command = new KingbaseCommand("CREATE TEMP TABLE kb_live_mode_execution(id pg_catalog.int4 PRIMARY KEY, value pg_catalog.text)", connection);
        await command.ExecuteNonQueryAsync(token);
        using (var transaction = await connection.BeginTransactionAsync(token))
        {
            if (mode == KingbaseCompatibilityMode.SqlServer) transaction.Save("kb_before");
            using var batch = new KingbaseBatch(connection, transaction);
            var insert = new KingbaseBatchCommand("INSERT INTO kb_live_mode_execution VALUES(:id,:v)");
            insert.Parameters.AddWithValue("id", 1); insert.Parameters.AddWithValue("v", "initial"); batch.BatchCommands.Add(insert);
            var update = new KingbaseBatchCommand("UPDATE kb_live_mode_execution SET value=$2 WHERE id=$1");
            update.Parameters.AddWithValue("id", 1); update.Parameters.AddWithValue("v", "changed"); batch.BatchCommands.Add(update);
            Assert.Equal(2, await batch.ExecuteNonQueryAsync(token));
            Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => Assert.Equal(KingbaseBatchCommandStatus.Succeeded, item.ExecutionStatus));
            command.Transaction = transaction; command.CommandText = "SELECT value FROM kb_live_mode_execution WHERE id=1";
            Assert.Equal("changed", await command.ExecuteScalarAsync(token));
            if (mode == KingbaseCompatibilityMode.SqlServer)
            {
                command.CommandText = "ROLLBACK TRANSACTION kb_before"; await command.ExecuteNonQueryAsync(token);
                Assert.Same(connection, transaction.Connection);
                command.CommandText = "SELECT count(*) FROM kb_live_mode_execution";
                Assert.Equal(0, await command.ExecuteScalarAsync(token));
            }
            await transaction.RollbackAsync(token);
        }
        command.Transaction = null; command.CommandText = "SELECT count(*) FROM kb_live_mode_execution";
        var count = await command.ExecuteScalarAsync(token);
        if (mode == KingbaseCompatibilityMode.SqlServer) Assert.Equal(0, count);
        else Assert.Equal(0L, count);
    });
}
