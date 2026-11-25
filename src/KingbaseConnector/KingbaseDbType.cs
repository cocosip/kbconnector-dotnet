namespace KingbaseConnector;

/// <summary>Explicit scalar types supported by the initial PG text binding path.</summary>
public enum KingbaseDbType
{
    Boolean = 1, Smallint, Integer, Bigint, Oid, Real, Double, Numeric,
    Text, Varchar, Char, Bytea, Uuid, Json, Jsonb, Date, Time, TimeTz, Timestamp, TimestampTz, Interval
}
