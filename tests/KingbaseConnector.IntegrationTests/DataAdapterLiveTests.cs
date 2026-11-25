using System.Data;
using System.Data.Common;

namespace KingbaseConnector.IntegrationTests;

public sealed class DataAdapterLiveTests
{
    [LiveFact(writes: true)]
    public Task PgAdapterFillsMappedTablesAndUpdatesOriginalCurrentAndNullValues() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter(id int PRIMARY KEY,value text)", token);
        await Scalar(connection, "INSERT INTO kb_adapter VALUES(1,'alpha'),(2,'beta')", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_adapter ORDER BY id", connection);
        using var adapter = new KingbaseDataAdapter(select) { MissingSchemaAction = MissingSchemaAction.AddWithKey };
        var mapping = adapter.TableMappings.Add("Table", "items"); mapping.ColumnMappings.Add("id", "client_id");
        using var data = new DataSet(); Assert.Equal(2, adapter.Fill(data)); var table = data.Tables["items"]!;
        Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal("client_id", Assert.Single(table.PrimaryKey).ColumnName);
        Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState); table.Rows[0]["value"] = DBNull.Value; table.Rows[1].Delete(); table.Rows.Add(3, "new");
        using var insert = Write(connection, "INSERT INTO kb_adapter(id,value) VALUES(@id,@value)", Param("id", DbType.Int32, "client_id"), Param("value", DbType.String, "value"));
        using var update = Write(connection, "UPDATE kb_adapter SET value=@value WHERE id=@old_id AND value IS NOT DISTINCT FROM @old_value", Param("value", DbType.String, "value"), Param("old_id", DbType.Int32, "client_id", DataRowVersion.Original), Param("old_value", DbType.String, "value", DataRowVersion.Original));
        using var delete = Write(connection, "DELETE FROM kb_adapter WHERE id=@old_id", Param("old_id", DbType.Int32, "client_id", DataRowVersion.Original));
        adapter.InsertCommand = insert; adapter.UpdateCommand = update; adapter.DeleteCommand = delete;
        var before = new List<StatementType>(); var after = new List<StatementType>();
        adapter.RowUpdating += (sender, args) => { Assert.Same(adapter, sender); Assert.NotNull(args.Command); before.Add(args.StatementType); };
        adapter.RowUpdated += (sender, args) => { Assert.Same(adapter, sender); Assert.NotNull(args.Command); Assert.Equal(1, args.RecordsAffected); after.Add(args.StatementType); };
        Assert.Equal(3, adapter.Update(data, "items"));
        Assert.Equal(new[] { StatementType.Update, StatementType.Delete, StatementType.Insert }, before); Assert.Equal(before, after);
        Assert.Equal(2, table.Rows.Count); Assert.All(table.Rows.Cast<DataRow>(), row => Assert.Equal(DataRowState.Unchanged, row.RowState));
        using var oracle = new KingbaseCommand("SELECT id,value FROM kb_adapter ORDER BY id", connection); using var reader = await oracle.ExecuteReaderAsync(token);
        Assert.True(reader.Read()); Assert.Equal(1, reader.GetInt32(0)); Assert.True(reader.IsDBNull(1));
        Assert.True(reader.Read()); Assert.Equal(3, reader.GetInt32(0)); Assert.Equal("new", reader.GetString(1)); Assert.False(reader.Read());
    });

    [LiveTheory(writes: true)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgAdapterReportsOptimisticConcurrencyAndPreservesFailedRows(bool continueOnError) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_conflict(id int PRIMARY KEY,value text)", token);
        await Scalar(connection, "INSERT INTO kb_adapter_conflict VALUES(1,'old')", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_adapter_conflict", connection); using var adapter = new KingbaseDataAdapter(select) { ContinueUpdateOnError = continueOnError };
        using var table = new DataTable(); Assert.Equal(1, adapter.Fill(table)); table.Rows[0]["value"] = "client";
        await Scalar(connection, "UPDATE kb_adapter_conflict SET value='server'", token);
        using var update = Write(connection, "UPDATE kb_adapter_conflict SET value=@value WHERE id=@id AND value IS NOT DISTINCT FROM @old_value", Param("value", DbType.String, "value"), Param("id", DbType.Int32, "id", DataRowVersion.Original), Param("old_value", DbType.String, "value", DataRowVersion.Original));
        adapter.UpdateCommand = update; var events = 0;
        adapter.RowUpdated += (_, args) => { events++; Assert.Equal(0, args.RecordsAffected); Assert.IsType<DBConcurrencyException>(args.Errors); };
        if (continueOnError) Assert.Equal(0, adapter.Update(table)); else Assert.Throws<DBConcurrencyException>(() => adapter.Update(table));
        Assert.Equal(1, events); Assert.Equal(DataRowState.Modified, table.Rows[0].RowState); Assert.True(table.Rows[0].HasErrors);
        Assert.Equal("old", table.Rows[0]["value", DataRowVersion.Original]); Assert.Equal("client", table.Rows[0]["value"]);
        Assert.Equal("server", await Scalar(connection, "SELECT value FROM kb_adapter_conflict", token));
    });

    [LiveFact(writes: true)]
    public Task PgAdapterRowEventsCanSkipAndReplaceCommands() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_events(id int PRIMARY KEY)", token);
        using var table = new DataTable(); table.Columns.Add("id", typeof(int)); table.Rows.Add(1); table.Rows.Add(2);
        using var insert = Write(connection, "INSERT INTO kb_adapter_events VALUES(@id)", Param("id", DbType.Int32, "id"));
        using var replacement = Write(connection, "INSERT INTO kb_adapter_events VALUES(20)");
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert }; var after = 0; var captured = new List<RowUpdatingEventArgs>();
        adapter.RowUpdating += (_, args) => { captured.Add(args); if ((int)args.Row["id"] == 1) args.Status = UpdateStatus.SkipCurrentRow; else args.Command = replacement; };
        adapter.RowUpdated += (_, args) => { after++; Assert.Same(replacement, args.Command); };
        Assert.Equal(1, adapter.Update(table)); Assert.Equal(1, after);
        Assert.Same(insert, captured[0].Command); Assert.Same(replacement, captured[1].Command);
        Assert.Equal(DataRowState.Added, table.Rows[0].RowState); Assert.Equal(DataRowState.Unchanged, table.Rows[1].RowState);
        Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_events", token)); Assert.Equal(20, await Scalar(connection, "SELECT id FROM kb_adapter_events", token));
    });

    [LiveFact]
    public Task PgAdapterOwnsOnlyConnectionsItOpensAndHonorsFillAcceptChanges() => LiveProfile.RunPg((settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        using var select = new KingbaseCommand("SELECT 7::int AS id", connection); using var adapter = new KingbaseDataAdapter(select) { AcceptChangesDuringFill = false };
        using var table = new DataTable(); Assert.Equal(1, adapter.Fill(table)); Assert.Equal(7, table.Rows[0]["id"]);
        Assert.Equal(DataRowState.Added, table.Rows[0].RowState); Assert.Equal(ConnectionState.Closed, connection.State);
        select.CommandText = "SELECT 1/0"; using var failed = new DataTable(); Assert.Throws<KingbaseException>(() => adapter.Fill(failed)); Assert.Equal(ConnectionState.Closed, connection.State);
        return Task.CompletedTask;
    });

    [LiveFact(writes: true)]
    public Task PgAdapterAppliesReturnedRecordsAndCanRetainPendingChanges() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_return(id serial PRIMARY KEY,value text)", token);
        using var table = new DataTable(); table.Columns.Add("id", typeof(int)); table.Columns.Add("value", typeof(string)); table.Rows.Add(DBNull.Value, "value");
        using var insert = Write(connection, "INSERT INTO kb_adapter_return(value) VALUES(@value) RETURNING id", Param("value", DbType.String, "value")); insert.UpdatedRowSource = UpdateRowSource.FirstReturnedRecord;
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert, AcceptChangesDuringUpdate = false };
        Assert.Equal(1, adapter.Update(table)); Assert.Equal(1, table.Rows[0]["id"]); Assert.NotEqual(DataRowState.Unchanged, table.Rows[0].RowState);
        Assert.Equal("value", await Scalar(connection, "SELECT value FROM kb_adapter_return WHERE id=1", token));
    });

    [LiveFact(writes: true)]
    public Task PgAdapterFillSchemaAndPagedTableArrayUseTheBufferedBridge() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_schema(id int PRIMARY KEY,value varchar(17))", token);
        await Scalar(connection, "INSERT INTO kb_adapter_schema VALUES(1,'one'),(2,'two'),(3,'three')", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_adapter_schema ORDER BY id", connection);
        using var adapter = new KingbaseDataAdapter(select); using var data = new DataSet();
        var schema = adapter.FillSchema(data, SchemaType.Source); var first = Assert.Single(schema);
        Assert.Empty(first.Rows); Assert.Equal("id", Assert.Single(first.PrimaryKey).ColumnName); Assert.Equal(17, first.Columns["value"]!.MaxLength);
        using var table = new DataTable(); Assert.Same(table, adapter.FillSchema(table, SchemaType.Source)); Assert.Empty(table.Rows); Assert.Single(table.PrimaryKey);
        Assert.Equal(1, adapter.Fill(1, 1, table)); Assert.Equal(2, table.Rows[0]["id"]); Assert.Equal(ConnectionState.Open, connection.State);
    });

    [LiveFact(writes: true)]
    public Task PgAdapterContinuesAfterSynchronizedServerErrorsAndKeepsRowErrors() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_errors(id int PRIMARY KEY)", token); await Scalar(connection, "INSERT INTO kb_adapter_errors VALUES(1)", token);
        using var table = new DataTable(); table.Columns.Add("id", typeof(int)); table.Rows.Add(1); table.Rows.Add(2);
        using var insert = Write(connection, "INSERT INTO kb_adapter_errors VALUES(@id)", Param("id", DbType.Int32, "id"));
        using var adapter = new KingbaseDataAdapter { InsertCommand = insert, ContinueUpdateOnError = true }; var seen = 0;
        adapter.RowUpdated += (_, args) => { if (++seen == 1) { Assert.Equal("23505", Assert.IsType<KingbaseException>(args.Errors).SqlState); Assert.Equal(UpdateStatus.ErrorsOccurred, args.Status); } else Assert.Null(args.Errors); };
        Assert.Equal(1, adapter.Update(table)); Assert.Equal(2, seen); Assert.True(table.Rows[0].HasErrors); Assert.Equal(DataRowState.Added, table.Rows[0].RowState);
        Assert.Equal(DataRowState.Unchanged, table.Rows[1].RowState); Assert.Equal(2L, await Scalar(connection, "SELECT count(*) FROM kb_adapter_errors", token));
    });

    [LiveFact(writes: true)]
    public Task PgAdapterOriginalNullFlagsAndExplicitTransactionRollbackRemainAccurate() => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString); await connection.OpenAsync(token);
        await Scalar(connection, "CREATE TEMP TABLE kb_adapter_transaction(id int PRIMARY KEY,value text)", token);
        await Scalar(connection, "INSERT INTO kb_adapter_transaction VALUES(1,NULL),(2,'old')", token);
        using var select = new KingbaseCommand("SELECT id,value FROM kb_adapter_transaction ORDER BY id", connection); using var adapter = new KingbaseDataAdapter(select) { AcceptChangesDuringUpdate = false };
        using var table = new DataTable(); Assert.Equal(2, adapter.Fill(table)); table.Rows[0]["value"] = "new"; table.Rows[1]["value"] = DBNull.Value;
        using var transaction = await connection.BeginTransactionAsync(token);
        var flag = Param("is_null", DbType.Int32, "value", DataRowVersion.Original); flag.SourceColumnNullMapping = true;
        using var update = Write(connection, "UPDATE kb_adapter_transaction SET value=@value WHERE id=@id AND ((@is_null=1 AND value IS NULL) OR value=@old_value)", Param("value", DbType.String, "value"), Param("id", DbType.Int32, "id", DataRowVersion.Original), flag, Param("old_value", DbType.String, "value", DataRowVersion.Original));
        update.Transaction = transaction; adapter.UpdateCommand = update; Assert.Equal(2, adapter.Update(table));
        Assert.All(table.Rows.Cast<DataRow>(), row => Assert.Equal(DataRowState.Modified, row.RowState));
        Assert.Equal("new", await Scalar(connection, "SELECT value FROM kb_adapter_transaction WHERE id=1", token));
        await transaction.RollbackAsync(token);
        Assert.Same(DBNull.Value, await Scalar(connection, "SELECT value FROM kb_adapter_transaction WHERE id=1", token));
        Assert.Equal("old", await Scalar(connection, "SELECT value FROM kb_adapter_transaction WHERE id=2", token));
        Assert.All(table.Rows.Cast<DataRow>(), row => Assert.Equal(DataRowState.Modified, row.RowState));
    });

    static KingbaseParameter Param(string name, DbType type, string source, DataRowVersion version = DataRowVersion.Current) => new() { ParameterName = name, DbType = type, SourceColumn = source, SourceVersion = version };
    static KingbaseCommand Write(KingbaseConnection connection, string sql, params KingbaseParameter[] parameters)
    {
        var command = new KingbaseCommand(sql, connection) { UpdatedRowSource = UpdateRowSource.None };
        foreach (var parameter in parameters) command.Parameters.Add(parameter); return command;
    }
    static async Task<object?> Scalar(KingbaseConnection connection, string sql, CancellationToken token)
    { using var command = new KingbaseCommand(sql, connection); return await command.ExecuteScalarAsync(token); }
}
