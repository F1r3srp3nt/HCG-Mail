using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.App.ViewModels;

public sealed partial class FolderViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))]
    private int _unreadCount;

    public FolderViewModel(MailFolder folder)
    {
        Folder = folder;
        Role = folder.Role;
        _unreadCount = folder.UnreadCount ?? 0;
    }

    /// <summary>Placeholder for a standard folder the server does not have.</summary>
    public FolderViewModel(FolderRole missingRole)
    {
        Role = missingRole;
    }

    public MailFolder? Folder { get; private set; }
    public FolderRole Role { get; }
    public string FullName => Folder?.FullName ?? "";
    public bool IsAvailable => Folder is { IsSelectable: true };
    public bool IsStandard => Role != FolderRole.None;

    public string DisplayName => Folder?.DisplayName ?? new MailFolder("", "", '/', Role, false, 0, null, null).DisplayName;

    public string ToolTip => Folder is null
        ? $"Your mail server has no {DisplayName} folder."
        : Folder.Role != FolderRole.None && !string.Equals(Folder.Name, DisplayName, StringComparison.OrdinalIgnoreCase)
            ? $"{DisplayName} — server folder \"{Folder.FullName}\""
            : Folder.FullName;

    public Geometry? Icon => IconResources.Get(IconResources.KeyFor(Role));
    public Thickness Indent => new(Role == FolderRole.None ? Math.Min(Folder?.Depth ?? 0, 6) * 12 : 0, 0, 0, 0);
    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 999 ? "999+" : UnreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public void Update(MailFolder folder)
    {
        Folder = folder;
        UnreadCount = folder.UnreadCount ?? UnreadCount;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(IsAvailable));
    }
}
