namespace HCG.Mail.Core.Accounts;

public sealed record ServerEndpoint(string Host, int Port, MailSecurityMode Security)
{
    public override string ToString() => $"{Host}:{Port} ({Security})";
}

public static class PortDefaults
{
    public const int ImapSslTls = 993;
    public const int ImapStartTls = 143;
    public const int SmtpSslTls = 465;
    public const int SmtpStartTls = 587;

    public static int Imap(MailSecurityMode mode) => mode == MailSecurityMode.SslTls ? ImapSslTls : ImapStartTls;

    public static int Smtp(MailSecurityMode mode) => mode == MailSecurityMode.SslTls ? SmtpSslTls : SmtpStartTls;
}
