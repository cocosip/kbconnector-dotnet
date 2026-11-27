using System.Net.Sockets;

namespace KingbaseConnector.Protocol;

/// <summary>
/// Sends one out-of-band PG cancel request on a fresh plaintext connection.
/// The cancel packet precedes any startup or TLS work by protocol, so this path never negotiates TLS
/// and never reads a reply: the server acknowledges nothing on the cancel connection.
/// </summary>
internal static class PgCancelChannel
{
    internal const int TimeoutMilliseconds = 5000;

    internal static async Task SendAsync(string host, int port, int processId, int secretKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || host.IndexOf('\0') >= 0)
            throw new ArgumentException("A valid TCP host is required.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var client = new TcpClient { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeoutMilliseconds);
        try
        {
            using (deadline.Token.Register(static state => { try { ((TcpClient)state!).Dispose(); } catch { } }, client))
            {
#if NET8_0_OR_GREATER
                await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
#else
                await client.ConnectAsync(host, port).ConfigureAwait(false);
#endif
                var message = PgFrontendMessages.CancelRequest(processId, secretKey);
                var stream = client.GetStream();
                await stream.WriteAsync(message, 0, message.Length, deadline.Token).ConfigureAwait(false);
                // The cancellation channel closes with no reply; reading that closure is the delivery barrier.
                // The caller's own ReadyForQuery drain remains the statement-synchronization barrier,
                // and an unconfirmed cancellation is bounded by the session's drain grace.
                var closed = new byte[1];
                if (await stream.ReadAsync(closed, 0, 1, deadline.Token).ConfigureAwait(false) != 0)
                    throw new IOException("The PG cancellation channel returned unexpected data.");
            }
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }
}
