using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public sealed class CompatibilityParameterPlanTests
{
    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle, false)]
    [InlineData(KingbaseCompatibilityMode.MySql, false)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, false)]
    [InlineData(KingbaseCompatibilityMode.Oracle, true)]
    [InlineData(KingbaseCompatibilityMode.MySql, true)]
    [InlineData(KingbaseCompatibilityMode.SqlServer, true)]
    public Task PublicPreparedAndAutomaticExecutionUsesDetectedMode(KingbaseCompatibilityMode mode, bool automatic) =>
        KingbaseConnector.Tests.PreparedAssetProbe.RunAsync(mode: mode, automatic: automatic);

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "SELECT q'[it's :hidden; @hidden $9]', :p")]
    [InlineData(KingbaseCompatibilityMode.Oracle, "SELECT Q'!:hidden ' @hidden;!', :p")]
    [InlineData(KingbaseCompatibilityMode.MySql, "SELECT `:hidden;``@hidden`, :p # :hidden; $9")]
    [InlineData(KingbaseCompatibilityMode.MySql, "SELECT 1 <@`:hidden;`, :p")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "SELECT [a:missing;]]@hidden], :p")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "SELECT @@version, :p")]
    public void ModeBodiesPreserveHiddenParameters(KingbaseCompatibilityMode mode, string sql)
    {
        var plan = PgParameterPlan.Create(sql, ["p"], mode, true);
        Assert.Equal(sql.Replace(":p", "$1"), plan.Sql);
        Assert.Equal(new[] { 0 }, plan.ParameterIndexes);
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public void NativeAndRepeatedNamedSlotsRetainBounds(KingbaseCompatibilityMode mode)
    {
        Assert.Equal("SELECT $1,$1", PgParameterPlan.Create("SELECT :p,@p", ["p"], mode, true).Sql);
        Assert.Equal("SELECT $2,$1", PgParameterPlan.Create("SELECT $2,$1", [null, null], mode, true).Sql);
        Assert.Throws<InvalidOperationException>(() => PgParameterPlan.Create("SELECT :p", ["p"], mode, null));
        Assert.Throws<ArgumentException>(() => PgParameterPlan.Create("SELECT :p; SELECT 2", ["p"], mode, true));
    }

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle, "SELECT q'[unterminated :p'")]
    [InlineData(KingbaseCompatibilityMode.MySql, "SELECT `unterminated :p")]
    [InlineData(KingbaseCompatibilityMode.MySql, "SELECT /*! :p */ 1")]
    [InlineData(KingbaseCompatibilityMode.MySql, "SELECT /*M! :p */ 1")]
    [InlineData(KingbaseCompatibilityMode.SqlServer, "SELECT [unterminated :p")]
    public void UncertainOrUnterminatedModeBodiesReject(KingbaseCompatibilityMode mode, string sql) =>
        Assert.Throws<ArgumentException>(() => PgParameterPlan.Create(sql, ["p"], mode, true));

    [Theory]
    [InlineData("SELECT /*! 1 */ 1")]
    [InlineData("SELECT /*M! 1 */ 1")]
    [InlineData("SELECT /*m! 1 */ 1")]
    [InlineData("SELECT `a\\b`")]
    [InlineData("SELECT \"a\\b\"")]
    [InlineData("SELECT 'a\\b'")]
    [InlineData("SELECT N'a\\b'")]
    [InlineData("SELECT E'a\\b'")]
    [InlineData("SELECT U&'a\\0062'")]
    public void MysqlUnconfirmedSyntaxRejectsWithoutAnUnrelatedBindingFailure(string sql) =>
        Assert.Throws<ArgumentException>(() => PgParameterPlan.Create(sql, [], KingbaseCompatibilityMode.MySql, true));

    [Theory]
    [InlineData("SELECT @`a:p;`, :p", "SELECT @`a:p;`, $1")]
    [InlineData("SELECT @'a:p;', :p", "SELECT @'a:p;', $1")]
    [InlineData("SELECT @\"a:p;\", :p", "SELECT @\"a:p;\", $1")]
    public void MysqlQuotedVariablesRemainServerOwned(string sql, string expected) =>
        Assert.Equal(expected, PgParameterPlan.Create(sql, ["p"], KingbaseCompatibilityMode.MySql, true).Sql);

    [Theory]
    [InlineData(KingbaseCompatibilityMode.Oracle)]
    [InlineData(KingbaseCompatibilityMode.MySql)]
    [InlineData(KingbaseCompatibilityMode.SqlServer)]
    public void UnverifiedNonPgTransactionChainingRejects(KingbaseCompatibilityMode mode) =>
        Assert.Throws<NotSupportedException>(() => PgParameterPlan.Create("COMMIT AND CHAIN", [], mode, true));
}
