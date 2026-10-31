using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Email;
using MailKit.Security;

namespace HCG.Mail.Tests.Email;

/// <summary>
/// Regression tests for server-certificate validation using real .NET chain building (with a local
/// CRL server), the same strict callback used by every IMAP/SMTP connection, and the error translator.
/// </summary>
public sealed class CertificateValidationTests : IDisposable
{
    private const string Host = "mail.hcgmail.test";
    private static readonly ServerEndpoint Endpoint = new(Host, 993, MailSecurityMode.SslTls);
    private readonly TestPki _pki = new();

    public void Dispose() => _pki.Dispose();

    private static (bool Accepted, TlsValidationRecorder Recorder, MailOperationException? Error) Run(
        X509Certificate2 cert, string host, X509Certificate2? trustedRoot)
    {
        var (chain, errors) = TestPki.Validate(cert, host, trustedRoot);
        using (chain)
        {
            var recorder = new TlsValidationRecorder();
            var accepted = recorder.Validate(new object(), cert, chain, errors);
            var error = accepted ? null : MailErrorTranslator.Translate(new SslHandshakeException("handshake failed"), MailProtocol.Imap, Endpoint with { Host = host }, recorder);
            return (accepted, recorder, error);
        }
    }

    [Fact]
    public void Valid_trusted_certificate_with_reachable_crl_is_accepted()
    {
        var crl = _pki.StartCrlServer();
        _pki.ServeCrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: crl);

        var (accepted, recorder, _) = Run(cert, Host, _pki.Root);

        Assert.True(accepted, recorder.ChainStatus.ToString());
        Assert.Equal([crl], recorder.CrlUrls);
    }

    [Fact]
    public void Expired_certificate_is_refused_as_expired()
    {
        var crl = _pki.StartCrlServer();
        _pki.ServeCrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: crl, notBefore: DateTimeOffset.UtcNow.AddDays(-90), notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var (accepted, _, error) = Run(cert, Host, _pki.Root);

        Assert.False(accepted);
        Assert.Equal(MailErrorKind.Certificate, error!.Kind);
        Assert.Contains("expired", error.UserMessage);
    }

    [Fact]
    public void Hostname_mismatch_is_refused_as_mismatch()
    {
        var crl = _pki.StartCrlServer();
        _pki.ServeCrl();
        using var cert = _pki.IssueServerCertificate("other.hosting.test", crlUrl: crl);

        var (accepted, _, error) = Run(cert, Host, _pki.Root);

        Assert.False(accepted);
        Assert.Equal(MailErrorKind.Certificate, error!.Kind);
        Assert.Contains("does not match", error.UserMessage);
        Assert.Contains("other.hosting.test", error.UserMessage);
    }

    [Fact]
    public void Untrusted_certificate_authority_is_refused_as_untrusted()
    {
        var crl = _pki.StartCrlServer();
        _pki.ServeCrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: crl);

        var (accepted, _, error) = Run(cert, Host, trustedRoot: null);

        Assert.False(accepted);
        Assert.Equal(MailErrorKind.Certificate, error!.Kind);
        Assert.Contains("not issued by a trusted authority", error.UserMessage);
    }

    [Fact]
    public void Revoked_certificate_is_refused_as_revoked()
    {
        var crl = _pki.StartCrlServer();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: crl);
        _pki.ServeCrl(cert);

        var (accepted, recorder, error) = Run(cert, Host, _pki.Root);

        Assert.False(accepted);
        Assert.True(recorder.ChainStatus.HasFlag(X509ChainStatusFlags.Revoked), recorder.ChainStatus.ToString());
        Assert.Equal(MailErrorKind.Certificate, error!.Kind);
        Assert.Contains("has been revoked", error.UserMessage);
    }

    [Fact]
    public void Unreachable_revocation_service_is_refused_but_reported_as_unavailable_not_revoked()
    {
        var unreachable = TestPki.UnreachableCrlUrl();
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: unreachable);

        var (accepted, recorder, error) = Run(cert, Host, _pki.Root);

        // Security policy unchanged: an undeterminable revocation status still blocks the connection.
        Assert.False(accepted);
        Assert.True(recorder.IsValidApartFromRevocation, recorder.ChainStatus.ToString());
        Assert.Equal(MailErrorKind.RevocationUnavailable, error!.Kind);
        Assert.Contains("could not be checked", error.UserMessage);
        Assert.Contains("127.0.0.1", error.UserMessage);            // names the revocation endpoint that failed
        Assert.Contains("not evidence that it is revoked", error.UserMessage);
        Assert.DoesNotContain("has been revoked", error.UserMessage);
        Assert.DoesNotContain("untrusted", error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Crl_endpoint_returning_an_error_is_reported_as_unavailable()
    {
        var crl = _pki.StartCrlServer(); // listening, but no CRL published → HTTP 503
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: crl);

        var (accepted, _, error) = Run(cert, Host, _pki.Root);

        Assert.False(accepted);
        Assert.Equal(MailErrorKind.RevocationUnavailable, error!.Kind);
    }

    [Fact]
    public void Revocation_problem_combined_with_another_problem_reports_the_other_problem()
    {
        using var cert = _pki.IssueServerCertificate("other.hosting.test", crlUrl: TestPki.UnreachableCrlUrl());

        var (accepted, recorder, error) = Run(cert, Host, _pki.Root);

        Assert.False(accepted);
        Assert.False(recorder.IsValidApartFromRevocation);
        Assert.Equal(MailErrorKind.Certificate, error!.Kind);
        Assert.Contains("does not match", error.UserMessage);
    }

    [Fact]
    public void Recorder_extracts_ocsp_and_crl_endpoints_and_chain_elements()
    {
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: "http://crl.example.test/ca.crl", ocspUrl: "http://ocsp.example.test");
        var (chain, errors) = TestPki.Validate(cert, Host, _pki.Root, checkRevocation: false);
        using (chain)
        {
            var recorder = new TlsValidationRecorder();
            Assert.True(recorder.Validate(new object(), cert, chain, errors));
            Assert.Equal(["http://ocsp.example.test"], recorder.OcspUrls);
            Assert.Equal(["http://crl.example.test/ca.crl"], recorder.CrlUrls);
            Assert.Equal(2, recorder.ChainElements.Count);
            Assert.Contains(Host, recorder.ChainElements[0].Subject);
        }
    }

    [Fact]
    public void Macos_explanation_is_given_for_crl_only_certificates()
    {
        using var cert = _pki.IssueServerCertificate(Host, crlUrl: TestPki.UnreachableCrlUrl());
        var (chain, errors) = TestPki.Validate(cert, Host, _pki.Root);
        using (chain)
        {
            var recorder = new TlsValidationRecorder();
            recorder.Validate(new object(), cert, chain, errors);
            var mac = MailErrorTranslator.DescribeRevocationProblem(recorder, isMacOS: true);
            var other = MailErrorTranslator.DescribeRevocationProblem(recorder, isMacOS: false);
            Assert.Contains("no OCSP", mac);
            Assert.Contains("macOS", mac);
            Assert.DoesNotContain("macOS", other);
        }
    }

    [Fact]
    public void Validation_callback_is_strict_for_every_failure_flag()
    {
        var recorder = new TlsValidationRecorder();
        foreach (var errors in new[] { SslPolicyErrors.RemoteCertificateChainErrors, SslPolicyErrors.RemoteCertificateNameMismatch, SslPolicyErrors.RemoteCertificateNotAvailable })
            Assert.False(recorder.Validate(new object(), null, null, errors));
        Assert.True(recorder.Validate(new object(), null, null, SslPolicyErrors.None));
    }

    [Fact]
    public async Task Server_that_accepts_but_never_answers_times_out_with_a_clear_message()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                    held.Add(await listener.AcceptTcpClientAsync());
            }
            catch
            {
                // listener stopped
            }
        });
        try
        {
            var recorder = new TlsValidationRecorder();
            using var client = new MailClientFactory { Timeout = TimeSpan.FromSeconds(2) }.CreateImapClient(recorder);
            var endpoint = new ServerEndpoint("127.0.0.1", port, MailSecurityMode.SslTls);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(endpoint.Host, endpoint.Port, SecurityOptions.ToSecureSocketOptions(endpoint.Security)));
            var translated = MailErrorTranslator.Translate(ex, MailProtocol.Imap, endpoint, recorder);
            Assert.Equal(MailErrorKind.Timeout, translated.Kind);
            Assert.Contains("did not respond", translated.UserMessage);
        }
        finally
        {
            listener.Stop();
            held.ForEach(c => c.Dispose());
        }
    }
}
