using System.Data;

namespace KingbaseConnector.IntegrationTests;

public sealed class SchemaOnlyLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgSchemaOnlyDoesNotExecuteDmlDdlVolatileFunctionsOrCommit(bool prepared) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_reader_schemaonly(id int PRIMARY KEY, amount numeric(12,3))", token);
        await Scalar(connection, "INSERT INTO kb_reader_schemaonly VALUES(1,1.250)", token);
        await Scalar(connection, "CREATE TEMP SEQUENCE kb_reader_schemaonly_seq", token);
        foreach (var (sql, fields) in new[] {
            ("INSERT INTO kb_reader_schemaonly VALUES(2,@amount) RETURNING id,amount", 2),
            ("UPDATE kb_reader_schemaonly SET amount=@amount RETURNING amount", 1),
            ("DELETE FROM kb_reader_schemaonly", 0),
            ("TRUNCATE kb_reader_schemaonly", 0),
            ("CREATE TEMP TABLE kb_reader_schemaonly_uncreated(id int)", 0),
            ("SELECT nextval('pg_temp.kb_reader_schemaonly_seq'), CAST(@amount AS numeric(12,3))", 2) })
        {
            using var command = new KingbaseCommand(sql, connection);
            if (sql.Contains("@amount", StringComparison.Ordinal)) command.Parameters.AddWithValue("amount", 2.500m);
            if (prepared) await command.PrepareAsync(token);
            using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token))
            {
                Assert.Equal(fields, reader.FieldCount); Assert.Equal(-1, reader.RecordsAffected);
                Assert.False(reader.Read()); Assert.False(reader.NextResult());
            }
            if (prepared) await command.UnprepareAsync(token);
            Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_reader_schemaonly", token));
            Assert.Equal(1.250m, await Scalar(connection, "SELECT amount FROM kb_reader_schemaonly WHERE id=1", token));
            Assert.Equal(false, await Scalar(connection, "SELECT is_called FROM kb_reader_schemaonly_seq", token));
            Assert.Equal(true, await Scalar(connection, "SELECT pg_catalog.to_regclass('pg_temp.kb_reader_schemaonly_uncreated') IS NULL", token));
        }
        using (var invalid = new KingbaseCommand("SELECT kb_schemaonly_missing_column", connection))
            Assert.Equal("42703", (await Assert.ThrowsAsync<KingbaseException>(() => invalid.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token))).SqlState);
        using var transaction = await connection.BeginTransactionAsync(token);
        await Scalar(connection, "UPDATE kb_reader_schemaonly SET amount=7", token);
        using (var commit = new KingbaseCommand("COMMIT", connection) { Transaction = transaction })
        {
            if (prepared) await commit.PrepareAsync(token);
            using (var reader = await commit.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token)) Assert.Equal(0, reader.FieldCount);
            if (prepared) await commit.UnprepareAsync(token);
        }
        Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token);
        Assert.Equal(1.250m, await Scalar(connection, "SELECT amount FROM kb_reader_schemaonly WHERE id=1", token));
    });

    [LiveFact(writes: true)]
    public Task PgPreparedBatchSchemaOnlyPreservesDataAndExecutionStatuses() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_batch_schemaonly(id int PRIMARY KEY, amount numeric(12,3))", token);
        await Scalar(connection, "INSERT INTO kb_batch_schemaonly VALUES(1,1.250)", token);
        using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new("INSERT INTO kb_batch_schemaonly VALUES(2,2.500) RETURNING id"));
        batch.BatchCommands.Add(new("UPDATE kb_batch_schemaonly SET amount=2.500 WHERE id=1 RETURNING amount"));
        batch.BatchCommands.Add(new("DELETE FROM kb_batch_schemaonly WHERE id=2"));
        await batch.PrepareAsync(token);
        using (var reader = await batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token))
        {
            Assert.Equal(1, reader.FieldCount); Assert.False(reader.Read()); Assert.True(reader.NextResult());
            Assert.Equal(1, reader.FieldCount); Assert.False(reader.Read()); Assert.True(reader.NextResult());
            Assert.Equal(0, reader.FieldCount); Assert.False(reader.Read()); Assert.False(reader.NextResult());
            Assert.Equal(-1, reader.RecordsAffected);
        }
        Assert.Null(batch.FailedCommandIndex);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => { Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, item.ExecutionStatus); Assert.Equal(-1, item.RecordsAffected); });
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_batch_schemaonly", token));
        Assert.Equal(1.250m, await Scalar(connection, "SELECT amount FROM kb_batch_schemaonly WHERE id=1", token));
        Assert.Equal(3, await batch.ExecuteNonQueryAsync(token));
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_batch_schemaonly", token));
        Assert.Equal(2.500m, await Scalar(connection, "SELECT amount FROM kb_batch_schemaonly WHERE id=1", token));
        batch.BatchCommands[1].CommandText = "SELECT kb_schemaonly_missing_column";
        var error = await Assert.ThrowsAsync<KingbaseException>(() => batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token));
        Assert.Equal("42703", error.SqlState); Assert.Same(batch.BatchCommands[1], error.BatchCommand); Assert.Equal(1, batch.FailedCommandIndex);
        Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => { Assert.Equal(KingbaseBatchCommandStatus.NotExecuted, item.ExecutionStatus); Assert.Equal(-1, item.RecordsAffected); });
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_batch_schemaonly", token));
    });

    [LiveFact]
    public Task PgSchemaOnlyWithDifferentInputTypeKeepsOriginalNamedStatementAttached() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var command = new KingbaseCommand("SELECT @p", connection); command.Parameters.AddWithValue("p", 42);
        await command.PrepareAsync(token); command.Parameters[0].Value = "schema";
        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token)) Assert.Equal(typeof(string), reader.GetFieldType(0));
        command.Parameters[0].Value = 42; Assert.Equal(42, await command.ExecuteScalarAsync(token));
        await command.UnprepareAsync(token);
        Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM pg_catalog.pg_prepared_statements WHERE statement='SELECT $1'", token));
    });

    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    {
        using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token);
    }
}
