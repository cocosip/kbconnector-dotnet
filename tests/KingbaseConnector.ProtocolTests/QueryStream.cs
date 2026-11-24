namespace KingbaseConnector.ProtocolTests;

internal sealed class QueryStream(byte[] input) : Stream
{
    int _position;
    readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int InputLength => input.Length;
    public int BytesRead => _position;
    public int DisposeCount { get; private set; }
    public int FragmentSize { get; init; } = 1;
    public bool BlockAtEnd { get; init; }
    public bool IgnoreReadCancellation { get; init; }
    public bool FailWrite { get; init; }
    public int FailWriteNumber { get; init; }
    public List<byte[]> Writes { get; } = new();
    public List<byte[]> BorrowedWriteBuffers { get; } = new();
    public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => input.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = Math.Min(count, Math.Min(FragmentSize, input.Length - _position));
        Array.Copy(input, _position, buffer, offset, read);
        _position += read;
        return read;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_position == input.Length && BlockAtEnd)
        {
            ReadBlocked.TrySetResult();
            await _closed.Task.WaitAsync(IgnoreReadCancellation ? CancellationToken.None : cancellationToken);
            throw new ObjectDisposedException(nameof(QueryStream));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Read(buffer, offset, count);
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        BorrowedWriteBuffers.Add(buffer);
        Writes.Add(buffer.Skip(offset).Take(count).ToArray());
        if (FailWrite || Writes.Count == FailWriteNumber) throw new IOException("Test write failure.");
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { DisposeCount++; _closed.TrySetResult(); }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
