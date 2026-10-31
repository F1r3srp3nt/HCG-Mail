namespace HCG.Mail.Core.Mail;

/// <summary>
/// Assigns <see cref="FolderRole"/>s to server folders. Server-advertised special-use flags
/// always win; well-known folder names (English and Norwegian, plus common provider variants)
/// are used only as a fallback for servers that do not advertise SPECIAL-USE. Each role is
/// assigned to at most one folder.
/// </summary>
public static class FolderRoleMapper
{
    public static readonly FolderRole[] StandardRoles =
        [FolderRole.Inbox, FolderRole.Drafts, FolderRole.Sent, FolderRole.Archive, FolderRole.Junk, FolderRole.Trash];

    private static readonly Dictionary<FolderRole, string[]> KnownNames = new()
    {
        [FolderRole.Drafts] = ["drafts", "draft", "utkast", "kladder", "kladd"],
        [FolderRole.Sent] = ["sent", "sent items", "sent messages", "sent mail", "sendt", "sendte", "sendte elementer", "sendte meldinger", "sendt post"],
        [FolderRole.Archive] = ["archive", "archives", "arkiv", "all mail"],
        [FolderRole.Junk] = ["junk", "spam", "junk e-mail", "junk email", "junk mail", "bulk mail", "søppelpost", "uønsket e-post", "uønsket epost"],
        [FolderRole.Trash] = ["trash", "deleted", "deleted items", "deleted messages", "bin", "papirkurv", "slettet", "slettede elementer", "slettede meldinger", "slettede objekter"],
    };

    public static IReadOnlyList<MailFolder> AssignRoles(IEnumerable<FolderDescriptor> folders)
    {
        var list = folders.ToList();
        var roles = new Dictionary<FolderDescriptor, FolderRole>();
        var taken = new HashSet<FolderRole>();

        // 1. INBOX is defined by the protocol (case-insensitive name "INBOX").
        var inbox = list.FirstOrDefault(f => string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(f => f.SpecialUse.HasFlag(FolderSpecialUse.Inbox));
        if (inbox is not null)
        {
            roles[inbox] = FolderRole.Inbox;
            taken.Add(FolderRole.Inbox);
        }

        // 2. Server-advertised special-use attributes.
        foreach (var role in StandardRoles.Where(r => r != FolderRole.Inbox))
        {
            var flag = ToSpecialUse(role);
            var match = list.FirstOrDefault(f => !roles.ContainsKey(f) && f.IsSelectable && f.SpecialUse.HasFlag(flag));
            if (match is not null)
            {
                roles[match] = role;
                taken.Add(role);
            }
        }

        // 3. Name heuristics for roles the server did not advertise. Prefer top-level folders,
        //    then direct children of INBOX (common on Courier/Dovecot "INBOX." namespaces).
        foreach (var role in StandardRoles.Where(r => !taken.Contains(r) && KnownNames.ContainsKey(r)))
        {
            var candidates = list
                .Where(f => !roles.ContainsKey(f) && f.IsSelectable && f.SpecialUse == FolderSpecialUse.None)
                .Select(f => (Folder: f, Rank: NameRank(f, role)))
                .Where(c => c.Rank >= 0)
                .OrderBy(c => c.Rank)
                .ThenBy(c => c.Folder.FullName.Length)
                .ToList();
            if (candidates.Count > 0)
            {
                roles[candidates[0].Folder] = role;
                taken.Add(role);
            }
        }

        return list
            .Select(f => new MailFolder(
                f.FullName,
                f.Name,
                f.Delimiter,
                roles.GetValueOrDefault(f, FolderRole.None),
                f.IsSelectable,
                Depth(f),
                f.UnreadCount,
                f.TotalCount))
            .ToList();
    }

    /// <summary>Orders folders for navigation: standard roles first in a fixed order, then the rest alphabetically by path.</summary>
    public static IReadOnlyList<MailFolder> SortForNavigation(IEnumerable<MailFolder> folders) =>
        folders
            .OrderBy(f => f.Role == FolderRole.None ? int.MaxValue : Array.IndexOf(StandardRoles, f.Role))
            .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static FolderSpecialUse ToSpecialUse(FolderRole role) => role switch
    {
        FolderRole.Inbox => FolderSpecialUse.Inbox,
        FolderRole.Drafts => FolderSpecialUse.Drafts,
        FolderRole.Sent => FolderSpecialUse.Sent,
        FolderRole.Archive => FolderSpecialUse.Archive,
        FolderRole.Junk => FolderSpecialUse.Junk,
        FolderRole.Trash => FolderSpecialUse.Trash,
        _ => FolderSpecialUse.None,
    };

    private static int NameRank(FolderDescriptor folder, FolderRole role)
    {
        var names = KnownNames[role];
        var leaf = folder.Name.Trim().ToLowerInvariant();
        var index = Array.IndexOf(names, leaf);
        if (index < 0)
            return -1;

        var depth = Depth(folder);
        var parent = ParentName(folder);
        if (depth == 0)
            return index;
        if (depth == 1 && string.Equals(parent, "INBOX", StringComparison.OrdinalIgnoreCase))
            return 100 + index;
        // e.g. "[Gmail]/Sent Mail" style provider containers.
        if (depth == 1 && parent is not null && parent.StartsWith('[') && parent.EndsWith(']'))
            return 200 + index;
        return -1;
    }

    private static int Depth(FolderDescriptor folder) =>
        folder.Delimiter == '\0' ? 0 : folder.FullName.Count(c => c == folder.Delimiter);

    private static string? ParentName(FolderDescriptor folder)
    {
        if (folder.Delimiter == '\0')
            return null;
        var idx = folder.FullName.LastIndexOf(folder.Delimiter);
        return idx <= 0 ? null : folder.FullName[..idx];
    }
}
