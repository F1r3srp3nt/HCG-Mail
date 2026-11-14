namespace HCG.Mail.Core.Accounts;

/// <summary>The result of an interactive Google sign-in. Contains no tokens.</summary>
/// <param name="AccountKey">Google's stable account identifier (the OpenID Connect "sub"): finds this account's token.</param>
/// <param name="Email">The Google account's e-mail address (verified by Google).</param>
public sealed record GoogleSignIn(string AccountKey, string Email, string? DisplayName);

/// <summary>
/// Delegated Google sign-in for Gmail access: OAuth 2.0 authorization code flow with PKCE in the system browser and
/// a loopback redirect, as Google requires for installed apps. Google authorization only grants access to the
/// connected mailbox; it never grants roles or permissions inside HCG Mail or any HCG system — HCG identity and
/// permissions remain with HCG Connect.
/// </summary>
public interface IGoogleAuth
{
    /// <summary>False when this build has no Google OAuth client; see <see cref="ConfigurationProblem"/>.</summary>
    bool IsConfigured { get; }

    string? ConfigurationProblem { get; }

    /// <summary>The OAuth scopes requested (Gmail access plus the account's name and address).</summary>
    IReadOnlyList<string> Scopes { get; }

    /// <summary>Opens the system browser for Google sign-in. Cancelling the token ends it with <see cref="Errors.MailErrorKind.Cancelled"/>.</summary>
    Task<GoogleSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken);

    /// <summary>A current access token, refreshed silently. Throws <see cref="Errors.MailErrorKind.ReconnectRequired"/> when the user must sign in again.</summary>
    Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>Deletes the account's stored token and asks Google to revoke it (best effort). Does not touch the mailbox.</summary>
    Task SignOutAsync(string accountKey);
}

/// <summary>Used when the build has no Google OAuth client configured, or on platforms where Google sign-in is not available yet.</summary>
public sealed class UnconfiguredGoogleAuth(string problem) : IGoogleAuth
{
    public bool IsConfigured => false;
    public string? ConfigurationProblem => problem;
    public IReadOnlyList<string> Scopes => [];

    public Task<GoogleSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken) =>
        throw new Errors.MailOperationException(Errors.MailErrorKind.NotSupported, problem);

    public Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken) =>
        throw new Errors.MailOperationException(Errors.MailErrorKind.NotSupported, problem);

    public Task SignOutAsync(string accountKey) => Task.CompletedTask;
}
