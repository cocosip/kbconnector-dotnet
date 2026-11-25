using System.Data.Common;

namespace KingbaseConnector.UnitTests;

public sealed class DataAdapterBatchingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(257)]
    [InlineData(int.MaxValue)]
    public void BatchingSizeHonorsTheBclNonnegativeContract(int size)
    {
        using DbDataAdapter adapter = new KingbaseDataAdapter(); adapter.UpdateBatchSize = size; Assert.Equal(size, adapter.UpdateBatchSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.UpdateBatchSize = -1); Assert.Equal(size, adapter.UpdateBatchSize);
    }
}
