namespace HCG.Mail.Core.Accounts;

/// <summary>The result of an interactive Microsoft sign-in. Contains no tokens.</summary>
/// <param name="AccountKey">MSAL home-account identifier: finds this account's tokens in the protected cache.</param>
/// <param name="Username">The sign-in name (user principal name), e.g. ola@contoso.com or ola@outlook.com.</param>
/// <param name="TenantId">The Microsoft Entra tenant of the account (the fixed consumer tenant for Outlook.com accounts).</param>
public sealed record MicrosoftSignIn(string AccountKey, string Username, string? DisplayName, string TenantId)
{
    /// <summary>The tenant Microsoft uses for personal accounts (Outlook.com, Hotmail, Live).</summary>
    public const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    public bool IsPersonalAccount => string.Equals(TenantId, ConsumerTenantId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Delegated Microsoft sign-in for mailbox access (authorization code flow with PKCE in the system browser, public
/// client, no client secret). Microsoft authorization only grants access to the connected mailbox; it never grants
/// roles or permissions inside HCG Mail or any HCG system — HCG identity and permissions remain with HCG Connect.
/// </summary>
public interface IMicrosoftAuth
{
    /// <summary>False when this build has no Microsoft application (client) ID; see <see cref="ConfigurationProblem"/>.</summary>
    bool IsConfigured { get; }

    string? ConfigurationProblem { get; }

    /// <summary>The delegated permissions requested, e.g. Mail.ReadWrite and Mail.Send.</summary>
    IReadOnlyList<string> Scopes { get; }

    /// <summary>Opens the system browser for Microsoft sign-in. Cancelling the token or closing the browser ends it with <see cref="Errors.MailErrorKind.Cancelled"/>.</summary>
    Task<MicrosoftSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken);

    /// <summary>A current access token for Graph, refreshed silently. Throws <see cref="Errors.MailErrorKind.ReconnectRequired"/> when the user must sign in again.</summary>
    Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>Removes the account's tokens from the protected cache. Does not touch the mailbox.</summary>
    Task SignOutAsync(string accountKey);
}

/// <summary>Used when the build has no Microsoft client ID configured.</summary>
public sealed class UnconfiguredMicrosoftAuth(string problem) : IMicrosoftAuth
{
    public bool IsConfigured => false;
    public string? ConfigurationProblem => problem;
    public IReadOnlyList<string> Scopes => [];

    public Task<MicrosoftSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken) =>
        throw new Errors.MailOperationException(Errors.MailErrorKind.NotSupported, problem);

    public Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken) =>
        throw new Errors.MailOperationException(Errors.MailErrorKind.NotSupported, problem);

    public Task SignOutAsync(string accountKey) => Task.CompletedTask;
}
