using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace KingbaseConnector.Tests;

// Runs against the explicitly selected archive asset, including each Standard implementation.
internal static class TlsAssetProbe
{
    internal static async Task RunAsync()
    {
        using var valid = new TlsTestCertificates();
        using var wrongName = new TlsTestCertificates(host: "wrong.invalid");
        using var expired = new TlsTestCertificates(expired: true);
        using var clientOnly = new TlsTestCertificates(clientOnly: true);
        using var intermediate = new TlsTestCertificates(intermediate: true);
        using var ip = new TlsTestCertificates(host: "127.0.0.1");
        using var root = new TlsCertificateFile(valid.Root.ExportCertificatePem());
        await Probe(valid, root.Path, "localhost", true);
        await Probe(valid, "", "localhost", false);
        await Probe(wrongName, root.Path, "localhost", false); // Unrelated root must fail too.
        using var nameRoot = new TlsCertificateFile(wrongName.Root.RawData);
        await Probe(wrongName, nameRoot.Path, "localhost", false);
        using var expiredRoot = new TlsCertificateFile(expired.Root.RawData);
        await Probe(expired, expiredRoot.Path, "localhost", false);
        using var purposeRoot = new TlsCertificateFile(clientOnly.Root.RawData);
        await Probe(clientOnly, purposeRoot.Path, "localhost", false);
        using var bundle = new TlsCertificateFile(intermediate.Intermediate!.ExportCertificatePem() + "\n" + intermediate.Root.ExportCertificatePem());
        await Probe(intermediate, bundle.Path, "localhost", true);
        using var ipRoot = new TlsCertificateFile(ip.Root.RawData);
        await Probe(ip, ipRoot.Path, "127.0.0.1", true);
        await Probe(valid, root.Path, "127.0.0.1", false);
    }

    static async Task Probe(TlsTestCertificates certificates, string rootPath, string host, bool trusted)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            var request = new byte[8]; await stream.ReadExactlyAsync(request, guard.Token);
            if (!request.SequenceEqual(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 })) throw new InvalidOperationException("Expected PG SSLRequest.");
            await stream.WriteAsync(new byte[] { (byte)'S' }, guard.Token);
            using var ssl = new SslStream(stream, true);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificates.Server }, guard.Token);
                var header = new byte[4]; var read = await ssl.ReadAsync(header, guard.Token);
                if (!trusted)
                {
                    if (read != 0) throw new InvalidOperationException("Untrusted TLS received application bytes.");
                    return;
                }
                if (read == 0) throw new InvalidOperationException("Trusted TLS did not receive StartupMessage.");
                if (read < 4) await ssl.ReadExactlyAsync(header.AsMemory(read), guard.Token);
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 8 || length > 4096) throw new InvalidOperationException("Invalid StartupMessage size.");
                var body = new byte[length - 4]; await ssl.ReadExactlyAsync(body, guard.Token);
                if (!body.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 3, 0, 0 })) throw new InvalidOperationException("Invalid startup protocol.");
                // Close before AuthenticationOk: EOF proves trust succeeded, without simulating server identity.
            }
            catch (AuthenticationException) when (!trusted) { }
            catch (IOException) when (!trusted) { }
        }, guard.Token);
        var settings = new KingbaseConnectionStringBuilder
        {
            Host = host, Port = ((IPEndPoint)listener.LocalEndpoint).Port, Database = "db", Username = "user",
            Pooling = false, SslMode = KingbaseSslMode.VerifyFull, RootCertificate = rootPath
        };
        using var connection = new KingbaseConnection(settings.ConnectionString);
        Exception? failure = null;
        try { await connection.OpenAsync(guard.Token); }
        catch (Exception error) { failure = error; }
        await peer.WaitAsync(guard.Token);
        if (trusted ? failure is not EndOfStreamException : failure is not AuthenticationException)
            throw new InvalidOperationException("The selected asset failed its custom CA policy.", failure);
        if (connection.State != System.Data.ConnectionState.Closed) throw new InvalidOperationException("Failed TLS initialization left a connection open.");
    }
}
