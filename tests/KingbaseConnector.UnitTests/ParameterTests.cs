using System.Data;
using System.Data.Common;

namespace KingbaseConnector.UnitTests;

public sealed class ParameterTests
{
    [Fact]
    public void ZeroValueConstructorPreservesExactClrTypes()
    {
        var values = new[] {
            new KingbaseParameter("p", 0), new KingbaseParameter("p", 0L),
            new KingbaseParameter("p", 0U), new KingbaseParameter("p", (short)0),
            new KingbaseParameter("p", (byte)0) };
        Assert.IsType<int>(values[0].Value);
        Assert.IsType<long>(values[1].Value);
        Assert.IsType<uint>(values[2].Value);
        Assert.IsType<short>(values[3].Value);
        Assert.IsType<byte>(values[4].Value);
    }

    [Fact]
    public void DefaultsAndResetKeepValueMetadataButRemoveExplicitTypeSelectors()
    {
        var p = new KingbaseParameter();
        Assert.Equal("", p.ParameterName);
        Assert.Equal("", p.SourceColumn);
        Assert.Equal(ParameterDirection.Input, p.Direction);
        Assert.Equal(DbType.String, p.DbType);
        Assert.Equal(KingbaseDbType.Text, p.KingbaseDbType);
        Assert.Null(p.Value);
        p.ParameterName = null;
        p.SourceColumn = null;
        Assert.Equal("", p.ParameterName);
        Assert.Equal("", p.SourceColumn);
        p.Value = 42;
        Assert.Equal(DbType.Int32, p.DbType);
        Assert.Equal(KingbaseDbType.Integer, p.KingbaseDbType);
        p.DbType = DbType.Int64;
        p.KingbaseDbType = KingbaseDbType.Smallint;
        p.DataTypeName = "int4";
        p.Precision = 8;
        p.Scale = 2;
        p.ResetDbType();
        Assert.Equal(DbType.Int32, p.DbType);
        Assert.Equal(KingbaseDbType.Integer, p.KingbaseDbType);
        Assert.Null(p.DataTypeName);
        Assert.Equal(42, p.Value);
        Assert.Equal(8, p.Precision);
        Assert.Equal(2, p.Scale);
    }

    [Theory]
    [InlineData(true, DbType.Boolean, KingbaseDbType.Boolean)]
    [InlineData((short)1, DbType.Int16, KingbaseDbType.Smallint)]
    [InlineData(1, DbType.Int32, KingbaseDbType.Integer)]
    [InlineData(1L, DbType.Int64, KingbaseDbType.Bigint)]
    [InlineData(1U, DbType.UInt32, KingbaseDbType.Oid)]
    [InlineData(1f, DbType.Single, KingbaseDbType.Real)]
    [InlineData(1d, DbType.Double, KingbaseDbType.Double)]
    [InlineData("text", DbType.String, KingbaseDbType.Text)]
    public void ClrTypeInferenceIsExact(object value, DbType dbType, KingbaseDbType provider)
    {
        DbParameter p = new KingbaseParameter("p", value);
        Assert.Equal(dbType, p.DbType);
        Assert.Equal(provider, ((KingbaseParameter)p).KingbaseDbType);
        Assert.Same(value, p.Value);
    }

    [Fact]
    public void AdditionalValueMappingsAndExplicitGettersKeepAssignedConstraints()
    {
        foreach (var pair in new (object Value, DbType Type, KingbaseDbType Provider)[] {
            (1.2300m, DbType.Decimal, KingbaseDbType.Numeric), (KingbaseNumeric.Parse("1.000"), DbType.Decimal, KingbaseDbType.Numeric),
            (Guid.NewGuid(), DbType.Guid, KingbaseDbType.Uuid), (new byte[] { 1 }, DbType.Binary, KingbaseDbType.Bytea) })
        {
            var p = new KingbaseParameter("p", pair.Value);
            Assert.Equal(pair.Type, p.DbType);
            Assert.Equal(pair.Provider, p.KingbaseDbType);
        }
        var selected = new KingbaseParameter("p", "{}") { KingbaseDbType = KingbaseDbType.Jsonb };
        Assert.Equal(DbType.String, selected.DbType);
        selected.DbType = DbType.Int32;
        Assert.Equal(DbType.Int32, selected.DbType);
        Assert.Equal(KingbaseDbType.Jsonb, selected.KingbaseDbType);
        selected.Direction = ParameterDirection.ReturnValue;
        Assert.Equal(ParameterDirection.ReturnValue, selected.Direction);
    }

    [Fact]
    public void InvalidEnumsAndNegativeSizeDoNotChangePriorMetadata()
    {
        var p = new KingbaseParameter("p", 1) { Size = 10 };
        Assert.Throws<ArgumentOutOfRangeException>(() => p.DbType = (DbType)999);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.KingbaseDbType = (KingbaseDbType)999);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Direction = (ParameterDirection)999);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Size = -1);
        Assert.Equal(10, p.Size);
        Assert.Equal(ParameterDirection.Input, p.Direction);
        Assert.Equal(DbType.Int32, p.DbType);
    }

    [Fact]
    public void BclSourceVersionMetadataRoundTripsAndRejectsInvalidEnums()
    {
        DbParameter p = new KingbaseParameter();
        Assert.Equal(DataRowVersion.Current, p.SourceVersion);
        p.SourceVersion = DataRowVersion.Original;
        Assert.Equal(DataRowVersion.Original, p.SourceVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.SourceVersion = (DataRowVersion)99);
        Assert.Equal(DataRowVersion.Original, p.SourceVersion);
    }

    [Fact]
    public void CollectionUsesTypedBclAccessAndUniqueNormalizedNames()
    {
        var c = new KingbaseParameterCollection();
        var first = c.AddWithValue("@first", 1);
        var second = c.Add("second", KingbaseDbType.Text);
        DbParameterCollection bcl = c;
        Assert.Same(first, bcl[":FIRST"]);
        Assert.Same(second, c[1]);
        Assert.True(c.Contains("FIRST"));
        Assert.Equal(1, c.IndexOf(second));
        Assert.Equal(-1, c.IndexOf(new object()));
        Assert.Throws<ArgumentException>(() => c.AddWithValue(":First", 2));
        Assert.Throws<ArgumentException>(() => second.ParameterName = "FIRST");
        Assert.Equal("second", second.ParameterName);
        second.ParameterName = "renamed";
        Assert.False(c.Contains("second"));
        Assert.Same(second, c[":RENAMED"]);
        var copy = new DbParameter[2];
        c.CopyTo(copy, 0);
        Assert.Equal(new DbParameter[] { first, second }, copy);
        Assert.Equal(new[] { first, second }, c.Cast<KingbaseParameter>());
    }

    [Fact]
    public void ParametersHaveOneOwnerUntilRemovalReplacementOrClear()
    {
        var a = new KingbaseParameterCollection();
        var b = new KingbaseParameterCollection();
        var p = a.AddWithValue("p", 1);
        Assert.Throws<ArgumentException>(() => b.Add(p));
        Assert.Throws<ArgumentException>(() => a.Insert(0, p));
        a[0] = p;
        a.Remove(p);
        b.Add(p);
        b.Clear();
        a.Add(p);
        var replacement = new KingbaseParameter("q", 2);
        a[0] = replacement;
        b.Add(p);
        a.Clear();
        b.Add(replacement);
        Assert.Equal(2, b.Count);
        Assert.Throws<ArgumentException>(() => b.AddWithValue("q", 3));
    }

    [Fact]
    public void RangeAndReplacementFailuresAreAtomic()
    {
        var c = new KingbaseParameterCollection();
        var original = c.AddWithValue("original", 1);
        var fresh = new KingbaseParameter("fresh", 2);
        var duplicate = new KingbaseParameter("ORIGINAL", 3);
        Assert.Throws<ArgumentException>(() => c.AddRange(new[] { fresh, duplicate }));
        Assert.Single(c.Cast<KingbaseParameter>());
        var other = new KingbaseParameterCollection();
        other.Add(fresh);
        other.Clear();
        Assert.Throws<ArgumentException>(() => c.AddRange(new[] { fresh, fresh }));
        Assert.Throws<ArgumentException>(() => c.AddRange(new object[] { fresh, new object() }));
        Assert.Throws<ArgumentException>(() => c.AddRange(new KingbaseParameter[] { fresh, null! }));
        Assert.Throws<ArgumentNullException>(() => c.AddRange(null!));
        c.AddWithValue("second", 2);
        Assert.Throws<ArgumentException>(() => c[1] = duplicate);
        Assert.Same(original, c[0]);
        Assert.Equal("second", c[1].ParameterName);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Insert(-1, fresh));
        other.Add(fresh);
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void UnnamedNativeParametersAndInsertRemoveIndexContractsWork()
    {
        var c = new KingbaseParameterCollection();
        var first = c.AddWithValue(null, 1);
        var second = new KingbaseParameter(null, 2);
        c.Insert(0, second);
        Assert.Same(first, c[1]);
        c.RemoveAt(0);
        second.ParameterName = "named";
        c.Add(second);
        c.RemoveAt(":NAMED");
        Assert.Single(c.Cast<KingbaseParameter>());
        Assert.False(c.Contains(new object()));
        Assert.Throws<IndexOutOfRangeException>(() => c.RemoveAt("missing"));
        Assert.Throws<IndexOutOfRangeException>(() => c["missing"] = second);
        Assert.Throws<ArgumentException>(() => c.Add(new object()));
        Assert.Throws<ArgumentException>(() => c.Remove(second));
    }
}
