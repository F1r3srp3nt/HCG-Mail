namespace HCG.Mail.Core.Accounts;

/// <summary>
/// Non-secret account configuration. The password is never part of this record; it lives in
/// the platform credential vault keyed by <see cref="CredentialKey"/>.
/// </summary>
/// <summary>How HCG Mail reaches the mailbox.</summary>
public enum MailProviderKind
{
    /// <summary>IMAP for reading, SMTP for sending, password in the OS credential store.</summary>
    ImapSmtp,

    /// <summary>
    /// Outlook.com / Hotmail / Microsoft 365 through Microsoft Graph, signed in with Microsoft in the system browser.
    /// No password is stored; delegated OAuth tokens live in the OS-protected token cache.
    /// </summary>
    MicrosoftGraph,

    /// <summary>
    /// Gmail / Google Workspace through the Gmail API, signed in with Google in the system browser. No password is
    /// stored; the Google refresh token lives in the OS credential store.
    /// </summary>
    Gmail,
}

public sealed record MailAccount
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string EmailAddress { get; init; }
    public required string Username { get; init; }
    public required ServerEndpoint Imap { get; init; }
    public required ServerEndpoint Smtp { get; init; }

    /// <summary>Identifier of the provider preset used to create the account, if any.</summary>
    public string? PresetId { get; init; }

    public MailProviderKind Provider { get; init; } = MailProviderKind.ImapSmtp;

    /// <summary>Microsoft accounts: MSAL's home-account identifier, used to find this account's tokens. Not a secret.</summary>
    public string? MicrosoftAccountId { get; init; }

    /// <summary>Google accounts: the Google account's stable subject identifier ("sub"), used to find its token. Not a secret.</summary>
    public string? GoogleAccountId { get; init; }

    public bool IsMicrosoft => Provider == MailProviderKind.MicrosoftGraph;

    public bool IsGoogle => Provider == MailProviderKind.Gmail;

    /// <summary>Signed in with the provider (OAuth) rather than an IMAP/SMTP password.</summary>
    public bool UsesOAuth => IsMicrosoft || IsGoogle;

    /// <summary>The service endpoint recorded for Microsoft accounts (Graph over HTTPS); IMAP/SMTP are not used for them.</summary>
    public static readonly ServerEndpoint GraphEndpoint = new("graph.microsoft.com", 443, MailSecurityMode.SslTls);

    /// <summary>The service endpoint recorded for Google accounts (Gmail API over HTTPS).</summary>
    public static readonly ServerEndpoint GmailEndpoint = new("gmail.googleapis.com", 443, MailSecurityMode.SslTls);

    public string CredentialKey => CredentialKeys.ForAccount(Id);

    public static string NewId() => Guid.NewGuid().ToString("N");
}

public static class CredentialKeys
{
    public static string ForAccount(string accountId) => $"account:{accountId}";

    /// <summary>Where a Google account's refresh token is kept: one entry per Google account, never shared.</summary>
    public static string ForGoogle(string googleAccountId) => $"oauth:google:{googleAccountId}";
}
