using CoreGraphics;
using Foundation;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Security;
using HCG.Mail.Infrastructure;
using HCG.Mail.Mobile.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using UIKit;

namespace HCG.Mail.iOS.Platform;

/// <summary>UIKit, Keychain and MSAL behind <see cref="IMobilePlatform"/>. Must be created on the main thread.</summary>
public sealed class IosPlatform : IMobilePlatform, IDisposable
{
    private readonly List<NSObject> _observers = [];
    private readonly Lazy<IMicrosoftAuth> _microsoftAuth;

    public IosPlatform()
    {
        // The app's sandbox: settings and the account list in Library/Application Support (no secrets, no mail),
        // temporary attachment copies in tmp/ (cleared at start).
        Paths = AppPaths.ForCurrentUser();
        TemporaryFilesDirectory = Path.Combine(Path.GetTempPath(), "HCG Mail");
        _microsoftAuth = new(() => IosMicrosoftAuth.Create(TopViewController, NullLogger.Instance));

        _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.ContentSizeCategoryChangedNotification, _ => RaiseChanged()));
        _observers.Add(UIView.Notifications.ObserveReduceMotionStatusDidChange((_, _) => RaiseChanged()));
    }

    public string Description => $"{UIDevice.CurrentDevice.SystemName} {UIDevice.CurrentDevice.SystemVersion}, {UIDevice.CurrentDevice.Model}";
    public AppPaths Paths { get; }
    public string TemporaryFilesDirectory { get; }
    public ICredentialVault CredentialVault { get; } = new IosKeychainCredentialVault();
    public IMicrosoftAuth MicrosoftAuth => _microsoftAuth.Value;
    public bool IsTablet => UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad;

    /// <summary>Dynamic Type: how much the user's text size scales 17-point body text.</summary>
    public double TextScale => (double)UIFontMetrics.DefaultMetrics.GetScaledValue(17) / 17.0;

    public bool PrefersReducedMotion => UIAccessibility.IsReduceMotionEnabled;

    public event EventHandler? AccessibilitySettingsChanged;

    public void OpenUrl(Uri uri)
    {
        using var url = new NSUrl(uri.AbsoluteUri);
        UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
    }

    /// <summary>The system share sheet (Save to Files, AirDrop, Mail, other apps). On iPad it is anchored as a popover.</summary>
    public async Task ShareFileAsync(string path)
    {
        if (TopViewController() is not { } presenter)
            return;
        using var url = NSUrl.FromFilename(path);
        var sheet = new UIActivityViewController([url], null);
        if (sheet.PopoverPresentationController is { } popover && presenter.View is { } view)
        {
            popover.SourceView = view;
            popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 1, 1);
            popover.PermittedArrowDirections = 0;
        }
        await presenter.PresentViewControllerAsync(sheet, true);
    }

    internal static UIViewController? TopViewController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes
            .ToArray()
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);
        var controller = window?.RootViewController;
        while (controller?.PresentedViewController is { } presented)
            controller = presented;
        return controller;
    }

    private void RaiseChanged() => AccessibilitySettingsChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        foreach (var observer in _observers)
            observer.Dispose();
        _observers.Clear();
    }
}
