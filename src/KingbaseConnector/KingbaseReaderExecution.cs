using System.Data;
using System.Diagnostics;
using System.Text;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal sealed partial class KingbaseReaderExecution
{
    readonly KingbaseCommand _command;
    readonly KingbaseConnection _connection;
    readonly CancellationTokenSource _cancel;
    readonly CancellationToken _caller;
    readonly CancellationTokenSource _deadline = new();
    readonly CancellationTokenSource _stop = new();
    readonly CancellationTokenSource _lifetime;
    readonly CancellationToken _token;
    readonly Stopwatch _elapsed = Stopwatch.StartNew();
    readonly int _timeout;
    readonly CommandBehavior _behavior;
    readonly object _terminal = new();
    readonly Task _timer;
    CancellationTokenRegistration _abort;
    PgPhysicalSession? _session;
    PgQuerySession? _queries;
    long _generation;
    long _writes;
    PgQuerySession.RowExchange? _rows;
    KingbaseDataReader? _reader;
    int _finished;
    int _busy;
    int _ordinal = -1;
    bool _onRow;
    bool _peeked;
    bool _hasRows;
    bool _returnedRow;
    bool _hidden;
    bool _materialized;
    string? _value;
    int _recordsAffected = -1;
    PgParameterBindings? _bindings;
    PgTextResult? _header;
    bool _acknowledged;
    bool _failed;
    bool _cleaning;
    bool _callbackFailed;

    internal KingbaseReaderExecution(KingbaseCommand command, KingbaseConnection connection, CancellationTokenSource cancel,
        CancellationToken caller, int timeout, CommandBehavior behavior)
    {
        _command = command; _connection = connection; _cancel = cancel; _caller = caller; _timeout = timeout; _behavior = behavior;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, cancel.Token, _deadline.Token); _token = _lifetime.Token;
        _timer = timeout == 0 ? Task.CompletedTask : KingbaseCommand.WatchDeadline((long)timeout * 1000, _elapsed, _deadline, _stop.Token);
    }
    internal PgTextResult? Header => _hidden ? null : _header;
    internal int RecordsAffected => _recordsAffected;
    internal bool OnRow => _onRow;

    internal void Attach(PgPhysicalSession session, long generation)
    {
        _session = session; _queries = session.Queries; _generation = generation; _writes = _queries.WriteAttempts;
        _abort = _token.Register(Abort);
    }
    internal async Task StartAsync(PgParameterBindings bindings, PgPreparedStatement? prepared, Action<PgPreparedStatement> attach)
    {
        _bindings = bindings;
        _rows = await _session!.Queries.BeginRowsAsync(bindings.Sql, _session.Catalog, bindings.Parameters, _token, ValidateSession, prepared, attach).ConfigureAwait(false);
        _header = _rows.Header;
        await ValidateHeaderAsync().ConfigureAwait(false);
        if (_connection.HasCallbackFailure(_session, _generation))
        { await DrainAsync().ConfigureAwait(false); ThrowCallbacks(); }
        CheckCancellation(CancellationToken.None);
    }
    internal void Publish(KingbaseDataReader reader)
    {
        _connection.PublishReader(_command, _session!, _generation, reader, keepExecuting: true); _reader = reader;
        if (_rows!.IsSynchronized) CompleteCurrent();
    }
    void ValidateSession()
    {
        var queries = _session!.Queries;
        if (!queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8" ||
            (queries.Parameters.TryGetValue("database_mode", out var mode) && PgIdentityProbe.ParseMode(mode) != _session.Identity.EffectiveMode))
        { _session.Dispose(); throw new NotSupportedException("The Kingbase streaming session identity or encoding changed."); }
    }
    internal void CheckAccess()
    {
        if (_connection.IsInsideCallback) throw new InvalidOperationException("A Reader callback cannot reenter its Reader or field resources.");
        if (Volatile.Read(ref _busy) != 0) throw new InvalidOperationException("A streaming Reader operation is already active.");
    }
    async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested(); CheckAccess();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A streaming Reader operation is already active.");
        try
        {
            if (_failed) throw new InvalidOperationException("The streaming Reader execution has terminated after a failure.");
            CheckCancellation(caller); _connection.ValidateReaderOwner(_command, _session!, _generation, _reader);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(caller, _token);
            using var abort = caller.Register(Abort);
            var result = await action(call.Token).ConfigureAwait(false);
            if (!_connection.OwnsReaderExecution(_command, _session!, _generation, _reader))
            { CheckCancellation(caller); throw new InvalidOperationException("The Kingbase reader no longer owns its connection."); }
            if (_connection.HasCallbackFailure(_session!, _generation))
            {
                await DrainAsync().ConfigureAwait(false);
                try { ThrowCallbacks(); }
                catch { _failed = true; throw; }
                finally { if (_rows!.IsSynchronized) CompleteCurrent(); }
            }
            if (_rows!.IsSynchronized) CompleteCurrent();
            return result;
        }
        catch (Exception error)
        {
            if (_failed || error is IOException or OperationCanceledException or TimeoutException or PgQueryException or DecoderFallbackException || !_session!.IsUsable)
                throw Failure(error, caller);
            throw;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
    internal Task<bool> HasRowsAsync() => RunAsync(async token =>
    {
        if (_hidden || _hasRows || _rows!.IsSynchronized) return !_hidden && _hasRows;
        _peeked = await _rows.ReadRowAsync(token).ConfigureAwait(false); _hasRows |= _peeked; return _hasRows;
    }, CancellationToken.None);
    internal Task<bool> ReadAsync(CancellationToken token) => RunAsync(async call =>
    {
        _onRow = false; ResetColumn();
        if (_hidden) return false;
        if (_returnedRow && (_behavior & CommandBehavior.SingleRow) != 0) { await DrainAllCoreAsync(call).ConfigureAwait(false); return false; }
        var read = _peeked || await _rows!.ReadRowAsync(call).ConfigureAwait(false); _peeked = false;
        _header = _rows!.Header;
        _onRow = read; _returnedRow |= read; _hasRows |= read; return read;
    }, token);
    internal Task<bool> NextResultAsync(CancellationToken token) => RunAsync(async call =>
    {
        _onRow = false; _peeked = false; ResetColumn();
        if ((_behavior & (CommandBehavior.SingleResult | CommandBehavior.SingleRow)) != 0)
        { await DrainAllCoreAsync(call).ConfigureAwait(false); _hidden = true; return false; }
        await _rows!.DrainAsync(call).ConfigureAwait(false); AcknowledgeCurrent();
        if (HasMoreBatchItems)
        { await StartNextBatchItemAsync(call).ConfigureAwait(false); _header = _rows!.Header; _hasRows = false; _returnedRow = false; return true; }
        _hidden = true; return false;
    }, token);
    void ResetColumn() { _ordinal = -1; _materialized = false; _value = null; _field = null; _childVersion++; }

    async Task<int> SelectAsync(int ordinal, CancellationToken token)
    {
        if (!_onRow) throw new InvalidOperationException("The Kingbase reader is not positioned on a row.");
        var length = await _rows!.SelectColumnAsync(ordinal, token).ConfigureAwait(false);
        if (ordinal != _ordinal) { ResetColumn(); _ordinal = ordinal; }
        return length;
    }
    internal Task<bool> IsDBNullAsync(int ordinal, CancellationToken token) => RunAsync(async call => await SelectAsync(ordinal, call).ConfigureAwait(false) == -1, token);
    internal Task<object> ValueAsync(int ordinal, PgTextCodecs codecs, bool provider, CancellationToken token) => RunAsync(async call =>
    {
        await MaterializeAsync(ordinal, call).ConfigureAwait(false);
        return codecs.DecodeStreaming(Header!.Columns[ordinal].TypeOid, _value, Header.DateStyle, Header.IntervalStyle, provider);
    }, token);
    internal Task<T> TypedValueAsync<T>(int ordinal, PgTextCodecs codecs, PgTypeDescription type, CancellationToken token) => RunAsync(async call =>
    {
        await MaterializeAsync(ordinal, call).ConfigureAwait(false);
        return KingbaseDataReader.ConvertFieldValue<T>(type, provider => codecs.DecodeStreaming(type.Oid, _value, Header!.DateStyle, Header.IntervalStyle, provider));
    }, token);
    async Task MaterializeAsync(int ordinal, CancellationToken token)
    {
        var length = await SelectAsync(ordinal, token).ConfigureAwait(false);
        if (!_materialized)
        {
            if (_field?.Started == true) throw new InvalidOperationException("A partially consumed sequential field cannot be materialized.");
            if (length >= 0)
            {
                var bytes = new byte[length]; var position = 0;
                while (position < bytes.Length) position += await _rows!.ReadColumnAsync(bytes, position, bytes.Length - position, token).ConfigureAwait(false);
                _value = PgBinary.Utf8.GetString(bytes);
                if (_value.IndexOf('\0') >= 0) throw new IOException("A streaming PG text field contains NUL.");
            }
            _materialized = true;
            _field = null; _childVersion++;
        }
    }

    async Task DrainAsync()
    {
        if (Volatile.Read(ref _finished) != 0 || _failed || _rows is null || (_rows.IsSynchronized && !HasMoreBatchItems) || !_session!.IsUsable) return;
        if (!_connection.OwnsReaderExecution(_command, _session, _generation, _reader))
        { _rows.Dispose(); _failed = true; RecordBatchFailure(); return; }
        _cleaning = true;
        _abort.Dispose(); _connection.SuppressReaderCallbacks(_command, _session, _generation);
        using var cleanup = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
        var timer = KingbaseCommand.WatchDeadline((long)_connection.CleanupTimeout * 1000, Stopwatch.StartNew(), cleanup, stop.Token);
        using var abort = cleanup.Token.Register(Abort);
        try { await DrainAllCoreAsync(cleanup.Token).ConfigureAwait(false); }
        catch (Exception) when (cleanup.IsCancellationRequested) { throw new TimeoutException("The Kingbase Reader cleanup deadline elapsed; the session was retired."); }
        finally { stop.Cancel(); await timer.ConfigureAwait(false); }
    }
    internal async Task CloseAsync(Action closed)
    {
        CheckAccess(); if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A streaming Reader operation is already active.");
        try
        {
            await DrainAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _finished) == 0)
            {
                if (_rows?.IsSynchronized == true && _session!.IsUsable && !_token.IsCancellationRequested && _connection.OwnsReaderExecution(_command, _session, _generation, _reader)) AcknowledgeCurrent();
                else if (_queries is not null)
                { try { _rows?.Dispose(); } finally { _failed = true; RecordBatchFailure(); } }
            }
        }
        catch (Exception error) { throw Failure(error, CancellationToken.None); }
        finally
        {
            try { Finish(); }
            finally { _onRow = false; ResetColumn(); Volatile.Write(ref _busy, 0); closed(); }
        }
    }
    internal void InvalidateFromConnection()
    {
        bool finish;
        lock (_terminal)
        {
            if (Volatile.Read(ref _finished) != 0) return;
            _failed = true;
            try { _rows?.Dispose(); } catch { }
            RecordBatchFailure(); _onRow = false; finish = ClaimFinishOwned();
        }
        if (finish) FinishClaimed();
    }
    void Observe() => _connection.ObserveReaderTransaction(_command, _session!, _generation, _rows?.IsSynchronized == true,
        _queries!.TransactionStatus, _rows?.IsSynchronized == true && _bindings?.EndsTransaction == true,
        rawRollback: _rows?.IsSynchronized == true && _bindings?.InvalidatesLargeObjects == true,
        rawScopeControl: _rows?.IsSynchronized == true && _bindings?.ChangesLargeObjectScopes == true);
    void CompleteCurrent()
    {
        bool finish;
        lock (_terminal)
        {
            if (Volatile.Read(ref _finished) != 0) return;
            AcknowledgeCurrent(); finish = !HasMoreBatchItems;
            if (finish && _batch is not null && !_failed) _batch.FailedCommandIndex = null;
            if (finish) finish = ClaimFinishOwned();
        }
        if (finish) FinishClaimed();
    }
    void Abort()
    {
        var session = _session;
        if (Volatile.Read(ref _finished) == 0 && session is not null && _queries!.HasActiveExchange && _queries.WriteAttempts != _writes)
            try { _connection.AbortExecution(_command, session, _generation); } catch { }
    }
    internal Exception FailStart(Exception error) => Failure(error, _caller);
    Exception Failure(Exception error, CancellationToken caller)
    {
        bool finish;
        lock (_terminal)
        {
            if (Volatile.Read(ref _finished) == 0)
            {
            _failed = true;
            if (_rows?.IsSynchronized != true) { try { _rows?.Dispose(); } catch { } Abort(); }
            RecordBatchFailure();
            if (_session is { IsUsable: true } && !_acknowledged) { try { Observe(); } catch { _session.Dispose(); } }
            }
            finish = ClaimFinishOwned();
        }
        if (finish) FinishClaimed();
        if (caller.IsCancellationRequested) return new OperationCanceledException(caller);
        if (_caller.IsCancellationRequested) return new OperationCanceledException(_caller);
        if (!_cleaning && _timeout != 0 && _elapsed.ElapsedMilliseconds >= (long)_timeout * 1000) return new TimeoutException("The Kingbase Reader command deadline elapsed; execution may have reached the server.");
        if (_token.IsCancellationRequested && (!_cleaning || _cancel.IsCancellationRequested)) return new OperationCanceledException(_token);
        return error is PgQueryException query ? new KingbaseException(query.ServerError, _batchCommands is null ? null : _batchCommands[_batchIndex]) : error;
    }
    void CheckCancellation(CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (_rows?.IsSynchronized == true) return;
        if (_timeout != 0 && _elapsed.ElapsedMilliseconds >= (long)_timeout * 1000) throw new TimeoutException("The Kingbase Reader command deadline elapsed.");
        _token.ThrowIfCancellationRequested();
    }
    void Finish()
    {
        bool finish; lock (_terminal) finish = ClaimFinishOwned();
        if (finish) FinishClaimed();
    }
    bool ClaimFinishOwned()
    {
        if (Volatile.Read(ref _finished) != 0) return false;
        Volatile.Write(ref _finished, 1); return true;
    }
    void FinishClaimed()
    {
        _abort.Dispose(); _stop.Cancel();
        try
        {
            if (_session is not null)
            {
                _connection.EndExecution(_command, _session, _generation);
                try { _connection.RetireIfBroken(_session); } catch { /* Preserve the primary execution failure. */ }
            }
        }
        finally { _command.ReleaseStreamingExecution(_cancel); _ = DisposeLifetimeAsync(); }
    }
    void ThrowCallbacks()
    {
        try { _connection.ThrowCallbackFailure(_session!, _generation); }
        catch
        {
            lock (_terminal)
            {
                _callbackFailed = true;
                if (_batch is not null && _acknowledged && !HasMoreBatchItems && Volatile.Read(ref _finished) == 0) _batch.FailedCommandIndex = null;
            }
            throw;
        }
    }
    async Task DisposeLifetimeAsync()
    {
        try { await _timer.ConfigureAwait(false); }
        finally { _lifetime.Dispose(); _deadline.Dispose(); _stop.Dispose(); _cancel.Dispose(); }
    }
}
