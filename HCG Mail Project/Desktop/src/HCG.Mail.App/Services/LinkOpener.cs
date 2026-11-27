using System.Diagnostics;

namespace HCG.Mail.App.Services;

/// <summary>Desktop launch and reveal; the link policy is in <see cref="SafeLinkOpener"/>.</summary>
public sealed class LinkOpener(IDialogService dialogs, AppSettingsService settings) : SafeLinkOpener(dialogs, settings)
{
    protected override void Launch(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    /// <summary>Shows the saved file in Explorer/Finder. The file itself is never opened or executed.</summary>
    public override void RevealInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! }, UseShellExecute = false });
        }
        catch (Exception)
        {
            // Revealing the folder is a convenience; failure is not an error for the user.
        }
    }
}
