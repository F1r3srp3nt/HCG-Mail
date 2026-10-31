namespace HCG.Mail.Core.Accounts;

/// <summary>
/// Transport security for a mail server connection. There is intentionally no "None" or
/// "when available" option: every connection is encrypted and a failed upgrade is an error,
/// never a silent downgrade to plaintext.
/// </summary>
public enum MailSecurityMode
{
    /// <summary>Implicit TLS: the TLS handshake happens immediately on connect (IMAP 993, SMTP 465).</summary>
    SslTls,

    /// <summary>Explicit TLS: connect in plaintext and require STARTTLS before any credentials are sent (IMAP 143, SMTP 587).</summary>
    StartTls,
}
