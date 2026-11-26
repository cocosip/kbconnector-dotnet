namespace KingbaseConnector.Protocol;

internal sealed class PgBinaryCopyRows
{
    static readonly byte[] Header = [75,66,67,79,80,89,10,255,13,10,0,0,0,0,0,0,0,0,0];
    readonly KingbaseCopyStream _copy;
    readonly string[] _codecs;
    int _busy;
    bool _disposed, _trailer, _ended;
    ulong _rows;
    internal ulong? RowsCopied => _copy.RowsCopied;
    internal PgBinaryCopyRows(KingbaseCopyStream copy, string[] codecs)
    {
        _copy = copy; _codecs = codecs;
        copy.CompletionValidator = count =>
        {
            // COPY WHERE and row-suppressing triggers can acknowledge fewer imported rows.
            if (copy.IsImport ? count > _rows : count != _rows || !_trailer) throw Invalid();
        };
    }
    internal async Task StartAsync(CancellationToken token)
    {
        try
        {
            if (_copy.ColumnCount != _codecs.Length) throw new ArgumentException("Binary COPY column types must match the server column count.");
            if (_copy.IsImport) { await _copy.WriteAsync(Header, 0, Header.Length, token).ConfigureAwait(false); return; }
            var header = new byte[19]; await ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (!header.Take(11).SequenceEqual(Header.Take(11)) || (PgBinary.ReadInt32(header, 11) & unchecked((int)0xffff0000)) != 0) throw Invalid();
            var extra = PgBinary.ReadInt32(header, 15);
            if (extra < 0 || extra > 65536) throw Invalid();
            var discard = new byte[Math.Min(extra, 8192)];
            while (extra > 0) { var size = Math.Min(extra, discard.Length); await ReadExactlyAsync(discard, token, size).ConfigureAwait(false); extra -= size; }
        }
        catch { await CleanupAsync().ConfigureAwait(false); throw; }
    }
    internal async Task WriteRowAsync(object?[] values, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Enter();
        var payloads = new byte[]?[_codecs.Length];
        var dispatch = false;
        try
        {
            if (values is null) throw new ArgumentNullException(nameof(values));
            if (values.Length != _codecs.Length) throw new ArgumentException("Binary COPY row has the wrong column count.", nameof(values));
            if (_rows == ulong.MaxValue) throw Invalid();
            var budget = PgBinaryCopyCodec.MaxRowBytes;
            for (var i = 0; i < values.Length; i++) { payloads[i] = PgBinaryCopyCodec.Encode(_codecs[i], values[i], budget); budget -= payloads[i]?.Length ?? 0; }
            // No outgoing row bytes precede complete validation and owned value snapshots.
            token.ThrowIfCancellationRequested(); dispatch = true;
            await _copy.WriteAsync(new byte[] { (byte)(_codecs.Length >> 8), (byte)_codecs.Length }, 0, 2, token).ConfigureAwait(false);
            var length = new byte[4];
            foreach (var bytes in payloads)
            {
                PgBinary.WriteInt32(length, 0, bytes?.Length ?? -1); await _copy.WriteAsync(length, 0, 4, token).ConfigureAwait(false);
                if (bytes is not null && bytes.Length != 0) await _copy.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            }
            _rows++;
        }
        catch { if (dispatch) { _copy.Abort(); await CleanupAsync().ConfigureAwait(false); } throw; }
        finally { foreach (var bytes in payloads) if (bytes is not null) Array.Clear(bytes, 0, bytes.Length); Volatile.Write(ref _busy, 0); }
    }
    internal async Task<object[]?> ReadRowAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Enter();
        object[]? values = null; var published = false;
        try
        {
            if (_ended) return null;
            var count = new byte[2]; await ReadExactlyAsync(count, token).ConfigureAwait(false);
            if (count[0] == 255 && count[1] == 255)
            {
                _trailer = true;
                if (await _copy.ReadAsync(new byte[1], 0, 1, token).ConfigureAwait(false) != 0) throw Invalid();
                _ended = true; return null;
            }
            if (((count[0] << 8) | count[1]) != _codecs.Length || _rows == ulong.MaxValue) throw Invalid();
            values = new object[_codecs.Length]; var length = new byte[4]; var budget = PgBinaryCopyCodec.MaxRowBytes;
            for (var i = 0; i < values.Length; i++)
            {
                await ReadExactlyAsync(length, token).ConfigureAwait(false); var size = PgBinary.ReadInt32(length, 0);
                if (size == -1) { values[i] = DBNull.Value; continue; }
                if (size < 0 || size > budget) throw Invalid(); budget -= size;
                var bytes = new byte[size];
                try { await ReadExactlyAsync(bytes, token).ConfigureAwait(false); values[i] = PgBinaryCopyCodec.Decode(_codecs[i], bytes); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
            _rows++; published = true; return values;
        }
        catch { if (RowsCopied.HasValue) _ended = true; await CleanupAsync().ConfigureAwait(false); throw; }
        finally
        {
            if (!published && values is not null) foreach (var value in values) if (value is byte[] bytes) Array.Clear(bytes, 0, bytes.Length);
            Volatile.Write(ref _busy, 0);
        }
    }
    internal async Task<ulong> CompleteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _copy.RejectCallbackReentry();
        if (RowsCopied is { } count) return count;
        Enter();
        try
        {
            await _copy.WriteAsync(new byte[] { 255,255 }, 0, 2, token).ConfigureAwait(false);
            return await _copy.CompleteAsync(token).ConfigureAwait(false);
        }
        catch { await CleanupAsync().ConfigureAwait(false); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }
    internal async Task CancelAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Enter(lifetime: false);
        try { await _copy.CancelAsync(token).ConfigureAwait(false); }
        finally { Volatile.Write(ref _busy, 0); }
    }
    internal async Task DisposeAsync()
    {
        _copy.RejectCallbackReentry(); if (_disposed) return; Enter(lifetime: false);
        try { await _copy.DisposeAsync().ConfigureAwait(false); }
        finally { if (_copy.HasFinished) _disposed = true; Volatile.Write(ref _busy, 0); }
    }
    void Enter(bool lifetime = true)
    {
        _copy.RejectCallbackReentry();
        if (_disposed) throw new ObjectDisposedException("BinaryCopy");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A binary COPY operation is already active.");
        try { if (lifetime) _copy.CheckBufferedAccess(); }
        catch { Volatile.Write(ref _busy, 0); throw; }
    }
    async Task ReadExactlyAsync(byte[] bytes, CancellationToken token, int? count = null)
    {
        var offset = 0; var end = count ?? bytes.Length;
        while (offset < end) { var read = await _copy.ReadAsync(bytes, offset, end - offset, token).ConfigureAwait(false); if (read == 0) throw Invalid(); offset += read; }
    }
    async Task CleanupAsync() { try { await _copy.DisposeAsync().ConfigureAwait(false); } catch { } }
    static IOException Invalid() => new("Invalid binary COPY header, row or completion.");
}
