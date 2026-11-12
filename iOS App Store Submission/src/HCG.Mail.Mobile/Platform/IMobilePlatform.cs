using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Security;
using HCG.Mail.Infrastructure;

namespace HCG.Mail.Mobile.Platform;

/// <summary>
/// Everything the mobile UI needs from the operating system. HCG.Mail.iOS implements it with UIKit, the Keychain and
/// MSAL for iOS; tests use an in-memory implementation. The UI never calls platform APIs directly.
/// </summary>
public interface IMobilePlatform
{
    /// <summary>Short platform name for diagnostics ("iOS 26.0, iPad").</summary>
    string Description { get; }

    /// <summary>Private per-app storage for settings and account metadata (never passwords or mail content).</summary>
    AppPaths Paths { get; }

    /// <summary>Directory for attachment files handed to the share sheet or picked for sending. Cleared on start.</summary>
    string TemporaryFilesDirectory { get; }

    /// <summary>Passwords: the iOS Keychain (this device only, not synced). Never a file.</summary>
    ICredentialVault CredentialVault { get; }

    /// <summary>Microsoft sign-in for this platform, or an unconfigured instance that explains why it is unavailable.</summary>
    IMicrosoftAuth MicrosoftAuth { get; }

    /// <summary>True on iPad (regular-width idiom); layout itself follows the actual window size.</summary>
    bool IsTablet { get; }

    /// <summary>The user's preferred text size as a factor of the default (1.0 = default, larger for accessibility sizes).</summary>
    double TextScale { get; }

    /// <summary>The user asked the system to reduce motion.</summary>
    bool PrefersReducedMotion { get; }

    /// <summary>Raised when <see cref="TextScale"/> or <see cref="PrefersReducedMotion"/> changes.</summary>
    event EventHandler? AccessibilitySettingsChanged;

    /// <summary>Opens an http(s) URL in the system browser. Called only after the shared link policy approved it.</summary>
    void OpenUrl(Uri uri);

    /// <summary>Presents the system share sheet for a file (Save to Files, AirDrop, another app). HCG Mail never opens it.</summary>
    Task ShareFileAsync(string path);
}
