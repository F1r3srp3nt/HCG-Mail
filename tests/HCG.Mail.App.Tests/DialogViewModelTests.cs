using Avalonia.Headless.XUnit;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HCG.Mail.App.Tests;

public class ComposeViewModelTests
{
    private static async Task<(UiHarness Ui, ComposeViewModel Compose, IDialogService Dialogs)> CreateAsync(OutgoingMessage? initial = null)
    {
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
        var ui = await UiHarness.StartAsync();
        var account = ui.ViewModel.Accounts[0];
        var compose = new ComposeViewModel(ui.ViewModel.Accounts, account, initial ?? new OutgoingMessage(),
            ui.Services.GetRequiredService<AppSettingsService>(), dialogs, Substitute.For<INotificationService>());
        return (ui, compose, dialogs);
    }

    [AvaloniaFact]
    public async Task Invalid_recipients_block_sending()
    {
        var (ui, compose, _) = await CreateAsync();
        await using var _ui = ui;
        compose.To = "valid@example.com, not an address";
        compose.Subject = "Hi";
        await compose.SendCommand.ExecuteAsync(null);
        Assert.True(compose.StatusIsError);
        Assert.Contains("not an address", compose.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Missing_recipients_block_sending()
    {
        var (ui, compose, _) = await CreateAsync();
        await using var _ui = ui;
        compose.Subject = "Hi";
        await compose.SendCommand.ExecuteAsync(null);
        Assert.Equal("Add at least one recipient.", compose.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Sending_in_preview_closes_and_lands_in_sample_sent_folder()
    {
        var (ui, compose, _) = await CreateAsync();
        await using var _ui = ui;
        var closed = false;
        compose.CloseRequested += (_, _) => closed = true;
        compose.To = "Kari Example <kari@example.org>";
        compose.ShowCcBcc = true;
        compose.Bcc = "hidden@example.org";
        compose.Subject = "Status update";
        compose.Body = "All good.";

        await compose.SendCommand.ExecuteAsync(null);
        Assert.True(closed);

        var sent = ui.ViewModel.Accounts[0].FindByRole(FolderRole.Sent)!;
        var page = await ui.ViewModel.Accounts[0].Session!.GetMessagesAsync(sent.FullName, 0, 50);
        Assert.Contains(page.Messages, m => m.Subject == "Status update");
    }

    [AvaloniaFact]
    public async Task Draft_save_keeps_window_open_and_replaces_previous_version()
    {
        var (ui, compose, _) = await CreateAsync();
        await using var _ui = ui;
        compose.To = "kari@example.org";
        compose.Subject = "Draft v1";
        await compose.SaveDraftCommand.ExecuteAsync(null);
        Assert.False(compose.IsDirty);
        Assert.Contains("Draft saved", compose.StatusMessage);

        compose.Subject = "Draft v2";
        Assert.True(compose.IsDirty);
        await compose.SaveDraftCommand.ExecuteAsync(null);

        var drafts = ui.ViewModel.Accounts[0].FindByRole(FolderRole.Drafts)!;
        var page = await ui.ViewModel.Accounts[0].Session!.GetMessagesAsync(drafts.FullName, 0, 50);
        Assert.Equal(["Draft v2"], page.Messages.Select(m => m.Subject));
    }

    [AvaloniaFact]
    public async Task Reply_prefills_and_builds_threaded_message()
    {
        var original = new MessageContent
        {
            Ref = new MessageRef("INBOX", 1, 1),
            From = new EmailAddress("Alice", "alice@example.com"),
            To = [new EmailAddress(null, "preview.user@example.com")],
            Subject = "Plan",
            MessageId = "orig@example.com",
            TextBody = "Original",
        };
        var reply = ReplyBuilder.Reply(original, ["preview.user@example.com"], replyAll: false);
        var (ui, compose, _) = await CreateAsync(reply);
        await using var _ui = ui;

        Assert.Equal("Alice <alice@example.com>", compose.To);
        Assert.Equal("Re: Plan", compose.Subject);
        var built = compose.BuildMessage(out var invalid);
        Assert.Empty(invalid);
        Assert.Equal("orig@example.com", built.InReplyTo);
        Assert.NotNull(built.MessageId);
        UiHarness.Capture(await new ComposeWindow { DataContext = compose, Width = 900, Height = 680 }.ShowAndSettleAsync(), "05-compose-reply");
    }
}

public class AccountSetupViewModelTests
{
    private static AccountSetupViewModel Create(IConnectionTester? tester = null, ICredentialVault? vault = null, List<MailAccount>? saved = null, MailAccount? existing = null) =>
        new(tester ?? Substitute.For<IConnectionTester>(), vault ?? new SessionCredentialVault(),
            a => { saved?.Add(a); return Task.CompletedTask; }, existing);

    [AvaloniaFact]
    public void Domene_preset_is_default_and_uses_secure_ports()
    {
        var vm = Create();
        Assert.Equal(ProviderPresets.DomeneNo, vm.SelectedPreset);
        Assert.Equal("993", vm.ImapPort);
        Assert.Equal(MailSecurityMode.SslTls, vm.ImapSecurity.Mode);
        Assert.Equal("465", vm.SmtpPort);
        Assert.Equal(MailSecurityMode.SslTls, vm.SmtpSecurity.Mode);
        Assert.Equal("", vm.ImapHost);
    }

    [AvaloniaFact]
    public void Username_follows_email_until_edited()
    {
        var vm = Create();
        vm.EmailAddress = "ola@firma.no";
        Assert.Equal("ola@firma.no", vm.Username);
        vm.Username = "ola";
        vm.EmailAddress = "ola.nordmann@firma.no";
        Assert.Equal("ola", vm.Username);
    }

    [AvaloniaFact]
    public void Changing_security_switches_default_port()
    {
        var vm = Create();
        vm.SmtpSecurity = SecurityOption.All.First(o => o.Mode == MailSecurityMode.StartTls);
        Assert.Equal("587", vm.SmtpPort);
        vm.ImapSecurity = SecurityOption.All.First(o => o.Mode == MailSecurityMode.StartTls);
        Assert.Equal("143", vm.ImapPort);
    }

    [AvaloniaFact]
    public async Task Save_validates_then_stores_password_only_in_vault()
    {
        var vault = new SessionCredentialVault();
        var saved = new List<MailAccount>();
        var vm = Create(vault: vault, saved: saved);
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(saved);
        Assert.NotNull(vm.EmailError);
        Assert.NotNull(vm.ImapHostError);

        vm.EmailAddress = "ola@firma.no";
        vm.DisplayName = "Ola";
        vm.Password = "pw-123";
        vm.ImapHost = "mail.firma.no";
        Assert.Equal("mail.firma.no", vm.SmtpHost);
        await vm.SaveCommand.ExecuteAsync(null);

        var account = Assert.Single(saved);
        Assert.Equal("domene.no", account.PresetId);
        Assert.Equal("pw-123", await vault.GetAsync(account.CredentialKey));
        Assert.Equal("", vm.Password);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task Connection_test_reports_each_step()
    {
        var tester = Substitute.For<IConnectionTester>();
        tester.RunAsync(default!, default!, default, default).ReturnsForAnyArgs(ci =>
        {
            var steps = Enum.GetValues<ConnectionTestStepKind>()
                .Select(k => new ConnectionTestStep(k, k == ConnectionTestStepKind.ImapAuthentication ? ConnectionTestStatus.Failed : ConnectionTestStatus.Passed, $"{k} detail"))
                .ToList();
            foreach (var s in steps)
                ci.Arg<IProgress<ConnectionTestStep>>()?.Report(s);
            return Task.FromResult(new ConnectionTestReport(steps));
        });
        var vm = Create(tester);
        vm.EmailAddress = "ola@firma.no";
        vm.Password = "pw";
        vm.ImapHost = "mail.firma.no";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(6, vm.Steps.Count);
        Assert.True(vm.Steps.Single(s => s.Kind == ConnectionTestStepKind.ImapAuthentication).IsFailed);
        Assert.True(vm.Steps.Single(s => s.Kind == ConnectionTestStepKind.Smtp).IsPassed);
        Assert.False(vm.TestSucceeded);
        await tester.Received(1).RunAsync(Arg.Is<MailAccount>(a => a.Imap.Host == "mail.firma.no" && a.Imap.Port == 993), "pw", Arg.Any<IProgress<ConnectionTestStep>>(), Arg.Any<CancellationToken>());

        UiHarness.Capture(await new AccountSetupWindow { DataContext = vm, Width = 1000, Height = 760 }.ShowAndSettleAsync(), "06-account-setup");
    }

    [AvaloniaFact]
    public void Credential_note_is_honest_about_session_only_storage()
    {
        var vm = Create(vault: new SessionCredentialVault());
        Assert.Contains("this session only", vm.CredentialStorageNote);
    }
}

public class SettingsTests
{
    [AvaloniaFact]
    public async Task Settings_changes_are_persisted_and_theme_applies()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-ui-settings-").FullName;
        try
        {
            var store = new JsonAppSettingsStore(new AppPaths(dir), Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonAppSettingsStore>.Instance);
            await using var ui = await UiHarness.StartAsync(s => s.AddSingleton<IAppSettingsStore>(store));
            var vm = new SettingsViewModel(
                ui.Services.GetRequiredService<AppSettingsService>(),
                ui.Services.GetRequiredService<ThemeService>(),
                Substitute.For<IDialogService>(),
                ui.Services.GetRequiredService<AppPaths>(),
                ui.Services.GetRequiredService<ICredentialVault>(),
                ui.ViewModel);

            vm.SelectedTheme = vm.Themes.Single(t => t.Kind == ThemeKind.HcgClassic);
            vm.SelectedRefresh = vm.RefreshOptions.Single(r => r.Minutes == 0);
            vm.NotifyOnNewMail = false;

            var persisted = store.Load();
            Assert.Equal(ThemeKind.HcgClassic, persisted.Theme);
            Assert.Equal(0, persisted.RefreshIntervalMinutes);
            Assert.False(persisted.NotifyOnNewMail);
            Assert.StartsWith("Manual refresh", ui.ViewModel.StatusText);

            UiHarness.Capture(await new SettingsWindow { DataContext = vm, Width = 900, Height = 660 }.ShowAndSettleAsync(), "07-settings-classic");
            vm.SelectedTheme = vm.Themes.Single(t => t.Kind == ThemeKind.HcgFuture);
            await UiHarness.Settle(150);
            UiHarness.Capture(await new SettingsWindow { DataContext = vm, Width = 900, Height = 660 }.ShowAndSettleAsync(), "08-settings-future");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

internal static class WindowTestExtensions
{
    public static async Task<T> ShowAndSettleAsync<T>(this T window) where T : Avalonia.Controls.Window
    {
        window.Show();
        await UiHarness.Settle(250);
        return window;
    }
}
