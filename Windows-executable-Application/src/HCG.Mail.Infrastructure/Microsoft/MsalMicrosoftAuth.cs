using System.Reflection;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace HCG.Mail.Infrastructure.Microsoft;

/// <summary>Where the Microsoft application (client) ID comes from. A client ID is public, not a secret.</summary>
public static class MicrosoftAuthOptions
{
    /// <summary>Development override: environment variable with the application (client) ID.</summary>
    public const string ClientIdVariable = "HCG_MAIL_MICROSOFT_CLIENT_ID";

    /// <summary>Build-time value: <c>dotnet publish -p:HcgMicrosoftClientId=&lt;GUID&gt;</c> stores it as assembly metadata.</summary>
    public const string AssemblyMetadataKey = "HcgMicrosoftClientId";

    /// <summary>The loopback redirect MSAL uses with the system browser. Register exactly this under "Mobile and desktop applications".</summary>
    public const string RedirectUri = "http://localhost";

    public static string? ResolveClientId(Assembly appAssembly)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ClientIdVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();
        return appAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == AssemblyMetadataKey)?.Value?.Trim() is { Length: > 0 } fromBuild ? fromBuild : null;
    }

    public static bool IsValidClientId(string? clientId) => Guid.TryParse(clientId, out var id) && id != Guid.Empty;

    public const string NotConfiguredMessage =
        "Microsoft sign-in is not set up in this build of HCG Mail: it has no Microsoft application (client) ID. " +
        "The app must be registered in Microsoft Entra and the ID added to the build (see docs/MICROSOFT_ACCOUNTS.md).";
}

/// <summary>
/// Microsoft sign-in with MSAL.NET as a public client: authorization code flow with PKCE in the system browser
/// (loopback redirect), no client secret, and HCG Mail never sees the Microsoft password. Tokens are kept in MSAL's
/// cache, persisted by Microsoft.Identity.Client.Extensions.Msal in OS protection: DPAPI (current user) on Windows,
/// the login Keychain on macOS, the Secret Service keyring on Linux. If that storage is unavailable, tokens are
/// kept in memory only and the user signs in again after restarting.
/// </summary>
public sealed class MsalMicrosoftAuth : IMicrosoftAuth
{
    /// <summary>
    /// Minimum delegated permissions for the implemented features: read/write mail (list, read, flag, move, delete,
    /// drafts) and send mail. User.Read gives the account's name and address. MSAL adds openid, profile and
    /// offline_access (refresh tokens) itself.
    /// </summary>
    public static readonly string[] MailScopes = ["User.Read", "Mail.ReadWrite", "Mail.Send"];

    private readonly IPublicClientApplication _app;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private bool _cacheReady;

    public MsalMicrosoftAuth(string clientId, AppPaths paths, ILogger<MsalMicrosoftAuth> logger)
    {
        if (!MicrosoftAuthOptions.IsValidClientId(clientId))
            throw new ArgumentException("The Microsoft client ID must be a GUID.", nameof(clientId));
        _logger = logger;
        CachePath = paths.DataDirectory;
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount) // work/school and personal accounts
            .WithRedirectUri(MicrosoftAuthOptions.RedirectUri)
            .WithClientName("HCG Mail")
            .Build();
    }

    public bool IsConfigured => true;
    public string? ConfigurationProblem => null;
    public IReadOnlyList<string> Scopes => MailScopes;

    /// <summary>Set when tokens cannot be persisted securely on this computer (kept in memory only).</summary>
    public string? PersistenceProblem { get; private set; }

    private string CachePath { get; }

    public async Task<MicrosoftSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken)
    {
        await EnsureCacheAsync().ConfigureAwait(false);
        var request = _app.AcquireTokenInteractive(MailScopes)
            .WithUseEmbeddedWebView(false) // the system browser: HCG Mail never sees the password
            .WithPrompt(Prompt.SelectAccount)
            .WithSystemWebViewOptions(new SystemWebViewOptions
            {
                HtmlMessageSuccess = "<html><body style='font-family:sans-serif'><h2>Signed in</h2><p>You can close this tab and return to HCG Mail.</p></body></html>",
                HtmlMessageError = "<html><body style='font-family:sans-serif'><h2>Sign-in did not complete</h2><p>Return to HCG Mail for details.</p></body></html>",
            });
        if (!string.IsNullOrWhiteSpace(loginHint))
            request = request.WithLoginHint(loginHint);

        try
        {
            var result = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var name = result.ClaimsPrincipal?.FindFirst("name")?.Value;
            return new MicrosoftSignIn(result.Account.HomeAccountId.Identifier, result.Account.Username, name, result.TenantId);
        }
        catch (OperationCanceledException)
        {
            throw new MailOperationException(MailErrorKind.Cancelled, "Microsoft sign-in was cancelled. No account was added.");
        }
        catch (MsalException ex)
        {
            throw Translate(ex, interactive: true);
        }
    }

    public async Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken)
    {
        await EnsureCacheAsync().ConfigureAwait(false);
        var account = await _app.GetAccountAsync(accountKey).ConfigureAwait(false)
                      ?? throw new MailOperationException(MailErrorKind.ReconnectRequired,
                          "HCG Mail has no Microsoft sign-in stored for this account on this computer. Reconnect the account.");
        try
        {
            var result = await _app.AcquireTokenSilent(MailScopes, account).WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            throw Translate(ex, interactive: false);
        }
        catch (HttpRequestException ex)
        {
            throw new MailOperationException(MailErrorKind.Network, "Could not reach Microsoft to renew the sign-in. Check the internet connection.", ex);
        }
    }

    public async Task SignOutAsync(string accountKey)
    {
        await EnsureCacheAsync().ConfigureAwait(false);
        var account = await _app.GetAccountAsync(accountKey).ConfigureAwait(false);
        if (account is not null)
            await _app.RemoveAsync(account).ConfigureAwait(false);
    }

    /// <summary>Maps MSAL errors (including AADSTS codes) to messages the user can act on.</summary>
    internal static MailOperationException Translate(MsalException ex, bool interactive)
    {
        var text = ex.Message ?? "";
        if (ex is MsalUiRequiredException)
        {
            return new MailOperationException(MailErrorKind.ReconnectRequired,
                "Microsoft needs you to sign in to this account again (the sign-in expired, the password changed, or access to HCG Mail was revoked). Reconnect the account.", ex);
        }
        if (text.Contains("AADSTS90094", StringComparison.Ordinal) || text.Contains("AADSTS90095", StringComparison.Ordinal) || text.Contains("AADSTS65001", StringComparison.Ordinal))
        {
            return new MailOperationException(MailErrorKind.Authentication,
                "Your organisation requires an administrator to approve HCG Mail before you can use it with this account (admin consent for Mail.ReadWrite and Mail.Send). Ask your Microsoft 365 administrator.", ex);
        }
        if (text.Contains("AADSTS50020", StringComparison.Ordinal) || text.Contains("AADSTS50177", StringComparison.Ordinal))
            return new MailOperationException(MailErrorKind.Authentication, "This account is not allowed to sign in to HCG Mail's registered application (it belongs to a directory the app does not accept).", ex);
        if (text.Contains("AADSTS700016", StringComparison.Ordinal) || text.Contains("AADSTS50011", StringComparison.Ordinal))
            return new MailOperationException(MailErrorKind.NotSupported, "HCG Mail's Microsoft app registration is incomplete (unknown client ID or redirect URI). See docs/MICROSOFT_ACCOUNTS.md.", ex);
        if (text.Contains("AADSTS53003", StringComparison.Ordinal))
            return new MailOperationException(MailErrorKind.Authentication, "Your organisation's Conditional Access policy blocked this sign-in. Contact your administrator.", ex);
        // Generic cancellation codes last: specific AADSTS reasons (admin consent, Conditional Access) also arrive as access_denied.
        if (ex.ErrorCode is "authentication_canceled" or "access_denied" || text.Contains("AADSTS65004", StringComparison.Ordinal))
            return new MailOperationException(MailErrorKind.Cancelled, "Microsoft sign-in was cancelled or permission was declined. No account was added.", ex);
        if (ex.ErrorCode is "network_not_available" or "request_timeout" || ex.InnerException is HttpRequestException)
            return new MailOperationException(MailErrorKind.Network, "Could not reach Microsoft. Check the internet connection.", ex);
        return new MailOperationException(interactive ? MailErrorKind.Authentication : MailErrorKind.ReconnectRequired,
            $"Microsoft sign-in failed ({ex.ErrorCode}). {(interactive ? "Try again." : "Reconnect the account.")}", ex);
    }

    private async Task EnsureCacheAsync()
    {
        if (_cacheReady)
            return;
        await _cacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheReady)
                return;
            try
            {
                AppPaths.CreatePrivateDirectory(CachePath);
                var properties = new StorageCreationPropertiesBuilder("msal-token-cache.bin", CachePath)
                    .WithMacKeyChain("HCG Mail Microsoft sign-in", "MSALCache")
                    .WithLinuxKeyring("hcgmail_msal.keyring", MsalCacheHelper.LinuxKeyRingDefaultCollection, "HCG Mail Microsoft sign-in",
                        new KeyValuePair<string, string>("Version", "1"), new KeyValuePair<string, string>("ProductGroup", "HCG Mail"))
                    .Build();
                var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
                helper.VerifyPersistence();
                helper.RegisterCache(_app.UserTokenCache);
            }
            catch (Exception ex) when (ex is MsalCachePersistenceException or PlatformNotSupportedException or IOException or UnauthorizedAccessException or DllNotFoundException)
            {
                // Never fall back to a plaintext token file.
                PersistenceProblem = "The protected token store is not available on this computer, so Microsoft sign-in is kept only until HCG Mail closes.";
                _logger.LogWarning("Microsoft token cache persistence unavailable ({Error}); tokens are kept in memory only", ex.GetType().Name);
            }
            _cacheReady = true;
        }
        finally
        {
            _cacheGate.Release();
        }
    }
}
