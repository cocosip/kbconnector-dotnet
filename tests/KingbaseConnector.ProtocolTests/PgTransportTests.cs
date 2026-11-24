using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KingbaseConnector.Protocol;

namespace KingbaseConnector.ProtocolTests;

public class PgTransportTests
{
    [Ipv6Fact]
    public async Task Ipv6LiteralEndpointsCanConnectWithoutAnIpv4Fallback()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptTcpClientAsync(guard.Token);
        using var transport = await PgTransport.ConnectAsync("::1", ((IPEndPoint)listener.LocalEndpoint).Port, KingbaseSslMode.Disable, guard.Token);
        using var accepted = await accepting;
        await transport.Stream.WriteAsync(new byte[] { 42 }, guard.Token);
        var value = new byte[1];
        await accepted.GetStream().ReadExactlyAsync(value, guard.Token);
        Assert.Equal(42, value[0]);
    }

    public sealed class Ipv6FactAttribute : FactAttribute
    {
        public Ipv6FactAttribute()
        {
            if (!Socket.OSSupportsIPv6) Skip = "IPv6 is unavailable on this test host.";
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitPlaintextAndEncryptedRequireModesRoundTripAndOwnTheirSocket(bool async, bool tls)
    {
        using var listener = Listen();
        using var certificate = Certificate();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(guard.Token);
            using var network = accepted.GetStream();
            Stream stream = network;
            SslStream? ssl = null;
            try
            {
                if (tls)
                {
                    await AssertSslRequest(network, guard.Token);
                    await network.WriteAsync(new byte[] { (byte)'S' }, guard.Token);
                    ssl = new SslStream(network, leaveInnerStreamOpen: true);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, guard.Token);
                    stream = ssl;
                }
                var bytes = new byte[3];
                await stream.ReadExactlyAsync(bytes, guard.Token);
                Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
                await stream.WriteAsync(new byte[] { 4, 5 }, guard.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
            }
            finally { ssl?.Dispose(); }
        }, guard.Token);
        try
        {
            using var transport = await Connect(async, "localhost", port, tls ? KingbaseSslMode.Require : KingbaseSslMode.Disable, guard.Token);
            if (tls) Assert.True(Assert.IsType<SslStream>(transport.Stream).IsEncrypted);
            else Assert.IsType<NetworkStream>(transport.Stream);
            await transport.Stream.WriteAsync(new byte[] { 1, 2, 3 }, guard.Token);
            var reply = new byte[2];
            await transport.Stream.ReadExactlyAsync(reply, guard.Token);
            Assert.Equal(new byte[] { 4, 5 }, reply);
            transport.Dispose();
            Assert.Throws<ObjectDisposedException>(() => transport.Stream);
        }
        finally { await peer.WaitAsync(guard.Token); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyFullRejectsUntrustedCertificatesBeforeApplicationData(bool async)
    {
        using var listener = Listen();
        using var certificate = Certificate();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var peer = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(guard.Token);
            using var network = accepted.GetStream();
            await AssertSslRequest(network, guard.Token);
            await network.WriteAsync(new byte[] { (byte)'S' }, guard.Token);
            using var ssl = new SslStream(network, leaveInnerStreamOpen: true);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, guard.Token);
                Assert.Equal(0, await ssl.ReadAsync(new byte[1], guard.Token));
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
        }, guard.Token);
        await Assert.ThrowsAsync<AuthenticationException>(() => Connect(async, "localhost", ((IPEndPoint)listener.LocalEndpoint).Port, KingbaseSslMode.VerifyFull, guard.Token));
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData((byte)'N', KingbaseSslMode.Require)]
    [InlineData((byte)'N', KingbaseSslMode.VerifyFull)]
    [InlineData((byte)'X', KingbaseSslMode.Require)]
    public async Task SslRejectionOrUnexpectedReplyNeverFallsBackOrSendsStartup(byte reply, KingbaseSslMode mode)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = accepted.GetStream();
            await AssertSslRequest(stream, guard.Token);
            await stream.WriteAsync(new[] { reply }, guard.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }, guard.Token);
        await Assert.ThrowsAsync<IOException>(() => PgTransport.ConnectAsync("localhost", ((IPEndPoint)listener.LocalEndpoint).Port, mode, guard.Token));
        await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileWaitingForSslReplyOrTlsHandshakeAbortsTheSocket(bool tlsStarted)
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(guard.Token);
            using var stream = accepted.GetStream();
            await AssertSslRequest(stream, guard.Token);
            if (tlsStarted) await stream.WriteAsync(new byte[] { (byte)'S' }, guard.Token);
            requestReceived.SetResult();
            var buffer = new byte[4096];
            // Drain a possible ClientHello; cancellation must ultimately close the socket.
            while (await stream.ReadAsync(buffer, guard.Token) != 0) { }
        }, guard.Token);
        var connecting = PgTransport.ConnectAsync("localhost", ((IPEndPoint)listener.LocalEndpoint).Port, KingbaseSslMode.Require, cancelled.Token);
        await requestReceived.Task.WaitAsync(guard.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(guard.Token));
        await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task PreCancellationAndInvalidArgumentsPerformNoConnectionAttempt()
    {
        using var listener = Listen();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PgTransport.ConnectAsync("localhost", port, KingbaseSslMode.Disable, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => PgTransport.ConnectAsync("private-host\0", port, KingbaseSslMode.Disable));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PgTransport.ConnectAsync("localhost", 0, KingbaseSslMode.Disable));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PgTransport.ConnectAsync("localhost", port, (KingbaseSslMode)99));
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task CancellationAfterSuccessDoesNotCloseAnEstablishedTransport()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        var accept = listener.AcceptTcpClientAsync(guard.Token);
        using var transport = await PgTransport.ConnectAsync("localhost", ((IPEndPoint)listener.LocalEndpoint).Port, KingbaseSslMode.Disable, cancelled.Token);
        using var accepted = await accept;
        cancelled.Cancel();
        await transport.Stream.WriteAsync(new byte[] { 42 }, guard.Token);
        var value = new byte[1];
        await accepted.GetStream().ReadExactlyAsync(value, guard.Token);
        Assert.Equal(42, value[0]);
    }

    [Fact]
    public async Task TruncatedSslReplyFailsAndClosesTransport()
    {
        using var listener = Listen();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(guard.Token);
            await AssertSslRequest(accepted.GetStream(), guard.Token);
        }, guard.Token);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PgTransport.ConnectAsync("localhost", ((IPEndPoint)listener.LocalEndpoint).Port, KingbaseSslMode.Require, guard.Token));
        await peer.WaitAsync(guard.Token);
    }

    static Task<PgTransport> Connect(bool async, string host, int port, KingbaseSslMode mode, CancellationToken cancellationToken) =>
        async ? PgTransport.ConnectAsync(host, port, mode, cancellationToken) : Task.Run(() => PgTransport.Connect(host, port, mode, cancellationToken), cancellationToken);

    static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    static async Task AssertSslRequest(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[8];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        Assert.Equal(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 }, bytes);
    }

    static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel on this host requires a reimported key for a TLS server credential.
        var pkcs12 = generated.Export(X509ContentType.Pfx);
        try
        {
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet);
#else
            return new X509Certificate2(pkcs12, (string?)null, X509KeyStorageFlags.UserKeySet);
#endif
        }
        finally { Array.Clear(pkcs12, 0, pkcs12.Length); }
    }
}
