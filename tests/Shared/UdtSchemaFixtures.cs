namespace KingbaseConnector.Tests;

internal static class UdtSchemaFixtures
{
    internal static readonly string[] Names = ["UserDefinedTypes", "TypeAttributes", "EnumLabels", "DomainConstraints"];
    internal static (string Name, Type Type)[] Fields(string name) => name switch
    {
        "UserDefinedTypes" => [.. Identity(), S("TYPE_KIND_CODE"), S("TYPE_KIND"), S("OWNER"),
            S("BASE_TYPE_CATALOG"), S("BASE_TYPE_SCHEMA"), S("BASE_TYPE_NAME"), S("DOMAIN_DEFAULT"), B("DOMAIN_IS_NULLABLE"), .. Limits(), .. Collation()],
        "TypeAttributes" => [.. Identity(), I("ORDINAL_POSITION"), S("ATTRIBUTE_NAME"), S("ATTRIBUTE_DEFAULT"), B("IS_NULLABLE"), S("DATA_TYPE"),
            .. Limits(), S("ATTRIBUTE_UDT_CATALOG"), S("ATTRIBUTE_UDT_SCHEMA"), S("ATTRIBUTE_UDT_NAME"), .. Collation()],
        "EnumLabels" => [.. Identity(), I("ORDINAL_POSITION"), S("ENUM_LABEL")],
        "DomainConstraints" => [.. Identity(), S("CONSTRAINT_NAME"), S("CONSTRAINT_CATALOG"), S("CONSTRAINT_SCHEMA"),
            S("CONSTRAINT_TYPE_CODE"), S("CONSTRAINT_TYPE"), B("IS_DEFERRABLE"), B("INITIALLY_DEFERRED"), B("IS_VALIDATED"), S("DEFINITION")],
        _ => throw new ArgumentException("Unknown fixture.", nameof(name))
    };
    internal static string?[] Values(string name) => name switch
    {
        "UserDefinedTypes" => ["db", "MiXeD汉%_'", "type", "d", "DOMAIN", "owner", "db", "pg_catalog", "numeric", "42", "NO",
            null, null, "12", "10", "-2", null, null, null, null, null, null],
        "TypeAttributes" => ["db", "MiXeD汉%_'", "type", "2", "field", null, "YES", "ARRAY", null, null, null, null, "-2", null, null, null,
            "db", "pg_catalog", "_int4", null, null, null],
        "EnumLabels" => ["db", "MiXeD汉%_'", "type", "1", ""],
        "DomainConstraints" => ["db", "MiXeD汉%_'", "type", "constraint", "db", "MiXeD汉%_'", "c", "CHECK", "NO", "NO", "YES", "CHECK ((VALUE > 0))"],
        _ => throw new ArgumentException("Unknown fixture.", nameof(name))
    };
    static (string, Type)[] Identity() => [S("UDT_CATALOG"), S("UDT_SCHEMA"), S("UDT_NAME")];
    static (string, Type)[] Limits() => [L("CHARACTER_MAXIMUM_LENGTH"), L("CHARACTER_OCTET_LENGTH"), I("NUMERIC_PRECISION"), I("NUMERIC_PRECISION_RADIX"),
        I("NUMERIC_SCALE"), I("DATETIME_PRECISION"), S("INTERVAL_TYPE"), I("INTERVAL_PRECISION")];
    static (string, Type)[] Collation() => [S("COLLATION_CATALOG"), S("COLLATION_SCHEMA"), S("COLLATION_NAME")];
    static (string, Type) S(string name) => (name, typeof(string));
    static (string, Type) I(string name) => (name, typeof(int));
    static (string, Type) L(string name) => (name, typeof(long));
    static (string, Type) B(string name) => (name, typeof(bool));
}
