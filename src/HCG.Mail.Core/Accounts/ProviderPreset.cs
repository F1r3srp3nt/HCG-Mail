namespace HCG.Mail.Core.Accounts;

/// <summary>
/// Server settings that a provider commonly uses. Presets never contain hostnames that are
/// specific to one customer: hosting providers frequently use per-domain or per-cluster server
/// names, so the user always enters the hostname their provider gave them.
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    int ImapPort,
    MailSecurityMode ImapSecurity,
    int SmtpPort,
    MailSecurityMode SmtpSecurity,
    string HostnameHint);

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

    public static readonly ProviderPreset Custom = new(
        Id: "custom",
        DisplayName: "Other IMAP / SMTP provider",
        ImapPort: PortDefaults.ImapSslTls,
        ImapSecurity: MailSecurityMode.SslTls,
        SmtpPort: PortDefaults.SmtpSslTls,
        SmtpSecurity: MailSecurityMode.SslTls,
        HostnameHint: "Use the server names supplied by your email provider.");

    public static IReadOnlyList<ProviderPreset> All { get; } = [DomeneNo, Custom];

    public static ProviderPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
