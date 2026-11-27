using System.Diagnostics;

namespace KingbaseConnector.Protocol;

/// <summary>Owns ordinary PG capacity through opening, reset and physical retirement.</summary>
internal sealed class PgSessionPool : IDisposable
{
    internal enum EntryState { Opening, Leased, Resetting, Idle, Validating, Retiring, Closed }
    internal sealed class Entry(long generation)
    {
        internal readonly long Generation = generation;
        internal readonly long Created = Stopwatch.GetTimestamp();
        internal long IdleSince;
        internal long CheckedAt;
        internal PgPhysicalSession? Session;
        internal EntryState State;
        internal LinkedListNode<Entry>? IdleNode;
    }
    internal sealed class Lease(PgSessionPool pool, Entry entry)
    {
        int _returned;
        internal PgSessionPool Pool => pool;
        internal PgPhysicalSession Session => Volatile.Read(ref _returned) == 0 ? entry.Session! : throw new ObjectDisposedException(nameof(Lease));
        internal Task ReturnAsync(bool canReset) => Interlocked.Exchange(ref _returned, 1) == 0 ? pool.ReturnAsync(entry, canReset) : Task.CompletedTask;
    }
    sealed class Waiter(CancellationToken token)
    {
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<Entry> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }
    readonly object _sync = new();
    readonly HashSet<Entry> _entries = new();
    readonly LinkedList<Entry> _idle = new();
    readonly LinkedList<Waiter> _waiters = new();
    readonly Queue<Entry> _retiring = new();
    readonly int _maximum, _minimum, _timeout, _idleLifetime, _lifetime, _pruningInterval, _keepAlive;
    readonly Timer _timer;
    readonly Action<PgSessionPool>? _onEmpty;
    readonly KingbasePasswordProvider? _passwordProvider;
    string _configuration;
    long _generation;
    long _lastPruned = Stopwatch.GetTimestamp();
    int _rents;
    bool _disposed;

    internal PgSessionPool(KingbaseConnectionStringBuilder settings, Action<PgSessionPool>? onEmpty = null, KingbasePasswordProvider? passwordProvider = null)
    {
        _configuration = settings.ConnectionString;
        _maximum = settings.MaxPoolSize; _minimum = settings.MinPoolSize; _timeout = settings.Timeout;
        _idleLifetime = settings.ConnectionIdleLifetime; _lifetime = settings.ConnectionLifetime; _pruningInterval = settings.ConnectionPruningInterval;
        _keepAlive = settings.KeepAlive;
        _onEmpty = onEmpty;
        _passwordProvider = passwordProvider;
        var cadence = (int)Math.Min((long)(_keepAlive > 0 && _keepAlive < _pruningInterval ? _keepAlive : _pruningInterval) * 1000, int.MaxValue);
        _timer = new Timer(static state => { if (((WeakReference<PgSessionPool>)state!).TryGetTarget(out var pool)) pool.Prune(); },
            new WeakReference<PgSessionPool>(this), cadence, cadence);
    }
    internal bool IsDisposed { get { lock (_sync) return _disposed; } }

    internal async Task<Lease> RentAsync(CancellationToken caller)
    {
        lock (_sync) { ThrowIfDisposed(); _rents++; }
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token);
        var budget = (long)_timeout * 1000;
        var timer = budget == 0 ? Task.CompletedTask : WatchDeadline(budget, elapsed, deadline, stop.Token);
        var waiting = true;
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var waiter = new Waiter(linked.Token);
                lock (_sync) { ThrowIfDisposed(); waiter.Node = _waiters.AddLast(waiter); Pump(); }
                DrainRetired();
                Entry? entry = null;
                waiting = true;
                try
                {
                    using (linked.Token.Register(() => Cancel(waiter)))
                        entry = await waiter.Completion.Task.ConfigureAwait(false);
                    waiting = false;
                    linked.Token.ThrowIfCancellationRequested();
                    string configuration;
                    lock (_sync) { ThrowIfDisposed(); configuration = _configuration; }
                    if (entry.Session is null)
                        entry.Session = await PgPhysicalSession.OpenAsync(configuration, linked.Token, prepareForPooling: true, passwordProvider: _passwordProvider).ConfigureAwait(false);
                    else
                    {
                        try { await Validate(entry.Session, linked.Token).ConfigureAwait(false); }
                        catch (IOException) when (!linked.IsCancellationRequested)
                        {
                            Retire(entry); entry = null; continue;
                        }
                    }
                    linked.Token.ThrowIfCancellationRequested();
                    if (budget != 0 && elapsed.ElapsedMilliseconds >= budget) throw new TimeoutException("The Kingbase pooled opening timed out.");
                    lock (_sync)
                    {
                        ThrowIfDisposed();
                        if (entry.Generation == _generation) { entry.State = EntryState.Leased; entry.CheckedAt = Stopwatch.GetTimestamp(); return new Lease(this, entry); }
                    }
                    Retire(entry); entry = null; // A clear raced initialization; reacquire under the original deadline.
                }
                catch { if (entry is not null) Retire(entry); throw; }
            }
        }
        catch
        {
            caller.ThrowIfCancellationRequested();
            lock (_sync) ThrowIfDisposed();
            if (budget != 0 && (deadline.IsCancellationRequested || elapsed.ElapsedMilliseconds >= budget))
                throw new TimeoutException(waiting ? "The Kingbase pool wait timed out." : "The Kingbase pooled opening timed out.");
            throw;
        }
        finally
        {
            stop.Cancel(); await timer.ConfigureAwait(false);
            lock (_sync) _rents--;
            Reclaim();
        }
    }

    static async Task Validate(PgPhysicalSession session, CancellationToken token)
    {
        if (!session.IsUsable) throw new IOException("The idle Kingbase session is no longer usable.");
        // Some Standard hosts cannot cancel pending socket/TLS reads. Abort the owned session instead.
        using var abort = token.Register(() => { try { session.Dispose(); } catch { } });
        var queries = session.Queries;
        var results = await queries.ExecuteAsync("SELECT 1", token).ConfigureAwait(false);
        abort.Dispose(); // Never let a completed validation's token close a later borrower's session.
        token.ThrowIfCancellationRequested();
        if (queries.TransactionStatus != PgTransactionStatus.Idle || results.Count != 1 || results[0].CommandTag != "SELECT 1" ||
            results[0].Columns.Count != 1 || results[0].Rows.Count != 1 || results[0].Rows[0].Count != 1 || results[0].Rows[0][0] != "1" ||
            !queries.Parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8" ||
            (queries.Parameters.TryGetValue("database_mode", out var mode) && !PgIdentityProbe.MatchesMode(mode, session.Identity.EffectiveMode)))
            throw new IOException("Invalid idle Kingbase session validation.");
        session.ValidateReportedBaseline();
    }

    async Task ReturnAsync(Entry entry, bool canReset)
    {
        lock (_sync)
        {
            if (entry.State is EntryState.Closed or EntryState.Retiring) return;
            if (!canReset || _disposed || entry.Generation != _generation || Expired(entry) || !entry.Session!.IsUsable)
                MarkRetiring(entry);
            else entry.State = EntryState.Resetting;
        }
        if (entry.State == EntryState.Resetting)
        {
            var clean = false;
            try { await entry.Session!.ResetAsync().ConfigureAwait(false); clean = true; } catch { }
            lock (_sync)
            {
                if (!clean || _disposed || entry.Generation != _generation || Expired(entry) || !entry.Session!.IsUsable) MarkRetiring(entry);
                else
                {
                    entry.State = EntryState.Idle; entry.IdleSince = Stopwatch.GetTimestamp(); entry.CheckedAt = entry.IdleSince; entry.IdleNode = _idle.AddLast(entry);
                }
                Pump();
            }
        }
        DrainRetired(); Reclaim();
    }

    void Cancel(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is null) return;
            _waiters.Remove(waiter.Node); waiter.Node = null; waiter.Completion.TrySetCanceled(); Pump();
        }
        DrainRetired(); Reclaim();
    }
    // Always called under _sync. Continuations run outside this lock.
    void Pump()
    {
        while (_waiters.First is { } node)
        {
            var waiter = node.Value;
            if (waiter.Token.IsCancellationRequested)
            { _waiters.RemoveFirst(); waiter.Node = null; waiter.Completion.TrySetCanceled(); continue; }
            Entry entry;
            if (_idle.First is { } idle)
            {
                entry = idle.Value;
                if (Expired(entry) || IdleExpired(entry) || !entry.Session!.IsUsable) { MarkRetiring(entry); continue; }
                _idle.RemoveFirst(); entry.IdleNode = null; entry.State = EntryState.Leased;
            }
            else if (_entries.Count < _maximum)
            { entry = new Entry(_generation); _entries.Add(entry); }
            else break;
            _waiters.RemoveFirst(); waiter.Node = null; waiter.Completion.TrySetResult(entry);
        }
    }
    bool Expired(Entry entry) => _lifetime != 0 && Age(entry.Created) >= _lifetime;
    bool IdleExpired(Entry entry) => _idleLifetime != 0 && Age(entry.IdleSince) >= _idleLifetime &&
        _entries.Count(e => e.State != EntryState.Retiring) > _minimum;
    static double Age(long since) => (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;
    void MarkRetiring(Entry entry)
    {
        if (entry.State is EntryState.Retiring or EntryState.Closed) return;
        if (entry.IdleNode is not null) { _idle.Remove(entry.IdleNode); entry.IdleNode = null; }
        entry.State = EntryState.Retiring; _retiring.Enqueue(entry);
    }
    void Retire(Entry entry) { lock (_sync) MarkRetiring(entry); DrainRetired(); }
    void DrainRetired()
    {
        while (true)
        {
            Entry entry;
            lock (_sync) { if (_retiring.Count == 0) break; entry = _retiring.Dequeue(); }
            try { entry.Session?.Dispose(); } catch { }
            finally { lock (_sync) { entry.State = EntryState.Closed; _entries.Remove(entry); if (!_disposed) Pump(); } }
        }
    }
    void Prune()
    {
        // The keepalive interval may be shorter than the pruning interval, so the heartbeat runs before the prune gate.
        ProbeIdle();
        lock (_sync)
        {
            if (_disposed || Age(_lastPruned) < _pruningInterval) return;
            _lastPruned = Stopwatch.GetTimestamp();
            foreach (var entry in _idle.ToArray()) if (Expired(entry) || IdleExpired(entry) || !entry.Session!.IsUsable) MarkRetiring(entry);
        }
        DrainRetired();
        Reclaim();
    }
    // Claims idle entries whose last successful probe is older than Keep Alive, then probes them one at a time.
    void ProbeIdle()
    {
        if (_keepAlive == 0) return;
        List<Entry>? claimed = null;
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var entry in _idle)
                if (Age(entry.CheckedAt) >= _keepAlive) (claimed ??= new()).Add(entry);
            if (claimed is null) return;
            foreach (var entry in claimed)
            {
                _idle.Remove(entry.IdleNode!); entry.IdleNode = null; entry.State = EntryState.Validating;
            }
        }
        _ = ProbeIdleAsync(claimed);
    }
    async Task ProbeIdleAsync(List<Entry> claimed)
    {
        // Probe concurrently: a black-holed network would otherwise block the whole pool for one timeout per session.
        var probes = new Task[claimed.Count];
        for (var index = 0; index < claimed.Count; index++) probes[index] = Probe(claimed[index]);
        await Task.WhenAll(probes).ConfigureAwait(false);
        DrainRetired(); Reclaim();

        async Task Probe(Entry entry)
        {
            var alive = false;
            try
            {
                if (entry.Session is { IsUsable: true } session)
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(_timeout > 0 ? _timeout : 30, 86_400)));
                    await Validate(session, budget.Token).ConfigureAwait(false);
                    alive = true;
                }
            }
            catch { }
            lock (_sync)
            {
                if (entry.State != EntryState.Validating) return; // Already retired by a clear, prune or lease.
                if (alive && entry.Session is { IsUsable: true } && entry.Generation == _generation && !_disposed)
                {
                    entry.CheckedAt = Stopwatch.GetTimestamp();
                    entry.State = EntryState.Idle; entry.IdleNode = _idle.AddLast(entry);
                }
                else MarkRetiring(entry);
                if (!_disposed) Pump();
            }
        }
    }
    internal void Clear()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _generation++;
            foreach (var entry in _entries.ToArray())
                if (entry.State is EntryState.Idle or EntryState.Validating) MarkRetiring(entry);
            Pump();
        }
        DrainRetired(); Reclaim();
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _configuration = ""; _generation++;
            foreach (var entry in _entries.ToArray())
                if (entry.State is EntryState.Idle or EntryState.Validating) MarkRetiring(entry);
            foreach (var waiter in _waiters) { waiter.Node = null; waiter.Completion.TrySetException(new ObjectDisposedException(nameof(KingbaseDataSource))); }
            _waiters.Clear();
        }
        _timer.Dispose(); DrainRetired();
    }
    internal bool TryDisposeEmpty()
    {
        lock (_sync)
        {
            if (_entries.Count != 0 || _waiters.Count != 0 || _rents != 0) return false;
            _disposed = true; _configuration = "";
        }
        _timer.Dispose(); return true;
    }
    void Reclaim() => _onEmpty?.Invoke(this);
    void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(KingbaseDataSource)); }
    static async Task WatchDeadline(long budget, Stopwatch elapsed, CancellationTokenSource deadline, CancellationToken stop)
    {
        try
        {
            long remaining;
            while ((remaining = budget - elapsed.ElapsedMilliseconds) > 0)
                await Task.Delay((int)Math.Min(remaining, int.MaxValue), stop).ConfigureAwait(false);
            try { deadline.Cancel(); } catch { }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
