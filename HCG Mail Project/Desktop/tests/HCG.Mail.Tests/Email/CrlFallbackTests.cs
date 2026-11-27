using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Email;
using HCG.Mail.Email.Revocation;
using MailKit.Security;

namespace HCG.Mail.Tests.Email;

/// <summary>
/// Tests for the macOS CRL fallback (docs/TLS_REVOCATION.md). The verifier must accept only a CRL that is
/// genuinely signed by the issuer, current, complete and for the right partition; everything else must
/// stay "unknown" so the connection remains blocked.
/// </summary>
public sealed class CrlFallbackTests : IDisposable
{
    private const string Host = "mail.hcgmail.test";
    private const string CrlUrl = "http://crl.hcgmail.test/1.crl";
    private readonly TestPki _pki = new();

    public void Dispose() => _pki.Dispose();

    private X509Certificate2 Leaf(string? crlUrl = CrlUrl) => _pki.IssueServerCertificate(Host, crlUrl: crlUrl);

    private CrlCheckResult Check(TestCrl crl, X509Certificate2 cert, string url = CrlUrl) =>
        CrlVerifier.Check(crl.Build(), cert, _pki.Root, url, DateTimeOffset.UtcNow);

    // ---- CrlVerifier ----

    [Fact]
    public void Crl_signed_by_the_issuer_without_the_certificate_is_good()
    {
        using var cert = Leaf();
        var result = Check(new TestCrl { Issuer = _pki.Root }, cert);
        Assert.Equal(CrlStatus.Good, result.Status);
        Assert.NotNull(result.NextUpdate);
    }

    [Fact]
    public void Crl_listing_the_certificate_is_revoked()
    {
        using var cert = Leaf();
        using var other = Leaf();
        var result = Check(new TestCrl { Issuer = _pki.Root, Revoked = [other, cert] }, cert);
        Assert.Equal(CrlStatus.Revoked, result.Status);
    }

    [Fact]
    public void Crl_listing_only_other_certificates_is_good()
    {
        using var cert = Leaf();
        using var other = Leaf();
        Assert.Equal(CrlStatus.Good, Check(new TestCrl { Issuer = _pki.Root, Revoked = [other] }, cert).Status);
    }

    [Fact]
    public void Crl_built_by_dotnet_is_accepted_and_revocations_are_found()
    {
        using var cert = Leaf();
        var builder = new CertificateRevocationListBuilder();
        var good = builder.Build(_pki.Root, System.Numerics.BigInteger.One, DateTimeOffset.UtcNow.AddDays(1), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        Assert.Equal(CrlStatus.Good, CrlVerifier.Check(good, cert, _pki.Root, CrlUrl, DateTimeOffset.UtcNow).Status);

        builder.AddEntry(cert, DateTimeOffset.UtcNow, X509RevocationReason.KeyCompromise);
        var revoked = builder.Build(_pki.Root, 2, DateTimeOffset.UtcNow.AddDays(1), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        Assert.Equal(CrlStatus.Revoked, CrlVerifier.Check(revoked, cert, _pki.Root, CrlUrl, DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public void Pem_encoded_crl_is_accepted()
    {
        using var cert = Leaf();
        var pem = Encoding.ASCII.GetBytes(System.Security.Cryptography.PemEncoding.WriteString("X509 CRL", new TestCrl { Issuer = _pki.Root }.Build()));
        Assert.Equal(CrlStatus.Good, CrlVerifier.Check(pem, cert, _pki.Root, CrlUrl, DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public void Crl_signed_by_an_impostor_with_the_same_name_is_unknown()
    {
        using var cert = Leaf();
        using var impostor = _pki.CreateImpostorIssuer();
        var result = Check(new TestCrl { Issuer = _pki.Root, Signer = impostor }, cert);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Contains("signature", result.Detail);
    }

    [Fact]
    public void Forged_crl_that_lists_the_certificate_is_not_reported_as_revoked()
    {
        // A forged CRL must not be able to cause a "revoked" verdict either; it is simply not trusted.
        using var cert = Leaf();
        using var impostor = _pki.CreateImpostorIssuer();
        Assert.Equal(CrlStatus.Unknown, Check(new TestCrl { Issuer = _pki.Root, Signer = impostor, Revoked = [cert] }, cert).Status);
    }

    [Fact]
    public void Tampered_crl_is_unknown()
    {
        using var cert = Leaf();
        var data = new TestCrl { Issuer = _pki.Root, Revoked = [cert] }.Build();
        // Flip one bit inside the revoked serial number: the content no longer matches the signature.
        var serial = cert.SerialNumberBytes.Span[^8..].ToArray();
        var index = IndexOf(data, serial);
        Assert.True(index > 0);
        data[index + serial.Length - 1] ^= 0x01;
        Assert.Equal(CrlStatus.Unknown, CrlVerifier.Check(data, cert, _pki.Root, CrlUrl, DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public void Crl_with_a_different_issuer_name_is_unknown()
    {
        using var cert = Leaf();
        var otherName = new X500DistinguishedName("CN=Someone Else").RawData;
        Assert.Equal(CrlStatus.Unknown, Check(new TestCrl { Issuer = _pki.Root, IssuerNameOverride = otherName }, cert).Status);
    }

    [Fact]
    public void Issuer_that_is_not_the_certificates_issuer_is_unknown()
    {
        using var cert = Leaf();
        using var otherPki = new TestPki();
        var result = CrlVerifier.Check(new TestCrl { Issuer = otherPki.Root }.Build(), cert, otherPki.Root, CrlUrl, DateTimeOffset.UtcNow);
        Assert.Equal(CrlStatus.Unknown, result.Status);
    }

    [Fact]
    public void Issuer_without_crl_signing_permission_is_unknown()
    {
        using var cert = Leaf();
        // An issuer with the right name whose KeyUsage lacks cRLSign.
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(_pki.Root.SubjectName, key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var noCrlSign = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var result = CrlVerifier.Check(new TestCrl { Issuer = noCrlSign }.Build(), cert, noCrlSign, CrlUrl, DateTimeOffset.UtcNow);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Contains("not permitted to sign CRLs", result.Detail);
    }

    [Fact]
    public void Out_of_date_crl_is_unknown()
    {
        using var cert = Leaf();
        var crl = new TestCrl { Issuer = _pki.Root, ThisUpdate = DateTimeOffset.UtcNow.AddDays(-8), NextUpdate = DateTimeOffset.UtcNow.AddHours(-1) };
        var result = Check(crl, cert);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Contains("out of date", result.Detail);
    }

    [Fact]
    public void Crl_from_the_future_is_unknown()
    {
        using var cert = Leaf();
        var crl = new TestCrl { Issuer = _pki.Root, ThisUpdate = DateTimeOffset.UtcNow.AddHours(2), NextUpdate = DateTimeOffset.UtcNow.AddDays(7) };
        Assert.Equal(CrlStatus.Unknown, Check(crl, cert).Status);
    }

    [Fact]
    public void Small_clock_skew_is_tolerated()
    {
        using var cert = Leaf();
        var crl = new TestCrl { Issuer = _pki.Root, ThisUpdate = DateTimeOffset.UtcNow.AddMinutes(2) };
        Assert.Equal(CrlStatus.Good, Check(crl, cert).Status);
    }

    [Fact]
    public void Crl_without_next_update_is_unknown()
    {
        using var cert = Leaf();
        Assert.Equal(CrlStatus.Unknown, Check(new TestCrl { Issuer = _pki.Root, NextUpdate = null }, cert).Status);
    }

    [Fact]
    public void Partitioned_crl_for_the_certificates_url_is_good()
    {
        using var cert = Leaf();
        Assert.Equal(CrlStatus.Good, Check(new TestCrl { Issuer = _pki.Root, PartitionUrl = CrlUrl }, cert).Status);
    }

    [Fact]
    public void Genuine_crl_of_a_different_partition_is_unknown()
    {
        // Substitution attack: an attacker serves a genuinely signed shard that does not list the certificate.
        using var cert = Leaf();
        var result = Check(new TestCrl { Issuer = _pki.Root, PartitionUrl = "http://crl.hcgmail.test/2.crl" }, cert);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Contains("different partition", result.Detail);
    }

    [Fact]
    public void Crl_covering_only_some_reasons_is_unknown()
    {
        using var cert = Leaf();
        Assert.Equal(CrlStatus.Unknown, Check(new TestCrl { Issuer = _pki.Root, PartitionUrl = CrlUrl, OnlySomeReasons = true }, cert).Status);
    }

    [Fact]
    public void Delta_crl_is_unknown()
    {
        using var cert = Leaf();
        var result = Check(new TestCrl { Issuer = _pki.Root, DeltaIndicator = true }, cert);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Contains("delta", result.Detail);
    }

    [Fact]
    public void Crl_with_an_unknown_critical_extension_is_unknown()
    {
        using var cert = Leaf();
        var result = Check(new TestCrl { Issuer = _pki.Root, UnknownCriticalExtension = "1.3.6.1.4.1.99999.1" }, cert);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Contains("critical", result.Detail);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x30 })]
    [InlineData(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 })]
    [InlineData(new byte[] { 0x04, 0x02, 0xFF, 0xFF })]
    public void Malformed_data_is_unknown(byte[] data)
    {
        using var cert = Leaf();
        Assert.Equal(CrlStatus.Unknown, CrlVerifier.Check(data, cert, _pki.Root, CrlUrl, DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public void Truncated_crl_is_unknown()
    {
        using var cert = Leaf();
        var data = new TestCrl { Issuer = _pki.Root }.Build();
        Assert.Equal(CrlStatus.Unknown, CrlVerifier.Check(data[..^10], cert, _pki.Root, CrlUrl, DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public void Crl_with_trailing_garbage_is_unknown()
    {
        using var cert = Leaf();
        var data = new TestCrl { Issuer = _pki.Root }.Build().Concat(new byte[] { 0x00, 0x01 }).ToArray();
        Assert.Equal(CrlStatus.Unknown, CrlVerifier.Check(data, cert, _pki.Root, CrlUrl, DateTimeOffset.UtcNow).Status);
    }

    // ---- CrlRevocationFallback ----

    private sealed class FakeFetcher(Func<string, byte[]?> serve) : ICrlFetcher
    {
        public List<string> Requests { get; } = [];

        public byte[]? Fetch(string url, out string? failure)
        {
            Requests.Add(url);
            var data = serve(url);
            failure = data is null ? "connection refused" : null;
            return data;
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly X509ChainStatusFlags Unknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    [Fact]
    public void Fallback_is_good_when_every_unknown_certificate_is_verified()
    {
        using var cert = Leaf();
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root }.Build());
        var result = new CrlRevocationFallback(fetcher).Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]);
        Assert.Equal(CrlStatus.Good, result.Status);
        Assert.Equal([CrlUrl], fetcher.Requests);
    }

    [Fact]
    public void Fallback_reports_revoked()
    {
        using var cert = Leaf();
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root, Revoked = [cert] }.Build());
        Assert.Equal(CrlStatus.Revoked, new CrlRevocationFallback(fetcher).Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]).Status);
    }

    [Fact]
    public void Fallback_stays_unknown_when_the_crl_cannot_be_downloaded()
    {
        using var cert = Leaf();
        var result = new CrlRevocationFallback(new FakeFetcher(_ => null)).Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Contains("could not be downloaded", result.Detail);
    }

    [Fact]
    public void Fallback_stays_unknown_when_the_certificate_lists_no_crl()
    {
        using var cert = Leaf(crlUrl: null);
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root }.Build());
        var result = new CrlRevocationFallback(fetcher).Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Empty(fetcher.Requests);
    }

    [Fact]
    public void Fallback_never_checks_the_root_and_needs_an_unknown_element()
    {
        using var cert = Leaf();
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root }.Build());
        var result = new CrlRevocationFallback(fetcher).Verify([cert, _pki.Root], [X509ChainStatusFlags.NoError, Unknown]);
        Assert.Equal(CrlStatus.Unknown, result.Status);
        Assert.Empty(fetcher.Requests);
    }

    [Fact]
    public void Fallback_caches_verified_crls_until_next_update()
    {
        using var cert = Leaf();
        var start = DateTimeOffset.UtcNow;
        var time = new ManualTime(start);
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root, ThisUpdate = time.Now.AddMinutes(-1), NextUpdate = time.Now.AddHours(1) }.Build());
        var fallback = new CrlRevocationFallback(fetcher, time);

        Assert.Equal(CrlStatus.Good, fallback.Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]).Status);
        Assert.Equal(CrlStatus.Good, fallback.Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]).Status);
        Assert.Single(fetcher.Requests);

        time.Now = start.AddHours(2); // cached CRL expired → downloaded again
        Assert.Equal(CrlStatus.Good, fallback.Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]).Status);
        Assert.Equal(2, fetcher.Requests.Count);
    }

    [Fact]
    public void Fallback_does_not_cache_rejected_crls()
    {
        using var cert = Leaf();
        using var impostor = _pki.CreateImpostorIssuer();
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root, Signer = impostor }.Build());
        var fallback = new CrlRevocationFallback(fetcher);
        Assert.Equal(CrlStatus.Unknown, fallback.Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]).Status);
        Assert.Equal(CrlStatus.Unknown, fallback.Verify([cert, _pki.Root], [Unknown, X509ChainStatusFlags.NoError]).Status);
        Assert.Equal(2, fetcher.Requests.Count);
    }

    // ---- Integration with the TLS validation callback ----
    // Linux/Windows reproduce macOS's verdict ("revocation unknown") when the CRL address is unreachable
    // for the platform; the fallback then gets the CRL from its own fetcher.

    private (bool Accepted, TlsValidationRecorder Recorder, MailOperationException? Error) RunWithFallback(
        X509Certificate2 cert, string host, X509Certificate2? trustedRoot, ICrlFetcher fetcher)
    {
        var (chain, errors) = TestPki.Validate(cert, host, trustedRoot);
        using (chain)
        {
            var recorder = new TlsValidationRecorder { RevocationFallback = new CrlRevocationFallback(fetcher) };
            var accepted = recorder.Validate(new object(), cert, chain, errors);
            var endpoint = new ServerEndpoint(host, 993, MailSecurityMode.SslTls);
            var error = accepted ? null : MailErrorTranslator.Translate(new SslHandshakeException("handshake failed"), MailProtocol.Imap, endpoint, recorder);
            return (accepted, recorder, error);
        }
    }

    [Fact]
    public void Unknown_revocation_verified_by_the_fallback_is_accepted()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: url);
        var fetcher = new FakeFetcher(u => u == url ? new TestCrl { Issuer = _pki.Root }.Build() : null);

        var (accepted, recorder, _) = RunWithFallback(cert, Host, _pki.Root, fetcher);

        Assert.True(accepted, recorder.FallbackResult?.Detail);
        Assert.True(recorder.VerifiedByFallback);
        Assert.Equal([url], fetcher.Requests);
    }

    [Fact]
    public void Revoked_certificate_found_by_the_fallback_is_refused_as_revoked()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: url);
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root, Revoked = [cert] }.Build());

        var (accepted, recorder, error) = RunWithFallback(cert, Host, _pki.Root, fetcher);

        Assert.False(accepted);
        Assert.True(recorder.ChainStatus.HasFlag(X509ChainStatusFlags.Revoked));
        Assert.Equal(MailErrorKind.Certificate, error!.Kind);
        Assert.Contains("has been revoked", error.UserMessage);
    }

    [Fact]
    public void Fallback_failure_keeps_the_connection_blocked_and_explains_why()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: url);

        var (accepted, recorder, error) = RunWithFallback(cert, Host, _pki.Root, new FakeFetcher(_ => null));

        Assert.False(accepted);
        Assert.False(recorder.VerifiedByFallback);
        Assert.Equal(MailErrorKind.RevocationUnavailable, error!.Kind);
        Assert.Contains("own CRL check could not confirm", error.UserMessage);
        Assert.Contains("not evidence that it is revoked", error.UserMessage);
    }

    [Fact]
    public void Forged_crl_served_to_the_fallback_keeps_the_connection_blocked()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: url);
        using var impostor = _pki.CreateImpostorIssuer();
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root, Signer = impostor }.Build());

        var (accepted, _, error) = RunWithFallback(cert, Host, _pki.Root, fetcher);

        Assert.False(accepted);
        Assert.Equal(MailErrorKind.RevocationUnavailable, error!.Kind);
    }

    [Fact]
    public void Fallback_is_not_consulted_for_a_hostname_mismatch()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate("other.hosting.test", crlUrl: url);
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root }.Build());

        var (accepted, recorder, error) = RunWithFallback(cert, Host, _pki.Root, fetcher);

        Assert.False(accepted);
        Assert.Null(recorder.FallbackResult);
        Assert.Empty(fetcher.Requests);
        Assert.Contains("does not match", error!.UserMessage);
    }

    [Fact]
    public void Fallback_is_not_consulted_for_an_untrusted_issuer()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: url);
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root }.Build());

        var (accepted, recorder, _) = RunWithFallback(cert, Host, trustedRoot: null, fetcher);

        Assert.False(accepted);
        Assert.Null(recorder.FallbackResult);
        Assert.Empty(fetcher.Requests);
    }

    [Fact]
    public void Fallback_is_not_consulted_for_an_expired_certificate()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: url, notBefore: DateTimeOffset.UtcNow.AddDays(-90), notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var fetcher = new FakeFetcher(_ => new TestCrl { Issuer = _pki.Root }.Build());

        var (accepted, recorder, error) = RunWithFallback(cert, Host, _pki.Root, fetcher);

        Assert.False(accepted);
        Assert.Null(recorder.FallbackResult);
        Assert.Contains("expired", error!.UserMessage);
    }

    [Fact]
    public void Without_a_fallback_unknown_revocation_is_still_refused()
    {
        var url = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: url);
        var (chain, errors) = TestPki.Validate(cert, Host, _pki.Root);
        using (chain)
        {
            var recorder = new TlsValidationRecorder();
            Assert.False(recorder.Validate(new object(), cert, chain, errors));
            Assert.Null(recorder.FallbackResult);
        }
    }

    [Fact]
    public void Factory_enables_the_fallback_only_on_macOS()
    {
        var factory = new MailClientFactory();
        if (OperatingSystem.IsMacOS())
            Assert.NotNull(factory.RevocationFallback);
        else
            Assert.Null(factory.RevocationFallback);
    }

    // ---- HttpCrlFetcher ----

    [Fact]
    public void Http_fetcher_downloads_a_crl()
    {
        var crl = new TestCrl { Issuer = _pki.Root }.Build();
        using var server = new OneShotServer(crl, HttpStatusCode.OK);
        using var fetcher = new HttpCrlFetcher();
        Assert.Equal(crl, fetcher.Fetch(server.Url, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void Http_fetcher_rejects_error_responses()
    {
        using var server = new OneShotServer([1, 2, 3], HttpStatusCode.NotFound);
        using var fetcher = new HttpCrlFetcher();
        Assert.Null(fetcher.Fetch(server.Url, out var failure));
        Assert.Equal("HTTP 404", failure);
    }

    [Fact]
    public void Http_fetcher_rejects_oversized_crls()
    {
        using var server = new OneShotServer(new byte[HttpCrlFetcher.MaxCrlBytes + 1], HttpStatusCode.OK, chunked: true);
        using var fetcher = new HttpCrlFetcher();
        Assert.Null(fetcher.Fetch(server.Url, out var failure));
        Assert.Equal("the CRL is too large", failure);
    }

    [Theory]
    [InlineData("ldap://ldap.example.test/cn=crl")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public void Http_fetcher_only_fetches_http_addresses(string url)
    {
        using var fetcher = new HttpCrlFetcher();
        Assert.Null(fetcher.Fetch(url, out var failure));
        Assert.Equal("not an HTTP address", failure);
    }

    [Fact]
    public void Http_fetcher_reports_unreachable_servers()
    {
        using var fetcher = new HttpCrlFetcher(TimeSpan.FromSeconds(5));
        Assert.Null(fetcher.Fetch(TestPki.UnreachableCrlUrl(), out var failure));
        Assert.False(string.IsNullOrEmpty(failure));
    }

    private sealed class OneShotServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public OneShotServer(byte[] body, HttpStatusCode status, bool chunked = false)
        {
            var port = TestPki.FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Url = $"http://127.0.0.1:{port}/test.crl";
            _ = Task.Run(async () =>
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    ctx.Response.StatusCode = (int)status;
                    if (chunked)
                        ctx.Response.SendChunked = true;
                    else
                        ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
                {
                    // client hung up (e.g. after the size limit) or the test finished
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }
}
