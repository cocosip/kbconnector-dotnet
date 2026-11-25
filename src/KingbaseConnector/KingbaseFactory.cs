using System.Data.Common;
namespace KingbaseConnector;
public sealed class KingbaseFactory : DbProviderFactory
{
    public static readonly KingbaseFactory Instance = new();
    KingbaseFactory() { }
    public override DbConnection CreateConnection() => new KingbaseConnection();
    public override DbCommand CreateCommand() => new KingbaseCommand();
    public override DbParameter CreateParameter() => new KingbaseParameter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new KingbaseConnectionStringBuilder();
#if NETSTANDARD2_0
    public bool CanCreateDataAdapter => true;
#else
    public override bool CanCreateDataAdapter => true;
#endif
    public override DbDataAdapter CreateDataAdapter() => new KingbaseDataAdapter();
#if NET6_0_OR_GREATER
    public override bool CanCreateBatch => true;
    public override KingbaseBatch CreateBatch() => new();
    public override KingbaseBatchCommand CreateBatchCommand() => new();
#else
    public bool CanCreateBatch => true;
    public KingbaseBatch CreateBatch() => new();
    public KingbaseBatchCommand CreateBatchCommand() => new();
#endif
}
