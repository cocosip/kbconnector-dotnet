namespace KingbaseConnector.Protocol;

internal sealed partial class PgWire
{
    int _rowPayloadRemaining;
    internal int RowPayloadRemaining => Volatile.Read(ref _rowPayloadRemaining);

    internal async Task<PgMessage> ReadRowMessageAsync(CancellationToken token, int maxControlPayload = MaxBufferedPayloadLength)
    {
        token.ThrowIfCancellationRequested();
        if (maxControlPayload < 0) throw new ArgumentOutOfRangeException(nameof(maxControlPayload));
        EnterOperation();
        var admitted = false;
        try
        {
            if (_rowPayloadRemaining != 0 || _copyPayloadRemaining != 0)
                throw new InvalidOperationException("A streaming PG payload has not been consumed.");
            if (_pendingRead is not null) throw new InvalidOperationException("A buffered PG frame read is still pending.");
            admitted = true;
            using var abort = token.Register(RetireAfterFailure);
            await ReadExactlyAsync(_header, token).ConfigureAwait(false);
            var length = PgBinary.ReadInt32(_header, 1);
            if (length < 4 || length > _maxMessageLength) throw new IOException("Invalid or oversized PG row message length.");
            if (_header[0] == (byte)'D')
            {
                _rowPayloadRemaining = length - 4;
                return new PgMessage((byte)'D', Array.Empty<byte>());
            }
            var payload = AllocatePayload(Math.Min(MaxBufferedPayloadLength, maxControlPayload));
            await ReadExactlyAsync(payload, token).ConfigureAwait(false);
            return new PgMessage(_header[0], payload);
        }
        catch when (!admitted) { throw; }
        catch when (token.IsCancellationRequested)
        { RetireAfterFailure(); throw new OperationCanceledException(token); }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal async Task<int> ReadRowPayloadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        EnterOperation();
        var admitted = false;
        try
        {
            if (_copyPayloadRemaining != 0) throw new InvalidOperationException("A streaming COPY payload has not been consumed.");
            if (_pendingRead is not null) throw new InvalidOperationException("A buffered PG frame read is still pending.");
            admitted = true;
            if (count == 0 || _rowPayloadRemaining == 0) return 0;
            using var abort = token.Register(RetireAfterFailure);
            var received = await _stream.ReadAsync(buffer, offset, Math.Min(count, _rowPayloadRemaining), token).ConfigureAwait(false);
            if (received == 0) throw new EndOfStreamException("The PG stream ended inside a data row.");
            _rowPayloadRemaining -= received;
            return received;
        }
        catch when (!admitted) { throw; }
        catch when (token.IsCancellationRequested)
        { RetireAfterFailure(); throw new OperationCanceledException(token); }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
