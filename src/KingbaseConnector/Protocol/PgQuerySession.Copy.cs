using System.Globalization;

namespace KingbaseConnector.Protocol;

internal sealed partial class PgQuerySession
{
    internal CopyExchange BeginCopy(Action validate)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A PG query exchange is already active.");
        try { ThrowIfDisposed(); validate(); return new CopyExchange(this, validate); }
        catch { Volatile.Write(ref _busy, 0); throw; }
    }

    internal sealed class CopyExchange(PgQuerySession owner, Action validate)
    {
        readonly byte[] _scratch = new byte[1];
        PgServerError? _error;
        int _controls;
        int _controlBytes;
        int _released;
        int _terminalRequest;
        readonly object _state = new();
        Task? _completion;
        internal bool IsImport { get; private set; }
        internal bool IsBinary { get; private set; }
        internal int ColumnCount { get; private set; }
        internal ulong? RowsCopied { get; private set; }
        internal bool IsSynchronized { get; private set; }
        internal void ThrowIfReceiveEnded() { if (_completion?.IsCompleted == true) _completion.GetAwaiter().GetResult(); }

        bool RequestTerminal(int request)
        {
            lock (_state)
            {
                if (_error is not null || _completion!.IsCompleted) return false;
                _terminalRequest = request; return true;
            }
        }

        internal async Task StartAsync(string sql, CancellationToken token)
        {
            var request = PgFrontendMessages.Query(sql);
            try { await owner._wire.WriteMessageAsync(request, token).ConfigureAwait(false); }
            finally { Array.Clear(request, 0, request.Length); }
            while (true)
            {
                var message = await owner._wire.ReadMessageAsync(token, MaxPayload).ConfigureAwait(false);
                if (Control(message.Code, message.Payload)) continue;
                if (message.Code == (byte)'E') { _error = PgBackendMessages.ParseError(message.Payload); await FinishAsync(token).ConfigureAwait(false); throw InvalidExchange(); }
                if (message.Code is not ((byte)'G' or (byte)'H')) throw InvalidExchange();
                var payload = message.Payload;
                if (payload.Length < 3 || payload[0] > 1) throw InvalidExchange();
                var columns = (payload[1] << 8) | payload[2];
                if (columns > short.MaxValue || payload.Length != 3 + 2 * columns) throw InvalidExchange();
                for (var i = 0; i < columns; i++)
                    if (payload[3 + 2 * i] != 0 || payload[4 + 2 * i] != payload[0]) throw InvalidExchange();
                IsImport = message.Code == (byte)'G'; IsBinary = payload[0] == 1; ColumnCount = columns;
                validate();
                if (IsImport)
                {
                    _completion = ReceiveImportCompletionAsync(token);
                    _ = _completion.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    if (_completion.IsCompleted) await _completion.ConfigureAwait(false);
                }
                return;
            }
        }

        internal async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            while (true)
            {
                var message = await owner._wire.ReadCopyMessageAsync(buffer, offset, count, token).ConfigureAwait(false);
                if (message.Code == (byte)'d')
                {
                    if (message.Count == 0) { Control(message.Code, Array.Empty<byte>()); continue; }
                    return message.Count;
                }
                if (Control(message.Code, message.Payload)) continue;
                if (message.Code == (byte)'E') { _error = PgBackendMessages.ParseError(message.Payload); await FinishAsync(token).ConfigureAwait(false); throw InvalidExchange(); }
                if (message.Code != (byte)'c' || message.Payload.Length != 0) throw InvalidExchange();
                await FinishAsync(token).ConfigureAwait(false); return 0;
            }
        }

        internal async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            while (count > 0)
            {
                if (_completion!.IsCompleted || Volatile.Read(ref _error) is not null) await _completion.ConfigureAwait(false);
                var size = Math.Min(count, 65536); var message = new byte[5 + size];
                message[0] = (byte)'d'; PgBinary.WriteInt32(message, 1, size + 4); Buffer.BlockCopy(buffer, offset, message, 5, size);
                try { await owner._wire.WriteCopyMessageAsync(message, token).ConfigureAwait(false); }
                finally { Array.Clear(message, 0, message.Length); }
                offset += size; count -= size;
            }
            if (_completion!.IsCompleted || Volatile.Read(ref _error) is not null) await _completion.ConfigureAwait(false);
        }

        internal async Task CompleteAsync(CancellationToken token)
        {
            if (RequestTerminal(1))
            {
                await owner._wire.WriteCopyMessageAsync(new byte[] { (byte)'c', 0, 0, 0, 4 }, token).ConfigureAwait(false);
            }
            await _completion!.ConfigureAwait(false);
        }

        internal async Task CancelAsync(CancellationToken token)
        {
            var sent = false;
            var text = PgBinary.Utf8.GetBytes("Client canceled COPY.\0"); var message = new byte[text.Length + 5];
            message[0] = (byte)'f'; PgBinary.WriteInt32(message, 1, text.Length + 4); Buffer.BlockCopy(text, 0, message, 5, text.Length);
            try
            {
                if (RequestTerminal(2))
                {
                    await owner._wire.WriteCopyMessageAsync(message, token).ConfigureAwait(false);
                    sent = true;
                }
                try { await _completion!.ConfigureAwait(false); }
                catch (PgQueryException error) when (sent && error.ServerError.SqlState == "57014" && IsSynchronized) { return; }
                throw new IOException("COPY cancellation was not acknowledged as a cancellation.");
            }
            finally { Array.Clear(message, 0, message.Length); }
        }

        async Task ReceiveImportCompletionAsync(CancellationToken token)
        {
            try { await FinishAsync(token).ConfigureAwait(false); }
            catch (PgQueryException) when (IsSynchronized) { throw; }
            catch { owner.Dispose(); Release(); throw; }
        }

        async Task FinishAsync(CancellationToken token)
        {
            while (true)
            {
                var message = await owner._wire.ReadCopyMessageAsync(_scratch, 0, 1, token).ConfigureAwait(false);
                if (Control(message.Code, message.Payload)) continue;
                switch (message.Code)
                {
                    case (byte)'E':
                        var error = PgBackendMessages.ParseError(message.Payload);
                        lock (_state)
                        {
                            if (_error is not null) throw InvalidExchange();
                            RowsCopied = null; Volatile.Write(ref _error, error);
                        }
                        break;
                    case (byte)'C':
                        if (_error is not null || RowsCopied is not null) throw InvalidExchange();
                        if (IsImport && Volatile.Read(ref _terminalRequest) != 1) throw InvalidExchange();
                        var offset = 0; var tag = PgBinary.ReadCString(message.Payload, ref offset);
                        if (offset != message.Payload.Length || !tag.StartsWith("COPY ", StringComparison.Ordinal) ||
                            !ulong.TryParse(tag.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out var rows)) throw InvalidExchange();
                        RowsCopied = rows; break;
                    case (byte)'Z':
                        if (_error is null && RowsCopied is null) throw InvalidExchange();
                        owner.TransactionStatus = PgBackendMessages.ParseReadyForQuery(message.Payload); validate();
                        IsSynchronized = true; Release();
                        if (_error is not null)
                        {
                            var severity = _error.Fields.TryGetValue((byte)'V', out var invariant) ? invariant : _error.Fields[(byte)'S'];
                            if (severity != "ERROR" || _error.SqlState.StartsWith("08", StringComparison.Ordinal) || _error.SqlState == "40003") owner.Dispose();
                            throw new PgQueryException(_error);
                        }
                        return;
                    default: throw InvalidExchange();
                }
            }
        }

        bool Control(byte code, byte[] payload)
        {
            if (++_controls > MaxFrames || (_controlBytes += payload.Length) > MaxPayload) throw new IOException("COPY control traffic exceeds its bounded limit.");
            switch (code)
            {
                case (byte)'N': var notice = PgBackendMessages.ParseError(payload); validate(); owner.NoticeHandler?.Invoke(new KingbaseNotice(notice)); return true;
                case (byte)'A': var notification = PgBackendMessages.ParseNotification(payload); validate(); owner.NotificationHandler?.Invoke(notification); return true;
                case (byte)'S':
                    var setting = PgBackendMessages.ParseParameterStatus(payload);
                    if (setting.Key == "client_encoding" && setting.Value != "UTF8") throw InvalidExchange();
                    owner.UpdateParameter(setting); validate(); return true;
                default: return false;
            }
        }

        internal void Release() { if (Interlocked.Exchange(ref _released, 1) == 0) Volatile.Write(ref owner._busy, 0); }
    }
}
