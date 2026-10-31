using System.Diagnostics;
using HCG.Mail.Email.Html;

namespace HCG.Mail.App.Services;

public interface ILinkOpener
{
    /// <summary>Opens a web link in the system browser after showing the user the real destination.</summary>
    Task OpenAsync(string url);

    void RevealInFolder(string path);
}

public sealed class LinkOpener(IDialogService dialogs, AppSettingsService settings) : ILinkOpener
{
    public async Task OpenAsync(string url)
    {
        url = url.Trim();
        if (!MailHtmlSanitizer.IsAllowedLink(url) || url.StartsWith('#') || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

        if (settings.Current.ConfirmBeforeOpeningLinks)
        {
            var insecure = uri.Scheme == Uri.UriSchemeHttp ? "\n\nThis link is not encrypted (http)." : "";
            var ok = await dialogs.ConfirmAsync(
                "Open link in your browser?",
                $"This link goes to:\n\n{uri.Host}\n\n{uri.AbsoluteUri}{insecure}\n\nOnly continue if you trust the sender and the destination.",
                "Open link");
            if (!ok)
                return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Shows the saved file in Explorer/Finder. The file itself is never opened or executed.</summary>
    public void RevealInFolder(string path)
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
