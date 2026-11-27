using Foundation;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Infrastructure.Microsoft;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using UIKit;

namespace HCG.Mail.iOS.Platform;

/// <summary>
/// Microsoft sign-in on iOS: the shared MSAL implementation (scopes, error messages, account handling) with an
/// iOS-built client. Sign-in runs in the system authentication session (ASWebAuthenticationSession), so HCG Mail never
/// sees the Microsoft password; MSAL keeps tokens in the iOS Keychain (access group com.microsoft.adalcache).
/// This is independent of HCG Connect.
/// If the build has no client ID, or Info.plist lacks the msauth redirect scheme, sign-in is reported as not set up
/// instead of offering a login that cannot work.
/// </summary>
public static class IosMicrosoftAuth
{
    public const string KeychainGroup = "com.microsoft.adalcache";

    public static string RedirectScheme(string bundleId) => "msauth." + bundleId;

    public static string RedirectUri(string bundleId) => RedirectScheme(bundleId) + "://auth";

    public static IMicrosoftAuth Create(Func<UIViewController?> parentController, ILogger logger)
    {
        // Build-time value (-p:HcgMicrosoftClientId=…), compiled into HcgBuildInfo by HCG.Mail.iOS.csproj.
        var clientId = HcgBuildInfo.MicrosoftClientId is { Length: > 0 } fromBuild ? fromBuild : null;
        if (!MicrosoftAuthOptions.IsValidClientId(clientId))
            return new UnconfiguredMicrosoftAuth(MicrosoftAuthOptions.NotConfiguredMessage);

        var bundleId = NSBundle.MainBundle.BundleIdentifier ?? "";
        if (!DeclaresUrlScheme(RedirectScheme(bundleId)))
        {
            return new UnconfiguredMicrosoftAuth(
                $"Microsoft sign-in is not set up in this build: Info.plist does not register the redirect scheme {RedirectScheme(bundleId)}. " +
                "Add it under CFBundleURLTypes and register the redirect URI in Microsoft Entra (see the iOS README).");
        }

        var app = MsalMicrosoftAuth.CreateBuilder(clientId!)
            .WithRedirectUri(RedirectUri(bundleId))
            .WithIosKeychainSecurityGroup(KeychainGroup)
            .Build();
        return new MsalMicrosoftAuth(
            app,
            persistCache: null, // MSAL for iOS stores its token cache in the Keychain itself
            configureInteractive: request =>
            {
                request = request.WithUseEmbeddedWebView(false);
                return parentController() is { } parent ? request.WithParentActivityOrWindow(parent) : request;
            },
            logger);
    }

    private static bool DeclaresUrlScheme(string scheme)
    {
        if (NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleURLTypes") is not NSArray types)
            return false;
        for (nuint i = 0; i < types.Count; i++)
        {
            if (types.GetItem<NSDictionary>(i)?["CFBundleURLSchemes"] is NSArray schemes)
            {
                for (nuint j = 0; j < schemes.Count; j++)
                {
                    if (string.Equals(schemes.GetItem<NSString>(j)?.ToString(), scheme, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }
}
