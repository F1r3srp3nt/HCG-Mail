namespace HCG.Mail.Core.Accounts;

/// <summary>
/// Server settings for a provider. Hosting providers (such as Domene.no) use per-domain or per-cluster server names, so
/// their presets carry no hostnames and the user enters what the provider gave them. Large mailbox providers publish
/// fixed server names; those presets fill them in. Every hostname here comes from the provider's own documentation
/// (<see cref="SourceUrl"/>) and its TLS certificate is checked by the Windows runtime check in CI.
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    int ImapPort,
    MailSecurityMode ImapSecurity,
    int SmtpPort,
    MailSecurityMode SmtpSecurity,
    string HostnameHint,
    string? ImapHost = null,
    string? SmtpHost = null,
    IReadOnlyList<string>? Domains = null,
    string? PasswordGuidance = null,
    string? SourceUrl = null)
{
    public bool HasHosts => ImapHost is not null && SmtpHost is not null;
    public bool HasPasswordGuidance => PasswordGuidance is not null;
}

public static class ProviderPresets
{
    public static readonly ProviderPreset DomeneNo = new(
        Id: "domene.no",
        DisplayName: "Domene.no",
        ImapPort: PortDefaults.ImapSslTls,
        ImapSecurity: MailSecurityMode.SslTls,
        SmtpPort: PortDefaults.SmtpSslTls,
        SmtpSecurity: MailSecurityMode.SslTls,
        HostnameHint: "Use the incoming and outgoing server names shown in your Domene.no control panel.");

    public static readonly ProviderPreset Gmail = new(
        Id: "gmail",
        DisplayName: "Gmail (app password)",
        ImapPort: 993, ImapSecurity: MailSecurityMode.SslTls,
        SmtpPort: 465, SmtpSecurity: MailSecurityMode.SslTls,
        HostnameHint: "Google's published IMAP and SMTP servers.",
        ImapHost: "imap.gmail.com", SmtpHost: "smtp.gmail.com",
        Domains: ["gmail.com", "googlemail.com"],
        PasswordGuidance: "Gmail does not accept your Google password here. Turn on 2-Step Verification for your Google account, " +
                          "create an app password (Google Account › Security › App passwords) and enter that 16-letter app password. " +
                          "IMAP must be allowed in Gmail's settings. Google Workspace administrators can turn app passwords off.",
        SourceUrl: "https://support.google.com/mail/answer/7126229");

    public static readonly ProviderPreset ICloud = new(
        Id: "icloud",
        DisplayName: "iCloud Mail (app-specific password)",
        ImapPort: 993, ImapSecurity: MailSecurityMode.SslTls,
        SmtpPort: 587, SmtpSecurity: MailSecurityMode.StartTls,
        HostnameHint: "Apple's published iCloud Mail servers.",
        ImapHost: "imap.mail.me.com", SmtpHost: "smtp.mail.me.com",
        Domains: ["icloud.com", "me.com", "mac.com"],
        PasswordGuidance: "iCloud Mail needs an app-specific password, not your Apple Account password. Create one at " +
                          "account.apple.com › Sign-In and Security › App-Specific Passwords (two-factor authentication must be on).",
        SourceUrl: "https://support.apple.com/102525");

    public static readonly ProviderPreset Yahoo = new(
        Id: "yahoo",
        DisplayName: "Yahoo Mail (app password)",
        ImapPort: 993, ImapSecurity: MailSecurityMode.SslTls,
        SmtpPort: 465, SmtpSecurity: MailSecurityMode.SslTls,
        HostnameHint: "Yahoo's published IMAP and SMTP servers.",
        ImapHost: "imap.mail.yahoo.com", SmtpHost: "smtp.mail.yahoo.com",
        Domains: ["yahoo.com", "ymail.com", "rocketmail.com"],
        PasswordGuidance: "Yahoo Mail needs an app password for other mail apps. Create one under Yahoo Account Info › Account security › " +
                          "Generate app password, and enter it here instead of your Yahoo password.",
        SourceUrl: "https://help.yahoo.com/kb/SLN4075.html");

    public static readonly ProviderPreset Fastmail = new(
        Id: "fastmail",
        DisplayName: "Fastmail (app password)",
        ImapPort: 993, ImapSecurity: MailSecurityMode.SslTls,
        SmtpPort: 465, SmtpSecurity: MailSecurityMode.SslTls,
        HostnameHint: "Fastmail's published IMAP and SMTP servers.",
        ImapHost: "imap.fastmail.com", SmtpHost: "smtp.fastmail.com",
        Domains: ["fastmail.com", "fastmail.fm"],
        PasswordGuidance: "Fastmail requires an app password for IMAP and SMTP. Create one under Settings › Privacy & Security › " +
                          "Manage app passwords (access: IMAP and SMTP).",
        SourceUrl: "https://www.fastmail.help/hc/en-us/articles/1500000278342");

    public static readonly ProviderPreset Custom = new(
        Id: "custom",
        DisplayName: "Other IMAP / SMTP provider",
        ImapPort: PortDefaults.ImapSslTls,
        ImapSecurity: MailSecurityMode.SslTls,
        SmtpPort: PortDefaults.SmtpSslTls,
        SmtpSecurity: MailSecurityMode.SslTls,
        HostnameHint: "Use the server names supplied by your email provider. Many providers require an app password instead of your normal password.");

    public static IReadOnlyList<ProviderPreset> All { get; } = [DomeneNo, Gmail, ICloud, Yahoo, Fastmail, Custom];

    public static ProviderPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset whose published domains include the address's domain, if any.</summary>
    public static ProviderPreset? ForAddress(string? address)
    {
        var domain = DomainOf(address);
        return domain is null ? null : All.FirstOrDefault(p => p.Domains?.Contains(domain, StringComparer.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Addresses that cannot be added with IMAP/SMTP and a password in this release, and why. Microsoft requires its own
    /// sign-in (OAuth) for Outlook.com and Hotmail in other mail apps; Proton Mail is reachable only through Proton Mail Bridge.
    /// </summary>
    public static string? NoticeFor(string? address)
    {
        var domain = DomainOf(address);
        if (domain is null)
            return null;
        var name = domain.Split('.')[0];
        if (name is "outlook" or "hotmail" or "live" or "msn" or "passport")
            return "Outlook.com and Hotmail mailboxes: Microsoft no longer accepts the account password over IMAP and SMTP in other mail apps; " +
                   "it requires Microsoft sign-in, which is not part of HCG Mail 1.0 Public Beta. Microsoft 365 work or school mailboxes " +
                   "work only if your organization still allows IMAP and SMTP with a password.";
        if (domain is "proton.me" or "protonmail.com" or "protonmail.ch" or "pm.me")
            return "Proton Mail works with other mail apps only through Proton Mail Bridge (paid plans). Install Bridge, then enter the " +
                   "server names, ports and the Bridge password that Bridge shows.";
        return null;
    }

    private static string? DomainOf(string? address)
    {
        var at = address?.Trim().LastIndexOf('@') ?? -1;
        return at < 0 || at == address!.Trim().Length - 1 ? null : address.Trim()[(at + 1)..].ToLowerInvariant();
    }
}
