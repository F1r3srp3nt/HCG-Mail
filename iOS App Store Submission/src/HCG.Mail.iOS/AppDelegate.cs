using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Foundation;
using HCG.Mail.App.ViewModels;
using HCG.Mail.iOS.Platform;
using HCG.Mail.Mobile;
using Microsoft.Identity.Client;

namespace HCG.Mail.iOS;

/// <summary>
/// The iOS entry point. Avalonia hosts the shared HCG Mail interface (HCG.Mail.Mobile) in a single UIKit view; this
/// class only supplies the iOS platform (Keychain, Microsoft sign-in, share sheet, Dynamic Type), forwards the
/// Microsoft redirect URL to MSAL and starts the optional self-test.
/// </summary>
/// <remarks>
/// UIScene life cycle (required from the iOS 27 SDK, Apple TN3187): Info.plist declares UIApplicationSceneManifest,
/// and Avalonia 12's <see cref="AvaloniaAppDelegate{TApp}"/> answers application:configurationForConnectingSceneSession:
/// with its scene delegate (AvaloniaSceneDelegate). Avalonia starts at launch, in application:didFinishLaunching (so
/// <see cref="CustomizeAppBuilder"/> and AfterSetup run before any window exists); the window and the Avalonia view are
/// created when iOS connects the scene, and MobileApp's main view is placed in it then. URLs opened while the app runs
/// (scene:openURLContexts:) and the URL that launched it (the scene's connection options) both reach
/// <see cref="OnActivated"/> through IActivatableLifetime.Activated, so the Microsoft redirect works either way.
/// </remarks>
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MobileApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        var platform = new IosPlatform();
        MobileApp.Platform = platform;
        MobileApp.Mode = new AppMode(IsPreview: PreviewRequested());
#if DEBUG
        MobileApp.PreviewScreen = Environment.GetEnvironmentVariable("HCG_MAIL_PREVIEW_SCREEN");
        // Tells scripts/run-simulator.sh that the app has started (a launch with --console-pty prints no process ID).
        if (MobileApp.PreviewScreen is { Length: > 0 } previewScreen)
            Console.WriteLine($"HCG-PREVIEW: started {previewScreen}");
#endif
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .AfterSetup((Action<AppBuilder>)(_ready =>
            {
                if (Avalonia.Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
                    lifetime.Activated += OnActivated;
                if (Environment.GetEnvironmentVariable(SelfTest.Variable) == "1")
                    _ = SelfTest.RunAsync(platform);
            }));
    }

    /// <summary>
    /// Returns from the Microsoft sign-in page (msauth.&lt;bundle id&gt;://auth) to MSAL. Avalonia raises it for
    /// scene:openURLContexts: and for a URL in the scene's connection options (cold launch).
    /// </summary>
    private static void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } protocol
            && protocol.Uri.Scheme.StartsWith("msauth.", StringComparison.OrdinalIgnoreCase))
        {
            using var url = new NSUrl(protocol.Uri.AbsoluteUri);
            AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(url);
        }
    }

    /// <summary>
    /// Development preview (sample mailbox, no network) for simulator screenshots: Debug builds only, enabled with
    /// HCG_MAIL_PREVIEW=1 (for example SIMCTL_CHILD_HCG_MAIL_PREVIEW=1 xcrun simctl launch …). Release builds ignore it.
    /// </summary>
    private static bool PreviewRequested()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable("HCG_MAIL_PREVIEW") == "1";
#else
        return false;
#endif
    }
}
