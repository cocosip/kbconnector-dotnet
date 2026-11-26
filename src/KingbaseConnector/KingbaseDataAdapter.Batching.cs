using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace KingbaseConnector;

public sealed partial class KingbaseDataAdapter
{
    sealed class QueuedCommand(KingbaseBatchCommand command, KingbaseConnection? connection, KingbaseTransaction? transaction, int timeout, StatementType statementType)
    {
        internal KingbaseBatchCommand Command { get; } = command;
        internal KingbaseConnection? Connection { get; } = connection;
        internal KingbaseTransaction? Transaction { get; } = transaction;
        internal int Timeout { get; } = timeout;
        internal StatementType StatementType { get; } = statementType;
        internal Exception? Error { get; set; }
    }
    int _updateBatchSize = 1;
    int _updating;
    List<QueuedCommand>? _queued;
    RowUpdatedEventArgs? _batchEvent;
    QueuedCommand? _preparedRow;
    KingbaseCommand? _preparedSource;
    bool _stopBatchedRows;
    Exception? _batchDiagnostic;
    Exception? _deferredFailure;
    DataRow? _stoppedRow;
    string? _stoppedRowError;
    (KingbaseParameter Parameter, object? Value)[]? _stoppedValues;
    public override int UpdateBatchSize
    {
        get => _updateBatchSize;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (Volatile.Read(ref _updating) != 0) throw new InvalidOperationException("The adapter is updating rows.");
            _updateBatchSize = value;
        }
    }
    protected override int Update(DataRow[] dataRows, DataTableMapping tableMapping)
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0) throw new InvalidOperationException("The adapter is already updating rows.");
        _stopBatchedRows = false; _deferredFailure = null;
        try
        {
            var result = base.Update(dataRows, tableMapping);
            if (_deferredFailure is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
        finally { _stopBatchedRows = false; _deferredFailure = null; ClearStoppedRow(); Volatile.Write(ref _updating, 0); }
    }
    void CaptureStoppedRow(DataRow row, IDbCommand? command)
    {
        ClearStoppedRow();
        if (!_stopBatchedRows) return;
        _stoppedRow = row; _stoppedRowError = row.RowError;
        _stoppedValues = KingbaseAdapterCommand.Unwrap(command)?.Parameters.Cast<KingbaseParameter>().Select(parameter => (parameter, parameter.Value)).ToArray();
    }
    void RestoreStoppedRow(DataRow row)
    {
        if (ReferenceEquals(row, _stoppedRow))
        {
            row.RowError = _stoppedRowError!;
            if (_stoppedValues is { } values) foreach (var item in values) item.Parameter.Value = item.Value;
        }
        ClearStoppedRow();
    }
    void ClearStoppedRow() { _stoppedRow = null; _stoppedRowError = null; _stoppedValues = null; }
    protected override void InitializeBatching() { _queued = []; _batchEvent = null; _batchDiagnostic = null; _preparedRow = null; _preparedSource = null; }
    protected override void ClearBatch() { _queued!.Clear(); _batchEvent = null; _batchDiagnostic = null; _preparedRow = null; _preparedSource = null; }
    protected override void TerminateBatching() { _queued = null; _batchEvent = null; _batchDiagnostic = null; _preparedRow = null; _preparedSource = null; }
    void PrepareBatchedRow(KingbaseRowUpdatingEventArgs args)
    {
        _preparedRow = null; _preparedSource = null;
        if (_queued is null || args.Status != UpdateStatus.Continue) return;
        try
        {
            var source = args.Command ?? throw new InvalidOperationException("The adapter row has no selected command.");
            lock (source.SyncRoot)
            {
                source.EnsureEditable();
                if ((source.UpdatedRowSource & UpdateRowSource.FirstReturnedRecord) != 0)
                    throw new NotSupportedException("FirstReturnedRecord is not supported for batched adapter updates.");
                var snapshot = new KingbaseBatchCommand(source.CommandText) { CommandType = source.CommandType, RequiresPgMetadata = source.RequiresPgMetadata };
                foreach (var parameter in source.Parameters.Cast<KingbaseParameter>())
                {
                    var copy = parameter.Snapshot();
                    if (copy.Value is Stream or TextReader)
                        throw new NotSupportedException("Queued adapter updates cannot snapshot streaming parameter inputs.");
                    if (copy.Value is byte[] bytes) copy.Value = (byte[])bytes.Clone();
                    else if (copy.Value is System.Collections.BitArray bits)
                    { Protocol.PgTextCodecs.ValidateBitLength(bits); copy.Value = bits.Clone(); }
                    snapshot.Parameters.Add(copy);
                }
                _preparedRow = new QueuedCommand(snapshot, source.Connection, source.Transaction, source.CommandTimeout, args.StatementType);
                _preparedSource = source;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { args.Errors = error; args.Status = UpdateStatus.ErrorsOccurred; }
    }
    protected override int AddToBatch(IDbCommand command)
    {
        if (_preparedRow is null || !ReferenceEquals(_preparedSource, KingbaseAdapterCommand.Unwrap(command)))
            throw new InvalidOperationException("The adapter row snapshot is unavailable.");
        var index = _queued!.Count;
        _queued.Add(_preparedRow); _preparedRow = null; _preparedSource = null;
        return index;
    }
    protected override IDataParameter GetBatchedParameter(int commandIdentifier, int parameterIndex) => _queued![commandIdentifier].Command.Parameters[parameterIndex];
    protected override bool GetBatchedRecordsAffected(int commandIdentifier, out int recordsAffected, out Exception? error)
    {
        var item = _queued![commandIdentifier];
        recordsAffected = item.Command.RecordsAffected; error = item.Error;
        return item.Command.ExecutionStatus == KingbaseBatchCommandStatus.Succeeded && recordsAffected >= 0;
    }
    protected override int ExecuteBatch()
    {
        var queued = _queued!;
        if (queued.Count == 0) return 0;
        var first = queued[0]; var stopped = 0;
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
        var timer = first.Timeout == 0 ? Task.CompletedTask : KingbaseCommand.WatchDeadline((long)first.Timeout * 1000, elapsed, deadline, stop.Token);
        try
        {
            var anchor = SelectCommand ?? InsertCommand ?? UpdateCommand ?? DeleteCommand;
            if (first.Connection is null || !ReferenceEquals(anchor?.Connection, first.Connection)) throw ContextError();
            for (var i = 0; i < queued.Count; i++)
            {
                stopped = i;
                if (!ReferenceEquals(first.Connection, queued[i].Connection) || !ReferenceEquals(first.Transaction, queued[i].Transaction) || first.Timeout != queued[i].Timeout)
                    throw ContextError();
            }
            long affected = 0;
            for (var offset = 0; offset < queued.Count; offset += 256)
            {
                stopped = offset;
                CheckDeadline();
                using var batch = new KingbaseBatch(first.Connection, first.Transaction) { Timeout = 0 };
                if (first.Timeout != 0) batch.SetSharedDeadline(elapsed, (long)first.Timeout * 1000);
                var count = Math.Min(256, queued.Count - offset);
                for (var i = 0; i < count; i++) batch.BatchCommands.Add(queued[offset + i].Command);
                try { batch.ExecuteNonQueryAsync(deadline.Token).GetAwaiter().GetResult(); }
                catch { stopped = offset + (batch.FailedCommandIndex ?? 0); throw; }
                stopped = offset + count - 1; CheckDeadline();
                for (var i = 0; i < count; i++)
                {
                    stopped = offset + i;
                    var result = queued[stopped].Command;
                    if (result.RecordsAffected < 0) throw new NotSupportedException("Batched adapter commands require acknowledged affected-row counts.");
                    affected += result.RecordsAffected;
                    if (affected > int.MaxValue) throw new OverflowException("Adapter batch affected row count exceeds Int32.");
                }
            }
            CheckDeadline(); return (int)affected;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            if (error is OperationCanceledException && deadline.IsCancellationRequested)
                error = new TimeoutException("The adapter batch timed out; execution may have reached the server.");
            if (queued[stopped].Command.ExecutionStatus != KingbaseBatchCommandStatus.Succeeded || queued[stopped].Command.RecordsAffected <= 0)
                queued[stopped].Error = error;
            foreach (var item in queued)
            {
                if (item.Command.ExecutionStatus == KingbaseBatchCommandStatus.OutcomeUnknown)
                    item.Error = new InvalidOperationException("The batched command execution outcome is unknown.");
                else if (item.Command.ExecutionStatus == KingbaseBatchCommandStatus.NotExecuted && item.Error is null)
                    item.Error = new InvalidOperationException("The batched command was not executed because the batch stopped.");
                else if (item.Command.ExecutionStatus == KingbaseBatchCommandStatus.Succeeded && item.Command.RecordsAffected < 0 && item.Error is null)
                    item.Error = new NotSupportedException("The batched command has no acknowledged affected-row count.");
            }
            // Set the existing execution event before BCL captures its Errors reference, retaining per-row outcomes.
            _batchEvent!.Errors = error;
            var rowErrors = queued.Any(item => item.Error is not null || (item.Command.RecordsAffected == 0 && item.StatementType is StatementType.Update or StatementType.Delete));
            _batchEvent.Status = rowErrors ? UpdateStatus.ErrorsOccurred : UpdateStatus.Continue;
            if (!rowErrors) _batchDiagnostic = error;
            var known = queued.Where(item => item.Command.RecordsAffected >= 0).Sum(item => (long)item.Command.RecordsAffected);
            return known <= int.MaxValue ? (int)known : -1;
        }
        finally { stop.Cancel(); timer.GetAwaiter().GetResult(); }

        void CheckDeadline()
        {
            if (first.Timeout != 0 && (deadline.IsCancellationRequested || elapsed.ElapsedMilliseconds >= (long)first.Timeout * 1000))
                throw new TimeoutException("The adapter batch timed out; execution may have reached the server.");
        }
    }
    static InvalidOperationException ContextError() => new("An adapter batch requires one connection, transaction and timeout.");
}
