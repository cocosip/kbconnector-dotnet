namespace KingbaseConnector.Protocol;

internal sealed partial class PgWire
{
    internal async Task WriteInputAsync(byte[] header, FileStream body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (header.Length < 11 || header[0] != (byte)'B' || PgBinary.ReadInt32(header, 1) != header.Length - 1L + body.Length + 2 ||
            PgBinary.ReadInt32(header, 1) > _maxMessageLength) throw new ArgumentException("Invalid streaming PG Bind frame.");
        var buffer = new byte[32 * 1024]; EnterOperation();
        try
        {
            body.Position = 0; Interlocked.Increment(ref _writeAttempts);
            await _stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            var remaining = body.Length;
            while (remaining != 0)
            {
                token.ThrowIfCancellationRequested();
                var count = await body.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Streaming PG Bind body ended early.");
                await _stream.WriteAsync(buffer, 0, count, token).ConfigureAwait(false); remaining -= count;
            }
            Array.Clear(buffer, 0, 2); await _stream.WriteAsync(buffer, 0, 2, token).ConfigureAwait(false);
        }
        catch { RetireAfterFailure(); throw; }
        finally { Array.Clear(buffer, 0, buffer.Length); Volatile.Write(ref _busy, 0); }
    }
}
