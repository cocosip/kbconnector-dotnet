using System.Data;

namespace KingbaseConnector;

internal static class KingbaseParameterTypes
{
    internal static KingbaseDbType? Infer(object? value) => value switch
    {
        bool => KingbaseDbType.Boolean, short => KingbaseDbType.Smallint, int => KingbaseDbType.Integer,
        long => KingbaseDbType.Bigint, uint => KingbaseDbType.Oid, float => KingbaseDbType.Real,
        double => KingbaseDbType.Double, decimal or KingbaseNumeric => KingbaseDbType.Numeric,
        string => KingbaseDbType.Text, byte[] => KingbaseDbType.Bytea, Guid => KingbaseDbType.Uuid,
        KingbaseDate => KingbaseDbType.Date,
        KingbaseTimeTz => KingbaseDbType.TimeTz,
#if NET6_0_OR_GREATER
        DateOnly => KingbaseDbType.Date,
        TimeOnly => KingbaseDbType.Time,
#endif
        _ => null
    };

    internal static string Name(KingbaseDbType type) => type switch
    {
        KingbaseDbType.Boolean => "bool", KingbaseDbType.Smallint => "int2", KingbaseDbType.Integer => "int4",
        KingbaseDbType.Bigint => "int8", KingbaseDbType.Oid => "oid", KingbaseDbType.Real => "float4",
        KingbaseDbType.Double => "float8", KingbaseDbType.Numeric => "numeric", KingbaseDbType.Text => "text",
        KingbaseDbType.Varchar => "varchar", KingbaseDbType.Char => "bpchar", KingbaseDbType.Bytea => "bytea",
        KingbaseDbType.Uuid => "uuid", KingbaseDbType.Json => "json", KingbaseDbType.Jsonb => "jsonb",
        KingbaseDbType.Date => "date", KingbaseDbType.Time => "time", KingbaseDbType.TimeTz => "timetz", _ => throw Unsupported()
    };

    internal static DbType ToDbType(KingbaseDbType type) => type switch
    {
        KingbaseDbType.Boolean => DbType.Boolean, KingbaseDbType.Smallint => DbType.Int16, KingbaseDbType.Integer => DbType.Int32,
        KingbaseDbType.Bigint => DbType.Int64, KingbaseDbType.Oid => DbType.UInt32, KingbaseDbType.Real => DbType.Single,
        KingbaseDbType.Double => DbType.Double, KingbaseDbType.Numeric => DbType.Decimal, KingbaseDbType.Bytea => DbType.Binary,
        KingbaseDbType.Uuid => DbType.Guid, KingbaseDbType.Char => DbType.StringFixedLength, KingbaseDbType.Date => DbType.Date,
        KingbaseDbType.Time or KingbaseDbType.TimeTz => DbType.Time,
        KingbaseDbType.Text or KingbaseDbType.Varchar or KingbaseDbType.Json or KingbaseDbType.Jsonb => DbType.String, _ => throw Unsupported()
    };

    internal static KingbaseDbType FromDbType(DbType type) => type switch
    {
        DbType.Boolean => KingbaseDbType.Boolean, DbType.Int16 => KingbaseDbType.Smallint, DbType.Int32 => KingbaseDbType.Integer,
        DbType.Int64 => KingbaseDbType.Bigint, DbType.UInt32 => KingbaseDbType.Oid, DbType.Single => KingbaseDbType.Real,
        DbType.Double => KingbaseDbType.Double, DbType.Decimal or DbType.VarNumeric => KingbaseDbType.Numeric,
        DbType.String => KingbaseDbType.Text, DbType.StringFixedLength => KingbaseDbType.Char,
        DbType.Binary => KingbaseDbType.Bytea, DbType.Guid => KingbaseDbType.Uuid, DbType.Date => KingbaseDbType.Date,
        DbType.Time => KingbaseDbType.Time, _ => throw Unsupported()
    };

    internal static bool Compatible(DbType constraint, KingbaseDbType selected) => constraint switch
    {
        DbType.String => selected is KingbaseDbType.Text or KingbaseDbType.Varchar or KingbaseDbType.Char or KingbaseDbType.Json or KingbaseDbType.Jsonb,
        DbType.VarNumeric => selected == KingbaseDbType.Numeric,
        DbType.Time => selected is KingbaseDbType.Time or KingbaseDbType.TimeTz,
        _ => FromDbType(constraint) == selected
    };
    internal static NotSupportedException Unsupported() => new("Unsupported Kingbase parameter type.");
}
