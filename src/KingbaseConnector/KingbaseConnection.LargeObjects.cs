using System.Diagnostics;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

public sealed partial class KingbaseConnection
{
    readonly List<KingbaseLargeObjectHandle> _largeObjects = new();
    readonly List<(string Name, long Sequence)> _largeObjectSavepoints = new();
    long _largeObjectSequence;

    internal bool IsLargeObjectValid(KingbaseLargeObjectHandle handle)
    {
        lock (_sync) return handle.Valid && ReferenceEquals(_transaction, handle.Transaction) && ReferenceEquals(_session, handle.Session) &&
            _generation == handle.Generation && handle.Session.IsUsable && handle.Session.Queries.TransactionStatus == PgTransactionStatus.InTransaction;
    }
    internal void ForgetLargeObject(KingbaseLargeObjectHandle handle)
    { lock (_sync) { handle.Valid = false; _largeObjects.Remove(handle); } }
    internal bool PrepareLargeObjectClose(KingbaseLargeObjectHandle handle)
    {
        lock (_sync)
        {
            if (IsLargeObjectValid(handle)) return true;
            if (handle.Valid && ReferenceEquals(_transaction, handle.Transaction) && ReferenceEquals(_session, handle.Session) &&
                _generation == handle.Generation && handle.Session.IsUsable && handle.Session.Queries.TransactionStatus == PgTransactionStatus.FailedTransaction)
                handle.PendingClose = true;
            else ForgetLargeObject(handle);
            return false;
        }
    }
    void InvalidateLargeObjects()
    {
        foreach (var handle in _largeObjects) handle.Valid = false;
        _largeObjects.Clear(); _largeObjectSavepoints.Clear();
    }
    void ObserveLargeObjectSavepoint(KingbaseTransactionOperation operation, string? name, bool rawRollback, bool rawScopeControl)
    {
        if (rawScopeControl && operation == KingbaseTransactionOperation.None) _largeObjectSavepoints.Clear();
        if (rawRollback && operation != KingbaseTransactionOperation.RollbackTo) { InvalidateLargeObjects(); return; }
        if (operation == KingbaseTransactionOperation.Save && name is not null)
        { _largeObjectSavepoints.Add((name, _largeObjectSequence)); return; }
        if (operation is not (KingbaseTransactionOperation.RollbackTo or KingbaseTransactionOperation.Release)) return;
        var index = _largeObjectSavepoints.FindLastIndex(mark => mark.Name == name);
        if (index < 0)
        {
            if (operation == KingbaseTransactionOperation.Release) _largeObjectSavepoints.Clear(); else InvalidateLargeObjects();
            return;
        }
        if (operation == KingbaseTransactionOperation.RollbackTo)
        {
            var sequence = _largeObjectSavepoints[index].Sequence;
            foreach (var handle in _largeObjects.Where(handle => handle.Sequence > sequence)) handle.Valid = false;
            _largeObjects.RemoveAll(handle => !handle.Valid); index++;
        }
        _largeObjectSavepoints.RemoveRange(index, _largeObjectSavepoints.Count - index);
    }

    internal async Task<T> LargeObjectOperationAsync<T>(KingbaseLargeObjectHandle? expected, Func<LargeObjectOperation, Task<T>> action,
        CancellationToken caller, bool cleanup = false)
    {
        caller.ThrowIfCancellationRequested();
        if (IsInsideCallback) throw new InvalidOperationException("A large-object callback cannot reenter its connection.");
        using var deadline = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token); using var marker = new KingbaseCommand();
        var elapsed = Stopwatch.StartNew();
        LargeObjectOperation operation; int timeout;
        lock (_sync)
        {
            var physical = OpenSession();
            if (_command is not null || _reader is not null) throw ActiveOperation();
            if (_transaction is null || physical.Queries.TransactionStatus != PgTransactionStatus.InTransaction)
                throw new InvalidOperationException("OID large objects require a healthy explicit local transaction.");
            if (expected is not null && !IsLargeObjectValid(expected)) throw new InvalidOperationException("The large-object handle no longer owns its transaction or descriptor.");
            var (session, generation) = BeginExecution(marker, cancel, _transaction);
            operation = new LargeObjectOperation(this, marker, session, _transaction, generation, expected, cancel.Token);
            timeout = cleanup ? _settings.CleanupTimeout : _settings.CommandTimeout;
        }
        var timer = timeout == 0 ? Task.CompletedTask : KingbaseCommand.WatchDeadline((long)timeout * 1000, elapsed, deadline, stop.Token);
        using var abort = cancel.Token.Register(() => { if (operation.IoStarted) { try { AbortExecution(marker, operation.Session, operation.Generation); } catch { } } });
        Exception? failure = null;
        try
        {
            await operation.CloseDeferredAsync().ConfigureAwait(false);
            var result = await action(operation).ConfigureAwait(false);
            abort.Dispose(); stop.Cancel(); await timer.ConfigureAwait(false);
            lock (_sync)
            {
                operation.Validate(); caller.ThrowIfCancellationRequested(); cancel.Token.ThrowIfCancellationRequested();
                if (timeout != 0 && elapsed.ElapsedMilliseconds >= (long)timeout * 1000) throw new TimeoutException("The large-object operation deadline elapsed; acknowledged effects must not be replayed.");
                ThrowCallbackFailure(operation.Session, operation.Generation); operation.Publish();
            }
            return result;
        }
        catch (Exception error)
        {
            failure = error;
            abort.Dispose(); stop.Cancel(); await timer.ConfigureAwait(false);
            if (error is PgQueryException && operation.Session.IsUsable && operation.Session.Queries.UncertainError is not null)
            { try { AbortExecution(marker, operation.Session, operation.Generation); } catch { } }
            await operation.CleanupAsync().ConfigureAwait(false);
            if (operation.Session.IsUsable)
            {
                try { ObserveTransaction(marker, operation.Session, operation.Generation, KingbaseTransactionOperation.None, false, operation.Session.Queries.TransactionStatus, false); }
                catch { try { AbortExecution(marker, operation.Session, operation.Generation); } catch { } }
            }
            if (error is PgQueryException query) throw new KingbaseException(query.ServerError);
            caller.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("The large-object operation timed out; do not automatically replay it.");
            if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel.Token);
            throw;
        }
        finally
        {
            abort.Dispose(); stop.Cancel(); await timer.ConfigureAwait(false);
            EndExecution(marker, operation.Session, operation.Generation);
            try { RetireIfBroken(operation.Session); } catch when (failure is not null) { }
        }
    }

    internal sealed class LargeObjectOperation(KingbaseConnection connection, KingbaseCommand marker, PgPhysicalSession session,
        KingbaseTransaction transaction, long generation, KingbaseLargeObjectHandle? expected, CancellationToken token)
    {
        readonly List<int> _abandonedDescriptors = new();
        readonly List<uint> _abandonedObjects = new();
        bool _expectedClosed;
        int _ioStarted;
        internal bool IoStarted => Volatile.Read(ref _ioStarted) != 0;
        internal PgPhysicalSession Session => session;
        internal long Generation => generation;
        internal void Validate()
        {
            lock (connection._sync)
            {
                if (!ReferenceEquals(connection._session, session) || connection._generation != generation || !ReferenceEquals(connection._command, marker) ||
                    !ReferenceEquals(connection._transaction, transaction) || (expected is not null && !expected.Valid && !_expectedClosed))
                    throw new InvalidOperationException("The large-object operation no longer owns its connection or handle.");
                var queries = session.Queries;
                if (!queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8" || queries.TransactionStatus != PgTransactionStatus.InTransaction ||
                    (queries.Parameters.TryGetValue("database_mode", out var mode) && PgIdentityProbe.ParseMode(mode) != session.Identity.EffectiveMode))
                { session.Dispose(); throw new IOException("The large-object session identity or transaction state changed."); }
            }
        }
        internal async Task<T> ScalarAsync<T>(string sql, params object[] values) => await ScalarCoreAsync<T>(sql, token, values).ConfigureAwait(false);
        async Task<T> ScalarCoreAsync<T>(string sql, CancellationToken cancellationToken, params object[] values)
        {
            Validate(); cancellationToken.ThrowIfCancellationRequested();
            var parameters = new KingbaseParameterCollection();
            for (var i = 0; i < values.Length; i++) parameters.AddWithValue("p" + i, values[i]);
            session.Queries.Parameters.TryGetValue("standard_conforming_strings", out var lexical);
            var bindings = PgParameterBindings.Create(sql, parameters, session.Catalog, session.Identity.EffectiveMode, lexical switch { "on" => true, "off" => false, _ => null });
            cancellationToken.ThrowIfCancellationRequested(); Volatile.Write(ref _ioStarted, 1);
            var results = await session.Queries.ExecuteExtendedAsync(bindings.Sql, session.Catalog, bindings.Parameters, cancellationToken).ConfigureAwait(false);
            Validate();
            if (results.Count != 1 || results[0].Columns.Count != 1 || results[0].Rows.Count != 1) throw InvalidResult();
            try { using var reader = new KingbaseDataReader(results, session.Catalog); if (reader.Read() && reader.GetValue(0) is T value) return value; }
            catch { throw InvalidResult(); }
            throw InvalidResult();
        }
        internal IOException InvalidResult() { session.Dispose(); return new IOException("Invalid large-object function result."); }
        internal void AbandonObjectOnFailure(uint oid) => _abandonedObjects.Add(oid);
        internal void AbandonDescriptorOnFailure(int descriptor) => _abandonedDescriptors.Add(descriptor);
        internal void ClosedExpected()
        {
            if (expected is null) throw new InvalidOperationException("There is no large-object descriptor to close.");
            connection.ForgetLargeObject(expected); _expectedClosed = true;
        }
        internal KingbaseLargeObjectHandle Register(uint oid, int descriptor, FileAccess access)
        {
            lock (connection._sync)
            {
                Validate();
                foreach (var previous in connection._largeObjects.Where(handle => handle.Descriptor == descriptor)) previous.Valid = false;
                connection._largeObjects.RemoveAll(handle => !handle.Valid);
                var handle = new KingbaseLargeObjectHandle(oid, descriptor, access, transaction, session, generation, ++connection._largeObjectSequence);
                connection._largeObjects.Add(handle); return handle;
            }
        }
        internal void Unlinked(uint oid)
        {
            lock (connection._sync)
            {
                Validate();
                var handles = connection._largeObjects.Where(handle => handle.Oid == oid && handle.Valid).ToArray();
                foreach (var handle in handles) { handle.Valid = false; connection._largeObjects.Remove(handle); }
            }
        }
        internal void Publish() { _abandonedDescriptors.Clear(); _abandonedObjects.Clear(); }
        internal async Task CloseDeferredAsync()
        {
            KingbaseLargeObjectHandle[] pending;
            Action<KingbaseNotice>? notice;
            Action<KingbaseNotificationEventArgs>? notification;
            lock (connection._sync)
            {
                Validate(); pending = connection._largeObjects.Where(handle => handle.Valid && handle.PendingClose).ToArray();
                if (pending.Length == 0) return;
                notice = session.Queries.NoticeHandler; notification = session.Queries.NotificationHandler;
                session.Queries.NoticeHandler = null; session.Queries.NotificationHandler = null;
            }
            using var deadline = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            int timeout; lock (connection._sync) timeout = connection._settings.CleanupTimeout;
            var timer = KingbaseCommand.WatchDeadline((long)timeout * 1000, Stopwatch.StartNew(), deadline, stop.Token);
            using var abort = cancel.Token.Register(() => { if (IoStarted) { try { connection.AbortExecution(marker, session, generation); } catch { } } });
            try
            {
                foreach (var handle in pending)
                {
                    try
                    {
                        if (await ScalarCoreAsync<int>("SELECT pg_catalog.lo_close(:p0)::pg_catalog.int4", cancel.Token, handle.Descriptor).ConfigureAwait(false) != 0) throw InvalidResult();
                        connection.ForgetLargeObject(handle);
                    }
                    catch (PgQueryException error) when (error.ServerError.SqlState == "42704") { connection.ForgetLargeObject(handle); throw; }
                }
            }
            catch (Exception) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            { throw new TimeoutException("The deferred large-object close exceeded Cleanup Timeout; the physical session was retired."); }
            finally
            {
                abort.Dispose(); stop.Cancel(); await timer.ConfigureAwait(false);
                lock (connection._sync)
                    if (ReferenceEquals(connection._session, session) && connection._generation == generation && ReferenceEquals(connection._command, marker) && session.IsUsable)
                    { session.Queries.NoticeHandler = notice; session.Queries.NotificationHandler = notification; }
            }
        }
        internal async Task CleanupAsync()
        {
            if (_abandonedDescriptors.Count == 0 && _abandonedObjects.Count == 0) return;
            lock (connection._sync)
            {
                if (!ReferenceEquals(connection._session, session) || connection._generation != generation || !ReferenceEquals(connection._command, marker) ||
                    !ReferenceEquals(connection._transaction, transaction) || !session.IsUsable || session.Queries.TransactionStatus != PgTransactionStatus.InTransaction) return;
                session.Queries.NoticeHandler = null; session.Queries.NotificationHandler = null;
                foreach (var handle in connection._largeObjects.Where(handle => _abandonedDescriptors.Contains(handle.Descriptor))) handle.Valid = false;
                connection._largeObjects.RemoveAll(handle => !handle.Valid);
            }
            using var cleanup = new CancellationTokenSource(); using var stop = new CancellationTokenSource();
            int timeout; lock (connection._sync) timeout = connection._settings.CleanupTimeout;
            var timer = KingbaseCommand.WatchDeadline((long)timeout * 1000, Stopwatch.StartNew(), cleanup, stop.Token);
            using var abort = cleanup.Token.Register(() => { try { connection.AbortExecution(marker, session, generation); } catch { } });
            try
            {
                foreach (var descriptor in _abandonedDescriptors)
                    if (await ScalarCoreAsync<int>("SELECT pg_catalog.lo_close(:p0)::pg_catalog.int4", cleanup.Token, descriptor).ConfigureAwait(false) != 0) throw InvalidResult();
                foreach (var oid in _abandonedObjects)
                    if (await ScalarCoreAsync<int>("SELECT pg_catalog.lo_unlink(:p0)::pg_catalog.int4", cleanup.Token, oid).ConfigureAwait(false) != 1) throw InvalidResult();
            }
            catch { try { connection.AbortExecution(marker, session, generation); } catch { } }
            finally { stop.Cancel(); await timer.ConfigureAwait(false); }
        }
    }
}

internal sealed class KingbaseLargeObjectHandle(uint oid, int descriptor, FileAccess access, KingbaseTransaction transaction,
    PgPhysicalSession session, long generation, long sequence)
{
    internal uint Oid { get; } = oid;
    internal int Descriptor { get; } = descriptor;
    internal FileAccess Access { get; } = access;
    internal KingbaseTransaction Transaction { get; } = transaction;
    internal PgPhysicalSession Session { get; } = session;
    internal long Generation { get; } = generation;
    internal long Sequence { get; } = sequence;
    internal bool Valid { get; set; } = true;
    internal bool PendingClose { get; set; }
}
