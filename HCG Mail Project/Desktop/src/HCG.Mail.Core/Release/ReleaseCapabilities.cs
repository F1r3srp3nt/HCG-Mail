using System.Reflection;

namespace HCG.Mail.Core.Release;

/// <summary>Which audience a build is for. Set at build time with <c>-p:HcgReleaseChannel=…</c>.</summary>
public enum ReleaseChannel
{
    /// <summary>HCG Mail 1.0 Public Beta: IMAP and SMTP only. The default, so a build without the property cannot expose more.</summary>
    PublicBeta,

    /// <summary>Internal development builds: everything implemented, including Microsoft and Google sign-in when configured.</summary>
    Internal,
}

/// <summary>
/// Release capability flags: which implemented features a build offers. A disabled capability is hidden from every entry
/// point (onboarding, Settings, account setup, provider selection); its implementation, configuration, tests and
/// documentation stay in place, and existing accounts that use it are kept unchanged (see docs/RELEASE_CAPABILITIES.md).
/// </summary>
public sealed record ReleaseCapabilities(ReleaseChannel Channel)
{
    public const string MetadataKey = "HcgReleaseChannel";

    public static ReleaseCapabilities PublicBeta { get; } = new(ReleaseChannel.PublicBeta);

    public static ReleaseCapabilities Internal { get; } = new(ReleaseChannel.Internal);

    /// <summary>Microsoft accounts (Outlook.com, Hotmail, Microsoft 365) through Microsoft sign-in and Microsoft Graph.</summary>
    public bool MicrosoftSignIn => Channel == ReleaseChannel.Internal;

    /// <summary>Google accounts (Gmail, Google Workspace) through Google sign-in and the Gmail API.</summary>
    public bool GoogleSignIn => Channel == ReleaseChannel.Internal;

    public string ChannelName => Channel == ReleaseChannel.PublicBeta ? "Public Beta" : "Internal development build";

    /// <summary>Shown for an existing Microsoft account in a build without Microsoft sign-in. Nothing was deleted.</summary>
    public const string MicrosoftUnavailableMessage =
        "Microsoft accounts are not available in HCG Mail 1.0 Public Beta. This account and its settings are kept unchanged " +
        "on this device. To read this mailbox now, add it with IMAP and SMTP if your Microsoft account allows that, or use an " +
        "internal development build.";

    /// <summary>Shown for an existing Google account in a build without Google sign-in. Nothing was deleted.</summary>
    public const string GoogleUnavailableMessage =
        "Google accounts are not available in HCG Mail 1.0 Public Beta. This account and its settings are kept unchanged on " +
        "this device. To read this mailbox now, add it with IMAP and SMTP using a Google app password, or use an internal " +
        "development build.";

    /// <summary>Unknown or missing values mean Public Beta: a build fails closed rather than exposing more.</summary>
    public static ReleaseCapabilities Parse(string? value) =>
        string.Equals(value?.Trim(), nameof(ReleaseChannel.Internal), StringComparison.OrdinalIgnoreCase) ? Internal : PublicBeta;

    public static ReleaseCapabilities FromAssembly(Assembly assembly) =>
        Parse(assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value);
}
