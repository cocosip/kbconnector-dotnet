using System.Data.Common;
using System.Data;

namespace KingbaseConnector.UnitTests;

public sealed class DataAdapterTests
{
    [Fact]
    public void FactoryCreatesTheStandardDataAdapterSurface()
    {
        DbProviderFactory factory = KingbaseFactory.Instance;
        Assert.True(factory.CanCreateDataAdapter);
        using var adapter = Assert.IsAssignableFrom<DbDataAdapter>(factory.CreateDataAdapter());
        Assert.Equal("KingbaseDataAdapter", adapter.GetType().Name);
        Assert.Null(adapter.SelectCommand);
        Assert.Equal(1, adapter.UpdateBatchSize);
    }

    [Fact]
    public void TypedCommandsShareBclStorageAndConstructorsPreserveCallerObjects()
    {
        using var connection = new KingbaseConnection(); using var select = new KingbaseCommand("SELECT 1", connection);
        using var adapter = new KingbaseDataAdapter(select); DbDataAdapter bcl = adapter;
        Assert.Same(select, adapter.SelectCommand); Assert.Same(select, bcl.SelectCommand);
        using var insert = new KingbaseCommand(); using var update = new KingbaseCommand(); using var delete = new KingbaseCommand();
        adapter.InsertCommand = insert; bcl.UpdateCommand = update; adapter.DeleteCommand = delete;
        Assert.Same(insert, bcl.InsertCommand); Assert.Same(update, adapter.UpdateCommand); Assert.Same(delete, bcl.DeleteCommand);
        adapter.SelectCommand = null; Assert.Null(bcl.SelectCommand);
        using var byConnection = new KingbaseDataAdapter("SELECT 2", connection);
        Assert.Same(connection, byConnection.SelectCommand!.Connection); Assert.Equal("SELECT 2", byConnection.SelectCommand.CommandText);
        using var byString = new KingbaseDataAdapter("SELECT 3", "Host=localhost;Database=app");
        using var ownedCommand = byString.SelectCommand!; using var ownedConnection = ownedCommand.Connection!;
        Assert.Equal("SELECT 3", ownedCommand.CommandText); Assert.Equal("app", ownedConnection.Database);
        adapter.UpdateBatchSize = 2; Assert.Equal(2, bcl.UpdateBatchSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignCommandsAreRejectedBeforeReplacingAnyExistingCommand(bool useInterface)
    {
        using var adapter = new KingbaseDataAdapter(); using var original = new KingbaseCommand(); using var foreign = new ForeignCommand();
        DbDataAdapter bcl = adapter; IDbDataAdapter idb = adapter;
        adapter.SelectCommand = adapter.InsertCommand = adapter.UpdateCommand = adapter.DeleteCommand = original;
        Action[] assignments = useInterface
            ? [() => idb.SelectCommand = foreign, () => idb.InsertCommand = foreign, () => idb.UpdateCommand = foreign, () => idb.DeleteCommand = foreign]
            : [() => bcl.SelectCommand = foreign, () => bcl.InsertCommand = foreign, () => bcl.UpdateCommand = foreign, () => bcl.DeleteCommand = foreign];
        foreach (var assignment in assignments) Assert.Throws<ArgumentException>(assignment);
        Assert.Same(original, adapter.SelectCommand); Assert.Same(original, adapter.InsertCommand); Assert.Same(original, adapter.UpdateCommand); Assert.Same(original, adapter.DeleteCommand);
    }

    sealed class ForeignCommand : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
