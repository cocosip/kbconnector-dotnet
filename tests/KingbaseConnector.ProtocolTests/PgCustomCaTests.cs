using System.Data;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using KingbaseConnector.Tests;
using static KingbaseConnector.ProtocolTests.PublicExecutionTests;

namespace KingbaseConnector.ProtocolTests;

public sealed class PgCustomCaTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task ConfiguredCaAllowsVerifiedPublicSyncAsyncSourceAndDirectOpen(bool sync, bool der, bool source)
    {
        using var certificates = new TlsTestCertificates();
        using var file = new TlsCertificateFile(der ? certificates.Root.RawData : System.Text.Encoding.UTF8.GetBytes(certificates.Root.ExportCertificatePem()));
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var peer = Serve(listener, certificates, true, guard.Token);
        var settings = Configuration(listener, file.Path);
        using var owner = source ? new KingbaseDataSourceBuilder(settings.ConnectionString).Build() : null;
        using var connection = owner?.CreateConnection() ?? new KingbaseConnection(settings.ConnectionString);
        if (sync) await Task.Run(connection.Open, guard.Token); else await connection.OpenAsync(guard.Token);
        Assert.Equal(ConnectionState.Open, connection.State);
        using var command = new KingbaseCommand("SELECT 42", connection);
        Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        await connection.CloseAsync(); await peer.WaitAsync(guard.Token);
    }

    [Theory]
    [InlineData("hostname")]
    [InlineData("expired")]
    [InlineData("authority")]
    [InlineData("purpose")]
    [InlineData("system-only")]
    public async Task CustomTrustNeverWaivesNameValidityPurposeOrAnchorChecks(string failure)
    {
        using var certificates = new TlsTestCertificates(host: failure == "hostname" ? "wrong.invalid" : "localhost", expired: failure == "expired", clientOnly: failure == "purpose");
        using var other = new TlsTestCertificates();
        using var file = new TlsCertificateFile((failure == "authority" ? other : certificates).Root.ExportCertificatePem());
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var peer = Serve(listener, certificates, false, guard.Token);
        var settings = Configuration(listener, failure == "system-only" ? "" : file.Path);
        using var connection = new KingbaseConnection(settings.ConnectionString);
        await Assert.ThrowsAsync<AuthenticationException>(() => connection.OpenAsync(guard.Token));
        Assert.Equal(ConnectionState.Closed, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task PemBundleSuppliesAnIntermediateButCannotTrustItWithoutItsRoot()
    {
        using var certificates = new TlsTestCertificates(intermediate: true);
        using var file = new TlsCertificateFile(certificates.Intermediate!.ExportCertificatePem() + "\n" + certificates.Root.ExportCertificatePem());
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // The server sends only the leaf; the configured bundle must provide the intermediate.
        var peer = Serve(listener, certificates, true, guard.Token);
        using var connection = new KingbaseConnection(Configuration(listener, file.Path).ConnectionString);
        await connection.OpenAsync(guard.Token);
        using var command = new KingbaseCommand("SELECT 42", connection); Assert.Equal(42, await command.ExecuteScalarAsync(guard.Token));
        await connection.CloseAsync(); await peer.WaitAsync(guard.Token);
        File.WriteAllText(file.Path, certificates.Intermediate.ExportCertificatePem());
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => connection.OpenAsync(guard.Token));
        Assert.Null(error.InnerException); Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("junk")]
    [InlineData("trailing")]
    [InlineData("leaf")]
    [InlineData("pfx")]
    [InlineData("private-key")]
    [InlineData("base64")]
    [InlineData("large")]
    [InlineData("many")]
    [InlineData("der-trailing")]
    [InlineData("utf8")]
    [InlineData("missing-end")]
    [InlineData("duplicate-constraints")]
    [InlineData("duplicate-usage")]
    [InlineData("duplicate-constraints-bundle")]
    [InlineData("duplicate-usage-bundle")]
    public async Task InvalidCaFilesFailSafelyBeforeNetworkAndDoNotBreakLazyBuild(string kind)
    {
        using var certificates = new TlsTestCertificates();
        using var file = new TlsCertificateFile(kind switch
        {
            "leaf" => certificates.Server.RawData, "pfx" => certificates.Root.Export(X509ContentType.Pfx),
            "large" => new byte[1024 * 1024 + 1],
            "der-trailing" => certificates.Root.RawData.Concat(new byte[] { 1, 2, 3 }).ToArray(), "utf8" => new byte[] { 0xff, 0xff },
            "duplicate-constraints" => DuplicateExtension(certificates.Root.RawData, "2.5.29.19"),
            "duplicate-usage" => DuplicateExtension(certificates.Root.RawData, "2.5.29.15"),
            "duplicate-constraints-bundle" => DuplicateBundle(certificates.Root, "2.5.29.19"),
            "duplicate-usage-bundle" => DuplicateBundle(certificates.Root, "2.5.29.15"),
            _ => System.Text.Encoding.UTF8.GetBytes(kind switch
            {
                "empty" => "", "trailing" => certificates.Root.ExportCertificatePem() + "private-junk",
                "private-key" => "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
                "base64" => "-----BEGIN CERTIFICATE-----\n???\n-----END CERTIFICATE-----",
                "missing-end" => "-----BEGIN CERTIFICATE-----\nAAAA",
                "many" => string.Concat(Enumerable.Repeat(certificates.Root.ExportCertificatePem() + "\n", 33)),
                _ => "private-invalid-content"
            })
        });
        if (kind == "missing") File.Delete(file.Path);
        using var listener = Listen();
        using var source = new KingbaseDataSourceBuilder(Configuration(listener, file.Path).ConnectionString).Build();
        using var connection = source.CreateConnection();
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => connection.OpenAsync());
        Assert.Null(error.InnerException); Assert.DoesNotContain("private-", error.ToString());
        Assert.Equal(ConnectionState.Closed, connection.State); Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(KingbaseSslMode.Disable)]
    [InlineData(KingbaseSslMode.Require)]
    public void RootConfigurationCannotBeSilentlyIgnoredByNonVerifyingModes(KingbaseSslMode mode)
    {
        var settings = new KingbaseConnectionStringBuilder("Host=localhost;Database=db;Username=u") { RootCertificate = "private-ca", SslMode = mode };
        Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Throws<ArgumentException>(() => new KingbaseDataSourceBuilder(settings.ConnectionString).Build());
    }

    [Fact]
    public async Task PreCancellationWinsOverInvalidFileAndOpensNoSocket()
    {
        using var listener = Listen(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var connection = new KingbaseConnection(Configuration(listener, "private-missing-file").ConnectionString);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.OpenAsync(cancelled.Token)); Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("timeout")]
    [InlineData("close")]
    [InlineData("source")]
    public async Task StalledCustomCaTlsHonorsTheOriginalOpenCancellationAndDisposal(string cause)
    {
        using var certificates = new TlsTestCertificates(); using var file = new TlsCertificateFile(certificates.Root.RawData);
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource(); var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = Configuration(listener, file.Path); settings.Host = "127.0.0.1"; settings.Timeout = 1;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build(); using var connection = source.CreateConnection();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            await stream.ReadExactlyAsync(new byte[8], guard.Token); await stream.WriteAsync(new byte[] { (byte)'S' }, guard.Token);
            arrived.SetResult();
            while (await stream.ReadAsync(new byte[4096], guard.Token) != 0) { }
        }, guard.Token);
        var opening = connection.OpenAsync(caller.Token);
        await Task.WhenAny(opening, arrived.Task).WaitAsync(guard.Token);
        if (!arrived.Task.IsCompleted) await opening; // Surface initialization failures before waiting on the peer.
        await arrived.Task.WaitAsync(guard.Token);
        if (cause == "caller") caller.Cancel(); else if (cause == "close") await connection.CloseAsync(); else if (cause == "source") source.Dispose();
        var error = await Record.ExceptionAsync(() => opening.WaitAsync(guard.Token));
        if (cause == "timeout") Assert.IsType<TimeoutException>(error);
        else if (cause == "source") Assert.IsType<ObjectDisposedException>(error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(ConnectionState.Closed, connection.State); await peer.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task PooledReuseKeepsItsTlsSessionAndClearReloadsTheCaFileWithCapacityRecovery()
    {
        using var certificates = new TlsTestCertificates(); using var file = new TlsCertificateFile(certificates.Root.ExportCertificatePem());
        using var listener = Listen(); using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var settings = PoolExecutionTests.Configuration(listener); settings.Host = "localhost"; settings.SslMode = KingbaseSslMode.VerifyFull; settings.RootCertificate = file.Path;
        using var source = new KingbaseDataSourceBuilder(settings.ConnectionString).Build();
        async Task Peer(int borrowers)
        {
            using var socket = await listener.AcceptTcpClientAsync(guard.Token); using var stream = socket.GetStream();
            using var ssl = await Upgrade(stream, certificates, guard.Token);
            await PoolExecutionTests.InitializePool(ssl, guard.Token);
            for (var i = 0; i < borrowers; i++)
            {
                if (i != 0) await PoolExecutionTests.Ping(ssl, guard.Token);
                await PoolExecutionTests.Query(ssl, "SELECT 7", i == 0 ? uint.MaxValue : 12002, guard.Token);
                await PoolExecutionTests.Reset(ssl, guard.Token);
            }
            Assert.Equal(0, await ssl.ReadAsync(new byte[1], guard.Token));
        }
        var firstPeer = Peer(2);
        async Task Borrow()
        {
            using var connection = await source.OpenConnectionAsync(guard.Token);
            using var command = new KingbaseCommand("SELECT 7", connection); Assert.Equal(7, await command.ExecuteScalarAsync(guard.Token));
            await connection.CloseAsync();
        }
        await Borrow(); File.WriteAllText(file.Path, "private-invalid-root"); await Borrow();
        source.ClearPool(); await firstPeer.WaitAsync(guard.Token);
        await Assert.ThrowsAsync<AuthenticationException>(() => source.OpenConnectionAsync(guard.Token).AsTask()); Assert.False(listener.Pending());
        File.WriteAllText(file.Path, certificates.Root.ExportCertificatePem());
        var nextPeer = Peer(1); await Borrow(); source.Dispose(); await nextPeer.WaitAsync(guard.Token);
    }

    internal static KingbaseConnectionStringBuilder Configuration(TcpListener listener, string path) => new()
    {
        Host = "localhost", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Database = "db", Username = "user",
        SslMode = KingbaseSslMode.VerifyFull, RootCertificate = path, Pooling = false
    };

    static async Task Serve(TcpListener listener, TlsTestCertificates certificates, bool success, CancellationToken token)
    {
        using var socket = await listener.AcceptTcpClientAsync(token); using var stream = socket.GetStream();
        try
        {
            using var ssl = await Upgrade(stream, certificates, token);
            if (!success) { Assert.Equal(0, await ssl.ReadAsync(new byte[1], token)); return; }
            await Initialize(ssl, token);
            await ReadExtended(ssl, token); await ssl.WriteAsync(Value("42"), token);
            Assert.Equal(0, await ssl.ReadAsync(new byte[1], token));
        }
        catch (AuthenticationException) when (!success) { }
        catch (IOException) when (!success) { }
    }

    static async Task<SslStream> Upgrade(Stream stream, TlsTestCertificates certificates, CancellationToken token)
    {
        var request = new byte[8]; await stream.ReadExactlyAsync(request, token);
        Assert.Equal(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 }, request);
        await stream.WriteAsync(new byte[] { (byte)'S' }, token);
        var ssl = new SslStream(stream, true);
        try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificates.Server }, token); return ssl; }
        catch { ssl.Dispose(); throw; }
    }

    static byte[] DuplicateExtension(byte[] certificate, string oid)
    {
        // Preserve the encoded certificate fields, duplicating only the selected extension.
        var outer = new AsnReader(certificate, AsnEncodingRules.DER).ReadSequence();
        var body = new AsnReader(outer.ReadEncodedValue(), AsnEncodingRules.DER).ReadSequence();
        var writer = new AsnWriter(AsnEncodingRules.DER); writer.PushSequence(); writer.PushSequence();
        var tag = new Asn1Tag(TagClass.ContextSpecific, 3, true);
        while (body.HasData)
        {
            if (!body.PeekTag().HasSameClassAndValue(tag)) { writer.WriteEncodedValue(body.ReadEncodedValue().Span); continue; }
            var extensions = body.ReadSequence(tag).ReadSequence(); byte[]? duplicate = null;
            writer.PushSequence(tag); writer.PushSequence();
            while (extensions.HasData)
            {
                var encoded = extensions.ReadEncodedValue(); writer.WriteEncodedValue(encoded.Span);
                if (new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence().ReadObjectIdentifier() == oid) duplicate = encoded.ToArray();
            }
            Assert.NotNull(duplicate); writer.WriteEncodedValue(duplicate);
            writer.PopSequence(); writer.PopSequence(tag);
        }
        writer.PopSequence();
        while (outer.HasData) writer.WriteEncodedValue(outer.ReadEncodedValue().Span);
        writer.PopSequence(); return writer.Encode();
    }

    static byte[] DuplicateBundle(X509Certificate2 certificate, string oid) => System.Text.Encoding.UTF8.GetBytes(
        certificate.ExportCertificatePem() + "\n-----BEGIN CERTIFICATE-----\n" +
        Convert.ToBase64String(DuplicateExtension(certificate.RawData, oid)) + "\n-----END CERTIFICATE-----");
}
