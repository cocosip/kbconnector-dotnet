using System.Collections;
using System.Data;
using System.Numerics;

namespace KingbaseConnector;

internal static class KingbaseParameterTypes
{
    internal static KingbaseDbType? Infer(object? value) => value switch
    {
        bool => KingbaseDbType.Boolean, short => KingbaseDbType.Smallint, int => KingbaseDbType.Integer,
        sbyte => KingbaseDbType.SignedTinyint, byte => KingbaseDbType.UnsignedTinyint,
        ulong => KingbaseDbType.UnsignedBigint, BigInteger => KingbaseDbType.Int128,
        BitArray => KingbaseDbType.Varbit,
        KingbaseMySqlDate => KingbaseDbType.MySqlDate, KingbaseMySqlDateTime => KingbaseDbType.MySqlDateTime,
        long => KingbaseDbType.Bigint, uint => KingbaseDbType.Oid, float => KingbaseDbType.Real,
        double => KingbaseDbType.Double, decimal or KingbaseNumeric => KingbaseDbType.Numeric,
        string => KingbaseDbType.Text, byte[] => KingbaseDbType.Bytea, Guid => KingbaseDbType.Uuid,
        KingbaseDate => KingbaseDbType.Date,
        KingbaseTimeTz => KingbaseDbType.TimeTz,
        KingbaseTimestamp => KingbaseDbType.Timestamp,
        KingbaseInterval or TimeSpan => KingbaseDbType.Interval,
        KingbaseTimestampTz or DateTimeOffset => KingbaseDbType.TimestampTz,
        DateTime date when date.Kind == DateTimeKind.Unspecified => KingbaseDbType.Timestamp,
        DateTime date when date.Kind == DateTimeKind.Utc => KingbaseDbType.TimestampTz,
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
        KingbaseDbType.Date => "date", KingbaseDbType.Time => "time", KingbaseDbType.TimeTz => "timetz",
        KingbaseDbType.Timestamp => "timestamp", KingbaseDbType.TimestampTz => "timestamptz",
        KingbaseDbType.Interval => "interval", KingbaseDbType.Xml => "xml",
        KingbaseDbType.Blob => "blob", KingbaseDbType.Clob => "clob", KingbaseDbType.NClob => "nclob",
        KingbaseDbType.SignedTinyint => "tinyint", KingbaseDbType.UnsignedTinyint => "tinyint_unsigned",
        KingbaseDbType.UnsignedInteger => "uint4", KingbaseDbType.UnsignedBigint => "uint8", KingbaseDbType.Int128 => "int16",
        KingbaseDbType.VarcharByte => "varcharbyte", KingbaseDbType.CharByte => "bpcharbyte", KingbaseDbType.NChar => "nchar",
        KingbaseDbType.NVarchar => "nvarchar", KingbaseDbType.SqlServerBit => "sqlserver_bit",
        KingbaseDbType.Bit => "bit", KingbaseDbType.Varbit => "varbit",
        KingbaseDbType.OracleDate => "oracle_date", KingbaseDbType.SqlServerDate => "sqlserver_date",
        KingbaseDbType.SqlServerDateTime => "sqlserver_datetime", KingbaseDbType.SqlServerDateTime2 => "sqlserver_datetime2",
        KingbaseDbType.SqlServerSmallDateTime => "sqlserver_smalldatetime", KingbaseDbType.SqlServerTime => "sqlserver_time",
        KingbaseDbType.MySqlDate => "mysql_date", KingbaseDbType.MySqlDateTime => "mysql_datetime",
        KingbaseDbType.MySqlTimestamp => "mysql_timestamp", KingbaseDbType.MySqlTime => "mysql_time", KingbaseDbType.MySqlYear => "mysql_year", _ => throw Unsupported()
    };

    internal static DbType ToDbType(KingbaseDbType type) => type switch
    {
        KingbaseDbType.Boolean => DbType.Boolean, KingbaseDbType.Smallint => DbType.Int16, KingbaseDbType.Integer => DbType.Int32,
        KingbaseDbType.Bigint => DbType.Int64, KingbaseDbType.Oid => DbType.UInt32, KingbaseDbType.Real => DbType.Single,
        KingbaseDbType.Double => DbType.Double, KingbaseDbType.Numeric => DbType.Decimal, KingbaseDbType.Bytea => DbType.Binary,
        KingbaseDbType.Uuid => DbType.Guid, KingbaseDbType.Char => DbType.StringFixedLength, KingbaseDbType.Date => DbType.Date,
        KingbaseDbType.Time or KingbaseDbType.TimeTz => DbType.Time,
        KingbaseDbType.Timestamp => DbType.DateTime, KingbaseDbType.TimestampTz => DbType.DateTimeOffset,
        KingbaseDbType.Interval => DbType.Object,
        KingbaseDbType.Xml => DbType.Xml,
        KingbaseDbType.Blob => DbType.Binary, KingbaseDbType.Clob or KingbaseDbType.NClob => DbType.String,
        KingbaseDbType.SignedTinyint => DbType.SByte, KingbaseDbType.UnsignedTinyint => DbType.Byte,
        KingbaseDbType.UnsignedInteger => DbType.UInt32, KingbaseDbType.UnsignedBigint => DbType.UInt64,
        KingbaseDbType.Int128 or KingbaseDbType.Bit or KingbaseDbType.Varbit => DbType.Object, KingbaseDbType.SqlServerBit => DbType.Boolean,
        KingbaseDbType.OracleDate or KingbaseDbType.SqlServerDateTime or KingbaseDbType.SqlServerSmallDateTime or KingbaseDbType.MySqlDateTime or KingbaseDbType.MySqlTimestamp => DbType.DateTime,
        KingbaseDbType.SqlServerDateTime2 => DbType.DateTime2,
        KingbaseDbType.SqlServerDate or KingbaseDbType.MySqlDate => DbType.Date,
        KingbaseDbType.SqlServerTime or KingbaseDbType.MySqlTime => DbType.Time,
        KingbaseDbType.MySqlYear => DbType.Int32,
        KingbaseDbType.CharByte or KingbaseDbType.NChar => DbType.StringFixedLength,
        KingbaseDbType.VarcharByte or KingbaseDbType.NVarchar => DbType.String,
        KingbaseDbType.Text or KingbaseDbType.Varchar or KingbaseDbType.Json or KingbaseDbType.Jsonb => DbType.String, _ => throw Unsupported()
    };

    internal static KingbaseDbType FromDbType(DbType type) => type switch
    {
        DbType.Boolean => KingbaseDbType.Boolean, DbType.Int16 => KingbaseDbType.Smallint, DbType.Int32 => KingbaseDbType.Integer,
        DbType.SByte => KingbaseDbType.SignedTinyint, DbType.Byte => KingbaseDbType.UnsignedTinyint,
        DbType.UInt64 => KingbaseDbType.UnsignedBigint,
        DbType.Int64 => KingbaseDbType.Bigint, DbType.UInt32 => KingbaseDbType.Oid, DbType.Single => KingbaseDbType.Real,
        DbType.Double => KingbaseDbType.Double, DbType.Decimal or DbType.VarNumeric => KingbaseDbType.Numeric,
        DbType.String => KingbaseDbType.Text, DbType.StringFixedLength => KingbaseDbType.Char,
        DbType.Binary => KingbaseDbType.Bytea, DbType.Guid => KingbaseDbType.Uuid, DbType.Date => KingbaseDbType.Date,
        DbType.Time => KingbaseDbType.Time, DbType.DateTime or DbType.DateTime2 => KingbaseDbType.Timestamp,
        DbType.DateTimeOffset => KingbaseDbType.TimestampTz, DbType.Xml => KingbaseDbType.Xml, _ => throw Unsupported()
    };

    internal static bool Compatible(DbType constraint, KingbaseDbType selected) => constraint switch
    {
        DbType.String => selected is KingbaseDbType.Text or KingbaseDbType.Varchar or KingbaseDbType.Char or KingbaseDbType.Json or KingbaseDbType.Jsonb or KingbaseDbType.Xml or
            KingbaseDbType.VarcharByte or KingbaseDbType.CharByte or KingbaseDbType.NChar or KingbaseDbType.NVarchar or KingbaseDbType.Clob or KingbaseDbType.NClob,
        DbType.Binary => selected is KingbaseDbType.Bytea or KingbaseDbType.Blob,
        DbType.StringFixedLength => selected is KingbaseDbType.Char or KingbaseDbType.CharByte or KingbaseDbType.NChar,
        DbType.SByte => selected is KingbaseDbType.SignedTinyint or KingbaseDbType.Smallint,
        DbType.Byte => selected is KingbaseDbType.UnsignedTinyint or KingbaseDbType.UnsignedInteger or KingbaseDbType.Smallint,
        DbType.UInt32 => selected is KingbaseDbType.Oid or KingbaseDbType.UnsignedInteger or KingbaseDbType.Bigint,
        DbType.UInt64 => selected is KingbaseDbType.UnsignedBigint or KingbaseDbType.Int128,
        DbType.Boolean => selected is KingbaseDbType.Boolean or KingbaseDbType.SqlServerBit,
        DbType.Object => selected is KingbaseDbType.Int128 or KingbaseDbType.Interval or KingbaseDbType.Bit or KingbaseDbType.Varbit,
        DbType.VarNumeric => selected == KingbaseDbType.Numeric,
        DbType.Int32 => selected is KingbaseDbType.Integer or KingbaseDbType.MySqlYear,
        DbType.Date => selected is KingbaseDbType.Date or KingbaseDbType.SqlServerDate or KingbaseDbType.MySqlDate,
        DbType.Time => selected is KingbaseDbType.Time or KingbaseDbType.TimeTz or KingbaseDbType.SqlServerTime or KingbaseDbType.MySqlTime,
        DbType.DateTime => selected is KingbaseDbType.Timestamp or KingbaseDbType.OracleDate or KingbaseDbType.SqlServerDateTime or KingbaseDbType.SqlServerDateTime2 or KingbaseDbType.SqlServerSmallDateTime or KingbaseDbType.MySqlDateTime or KingbaseDbType.MySqlTimestamp,
        DbType.DateTime2 => selected is KingbaseDbType.Timestamp or KingbaseDbType.SqlServerDateTime2 or KingbaseDbType.MySqlDateTime or KingbaseDbType.MySqlTimestamp,
        _ => FromDbType(constraint) == selected
    };
    internal static NotSupportedException Unsupported() => new("Unsupported Kingbase parameter type.");
}
