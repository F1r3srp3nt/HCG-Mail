using Avalonia.Headless.XUnit;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;
using HCG.Mail.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.Mail.Mobile.Tests;

/// <summary>End-to-end flows on the iPhone layout against the in-memory preview mailbox. Nothing leaves the process.</summary>
public sealed class FlowTests
{
    [AvaloniaFact]
    public async Task Mailboxes_folder_list_reader_and_back()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Shell.ShowMailboxesCommand.Execute(null);
        await MobileHarness.Settle();
        Assert.Equal(MailPage.Mailboxes, ui.Shell.Page);
        Assert.True(ui.View.MailboxesPane.IsVisible);
        ui.Capture("iphone-mailboxes");

        var sent = ui.Mail.SelectedAccount!.FindByRole(FolderRole.Sent)!;
        ui.Mail.SelectedStandardFolder = sent;
        ui.Shell.OnFolderChosen();
        await MobileHarness.Settle();
        Assert.Equal(MailPage.Messages, ui.Shell.Page);
        Assert.Equal(sent.DisplayName, ui.Mail.MessageList.FolderTitle);

        ui.Mail.SelectedStandardFolder = ui.Mail.SelectedAccount.FindByRole(FolderRole.Inbox);
        await ui.OpenFirstMessageAsync();
        Assert.Equal(MailPage.Reader, ui.Shell.Page);
        Assert.True(ui.View.ReaderPane.IsVisible);
        Assert.False(ui.View.ListPane.IsVisible);
        ui.Capture("iphone-reader");

        Assert.True(await ui.Shell.GoBackAsync());
        Assert.Equal(MailPage.Messages, ui.Shell.Page);
        Assert.True(await ui.Shell.GoBackAsync());
        Assert.Equal(MailPage.Mailboxes, ui.Shell.Page);
        Assert.False(await ui.Shell.GoBackAsync()); // nothing further back
    }

    [AvaloniaFact]
    public async Task Tapping_the_already_selected_message_opens_it_again()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        await ui.OpenFirstMessageAsync();
        await ui.Shell.GoBackAsync();
        ui.Shell.OnMessageChosen(); // same selection, no SelectionChanged
        Assert.Equal(MailPage.Reader, ui.Shell.Page);
    }

    [AvaloniaFact]
    public async Task Refresh_and_paging_use_the_shared_coordinator()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        await MobileHarness.WaitUntil(() => ui.Mail.MessageList.Items.Count > 0, "inbox");
        Assert.True(ui.Mail.RefreshCommand.CanExecute(null));
        await ui.Mail.RefreshCommand.ExecuteAsync(null);
        Assert.False(ui.Mail.IsRefreshing);
        Assert.Contains("Last refreshed", ui.Mail.StatusText);
        Assert.Contains("pull down", ui.Mail.StatusText);
        Assert.DoesNotContain("F5", ui.Mail.StatusText);
        Assert.True(ui.View.ListPane.LoadOlderButton.IsVisible == ui.Mail.MessageList.HasOlder);
    }

    [AvaloniaFact]
    public async Task Flag_read_archive_and_delete_from_the_reader()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        await ui.OpenFirstMessageAsync();
        var item = ui.Mail.MessageList.SelectedItem!;

        var flagged = item.IsFlagged;
        await ui.Mail.ToggleFlagCommand.ExecuteAsync(null);
        Assert.NotEqual(flagged, item.IsFlagged);

        var read = item.IsRead;
        await ui.Mail.ToggleReadCommand.ExecuteAsync(null);
        Assert.NotEqual(read, item.IsRead);

        var count = ui.Mail.MessageList.Items.Count;
        await ui.Mail.ArchiveCommand.ExecuteAsync(null);
        await MobileHarness.Settle();
        Assert.Equal(count - 1, ui.Mail.MessageList.Items.Count);
        Assert.DoesNotContain(item, ui.Mail.MessageList.Items);

        await ui.OpenFirstMessageAsync();
        var toDelete = ui.Mail.MessageList.SelectedItem!;
        await ui.Mail.DeleteCommand.ExecuteAsync(null); // moves to Trash without a question
        await MobileHarness.Settle();
        Assert.DoesNotContain(toDelete, ui.Mail.MessageList.Items);
    }

    [AvaloniaFact]
    public async Task Compose_send_closes_the_sheet_and_files_the_message_in_Sent()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Mail.ComposeCommand.Execute(null);
        await MobileHarness.Settle();
        var compose = Assert.IsType<ComposeViewModel>(ui.Overlays.CurrentSheet);
        compose.To = "someone@example.com";
        compose.Subject = "Mobile test";
        compose.Body = "Sent from the headless iPhone test (preview mailbox only).";
        ui.Capture("iphone-compose");
        await compose.SendCommand.ExecuteAsync(null);
        await MobileHarness.Settle();
        Assert.False(ui.Overlays.HasSheet);

        ui.Mail.SelectedStandardFolder = ui.Mail.SelectedAccount!.FindByRole(FolderRole.Sent);
        await MobileHarness.WaitUntil(() => ui.Mail.MessageList.Items.Any(m => m.Subject == "Mobile test"), "the sent message in Sent");
    }

    [AvaloniaFact]
    public async Task Cancelling_an_unsent_message_asks_and_can_save_a_draft()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Mail.ComposeCommand.Execute(null);
        await MobileHarness.Settle();
        var compose = Assert.IsType<ComposeViewModel>(ui.Overlays.CurrentSheet);
        compose.Subject = "Unfinished";
        compose.Body = "Draft text";

        var closing = ui.Shell.CloseSheetCommand.ExecuteAsync(compose);
        await MobileHarness.WaitUntil(() => ui.Overlays.HasPrompt, "the save-draft question");
        Assert.True(ui.View.PromptLayer.IsVisible);
        Assert.Equal("Save draft", ui.Overlays.Prompt!.PrimaryText);
        ui.Capture("iphone-draft-prompt");

        ui.Overlays.Prompt.CancelCommand.Execute(null); // keep editing
        await closing;
        Assert.Same(compose, ui.Overlays.CurrentSheet);

        closing = ui.Shell.CloseSheetCommand.ExecuteAsync(compose);
        await MobileHarness.WaitUntil(() => ui.Overlays.HasPrompt, "the question again");
        ui.Overlays.Prompt!.PrimaryCommand.Execute(null); // save draft
        await closing;
        await MobileHarness.Settle();
        Assert.False(ui.Overlays.HasSheet);

        ui.Mail.SelectedStandardFolder = ui.Mail.SelectedAccount!.FindByRole(FolderRole.Drafts);
        await MobileHarness.WaitUntil(() => ui.Mail.MessageList.Items.Any(m => m.Subject == "Unfinished"), "the draft in Drafts");
    }

    [AvaloniaFact]
    public async Task Closing_an_untouched_composer_does_not_ask_about_a_draft()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        await ui.OpenFirstMessageAsync();
        foreach (var open in new Action[] { () => ui.Mail.ComposeCommand.Execute(null), () => ui.Mail.ReplyCommand.Execute(null) })
        {
            open();
            await MobileHarness.Settle();
            var compose = Assert.IsType<ComposeViewModel>(ui.Overlays.CurrentSheet);
            Assert.False(compose.IsDirty);
            await ui.Shell.CloseSheetCommand.ExecuteAsync(compose);
            Assert.False(ui.Overlays.HasPrompt);
            Assert.False(ui.Overlays.HasSheet);
        }
    }

    [AvaloniaTheory]
    [InlineData(ComposeKind.Reply)]
    [InlineData(ComposeKind.ReplyAll)]
    [InlineData(ComposeKind.Forward)]
    public async Task Reply_reply_all_and_forward_open_a_prefilled_composer(ComposeKind kind)
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        await ui.OpenFirstMessageAsync();
        System.Windows.Input.ICommand command = kind switch
        {
            ComposeKind.Reply => ui.Mail.ReplyCommand,
            ComposeKind.ReplyAll => ui.Mail.ReplyAllCommand,
            _ => ui.Mail.ForwardCommand,
        };
        command.Execute(null);
        await MobileHarness.Settle();
        var compose = Assert.IsType<ComposeViewModel>(ui.Overlays.CurrentSheet);
        Assert.Equal(kind, compose.Kind);
        Assert.StartsWith(kind == ComposeKind.Forward ? "Fwd:" : "Re:", compose.Subject, StringComparison.OrdinalIgnoreCase);
        if (kind != ComposeKind.Forward)
            Assert.False(string.IsNullOrWhiteSpace(compose.To));
    }

    [AvaloniaFact]
    public async Task Saving_an_attachment_hands_it_to_the_share_sheet_and_never_opens_it()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        await MobileHarness.WaitUntil(() => ui.Mail.MessageList.Items.Any(m => m.HasAttachments), "a message with attachments");
        ui.Mail.MessageList.SelectedItem = ui.Mail.MessageList.Items.First(m => m.HasAttachments);
        ui.Shell.OnMessageChosen();
        await MobileHarness.WaitUntil(() => ui.Mail.Reader.HasAttachments, "attachments");
        ui.Capture("iphone-attachments");

        var attachment = ui.Mail.Reader.Attachments.First(a => !a.IsDangerous);
        await attachment.SaveCommand.ExecuteAsync(null);
        await MobileHarness.Settle();
        var shared = Assert.Single(ui.Platform.SharedFiles);
        Assert.True(File.Exists(shared));
        Assert.StartsWith(ui.Platform.TemporaryFilesDirectory, shared);

        var dangerous = ui.Mail.Reader.Attachments.FirstOrDefault(a => a.IsDangerous);
        if (dangerous is not null)
        {
            var saving = dangerous.SaveCommand.ExecuteAsync(null);
            await MobileHarness.WaitUntil(() => ui.Overlays.HasPrompt, "the dangerous-file warning");
            ui.Overlays.Prompt!.CancelCommand.Execute(null);
            await saving;
            Assert.Single(ui.Platform.SharedFiles); // declined: nothing saved or shared
        }
    }

    [AvaloniaFact]
    public async Task Links_show_the_real_destination_and_open_only_after_confirmation()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        var opening = ui.Mail.OpenLinkAsync("https://shop.example.com/offer?id=1");
        await MobileHarness.WaitUntil(() => ui.Overlays.HasPrompt, "link confirmation");
        Assert.Contains("shop.example.com", ui.Overlays.Prompt!.Message);
        ui.Overlays.Prompt.PrimaryCommand.Execute(null);
        await opening;
        Assert.Equal("https://shop.example.com/offer?id=1", Assert.Single(ui.Platform.OpenedUrls).AbsoluteUri);

        await ui.Mail.OpenLinkAsync("javascript:alert(1)");
        await ui.Mail.OpenLinkAsync("file:///etc/passwd");
        Assert.False(ui.Overlays.HasPrompt);
        Assert.Single(ui.Platform.OpenedUrls);
    }

    [AvaloniaFact]
    public async Task Account_setup_runs_diagnostics_and_cancel_keeps_nothing()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        var before = ui.Mail.Accounts.Count;
        var adding = ui.Mail.AddAccountCommand.ExecuteAsync(null);
        await MobileHarness.Settle();
        var setup = Assert.IsType<AccountSetupViewModel>(ui.Overlays.CurrentSheet);
        setup.EmailAddress = "ola@firma.example";
        setup.DisplayName = "Ola";
        setup.Username = "ola@firma.example";
        setup.Password = "not-a-real-password";
        setup.ImapHost = "imap.firma.example";
        setup.SmtpHost = "smtp.firma.example";
        await MobileHarness.Settle();
        ui.Capture("iphone-account-setup");

        await setup.TestConnectionCommand.ExecuteAsync(null); // preview tester: no network
        Assert.NotEmpty(setup.Steps);
        Assert.True(setup.HasTestSummary);

        await ui.Shell.CloseSheetCommand.ExecuteAsync(setup);
        await adding;
        Assert.False(ui.Overlays.HasSheet);
        Assert.Equal(before, ui.Mail.Accounts.Count);
        Assert.Equal("", setup.Password); // the typed password is cleared
    }

    [AvaloniaFact]
    public async Task Microsoft_sign_in_is_shown_as_pending_when_this_build_has_no_client_id()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        var showing = ui.Mail.AddMicrosoftAccountCommand.ExecuteAsync(null);
        await MobileHarness.Settle();
        var microsoft = Assert.IsType<MicrosoftAccountViewModel>(ui.Overlays.CurrentSheet);
        Assert.False(microsoft.IsConfigured);
        var view = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(ui.View).OfType<Views.MicrosoftAccountView>().Single();
        Assert.False(view.SignInButton.IsVisible); // no fake working login
        Assert.True(view.ConfigurationProblem.IsEffectivelyVisible);
        ui.Capture("iphone-microsoft-pending");
        await ui.Shell.CloseSheetCommand.ExecuteAsync(microsoft);
        await showing;
        Assert.False(ui.Overlays.HasSheet);
    }

    [AvaloniaFact]
    public async Task Settings_explain_that_nothing_is_checked_in_the_background()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Mail.OpenSettingsCommand.Execute(null);
        await MobileHarness.Settle();
        var settings = Assert.IsType<SettingsViewModel>(ui.Overlays.CurrentSheet);
        Assert.Equal(0, settings.SelectedRefresh.Minutes); // manual by default
        var view = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(ui.View).OfType<Views.SettingsView>().Single();
        Assert.Contains("does not check for mail", view.RefreshExplanation.Text);
        await ui.Shell.CloseSheetCommand.ExecuteAsync(settings);
        Assert.False(ui.Overlays.HasSheet);
    }

    [AvaloniaFact]
    public async Task Mail_Health_opens_as_a_sheet()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Mail.OpenMailHealthCommand.Execute(null);
        await MobileHarness.Settle();
        Assert.IsType<MailHealthViewModel>(ui.Overlays.CurrentSheet);
        ui.Capture("iphone-mail-health");
        Assert.True(await ui.Shell.GoBackAsync());
        Assert.False(ui.Overlays.HasSheet);
    }

    [AvaloniaFact]
    public async Task Background_suspends_the_optional_timer_and_resume_refreshes_only_when_enabled()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        var timer = typeof(MainWindowViewModel).GetField("_autoRefresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(timer.GetValue(ui.Mail)); // manual refresh: no timer at all

        var settings = ui.Services.GetRequiredService<AppSettingsService>();
        settings.Update(s => s with { AutomaticRefreshMinutes = 5 });
        ui.Mail.ApplyRefreshSchedule();
        Assert.NotNull(timer.GetValue(ui.Mail));

        ui.Shell.OnEnteredBackground();
        Assert.True(ui.Mail.IsSuspended);
        Assert.Null(timer.GetValue(ui.Mail)); // nothing runs while iOS has the app suspended

        await ui.Shell.OnReturnedToForegroundAsync();
        Assert.False(ui.Mail.IsSuspended);
        Assert.NotNull(timer.GetValue(ui.Mail));

        settings.Update(s => s with { AutomaticRefreshMinutes = 0 });
        ui.Mail.ApplyRefreshSchedule();
        ui.Shell.OnEnteredBackground();
        await ui.Shell.OnReturnedToForegroundAsync();
        Assert.Null(timer.GetValue(ui.Mail));
    }

    [AvaloniaFact]
    public async Task Errors_and_notices_appear_as_banners_that_can_be_dismissed()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPhone);
        ui.Services.GetRequiredService<INotificationService>().Error("Could not connect", "The server did not answer.");
        await MobileHarness.Settle();
        Assert.True(ui.View.BannerHost.IsVisible);
        Assert.Equal("Could not connect", ui.View.BannerTitle.Text);
        ui.Capture("iphone-error-banner");
        ui.Overlays.DismissBannerCommand.Execute(null);
        await MobileHarness.Settle();
        Assert.False(ui.View.BannerHost.IsVisible);
    }
}
