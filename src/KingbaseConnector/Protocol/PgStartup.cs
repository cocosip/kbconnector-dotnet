using System.Collections.ObjectModel;

namespace KingbaseConnector.Protocol;

internal sealed class PgStartupResult(IReadOnlyDictionary<string, string> parameters, PgBackendKey backendKey)
{
    internal IReadOnlyDictionary<string, string> Parameters { get; } = parameters;
    internal PgBackendKey BackendKey { get; } = backendKey;
}

internal sealed class PgStartupException(PgServerError serverError)
    : IOException($"PG startup failed (SQLSTATE {serverError.SqlState}).")
{
    internal PgServerError ServerError { get; } = serverError;
}

internal static class PgStartup
{
    const int MaxBackendFrames = 256;
    const int MaxBackendPayload = 1024 * 1024;

    internal static Task<PgStartupResult> RunAsync(PgWire wire, string username, string database, string password,
        CancellationToken cancellationToken = default, string applicationName = "", string searchPath = "", string timezone = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startup = PgFrontendMessages.Startup(username, database, applicationName, searchPath, timezone);
        return RunAsync(wire, startup, username, password, cancellationToken);
    }

    // The physical initializer can encode/validate the whole frame before creating a transport.
    internal static async Task<PgStartupResult> RunAsync(PgWire wire, byte[] startup, string username, string password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var auth = new PgPasswordAuthentication();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        PgBackendKey? key = null;
        var payloadBytes = 0;
        try
        {
            await wire.WriteMessageAsync(startup, cancellationToken).ConfigureAwait(false);
            for (var frames = 0; frames < MaxBackendFrames; frames++)
            {
                var message = await wire.ReadMessageAsync(cancellationToken, MaxBackendPayload - payloadBytes).ConfigureAwait(false);
                payloadBytes += message.Payload.Length;
                switch (message.Code)
                {
                    case (byte)'R':
                        var response = auth.Process(message, username, password, cancellationToken);
                        if (response is not null)
                        {
                            try { await wire.WriteMessageAsync(response, cancellationToken).ConfigureAwait(false); }
                            finally { Array.Clear(response, 0, response.Length); }
                        }
                        break;
                    case (byte)'S':
                        if (!auth.IsComplete) throw InvalidExchange();
                        var parameter = PgBackendMessages.ParseParameterStatus(message.Payload);
                        parameters[parameter.Key] = parameter.Value;
                        break;
                    case (byte)'K':
                        if (!auth.IsComplete || key.HasValue) throw InvalidExchange();
                        key = PgBackendMessages.ParseBackendKeyData(message.Payload);
                        break;
                    case (byte)'Z':
                        if (!auth.IsComplete || !key.HasValue ||
                            !parameters.TryGetValue("client_encoding", out var encoding) || encoding != "UTF8" ||
                            PgBackendMessages.ParseReadyForQuery(message.Payload) != PgTransactionStatus.Idle)
                            throw InvalidExchange();
                        return new PgStartupResult(new ReadOnlyDictionary<string, string>(parameters), key.Value);
                    case (byte)'N':
                        PgBackendMessages.ParseError(message.Payload);
                        break;
                    case (byte)'E':
                        throw new PgStartupException(PgBackendMessages.ParseError(message.Payload));
                    default:
                        throw InvalidExchange();
                }
            }
            throw new IOException("PG startup exceeded its message limit.");
        }
        catch
        {
            // A failed startup has no reusable synchronization point; retain the original error.
            try { wire.Dispose(); } catch { }
            throw;
        }
    }

    static IOException InvalidExchange() => new("Invalid PG startup exchange.");
}
