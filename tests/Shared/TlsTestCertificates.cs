using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KingbaseConnector.Tests;

internal sealed class TlsTestCertificates : IDisposable
{
    internal X509Certificate2 Root { get; }
    internal X509Certificate2 Server { get; }
    internal X509Certificate2? Intermediate { get; }

    internal TlsTestCertificates(string host = "localhost", bool expired = false, bool clientOnly = false, bool intermediate = false)
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = Request("CN=Kingbase offline root " + Guid.NewGuid().ToString("N"), rootKey, ca: true);
        Root = rootRequest.CreateSelfSigned(now.AddDays(-3), now.AddDays(3));
        X509Certificate2 issuer = Root;
        if (intermediate)
        {
            using var intermediateKey = RSA.Create(2048);
            var request = Request("CN=Kingbase offline intermediate", intermediateKey, ca: true);
            using var signed = request.Create(Root, now.AddDays(-2), now.AddDays(2), RandomNumberGenerator.GetBytes(16));
            Intermediate = signed.CopyWithPrivateKey(intermediateKey);
            issuer = Intermediate;
        }
        using var serverKey = RSA.Create(2048);
        var serverRequest = Request("CN=" + host, serverKey, ca: false);
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address)) san.AddIpAddress(address); else san.AddDnsName(host);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        using var leaf = serverRequest.Create(issuer, now.AddDays(-1), expired ? now.AddHours(-1) : now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var keyed = leaf.CopyWithPrivateKey(serverKey);
        var pfx = keyed.Export(X509ContentType.Pfx);
        try
        {
#if NET9_0_OR_GREATER
            Server = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
#else
            Server = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
#endif
        }
        finally { Array.Clear(pfx, 0, pfx.Length); }
    }

    static CertificateRequest Request(string name, RSA key, bool ca)
    {
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    public void Dispose() { Server.Dispose(); Intermediate?.Dispose(); Root.Dispose(); }
}

internal sealed class TlsCertificateFile : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kb-private-ca-" + Guid.NewGuid().ToString("N") + ".crt");
    internal TlsCertificateFile(byte[] contents) => File.WriteAllBytes(Path, contents);
    internal TlsCertificateFile(string contents) => File.WriteAllText(Path, contents);
    public void Dispose() => File.Delete(Path);
}
