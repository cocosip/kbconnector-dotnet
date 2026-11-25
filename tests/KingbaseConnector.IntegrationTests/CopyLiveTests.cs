using System.Data;
using System.Text;

namespace KingbaseConnector.IntegrationTests;

public sealed class CopyLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task TextCopyPreservesUnicodeNullEscapesAndCsvOptions(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Create(connection, "kb_copy_text", token);
        using (var empty = await connection.BeginTextExportAsync("COPY kb_copy_text TO STDOUT", token)) Assert.Equal("", await empty.ReadToEndAsync());
        var text = "1\t文字😀\\tquote\\\\x\n2\t\\N\n";
        using (var writer = await connection.BeginTextImportAsync("COPY kb_copy_text FROM STDIN", token)) { await writer.WriteAsync(text); Assert.Equal(2UL, await writer.CompleteAsync(token)); }
        using (var command = new KingbaseCommand("SELECT v FROM kb_copy_text WHERE id=1", connection)) Assert.Equal("文字😀\tquote\\x", await command.ExecuteScalarAsync(token));
        using (var reader = await connection.BeginTextExportAsync("COPY (SELECT * FROM kb_copy_text ORDER BY id) TO STDOUT", token)) Assert.Equal(text, await reader.ReadToEndAsync());
        await Create(connection, "kb_copy_csv", token); var csv = "3|\"line1\nline2|\"\"quote\"\"\"\n4|NULL\n";
        var writer2 = connection.BeginTextImport("COPY kb_copy_csv FROM STDIN (FORMAT CSV, DELIMITER '|', NULL 'NULL')"); writer2.Write(csv); writer2.Dispose(); Assert.Equal(2UL, writer2.RowsCopied);
        using (var command = new KingbaseCommand("SELECT v FROM kb_copy_csv WHERE id=3", connection)) Assert.Equal("line1\nline2|\"quote\"", await command.ExecuteScalarAsync(token));
        using (var command = new KingbaseCommand("SELECT v FROM kb_copy_csv WHERE id=4", connection)) Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task RawBinaryCopyRoundTripsActualServerHeaderRowsAndNull(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Create(connection, "kb_copy_binary", token);
        using (var empty = await connection.BeginRawBinaryCopyAsync("COPY kb_copy_binary TO STDOUT (FORMAT BINARY)", token))
        { using var emptyBytes = new MemoryStream(); await empty.CopyToAsync(emptyBytes, 19, token); Assert.Equal(0UL, empty.RowsCopied); Assert.True(emptyBytes.Length >= 21); }
        using (var command = new KingbaseCommand("INSERT INTO kb_copy_binary VALUES (1, :v), (2, NULL)", connection)) { command.Parameters.AddWithValue("v", "binary_文字😀"); await command.ExecuteNonQueryAsync(token); }
        using var bytes = new MemoryStream();
        using (var export = await connection.BeginRawBinaryCopyAsync("COPY (SELECT * FROM kb_copy_binary ORDER BY id) TO STDOUT (FORMAT BINARY)", token))
        { await export.CopyToAsync(bytes, 7919, token); Assert.False(export.IsImport); Assert.True(export.IsBinary); Assert.Equal(2, export.ColumnCount); Assert.Equal(2UL, export.RowsCopied); }
        Assert.Equal(new byte[] { 75, 66, 67, 79, 80, 89, 10, 255, 13, 10, 0 }, bytes.ToArray().Take(11));
        await Create(connection, "kb_copy_binary_target", token);
        using (var import = connection.BeginRawBinaryCopy("COPY kb_copy_binary_target FROM STDIN (FORMAT BINARY)")) { bytes.Position = 0; await bytes.CopyToAsync(import, 7919, token); Assert.Equal(2UL, await import.CompleteAsync(token)); }
        using (var command = new KingbaseCommand("SELECT v FROM kb_copy_binary_target WHERE id=1", connection)) Assert.Equal("binary_文字😀", await command.ExecuteScalarAsync(token));
        using (var command = new KingbaseCommand("SELECT v FROM kb_copy_binary_target WHERE id=2", connection)) Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, false)]
    [InlineData(KingbaseCompatibilityMode.Oracle, false)]
    [InlineData(KingbaseCompatibilityMode.MySql, false)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, false)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql, true)]
    [InlineData(KingbaseCompatibilityMode.Oracle, true)]
    [InlineData(KingbaseCompatibilityMode.MySql, true)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, true)]
    public Task CancelledImportIsAtomicAndDoesNotPretendToRollbackTheOuterTransaction(KingbaseCompatibilityMode mode, bool transactional) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Create(connection, "kb_copy_cancel", token);
        using (var command = new KingbaseCommand("INSERT INTO kb_copy_cancel VALUES (1, 'before')", connection)) await command.ExecuteNonQueryAsync(token);
        var pid = await Pid(connection, token); using var transaction = transactional ? await connection.BeginTransactionAsync(token) : null;
        if (transaction is not null) { using var command = new KingbaseCommand("INSERT INTO kb_copy_cancel VALUES (3, 'outer')", connection) { Transaction = transaction }; await command.ExecuteNonQueryAsync(token); }
        using (var writer = await connection.BeginTextImportAsync("COPY kb_copy_cancel FROM STDIN", token))
        {
            if (transaction is not null) await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync(token));
            await writer.WriteAsync("2\tpending\n"); await writer.FlushAsync(); await writer.CancelAsync(token); Assert.Null(writer.RowsCopied);
        }
        Assert.Equal(ConnectionState.Open, connection.State);
        if (transaction is not null) { Assert.Same(connection, transaction.Connection); await transaction.RollbackAsync(token); }
        Assert.Equal(pid, await Pid(connection, token)); using var count = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM kb_copy_cancel", connection); Assert.Equal(1, await count.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task TextConversionAndConstraintErrorsPreserveActualServerSemantics(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Create(connection, "kb_copy_error", token); var pid = await Pid(connection, token);
        using (var writer = await connection.BeginTextImportAsync("COPY kb_copy_error FROM STDIN", token))
        {
            await writer.WriteAsync("bad_integer\tvalue\n");
            if (mode == KingbaseCompatibilityMode.MySql) Assert.Equal(1UL, await writer.CompleteAsync(token));
            else { var error = await Assert.ThrowsAsync<KingbaseException>(() => writer.CompleteAsync(token)); Assert.Equal("22P02", error.SqlState); Assert.Null(writer.RowsCopied); }
        }
        if (mode == KingbaseCompatibilityMode.MySql) { using var converted = new KingbaseCommand("SELECT id FROM kb_copy_error", connection); Assert.Equal(0, await converted.ExecuteScalarAsync(token)); }
        using (var create = new KingbaseCommand("CREATE TEMP TABLE kb_copy_constraint (id pg_catalog.int4 CHECK (id>0), v pg_catalog.text)", connection)) await create.ExecuteNonQueryAsync(token);
        using (var writer = await connection.BeginTextImportAsync("COPY kb_copy_constraint FROM STDIN", token))
        { await writer.WriteAsync("0\tfailed\n"); var error = await Assert.ThrowsAsync<KingbaseException>(() => writer.CompleteAsync(token)); Assert.Equal("23514", error.SqlState); Assert.Null(writer.RowsCopied); }
        Assert.Equal(pid, await Pid(connection, token)); using var count = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM kb_copy_constraint", connection); Assert.Equal(0, await count.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task UnfinishedExportRetiresThePhysicalSessionBeforeAnotherPooledBorrower(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.Pooling = true; settings.MaxPoolSize = 1; using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); int previous;
        using (var connection = await source.OpenConnectionAsync(token))
        {
            previous = await Pid(connection, token); await Create(connection, "kb_copy_early", token);
            using (var command = new KingbaseCommand("INSERT INTO kb_copy_early VALUES (1, 'pending')", connection)) await command.ExecuteNonQueryAsync(token);
            using var copy = await connection.BeginRawBinaryCopyAsync("COPY kb_copy_early TO STDOUT (FORMAT BINARY)", token); await copy.DisposeAsync(); Assert.Equal(ConnectionState.Broken, connection.State);
        }
        using var next = await source.OpenConnectionAsync(token); Assert.NotEqual(previous, await Pid(next, token)); using var check = new KingbaseCommand("SELECT 42", next); Assert.Equal(42, await check.ExecuteScalarAsync(token));
    });

    [LiveTheory(writes: true)]
    [InlineData(KingbaseCompatibilityMode.PostgreSql)]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public Task MultiMegabyteImportConsumesTriggerNoticesWhileWriting(KingbaseCompatibilityMode mode) => LiveProfile.Run(mode, async (settings, token) =>
    {
        settings.CommandTimeout = 0; using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Create(connection, "kb_copy_pressure", token);
        using (var command = new KingbaseCommand("CREATE FUNCTION pg_temp.kb_copy_notice() RETURNS trigger LANGUAGE plpgsql AS $kb$ BEGIN RAISE NOTICE 'copy pressure'; RETURN NEW; END; $kb$", connection)) await command.ExecuteNonQueryAsync(token);
        using (var command = new KingbaseCommand("CREATE TRIGGER kb_copy_notice BEFORE INSERT ON kb_copy_pressure FOR EACH ROW EXECUTE PROCEDURE pg_temp.kb_copy_notice()", connection)) await command.ExecuteNonQueryAsync(token);
        var notices = 0; connection.Notice += (_, args) => { Assert.Equal("copy pressure", args.Notice.MessageText); Interlocked.Increment(ref notices); };
        using (var writer = await connection.BeginTextImportAsync("COPY kb_copy_pressure FROM STDIN", token))
        { var row = "1\t" + new string('x', 2048) + "\n"; for (var i = 0; i < 1500; i++) await writer.WriteAsync(row); Assert.Equal(1500UL, await writer.CompleteAsync(token)); }
        Assert.Equal(1500, notices); using var count = new KingbaseCommand("SELECT count(*)::pg_catalog.int4 FROM kb_copy_pressure", connection); Assert.Equal(1500, await count.ExecuteScalarAsync(token));
    });

    static async Task Create(KingbaseConnection connection, string name, CancellationToken token)
    { using var command = new KingbaseCommand("CREATE TEMP TABLE " + name + " (id pg_catalog.int4, v pg_catalog.text)", connection); await command.ExecuteNonQueryAsync(token); }
    static async Task<int> Pid(KingbaseConnection connection, CancellationToken token)
    { using var command = new KingbaseCommand("SELECT pg_catalog.pg_backend_pid()", connection); return (int)(await command.ExecuteScalarAsync(token))!; }
}
