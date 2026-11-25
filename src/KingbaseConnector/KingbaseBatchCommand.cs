using System.Data;
using System.Data.Common;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>The execution acknowledgment of one sequential batch command, independent of transaction durability.</summary>
public enum KingbaseBatchCommandStatus { NotExecuted, Succeeded, Failed, OutcomeUnknown }

/// <summary>A single Text statement and its own parameters in a sequential batch.</summary>
public sealed class KingbaseBatchCommand
#if NET6_0_OR_GREATER
    : DbBatchCommand
#endif
{
    string _text = "";
    CommandType _type = CommandType.Text;
    readonly KingbaseParameterCollection _parameters = new();
    internal KingbaseBatchCommandCollection? Owner { get; set; }
    internal PgPreparedStatement? Prepared { get; set; }
    internal long PreparedGeneration { get; set; }
    internal bool RequiresPgMetadata { get; set; }
    internal int Affected = -1;
    public KingbaseBatchCommand() { }
    public KingbaseBatchCommand(string? commandText) => _text = commandText ?? "";
    public
#if NET6_0_OR_GREATER
        override
#endif
        string CommandText
    {
        get => _text;
        set => Edit(() => { if (_text != (value ?? "")) Prepared = null; _text = value ?? ""; });
    }
    public
#if NET6_0_OR_GREATER
        override
#endif
        CommandType CommandType
    {
        get => _type;
        set => Edit(() => { if (!Enum.IsDefined(typeof(CommandType), value)) throw new ArgumentOutOfRangeException(nameof(value)); if (_type != value) Prepared = null; _type = value; });
    }
    public
#if NET6_0_OR_GREATER
        override
#endif
        int RecordsAffected => Affected;
    public KingbaseBatchCommandStatus ExecutionStatus { get; internal set; }
#if NET6_0_OR_GREATER
    protected override DbParameterCollection DbParameterCollection => _parameters;
    public new KingbaseParameterCollection Parameters => _parameters;
    public override bool CanCreateParameter => true;
    public override KingbaseParameter CreateParameter() => new();
#else
    public KingbaseParameterCollection Parameters => _parameters;
    public bool CanCreateParameter => true;
    public KingbaseParameter CreateParameter() => new();
#endif
    void Edit(Action action)
    {
        if (Owner is not { } owner) { action(); return; }
        lock (owner.Batch.SyncRoot) { owner.Batch.EnsureEditable(); action(); }
    }
}
