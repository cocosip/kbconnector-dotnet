using System.Diagnostics;

namespace KingbaseConnector.Protocol;

internal readonly struct PgMessage(byte code, byte[] payload)
{
    internal byte Code { get; } = code;
    internal byte[] Payload { get; } = payload;
}

/// <summary>Owns one stream and at most one resumable idle frame read.</summary>
internal sealed partial class PgWire : IDisposable
{
    // Materialized control/small-result path; streaming large rows/COPY need a separate bounded reader.
    internal const int MaxMessageLength = 64 * 1024 * 1024;
    internal const int MaxBufferedPayloadLength = 1024 * 1024;
    readonly Stream _stream;
    readonly int _maxMessageLength;
    readonly byte[] _header = new byte[5];
    readonly CancellationTokenSource _readLifetime = new();
    Task<PgMessage>? _pendingRead;
    int _disposed;
    int _busy;
    long _writeAttempts;
    internal long WriteAttempts => Interlocked.Read(ref _writeAttempts);
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal PgWire(Stream stream, int maxMessageLength = MaxMessageLength)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (maxMessageLength < 4 || maxMessageLength > MaxMessageLength)
            throw new ArgumentOutOfRangeException(nameof(maxMessageLength));
        _stream = stream;
        _maxMessageLength = maxMessageLength;
    }

    internal PgMessage ReadMessage(int maxPayloadLength = int.MaxValue) => ReadMessageAsync(maxPayloadLength: maxPayloadLength).GetAwaiter().GetResult();

    internal async Task<PgMessage> ReadMessageAsync(CancellationToken cancellationToken = default,
        int maxPayloadLength = int.MaxValue) =>
        await ReadOwnedAsync(-1, cancellationToken, maxPayloadLength, preserveCancellation: false).ConfigureAwait(false) ?? throw new IOException("Incomplete PG read.");

    internal Task<PgMessage?> ReadMessageWithTimeoutAsync(int timeoutMilliseconds, CancellationToken cancellationToken,
        int maxPayloadLength) => ReadOwnedAsync(timeoutMilliseconds, cancellationToken, maxPayloadLength, preserveCancellation: true);

    async Task<PgMessage?> ReadOwnedAsync(int timeoutMilliseconds, CancellationToken cancellationToken, int maxPayloadLength, bool preserveCancellation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxPayloadLength < 0) throw new ArgumentOutOfRangeException(nameof(maxPayloadLength));
        if (timeoutMilliseconds < -1) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        EnterOperation();
        try
        {
            if (_copyPayloadRemaining != 0) throw new InvalidOperationException("A streaming COPY payload has not been consumed.");
            var elapsed = Stopwatch.StartNew();
            if (_pendingRead is null)
            {
                _pendingRead = ReadFrameAsync(preserveCancellation ? Math.Min(MaxBufferedPayloadLength, _maxMessageLength - 4) : maxPayloadLength);
                _ = _pendingRead.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            var read = _pendingRead;
            if (!read.IsCompleted)
            {
                if (timeoutMilliseconds == 0) return null;
                var remaining = timeoutMilliseconds < 0 ? -1 : (int)Math.Max(0, timeoutMilliseconds - elapsed.ElapsedMilliseconds);
                if (remaining == 0) return null;
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task winner;
                try { winner = await Task.WhenAny(read, Task.Delay(remaining, stop.Token)).ConfigureAwait(false); }
                finally { stop.Cancel(); }
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(winner, read)) return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutMilliseconds > 0 && elapsed.ElapsedMilliseconds >= timeoutMilliseconds && read.Status == TaskStatus.RanToCompletion) return null;
            var message = await read.ConfigureAwait(false);
            if (message.Payload.Length > maxPayloadLength) throw new IOException("Resumed PG message exceeds the current read budget.");
            _pendingRead = null;
            return message;
        }
        catch (OperationCanceledException) when (preserveCancellation && cancellationToken.IsCancellationRequested && !IsDisposed) { throw; }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    async Task<PgMessage> ReadFrameAsync(int maxPayloadLength)
    {
        try
        {
            var token = _readLifetime.Token;
            await ReadExactlyAsync(_header, token).ConfigureAwait(false);
            var payload = AllocatePayload(maxPayloadLength);
            await ReadExactlyAsync(payload, token).ConfigureAwait(false);
            return new PgMessage(_header[0], payload);
        }
        catch (OperationCanceledException) when (IsDisposed) { throw new ObjectDisposedException(nameof(PgWire)); }
        catch { RetireAfterFailure(); throw; }
    }

    internal void WriteMessage(byte[] message)
    {
        ValidateOutgoing(message);
        EnterOperation();
        try { Interlocked.Increment(ref _writeAttempts); _stream.Write(message, 0, message.Length); }
        catch { RetireAfterFailure(); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal async Task WriteMessageAsync(byte[] message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOutgoing(message);
        EnterOperation();
        try { Interlocked.Increment(ref _writeAttempts); await _stream.WriteAsync(message, 0, message.Length, cancellationToken).ConfigureAwait(false); }
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

    byte[] AllocatePayload(int maxPayloadLength)
    {
        var length = PgBinary.ReadInt32(_header, 1);
        if (length < 4 || length > _maxMessageLength || length - 4 > maxPayloadLength)
            throw new IOException("Invalid or oversized PG message length.");
        return length == 4 ? Array.Empty<byte>() : new byte[length - 4];
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
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _readLifetime.Cancel(); }
            finally { _readLifetime.Dispose(); _stream.Dispose(); }
        }
    }
}
