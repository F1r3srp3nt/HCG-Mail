using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using HCG.Mail.Mobile.ViewModels;

namespace HCG.Mail.Mobile.Tests;

/// <summary>Adaptive layout: iPhone navigation, iPad split panes, rotation, split view, text size, safe area, keyboard.</summary>
public sealed class LayoutTests
{
    [AvaloniaFact]
    public async Task IPhone_shows_one_screen_at_a_time_starting_with_the_inbox()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        Assert.Equal(ShellLayout.Compact, ui.Shell.Layout);
        Assert.Equal(MailPage.Messages, ui.Shell.Page);
        Assert.True(ui.View.ListPane.IsVisible);
        Assert.False(ui.View.ReaderPane.IsVisible);
        Assert.False(ui.View.MailboxesPane.IsVisible);
        await MobileHarness.WaitUntil(() => ui.Mail.MessageList.Items.Count > 0, "inbox");
        Assert.Equal("Inbox", ui.Mail.MessageList.FolderTitle);
        ui.Capture("iphone-inbox");
    }

    [AvaloniaFact]
    public async Task IPad_portrait_shows_list_and_reader_with_mailboxes_as_slide_over()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPadPortrait);
        Assert.Equal(ShellLayout.Regular, ui.Shell.Layout);
        Assert.True(ui.View.ListPane.IsVisible);
        Assert.True(ui.View.ReaderPane.IsVisible);
        Assert.False(ui.View.MailboxesPane.IsVisible);

        ui.Shell.ShowMailboxesCommand.Execute(null);
        await MobileHarness.Settle();
        Assert.True(ui.View.MailboxesPane.IsVisible);
        Assert.True(ui.View.SidebarScrim.IsVisible);
        ui.Capture("ipad-portrait-sidebar");

        ui.Shell.CloseSidebarCommand.Execute(null);
        await ui.OpenFirstMessageAsync();
        Assert.True(ui.View.ListPane.IsVisible); // the list stays next to the reader
        ui.Capture("ipad-portrait-reading");
    }

    [AvaloniaFact]
    public async Task IPad_landscape_shows_mailboxes_list_and_reader_side_by_side()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPadLandscape);
        Assert.Equal(ShellLayout.Wide, ui.Shell.Layout);
        Assert.True(ui.View.MailboxesPane.IsVisible && ui.View.ListPane.IsVisible && ui.View.ReaderPane.IsVisible);
        Assert.False(ui.Shell.ShowMailboxesButton);
        await ui.OpenFirstMessageAsync();
        var list = ui.View.ListPane.Bounds;
        var reader = ui.View.ReaderPane.Bounds;
        Assert.True(ui.View.MailboxesPane.Bounds.Right <= list.Left + 1, "mailboxes left of the list");
        Assert.True(list.Right <= reader.Left + 1, "list left of the reader");
        Assert.True(reader.Width > 400, "the reader gets the remaining width");
        ui.Capture("ipad-landscape-reading");
    }

    [AvaloniaFact]
    public async Task Rotation_and_split_view_switch_layout_without_losing_the_open_message()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPadLandscape);
        await ui.OpenFirstMessageAsync();
        var subject = ui.Mail.Reader.Subject;

        await ui.ResizeAsync(Devices.IPadSplitNarrow);
        Assert.Equal(ShellLayout.Compact, ui.Shell.Layout);
        Assert.Equal(subject, ui.Mail.Reader.Subject);

        await ui.ResizeAsync(Devices.IPadPortrait);
        Assert.Equal(ShellLayout.Regular, ui.Shell.Layout);
        Assert.Equal(subject, ui.Mail.Reader.Subject);

        await ui.ResizeAsync(Devices.IPhoneLandscape);
        Assert.Equal(ShellLayout.Regular, ui.Shell.Layout); // a large phone in landscape also gets two panes
    }

    [AvaloniaFact]
    public async Task Larger_text_scales_the_interface_and_moves_to_a_simpler_layout()
    {
        var platform = new FakeMobilePlatform { TextScale = 1.5 };
        await using var ui = await MobileHarness.StartAsync(Devices.IPadPortrait, platform);
        Assert.Equal(1.5, ui.Shell.TextScale);
        var transform = Assert.IsType<ScaleTransform>(ui.View.Zoom.LayoutTransform);
        Assert.Equal(1.5, transform.ScaleX);
        // 820 points at 150 % leave 547 points of layout width: one screen at a time.
        Assert.Equal(ShellLayout.Compact, ui.Shell.Layout);
        ui.Capture("ipad-portrait-large-text");

        platform.TextScale = 4; // accessibility sizes are capped so the interface stays usable
        await MobileHarness.Settle();
        Assert.Equal(MobileShellViewModel.MaxTextScale, ui.Shell.TextScale);
        platform.TextScale = 1;
        await MobileHarness.Settle();
        Assert.Null(ui.View.Zoom.LayoutTransform);
    }

    [AvaloniaFact]
    public async Task Safe_area_and_keyboard_keep_content_visible()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Shell.SafeArea = new Thickness(0, 47, 0, 34);
        await MobileHarness.Settle();
        Assert.Equal(new Thickness(0, 47, 0, 34), ui.View.MailArea.Margin);

        ui.Shell.KeyboardHeight = 336;
        Assert.Equal(336, ui.Shell.ContentPadding.Bottom);
        ui.Mail.ComposeCommand.Execute(null);
        await MobileHarness.Settle();
        Assert.Equal(336, ui.View.SheetFrame.Padding.Bottom); // the composer ends above the keyboard
        ui.Capture("iphone-compose-keyboard");

        ui.Shell.KeyboardHeight = 0;
        Assert.Equal(34, ui.Shell.ContentPadding.Bottom);
    }

    [AvaloniaFact]
    public async Task Sheets_fill_the_iPhone_and_float_as_cards_on_iPad()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Mail.OpenSettingsCommand.Execute(null);
        await MobileHarness.Settle();
        Assert.DoesNotContain("card", ui.View.SheetFrame.Classes);
        Assert.Equal(ui.View.Root.Bounds.Width, ui.View.SheetFrame.Bounds.Width, 1);
        ui.Capture("iphone-settings");

        await ui.ResizeAsync(Devices.IPadLandscape);
        Assert.Contains("card", ui.View.SheetFrame.Classes);
        Assert.True(ui.View.SheetFrame.Bounds.Width <= Views.MobileShellView.SheetCardMaxWidth + 1);
        ui.Capture("ipad-settings-card");
    }

    [AvaloniaFact]
    public async Task Reduce_motion_stops_the_refresh_spinner()
    {
        var platform = new FakeMobilePlatform();
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone, platform);
        Assert.True(ui.Shell.AnimationsEnabled);
        platform.PrefersReducedMotion = true;
        await MobileHarness.Settle();
        Assert.True(ui.Shell.ReduceMotion);
        Assert.False(ui.Shell.SpinRefreshIcon);
    }

    [AvaloniaFact]
    public async Task Every_tappable_control_on_the_main_screens_is_at_least_44_points()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        await ui.OpenFirstMessageAsync();
        var small = new List<string>();
        foreach (var button in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(ui.View).OfType<Avalonia.Controls.Button>())
        {
            if (!button.IsEffectivelyVisible || button.Bounds.Width == 0)
                continue;
            if (button.Bounds.Height < 43.5)
                small.Add($"{button.Name ?? button.Content?.ToString() ?? button.GetType().Name}: {button.Bounds.Height:F0}pt");
        }
        Assert.True(small.Count == 0, string.Join(", ", small));
    }
}
