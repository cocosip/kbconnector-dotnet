using System.Data;
using System.Data.Common;
#if !NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
#endif

namespace KingbaseConnector;

/// <summary>Describes a parameter value and explicit type constraints.</summary>
public sealed class KingbaseParameter : DbParameter
{
    string _name = "";
    string _sourceColumn = "";
    ParameterDirection _direction = ParameterDirection.Input;
    DataRowVersion _sourceVersion = DataRowVersion.Current;
    int _size;
    byte _scale;
    internal KingbaseParameterCollection? Owner { get; set; }
    internal DbType? ExplicitDbType { get; private set; }
    internal KingbaseDbType? ExplicitProviderType { get; private set; }
    internal bool ScaleSpecified { get; private set; }
    public KingbaseParameter() { }
    public KingbaseParameter(string? name, object? value) { ParameterName = name!; Value = value; }
    public override DbType DbType
    {
        get
        {
            if (ExplicitDbType is { } selected) return selected;
            if ((ExplicitProviderType ?? KingbaseParameterTypes.Infer(Value)) is { } type) return KingbaseParameterTypes.ToDbType(type);
            return Value is null or DBNull ? DbType.String : DbType.Object;
        }
        set
        {
            if (!Enum.IsDefined(typeof(DbType), value)) throw new ArgumentOutOfRangeException(nameof(value));
            ExplicitDbType = value;
        }
    }
    public KingbaseDbType KingbaseDbType
    {
        get => ExplicitProviderType ?? (ExplicitDbType.HasValue ? KingbaseParameterTypes.FromDbType(ExplicitDbType.Value) :
            KingbaseParameterTypes.Infer(Value) ?? (Value is null or DBNull ? KingbaseDbType.Text : throw KingbaseParameterTypes.Unsupported()));
        set
        {
            if (!Enum.IsDefined(typeof(KingbaseDbType), value)) throw new ArgumentOutOfRangeException(nameof(value));
            ExplicitProviderType = value;
        }
    }
    public string? DataTypeName { get; set; }
    public override ParameterDirection Direction
    {
        get => _direction;
        set
        {
            if (!Enum.IsDefined(typeof(ParameterDirection), value)) throw new ArgumentOutOfRangeException(nameof(value));
            _direction = value;
        }
    }
    public override bool IsNullable { get; set; }
#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public override string ParameterName
    {
        get => _name;
        set { var name = value ?? ""; Owner?.ValidateName(this, name); _name = name; }
    }
#if !NETSTANDARD2_0
    [AllowNull]
#endif
    public override string SourceColumn { get => _sourceColumn; set => _sourceColumn = value ?? ""; }
    public override DataRowVersion SourceVersion
    {
        get => _sourceVersion;
        set
        {
            if (!Enum.IsDefined(typeof(DataRowVersion), value)) throw new ArgumentOutOfRangeException(nameof(value));
            _sourceVersion = value;
        }
    }
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    /// <summary>Maximum UTF16 characters or bytes; zero leaves input size unconstrained. Never truncates.</summary>
    public override int Size
    {
        get => _size;
        set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _size = value; }
    }
    public override byte Precision { get; set; }
    public override byte Scale { get => _scale; set { _scale = value; ScaleSpecified = true; } }
    public override void ResetDbType() { ExplicitDbType = null; ExplicitProviderType = null; DataTypeName = null; }

    internal KingbaseParameter Snapshot()
    {
        var copy = (KingbaseParameter)MemberwiseClone();
        copy.Owner = null;
        return copy;
    }
}
