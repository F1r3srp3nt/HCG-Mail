using HCG.Mail.Core.Mail;

namespace HCG.Mail.Tests.Core;

public class FolderRoleMapperTests
{
    private static FolderDescriptor F(string fullName, FolderSpecialUse use = FolderSpecialUse.None, char delimiter = '/', bool selectable = true)
    {
        var name = fullName.Contains(delimiter) ? fullName[(fullName.LastIndexOf(delimiter) + 1)..] : fullName;
        return new FolderDescriptor(fullName, name, delimiter, use, selectable);
    }

    private static FolderRole RoleOf(IReadOnlyList<MailFolder> folders, string fullName) => folders.Single(f => f.FullName == fullName).Role;

    [Fact]
    public void Special_use_flags_take_priority_over_names()
    {
        var folders = FolderRoleMapper.AssignRoles([
            F("INBOX"),
            F("Sent"),
            F("Sendte elementer", FolderSpecialUse.Sent),
            F("Papirkurv", FolderSpecialUse.Trash),
        ]);
        Assert.Equal(FolderRole.Inbox, RoleOf(folders, "INBOX"));
        Assert.Equal(FolderRole.Sent, RoleOf(folders, "Sendte elementer"));
        Assert.Equal(FolderRole.None, RoleOf(folders, "Sent"));
        Assert.Equal(FolderRole.Trash, RoleOf(folders, "Papirkurv"));
    }

    [Fact]
    public void Norwegian_names_are_recognised_without_special_use()
    {
        var folders = FolderRoleMapper.AssignRoles([
            F("INBOX"), F("Utkast"), F("Sendte elementer"), F("Arkiv"), F("Søppelpost"), F("Slettede elementer"),
        ]);
        Assert.Equal(FolderRole.Drafts, RoleOf(folders, "Utkast"));
        Assert.Equal(FolderRole.Sent, RoleOf(folders, "Sendte elementer"));
        Assert.Equal(FolderRole.Archive, RoleOf(folders, "Arkiv"));
        Assert.Equal(FolderRole.Junk, RoleOf(folders, "Søppelpost"));
        Assert.Equal(FolderRole.Trash, RoleOf(folders, "Slettede elementer"));
    }

    [Fact]
    public void Inbox_namespace_children_are_mapped_for_courier_style_servers()
    {
        var folders = FolderRoleMapper.AssignRoles([
            F("INBOX", delimiter: '.'), F("INBOX.Sent", delimiter: '.'), F("INBOX.Drafts", delimiter: '.'), F("INBOX.Trash", delimiter: '.'), F("INBOX.spam", delimiter: '.'),
        ]);
        Assert.Equal(FolderRole.Sent, RoleOf(folders, "INBOX.Sent"));
        Assert.Equal(FolderRole.Drafts, RoleOf(folders, "INBOX.Drafts"));
        Assert.Equal(FolderRole.Trash, RoleOf(folders, "INBOX.Trash"));
        Assert.Equal(FolderRole.Junk, RoleOf(folders, "INBOX.spam"));
    }

    [Fact]
    public void Inbox_is_matched_case_insensitively()
    {
        var folders = FolderRoleMapper.AssignRoles([F("Inbox")]);
        Assert.Equal(FolderRole.Inbox, folders[0].Role);
    }

    [Fact]
    public void Each_role_is_assigned_once_preferring_top_level_names()
    {
        var folders = FolderRoleMapper.AssignRoles([
            F("INBOX"), F("Projects/Sent"), F("Sent Items"), F("Sent"),
        ]);
        Assert.Equal(FolderRole.Sent, RoleOf(folders, "Sent"));
        Assert.Equal(FolderRole.None, RoleOf(folders, "Sent Items"));
        Assert.Equal(FolderRole.None, RoleOf(folders, "Projects/Sent"));
        Assert.Single(folders, f => f.Role == FolderRole.Sent);
    }

    [Fact]
    public void Nested_user_folders_with_role_names_are_not_treated_as_special()
    {
        var folders = FolderRoleMapper.AssignRoles([F("INBOX"), F("Clients/Acme/Archive")]);
        Assert.Equal(FolderRole.None, RoleOf(folders, "Clients/Acme/Archive"));
        Assert.Equal(2, folders.Single(f => f.FullName == "Clients/Acme/Archive").Depth);
    }

    [Fact]
    public void Gmail_style_container_children_are_recognised()
    {
        var folders = FolderRoleMapper.AssignRoles([F("INBOX"), F("[Gmail]", selectable: false), F("[Gmail]/Sent Mail"), F("[Gmail]/Spam")]);
        Assert.Equal(FolderRole.Sent, RoleOf(folders, "[Gmail]/Sent Mail"));
        Assert.Equal(FolderRole.Junk, RoleOf(folders, "[Gmail]/Spam"));
        Assert.Equal(FolderRole.None, RoleOf(folders, "[Gmail]"));
    }

    [Fact]
    public void Non_selectable_folders_never_get_a_role()
    {
        var folders = FolderRoleMapper.AssignRoles([F("INBOX"), F("Trash", FolderSpecialUse.Trash, selectable: false)]);
        Assert.Equal(FolderRole.None, RoleOf(folders, "Trash"));
    }

    [Fact]
    public void Navigation_order_puts_standard_roles_first()
    {
        var mapped = FolderRoleMapper.AssignRoles([F("Zeta"), F("Trash"), F("Alpha"), F("Sent"), F("INBOX"), F("Drafts")]);
        var ordered = FolderRoleMapper.SortForNavigation(mapped).Select(f => f.FullName).ToList();
        Assert.Equal(["INBOX", "Drafts", "Sent", "Trash", "Alpha", "Zeta"], ordered);
    }

    [Fact]
    public void Display_names_use_standard_labels_for_roles()
    {
        var mapped = FolderRoleMapper.AssignRoles([F("INBOX"), F("Søppelpost"), F("Custom")]);
        Assert.Equal("Inbox", mapped.Single(f => f.FullName == "INBOX").DisplayName);
        Assert.Equal("Spam / Junk", mapped.Single(f => f.FullName == "Søppelpost").DisplayName);
        Assert.Equal("Custom", mapped.Single(f => f.FullName == "Custom").DisplayName);
    }
}
