using System.Globalization;

namespace KingbaseConnector.Protocol;

internal sealed class PgPreparedStatement(PgQuerySession owner, string name, string sql, uint[] oids,
    string? lexicalStatus, int retainedBytes)
{
    int _invalid;
    internal PgQuerySession Owner { get; } = owner;
    internal string Name { get; } = name;
    internal string Sql { get; } = sql;
    internal IReadOnlyList<uint> TypeOids { get; } = Array.AsReadOnly(oids);
    internal string? LexicalStatus { get; } = lexicalStatus;
    internal int RetainedBytes { get; } = retainedBytes;
    internal bool IsValid => Volatile.Read(ref _invalid) == 0 && !Owner.IsDisposed;
    internal void Invalidate() => Volatile.Write(ref _invalid, 1);
}

internal sealed partial class PgQuerySession
{
    const int MaxPreparedStatements = 64;
    const int MaxPreparedBytes = 1024 * 1024;
    readonly LinkedList<PgPreparedStatement> _preparedStatements = new();
    int _preparedBytes;
    ulong _statementSequence;

    internal Task<PgPreparedStatement> PrepareAsync(string sql, PgTypeCatalog catalog, IReadOnlyList<uint> typeOids,
        CancellationToken token = default) => OwnAsync(async () =>
        {
            if (typeOids is null) throw new ArgumentNullException(nameof(typeOids));
            var codecs = new PgTextCodecs(catalog);
            if (typeOids.Count > 1024) throw InvalidSignature();
            var snapshot = typeOids.Take(1025).ToArray();
            if (snapshot.Length > 1024 || snapshot.Length != typeOids.Count) throw InvalidSignature();
            foreach (var oid in snapshot) codecs.Encode(oid, null);
            PgFrontendMessages.CStringLength(sql);
            var lexical = LexicalStatus;
            lock (_preparedStatements)
            {
                var cached = _preparedStatements.FirstOrDefault(s => s.IsValid && s.Sql == sql &&
                    s.LexicalStatus == lexical && s.TypeOids.SequenceEqual(snapshot));
                if (cached is not null) { Touch(cached); return cached; }
            }
            if (_statementSequence == ulong.MaxValue) throw new InvalidOperationException("PG statement identity space is exhausted.");
            var name = "kbp_" + (++_statementSequence).ToString(CultureInfo.InvariantCulture);
            var frames = PgExtendedQuery.EncodePrepare(sql, snapshot, name);
            try
            {
                var bytes = frames[0].Length;
                PgPreparedStatement? eviction;
                while (true)
                {
                    lock (_preparedStatements)
                    {
                        eviction = _preparedStatements.FirstOrDefault(s => !s.IsValid);
                        if (eviction is null && (_preparedStatements.Count >= MaxPreparedStatements || _preparedBytes + bytes > MaxPreparedBytes))
                            eviction = _preparedStatements.Last!.Value;
                    }
                    if (eviction is null) break;
                    await CloseOwnedAsync(eviction, token).ConfigureAwait(false);
                }
                await ExchangeAsync(() => frames, PgQueryExchange.Prepare, token).ConfigureAwait(false);
                var statement = new PgPreparedStatement(this, name, sql, snapshot, lexical, bytes);
                lock (_preparedStatements)
                {
                    ThrowIfDisposed();
                    _preparedStatements.AddFirst(statement);
                    _preparedBytes += bytes;
                }
                return statement;
            }
            finally { foreach (var frame in frames) Array.Clear(frame, 0, frame.Length); }
        }, token);

    internal Task<IReadOnlyList<PgTextResult>> ExecutePreparedAsync(PgPreparedStatement statement, PgTypeCatalog catalog,
        IReadOnlyList<PgQueryParameter> parameters, CancellationToken token = default) => OwnAsync(async () =>
        {
            CheckOwner(statement);
            if (parameters is null) throw new ArgumentNullException(nameof(parameters));
            if (!statement.IsValid || statement.LexicalStatus != LexicalStatus)
                throw new InvalidOperationException("The PG prepared statement is no longer usable.");
            if (parameters.Count != statement.TypeOids.Count) throw InvalidSignature();
            var snapshot = parameters.Take(1025).ToArray();
            if (snapshot.Length != statement.TypeOids.Count || snapshot.Where((p, i) => p is null || p.TypeOid != statement.TypeOids[i]).Any())
                throw InvalidSignature();
            lock (_preparedStatements) { ThrowIfDisposed(); Touch(statement); }
            try
            {
                return await ExchangeAsync(() => PgExtendedQuery.EncodePrepared(statement.Name, catalog, snapshot),
                    PgQueryExchange.PreparedExecution, token).ConfigureAwait(false);
            }
            catch (PgQueryException error) when (error.ServerError.SqlState is "26000" or "0A000")
            {
                statement.Invalidate();
                throw;
            }
        }, token);

    internal Task UnprepareAsync(PgPreparedStatement statement, CancellationToken token = default) => OwnAsync(async () =>
        {
            CheckOwner(statement);
            await CloseOwnedAsync(statement, token).ConfigureAwait(false);
            return true;
        }, token);

    // Reset's physical owner has revoked all logical users; disposal may also call concurrently.
    internal void InvalidatePreparedStatements(bool forget = true)
    {
        lock (_preparedStatements)
        {
            foreach (var statement in _preparedStatements) statement.Invalidate();
            // A single DEALLOCATE tag omits the name; other server handles still need Close.
            if (forget) { _preparedStatements.Clear(); _preparedBytes = 0; }
        }
    }

    async Task CloseOwnedAsync(PgPreparedStatement statement, CancellationToken token)
    {
        lock (_preparedStatements) if (!_preparedStatements.Contains(statement)) return;
        try { await ExchangeAsync(() => PgExtendedQuery.EncodeClose(statement.Name), PgQueryExchange.Close, token).ConfigureAwait(false); }
        catch (PgQueryException error)
        {
            if (error.ServerError.SqlState != "26000" || CommandRejection is null)
            {
                Dispose();
                throw;
            }
        }
        lock (_preparedStatements)
        {
            statement.Invalidate();
            if (_preparedStatements.Remove(statement)) _preparedBytes -= statement.RetainedBytes;
        }
    }

    string? LexicalStatus => _parameters.TryGetValue("standard_conforming_strings", out var value) ? value : null;
    void CheckOwner(PgPreparedStatement statement)
    {
        if (statement is null) throw new ArgumentNullException(nameof(statement));
        if (!ReferenceEquals(statement.Owner, this)) throw new ArgumentException("A PG prepared statement belongs to another physical session.");
    }
    // Call while holding the list lock; the one exchange gate prevents other cache users.
    void Touch(PgPreparedStatement statement)
    {
        _preparedStatements.Remove(statement);
        _preparedStatements.AddFirst(statement);
    }
    static ArgumentException InvalidSignature() => new("Invalid PG prepared-statement parameter signature.");
}
