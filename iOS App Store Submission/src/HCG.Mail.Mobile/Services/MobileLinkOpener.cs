using HCG.Mail.App.Services;
using HCG.Mail.Mobile.Platform;

namespace HCG.Mail.Mobile.Services;

/// <summary>The shared link policy (http/https only, confirmation showing the real host) with the iOS browser and share sheet.</summary>
public sealed class MobileLinkOpener(IDialogService prompts, AppSettingsService settings, IMobilePlatform platform)
    : SafeLinkOpener(prompts, settings)
{
    protected override void Launch(Uri uri) => platform.OpenUrl(uri);

    /// <summary>On iOS a saved file is handed to the share sheet; it is never opened by HCG Mail.</summary>
    public override void RevealInFolder(string path) => _ = platform.ShareFileAsync(path);
}
