namespace KingbaseConnector;

/// <summary>Explicit scalar selectors resolved against the actual session catalog.</summary>
public enum KingbaseDbType
{
    Boolean = 1, Smallint, Integer, Bigint, Oid, Real, Double, Numeric,
    Text, Varchar, Char, Bytea, Uuid, Json, Jsonb, Date, Time, TimeTz, Timestamp, TimestampTz, Interval, Xml,
    SignedTinyint, UnsignedTinyint, UnsignedInteger, UnsignedBigint, Int128, VarcharByte, CharByte, NChar, NVarchar, SqlServerBit,
    Bit, Varbit,
    OracleDate, SqlServerDate, SqlServerDateTime, SqlServerDateTime2, SqlServerSmallDateTime, SqlServerTime,
    MySqlDate, MySqlDateTime, MySqlTimestamp, MySqlTime, MySqlYear
}
