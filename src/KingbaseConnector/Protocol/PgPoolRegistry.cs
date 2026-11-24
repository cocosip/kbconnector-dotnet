namespace KingbaseConnector.Protocol;

internal static class PgPoolRegistry
{
    static readonly object Sync = new();
    static readonly Dictionary<string, PgSessionPool> Direct = new(StringComparer.Ordinal);
    static readonly List<WeakReference<PgSessionPool>> All = new();
    internal static PgSessionPool CreatePrivate(KingbaseConnectionStringBuilder settings, KingbasePasswordProvider? passwordProvider = null)
    {
        var pool = new PgSessionPool(settings, passwordProvider: passwordProvider);
        lock (Sync) Register(pool);
        return pool;
    }
    static void Register(PgSessionPool pool)
    {
        All.RemoveAll(reference => !reference.TryGetTarget(out var existing) || existing.IsDisposed);
        All.Add(new WeakReference<PgSessionPool>(pool));
    }
    internal static Task<PgSessionPool.Lease> RentAsync(KingbaseConnectionStringBuilder settings, CancellationToken token)
    {
        lock (Sync)
        {
            var key = settings.GetPoolKey();
            if (!Direct.TryGetValue(key, out var pool))
            { pool = new PgSessionPool(settings, Reclaim); Direct.Add(key, pool); Register(pool); }
            // Rent pins the pool synchronously before its first await, so reclamation cannot race this lookup.
            return pool.RentAsync(token);
        }
    }
    static void Reclaim(PgSessionPool pool)
    {
        lock (Sync)
        {
            var entry = Direct.FirstOrDefault(pair => ReferenceEquals(pair.Value, pool));
            if (entry.Key is not null && pool.TryDisposeEmpty()) Direct.Remove(entry.Key);
        }
    }
    internal static void Clear(KingbaseConnectionStringBuilder settings)
    {
        PgSessionPool? pool;
        lock (Sync) Direct.TryGetValue(settings.GetPoolKey(), out pool);
        pool?.Clear();
    }
    internal static void ClearAll()
    {
        PgSessionPool[] pools;
        lock (Sync)
        {
            All.RemoveAll(reference => !reference.TryGetTarget(out var pool) || pool.IsDisposed);
            pools = All.Select(reference => reference.TryGetTarget(out var pool) ? pool : null).Where(pool => pool is not null).Cast<PgSessionPool>().ToArray();
        }
        foreach (var pool in pools) pool.Clear();
    }
}
