using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KingbaseConnector.Protocol;

/// <summary>Owns the explicitly configured CA certificates for one physical TLS session.</summary>
internal sealed class PgCertificateTrust : IDisposable
{
    const int MaxFileSize = 1024 * 1024;
    readonly List<X509Certificate2> _certificates = [];
    readonly List<X509Certificate2> _roots = [];

    internal static async Task<PgCertificateTrust> LoadAsync(string path, CancellationToken token)
    {
        var trust = new PgCertificateTrust();
        try
        {
            token.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length <= 0 || file.Length > MaxFileSize) throw new FormatException();
            var data = new byte[(int)file.Length + 1];
            var count = 0;
            while (count < data.Length)
            {
                var read = await file.ReadAsync(data, count, data.Length - count, token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            token.ThrowIfCancellationRequested();
            // A growing file is not a stable trust snapshot; retry with a later physical creation.
            if (count == data.Length || count == 0) throw new FormatException();
            Array.Resize(ref data, count);
            if (data[0] == 0x30) trust.Add(data);
            else
            {
                var text = new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF');
                const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
                var position = 0;
                while (position < text.Length)
                {
                    while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                    if (position == text.Length) break;
                    if (text.IndexOf(begin, position, StringComparison.Ordinal) != position) throw new FormatException();
                    position += begin.Length;
                    var finish = text.IndexOf(end, position, StringComparison.Ordinal);
                    if (finish < 0) throw new FormatException();
                    trust.Add(Convert.FromBase64String(text.Substring(position, finish - position)));
                    position = finish + end.Length;
                    token.ThrowIfCancellationRequested();
                }
            }
            if (trust._roots.Count == 0) throw new FormatException();
            token.ThrowIfCancellationRequested();
            return trust;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { trust.Dispose(); throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException or CryptographicException or FormatException)
        {
            trust.Dispose();
            token.ThrowIfCancellationRequested();
            throw new AuthenticationException("The configured root certificate file is invalid or unavailable.");
        }
        catch { trust.Dispose(); throw; }
    }

    void Add(byte[] der)
    {
        if (_certificates.Count >= 32 || X509Certificate2.GetCertContentType(der) != X509ContentType.Cert)
            throw new FormatException();
        var certificate = LoadCertificate(der);
        try
        {
            if (!certificate.RawData.SequenceEqual(der)) throw new FormatException();
            var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().ToArray();
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().ToArray();
            if (constraints.Length != 1 || !constraints[0].CertificateAuthority || usage.Length > 1 ||
                (usage.Length == 1 && (usage[0].KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0)) throw new FormatException();
            if (certificate.SubjectName.RawData.SequenceEqual(certificate.IssuerName.RawData)) _roots.Add(certificate);
            _certificates.Add(certificate);
        }
        catch { certificate.Dispose(); throw; }
    }

    internal bool Validate(object sender, X509Certificate? certificate, X509Chain? peerChain, SslPolicyErrors errors)
    {
        // Platform name validation must survive replacing platform chain trust.
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        try
        {
            using var leaf = LoadCertificate(certificate.GetRawCertData());
            using var chain = new X509Chain();
            chain.ChainPolicy = CreatePolicy();
            if (peerChain is not null)
                foreach (var element in peerChain.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            if (!chain.Build(leaf) || chain.ChainElements.Count == 0) return false;
            var anchor = chain.ChainElements[chain.ChainElements.Count - 1].Certificate.RawData;
            return _roots.Any(root => root.RawData.SequenceEqual(anchor));
        }
        catch (CryptographicException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    internal X509ChainPolicy CreatePolicy()
    {
        var policy = new X509ChainPolicy { RevocationMode = X509RevocationMode.NoCheck };
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        foreach (var ca in _certificates) policy.ExtraStore.Add(ca);
#if NET8_0_OR_GREATER
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.DisableCertificateDownloads = true;
        foreach (var root in _roots) policy.CustomTrustStore.Add(root);
#else
        policy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
#endif
        return policy;
    }

    static X509Certificate2 LoadCertificate(byte[] der)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(der);
#else
        return new X509Certificate2(der);
#endif
    }

    public void Dispose() { foreach (var certificate in _certificates) certificate.Dispose(); }
}
