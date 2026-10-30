namespace HCG.Mail.Core.Mail;

/// <summary>The purpose of a folder, independent of what the server calls it.</summary>
public enum FolderRole
{
    None,
    Inbox,
    Drafts,
    Sent,
    Archive,
    Junk,
    Trash,
}

/// <summary>Special-use attributes advertised by the server (RFC 6154 / XLIST).</summary>
[Flags]
public enum FolderSpecialUse
{
    None = 0,
    Inbox = 1 << 0,
    Drafts = 1 << 1,
    Sent = 1 << 2,
    Archive = 1 << 3,
    Junk = 1 << 4,
    Trash = 1 << 5,
    All = 1 << 6,
    Flagged = 1 << 7,
    Important = 1 << 8,
}

/// <summary>A folder as reported by the server, before roles are assigned.</summary>
public sealed record FolderDescriptor(
    string FullName,
    string Name,
    char Delimiter,
    FolderSpecialUse SpecialUse,
    bool IsSelectable,
    int? UnreadCount = null,
    int? TotalCount = null);

public sealed record MailFolder(
    string FullName,
    string Name,
    char Delimiter,
    FolderRole Role,
    bool IsSelectable,
    int Depth,
    int? UnreadCount,
    int? TotalCount)
{
    /// <summary>Label shown in navigation: the standard name for special folders, otherwise the server name.</summary>
    public string DisplayName => Role switch
    {
        FolderRole.Inbox => "Inbox",
        FolderRole.Drafts => "Drafts",
        FolderRole.Sent => "Sent",
        FolderRole.Archive => "Archive",
        FolderRole.Junk => "Spam / Junk",
        FolderRole.Trash => "Trash",
        _ => Name,
    };
}
