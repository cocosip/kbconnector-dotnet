namespace KingbaseConnector.Protocol;

internal sealed partial class PgQuerySession
{
    internal async Task<RowExchange> BeginRowsAsync(string sql, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters,
        CancellationToken token = default, Action? validateSession = null, PgPreparedStatement? statement = null,
        Action<PgPreparedStatement>? attachStatement = null, Action? beforeExecute = null)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A PG query exchange is already active.");
        RowExchange? rows = null; ExecutionPlan? plan = null;
        try
        {
            ThrowIfDisposed(); ResetCommandOutcome(); validateSession?.Invoke();
            if (statement is not null && statement.Sql != sql) throw new ArgumentException("The prepared statement does not match this SQL.", nameof(statement));
            plan = statement is not null ? CreatePreparedPlanOwned(statement, catalog, parameters) :
                await CreateAutoPlanOwnedAsync(sql, catalog, parameters, token, validateSession, attachStatement).ConfigureAwait(false);
            beforeExecute?.Invoke();
            rows = new RowExchange(this, validateSession, plan);
            await rows.StartAsync(plan.Frames, token).ConfigureAwait(false);
            return rows;
        }
        catch
        {
            try { rows?.Dispose(); } catch { }
            plan?.Dispose(); if (rows is null) Volatile.Write(ref _busy, 0); throw;
        }
    }

    internal sealed class RowExchange(PgQuerySession owner, Action? validate, ExecutionPlan? plan = null) : IDisposable
    {
        readonly byte[] _small = new byte[4];
        readonly byte[] _discard = new byte[8192];
        readonly object _terminal = new();
        readonly long _writeBaseline = owner.WriteAttempts;
        PgServerError? _error;
        int _phase;
        int _controlFrames;
        int _controlBytes;
        int _ordinal = -1;
        int _fieldLength;
        int _fieldRemaining;
        int _call;
        int _released;
        bool _finished;
        bool _uncertain;
        bool _row;
        bool _description;
        bool _empty;
        internal PgTextResult Header { get; private set; } = new(Array.Empty<PgColumnDescription>(), Array.Empty<IReadOnlyList<string?>>(), null);
        internal bool IsSynchronized { get; private set; }
        internal long RowSequence { get; private set; }
        internal int ColumnOrdinal => _ordinal;
        internal int ColumnRemaining => _fieldRemaining;
        internal int ColumnLength => _fieldLength;

        internal async Task StartAsync(byte[][] frames, CancellationToken token)
        {
            owner.ResetCommandOutcome();
            try
            {
                token.ThrowIfCancellationRequested();
                lock (_terminal) if (_finished) throw new ObjectDisposedException(nameof(RowExchange));
                if (plan?.Exchange == PgQueryExchange.PreparedExecution) _phase = 1;
                foreach (var frame in frames)
                {
                    var writes = owner._wire.WriteAttempts;
                    try { await owner._wire.WriteMessageAsync(frame, token).ConfigureAwait(false); }
                    finally
                    {
                        if (frame.Length != 0 && frame[0] is (byte)'Q' or (byte)'E') owner.ExecutionWriteAttempts += owner._wire.WriteAttempts - writes;
                        Array.Clear(frame, 0, frame.Length);
                    }
                }
                while (true)
                {
                    var message = await owner._wire.ReadMessageAsync(token, MaxPayload - _controlBytes).ConfigureAwait(false);
                    CountControl(message); if (Control(message)) continue;
                    if (message.Code == (byte)'E') { SetError(message.Payload); await ReadToReadyAsync(token).ConfigureAwait(false); throw InvalidExchange(); }
                    switch (message.Code)
                    {
                        case (byte)'1' when _phase == 0 && message.Payload.Length == 0: _phase = 1; break;
                        case (byte)'2' when _phase == 1 && message.Payload.Length == 0: _phase = 2; break;
                        case (byte)'T' when _phase == 2:
                            Header = new PgTextResult(ParseDescription(message.Payload), Array.Empty<IReadOnlyList<string?>>(), null);
                            _description = true; _phase = 3; Snapshot(); validate?.Invoke(); owner.ThrowIfDisposed(); return;
                        case (byte)'n' when _phase == 2 && message.Payload.Length == 0:
                            _phase = 3; await ReadRowCoreAsync(token).ConfigureAwait(false); return;
                        default: throw InvalidExchange();
                    }
                }
            }
            finally { foreach (var frame in frames) Array.Clear(frame, 0, frame.Length); plan?.ReleaseFrames(); }
        }

        internal async Task<bool> ReadRowAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Enter();
            try { return await GuardAsync(() => ReadRowCoreAsync(token)).ConfigureAwait(false); }
            finally { Volatile.Write(ref _call, 0); }
        }
        internal async Task<int> SelectColumnAsync(int ordinal, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Enter();
            try
            {
                if (!_row) throw new InvalidOperationException("The PG reader is not positioned on a row.");
                if (ordinal < 0 || ordinal >= Header.Columns.Count) throw new IndexOutOfRangeException("Invalid PG result column ordinal.");
                if (ordinal < _ordinal) throw new InvalidOperationException("Sequential columns cannot move backwards.");
                return await GuardAsync(() => SelectColumnCoreAsync(ordinal, token)).ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _call, 0); }
        }
        internal async Task<int> ReadColumnAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Enter();
            try
            {
                if (!_row || _ordinal < 0) throw new InvalidOperationException("No PG field has been selected.");
                if (count == 0 || _fieldRemaining == 0) return 0;
                return await GuardAsync(async () =>
                {
                    var received = await owner._wire.ReadRowPayloadAsync(buffer, offset, Math.Min(count, _fieldRemaining), token).ConfigureAwait(false);
                    if (received == 0) throw InvalidExchange(); _fieldRemaining -= received; return received;
                }).ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _call, 0); }
        }
        internal async Task DrainAsync(CancellationToken token)
        { while (await ReadRowAsync(token).ConfigureAwait(false)) { } }

        async Task<bool> ReadRowCoreAsync(CancellationToken token)
        {
            if (IsSynchronized) return false;
            if (_row)
            {
                for (var column = _ordinal + 1; column < Header.Columns.Count; column++) await SelectColumnCoreAsync(column, token).ConfigureAwait(false);
                await SkipFieldAsync(token).ConfigureAwait(false);
                if (owner._wire.RowPayloadRemaining != 0) throw InvalidExchange();
                _row = false;
            }
            while (true)
            {
                var message = await owner._wire.ReadRowMessageAsync(token, MaxPayload - _controlBytes).ConfigureAwait(false);
                if (message.Code == (byte)'D')
                {
                    if (_error is not null || _phase != 3 || !_description) throw InvalidExchange();
                    await ExactPayloadAsync(2, token).ConfigureAwait(false);
                    if (ReadUInt16(_small, 0) != Header.Columns.Count || owner._wire.RowPayloadRemaining < 4 * Header.Columns.Count ||
                        (Header.Columns.Count == 0 && owner._wire.RowPayloadRemaining != 0)) throw InvalidExchange();
                    _row = true; _ordinal = -1; _fieldLength = _fieldRemaining = 0; RowSequence = checked(RowSequence + 1);
                    Snapshot(); validate?.Invoke(); owner.ThrowIfDisposed(); return true;
                }
                CountControl(message); if (Control(message)) continue;
                if (_error is not null && message.Code != (byte)'Z') throw InvalidExchange();
                switch (message.Code)
                {
                    case (byte)'C' when _phase == 3 && !_empty:
                        var offset = 0; var tag = PgBinary.ReadCString(message.Payload, ref offset);
                        if (tag.Length == 0 || offset != message.Payload.Length) throw InvalidExchange();
                        if (tag is "DISCARD ALL" or "DEALLOCATE ALL") owner.InvalidatePreparedStatements();
                        else if (tag == "DEALLOCATE") owner.InvalidatePreparedStatements(forget: false);
                        _phase = 4; Snapshot(tag); break;
                    case (byte)'I' when _phase == 3 && !_description && message.Payload.Length == 0:
                        _empty = true; _phase = 4; break;
                    case (byte)'E': SetError(message.Payload); break;
                    case (byte)'Z':
                        if (_error is null && _phase != 4) throw InvalidExchange();
                        Ready(message.Payload); return false;
                    default: throw InvalidExchange();
                }
            }
        }
        async Task<int> SelectColumnCoreAsync(int target, CancellationToken token)
        {
            while (_ordinal < target)
            {
                await SkipFieldAsync(token).ConfigureAwait(false); await ExactPayloadAsync(4, token).ConfigureAwait(false);
                var length = PgBinary.ReadInt32(_small, 0);
                var futureHeaders = 4 * (Header.Columns.Count - _ordinal - 2);
                if (length < -1 || Math.Max(0, length) > owner._wire.RowPayloadRemaining - futureHeaders ||
                    (futureHeaders == 0 && Math.Max(0, length) != owner._wire.RowPayloadRemaining)) throw InvalidExchange();
                _ordinal++; _fieldLength = length; _fieldRemaining = Math.Max(0, length);
            }
            return _fieldLength;
        }
        async Task SkipFieldAsync(CancellationToken token)
        {
            while (_fieldRemaining != 0)
            {
                var count = await owner._wire.ReadRowPayloadAsync(_discard, 0, Math.Min(_discard.Length, _fieldRemaining), token).ConfigureAwait(false);
                if (count == 0) throw InvalidExchange(); _fieldRemaining -= count;
            }
        }
        async Task ExactPayloadAsync(int count, CancellationToken token)
        {
            if (count > owner._wire.RowPayloadRemaining) throw InvalidExchange();
            var position = 0;
            while (position < count)
            {
                var received = await owner._wire.ReadRowPayloadAsync(_small, position, count - position, token).ConfigureAwait(false);
                if (received == 0) throw InvalidExchange(); position += received;
            }
        }
        async Task ReadToReadyAsync(CancellationToken token)
        {
            while (true)
            {
                var message = await owner._wire.ReadRowMessageAsync(token, MaxPayload - _controlBytes).ConfigureAwait(false); CountControl(message);
                if (Control(message)) continue;
                if (message.Code != (byte)'Z') throw InvalidExchange(); Ready(message.Payload); return;
            }
        }
        void SetError(byte[] payload)
        {
            if (_error is not null) throw InvalidExchange();
            _error = PgBackendMessages.ParseError(payload);
            var severity = _error.Fields.TryGetValue((byte)'V', out var invariant) ? invariant : _error.Fields[(byte)'S'];
            if (_phase != 4 && severity == "ERROR" && _error.SqlState != "40003" && !_error.SqlState.StartsWith("08", StringComparison.Ordinal)) owner.CommandRejection = _error;
            else { _uncertain = true; owner.UncertainError = _error; }
            Snapshot();
        }
        void Ready(byte[] payload)
        {
            var status = PgBackendMessages.ParseReadyForQuery(payload);
            lock (_terminal)
            {
                if (_finished) throw new ObjectDisposedException(nameof(RowExchange));
                owner.ThrowIfDisposed(); owner.TransactionStatus = status; validate?.Invoke(); owner.ThrowIfDisposed();
                if (_error is not null) plan?.Failed(_error); else plan?.Succeeded();
                IsSynchronized = true; _finished = true;
                try { if (_uncertain) { try { owner.Dispose(); } catch { } } }
                finally { plan?.Dispose(); Release(); }
            }
            if (_error is not null) throw new PgQueryException(_error);
        }
        void CountControl(PgMessage message)
        {
            if (++_controlFrames > MaxFrames || (_controlBytes += message.Payload.Length) > MaxPayload)
                throw new IOException("Streaming query control traffic exceeds its bounded limit.");
        }
        bool Control(PgMessage message)
        {
            switch (message.Code)
            {
                case (byte)'S':
                    var setting = PgBackendMessages.ParseParameterStatus(message.Payload);
                    if (setting.Key == "client_encoding" && setting.Value != "UTF8") throw InvalidExchange();
                    owner.UpdateParameter(setting); break;
                case (byte)'N':
                    var notice = PgBackendMessages.ParseError(message.Payload); validate?.Invoke(); owner.NoticeHandler?.Invoke(new KingbaseNotice(notice)); break;
                case (byte)'A':
                    var notification = PgBackendMessages.ParseNotification(message.Payload); validate?.Invoke(); owner.NotificationHandler?.Invoke(notification); break;
                default: return false;
            }
            validate?.Invoke(); owner.ThrowIfDisposed(); return true;
        }
        void Snapshot(string? tag = null)
        {
            owner._parameters.TryGetValue("DateStyle", out var date); owner._parameters.TryGetValue("IntervalStyle", out var interval);
            Header = new PgTextResult(Header.Columns, Array.Empty<IReadOnlyList<string?>>(), tag, date, interval);
        }
        void Enter()
        {
            if (Interlocked.CompareExchange(ref _call, 1, 0) != 0) throw new InvalidOperationException("A streaming PG row operation is already active.");
            try
            {
                lock (_terminal) if (_finished && !IsSynchronized) throw new ObjectDisposedException(nameof(RowExchange));
                if (!IsSynchronized) owner.ThrowIfDisposed();
            }
            catch { Volatile.Write(ref _call, 0); throw; }
        }
        async Task<T> GuardAsync<T>(Func<Task<T>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch { try { Dispose(); } catch { } throw; }
        }
        void Release() { if (Interlocked.Exchange(ref _released, 1) == 0) Volatile.Write(ref owner._busy, 0); }
        public void Dispose()
        {
            lock (_terminal)
            {
                if (_finished) return;
                _finished = true;
                try { if (owner.WriteAttempts != _writeBaseline) owner.Dispose(); }
                finally { plan?.Dispose(); Release(); }
            }
        }
    }
}
