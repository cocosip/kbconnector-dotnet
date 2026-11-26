namespace KingbaseConnector.Protocol;

internal readonly struct PgCopyMessage(byte code, int count, byte[] payload)
{
    internal byte Code { get; } = code;
    internal int Count { get; } = count;
    internal byte[] Payload { get; } = payload;
}

internal sealed partial class PgWire
{
    int _copyPayloadRemaining;
    int _copyWriting;

    internal async Task WriteCopyMessageAsync(byte[] message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateOutgoing(message);
        if (Interlocked.CompareExchange(ref _copyWriting, 1, 0) != 0) throw new InvalidOperationException("A COPY write is already active.");
        try
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(PgWire));
            Interlocked.Increment(ref _writeAttempts);
            await _stream.WriteAsync(message, 0, message.Length, token).ConfigureAwait(false);
        }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _copyWriting, 0); }
    }

    internal async Task<PgCopyMessage> ReadCopyMessageAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count <= 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        EnterOperation();
        var admitted = false;
        try
        {
            if (_rowPayloadRemaining != 0) throw new InvalidOperationException("A streaming data row has not been consumed.");
            if (_pendingRead is not null) throw new InvalidOperationException("A buffered PG frame read is still pending.");
            admitted = true;
            if (_copyPayloadRemaining == 0)
            {
                await ReadExactlyAsync(_header, token).ConfigureAwait(false);
                var length = PgBinary.ReadInt32(_header, 1);
                if (length < 4 || length > _maxMessageLength) throw new IOException("Invalid or oversized COPY message length.");
                if (_header[0] != (byte)'d')
                {
                    var payload = AllocatePayload(MaxBufferedPayloadLength);
                    await ReadExactlyAsync(payload, token).ConfigureAwait(false);
                    return new PgCopyMessage(_header[0], 0, payload);
                }
                _copyPayloadRemaining = length - 4;
                if (_copyPayloadRemaining == 0) return new PgCopyMessage((byte)'d', 0, Array.Empty<byte>());
            }
            var received = await _stream.ReadAsync(buffer, offset, Math.Min(count, _copyPayloadRemaining), token).ConfigureAwait(false);
            if (received == 0) throw new EndOfStreamException("The COPY stream ended inside a data message.");
            _copyPayloadRemaining -= received;
            return new PgCopyMessage((byte)'d', received, Array.Empty<byte>());
        }
        catch when (!admitted) { throw; }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
