using System.Data;
using System.Text;

namespace KingbaseConnector.IntegrationTests;

public sealed class StreamingLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task LargeTextBinaryAndOrdinaryLobFieldsUsePublicPreparedSequentialReaders(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.MaxAutoPrepare = 2; settings.AutoPrepareMinUsages = 2;
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        using var transaction = await connection.BeginTransactionAsync(token);
        var lob = mode is KingbaseCompatibilityMode.Oracle or KingbaseCompatibilityMode.MySql;
        var schema = mode == KingbaseCompatibilityMode.Oracle ? "pg_catalog" : "sys";
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_stream_live (t " + (lob ? schema + ".clob" : "pg_catalog.text") +
            ",b " + (lob ? schema + ".blob" : "pg_catalog.bytea") + ",n " + (lob ? schema + ".nclob" : "pg_catalog.text") + ")", connection) { Transaction = transaction })
            await create.ExecuteNonQueryAsync(token);
        using (var insert = new KingbaseCommand("INSERT INTO kb_stream_live VALUES (:t,:b,:t)", connection) { Transaction = transaction })
        { insert.Parameters.AddWithValue("t", "seed"); insert.Parameters.AddWithValue("b", new byte[] { 0 }); Assert.Equal(1, await insert.ExecuteNonQueryAsync(token)); }
        var chunkText = string.Concat(Enumerable.Repeat("文字😀", 8192)); var chunkBytes = Enumerable.Range(0, 32768).Select(i => (byte)(i % 251)).ToArray();
        const int repeats = 40;
        using (var append = new KingbaseCommand("UPDATE kb_stream_live SET t=t::pg_catalog.text OPERATOR(pg_catalog.||) :t," +
            "b=b::pg_catalog.bytea OPERATOR(pg_catalog.||) :b,n=n::pg_catalog.text OPERATOR(pg_catalog.||) :t", connection) { Transaction = transaction })
        {
            append.Parameters.AddWithValue("t", chunkText); append.Parameters.AddWithValue("b", chunkBytes); await append.PrepareAsync(token);
            for (var i = 0; i < repeats; i++) Assert.Equal(1, await append.ExecuteNonQueryAsync(token));
        }
        var expectedText = "seed" + string.Concat(Enumerable.Repeat(chunkText, repeats));
        var expectedBytes = new byte[1 + chunkBytes.Length * repeats];
        for (var i = 0; i < repeats; i++) Array.Copy(chunkBytes, 0, expectedBytes, 1 + i * chunkBytes.Length, chunkBytes.Length);
        using var query = new KingbaseCommand("SELECT t,b,n,NULL::pg_catalog.text FROM kb_stream_live", connection) { Transaction = transaction };
        for (var iteration = 0; iteration < 3; iteration++)
        {
            if (iteration == 2) await query.PrepareAsync(token);
            using var reader = await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            Assert.True(reader.HasRows); Assert.True(await reader.ReadAsync(token));
            if (iteration == 2)
            { Assert.Equal(expectedText, await reader.GetFieldValueAsync<string>(0, token)); Assert.Equal(expectedBytes, await reader.GetFieldValueAsync<byte[]>(1, token)); }
            else
            {
                using var text = reader.GetTextReader(0); var chars = new char[4093]; var position = 0; int count;
                while ((count = await text.ReadAsync(chars.AsMemory(), token)) != 0)
                { Assert.True(expectedText.AsSpan(position, count).SequenceEqual(chars.AsSpan(0, count))); position += count; }
                Assert.Equal(expectedText.Length, position);
                using var binary = reader.GetStream(1); var buffer = new byte[7919]; position = 0;
                while ((count = await binary.ReadAsync(buffer, 0, buffer.Length, token)) != 0)
                { Assert.True(expectedBytes.AsSpan(position, count).SequenceEqual(buffer.AsSpan(0, count))); position += count; }
                Assert.Equal(expectedBytes.Length, position);
                Assert.Throws<InvalidOperationException>(() => text.Peek());
            }
            var partial = new char[13]; Assert.Equal(13, reader.GetChars(2, 9, partial, 0, partial.Length)); Assert.Equal(expectedText.Substring(9, 13), new string(partial));
            Assert.Equal(expectedText.Length, reader.GetChars(2, 0, null, 0, 0));
            Assert.Throws<InvalidOperationException>(() => reader.GetChars(2, 0, partial, 0, 1));
            Assert.True(await reader.IsDBNullAsync(3, token)); Assert.False(await reader.ReadAsync(token)); Assert.False(await reader.NextResultAsync(token));
        }
        await transaction.RollbackAsync(token);
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task EarlyCloseAcknowledgesTheRemainingBatchAndPoolReuseKeepsOldChildrenInert(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1; using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        TextReader old; int previous;
        using (var connection = await source.OpenConnectionAsync(token))
        {
            using (var pid = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection)) previous = (int)(await pid.ExecuteScalarAsync(token))!;
            using var transaction = await connection.BeginTransactionAsync(token);
            using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_stream_batch (id pg_catalog.int4)", connection) { Transaction = transaction }) await create.ExecuteNonQueryAsync(token);
            using var batch = new KingbaseBatch(connection, transaction);
            var first = new KingbaseBatchCommand("SELECT :value::pg_catalog.text"); first.Parameters.AddWithValue("value", "文字😀first"); batch.BatchCommands.Add(first);
            batch.BatchCommands.Add(new("INSERT INTO kb_stream_batch VALUES (1)")); batch.BatchCommands.Add(new("SELECT id FROM kb_stream_batch"));
            using (var reader = await batch.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, token))
            {
                Assert.True(await reader.ReadAsync(token)); old = reader.GetTextReader(0); Assert.Equal('文', old.Read());
                await reader.CloseAsync(); Assert.Equal(1, reader.RecordsAffected);
            }
            Assert.All(batch.BatchCommands.Cast<KingbaseBatchCommand>(), item => Assert.Equal(KingbaseBatchCommandStatus.Succeeded, item.ExecutionStatus)); Assert.Null(batch.FailedCommandIndex);
            using (var count = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM kb_stream_batch", connection) { Transaction = transaction }) Assert.Equal(1, await count.ExecuteScalarAsync(token));
            await transaction.RollbackAsync(token);
        }
        using var next = await source.OpenConnectionAsync(token); old.Dispose(); old.Dispose();
        using var current = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", next); Assert.Equal(previous, await current.ExecuteScalarAsync(token));
    });

    [LiveTheory]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task ReaderLifetimeCancellationRetiresThePoolLeaseWithoutReplaying(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1; using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); int previous;
        using (var connection = await source.OpenConnectionAsync(token))
        {
            using (var pid = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection)) previous = (int)(await pid.ExecuteScalarAsync(token))!;
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var query = new KingbaseCommand("SELECT 1 FROM pg_catalog.pg_sleep(5)", connection);
            using var reader = await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess, caller.Token); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(token)); Assert.Equal(ConnectionState.Broken, connection.State);
        }
        using var next = await source.OpenConnectionAsync(token); using var command = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", next);
        Assert.NotEqual(previous, await command.ExecuteScalarAsync(token));
    });
}
