namespace HCG.Mail.App.Services;

/// <summary>Words in shared messages that differ by platform ("this computer" on the desktop, "this device" on iPhone/iPad).</summary>
public static class PlatformText
{
    /// <summary>Set once at start-up by the platform host.</summary>
    public static string Device { get; set; } = "computer";
}
