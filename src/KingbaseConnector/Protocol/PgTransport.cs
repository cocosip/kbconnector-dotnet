using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
#if NET8_0_OR_GREATER
using System.Security.Cryptography.X509Certificates;
#endif

namespace KingbaseConnector.Protocol;

/// <summary>Owns one TCP stream, optionally upgraded with PG SSLRequest and platform TLS.</summary>
internal sealed class PgTransport(TcpClient client, Stream stream, PgCertificateTrust? trust = null) : IDisposable
{
    int _disposed;

    internal Stream Stream => Volatile.Read(ref _disposed) == 0 ? stream : throw new ObjectDisposedException(nameof(PgTransport));

    internal static PgTransport Connect(string host, int port, KingbaseSslMode sslMode,
        CancellationToken cancellationToken = default, string rootCertificate = "") =>
        ConnectAsync(host, port, sslMode, cancellationToken, rootCertificate).GetAwaiter().GetResult();

    internal static async Task<PgTransport> ConnectAsync(string host, int port, KingbaseSslMode sslMode,
        CancellationToken cancellationToken = default, string rootCertificate = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(host) || host.IndexOf('\0') >= 0)
            throw new ArgumentException("A valid TCP host is required.", nameof(host));
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (sslMode != KingbaseSslMode.Disable && sslMode != KingbaseSslMode.Require && sslMode != KingbaseSslMode.VerifyFull)
            throw new ArgumentOutOfRangeException(nameof(sslMode));
        if (rootCertificate.Length != 0 && sslMode != KingbaseSslMode.VerifyFull)
            throw new ArgumentException("Root Certificate requires SSL Mode=VerifyFull.");

        var client = new TcpClient { NoDelay = true };
        Stream? connectedStream = null;
        PgCertificateTrust? certificateTrust = null;
        try
        {
            if (rootCertificate.Length != 0) certificateTrust = await PgCertificateTrust.LoadAsync(rootCertificate, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using (cancellationToken.Register(() => Abort(client)))
            {
#if NET8_0_OR_GREATER
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
#else
                await client.ConnectAsync(host, port).ConfigureAwait(false);
#endif
                connectedStream = client.GetStream();
                if (sslMode != KingbaseSslMode.Disable)
                {
                    var request = PgFrontendMessages.SslRequest();
                    await connectedStream.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);
                    var reply = new byte[1];
                    if (await connectedStream.ReadAsync(reply, 0, 1, cancellationToken).ConfigureAwait(false) != 1)
                        throw new EndOfStreamException("The PG transport ended before its SSL reply.");
                    if (reply[0] != (byte)'S') throw new IOException("The PG endpoint did not accept TLS.");

                    var ssl = sslMode == KingbaseSslMode.Require
                        ? new SslStream(connectedStream, false, (_, _, _, _) => true)
                        : certificateTrust is not null ? new SslStream(connectedStream, false, certificateTrust.Validate)
                        : new SslStream(connectedStream, false);
                    connectedStream = ssl;
#if NET8_0_OR_GREATER
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = SslProtocols.None,
                        CertificateChainPolicy = certificateTrust?.CreatePolicy(),
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, cancellationToken).ConfigureAwait(false);
#else
                    await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.None, false).ConfigureAwait(false);
#endif
                    if (!ssl.IsEncrypted || !ssl.IsSigned) throw new IOException("The PG TLS stream is not protected.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new PgTransport(client, connectedStream, certificateTrust);
        }
        catch
        {
            try { connectedStream?.Dispose(); } catch { }
            Abort(client);
            certificateTrust?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    static void Abort(TcpClient client)
    {
        try { client.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { stream.Dispose(); }
        finally { try { client.Dispose(); } finally { trust?.Dispose(); } }
    }
}
