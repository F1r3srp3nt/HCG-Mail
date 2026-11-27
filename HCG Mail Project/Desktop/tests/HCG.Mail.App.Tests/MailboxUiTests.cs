using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace HCG.Mail.App.Tests;

/// <summary>Mailbox polish: the message context menu and resizable panes.</summary>
public class MailboxUiTests
{
    [AvaloniaFact]
    public async Task Messages_have_a_context_menu_bound_to_the_working_message_commands()
    {
        await using var ui = await UiHarness.StartAsync();
        var list = ui.Window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Messages");
        var menu = Assert.IsType<ContextMenu>(list.ContextMenu);
        menu.Open(list); // bindings resolve against the list's view model when the menu opens, as on a right-click
        await UiHarness.Settle(100);
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Reply", "Reply all", "Forward", "Mark read / unread", "Flag / unflag", "Archive", "Move to Trash"],
            items.Select(i => (string)i.Header!).ToArray());
        Assert.All(items, i => Assert.NotNull(i.Command)); // every entry does something
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Navigation_list_and_reader_are_resizable()
    {
        await using var ui = await UiHarness.StartAsync();
        Assert.Equal(2, ui.Window.GetVisualDescendants().OfType<GridSplitter>().Count());
    }
}
