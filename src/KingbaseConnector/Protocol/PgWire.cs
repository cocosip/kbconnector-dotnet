namespace KingbaseConnector.Protocol;

internal readonly struct PgMessage(byte code, byte[] payload)
{
    internal byte Code { get; } = code;
    internal byte[] Payload { get; } = payload;
}

/// <summary>Owns one stream; incomplete or malformed I/O permanently retires it.</summary>
internal sealed class PgWire : IDisposable
{
    // Materialized control/small-result path; streaming large rows/COPY need a separate bounded reader.
    internal const int MaxMessageLength = 64 * 1024 * 1024;
    readonly Stream _stream;
    readonly int _maxMessageLength;
    readonly byte[] _header = new byte[5];
    int _disposed;
    int _busy;

    internal PgWire(Stream stream, int maxMessageLength = MaxMessageLength)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (maxMessageLength < 4 || maxMessageLength > MaxMessageLength)
            throw new ArgumentOutOfRangeException(nameof(maxMessageLength));
        _stream = stream;
        _maxMessageLength = maxMessageLength;
    }

    internal PgMessage ReadMessage()
    {
        EnterOperation();
        try
        {
            ReadExactly(_header);
            var payload = AllocatePayload();
            ReadExactly(payload);
            return new PgMessage(_header[0], payload);
        }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal async Task<PgMessage> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterOperation();
        try
        {
            await ReadExactlyAsync(_header, cancellationToken).ConfigureAwait(false);
            var payload = AllocatePayload();
            await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return new PgMessage(_header[0], payload);
        }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal void WriteMessage(byte[] message)
    {
        ValidateOutgoing(message);
        EnterOperation();
        try { _stream.Write(message, 0, message.Length); }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal async Task WriteMessageAsync(byte[] message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOutgoing(message);
        EnterOperation();
        try { await _stream.WriteAsync(message, 0, message.Length, cancellationToken).ConfigureAwait(false); }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    void EnterOperation()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A PG stream operation is already active.");
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref _busy, 0);
            throw new ObjectDisposedException(nameof(PgWire));
        }
    }

    byte[] AllocatePayload()
    {
        var length = PgBinary.ReadInt32(_header, 1);
        if (length < 4 || length > _maxMessageLength)
            throw new IOException("Invalid or oversized PG message length.");
        return length == 4 ? Array.Empty<byte>() : new byte[length - 4];
    }

    void ReadExactly(byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = _stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) throw new EndOfStreamException("The PG stream ended before a complete message.");
            offset += read;
        }
    }

    async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await _stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The PG stream ended before a complete message.");
            offset += read;
        }
    }

    void ValidateOutgoing(byte[] message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (message.Length == 0 || message.Length > _maxMessageLength + 1)
            throw new ArgumentException("Invalid or oversized encoded PG message.", nameof(message));
    }

    void RetireAfterFailure()
    {
        // Preserve the original I/O failure if a stream also fails while closing.
        try { Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _stream.Dispose();
    }
}
