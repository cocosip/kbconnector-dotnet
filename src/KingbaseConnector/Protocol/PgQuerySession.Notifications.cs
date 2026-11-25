using System.Diagnostics;

namespace KingbaseConnector.Protocol;

internal sealed partial class PgQuerySession
{
    internal Task<bool> WaitForNotificationAsync(int timeoutMilliseconds, CancellationToken token, Action validate) => OwnAsync(async () =>
    {
        if (TransactionStatus != PgTransactionStatus.Idle) throw new InvalidOperationException("Notification waiting requires an idle session.");
        var elapsed = Stopwatch.StartNew(); var retained = 0;
        try
        {
            validate();
            for (var frame = 0; frame < MaxFrames; frame++)
            {
                if (timeoutMilliseconds > 0 && elapsed.ElapsedMilliseconds >= timeoutMilliseconds) return false;
                var remaining = timeoutMilliseconds < 0 ? -1 : (int)Math.Max(0, timeoutMilliseconds - elapsed.ElapsedMilliseconds);
                var incoming = await _wire.ReadMessageWithTimeoutAsync(remaining, token, MaxPayload - retained).ConfigureAwait(false);
                if (incoming is not { } message) return false;
                retained += message.Payload.Length;
                switch (message.Code)
                {
                    case (byte)'A':
                        var notification = PgBackendMessages.ParseNotification(message.Payload);
                        validate(); NotificationHandler?.Invoke(notification); return true;
                    case (byte)'N': PgBackendMessages.ParseError(message.Payload); break;
                    case (byte)'S':
                        var setting = PgBackendMessages.ParseParameterStatus(message.Payload);
                        if (setting.Key == "client_encoding" && setting.Value != "UTF8") throw InvalidExchange();
                        UpdateParameter(setting); validate(); break;
                    case (byte)'E': throw new PgQueryException(PgBackendMessages.ParseError(message.Payload));
                    default: throw InvalidExchange();
                }
            }
            throw new IOException("Notification wait exceeded its bounded message limit.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !IsDisposed) { throw; }
        catch { try { Dispose(); } catch { } throw; }
    }, token);
}
