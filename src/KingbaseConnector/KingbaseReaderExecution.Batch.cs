using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

internal sealed partial class KingbaseReaderExecution
{
    KingbaseBatch? _batch;
    KingbaseBatchCommand[]? _batchCommands;
    KingbaseTransaction? _transaction;
    int _batchIndex = -1;
    long _batchWrites;
    bool _resultError;
    bool _acknowledgmentError;
    bool HasMoreBatchItems => _batchCommands is not null && _batchIndex + 1 < _batchCommands.Length;

    internal async Task StartBatchAsync(KingbaseBatch batch, KingbaseBatchCommand[] commands, KingbaseTransaction? transaction)
    {
        _batch = batch; _batchCommands = commands; _transaction = transaction;
        await StartNextBatchItemAsync(_token).ConfigureAwait(false); _header = _rows!.Header;
        if (_connection.HasCallbackFailure(_session!, _generation))
        { await DrainAsync().ConfigureAwait(false); ThrowCallbacks(); }
        CheckCancellation(CancellationToken.None);
    }
    async Task StartNextBatchItemAsync(CancellationToken token)
    {
        try { await StartNextBatchItemCoreAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (StatementDeadlineElapsed && !_caller.IsCancellationRequested && !_cancel.IsCancellationRequested)
        { _failed = true; throw new TimeoutException("The Kingbase batch deadline elapsed before the next statement completed."); }
        catch { _failed = true; throw; }
    }
    async Task StartNextBatchItemCoreAsync(CancellationToken token)
    {
        _token.ThrowIfCancellationRequested();
        CheckStatementDeadline();
        _connection.ValidateReaderOwner(_command, _session!, _generation, _reader);
        using var start = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
        token = start.Token;
        lock (_terminal)
        {
            if (_failed || Volatile.Read(ref _finished) != 0) throw new InvalidOperationException("The streaming batch has terminated.");
            _connection.ValidateReaderOwner(_command, _session!, _generation, _reader);
            _batchIndex++; _batch!.FailedCommandIndex = _batchIndex; _acknowledged = false; _rows = null; _bindings = null;
            _batchWrites = _queries!.ExecutionWriteAttempts;
        }
        token.ThrowIfCancellationRequested(); ValidateSession();
        var command = _batchCommands![_batchIndex];
        if (command.RequiresPgMetadata && _session!.Identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
            throw new NotSupportedException("PG-generated commands require PostgreSql compatibility mode.");
        if (_transaction is not null && !_connection.OwnsTransaction(_transaction)) throw new InvalidOperationException("The batch transaction is no longer active on this connection.");
        if (command.CommandType != CommandType.Text) throw new NotSupportedException("Only Text batch commands are implemented.");
        if (string.IsNullOrWhiteSpace(command.CommandText)) throw new InvalidOperationException("Kingbase batch CommandText is empty.");
        _queries.Parameters.TryGetValue("standard_conforming_strings", out var status);
        bool? conforming = status switch { "on" => true, "off" => false, _ => null };
        var bindings = PgParameterBindings.Create(command.CommandText, command.Parameters, _session!.Catalog, _session.Identity.EffectiveMode, conforming);
        _bindings = bindings;
        var statement = command.Prepared;
        var usePrepared = statement is not null && statement.IsValid && ReferenceEquals(statement.Owner, _queries) &&
            command.PreparedGeneration == _generation && statement.Sql == bindings.Sql && statement.LexicalStatus == status &&
            statement.TypeOids.SequenceEqual(bindings.Parameters.Select(p => p.TypeOid));
        if (!usePrepared) command.Prepared = null;
        _rows = await _queries.BeginRowsAsync(bindings.Sql, _session.Catalog, bindings.Parameters, token, ValidateSession, usePrepared ? statement : null,
            prepared => { lock (_command.SyncRoot) if (Volatile.Read(ref _finished) == 0) { command.Prepared = prepared; command.PreparedGeneration = _generation; } }, CheckStatementDeadline).ConfigureAwait(false);
        if (_rows.IsSynchronized) AcknowledgeCurrent();
        await ValidateHeaderAsync().ConfigureAwait(false);
    }
    async Task ValidateHeaderAsync()
    {
        var codecs = new PgTextCodecs(_session!.Catalog);
        try
        {
            foreach (var column in _rows!.Header.Columns)
            {
                _ = codecs.GetFieldType(column.TypeOid);
                if (column.TypeSize != _session.Catalog.ByOid[column.TypeOid].Size)
                    throw new NotSupportedException("PG result type size does not match its catalog.");
            }
        }
        catch (NotSupportedException)
        { _resultError = true; await DrainCurrentBoundedAsync().ConfigureAwait(false); throw; }
    }
    void AcknowledgeCurrent()
    {
        lock (_terminal) AcknowledgeCurrentOwned();
    }
    void AcknowledgeCurrentOwned(bool observe = true)
    {
        if (_acknowledged || Volatile.Read(ref _finished) != 0) return;
        if (_rows?.IsSynchronized != true) return;
        _acknowledged = true;
        if (_batchCommands is not null)
        {
            _batchCommands[_batchIndex].ExecutionStatus = KingbaseBatchCommandStatus.Succeeded;
        }
        try
        {
            var affected = KingbaseDataReader.AffectedCount(_rows.Header.CommandTag);
            if (_batchCommands is not null) _batchCommands[_batchIndex].Affected = affected ?? -1;
            if (affected is { } count) _recordsAffected = _recordsAffected < 0 ? count : checked(_recordsAffected + count);
            if (observe) Observe();
        }
        catch { _acknowledgmentError = true; _failed = true; throw; }
    }
    async Task DrainAllCoreAsync(CancellationToken token)
    {
        while (true)
        {
            await _rows!.DrainAsync(token).ConfigureAwait(false); AcknowledgeCurrent();
            if (!HasMoreBatchItems) break;
            if (_cleaning && (_token.IsCancellationRequested || !_connection.OwnsReaderExecution(_command, _session!, _generation, _reader)))
            { RecordBatchFailure(); if (StatementDeadlineElapsed && !_caller.IsCancellationRequested && !_cancel.IsCancellationRequested) CheckStatementDeadline(); return; }
            await StartNextBatchItemAsync(token).ConfigureAwait(false);
        }
        lock (_terminal) if (_batch is not null && !_failed && Volatile.Read(ref _finished) == 0) _batch.FailedCommandIndex = null;
    }
    async Task DrainCurrentBoundedAsync()
    {
        _cleaning = true; _abort.Dispose(); _connection.SuppressReaderCallbacks(_command, _session!, _generation);
        using var cleanup = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
        var timer = KingbaseCommand.WatchDeadline((long)_connection.CleanupTimeout * 1000, System.Diagnostics.Stopwatch.StartNew(), cleanup, stop.Token);
        using var abort = cleanup.Token.Register(Abort);
        try { await _rows!.DrainAsync(cleanup.Token).ConfigureAwait(false); AcknowledgeCurrent(); }
        catch (Exception) when (cleanup.IsCancellationRequested) { throw new TimeoutException("The Kingbase Reader cleanup deadline elapsed; the session was retired."); }
        finally { stop.Cancel(); await timer.ConfigureAwait(false); }
    }
    void RecordBatchFailure()
    {
        lock (_terminal) if (Volatile.Read(ref _finished) == 0) RecordBatchFailureOwned();
    }
    void RecordBatchFailureOwned()
    {
        if (_batchCommands is null || _batchIndex < 0) return;
        if (_rows?.IsSynchronized == true && _queries!.CommandRejection is null && _queries.UncertainError is null)
        {
            try { AcknowledgeCurrentOwned(observe: false); } catch { }
            if (_acknowledged && !HasMoreBatchItems && !_resultError && !_acknowledgmentError)
            { _batch!.FailedCommandIndex = null; return; }
        }
        if (_callbackFailed && _acknowledged && !HasMoreBatchItems) return;
        _batch!.FailedCommandIndex = _acknowledged && HasMoreBatchItems && (_token.IsCancellationRequested || StatementDeadlineElapsed ||
            !_connection.OwnsReaderExecution(_command, _session!, _generation, _reader)) ? _batchIndex + 1 : _batchIndex;
        var command = _batchCommands[_batchIndex];
        if (command.ExecutionStatus != KingbaseBatchCommandStatus.NotExecuted) return;
        command.ExecutionStatus = _queries!.ExecutionWriteAttempts != _batchWrites && (_session?.IsUsable != true || _queries.UncertainError is not null) &&
            !(_rows?.IsSynchronized == true && _queries.CommandRejection is not null)
            ? KingbaseBatchCommandStatus.OutcomeUnknown : KingbaseBatchCommandStatus.Failed;
    }
    bool StatementDeadlineElapsed => (_timeout != 0 && _elapsed.ElapsedMilliseconds >= (long)_timeout * 1000) ||
        (_command.SharedDeadline is { } shared && shared.Elapsed.ElapsedMilliseconds >= shared.Budget);
    void CheckStatementDeadline()
    { if (StatementDeadlineElapsed) throw new TimeoutException("The Kingbase batch deadline elapsed before the next statement was sent."); }
}
