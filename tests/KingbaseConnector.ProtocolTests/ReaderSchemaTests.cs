using System.Data;
using System.Data.Common;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class ReaderSchemaTests
{
    [Theory]
    [InlineData("varchar", 21, 17, null, null)]
    [InlineData("bpchar", 9, 5, null, null)]
    [InlineData("numeric", 786439, null, 12, 3)]
    [InlineData("numeric", 329730, null, 5, -2)]
    [InlineData("numeric", 196617, null, 3, 5)]
    [InlineData("numeric", 65537052, null, 1000, -1000)]
    [InlineData("numeric", -1, null, null, null)]
    [InlineData("varchar", -1, null, null, null)]
    [InlineData("text", 21, null, null, null)]
    [InlineData("int4", 786439, null, null, null)]
    public async Task DeclaredFacetsUseActualCatalogTypeAndAgreeAcrossBclSchemaApis(
        string type, int modifier, int? size, int? precision, int? scale)
    {
        var catalog = await PgTextCodecsTests.LoadCatalog(9100);
        var description = catalog.ByName["pg_catalog." + type];
        using var reader = new KingbaseDataReader([new PgTextResult([
            new PgColumnDescription("alias", uint.MaxValue, 7, description.Oid, description.Size, modifier)],
            [[null]], "SELECT 1")], catalog);
        var first = Assert.Single(((IDbColumnSchemaGenerator)reader).GetColumnSchema());
        Assert.Equal(size, first.ColumnSize);
        Assert.Equal(precision, first.NumericPrecision);
        Assert.Equal(scale, first.NumericScale);
        Assert.Equal("alias", first.ColumnName);
        Assert.Null(first.AllowDBNull);
        Assert.Null(first.IsKey);
        Assert.Null(first.BaseTableName);
        Assert.Null(first.BaseColumnName);
        using var table = reader.GetSchemaTable();
        var row = Assert.Single(table.Rows.Cast<DataRow>());
        Assert.Equal(size is null ? DBNull.Value : size.Value, row[SchemaTableColumn.ColumnSize]);
        Assert.Equal(precision is null ? DBNull.Value : (short)precision.Value, row[SchemaTableColumn.NumericPrecision]);
        Assert.Equal(scale is null ? DBNull.Value : (short)scale.Value, row[SchemaTableColumn.NumericScale]);
        Assert.Equal(modifier, row["TypeModifier"]);
        Assert.Equal(description.Oid, row["DataTypeOID"]);
        Assert.Equal(uint.MaxValue, row["BaseTableOID"]);
        Assert.True(reader.Read()); Assert.True(reader.IsDBNull(0));
        Assert.Equal(size, Assert.Single(reader.GetColumnSchema()).ColumnSize);
    }

    [Theory]
    [InlineData("varchar", -2)]
    [InlineData("varchar", 4)]
    [InlineData("numeric", 4)]
    [InlineData("numeric", 65539)]
    [InlineData("numeric", 67588)]
    [InlineData("numeric", 66541)]
    [InlineData("numeric", 66587)]
    [InlineData("numeric", 65601540)]
    public async Task UnknownOrInvalidModifiersDoNotInventFacets(string type, int modifier)
    {
        var catalog = await PgTextCodecsTests.LoadCatalog();
        var description = catalog.ByName["pg_catalog." + type];
        using var reader = new KingbaseDataReader([new PgTextResult([
            new PgColumnDescription("empty", 0, 0, description.Oid, description.Size, modifier)], [], "SELECT 0")], catalog);
        var column = Assert.Single(reader.GetColumnSchema());
        Assert.Null(column.ColumnSize); Assert.Null(column.NumericPrecision); Assert.Null(column.NumericScale);
        using var table = reader.GetSchemaTable();
        foreach (var field in new[] { SchemaTableColumn.ColumnSize, SchemaTableColumn.NumericPrecision, SchemaTableColumn.NumericScale })
            Assert.Same(DBNull.Value, table.Rows[0][field]);
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task ResultTransitionsUseEachDescriptionsFacetsWithoutRowValuesOrExtraQueries()
    {
        var catalog = await PgTextCodecsTests.LoadCatalog();
        var type = catalog.ByName["pg_catalog.varchar"];
        using var reader = new KingbaseDataReader([
            new PgTextResult([new PgColumnDescription("first", 0, 0, type.Oid, -1, 21)], [], "SELECT 0"),
            new PgTextResult([new PgColumnDescription("second", 0, 0, type.Oid, -1, 9)], [["汉😀"]], "SELECT 1")], catalog);
        var first = reader.GetColumnSchema();
        Assert.Equal(17, Assert.Single(first).ColumnSize);
        Assert.True(reader.NextResult()); Assert.True(reader.Read());
        Assert.Equal(5, Assert.Single(reader.GetColumnSchema()).ColumnSize);
        Assert.Equal("汉😀", reader.GetString(0));
        Assert.Equal(17, first[0].ColumnSize);
        Assert.NotSame(first, reader.GetColumnSchema());
        reader.Close(); Assert.Throws<InvalidOperationException>(() => reader.GetColumnSchema());
    }
}
