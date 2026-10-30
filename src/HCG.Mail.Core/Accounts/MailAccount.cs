namespace HCG.Mail.Core.Accounts;

/// <summary>
/// Non-secret account configuration. The password is never part of this record; it lives in
/// the platform credential vault keyed by <see cref="CredentialKey"/>.
/// </summary>
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

    public string CredentialKey => CredentialKeys.ForAccount(Id);

    public static string NewId() => Guid.NewGuid().ToString("N");
}

public static class CredentialKeys
{
    public static string ForAccount(string accountId) => $"account:{accountId}";
}
