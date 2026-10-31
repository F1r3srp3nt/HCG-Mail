using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HCG.Mail.Tests.Email;

/// <summary>
/// A throwaway certificate authority for certificate-validation tests: issues server certificates
/// with chosen names, dates and revocation endpoints, and can serve a CRL over local HTTP.
/// Each instance uses a unique root name, so the .NET CRL disk cache never mixes test runs.
/// </summary>
internal sealed class TestPki : IDisposable
{
    private readonly RSA _rootKey = RSA.Create(2048);
    private HttpListener? _listener;
    private byte[]? _crl;

    public TestPki()
    {
        var request = new CertificateRequest($"CN=HCG Mail Test Root {Guid.NewGuid():N}", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    public X509Certificate2 Root { get; }

    /// <summary>URL of a CRL endpoint that is served by <see cref="ServeCrl"/>.</summary>
    public string CrlUrl { get; private set; } = "";

    /// <summary>A CRL URL on a port where nothing listens (revocation source unreachable).</summary>
    public static string UnreachableCrlUrl() => $"http://127.0.0.1:{FreePort()}/unreachable.crl";

    public string StartCrlServer()
    {
        var port = FreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        CrlUrl = $"http://127.0.0.1:{port}/{Guid.NewGuid():N}.crl";
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }
                if (_crl is null)
                {
                    ctx.Response.StatusCode = 503;
                }
                else
                {
                    ctx.Response.ContentType = "application/pkix-crl";
                    await ctx.Response.OutputStream.WriteAsync(_crl);
                }
                ctx.Response.Close();
            }
        });
        return CrlUrl;
    }

    /// <summary>Publishes a CRL signed by the root, listing the given certificates as revoked.</summary>
    public void ServeCrl(params X509Certificate2[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var cert in revoked)
            builder.AddEntry(cert, DateTimeOffset.UtcNow.AddMinutes(-5), X509RevocationReason.KeyCompromise);
        _crl = builder.Build(Root, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public X509Certificate2 IssueServerCertificate(
        string dnsName,
        string? crlUrl = null,
        string? ocspUrl = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        if (crlUrl is not null)
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUrl]));
        if (ocspUrl is not null)
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension([ocspUrl], null));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return request.Create(Root, notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(60), serial);
    }

    /// <summary>
    /// Builds the chain the way the TLS stack does for a server certificate and returns the resulting
    /// policy errors, exactly as SslStream would pass them to the validation callback.
    /// </summary>
    public static (X509Chain Chain, System.Net.Security.SslPolicyErrors Errors) Validate(
        X509Certificate2 serverCert, string hostname, X509Certificate2? trustedRoot, bool checkRevocation = true)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        if (trustedRoot is not null)
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));

        var errors = System.Net.Security.SslPolicyErrors.None;
        if (!chain.Build(serverCert))
            errors |= System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
        if (!serverCert.MatchesHostname(hostname))
            errors |= System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
        return (chain, errors);
    }

    /// <summary>A different key pair that claims the same name as the real root (forgery attempt).</summary>
    public X509Certificate2 CreateImpostorIssuer()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(Root.SubjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        _rootKey.Dispose();
        Root.Dispose();
    }
}
