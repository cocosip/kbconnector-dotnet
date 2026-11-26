using System.Data;
using KingbaseConnector.Protocol;

namespace KingbaseConnector;

public sealed partial class KingbaseCommand
{
    async Task<KingbaseDataReader> ExecuteStreamingCoreAsync(CommandBehavior behavior, CancellationToken caller)
    {
        if (_batch is null) caller.ThrowIfCancellationRequested();
        KingbaseConnection connection; KingbaseTransaction? transaction; string text; int timeout;
        CancellationTokenSource cancel; PgPreparedStatement? prepared; long preparedGeneration;
        KingbaseBatchCommand[]? batchCommands;
        lock (_sync)
        {
            EnsureEditable();
            batchCommands = _batch?.BatchCommands.Snapshot();
            if (batchCommands is not null)
            {
                _batch!.FailedCommandIndex = null;
                foreach (var item in batchCommands) { item.ExecutionStatus = KingbaseBatchCommandStatus.NotExecuted; item.Affected = -1; }
            }
            caller.ThrowIfCancellationRequested();
            const CommandBehavior known = CommandBehavior.SingleResult | CommandBehavior.KeyInfo | CommandBehavior.SingleRow |
                CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection;
            if ((behavior & ~known) != 0) throw new ArgumentOutOfRangeException(nameof(behavior));
            if ((behavior & CommandBehavior.KeyInfo) != 0)
                throw new NotSupportedException("Streaming KeyInfo requires an additional execution contract.");
            if (batchCommands is null)
            {
                if (_type != CommandType.Text) throw new NotSupportedException("Only Text commands are implemented.");
                if (string.IsNullOrWhiteSpace(_text)) throw new InvalidOperationException("Kingbase CommandText is empty.");
            }
            else if (batchCommands.Length == 0) throw new InvalidOperationException("The Kingbase batch has no commands.");
            connection = _connection ?? throw new InvalidOperationException("The Kingbase command has no connection.");
            transaction = _transaction; text = _text; timeout = _timeout ?? connection.DefaultCommandTimeout;
            prepared = _prepared; preparedGeneration = _preparedGeneration;
            cancel = new CancellationTokenSource(); _cancel = cancel; _executing = true;
        }
        var execution = new KingbaseReaderExecution(this, connection, cancel, caller, timeout, behavior);
        try
        {
            var (session, generation) = connection.BeginExecution(this, cancel, transaction);
            execution.Attach(session, generation);
            if (batchCommands is not null)
                await execution.StartBatchAsync(_batch!, batchCommands, transaction).ConfigureAwait(false);
            else
            {
            if (RequiresPgMetadata && session.Identity.EffectiveMode != KingbaseCompatibilityMode.PostgreSql)
                throw new NotSupportedException("PG-generated commands require PostgreSql compatibility mode.");
            var queries = session.Queries;
            bool? conforming = queries.Parameters.TryGetValue("standard_conforming_strings", out var status)
                ? status switch { "on" => true, "off" => false, _ => null } : null;
            var bindings = PgParameterBindings.Create(text, _parameters, session.Catalog, session.Identity.EffectiveMode, conforming);
            var usePrepared = prepared is not null && prepared.IsValid && ReferenceEquals(prepared.Owner, queries) &&
                preparedGeneration == generation && prepared.Sql == bindings.Sql && prepared.LexicalStatus == status &&
                prepared.TypeOids.SequenceEqual(bindings.Parameters.Select(p => p.TypeOid));
            if (!usePrepared) lock (_sync) _prepared = null;
            await execution.StartAsync(bindings, usePrepared ? prepared : null, statement =>
            {
                lock (_sync) if (!_disposed) { _prepared = statement; _preparedGeneration = generation; }
            }).ConfigureAwait(false);
            }
            var reader = new KingbaseDataReader(execution, session.Catalog, behavior,
                r => connection.ReleaseReader(r, session, generation, (behavior & CommandBehavior.CloseConnection) != 0));
            execution.Publish(reader);
            lock (_sync) if (ReferenceEquals(_cancel, cancel)) _streamingPublished = true;
            return reader;
        }
        catch (Exception error) { throw execution.FailStart(error); }
    }

    internal void ReleaseStreamingExecution(CancellationTokenSource cancel)
    {
        lock (_sync)
            if (ReferenceEquals(_cancel, cancel)) { _cancel = null; _executing = false; _streamingPublished = false; }
    }
}
