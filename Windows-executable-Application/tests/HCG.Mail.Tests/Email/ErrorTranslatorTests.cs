using System.Net.Security;
using System.Net.Sockets;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Email;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace HCG.Mail.Tests.Email;

public class ErrorTranslatorTests
{
    private static readonly ServerEndpoint Imap = new("mail.firma.no", 993, MailSecurityMode.SslTls);

    [Theory]
    [InlineData(SocketError.HostNotFound, MailErrorKind.Dns)]
    [InlineData(SocketError.NoData, MailErrorKind.Dns)]
    [InlineData(SocketError.ConnectionRefused, MailErrorKind.Network)]
    [InlineData(SocketError.TimedOut, MailErrorKind.Timeout)]
    [InlineData(SocketError.NetworkUnreachable, MailErrorKind.Network)]
    public void Socket_errors(SocketError error, MailErrorKind kind)
    {
        var result = MailErrorTranslator.Translate(new SocketException((int)error), MailProtocol.Imap, Imap);
        Assert.Equal(kind, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.UserMessage));
    }

    [Fact]
    public void Authentication_failure_is_reported_without_credentials()
    {
        var result = MailErrorTranslator.Translate(new AuthenticationException("AUTHENTICATIONFAILED"), MailProtocol.Imap, Imap);
        Assert.Equal(MailErrorKind.Authentication, result.Kind);
        Assert.Contains("rejected the username or password", result.UserMessage);
    }

    [Fact]
    public void Certificate_name_mismatch_is_explained()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=*.hosting.example", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddDnsName("*.hosting.example");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var recorder = new TlsValidationRecorder();
        // The validation callback rejects a certificate issued for another host...
        Assert.False(recorder.Validate(this, cert, null, SslPolicyErrors.RemoteCertificateNameMismatch));

        var result = MailErrorTranslator.Translate(new SslHandshakeException("handshake failed"), MailProtocol.Imap, Imap, recorder);
        Assert.Equal(MailErrorKind.Certificate, result.Kind);
        Assert.Contains("*.hosting.example", result.UserMessage);
        Assert.Contains("does not match 'mail.firma.no'", result.UserMessage);
    }

    [Fact]
    public void Handshake_failure_without_certificate_problem_suggests_port_mismatch()
    {
        var result = MailErrorTranslator.Translate(new SslHandshakeException("wrong version"), MailProtocol.Imap, Imap, new TlsValidationRecorder());
        Assert.Equal(MailErrorKind.Tls, result.Kind);
        Assert.Contains("security setting matches the port", result.UserMessage);
    }

    [Fact]
    public void Missing_starttls_is_never_silently_downgraded()
    {
        var endpoint = new ServerEndpoint("mail.firma.no", 143, MailSecurityMode.StartTls);
        var result = MailErrorTranslator.Translate(new NotSupportedException("The IMAP server does not support the STARTTLS extension."), MailProtocol.Imap, endpoint);
        Assert.Equal(MailErrorKind.Tls, result.Kind);
        Assert.Contains("never connects without encryption", result.UserMessage);
    }

    [Fact]
    public void Smtp_recipient_rejection_names_the_recipient()
    {
        var ex = new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, new MailboxAddress(null, "nobody@example.com"), "5.1.1 User unknown");
        var result = MailErrorTranslator.Translate(ex, MailProtocol.Smtp, Imap with { Port = 465 });
        Assert.Equal(MailErrorKind.ServerRejected, result.Kind);
        Assert.Contains("nobody@example.com", result.UserMessage);
    }

    [Fact]
    public void Lost_connection_is_transient()
    {
        var result = MailErrorTranslator.Translate(new IOException("reset"), MailProtocol.Imap, Imap);
        Assert.Equal(MailErrorKind.Network, result.Kind);
        Assert.True(result.IsTransient);
    }

    [Fact]
    public void Protocol_error_on_mismatched_port_explains_security_setting()
    {
        var endpoint = new ServerEndpoint("mail.firma.no", 993, MailSecurityMode.StartTls);
        var result = MailErrorTranslator.Translate(new ImapProtocolException("unexpected token"), MailProtocol.Imap, endpoint);
        Assert.Equal(MailErrorKind.Protocol, result.Kind);
        Assert.Contains("993", result.UserMessage);
    }

    [Fact]
    public void Missing_folder_and_message()
    {
        Assert.Equal(MailErrorKind.FolderNotFound, MailErrorTranslator.Translate(new FolderNotFoundException("X"), MailProtocol.Imap, Imap).Kind);
        Assert.Equal(MailErrorKind.MessageNotFound, MailErrorTranslator.Translate(new MessageNotFoundException("gone"), MailProtocol.Imap, Imap).Kind);
    }

    [Fact]
    public void Unknown_errors_do_not_leak_exception_messages()
    {
        var result = MailErrorTranslator.Translate(new InvalidOperationException("internal detail password=hunter2"), MailProtocol.Imap, Imap);
        Assert.Equal(MailErrorKind.Unknown, result.Kind);
        Assert.DoesNotContain("hunter2", result.UserMessage);
    }

    [Fact]
    public void Already_translated_errors_pass_through()
    {
        var original = new MailOperationException(MailErrorKind.Tls, "x");
        Assert.Same(original, MailErrorTranslator.Translate(original, MailProtocol.Imap, Imap));
    }
}

public class SecurityOptionsTests
{
    [Theory]
    [InlineData(MailSecurityMode.SslTls, SecureSocketOptions.SslOnConnect)]
    [InlineData(MailSecurityMode.StartTls, SecureSocketOptions.StartTls)]
    public void Maps_to_strict_mailkit_options(MailSecurityMode mode, SecureSocketOptions expected) =>
        Assert.Equal(expected, SecurityOptions.ToSecureSocketOptions(mode));

    [Fact]
    public void Never_maps_to_options_that_allow_plaintext()
    {
        var mapped = Enum.GetValues<MailSecurityMode>().Select(SecurityOptions.ToSecureSocketOptions).ToList();
        Assert.DoesNotContain(SecureSocketOptions.None, mapped);
        Assert.DoesNotContain(SecureSocketOptions.Auto, mapped);
        Assert.DoesNotContain(SecureSocketOptions.StartTlsWhenAvailable, mapped);
    }

    [Fact]
    public void Clients_use_strict_certificate_validation_and_modern_tls()
    {
        var recorder = new TlsValidationRecorder();
        using var imap = (ImapClient)new MailClientFactory().CreateImapClient(recorder);
        Assert.True(imap.CheckCertificateRevocation);
        Assert.NotNull(imap.ServerCertificateValidationCallback);
        Assert.False(imap.ServerCertificateValidationCallback!(imap, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(imap.ServerCertificateValidationCallback!(imap, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(imap.ServerCertificateValidationCallback!(imap, null, null, SslPolicyErrors.None));
        Assert.True(imap.SslProtocols.HasFlag(System.Security.Authentication.SslProtocols.Tls12));
#pragma warning disable SYSLIB0039 // checking that obsolete protocols are NOT enabled
        Assert.False(imap.SslProtocols.HasFlag(System.Security.Authentication.SslProtocols.Tls11));
        Assert.False(imap.SslProtocols.HasFlag(System.Security.Authentication.SslProtocols.Tls));
#pragma warning restore SYSLIB0039
    }
}
