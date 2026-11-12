using Avalonia;
using Avalonia.Media;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.App.ViewModels;

internal static class IconResources
{
    public static Geometry? Get(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;

    public static string KeyFor(FolderRole role) => role switch
    {
        FolderRole.Inbox => "Icon.Inbox",
        FolderRole.Drafts => "Icon.Drafts",
        FolderRole.Sent => "Icon.Sent",
        FolderRole.Archive => "Icon.Archive",
        FolderRole.Junk => "Icon.Junk",
        FolderRole.Trash => "Icon.Trash",
        _ => "Icon.Folder",
    };
}
