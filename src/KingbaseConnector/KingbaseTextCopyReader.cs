using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal sealed class KingbaseTextCopyReader : TextReader
{
    readonly KingbaseCopyStream _copy;
    readonly StreamReader _reader;
    internal KingbaseTextCopyReader(KingbaseCopyStream copy)
    { _copy = copy; _reader = new StreamReader(copy, PgBinary.Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true); }
    void Check() => _copy.RejectCallbackReentry();
    public override int Peek() { Check(); return _reader.Peek(); }
    public override int Read() { Check(); return _reader.Read(); }
    public override int Read(char[] buffer, int index, int count) { Check(); return _reader.Read(buffer, index, count); }
    public override int ReadBlock(char[] buffer, int index, int count) { Check(); return _reader.ReadBlock(buffer, index, count); }
    public override string ReadToEnd() { Check(); return _reader.ReadToEnd(); }
    public override string? ReadLine() { Check(); return _reader.ReadLine(); }
    public override Task<int> ReadAsync(char[] buffer, int index, int count) { Check(); return _reader.ReadAsync(buffer, index, count); }
    public override Task<int> ReadBlockAsync(char[] buffer, int index, int count) { Check(); return _reader.ReadBlockAsync(buffer, index, count); }
    public override Task<string> ReadToEndAsync() { Check(); return _reader.ReadToEndAsync(); }
    public override Task<string?> ReadLineAsync() { Check(); return _reader.ReadLineAsync(); }
#if !NETSTANDARD2_0
    public override int Read(Span<char> buffer) { Check(); return _reader.Read(buffer); }
    public override int ReadBlock(Span<char> buffer) { Check(); return _reader.ReadBlock(buffer); }
    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) { Check(); return _reader.ReadAsync(buffer, cancellationToken); }
    public override ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancellationToken = default) { Check(); return _reader.ReadBlockAsync(buffer, cancellationToken); }
#endif
#if NET8_0_OR_GREATER
    public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) { Check(); return _reader.ReadToEndAsync(cancellationToken); }
    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) { Check(); return _reader.ReadLineAsync(cancellationToken); }
#endif
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Check(); _copy.Dispose(); _reader.Dispose(); }
        base.Dispose(disposing);
    }
}
