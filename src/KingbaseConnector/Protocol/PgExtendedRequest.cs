namespace KingbaseConnector.Protocol;

// Owns outgoing frames and, for length-prefixed streaming inputs, one temporary Bind body.
internal sealed class PgExtendedRequest : IDisposable
{
    byte[][] _frames;
    PgTypeCatalog? _catalog;
    IReadOnlyList<PgQueryParameter>? _parameters;
    FileStream? _body;
    int _disposed;
    internal int ParseBytes => _frames[0].Length;
    internal string? TemporaryPath => _body?.Name;
    internal PgExtendedRequest(byte[][] frames) => _frames = frames;
    PgExtendedRequest(byte[][] frames, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters, FileStream body)
    { _frames = frames; _catalog = catalog; _parameters = parameters; _body = body; }

    internal static async Task<PgExtendedRequest> CreateAsync(string sql, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters,
        CancellationToken token, string? statement = null)
    {
        token.ThrowIfCancellationRequested();
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        if (parameters.Count > 1024) throw TooLarge();
        var snapshot = parameters.ToArray();
        if (snapshot.Any(p => p is null)) throw new ArgumentException("PG query parameters cannot contain null entries.", nameof(parameters));
        if (!snapshot.Any(p => p.Value is PgParameterInput))
            return new PgExtendedRequest(statement is null ? PgExtendedQuery.Encode(sql, catalog, snapshot) : PgExtendedQuery.EncodePrepared(statement, catalog, snapshot));
        // Validate every ordinary value before invoking any caller input.
        var shape = snapshot.Select(p => new PgQueryParameter(p.TypeOid, p.Value is PgParameterInput ? null : p.Value)).ToArray();
        var frames = statement is null ? PgExtendedQuery.Encode(sql, catalog, shape) : PgExtendedQuery.EncodePrepared(statement, catalog, shape);
        FileStream? body = null;
        try
        {
            var header = BindHeader(statement ?? "", snapshot.Length, 0);
            var limit = PgWire.MaxMessageLength - header.Length - 1;
            body = CreateTemporaryFile(); var small = new byte[4]; var codecs = new PgTextCodecs(catalog);
            foreach (var parameter in snapshot)
            {
                token.ThrowIfCancellationRequested();
                if (body.Position + 4 > limit) throw TooLarge();
                var offset = body.Position; await body.WriteAsync(small, 0, 4, token).ConfigureAwait(false);
                if (parameter.Value is PgParameterInput input) await input.WriteAsync(body, limit, token).ConfigureAwait(false);
                else if (codecs.Encode(parameter.TypeOid, parameter.Value) is { } text)
                {
                    var bytes = PgBinary.Utf8.GetBytes(text);
                    try
                    {
                        if (body.Position + bytes.Length > limit) throw TooLarge();
                        await body.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                }
                var end = body.Position;
                PgBinary.WriteInt32(small, 0, parameter.Value is null or DBNull ? -1 : checked((int)(end - offset - 4)));
                body.Position = offset; await body.WriteAsync(small, 0, 4, token).ConfigureAwait(false); body.Position = end;
            }
            var index = statement is null ? 1 : 0;
            Array.Clear(frames[index], 0, frames[index].Length); frames[index] = BindHeader(statement ?? "", snapshot.Length, body.Length);
            var request = new PgExtendedRequest(frames, catalog, snapshot, body); body = null; return request;
        }
        catch { Clear(frames); throw; }
        finally { body?.Dispose(); }
    }

    internal void UsePrepared(string name, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters)
    {
        ThrowIfDisposed();
        byte[][] replacement;
        if (_body is null) replacement = PgExtendedQuery.EncodePrepared(name, catalog, parameters);
        else
        {
            var skeleton = PgExtendedQuery.EncodePrepared(name, _catalog!, _parameters!.Select(p => new PgQueryParameter(p.TypeOid, null)).ToArray());
            try { skeleton[0] = BindHeader(name, _parameters!.Count, _body.Length); replacement = skeleton; }
            catch { Clear(skeleton); throw; }
        }
        Clear(_frames); _frames = replacement;
    }

    internal async Task SendAsync(PgWire wire, Action<long> executionWrites, CancellationToken token)
    {
        ThrowIfDisposed();
        try
        {
            foreach (var frame in _frames)
            {
                var baseline = wire.WriteAttempts;
                try
                {
                    if (frame.Length != 0 && frame[0] == (byte)'B' && _body is not null) await wire.WriteInputAsync(frame, _body, token).ConfigureAwait(false);
                    else await wire.WriteMessageAsync(frame, token).ConfigureAwait(false);
                }
                finally { if (frame.Length != 0 && frame[0] is (byte)'E' or (byte)'Q') executionWrites(wire.WriteAttempts - baseline); }
            }
        }
        finally { Dispose(); }
    }

    static byte[] BindHeader(string name, int count, long bodyLength)
    {
        var header = new byte[10 + PgFrontendMessages.CStringLength(name)];
        var length = header.Length - 1L + bodyLength + 2;
        if (length > PgWire.MaxMessageLength) throw TooLarge();
        header[0] = (byte)'B'; PgBinary.WriteInt32(header, 1, (int)length);
        PgBinary.Utf8.GetBytes(name, 0, name.Length, header, 6);
        header[header.Length - 2] = (byte)(count >> 8); header[header.Length - 1] = (byte)count;
        return header;
    }
    static FileStream CreateTemporaryFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "kbconnector-bind-" + Guid.NewGuid().ToString("N") + ".tmp");
#if NET6_0_OR_GREATER
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose, BufferSize = 4096 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
#else
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
#endif
    }
    static ArgumentException TooLarge() => new("Streaming parameters exceed the PG Bind frame limit.");
    void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PgExtendedRequest)); }
    static void Clear(byte[][] frames) { foreach (var frame in frames) Array.Clear(frame, 0, frame.Length); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Clear(_frames); _frames = Array.Empty<byte[]>(); _catalog = null; _parameters = null;
        Interlocked.Exchange(ref _body, null)?.Dispose();
    }
}
