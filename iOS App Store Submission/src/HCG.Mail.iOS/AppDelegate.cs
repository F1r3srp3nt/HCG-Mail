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
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MobileApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        var platform = new IosPlatform();
        MobileApp.Platform = platform;
        MobileApp.Mode = new AppMode(IsPreview: PreviewRequested());
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

    /// <summary>Returns from the Microsoft sign-in page (msauth.&lt;bundle id&gt;://auth) to MSAL.</summary>
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
