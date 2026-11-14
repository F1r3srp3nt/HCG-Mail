using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Mail;
using HCG.Mail.Mobile.ViewModels;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace HCG.Mail.Mobile.Tests;

/// <summary>Runs only with HCG_MAIL_E2E=1 and the disposable local test server (tests/integration-server).</summary>
public sealed class LocalMailServerFactAttribute : FactAttribute
{
    public LocalMailServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HCG_MAIL_E2E") != "1")
            Skip = "Set HCG_MAIL_E2E=1 and start Windows-executable-Application/tests/integration-server/setup-local-mail-server.sh to run end-to-end tests.";
    }
}

/// <summary>
/// The complete iPhone mail workflow with production services (MailKit, TLS with revocation checks, JSON stores,
/// the platform credential vault) against the disposable local Dovecot/Postfix server — never a real mailbox:
/// add an account through the setup sheet → connect → folders and inbox → open a message → compose and send →
/// refresh → relaunch with the stored account → remove the account and its stored password.
/// The server delivers only to @hcgmail.test, so nothing can leave the machine.
/// </summary>
public sealed class RealServerWorkflowTests
{
    private const string Host = "mail.hcgmail.test";
    private const string Address = "ola@hcgmail.test";
    private const string Password = "ola-test-password";

    private static async Task<string> DeliverFromKariAsync(string token)
    {
        var message = new MimeMessage { Subject = $"Mobile E2E inbound {token}" };
        message.From.Add(new MailboxAddress("Kari Test", "kari@hcgmail.test"));
        message.To.Add(new MailboxAddress("Ola Test", Address));
        message.Body = new BodyBuilder
        {
            TextBody = $"Plain alternative {token}",
            HtmlBody = $"<html><body><h2>Quarterly note {token}</h2><p>Body text for the mobile reader.</p>" +
                       "<img src=\"https://tracker.invalid/p.gif\"><script>alert(1)</script></body></html>",
        }.ToMessageBody();
        using var imap = new ImapClient();
        await imap.ConnectAsync(Host, 993, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync(Address, Password);
        await imap.Inbox.AppendAsync(new AppendRequest(message, MailKit.MessageFlags.None));
        await imap.DisconnectAsync(true);
        return message.Subject;
    }

    [LocalMailServerFact]
    public Task Add_read_send_relaunch_and_remove_an_account_on_iPhone() => MobileHarness.OnUiThread(async () =>
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var inbound = await DeliverFromKariAsync(token);
        using var platform = new FakeMobilePlatform();

        // ---- First launch: no account yet ----------------------------------------------------------------
        await using (var ui = await MobileHarness.StartAsync(Devices.IPhone, platform, realMode: true, keepPlatform: true))
        {
            Assert.False(ui.Mail.IsPreviewMode);
            Assert.True(ui.Mail.ShowWelcome);
            Assert.True(ui.View.Welcome.IsVisible);
            ui.Capture("e2e-1-welcome");

            // Add the account through the setup sheet, as a user would.
            var adding = ui.Mail.AddAccountCommand.ExecuteAsync(null);
            await MobileHarness.WaitUntil(() => ui.Overlays.CurrentSheet is AccountSetupViewModel, "the account setup sheet");
            var setup = (AccountSetupViewModel)ui.Overlays.CurrentSheet!;
            setup.SelectedPreset = ProviderPresets.Custom;
            setup.EmailAddress = Address;
            setup.DisplayName = "Ola Test";
            setup.Username = Address;
            setup.Password = Password;
            setup.ImapHost = Host;
            setup.SmtpHost = Host;
            await setup.TestConnectionCommand.ExecuteAsync(null);
            Assert.True(setup.TestSucceeded, string.Join("; ", setup.Steps.Select(s => $"{s.Title}: {s.Detail}")));
            ui.Capture("e2e-2-connection-test");
            await setup.SaveCommand.ExecuteAsync(null);
            await adding;
            Assert.False(ui.Overlays.HasSheet);

            // Connected, folders and inbox loaded.
            var account = Assert.Single(ui.Mail.Accounts);
            await MobileHarness.WaitUntil(() => account.IsConnected && ui.Mail.MessageList.Items.Count > 0, "connection and inbox",
                20000, () => account.StatusText);
            Assert.All(FolderRoleMapper.StandardRoles, role => Assert.NotNull(account.FindByRole(role)));
            Assert.Equal(FolderRole.Inbox, ui.Mail.SelectedFolder?.Role);
            Assert.Equal(MailPage.Messages, ui.Shell.Page);
            Assert.Equal(Password, await platform.CredentialVault.GetAsync(account.Account.CredentialKey)); // in the vault, not in a file
            Assert.DoesNotContain(Password, File.ReadAllText(platform.Paths.AccountsFile), StringComparison.Ordinal);
            ui.Capture("e2e-3-inbox");

            // Open the message.
            var item = ui.Mail.MessageList.Items.First(i => i.Subject == inbound);
            ui.Mail.MessageList.SelectedItem = item;
            ui.Shell.OnMessageChosen();
            await MobileHarness.WaitUntil(() => ui.Mail.Reader.HasMessage, "the message", 15000, () => ui.Mail.Reader.ErrorMessage ?? "");
            Assert.Equal(MailPage.Reader, ui.Shell.Page);
            Assert.Contains($"Quarterly note {token}", ui.Mail.Reader.SanitizedHtml);
            Assert.DoesNotContain("<script", ui.Mail.Reader.SanitizedHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tracker.invalid", ui.Mail.Reader.SanitizedHtml);
            Assert.True(ui.Mail.Reader.ShowRemoteBanner);
            await MobileHarness.WaitUntil(() => item.IsRead, "the message to be marked read");
            await MobileHarness.Settle(300);
            ui.Capture("e2e-4-reader");
            await ui.Shell.GoBackAsync();

            // Compose and send to the same test mailbox.
            ui.Mail.ComposeCommand.Execute(null);
            await MobileHarness.Settle();
            var compose = Assert.IsType<ComposeViewModel>(ui.Overlays.CurrentSheet);
            compose.To = Address;
            compose.Subject = $"Mobile E2E outbound {token}";
            compose.Body = "Sent by the HCG Mail iOS end-to-end test to the local test server.";
            ui.Capture("e2e-5-compose");
            await compose.SendCommand.ExecuteAsync(null);
            await MobileHarness.WaitUntil(() => !ui.Overlays.HasSheet, "the composer to close after sending", 20000,
                () => compose.StatusMessage ?? "");

            // Refresh: the delivered message appears in the inbox; a copy is in Sent.
            await MobileHarness.WaitUntil(() => ui.Mail.RefreshCommand.CanExecute(null), "refresh to be available");
            for (var attempt = 0; attempt < 10 && !ui.Mail.MessageList.Items.Any(i => i.Subject == compose.Subject); attempt++)
            {
                await ui.Mail.RefreshCommand.ExecuteAsync(null);
                await MobileHarness.Settle(500);
            }
            Assert.Contains(ui.Mail.MessageList.Items, i => i.Subject == $"Mobile E2E outbound {token}");
            Assert.Contains("Last refreshed", ui.Mail.StatusText);
            ui.Mail.SelectedStandardFolder = account.FindByRole(FolderRole.Sent);
            await MobileHarness.WaitUntil(() => ui.Mail.MessageList.Items.Any(i => i.Subject == $"Mobile E2E outbound {token}"),
                "the sent copy in Sent", 15000);
            ui.Mail.SelectedStandardFolder = account.FindByRole(FolderRole.Inbox);
            await MobileHarness.Settle(500);
            ui.Capture("e2e-6-after-send");
        }

        // ---- Relaunch: the account, its stored password and the settings survive ------------------------
        await using (var ui = await MobileHarness.StartAsync(Devices.IPhone, platform, realMode: true, keepPlatform: true))
        {
            var account = Assert.Single(ui.Mail.Accounts);
            Assert.Equal(Address, account.EmailAddress);
            await MobileHarness.WaitUntil(() => account.IsConnected && ui.Mail.MessageList.Items.Count > 0,
                "reconnecting with the stored password after relaunch", 20000, () => account.StatusText);
            Assert.False(account.NeedsPassword);
            Assert.Contains(ui.Mail.MessageList.Items, i => i.Subject == $"Mobile E2E outbound {token}");
            Assert.True(ui.Mail.MessageList.Items.First(i => i.Subject == inbound).IsRead); // read state comes from the server
            ui.Capture("e2e-7-relaunched");

            // Remove the account: confirmation, then the account, its password and its history are gone.
            var removing = ui.Mail.RemoveAccountAsync(account.Account);
            await MobileHarness.WaitUntil(() => ui.Overlays.HasPrompt, "the removal confirmation");
            ui.Capture("e2e-8-remove-confirm");
            ui.Overlays.Prompt!.PrimaryCommand.Execute(null);
            Assert.True(await removing);
            await MobileHarness.Settle();
            Assert.Empty(ui.Mail.Accounts);
            Assert.True(ui.Mail.ShowWelcome);
            Assert.Null(await platform.CredentialVault.GetAsync(account.Account.CredentialKey));
            Assert.DoesNotContain(Address, File.ReadAllText(platform.Paths.AccountsFile), StringComparison.OrdinalIgnoreCase);
        }

        // The log file (operations, hosts, account ids) never contains the password or the full address.
        var logs = string.Concat(Directory.GetFiles(platform.Paths.LogDirectory).Select(File.ReadAllText));
        Assert.Contains("IMAP connected for account", logs);
        Assert.DoesNotContain(Password, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Address, logs, StringComparison.OrdinalIgnoreCase);

        // ---- Relaunch after removal: nothing is left behind -----------------------------------------------
        await using (var ui = await MobileHarness.StartAsync(Devices.IPhone, platform, realMode: true, keepPlatform: true))
        {
            Assert.Empty(ui.Mail.Accounts);
            Assert.True(ui.Mail.ShowWelcome);
        }

        // Mail on the server is untouched by removing the account.
        using var imap = new ImapClient();
        await imap.ConnectAsync(Host, 993, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync(Address, Password);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.NotEmpty(await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(token)));
        await imap.DisconnectAsync(true);
    });

    [LocalMailServerFact]
    public Task Losing_the_connection_keeps_the_list_and_shows_an_offline_notice() => MobileHarness.OnUiThread(async () =>
    {
        // IMAP goes through a relay that can be cut, like Wi-Fi dropping out or the phone going into a tunnel.
        using var relay = new TcpRelay(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 993), 29993);
        relay.Restore();
        await DeliverFromKariAsync(Guid.NewGuid().ToString("N")[..8]);
        using var platform = new FakeMobilePlatform();
        var account = new MailAccount
        {
            Id = "mobile-e2e-offline", DisplayName = "Ola Test", EmailAddress = Address, Username = Address,
            Imap = new ServerEndpoint(Host, relay.Port, MailSecurityMode.SslTls), Smtp = new ServerEndpoint(Host, 465, MailSecurityMode.SslTls),
        };
        await platform.CredentialVault.SetAsync(account.CredentialKey, Password);
        new HCG.Mail.Infrastructure.Storage.JsonAccountStore(platform.Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<HCG.Mail.Infrastructure.Storage.JsonAccountStore>.Instance).Save([account]);

        await using var ui = await MobileHarness.StartAsync(Devices.IPhone, platform, realMode: true);
        var avm = Assert.Single(ui.Mail.Accounts);
        await MobileHarness.WaitUntil(() => avm.IsConnected && ui.Mail.MessageList.Items.Count > 0, "inbox", 20000, () => avm.StatusText);
        var count = ui.Mail.MessageList.Items.Count;
        Assert.False(ui.Shell.ShowOfflineNotice);

        relay.Cut();
        await ui.Mail.RefreshCommand.ExecuteAsync(null);
        await MobileHarness.WaitUntil(() => avm.HasError, "the error state", 20000, () => avm.StatusText);
        await MobileHarness.Settle();
        Assert.True(ui.Shell.ShowOfflineNotice);
        Assert.True(ui.View.ListPane.OfflineNotice.IsVisible);
        Assert.Contains("showing messages loaded earlier", ui.Shell.OfflineNoticeText);
        Assert.Equal(count, ui.Mail.MessageList.Items.Count); // still readable offline
        Assert.False(ui.Overlays.HasBanner); // no second copy of the same message on top of the notice
        ui.Capture("e2e-offline");

        relay.Restore();
        await ui.Mail.ReconnectCommand.ExecuteAsync(null);
        await MobileHarness.WaitUntil(() => avm.IsConnected, "reconnecting", 20000, () => avm.StatusText);
        await ui.Mail.RefreshCommand.ExecuteAsync(null);
        await MobileHarness.Settle();
        Assert.False(ui.Shell.ShowOfflineNotice);
        Assert.False(ui.View.ListPane.OfflineNotice.IsVisible);
    });
}
