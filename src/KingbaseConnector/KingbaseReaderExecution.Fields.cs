using System.Text;
#if !NETSTANDARD2_0
using System.Runtime.InteropServices;
#endif
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal sealed partial class KingbaseReaderExecution
{
    FieldCursor? _field;
    long _childVersion;

    async Task<FieldCursor> FieldAsync(int ordinal, bool binary, PgTextCodecs codecs, CancellationToken token)
    {
        ValidateField(ordinal, binary, codecs);
        var length = await SelectAsync(ordinal, token).ConfigureAwait(false);
        if (length == -1) throw new InvalidCastException("A NULL column has no field reader.");
        if (_field is null)
        {
            var value = _materialized ? codecs.DecodeStreaming(Header!.Columns[ordinal].TypeOid, _value, Header.DateStyle, Header.IntervalStyle, false) : null;
            _field = new FieldCursor(_rows!, binary, value);
        }
        return _field;
    }

    internal Task<Stream> StreamAsync(int ordinal, PgTextCodecs codecs) => RunAsync<Stream>(async token =>
    { await FieldAsync(ordinal, true, codecs, token).ConfigureAwait(false); return new FieldStream(this, ++_childVersion); }, CancellationToken.None);
    internal Task<TextReader> TextReaderAsync(int ordinal, PgTextCodecs codecs) => RunAsync<TextReader>(async token =>
    { await FieldAsync(ordinal, false, codecs, token).ConfigureAwait(false); return new FieldTextReader(this, ++_childVersion); }, CancellationToken.None);

    internal Task<long> CopyBytesAsync(int ordinal, long position, byte[]? buffer, int offset, int count, PgTextCodecs codecs) => RunAsync(async token =>
    {
        ValidateField(ordinal, true, codecs);
        if (buffer is not null && ordinal == _ordinal && _field is not null && position < _field.Position) throw Backwards();
        _childVersion++;
        if (buffer is null && (_ordinal != ordinal || _field?.Started != true)) await MaterializeAsync(ordinal, token).ConfigureAwait(false);
        var field = await FieldAsync(ordinal, true, codecs, token).ConfigureAwait(false);
        if (buffer is null && !field.Started && field.MemoryLength is { } known) return known;
        if (buffer is null) { var scratch = field.ByteScratch; while (await field.ReadBytesAsync(scratch, 0, scratch.Length, token).ConfigureAwait(false) != 0) { } return field.Position; }
        if (position < field.Position) throw Backwards();
        while (position > field.Position)
        {
            var skip = field.ByteScratch;
            if (await field.ReadBytesAsync(skip, 0, (int)Math.Min(skip.Length, position - field.Position), token).ConfigureAwait(false) == 0) return 0L;
        }
        return (long)await field.ReadBytesAsync(buffer, offset, count, token).ConfigureAwait(false);
    }, CancellationToken.None);
    void ValidateField(int ordinal, bool binary, PgTextCodecs codecs)
    {
        if (codecs.GetFieldType(Header!.Columns[ordinal].TypeOid) != (binary ? typeof(byte[]) : typeof(string)))
            throw new InvalidCastException("This column does not support the requested field reader.");
        if (ordinal < _ordinal) throw Backwards();
    }
    internal Task<long> CopyCharsAsync(int ordinal, long position, char[]? buffer, int offset, int count, PgTextCodecs codecs) => RunAsync(async token =>
    {
        ValidateField(ordinal, false, codecs);
        if (buffer is not null && ordinal == _ordinal && _field is not null && position < _field.Position) throw Backwards();
        _childVersion++;
        if (buffer is null && (_ordinal != ordinal || _field?.Started != true)) await MaterializeAsync(ordinal, token).ConfigureAwait(false);
        var field = await FieldAsync(ordinal, false, codecs, token).ConfigureAwait(false);
        if (buffer is null && !field.Started && field.MemoryLength is { } known) return known;
        if (buffer is null) { var scratch = field.CharScratch; while (await field.ReadCharsAsync(scratch, 0, scratch.Length, token).ConfigureAwait(false) != 0) { } return field.Position; }
        if (position < field.Position) throw Backwards();
        while (position > field.Position)
        {
            var skip = field.CharScratch;
            if (await field.ReadCharsAsync(skip, 0, (int)Math.Min(skip.Length, position - field.Position), token).ConfigureAwait(false) == 0) return 0L;
        }
        return (long)await field.ReadCharsAsync(buffer, offset, count, token).ConfigureAwait(false);
    }, CancellationToken.None);
    static InvalidOperationException Backwards() => new("Sequential field offsets cannot move backwards.");

    void ValidateChild(long version)
    {
        if (_reader?.IsClosed != false || !_onRow || version != _childVersion || _field is null)
            throw new InvalidOperationException("The sequential field reader is no longer current.");
    }
    Task<int> ChildBytesAsync(long version, byte[] buffer, int offset, int count, CancellationToken token) => RunAsync(async call =>
    { ValidateChild(version); return await _field!.ReadBytesAsync(buffer, offset, count, call).ConfigureAwait(false); }, token);
    Task<int> ChildCharsAsync(long version, char[] buffer, int offset, int count, CancellationToken token, bool peek = false) => RunAsync(async call =>
    { ValidateChild(version); return await _field!.ReadCharsAsync(buffer, offset, count, call, peek).ConfigureAwait(false); }, token);
    void DisposeChild(long version)
    {
        if (_connection.IsInsideCallback) throw new InvalidOperationException("A Reader callback cannot reenter its Reader or field resources.");
        if (version == _childVersion && _onRow && _reader?.IsClosed == false) CheckAccess();
    }

    sealed class FieldCursor
    {
        readonly PgQuerySession.RowExchange _rows;
        readonly PgByteaDecoder? _binary;
        readonly Decoder? _text;
        readonly byte[] _raw = new byte[8192];
        readonly char[] _chars = new char[4096];
        readonly byte[]? _memoryBytes;
        readonly string? _memoryText;
        int _rawPosition;
        int _rawCount;
        int _charPosition;
        int _charCount;
        bool _complete;
        bool _failed;
        byte[]? _byteScratch;
        char[]? _charScratch;
        internal byte[] ByteScratch => _byteScratch ??= new byte[8192];
        internal char[] CharScratch => _charScratch ??= new char[4096];
        internal long Position { get; private set; }
        internal bool Started { get; private set; }
        internal long? MemoryLength => _memoryBytes?.Length ?? _memoryText?.Length;
        internal FieldCursor(PgQuerySession.RowExchange rows, bool binary, object? value)
        { _rows = rows; _binary = binary ? new PgByteaDecoder() : null; _text = binary ? null : PgBinary.Utf8.GetDecoder(); _memoryBytes = value as byte[]; _memoryText = value as string; }
        async Task FillAsync(CancellationToken token)
        {
            if (_rawPosition != _rawCount || _rows.ColumnRemaining == 0) return;
            _rawPosition = 0; _rawCount = await _rows.ReadColumnAsync(_raw, 0, _raw.Length, token).ConfigureAwait(false);
        }
        internal async Task<int> ReadBytesAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (_failed) throw new InvalidOperationException("The field decoder previously failed.");
            if (count == 0) return 0;
            Started = true;
            if (_memoryBytes is not null)
            {
                var copied = (int)Math.Min(count, _memoryBytes.Length - Position);
                Array.Copy(_memoryBytes, (int)Position, buffer, offset, copied); Position += copied; return copied;
            }
            try
            {
                while (!_complete)
                {
                    await FillAsync(token).ConfigureAwait(false);
                    var written = _binary!.Convert(_raw, _rawPosition, _rawCount - _rawPosition, buffer, offset, count, out var consumed); _rawPosition += consumed;
                    if (_rawPosition == _rawCount && _rows.ColumnRemaining == 0) { _binary.Complete(); _complete = true; }
                    if (written != 0) { Position += written; return written; }
                }
                return 0;
            }
            catch { _failed = true; throw; }
        }
        internal async Task<int> ReadCharsAsync(char[] buffer, int offset, int count, CancellationToken token, bool peek = false)
        {
            if (_failed) throw new InvalidOperationException("The field decoder previously failed.");
            if (count == 0) return 0;
            Started = true;
            if (_memoryText is not null)
            {
                var copied = (int)Math.Min(count, _memoryText.Length - Position);
                _memoryText.CopyTo((int)Position, buffer, offset, copied); if (!peek) Position += copied; return copied;
            }
            try
            {
                while (_charPosition == _charCount && !_complete)
                {
                    await FillAsync(token).ConfigureAwait(false);
                    _text!.Convert(_raw, _rawPosition, _rawCount - _rawPosition, _chars, 0, _chars.Length,
                        _rows.ColumnRemaining == 0, out var consumed, out _charCount, out var completed);
                    _rawPosition += consumed; _charPosition = 0;
                    if (Array.IndexOf(_chars, '\0', 0, _charCount) >= 0) throw new IOException("A streaming PG text field contains NUL.");
                    _complete = completed && _rawPosition == _rawCount && _rows.ColumnRemaining == 0;
                }
                var copied = Math.Min(count, _charCount - _charPosition);
                Array.Copy(_chars, _charPosition, buffer, offset, copied);
                if (!peek) { _charPosition += copied; Position += copied; } return copied;
            }
            catch { _failed = true; throw; }
        }
    }

    sealed class FieldStream(KingbaseReaderExecution owner, long version) : Stream
    {
        bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            owner.CheckAccess();
            if (_disposed) throw new ObjectDisposedException(nameof(FieldStream)); ValidateBuffer(buffer, offset, count);
            return owner.ChildBytesAsync(version, buffer, offset, count, token);
        }
#if !NETSTANDARD2_0
        public override int Read(Span<byte> buffer)
        { var temporary = new byte[Math.Min(buffer.Length, 8192)]; var count = Read(temporary, 0, temporary.Length); temporary.AsSpan(0, count).CopyTo(buffer); return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var array) ? new(ReadAsync(array.Array!, array.Offset, array.Count, cancellationToken)) : new(ReadMemoryAsync(buffer, cancellationToken));
        async Task<int> ReadMemoryAsync(Memory<byte> buffer, CancellationToken token)
        { var temporary = new byte[Math.Min(buffer.Length, 8192)]; var count = await ReadAsync(temporary, 0, temporary.Length, token).ConfigureAwait(false); temporary.AsMemory(0, count).CopyTo(buffer); return count; }
#endif
        protected override void Dispose(bool disposing) { if (disposing) { owner.DisposeChild(version); _disposed = true; } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    sealed class FieldTextReader(KingbaseReaderExecution owner, long version) : TextReader
    {
        bool _disposed;
        readonly char[] _one = new char[1];
        public override int Read() => Read(_one, 0, 1) == 0 ? -1 : _one[0];
        public override int Peek()
        { EnsureActive(); return owner.ChildCharsAsync(version, _one, 0, 1, CancellationToken.None, true).GetAwaiter().GetResult() == 0 ? -1 : _one[0]; }
        public override int Read(char[] buffer, int index, int count) => ReadAsync(buffer, index, count).GetAwaiter().GetResult();
        public override Task<int> ReadAsync(char[] buffer, int index, int count)
        { EnsureActive(); ValidateBuffer(buffer, index, count); return owner.ChildCharsAsync(version, buffer, index, count, CancellationToken.None); }
#if !NETSTANDARD2_0
        public override int Read(Span<char> buffer)
        { var temporary = new char[Math.Min(buffer.Length, 4096)]; var count = Read(temporary, 0, temporary.Length); temporary.AsSpan(0, count).CopyTo(buffer); return count; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            return MemoryMarshal.TryGetArray((ReadOnlyMemory<char>)buffer, out var array) ? new(owner.ChildCharsAsync(version, array.Array!, array.Offset, array.Count, cancellationToken)) : new(ReadMemoryAsync(buffer, cancellationToken));
        }
        async Task<int> ReadMemoryAsync(Memory<char> buffer, CancellationToken token)
        { var temporary = new char[Math.Min(buffer.Length, 4096)]; var count = await owner.ChildCharsAsync(version, temporary, 0, temporary.Length, token).ConfigureAwait(false); temporary.AsMemory(0, count).CopyTo(buffer); return count; }
#endif
        void EnsureActive() { owner.CheckAccess(); if (_disposed) throw new ObjectDisposedException(nameof(FieldTextReader)); }
        protected override void Dispose(bool disposing) { if (disposing) { owner.DisposeChild(version); _disposed = true; } base.Dispose(disposing); }
    }
    static void ValidateBuffer(Array buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
    }
}
