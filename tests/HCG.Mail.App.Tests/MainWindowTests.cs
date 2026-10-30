using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using HCG.Mail.App.Services;
using HCG.Mail.App.Themes;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.Mail.App.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public async Task Preview_mode_loads_account_folders_and_inbox()
    {
        await using var ui = await UiHarness.StartAsync();
        var vm = ui.ViewModel;

        Assert.True(vm.IsPreviewMode);
        var account = Assert.Single(vm.Accounts);
        Assert.True(account.IsConnected);
        Assert.Equal(FolderRole.Inbox, vm.SelectedFolder?.Role);
        Assert.Equal(FolderRoleMapper.StandardRoles.Length, account.StandardFolders.Count);
        Assert.True(account.HasOtherFolders);
        Assert.NotEmpty(vm.MessageList.Items);
        // Newest first.
        var dates = vm.MessageList.Items.Select(i => i.Summary.Date).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
        UiHarness.Capture(ui.Window, "01-future-inbox");
    }

    [AvaloniaFact]
    public async Task Opening_a_message_renders_it_and_marks_it_read()
    {
        await using var ui = await UiHarness.StartAsync();
        var vm = ui.ViewModel;
        var unread = vm.MessageList.Items.First(i => !i.IsRead && !i.Summary.Subject.Contains("HTML"));
        var unreadBefore = vm.MessageList.UnreadCount;

        vm.MessageList.SelectedItem = unread;
        await UiHarness.Settle(700);

        Assert.True(vm.Reader.HasMessage);
        Assert.Equal(unread.Summary.Subject, vm.Reader.Subject);
        Assert.True(unread.IsRead);
        Assert.Equal(unreadBefore - 1, vm.MessageList.UnreadCount);
        Assert.True(vm.Reader.HasAttachments);
        Assert.True(vm.ReplyCommand.CanExecute(null));
        UiHarness.Capture(ui.Window, "02-future-reading");
    }

    [AvaloniaFact]
    public async Task Html_message_is_sanitized_and_remote_images_are_blocked_until_allowed()
    {
        await using var ui = await UiHarness.StartAsync();
        var vm = ui.ViewModel;
        vm.MessageList.SelectedItem = vm.MessageList.Items.First(i => i.Summary.Subject.Contains("HTML"));
        await UiHarness.Settle(700);

        Assert.True(vm.Reader.ShowHtmlView);
        Assert.DoesNotContain("<script", vm.Reader.SanitizedHtml);
        Assert.DoesNotContain("images.example.com", vm.Reader.SanitizedHtml);
        Assert.True(vm.Reader.ShowRemoteBanner);
        UiHarness.Capture(ui.Window, "03-future-html-blocked");

        vm.Reader.LoadRemoteImagesCommand.Execute(null);
        Assert.Contains("images.example.com", vm.Reader.SanitizedHtml);
        Assert.False(vm.Reader.ShowRemoteBanner);

        vm.Reader.ToggleViewModeCommand.Execute(null);
        Assert.True(vm.Reader.ShowTextView);
    }

    [AvaloniaFact]
    public async Task Skin_switch_is_live_persisted_and_keeps_connections()
    {
        await using var ui = await UiHarness.StartAsync();
        var themes = ui.Services.GetRequiredService<ThemeService>();
        var settings = ui.Services.GetRequiredService<AppSettingsService>();
        var session = ui.ViewModel.Accounts[0].Session;
        var messageCount = ui.ViewModel.MessageList.Items.Count;

        Color AppBackground()
        {
            Assert.True(ui.Window.TryFindResource("Hcg.Bg.App", ui.Window.ActualThemeVariant, out var brush));
            return ((ISolidColorBrush)brush!).Color;
        }

        var futureBg = AppBackground();
        themes.SetTheme(ThemeKind.HcgClassic);
        await UiHarness.Settle(200);

        Assert.Equal(HcgThemeVariants.Classic, Application.Current!.RequestedThemeVariant);
        Assert.NotEqual(futureBg, AppBackground());
        Assert.Equal(ThemeKind.HcgClassic, settings.Current.Theme);
        Assert.Same(session, ui.ViewModel.Accounts[0].Session);
        Assert.True(ui.ViewModel.Accounts[0].IsConnected);
        Assert.Equal(messageCount, ui.ViewModel.MessageList.Items.Count);

        ui.ViewModel.MessageList.SelectedItem = ui.ViewModel.MessageList.Items[0];
        await UiHarness.Settle(600);
        UiHarness.Capture(ui.Window, "04-classic-reading");

        themes.SetTheme(ThemeKind.HcgFuture);
        await UiHarness.Settle(100);
        Assert.Equal(futureBg, AppBackground());
    }

    [AvaloniaFact]
    public async Task Search_filters_loaded_messages()
    {
        await using var ui = await UiHarness.StartAsync();
        var list = ui.ViewModel.MessageList;
        var total = list.Items.Count;

        list.SearchText = "itinerary";
        Assert.Single(list.Items);

        list.SearchText = "INGRID"; // sender name, case-insensitive
        Assert.Single(list.Items);

        list.SearchText = "no such text";
        Assert.Empty(list.Items);
        Assert.True(list.IsEmpty);

        list.ClearSearchCommand.Execute(null);
        Assert.Equal(total, list.Items.Count);
    }

    [AvaloniaFact]
    public async Task Delete_moves_to_trash_and_toggles_update_state()
    {
        await using var ui = await UiHarness.StartAsync();
        var vm = ui.ViewModel;
        var item = vm.MessageList.Items.First(i => i.IsRead);
        vm.MessageList.SelectedItem = item;
        await UiHarness.Settle(500);

        await vm.ToggleFlagCommand.ExecuteAsync(item);
        Assert.Equal(!item.Summary.Flags.HasFlag(MessageFlags.Flagged), item.IsFlagged);

        await vm.ToggleReadCommand.ExecuteAsync(item);
        Assert.False(item.IsRead);

        var count = vm.MessageList.Items.Count;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(count - 1, vm.MessageList.Items.Count);
        Assert.DoesNotContain(item, vm.MessageList.Items);

        vm.SelectedFolder = vm.Accounts[0].FindByRole(FolderRole.Trash);
        await UiHarness.Settle(500);
        Assert.Contains(vm.MessageList.Items, i => i.Summary.Subject == item.Summary.Subject);
    }

    [AvaloniaFact]
    public async Task Missing_standard_folder_is_shown_disabled()
    {
        await using var ui = await UiHarness.StartAsync();
        var vm = ui.ViewModel;
        // Every standard role exists in the preview mailbox.
        Assert.All(vm.Accounts[0].StandardFolders, f => Assert.True(f.IsAvailable));
        var placeholder = new ViewModels.FolderViewModel(FolderRole.Archive);
        Assert.False(placeholder.IsAvailable);
        Assert.Contains("no Archive folder", placeholder.ToolTip);
    }
}
