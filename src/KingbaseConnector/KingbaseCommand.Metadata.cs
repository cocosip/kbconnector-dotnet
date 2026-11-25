using KingbaseConnector.Protocol;

namespace KingbaseConnector;

public sealed partial class KingbaseCommand
{
    async Task<IReadOnlyList<PgTextResult>> EnrichMetadataAsync(KingbaseConnection connection, PgPhysicalSession session,
        long generation, IReadOnlyList<PgTextResult> results, string sql, CancellationToken token, Action checkMode)
    {
        try
        {
            var enriched = await PgColumnMetadata.EnrichAsync(session, results, sql, token).ConfigureAwait(false);
            long bytes = 0;
            foreach (var result in enriched)
            {
                bytes += PgColumnMetadata.RetainedBytes(result);
                foreach (var column in result.Columns) bytes += PgBinary.Utf8.GetByteCount(column.Name) + 32;
                foreach (var row in result.Rows)
                    foreach (var value in row) bytes += 4 + (value is null ? 0 : PgBinary.Utf8.GetByteCount(value));
                if (result.CommandTag is { } tag) bytes += PgBinary.Utf8.GetByteCount(tag);
                if (bytes > 1024 * 1024) throw new NotSupportedException("PG column metadata and results exceed the buffered byte limit.");
            }
            return enriched;
        }
        finally
        {
            if (session.IsUsable)
            {
                checkMode();
                connection.ObserveTransaction(this, session, generation, KingbaseTransactionOperation.None, true, session.Queries.TransactionStatus, false);
            }
        }
    }
}
