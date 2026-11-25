using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

public sealed partial class KingbaseCommand
{
    async Task<KingbaseDataReader?> ExecuteBatchOwnedAsync(KingbaseBatchCommand[] commands, Operation operation,
        CommandBehavior behavior, KingbaseConnection connection, PgPhysicalSession session, long generation,
        KingbaseTransaction? transaction, bool scalar, CancellationToken token, Action checkMode, Action checkCancellation)
    {
        var queries = session.Queries;
        var allResults = new List<PgTextResult>();
        var totalRows = 0; var totalColumns = 0; long totalBytes = 0; long totalAffected = 0;
        var scalarSelected = false;
        var schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
        for (var index = 0; index < commands.Length; index++)
        {
            var command = commands[index];
            _batch!.FailedCommandIndex = index;
            var writes = queries.ExecutionWriteAttempts;
            var originalStarted = false;
            var largeObjectControl = false;
            try
            {
                checkCancellation(); checkMode();
                if (command.RequiresPgMetadata && session.Identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
                    throw new NotSupportedException("PG-generated commands require PostgreSql compatibility mode.");
                if (transaction is not null && !connection.OwnsTransaction(transaction))
                    throw new InvalidOperationException("The batch transaction is no longer active on this connection.");
                if (command.CommandType != CommandType.Text) throw new NotSupportedException("Only Text batch commands are implemented.");
                if (string.IsNullOrWhiteSpace(command.CommandText)) throw new InvalidOperationException("Kingbase batch CommandText is empty.");
                queries.Parameters.TryGetValue("standard_conforming_strings", out var lexicalStatus);
                bool? conforming = lexicalStatus switch { "on" => true, "off" => false, _ => null };
                var bindings = PgParameterBindings.Create(command.CommandText, command.Parameters, session.Catalog,
                    session.Identity.EffectiveMode, conforming);
                largeObjectControl = bindings.InvalidatesLargeObjects || bindings.ChangesLargeObjectScopes;
                checkCancellation();
                if (operation == Operation.Prepare)
                {
                    command.Prepared = null;
                    var prepared = await queries.PrepareAsync(bindings.Sql, session.Catalog,
                        bindings.Parameters.Select(p => p.TypeOid).ToArray(), token, checkMode).ConfigureAwait(false);
                    connection.ObserveTransaction(this, session, generation, KingbaseTransactionOperation.None, true,
                        queries.TransactionStatus, false);
                    checkMode(); checkCancellation();
                    lock (_sync) if (!_disposed) { command.Prepared = prepared; command.PreparedGeneration = generation; }
                    continue;
                }
                var statement = command.Prepared;
                IReadOnlyList<PgTextResult>? metadataBefore = null;
                if ((behavior & CommandBehavior.KeyInfo) != 0)
                {
                    var discovery = await queries.DescribeAsync(bindings.Sql, session.Catalog, bindings.Parameters.Select(p => p.TypeOid).ToArray(), token).ConfigureAwait(false);
                    metadataBefore = await EnrichMetadataAsync(connection, session, generation, discovery, bindings.Sql, token, checkMode).ConfigureAwait(false);
                    checkCancellation();
                }
                var usePrepared = statement is not null && statement.IsValid && ReferenceEquals(statement.Owner, queries) &&
                    command.PreparedGeneration == generation && statement.Sql == bindings.Sql && statement.LexicalStatus == lexicalStatus &&
                    statement.TypeOids.SequenceEqual(bindings.Parameters.Select(p => p.TypeOid));
                IReadOnlyList<PgTextResult> results;
                originalStarted = true;
                writes = queries.ExecutionWriteAttempts;
                if (schemaOnly)
                    results = await queries.DescribeAsync(bindings.Sql, session.Catalog, bindings.Parameters.Select(p => p.TypeOid).ToArray(), token).ConfigureAwait(false);
                else if (usePrepared)
                    results = await queries.ExecutePreparedAsync(statement!, session.Catalog, bindings.Parameters, token).ConfigureAwait(false);
                else
                {
                    command.Prepared = null;
                    var automatic = await queries.ExecuteAutoPreparedAsync(bindings.Sql, session.Catalog, bindings.Parameters, token, checkMode,
                        prepared => { lock (_sync) if (!_disposed) { command.Prepared = prepared; command.PreparedGeneration = generation; } }).ConfigureAwait(false);
                    results = automatic.Results;
                }
                // Acknowledgment remains visible even if local result validation or the following deadline check fails.
                if (!schemaOnly) command.ExecutionStatus = KingbaseBatchCommandStatus.Succeeded;
                foreach (var result in results)
                    if (KingbaseDataReader.AffectedCount(result.CommandTag) is { } count)
                        command.Affected = command.Affected < 0 ? count : checked(command.Affected + count);
                connection.ObserveTransaction(this, session, generation, KingbaseTransactionOperation.None, true,
                    queries.TransactionStatus, !schemaOnly && bindings.EndsTransaction, rawRollback: !schemaOnly && bindings.InvalidatesLargeObjects,
                    rawScopeControl: !schemaOnly && bindings.ChangesLargeObjectScopes);
                checkMode();
                if ((behavior & CommandBehavior.KeyInfo) != 0)
                {
                    results = await EnrichMetadataAsync(connection, session, generation, results, bindings.Sql, token, checkMode).ConfigureAwait(false);
                    PgColumnMetadata.VerifyStable(metadataBefore!, results);
                }
                using (var validation = new KingbaseDataReader(results, session.Catalog))
                {
                    if (scalar && !scalarSelected)
                        do
                        {
                            if (validation.FieldCount == 0) continue;
                            scalarSelected = true;
                            if (validation.Read()) _ = validation.GetValue(0);
                            break;
                        } while (validation.NextResult());
                }
                if (command.Affected >= 0) totalAffected += command.Affected;
                if (totalAffected > int.MaxValue) throw new OverflowException("Kingbase batch affected row count exceeds Int32.");
                foreach (var result in results)
                {
                    totalBytes += PgColumnMetadata.RetainedBytes(result);
                    totalRows += result.Rows.Count; totalColumns += result.Columns.Count;
                    foreach (var column in result.Columns) totalBytes += PgBinary.Utf8.GetByteCount(column.Name) + 32;
                    foreach (var row in result.Rows)
                        foreach (var value in row) totalBytes += 4 + (value is null ? 0 : PgBinary.Utf8.GetByteCount(value));
                    if (result.CommandTag is { } tag) totalBytes += PgBinary.Utf8.GetByteCount(tag);
                    if (allResults.Count >= 256 || totalRows > 1024 || totalColumns > 1024 || totalBytes > 1024 * 1024)
                        throw new NotSupportedException("Kingbase batch buffered results exceed their bounded capacity.");
                    allResults.Add(result);
                }
                checkCancellation();
            }
            catch
            {
                if (largeObjectControl && queries.UncertainError is not null) session.Dispose();
                if (operation == Operation.Execute && !schemaOnly && command.ExecutionStatus == KingbaseBatchCommandStatus.NotExecuted &&
                    ((behavior & CommandBehavior.KeyInfo) == 0 || originalStarted))
                    command.ExecutionStatus = queries.ExecutionWriteAttempts != writes && (!session.IsUsable || queries.UncertainError is not null)
                        ? KingbaseBatchCommandStatus.OutcomeUnknown : KingbaseBatchCommandStatus.Failed;
                throw;
            }
        }
        if (operation == Operation.Prepare) { _batch!.FailedCommandIndex = null; connection.ThrowCallbackFailure(session, generation); return null; }
        var reader = new KingbaseDataReader(allResults.AsReadOnly(), session.Catalog, behavior,
            r => connection.ReleaseReader(r, session, generation, (behavior & CommandBehavior.CloseConnection) != 0));
        try { _batch!.FailedCommandIndex = null; connection.ThrowCallbackFailure(session, generation); connection.PublishReader(this, session, generation, reader); return reader; }
        catch { reader.Close(); throw; }
    }
}
