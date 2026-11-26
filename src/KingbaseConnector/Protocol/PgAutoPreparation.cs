namespace KingbaseConnector.Protocol;

internal sealed partial class PgQuerySession
{
    const int MaxAutoCandidates = 128;
    readonly int _maxAutoPrepare;
    readonly int _autoPrepareMinUsages;
    readonly LinkedList<AutoCandidate> _autoCandidates = new();
    int _autoCandidateBytes;

    sealed class AutoCandidate(string sql, uint[] oids, string? lexicalStatus, int bytes)
    {
        internal string Sql { get; } = sql;
        internal uint[] Oids { get; } = oids;
        internal string? LexicalStatus { get; } = lexicalStatus;
        internal int Bytes { get; } = bytes;
        internal int Usages { get; set; }
    }

    // The same gate covers lookup, candidate accounting, Close/Parse and execution.
    internal Task<(IReadOnlyList<PgTextResult> Results, PgPreparedStatement? Statement)> ExecuteAutoPreparedAsync(
        string sql, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters, CancellationToken token = default,
        Action? validateSession = null, Action<PgPreparedStatement>? attachStatement = null) =>
        OwnAsync(async () =>
        {
            var plan = await CreateAutoPlanOwnedAsync(sql, catalog, parameters, token, validateSession, attachStatement).ConfigureAwait(false);
            return (await ExecutePlanOwnedAsync(plan, token).ConfigureAwait(false), plan.Statement);
        }, token);

    async Task<ExecutionPlan> CreateAutoPlanOwnedAsync(string sql, PgTypeCatalog catalog, IReadOnlyList<PgQueryParameter> parameters,
        CancellationToken token, Action? validateSession, Action<PgPreparedStatement>? attachStatement)
    {
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        if (_maxAutoPrepare == 0) return new ExecutionPlan(this, PgExtendedQuery.Encode(sql, catalog, parameters), PgQueryExchange.Extended, null);
        var snapshot = parameters.Take(1025).ToArray();
        if (snapshot.Length != parameters.Count) throw InvalidSignature();
        // Validate all current inputs and the ordinary aggregate frame bound before retaining SQL.
        var frames = PgExtendedQuery.Encode(sql, catalog, snapshot);
        try
        {
            var oids = snapshot.Select(p => p.TypeOid).ToArray();
            var lexical = LexicalStatus;
            PgPreparedStatement? cached;
            AutoCandidate? candidate = null;
            lock (_preparedStatements)
            {
                ThrowIfDisposed();
                cached = _preparedStatements.FirstOrDefault(s => s.IsValid && s.Sql == sql &&
                    s.LexicalStatus == lexical && s.TypeOids.SequenceEqual(oids));
                if (cached is null)
                {
                    candidate = _autoCandidates.FirstOrDefault(c => c.Sql == sql && c.LexicalStatus == lexical && c.Oids.SequenceEqual(oids));
                    if (candidate is null)
                    {
                        var bytes = frames[0].Length;
                        while (_autoCandidates.Count >= MaxAutoCandidates || _autoCandidateBytes + bytes > MaxPreparedBytes)
                            RemoveAutoCandidate(_autoCandidates.Last!.Value);
                        candidate = new AutoCandidate(sql, oids, lexical, bytes);
                        _autoCandidateBytes += bytes;
                    }
                    else _autoCandidates.Remove(candidate);
                    _autoCandidates.AddFirst(candidate);
                }
            }
            if (cached is null && candidate!.Usages >= _autoPrepareMinUsages - 1)
            {
                if (_statementSequence == ulong.MaxValue) throw new InvalidOperationException("PG statement identity space is exhausted.");
                var name = "kbp_" + (_statementSequence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                // Named Bind may have a different frame size; reject before any Close or Parse.
                var preflight = PgExtendedQuery.EncodePrepared(name, catalog, snapshot);
                foreach (var frame in preflight) Array.Clear(frame, 0, frame.Length);
                cached = await PrepareOwnedAsync(sql, catalog, oids, true, token, validateSession).ConfigureAwait(false);
            }
            if (cached is not null)
            {
                attachStatement?.Invoke(cached);
                var plan = CreatePreparedPlanOwned(cached, catalog, snapshot);
                foreach (var frame in frames) Array.Clear(frame, 0, frame.Length);
                return plan;
            }
            return new ExecutionPlan(this, frames, PgQueryExchange.Extended, null, () =>
            {
                lock (_preparedStatements)
                    if (lexical == LexicalStatus && _autoCandidates.Contains(candidate!)) candidate!.Usages++;
            });
        }
        catch { foreach (var frame in frames) Array.Clear(frame, 0, frame.Length); throw; }
    }

    // All candidate mutations use the prepared-list lock, also held by disposal/reset invalidation.
    void RemoveAutoCandidate(string sql, IReadOnlyList<uint> oids, string? lexical)
    {
        var candidate = _autoCandidates.FirstOrDefault(c => c.Sql == sql && c.LexicalStatus == lexical && c.Oids.SequenceEqual(oids));
        if (candidate is not null) RemoveAutoCandidate(candidate);
    }
    void RemoveAutoCandidate(AutoCandidate candidate)
    {
        if (_autoCandidates.Remove(candidate)) _autoCandidateBytes -= candidate.Bytes;
    }
}
