using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>Reads bounded binary COPY rows. ReadRow returns null only after trailer and server completion are acknowledged.</summary>
public sealed class KingbaseBinaryExporter : IDisposable
#if !NETSTANDARD2_0
    , IAsyncDisposable
#endif
{
    readonly PgBinaryCopyRows _rows;
    internal KingbaseBinaryExporter(PgBinaryCopyRows rows) => _rows = rows;
    public ulong? RowsCopied => _rows.RowsCopied;
    public object[]? ReadRow() => ReadRowAsync().GetAwaiter().GetResult();
    public Task<object[]?> ReadRowAsync(CancellationToken cancellationToken = default) => _rows.ReadRowAsync(cancellationToken);
    public void Dispose() => _rows.DisposeAsync().GetAwaiter().GetResult();
#if !NETSTANDARD2_0
    public ValueTask DisposeAsync() => new(_rows.DisposeAsync());
#else
    public Task DisposeAsync() => _rows.DisposeAsync();
#endif
}
