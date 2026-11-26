using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>Writes bounded binary COPY rows with explicitly declared base column types. Dispose cancels unless Complete succeeds.</summary>
public sealed class KingbaseBinaryImporter : IDisposable
#if !NETSTANDARD2_0
    , IAsyncDisposable
#endif
{
    readonly PgBinaryCopyRows _rows;
    internal KingbaseBinaryImporter(PgBinaryCopyRows rows) => _rows = rows;
    public ulong? RowsCopied => _rows.RowsCopied;
    public void WriteRow(params object?[] values) => WriteRowAsync(values).GetAwaiter().GetResult();
    public Task WriteRowAsync(object?[] values, CancellationToken cancellationToken = default) => _rows.WriteRowAsync(values, cancellationToken);
    public ulong Complete() => CompleteAsync().GetAwaiter().GetResult();
    public Task<ulong> CompleteAsync(CancellationToken cancellationToken = default) => _rows.CompleteAsync(cancellationToken);
    public void Cancel() => CancelAsync().GetAwaiter().GetResult();
    public Task CancelAsync(CancellationToken cancellationToken = default) => _rows.CancelAsync(cancellationToken);
    public void Dispose() => _rows.DisposeAsync().GetAwaiter().GetResult();
#if !NETSTANDARD2_0
    public ValueTask DisposeAsync() => new(_rows.DisposeAsync());
#else
    public Task DisposeAsync() => _rows.DisposeAsync();
#endif
}
