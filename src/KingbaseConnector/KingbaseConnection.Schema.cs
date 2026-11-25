using System.Data;

namespace KingbaseConnector;

public sealed partial class KingbaseConnection
{
    public override DataTable GetSchema() => GetSchema(null);
    public override DataTable GetSchema(string? collectionName) => GetSchema(collectionName, null);
    public override DataTable GetSchema(string? collectionName, string?[]? restrictionValues) =>
        GetSchemaCoreAsync(collectionName, restrictionValues, CancellationToken.None).GetAwaiter().GetResult();

#if NET5_0_OR_GREATER
    public override Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default) =>
        GetSchemaCoreAsync(null, null, cancellationToken);
    public override Task<DataTable> GetSchemaAsync(string? collectionName, CancellationToken cancellationToken = default) =>
        GetSchemaCoreAsync(collectionName, null, cancellationToken);
    public override Task<DataTable> GetSchemaAsync(string? collectionName, string?[]? restrictionValues, CancellationToken cancellationToken = default) =>
        GetSchemaCoreAsync(collectionName, restrictionValues, cancellationToken);
#else
    public Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default) =>
        GetSchemaCoreAsync(null, null, cancellationToken);
    public Task<DataTable> GetSchemaAsync(string? collectionName, CancellationToken cancellationToken = default) =>
        GetSchemaCoreAsync(collectionName, null, cancellationToken);
    public Task<DataTable> GetSchemaAsync(string? collectionName, string?[]? restrictionValues, CancellationToken cancellationToken = default) =>
        GetSchemaCoreAsync(collectionName, restrictionValues, cancellationToken);
#endif

    async Task<DataTable> GetSchemaCoreAsync(string? collectionName, string?[]? restrictionValues, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = KingbaseSchema.Resolve(collectionName, restrictionValues);
        var definition = KingbaseObjectSchema.Find(name);
        KingbaseCommand command;
        lock (_sync)
        {
            var session = OpenSession();
            if (_command is not null || _reader is not null) throw ActiveOperation();
            if (session.Identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
                throw new NotSupportedException("Schema collections for this compatibility mode are not implemented.");
            if (name != "ReservedWords" && definition is null) return KingbaseSchema.Create(name, session.Identity.Version, session.Catalog);
            command = definition is null ? new KingbaseCommand(KingbaseSchema.KeywordsSql, this) { Transaction = _transaction } :
                KingbaseObjectSchema.Command(definition, restrictionValues, this, _transaction);
        }
        using (command)
        using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            return definition is null ? KingbaseSchema.ReadWords(reader, token) : KingbaseObjectSchema.Read(definition, reader, token);
    }
}
