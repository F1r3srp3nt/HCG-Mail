using System.Reflection;
using System.Text.RegularExpressions;

namespace HCG.Mail.Infrastructure.Google;

/// <summary>
/// Where the Google OAuth client comes from. Built in through oauth-clients.props (assembly metadata); environment
/// variables override it for development. The client ID is public; the "Desktop app" client secret is not treated as
/// confidential by Google for installed apps, but it is never committed to the repository.
/// </summary>
public static partial class GoogleAuthOptions
{
    public const string ClientIdVariable = "HCG_MAIL_GOOGLE_CLIENT_ID";
    public const string ClientSecretVariable = "HCG_MAIL_GOOGLE_CLIENT_SECRET";
    public const string ClientIdMetadataKey = "HcgGoogleClientId";
    public const string ClientSecretMetadataKey = "HcgGoogleClientSecret";

    /// <summary>
    /// Minimum scopes for the implemented features. gmail.modify: read, label, mark read/starred, move to Trash, drafts
    /// and send — but not permanent deletion. openid/email/profile: which Google account signed in.
    /// </summary>
    public const string GmailScope = "https://www.googleapis.com/auth/gmail.modify";

    public static readonly string[] Scopes = ["openid", "email", "profile", GmailScope];

    public const string NotConfiguredMessage =
        "Google sign-in is not set up in this build of HCG Mail: it has no Google OAuth client. The app must be registered " +
        "in Google Cloud and its client ID and desktop client secret added to the build (see docs/GOOGLE_ACCOUNTS.md).";

    public static string? ResolveClientId(Assembly appAssembly) => Resolve(ClientIdVariable, ClientIdMetadataKey, appAssembly);

    public static string? ResolveClientSecret(Assembly appAssembly) => Resolve(ClientSecretVariable, ClientSecretMetadataKey, appAssembly);

    public static bool IsValidClientId(string? clientId) => clientId is not null && ClientIdPattern().IsMatch(clientId);

    private static string? Resolve(string variable, string metadataKey, Assembly assembly)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == metadataKey)?.Value?.Trim() is { Length: > 0 } fromBuild ? fromBuild : null;
    }

    [GeneratedRegex(@"^[0-9]+-[0-9a-z]+\.apps\.googleusercontent\.com$")]
    private static partial Regex ClientIdPattern();
}

/// <summary>Google's OAuth endpoints (overridable for tests only).</summary>
public sealed record GoogleEndpoints(Uri Authorization, Uri Token)
{
    public static readonly GoogleEndpoints Production = new(
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"));
}
