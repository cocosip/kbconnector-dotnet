using System.Data;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif

namespace KingbaseConnector;

// BCL adapters request SequentialAccess even when consuming fully buffered rows.
internal sealed class KingbaseAdapterCommand(KingbaseCommand command) : IDbCommand
{
    internal static KingbaseCommand? Unwrap(IDbCommand? value) => value is KingbaseAdapterCommand adapter ? adapter._command : (KingbaseCommand?)value;
    readonly KingbaseCommand _command = command;
#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public string CommandText { get => _command.CommandText; set => _command.CommandText = value; }
    public int CommandTimeout { get => _command.CommandTimeout; set => _command.CommandTimeout = value; }
    public CommandType CommandType { get => _command.CommandType; set => _command.CommandType = value; }
    public IDbConnection? Connection { get => _command.Connection; set => _command.Connection = (KingbaseConnection?)value; }
    public IDbTransaction? Transaction { get => _command.Transaction; set => _command.Transaction = (KingbaseTransaction?)value; }
    public IDataParameterCollection Parameters => _command.Parameters;
    public UpdateRowSource UpdatedRowSource { get => _command.UpdatedRowSource; set => _command.UpdatedRowSource = value; }
    public void Cancel() => _command.Cancel();
    public IDbDataParameter CreateParameter() => _command.CreateParameter();
    public int ExecuteNonQuery() => _command.ExecuteNonQuery();
    public object? ExecuteScalar() => _command.ExecuteScalar();
    public IDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);
    public IDataReader ExecuteReader(CommandBehavior behavior) => _command.ExecuteReader(behavior & ~CommandBehavior.SequentialAccess);
    public void Prepare() => _command.Prepare();
    public void Dispose() { }
}
