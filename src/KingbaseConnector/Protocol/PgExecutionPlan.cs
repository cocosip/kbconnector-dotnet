namespace KingbaseConnector.Protocol;

internal sealed partial class PgQuerySession
{
    internal sealed class ExecutionPlan(PgQuerySession owner, PgExtendedRequest request, PgQueryExchange exchange,
        PgPreparedStatement? statement, Action? success = null) : IDisposable
    {
        PgExtendedRequest? _request = request;
        Action? _success = success;
        internal PgExtendedRequest Request => _request ?? throw new ObjectDisposedException(nameof(ExecutionPlan));
        internal PgQueryExchange Exchange { get; } = exchange;
        internal PgPreparedStatement? Statement { get; } = statement;
        internal void Succeeded() => Interlocked.Exchange(ref _success, null)?.Invoke();
        internal void Failed(PgServerError error)
        {
            _success = null;
            if (Statement is null || error.SqlState is not ("26000" or "0A000")) return;
            Statement.Invalidate();
            lock (owner._preparedStatements) owner.RemoveAutoCandidate(Statement.Sql, Statement.TypeOids, Statement.LexicalStatus);
        }
        internal void ReleaseFrames()
        {
            Interlocked.Exchange(ref _request, null)?.Dispose();
        }
        public void Dispose() { _success = null; ReleaseFrames(); }
    }

    async Task<IReadOnlyList<PgTextResult>> ExecutePlanOwnedAsync(ExecutionPlan plan, CancellationToken token)
    {
        using (plan)
        {
            try
            {
                var results = await ExchangeRequestAsync(() => plan.Request, plan.Exchange, token).ConfigureAwait(false);
                plan.Succeeded(); return results;
            }
            catch (PgQueryException error) { plan.Failed(error.ServerError); throw; }
        }
    }
}
