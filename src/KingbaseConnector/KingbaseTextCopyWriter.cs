using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

/// <summary>Writes UTF8 COPY text. Dispose completes; Cancel discards pending text and cancels import.</summary>
public sealed class KingbaseTextCopyWriter : TextWriter
{
    readonly KingbaseCopyStream _copy;
    readonly StreamWriter _writer;
    bool _closed;
    int _terminalBusy;
    internal KingbaseTextCopyWriter(KingbaseCopyStream copy) { _copy = copy; _writer = new StreamWriter(copy, PgBinary.Utf8, 4096, leaveOpen: true); }
    public override Encoding Encoding => PgBinary.Utf8;
    public ulong? RowsCopied => _copy.RowsCopied;
    void Check() { _copy.RejectCallbackReentry(); if (Volatile.Read(ref _terminalBusy) != 0) throw new InvalidOperationException("A text COPY terminal operation is already active."); if (_closed) throw new ObjectDisposedException(nameof(KingbaseTextCopyWriter)); }
    public override void Write(char value) { Check(); _writer.Write(value); }
    public override void Write(string? value) { Check(); _writer.Write(value); }
    public override void Write(char[] buffer, int index, int count) { Check(); _writer.Write(buffer, index, count); }
    public override Task WriteAsync(char value) { Check(); return _writer.WriteAsync(value); }
    public override Task WriteAsync(string? value) { Check(); return _writer.WriteAsync(value); }
    public override Task WriteAsync(char[] buffer, int index, int count) { Check(); return _writer.WriteAsync(buffer, index, count); }
    public override void Flush() { Check(); _writer.Flush(); }
    public override Task FlushAsync() { Check(); return _writer.FlushAsync(); }
#if !NETSTANDARD2_0
    public override void Write(ReadOnlySpan<char> buffer) { Check(); _writer.Write(buffer); }
    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    { Check(); return _writer.WriteAsync(buffer, cancellationToken); }
    public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Check(); await _writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
#endif
    public ulong Complete() => CompleteAsync().GetAwaiter().GetResult();
    public Task<ulong> CompleteAsync(CancellationToken cancellationToken = default) => CompleteCoreAsync(cancellationToken, disposing: false);
    async Task<ulong> CompleteCoreAsync(CancellationToken cancellationToken, bool disposing)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _copy.RejectCallbackReentry();
        if (Interlocked.CompareExchange(ref _terminalBusy, 1, 0) != 0) throw new InvalidOperationException("A text COPY terminal operation is already active.");
        var close = false;
        try
        {
            if (_closed) return RowsCopied ?? (disposing ? 0UL : throw new ObjectDisposedException(nameof(KingbaseTextCopyWriter)));
            _closed = true; close = true;
            using (cancellationToken.Register(_copy.Abort)) await _writer.FlushAsync().ConfigureAwait(false);
            return await _copy.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!_copy.HasFinished) { _closed = false; close = false; throw; }
        catch { try { await _copy.CancelAsync().ConfigureAwait(false); } catch { } cancellationToken.ThrowIfCancellationRequested(); throw; }
        finally { try { if (close) CloseWriter(); } finally { Volatile.Write(ref _terminalBusy, 0); } }
    }
    public void Cancel() => CancelAsync().GetAwaiter().GetResult();
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _copy.RejectCallbackReentry();
        if (Interlocked.CompareExchange(ref _terminalBusy, 1, 0) != 0) throw new InvalidOperationException("A text COPY terminal operation is already active.");
        var close = false;
        try { if (_closed) return; _closed = true; close = true; await _copy.CancelAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) when (!_copy.HasFinished) { _closed = false; close = false; throw; }
        finally { try { if (close) CloseWriter(); } finally { Volatile.Write(ref _terminalBusy, 0); } }
    }
    void CloseWriter() { try { _writer.Dispose(); } catch { } _copy.Dispose(); }
    protected override void Dispose(bool disposing) { if (disposing) CompleteCoreAsync(CancellationToken.None, disposing: true).GetAwaiter().GetResult(); base.Dispose(disposing); }
#if !NETSTANDARD2_0
    public override async ValueTask DisposeAsync() { await CompleteCoreAsync(CancellationToken.None, disposing: true).ConfigureAwait(false); GC.SuppressFinalize(this); }
#else
    public Task DisposeAsync() => CompleteCoreAsync(CancellationToken.None, disposing: true);
#endif
}
