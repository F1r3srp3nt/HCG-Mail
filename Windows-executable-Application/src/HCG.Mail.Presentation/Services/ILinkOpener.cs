using HCG.Mail.Email.Html;

namespace HCG.Mail.App.Services;

public interface ILinkOpener
{
    /// <summary>Opens a web link in the system browser after showing the user the real destination.</summary>
    Task OpenAsync(string url);

    /// <summary>Shows a saved file to the user (desktop: in Explorer/Finder; mobile: the share sheet). Never opens or runs it.</summary>
    void RevealInFolder(string path);
}

/// <summary>
/// The platform-neutral link policy: only http(s), never mailto/javascript/file or in-page anchors, and by default a
/// confirmation that shows the real host before anything leaves the app. Platforms supply only the final launch.
/// </summary>
public abstract class SafeLinkOpener(IUserPrompts prompts, AppSettingsService settings) : ILinkOpener
{
    public async Task OpenAsync(string url)
    {
        if (Validate(url) is not { } uri)
            return;
        if (settings.Current.ConfirmBeforeOpeningLinks)
        {
            var insecure = uri.Scheme == Uri.UriSchemeHttp ? "\n\nThis link is not encrypted (http)." : "";
            var ok = await prompts.ConfirmAsync(
                "Open link in your browser?",
                $"This link goes to:\n\n{uri.Host}\n\n{uri.AbsoluteUri}{insecure}\n\nOnly continue if you trust the sender and the destination.",
                "Open link");
            if (!ok)
                return;
        }
        Launch(uri);
    }

    /// <summary>The URL as an absolute http(s) URI, or null when it must not be opened.</summary>
    public static Uri? Validate(string url)
    {
        url = url.Trim();
        if (!MailHtmlSanitizer.IsAllowedLink(url) || url.StartsWith('#') || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;
        return uri;
    }

    protected abstract void Launch(Uri uri);

    public abstract void RevealInFolder(string path);
}
