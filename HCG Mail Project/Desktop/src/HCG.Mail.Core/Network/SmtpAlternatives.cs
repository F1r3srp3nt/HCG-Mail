using HCG.Mail.Core.Accounts;

namespace HCG.Mail.Core.Network;

public sealed record SmtpAlternativeCandidate(ServerEndpoint Endpoint, string Source, bool DocumentedByProvider);

/// <summary>
/// Other ways to reach an account's outgoing server when a network blocks its port. Restricted networks often close one
/// submission port (465 or 587) and leave the other open. Only two kinds of alternative are offered, both encrypted and with
/// the same server name and certificate checks: ports the provider documents for its servers, and — for other servers — the
/// other standard submission port of the same server, which the Network Doctor offers only after the server itself has shown,
/// on this network, a valid certificate, encryption and accepted sign-in. Port 25 and unencrypted connections are never offered.
/// </summary>
public static class SmtpAlternatives
{
    private sealed record Documented(string Host, int Port, MailSecurityMode Security, string Source);

    // From each provider's own instructions (see ProviderPresets.SourceUrl).
    private static readonly Documented[] DocumentedEndpoints =
    [
        new("smtp.gmail.com", 465, MailSecurityMode.SslTls, "documented by Google"),
        new("smtp.gmail.com", 587, MailSecurityMode.StartTls, "documented by Google"),
        new("smtp.mail.yahoo.com", 465, MailSecurityMode.SslTls, "documented by Yahoo"),
        new("smtp.mail.yahoo.com", 587, MailSecurityMode.StartTls, "documented by Yahoo"),
        new("smtp.fastmail.com", 465, MailSecurityMode.SslTls, "documented by Fastmail"),
        new("smtp.fastmail.com", 587, MailSecurityMode.StartTls, "documented by Fastmail"),
        new("smtp.mail.me.com", 587, MailSecurityMode.StartTls, "documented by Apple"),
    ];

    public static IReadOnlyList<SmtpAlternativeCandidate> For(ServerEndpoint current)
    {
        var host = current.Host.Trim().TrimEnd('.');
        var documented = DocumentedEndpoints.Where(d => string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
        if (documented.Count > 0)
        {
            return documented
                .Where(d => d.Port != current.Port || d.Security != current.Security)
                .Select(d => new SmtpAlternativeCandidate(new ServerEndpoint(d.Host, d.Port, d.Security), d.Source, DocumentedByProvider: true))
                .ToList();
        }

        // Any other server: the other standard submission port, verified live before it is offered.
        ServerEndpoint? other = current.Port switch
        {
            PortDefaults.SmtpSslTls => new ServerEndpoint(host, PortDefaults.SmtpStartTls, MailSecurityMode.StartTls),
            PortDefaults.SmtpStartTls => new ServerEndpoint(host, PortDefaults.SmtpSslTls, MailSecurityMode.SslTls),
            _ => null,
        };
        return other is null ? [] :
            [new SmtpAlternativeCandidate(other, "standard submission port, confirmed by your server on this network", DocumentedByProvider: false)];
    }
}
