using System.Security.Authentication;
using HCG.Mail.Core.Accounts;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using HCG.Mail.Email.Revocation;

namespace HCG.Mail.Email;

public interface IMailClientFactory
{
    IImapClient CreateImapClient(TlsValidationRecorder recorder);

    ISmtpClient CreateSmtpClient(TlsValidationRecorder recorder);
}

/// <summary>
/// Creates MailKit clients with HCG Mail's security policy applied: TLS 1.2+ only, strict
/// certificate validation with revocation checking, and bounded network timeouts.
/// Protocol logging is never enabled because IMAP/SMTP logs contain authentication exchanges.
/// </summary>
public sealed class MailClientFactory : IMailClientFactory
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Verified CRL check used when the platform cannot determine revocation status. Enabled on macOS only,
    /// where .NET requires a positive OCSP-style answer that CRL-only certificates cannot provide
    /// (see docs/TLS_REVOCATION.md). Windows and Linux fetch CRLs natively and keep their behaviour.
    /// </summary>
    public IRevocationFallback? RevocationFallback { get; init; } = OperatingSystem.IsMacOS() ? CrlRevocationFallback.Shared : null;

    public IImapClient CreateImapClient(TlsValidationRecorder recorder)
    {
        var client = new ImapClient();
        Configure(client, recorder);
        return client;
    }

    public ISmtpClient CreateSmtpClient(TlsValidationRecorder recorder)
    {
        var client = new SmtpClient();
        Configure(client, recorder);
        return client;
    }

    private void Configure(MailService service, TlsValidationRecorder recorder)
    {
        service.Timeout = (int)Timeout.TotalMilliseconds;
        service.CheckCertificateRevocation = true;
        recorder.RevocationFallback = RevocationFallback;
        service.ServerCertificateValidationCallback = recorder.Validate;
        service.SslProtocols = SupportedProtocols;
    }

    /// <summary>
    /// TLS 1.0/1.1 are never offered. TLS 1.3 is only requested where the OS TLS stack supports it as a
    /// client (Windows 11+, macOS, Linux); requesting it on older Windows 10 builds can break the handshake.
    /// </summary>
    internal static SslProtocols SupportedProtocols =>
        OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? SslProtocols.Tls12
            : SslProtocols.Tls12 | SslProtocols.Tls13;
}

public static class SecurityOptions
{
    /// <summary>Maps the account setting to MailKit. Never returns Auto, None or StartTlsWhenAvailable, which could fall back to plaintext.</summary>
    public static SecureSocketOptions ToSecureSocketOptions(MailSecurityMode mode) => mode switch
    {
        MailSecurityMode.SslTls => SecureSocketOptions.SslOnConnect,
        MailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported security mode."),
    };
}
