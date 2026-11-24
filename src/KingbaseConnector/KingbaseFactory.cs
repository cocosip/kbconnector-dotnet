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
}
