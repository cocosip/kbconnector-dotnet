using System.Data;
using System.Data.Common;

namespace KingbaseConnector.IntegrationTests;

public sealed class ReaderSchemaLiveTests
{
    [LiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PgDeclaredReaderFacetsRemainStableForNullValuesAndEmptyPreparedResults(bool prepared) => LiveProfile.RunPg(async (settings, token) =>
    {
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        foreach (var empty in new[] { false, true })
        {
            using var command = new KingbaseCommand("SELECT CAST(@v AS varchar(17)) AS \"alias汉\", NULL::char(5), " +
                "NULL::numeric(12,3), NULL::numeric(1000,0), NULL::numeric, NULL::text WHERE " + (empty ? "false" : "true"), connection);
            command.Parameters.Add(new KingbaseParameter("v", "汉😀") { KingbaseDbType = KingbaseDbType.Text });
            if (prepared) await command.PrepareAsync(token);
            for (var iteration = 0; iteration < 2; iteration++)
            {
                if (iteration != 0) command.Parameters[0].Value = DBNull.Value;
                using DbDataReader reader = await command.ExecuteReaderAsync(token);
                var columns = reader.GetColumnSchema();
                Assert.Equal(6, columns.Count);
                Assert.Equal("alias汉", columns[0].ColumnName);
                Assert.Equal(17, columns[0].ColumnSize); Assert.Equal(5, columns[1].ColumnSize);
                Assert.Equal(12, columns[2].NumericPrecision); Assert.Equal(3, columns[2].NumericScale);
                Assert.Equal(1000, columns[3].NumericPrecision); Assert.Equal(0, columns[3].NumericScale);
                Assert.Null(columns[4].NumericPrecision); Assert.Null(columns[4].NumericScale);
                Assert.Null(columns[5].ColumnSize);
                using var table = reader.GetSchemaTable(); Assert.NotNull(table);
                Assert.Equal(21, table.Rows[0]["TypeModifier"]); Assert.Equal(9, table.Rows[1]["TypeModifier"]);
                Assert.Equal(786439, table.Rows[2]["TypeModifier"]);
                for (var i = 0; i < columns.Count; i++)
                {
                    Assert.Null(columns[i].AllowDBNull); Assert.Null(columns[i].IsKey); Assert.Null(columns[i].BaseTableName);
                    Assert.Equal(columns[i].ColumnSize is { } size ? size : DBNull.Value, table.Rows[i][SchemaTableColumn.ColumnSize]);
                    Assert.Equal(columns[i].NumericPrecision is { } precision ? (short)precision : DBNull.Value, table.Rows[i][SchemaTableColumn.NumericPrecision]);
                    Assert.Equal(columns[i].NumericScale is { } scale ? (short)scale : DBNull.Value, table.Rows[i][SchemaTableColumn.NumericScale]);
                }
                Assert.Equal(!empty, await reader.ReadAsync(token));
                if (!empty) Assert.Equal(iteration != 0, reader.IsDBNull(0));
                Assert.False(await reader.NextResultAsync(token));
            }
        }
    });
}
