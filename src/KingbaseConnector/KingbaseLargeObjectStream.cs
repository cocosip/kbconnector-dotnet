namespace KingbaseConnector;

/// <summary>A seekable descriptor bound to one physical session and explicit local transaction.</summary>
public sealed class KingbaseLargeObjectStream : Stream
{
    internal const int ChunkSize = 65536;
    readonly KingbaseConnection _connection;
    readonly KingbaseLargeObjectHandle _handle;
    int _busy;
    bool _disposed;
    internal KingbaseLargeObjectStream(KingbaseConnection connection, KingbaseLargeObjectHandle handle) { _connection = connection; _handle = handle; }
    public uint Oid => _handle.Oid;
    public override bool CanRead => !_disposed && (_handle.Access & FileAccess.Read) != 0 && _connection.IsLargeObjectValid(_handle);
    public override bool CanWrite => !_disposed && (_handle.Access & FileAccess.Write) != 0 && _connection.IsLargeObjectValid(_handle);
    public override bool CanSeek => !_disposed && _connection.IsLargeObjectValid(_handle);
    public override long Length => OperationAsync(LengthAsync, CancellationToken.None).GetAwaiter().GetResult();
    public override long Position { get => OperationAsync(TellAsync, CancellationToken.None).GetAwaiter().GetResult(); set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count); CheckAccess(FileAccess.Read);
        return OperationAsync(async operation =>
        {
            if (count == 0) return 0;
            var bytes = await operation.ScalarAsync<byte[]>("SELECT pg_catalog.loread(:p0,:p1)::pg_catalog.bytea", _handle.Descriptor, Math.Min(count, ChunkSize)).ConfigureAwait(false);
            if (bytes.Length > Math.Min(count, ChunkSize)) throw operation.InvalidResult();
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length); return bytes.Length;
        }, cancellationToken);
    }
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count); CheckAccess(FileAccess.Write);
        return OperationAsync(async operation =>
        {
            while (count > 0)
            {
                var size = Math.Min(count, ChunkSize); var bytes = new byte[size]; Buffer.BlockCopy(buffer, offset, bytes, 0, size);
                int written;
                try { written = await operation.ScalarAsync<int>("SELECT pg_catalog.lowrite(:p0,:p1)::pg_catalog.int4", _handle.Descriptor, bytes).ConfigureAwait(false); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
                if (written <= 0 || written > size) throw operation.InvalidResult();
                offset += written; count -= written;
            }
            return 0;
        }, cancellationToken);
    }
    public override long Seek(long offset, SeekOrigin origin) => SeekAsync(offset, origin).GetAwaiter().GetResult();
    public Task<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken cancellationToken = default)
    {
        if (origin is not (SeekOrigin.Begin or SeekOrigin.Current or SeekOrigin.End)) throw new ArgumentOutOfRangeException(nameof(origin));
        if (origin == SeekOrigin.Begin && offset < 0) throw new IOException("Cannot seek before the beginning of a large object.");
        return OperationAsync(async operation =>
        {
            var start = origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? await TellAsync(operation).ConfigureAwait(false) : await LengthAsync(operation).ConfigureAwait(false);
            long target; try { target = checked(start + offset); } catch (OverflowException) { throw new IOException("Large-object seek exceeds Int64."); }
            if (target < 0) throw new IOException("Cannot seek before the beginning of a large object.");
            var position = await operation.ScalarAsync<long>("SELECT pg_catalog.lo_lseek64(:p0,:p1,:p2)::pg_catalog.int8", _handle.Descriptor, target, 0).ConfigureAwait(false);
            if (position != target) throw operation.InvalidResult(); return position;
        }, cancellationToken);
    }
    public override void SetLength(long value) => SetLengthAsync(value).GetAwaiter().GetResult();
    public Task SetLengthAsync(long value, CancellationToken cancellationToken = default)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); CheckAccess(FileAccess.Write);
        return OperationAsync(async operation =>
        {
            if (await operation.ScalarAsync<int>("SELECT pg_catalog.lo_truncate64(:p0,:p1)::pg_catalog.int4", _handle.Descriptor, value).ConfigureAwait(false) != 0) throw operation.InvalidResult();
            return 0;
        }, cancellationToken);
    }
    async Task<long> TellAsync(KingbaseConnection.LargeObjectOperation operation)
    {
        var position = await operation.ScalarAsync<long>("SELECT pg_catalog.lo_tell64(:p0)::pg_catalog.int8", _handle.Descriptor).ConfigureAwait(false);
        if (position < 0) throw operation.InvalidResult(); return position;
    }
    async Task<long> LengthAsync(KingbaseConnection.LargeObjectOperation operation)
    {
        var position = await TellAsync(operation).ConfigureAwait(false);
        try
        {
            var length = await operation.ScalarAsync<long>("SELECT pg_catalog.lo_lseek64(:p0,:p1,:p2)::pg_catalog.int8", _handle.Descriptor, 0L, 2).ConfigureAwait(false);
            if (length < 0) throw operation.InvalidResult();
            var restored = await operation.ScalarAsync<long>("SELECT pg_catalog.lo_lseek64(:p0,:p1,:p2)::pg_catalog.int8", _handle.Descriptor, position, 0).ConfigureAwait(false);
            if (restored != position) throw operation.InvalidResult(); return length;
        }
        catch { _connection.ForgetLargeObject(_handle); throw; }
    }
    async Task<T> OperationAsync<T>(Func<KingbaseConnection.LargeObjectOperation, Task<T>> action, CancellationToken token, bool cleanup = false)
    {
        token.ThrowIfCancellationRequested(); Enter();
        try { return await _connection.LargeObjectOperationAsync(_handle, action, token, cleanup).ConfigureAwait(false); }
        finally { Volatile.Write(ref _busy, 0); }
    }
    void Enter()
    {
        if (_connection.IsInsideCallback) throw new InvalidOperationException("A large-object callback cannot reenter its stream.");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A large-object stream operation is already active.");
        if (_disposed) { Volatile.Write(ref _busy, 0); throw new ObjectDisposedException(nameof(KingbaseLargeObjectStream)); }
    }
    void CheckAccess(FileAccess access)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KingbaseLargeObjectStream));
        if ((_handle.Access & access) == 0) throw new NotSupportedException("The large-object stream does not permit this direction.");
    }
    static void ValidateBuffer(byte[] buffer, int offset, int count)
    { if (buffer is null) throw new ArgumentNullException(nameof(buffer)); if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count)); }
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken cancellationToken) => OperationAsync(_ => Task.FromResult(0), cancellationToken);
    async Task CloseAsync()
    {
        if (_disposed) return; Enter();
        try
        {
            if (!_connection.PrepareLargeObjectClose(_handle)) { _disposed = true; return; }
            await _connection.LargeObjectOperationAsync(_handle, async operation =>
            {
                if (await operation.ScalarAsync<int>("SELECT pg_catalog.lo_close(:p0)::pg_catalog.int4", _handle.Descriptor).ConfigureAwait(false) != 0) throw operation.InvalidResult();
                operation.ClosedExpected(); _disposed = true; return 0;
            }, CancellationToken.None, cleanup: true).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
    protected override void Dispose(bool disposing) { if (disposing) CloseAsync().GetAwaiter().GetResult(); base.Dispose(disposing); }
#if !NETSTANDARD2_0
    public override ValueTask DisposeAsync() => new(CloseAsync());
#else
    public Task DisposeAsync() => CloseAsync();
#endif
}
