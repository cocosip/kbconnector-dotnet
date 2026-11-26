using System.Text;

namespace KingbaseConnector.Protocol;

// A snapshot of caller-owned input. Preparing or describing this value must not read it.
internal sealed class PgParameterInput(object source, int size)
{
    internal object Source { get; } = source;
    internal int Size { get; } = size;
    internal static PgParameterInput Create(object source, string codec, int size)
    {
        if (source is Stream stream)
        {
            if (codec != "bytea") throw new InvalidCastException("Streaming binary input requires a bytea/BLOB type.");
            if (!stream.CanRead) throw new ArgumentException("Streaming binary input requires a readable Stream.", nameof(source));
        }
        else if (source is not TextReader || codec != "text")
            throw new InvalidCastException("Streaming character input requires TextReader and a text/CLOB/NCLOB type.");
        return new PgParameterInput(source, size);
    }

    internal async Task WriteAsync(FileStream destination, int limit, CancellationToken token)
    {
        var bytes = new byte[16 * 1024]; var encoded = new byte[48 * 1024];
        var chars = Source is TextReader ? new char[8192] : Array.Empty<char>();
        var encoder = PgBinary.Utf8.GetEncoder(); long units = 0;
        try
        {
            if (Source is Stream) await AppendAsync(new byte[] { (byte)'\\', (byte)'x' }, 2).ConfigureAwait(false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Isolate arbitrary caller code, including implementations that ignore cancellation.
                var work = Task.Run(() => Source is Stream stream ? stream.ReadAsync(bytes, 0, bytes.Length, token) :
                    ((TextReader)Source).ReadAsync(chars, 0, chars.Length), token);
                var read = await AwaitReadAsync(work, Source is Stream ? bytes : chars, token).ConfigureAwait(false);
                if (read < 0 || read > (Source is Stream ? bytes.Length : chars.Length)) throw new IOException("Streaming parameter returned an invalid read count.");
                units += read;
                if (Size != 0 && units > Size) throw new ArgumentException("Parameter input exceeds Size; truncation is not supported.");
                int count;
                if (Source is Stream)
                {
                    if (read == 0) break;
                    const string hex = "0123456789abcdef";
                    for (var i = 0; i < read; i++) { encoded[i * 2] = (byte)hex[bytes[i] >> 4]; encoded[i * 2 + 1] = (byte)hex[bytes[i] & 15]; }
                    count = read * 2;
                }
                else
                {
                    for (var i = 0; i < read; i++) if (chars[i] == '\0') throw new EncoderFallbackException("NUL is not valid in a character parameter.");
                    count = encoder.GetBytes(chars, 0, read, encoded, 0, flush: read == 0);
                }
                await AppendAsync(encoded, count).ConfigureAwait(false);
                if (read == 0) break;
            }
        }
        finally { Array.Clear(bytes, 0, bytes.Length); Array.Clear(encoded, 0, encoded.Length); Array.Clear(chars, 0, chars.Length); }

        async Task AppendAsync(byte[] buffer, int count)
        {
            token.ThrowIfCancellationRequested();
            if (destination.Position + count > limit) throw new ArgumentException("Streaming parameters exceed the PG Bind frame limit.");
            await destination.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
        }
    }

    static async Task<int> AwaitReadAsync(Task<int> work, Array buffer, CancellationToken token)
    {
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => canceled.TrySetResult(true)))
        {
            if (await Task.WhenAny(work, canceled.Task).ConfigureAwait(false) != work)
            {
                _ = work.ContinueWith(task => { _ = task.Exception; Array.Clear(buffer, 0, buffer.Length); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                token.ThrowIfCancellationRequested();
            }
        }
        var read = await work.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return read;
    }
}
