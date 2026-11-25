using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class DirectProjectionTests
{
    [Theory]
    [InlineData("SELECT id, name AS label FROM public.items", "public", "items", false)]
    [InlineData("SELECT U&\"i\\0064\" FROM U&\"pub\\006cic\".U&\"it\\0022ems\"", "public", "it\"ems", false)]
    [InlineData("SELECT t.* FROM ONLY public.items AS t WHERE id=$1 ORDER BY t.id DESC LIMIT 5;", "public", "items", true)]
    [InlineData("SELECT \"from\" AS \"join\" FROM \"汉\".\"MiXeD\" WHERE \"from\"='UNION ( join'", "汉", "MiXeD", false)]
    [InlineData("/* nested /* x */ y */SELECT DISTINCT id FROM app.public.items i -- tail\n WHERE id>0", "public", "items", false)]
    public void RecognizedProjectionMatchesOnlyTheResolvedRelation(string sql, string schema, string table, bool only)
    {
        var shape = Assert.IsType<PgDirectProjection>(PgDirectProjection.Parse(sql));
        Assert.True(shape.Matches("app", schema, table, ["id", "from"])); Assert.False(shape.Matches("app", schema, "another", ["id"])); Assert.Equal(only, shape.Only);
    }

    [Theory]
    [InlineData("SELECT t.id FROM public.items t LEFT JOIN public.items u ON true")]
    [InlineData("SELECT t.id FROM public.items t, public.items u")]
    [InlineData("SELECT id, generate_series(1,2) FROM public.items")]
    [InlineData("SELECT id FROM public.items ORDER BY generate_series(1,2)")]
    [InlineData("SELECT t.id FROM public.items t ORDER BY t.id + t.computed_field")]
    [InlineData("SELECT t.id FROM public.items t ORDER BY t.id DESC + t.computed_field")]
    [InlineData("SELECT t.id FROM public.items t ORDER BY t.id COLLATE custom")]
    [InlineData("SELECT id FROM public.items GROUP BY ROLLUP(id)")]
    [InlineData("SELECT id FROM public.items WHERE true UNION SELECT id FROM public.items")]
    [InlineData("WITH x AS (SELECT * FROM public.items) SELECT id FROM x")]
    [InlineData("SELECT id FROM (SELECT * FROM public.items) t")]
    [InlineData("SELECT id FROM public.items WHERE EXISTS(SELECT 1)")]
    [InlineData("SELECT id FROM public.items TABLESAMPLE SYSTEM(50)")]
    [InlineData("SELECT id FROM public.items WHERE id=E'foo\\\\bar'")]
    [InlineData("SELECT id FROM public.items WHERE id=$x$UNION$x$")]
    [InlineData("SELECT id FROM public.未验证")]
    public void ComplexOrUnprovenShapesRemainUnknown(string sql) => Assert.Null(PgDirectProjection.Parse(sql));

    [Theory]
    [InlineData("SELECT t.id FROM public.items t ORDER BY t.computed_field")]
    [InlineData("SELECT t.id AS computed_field FROM public.items t ORDER BY t.computed_field")]
    public void QualifiedSortsMustNameVerifiedBaseColumnsRatherThanComputedFields(string sql)
    {
        var shape = Assert.IsType<PgDirectProjection>(PgDirectProjection.Parse(sql));
        Assert.False(shape.Matches("app", "public", "items", ["id"], ["computed_field"]));
    }
}
