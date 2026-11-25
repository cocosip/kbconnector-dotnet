namespace KingbaseConnector.UnitTests;

public sealed class NativeScalarParameterTests
{
    [Theory]
    [InlineData(KingbaseDbType.SignedTinyint, System.Data.DbType.SByte)]
    [InlineData(KingbaseDbType.UnsignedTinyint, System.Data.DbType.Byte)]
    [InlineData(KingbaseDbType.UnsignedInteger, System.Data.DbType.UInt32)]
    [InlineData(KingbaseDbType.UnsignedBigint, System.Data.DbType.UInt64)]
    [InlineData(KingbaseDbType.Int128, System.Data.DbType.Object)]
    [InlineData(KingbaseDbType.VarcharByte, System.Data.DbType.String)]
    [InlineData(KingbaseDbType.CharByte, System.Data.DbType.StringFixedLength)]
    [InlineData(KingbaseDbType.NChar, System.Data.DbType.StringFixedLength)]
    [InlineData(KingbaseDbType.NVarchar, System.Data.DbType.String)]
    [InlineData(KingbaseDbType.SqlServerBit, System.Data.DbType.Boolean)]
    public void ExplicitNativeSelectorsHaveStableAdoTypes(KingbaseDbType provider, System.Data.DbType expected) =>
        Assert.Equal(expected, new KingbaseParameter { KingbaseDbType = provider }.DbType);

    [Fact]
    public void InferenceAddsIntegerFamiliesAndPreservesExistingOidInference()
    {
        Assert.Equal(KingbaseDbType.Oid, new KingbaseParameter("p", uint.MaxValue).KingbaseDbType);
        Assert.Equal(KingbaseDbType.SignedTinyint, new KingbaseParameter("p", sbyte.MinValue).KingbaseDbType);
        Assert.Equal(KingbaseDbType.UnsignedTinyint, new KingbaseParameter("p", byte.MaxValue).KingbaseDbType);
        Assert.Equal(KingbaseDbType.UnsignedBigint, new KingbaseParameter("p", ulong.MaxValue).KingbaseDbType);
        Assert.Equal(KingbaseDbType.Int128, new KingbaseParameter("p", System.Numerics.BigInteger.One).KingbaseDbType);
    }

    [Fact]
    public void NativeSelectorsAppendWithoutRenumberingExistingValues()
    {
        Assert.Equal(22, (int)KingbaseDbType.Xml);
        foreach (var name in new[] { "SignedTinyint", "UnsignedTinyint", "UnsignedInteger", "UnsignedBigint", "Int128", "VarcharByte", "CharByte", "NChar", "NVarchar", "SqlServerBit" })
        {
            Assert.True(Enum.TryParse<KingbaseDbType>(name, out var value)); Assert.True((int)value > 22);
        }
    }
}
