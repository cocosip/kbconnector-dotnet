using System.Data;
using System.Data.Common;

namespace KingbaseConnector.IntegrationTests;

public sealed class DataAdapterBatchLiveTests
{
    [LiveTheory(writes: true)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(257)]
    public Task PgBatchedAdapterSnapshotsEachRowsValuesAndBinaryBuffers(int size) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_batch(id int PRIMARY KEY,value text,payload bytea)", token);
        using var table = new DataTable(); table.Columns.Add("id", typeof(int)); table.Columns.Add("value", typeof(string)); table.Columns.Add("payload", typeof(byte[]));
        var shared = new byte[] { 0 }; for (var i = 1; i <= 5; i++) table.Rows.Add(i, i == 3 ? DBNull.Value : "v" + i, shared);
        using var insert = Write(connection, "INSERT INTO kb_adapter_batch VALUES(@id,@value,@payload)", Param("id", DbType.Int32, "id"), Param("value", DbType.String, "value"), Param("payload", DbType.Binary, "payload"));
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = size }; var rowCounts = new List<int>();
        adapter.RowUpdating += (_, args) => shared[0] = (byte)(int)args.Row["id"];
        adapter.RowUpdated += (_, args) => { Assert.Same(insert, args.Command); Assert.Null(args.Errors); rowCounts.Add(args.RowCount); var rows = new DataRow[args.RowCount]; args.CopyToRows(rows); Assert.All(rows, row => Assert.Same(table, row.Table)); };
        Assert.Equal(5, adapter.Update(table)); Assert.Equal(5, rowCounts.Sum()); Assert.Equal(size == 2 ? 3 : 1, rowCounts.Count);
        Assert.All(table.Rows.Cast<DataRow>(), row => Assert.Equal(DataRowState.Unchanged, row.RowState)); Assert.Equal(ConnectionState.Open, connection.State);
        using var oracle = new KingbaseCommand("SELECT id,value,payload FROM kb_adapter_batch ORDER BY id", connection); using var reader = await oracle.ExecuteReaderAsync(token);
        for (var i = 1; i <= 5; i++) { Assert.True(reader.Read()); Assert.Equal(i, reader.GetInt32(0)); if (i == 3) Assert.True(reader.IsDBNull(1)); else Assert.Equal("v" + i, reader.GetString(1)); Assert.Equal(new byte[] { (byte)i }, reader.GetFieldValue<byte[]>(2)); } Assert.False(reader.Read());
    });

    [LiveTheory(writes: true)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public Task PgBatchedAdapterRetainsAcknowledgmentsAndDoesNotReplayUnsentRows(bool continueOnError, int duplicate) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_partial(id int PRIMARY KEY)", token); await Scalar(connection, "INSERT INTO kb_adapter_partial VALUES(" + duplicate + ")", token);
        using var table = IdRows(5); using var insert = Write(connection, "INSERT INTO kb_adapter_partial VALUES(@id)", Param("id", DbType.Int32, "id"));
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = 3, ContinueUpdateOnError = continueOnError }; var errors = new List<Exception>();
        adapter.RowUpdated += (_, args) => { if (args.Errors is { } error) errors.Add(error); };
        if (continueOnError) Assert.Equal(duplicate - 1 + 2, adapter.Update(table)); else Assert.Equal("23505", Assert.Throws<KingbaseException>(() => adapter.Update(table)).SqlState);
        Assert.Equal("23505", Assert.IsType<KingbaseException>(Assert.Single(errors)).SqlState);
        for (var i = 1; i <= 5; i++)
        {
            var row = table.Rows[i - 1]; var succeeded = i < duplicate || (continueOnError && i >= 4);
            Assert.Equal(succeeded ? DataRowState.Unchanged : DataRowState.Added, row.RowState);
            if (i == duplicate || (i > duplicate && i <= 3)) Assert.True(row.HasErrors); else Assert.False(row.HasErrors);
            if (i > duplicate && i <= 3) Assert.Contains("not executed", row.RowError);
        }
        Assert.Equal((long)duplicate + (continueOnError ? 2 : 0), await Scalar(connection, "SELECT count(*) FROM kb_adapter_partial", token));
        if (duplicate < 3) Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_partial WHERE id>" + duplicate + " AND id<=3", token));
        Assert.Equal(ConnectionState.Open, connection.State);
    });

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgBatchedGeneratedCrudSharesOriginalNullsAndTransactionContext(bool explicitTransaction) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_generated(id int PRIMARY KEY,value text)", token); await Scalar(connection, "INSERT INTO kb_adapter_generated VALUES(1,NULL),(2,'delete')", token);
        using var transaction = explicitTransaction ? await connection.BeginTransactionAsync(token) : null;
        using var select = new KingbaseCommand("SELECT id,value FROM kb_adapter_generated ORDER BY id", connection) { Transaction = transaction };
        using var adapter = new KingbaseDataAdapter(select) { UpdateBatchSize = 0, AcceptChangesDuringUpdate = !explicitTransaction }; using var builder = new KingbaseCommandBuilder(adapter);
        using var table = new DataTable(); Assert.Equal(2, adapter.Fill(table)); table.Rows[0]["value"] = "new"; table.Rows[1].Delete(); table.Rows.Add(3, DBNull.Value);
        Assert.Equal(3, adapter.Update(table)); Assert.Equal("new", await Scalar(connection, "SELECT value FROM kb_adapter_generated WHERE id=1", token)); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_generated WHERE id=2", token)); Assert.Same(DBNull.Value, await Scalar(connection, "SELECT value FROM kb_adapter_generated WHERE id=3", token));
        if (transaction is not null) { await transaction.RollbackAsync(token); Assert.Same(DBNull.Value, await Scalar(connection, "SELECT value FROM kb_adapter_generated WHERE id=1", token)); Assert.Equal("delete", await Scalar(connection, "SELECT value FROM kb_adapter_generated WHERE id=2", token)); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_generated WHERE id=3", token)); Assert.Equal(DataRowState.Modified, table.Rows[0].RowState); }
    });

    [LiveTheory(writes: true)]
    [InlineData(0)]
    [InlineData(257)]
    public Task PgAdapterSplitsLogicalBatchesPastThePhysicalCommandBound(int size) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_large(id int PRIMARY KEY)", token);
        using var table = IdRows(300); using var insert = Write(connection, "INSERT INTO kb_adapter_large VALUES(@id)", Param("id", DbType.Int32, "id"));
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = size }; var groups = new List<int>(); adapter.RowUpdated += (_, args) => groups.Add(args.RowCount);
        Assert.Equal(300, adapter.Update(table)); Assert.Equal(size == 0 ? new[] { 300 } : new[] { 257, 43 }, groups);
        Assert.Equal(300L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_large", token)); Assert.Equal(1, await Scalar(connection, "SELECT min(id) FROM kb_adapter_large", token)); Assert.Equal(300, await Scalar(connection, "SELECT max(id) FROM kb_adapter_large", token));
    });

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgBatchedConcurrencyRetainsOtherAcknowledgedUpdates(bool continueOnError) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_concurrency(id int PRIMARY KEY,value text)", token); await Scalar(connection, "INSERT INTO kb_adapter_concurrency VALUES(1,'old'),(2,'old')", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_adapter_concurrency ORDER BY id", connection); using var adapter = new KingbaseDataAdapter(select) { UpdateBatchSize = 0, ContinueUpdateOnError = continueOnError }; using var builder = new KingbaseCommandBuilder(adapter);
        using var table = new DataTable(); adapter.Fill(table); table.Rows[0]["value"] = "client1"; table.Rows[1]["value"] = "client2"; await Scalar(connection, "UPDATE kb_adapter_concurrency SET value='server' WHERE id=1", token);
        if (continueOnError) Assert.Equal(1, adapter.Update(table)); else Assert.Throws<DBConcurrencyException>(() => adapter.Update(table));
        Assert.Equal(DataRowState.Modified, table.Rows[0].RowState); Assert.True(table.Rows[0].HasErrors); Assert.Equal(DataRowState.Unchanged, table.Rows[1].RowState); Assert.False(table.Rows[1].HasErrors);
        Assert.Equal("server", await Scalar(connection, "SELECT value FROM kb_adapter_concurrency WHERE id=1", token)); Assert.Equal("client2", await Scalar(connection, "SELECT value FROM kb_adapter_concurrency WHERE id=2", token));
    });

    [LiveTheory(writes: true)]
    [InlineData("timeout")]
    [InlineData("transaction")]
    [InlineData("connection")]
    public Task PgBatchedContextMismatchRejectsEveryUnsentRow(string difference) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); using var other = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await other.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_context(id int PRIMARY KEY)", token);
        using var transaction = difference == "transaction" ? await connection.BeginTransactionAsync(token) : null;
        using var table = IdRows(2); using var insert = Write(connection, "INSERT INTO kb_adapter_context VALUES(@id)", Param("id", DbType.Int32, "id")); using var alternate = Write(difference == "connection" ? other : connection, "INSERT INTO kb_adapter_context VALUES(2)");
        if (difference == "transaction") alternate.Transaction = transaction; if (difference == "timeout") alternate.CommandTimeout = insert.CommandTimeout + 1;
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = 0 }; adapter.RowUpdating += (_, args) => { if ((int)args.Row["id"] == 2) args.Command = alternate; };
        Assert.Throws<InvalidOperationException>(() => adapter.Update(table)); Assert.All(table.Rows.Cast<DataRow>(), row => { Assert.Equal(DataRowState.Added, row.RowState); Assert.True(row.HasErrors); });
        if (transaction is not null) await transaction.RollbackAsync(token); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_context", token));
    });

    [LiveFact(writes: true)]
    public Task PgBatchedRowEventsCanSkipReplaceAndRejectReentrancy() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Scalar(connection, "CREATE TEMP TABLE kb_adapter_events_batch(id int PRIMARY KEY)", token);
        using var table = IdRows(4); using var insert = Write(connection, "INSERT INTO kb_adapter_events_batch VALUES(@id)", Param("id", DbType.Int32, "id")); using var replacement = Write(connection, "INSERT INTO kb_adapter_events_batch VALUES(30)");
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = 2 }; var captured = new List<RowUpdatingEventArgs>();
        adapter.RowUpdating += (_, args) => { captured.Add(args); if ((int)args.Row["id"] == 1) { Assert.Throws<InvalidOperationException>(() => adapter.Update(table)); Assert.Throws<InvalidOperationException>(() => adapter.UpdateBatchSize = 3); } if ((int)args.Row["id"] == 2) args.Status = UpdateStatus.SkipCurrentRow; if ((int)args.Row["id"] == 3) args.Command = replacement; };
        Assert.Equal(3, adapter.Update(table)); Assert.Equal(DataRowState.Added, table.Rows[1].RowState); Assert.Same(replacement, captured[2].Command); Assert.Equal(3L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_events_batch WHERE id IN (1,30,4)", token)); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_events_batch WHERE id IN (2,3)", token));
    });

    [LiveFact]
    public Task PgBatchedUnknownAffectedCountKeepsRowsPendingAndClosesOwnedConnection() => LiveProfile.RunPg((settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); using var table = IdRows(2); using var command = Write(connection, "SELECT @id", Param("id", DbType.Int32, "id"));
        using var adapter = new KingbaseDataAdapter { InsertCommand = command, UpdateBatchSize = 0 }; Assert.Throws<NotSupportedException>(() => adapter.Update(table)); Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.All(table.Rows.Cast<DataRow>(), row => { Assert.Equal(DataRowState.Added, row.RowState); Assert.True(row.HasErrors); }); return Task.CompletedTask;
    });

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgRowUpdatedStopDoesNotFlushTheSameQueueAgain(bool acceptCurrent) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Scalar(connection, "CREATE TEMP TABLE kb_adapter_stop(id int)", token);
        using var table = IdRows(4); table.Rows[2].RowError = "retain pending detail"; table.Rows[3].RowError = "retain later detail"; using var insert = Write(connection, "INSERT INTO kb_adapter_stop VALUES(@id)", Param("id", DbType.Int32, "id")); using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = 2 };
        var updating = 0; var updated = 0; adapter.RowUpdating += (_, _) => updating++;
        adapter.RowUpdated += (_, args) => { if (++updated == 1) { if (acceptCurrent) { var rows = new DataRow[args.RowCount]; args.CopyToRows(rows); foreach (var row in rows) row.AcceptChanges(); } args.Status = UpdateStatus.SkipAllRemainingRows; } };
        Assert.Equal(acceptCurrent ? 2 : 0, adapter.Update(table)); Assert.Equal(2, updating); Assert.Equal(1, updated); Assert.Equal(2L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_stop", token)); Assert.False(table.Rows[0].HasErrors); Assert.False(table.Rows[1].HasErrors); Assert.Equal("retain pending detail", table.Rows[2].RowError); Assert.Equal("retain later detail", table.Rows[3].RowError); Assert.Equal(2, insert.Parameters[0].Value); Assert.Equal(DataRowState.Added, table.Rows[2].RowState);
    });

    [LiveFact(writes: true)]
    public Task PgMissingCommandMarksItsOwnRowAndPreservesEarlierQueuedRows() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Scalar(connection, "CREATE TEMP TABLE kb_adapter_missing(id int PRIMARY KEY)", token);
        using var table = IdRows(2); table.Rows[1].AcceptChanges(); table.Rows[1]["id"] = 20;
        using var insert = Write(connection, "INSERT INTO kb_adapter_missing VALUES(@id)", Param("id", DbType.Int32, "id")); using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = 0, ContinueUpdateOnError = true };
        Assert.Equal(1, adapter.Update(table)); Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState); Assert.False(table.Rows[0].HasErrors); Assert.Equal(DataRowState.Modified, table.Rows[1].RowState); Assert.True(table.Rows[1].HasErrors); Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_missing", token));
    });

    [LiveFact(writes: true)]
    public Task PgDisposedCommandsCannotAuthorizeBatchedWrites() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token); await Scalar(connection, "CREATE TEMP TABLE kb_adapter_disposed(id int)", token);
        using var table = IdRows(2); using var insert = Write(connection, "INSERT INTO kb_adapter_disposed VALUES(@id)", Param("id", DbType.Int32, "id")); insert.Dispose(); using var adapter = new KingbaseDataAdapter { InsertCommand = insert, UpdateBatchSize = 0 };
        Assert.Throws<ObjectDisposedException>(() => adapter.Update(table)); Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_disposed", token)); Assert.All(table.Rows.Cast<DataRow>(), row => Assert.Equal(DataRowState.Added, row.RowState));
    });

    static DataTable IdRows(int count) { var table = new DataTable(); table.Columns.Add("id", typeof(int)); for (var i = 1; i <= count; i++) table.Rows.Add(i); return table; }
    static KingbaseParameter Param(string name, DbType type, string source) => new() { ParameterName = name, DbType = type, SourceColumn = source };
    static KingbaseCommand Write(KingbaseConnection connection, string sql, params KingbaseParameter[] parameters)
    { var command = new KingbaseCommand(sql, connection) { UpdatedRowSource = UpdateRowSource.None }; foreach (var parameter in parameters) command.Parameters.Add(parameter); return command; }
    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }
}
