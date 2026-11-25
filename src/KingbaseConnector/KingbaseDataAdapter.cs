using System.Data;
using System.Data.Common;

namespace KingbaseConnector;

/// <summary>Fills and updates disconnected tables through explicitly supplied commands.</summary>
public sealed class KingbaseDataAdapter : DbDataAdapter, IDbDataAdapter
{
    KingbaseCommand? _select;
    KingbaseCommand? _insert;
    KingbaseCommand? _update;
    KingbaseCommand? _delete;
    public KingbaseDataAdapter() { }
    public KingbaseDataAdapter(KingbaseCommand? selectCommand) => SelectCommand = selectCommand;
    public KingbaseDataAdapter(string? selectCommandText, KingbaseConnection? connection)
        : this(new KingbaseCommand(selectCommandText, connection)) { }
    public KingbaseDataAdapter(string? selectCommandText, string? connectionString)
        : this(selectCommandText, new KingbaseConnection(connectionString ?? "")) { }

    public new KingbaseCommand? SelectCommand { get => _select; set => _select = value; }
    public new KingbaseCommand? InsertCommand { get => _insert; set => _insert = value; }
    public new KingbaseCommand? UpdateCommand { get => _update; set => _update = value; }
    public new KingbaseCommand? DeleteCommand { get => _delete; set => _delete = value; }
    IDbCommand? IDbDataAdapter.SelectCommand { get => _select; set => _select = ValidateCommand(value); }
    IDbCommand? IDbDataAdapter.InsertCommand { get => _insert; set => _insert = ValidateCommand(value); }
    IDbCommand? IDbDataAdapter.UpdateCommand { get => _update; set => _update = ValidateCommand(value); }
    IDbCommand? IDbDataAdapter.DeleteCommand { get => _delete; set => _delete = ValidateCommand(value); }
    static KingbaseCommand? ValidateCommand(IDbCommand? command) => command is null or KingbaseCommand
        ? (KingbaseCommand?)command : throw new ArgumentException("DataAdapter commands must be KingbaseCommand instances.", nameof(command));

    public event EventHandler<KingbaseRowUpdatingEventArgs>? RowUpdating;
    public event EventHandler<KingbaseRowUpdatedEventArgs>? RowUpdated;

    protected override int Fill(DataSet dataSet, int startRecord, int maxRecords, string srcTable, IDbCommand command, CommandBehavior behavior) =>
        base.Fill(dataSet, startRecord, maxRecords, srcTable, Buffer(command), behavior);
    protected override int Fill(DataTable dataTable, IDbCommand command, CommandBehavior behavior) =>
        base.Fill(dataTable, Buffer(command), behavior);
    protected override int Fill(DataTable[] dataTables, int startRecord, int maxRecords, IDbCommand command, CommandBehavior behavior) =>
        base.Fill(dataTables, startRecord, maxRecords, Buffer(command), behavior);
    protected override DataTable[] FillSchema(DataSet dataSet, SchemaType schemaType, IDbCommand command, string srcTable, CommandBehavior behavior) =>
        base.FillSchema(dataSet, schemaType, Buffer(command), srcTable, behavior);
    protected override DataTable? FillSchema(DataTable dataTable, SchemaType schemaType, IDbCommand command, CommandBehavior behavior) =>
        base.FillSchema(dataTable, schemaType, Buffer(command), behavior);

    static IDbCommand Buffer(IDbCommand command) => command is KingbaseCommand kingbase ? new KingbaseAdapterCommand(kingbase) : command;
    protected override RowUpdatingEventArgs CreateRowUpdatingEvent(DataRow row, IDbCommand? command,
        StatementType statementType, DataTableMapping tableMapping) => new KingbaseRowUpdatingEventArgs(row, command, statementType, tableMapping);
    protected override RowUpdatedEventArgs CreateRowUpdatedEvent(DataRow row, IDbCommand? command,
        StatementType statementType, DataTableMapping tableMapping) => new KingbaseRowUpdatedEventArgs(row, command, statementType, tableMapping);
    protected override void OnRowUpdating(RowUpdatingEventArgs value)
    {
        base.OnRowUpdating(value);
        var args = new KingbaseRowUpdatingEventArgs(value.Row, value.Command, value.StatementType, value.TableMapping)
        { Status = value.Status, Errors = value.Errors };
        RowUpdating?.Invoke(this, args);
        value.Command = args.Command is { } command ? Buffer(command) : null;
        value.Status = args.Status; value.Errors = args.Errors;
    }
    protected override void OnRowUpdated(RowUpdatedEventArgs value)
    {
        base.OnRowUpdated(value);
        if (value is KingbaseRowUpdatedEventArgs args) RowUpdated?.Invoke(this, args);
    }
}

public sealed class KingbaseRowUpdatingEventArgs : RowUpdatingEventArgs
{
    public KingbaseRowUpdatingEventArgs(DataRow row, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping)
        : base(row, KingbaseAdapterCommand.Unwrap(command), statementType, tableMapping) { }
    public new KingbaseCommand? Command { get => KingbaseAdapterCommand.Unwrap(base.Command); set => base.Command = value; }
}

public sealed class KingbaseRowUpdatedEventArgs : RowUpdatedEventArgs
{
    public KingbaseRowUpdatedEventArgs(DataRow row, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping)
        : base(row, KingbaseAdapterCommand.Unwrap(command), statementType, tableMapping) { }
    public new KingbaseCommand? Command => KingbaseAdapterCommand.Unwrap(base.Command);
}
