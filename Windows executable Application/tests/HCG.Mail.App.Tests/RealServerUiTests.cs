using System.Reflection;
using Avalonia.Headless;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Utils;

namespace HCG.Mail.App.Tests;

public sealed class LocalMailServerFactAttribute : FactAttribute
{
    public LocalMailServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HCG_MAIL_E2E") != "1")
            Skip = "Set HCG_MAIL_E2E=1 and start tests/integration-server/setup-local-mail-server.sh to run end-to-end tests.";
    }
}

/// <summary>
/// The real application (production services, MailKit, sanitizer, HTML renderer) against the
/// disposable local Dovecot/Postfix server — never a real mailbox.
/// </summary>
public class RealServerUiTests
{
    private static readonly MailAccount Ola = new()
    {
        Id = "e2e-ui-ola",
        DisplayName = "Ola Test",
        EmailAddress = "ola@hcgmail.test",
        Username = "ola@hcgmail.test",
        Imap = new ServerEndpoint("mail.hcgmail.test", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("mail.hcgmail.test", 465, MailSecurityMode.SslTls),
        PresetId = ProviderPresets.DomeneNo.Id,
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HCG.Mail.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    /// <summary>Runs an async test body on the headless UI thread, pumping the dispatcher until it completes.</summary>
    private static Task OnUiThread(Func<Task> test) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly()).Dispatch(() =>
        {
            var task = test();
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("End-to-end UI test did not complete.");
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(5);
            }
            task.GetAwaiter().GetResult();
        }, CancellationToken.None);

    private static async Task<string> AppendHtmlMessageAsync()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var builder = new BodyBuilder { TextBody = "Text alternative" };
        var logo = builder.LinkedResources.Add("chart.png", File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icons", "hcg-mail-1024.png")));
        logo.ContentId = MimeUtils.GenerateMessageId();
        builder.HtmlBody = $"""
            <html><body style="font-family:Arial;color:#1a1f24">
            <h2 style="color:#0b5c7a">Portfolio summary {token}</h2>
            <p>Inline chart (embedded, shown):</p><img src="cid:{logo.ContentId}" width="96" height="96">
            <p>Tracking pixel (remote, blocked): <img src="https://tracker.invalid/p.gif?u=ola"></p>
            <p><a href="https://www.example.com/">example.com</a></p><script>document.write('x')</script>
            </body></html>
            """;
        builder.Attachments.Add("summary.csv", "a,b\n1,2\n"u8.ToArray());
        var message = new MimeMessage
        {
            Subject = $"HTML E2E {token}",
            Body = builder.ToMessageBody(),
        };
        message.From.Add(new MailboxAddress("Kari Test", "kari@hcgmail.test"));
        message.To.Add(new MailboxAddress("Ola Test", "ola@hcgmail.test"));

        using var imap = new ImapClient();
        await imap.ConnectAsync("mail.hcgmail.test", 993, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync("ola@hcgmail.test", "ola-test-password");
        await imap.Inbox.AppendAsync(new AppendRequest(message, MailKit.MessageFlags.None));
        await imap.DisconnectAsync(true);
        return message.Subject;
    }

    [LocalMailServerFact]
    public Task Real_account_loads_inbox_and_renders_html_safely() => OnUiThread(async () =>
    {
        var subject = await AppendHtmlMessageAsync();
        var dataDir = Directory.CreateTempSubdirectory("hcg-mail-e2e-ui-").FullName;
        Environment.SetEnvironmentVariable("HCG_MAIL_DATA_DIR", dataDir);
        var vault = new SessionCredentialVault();
        await vault.SetAsync(Ola.CredentialKey, "ola-test-password");
        var accounts = new InMemoryAccountStore();
        accounts.Save([Ola]);

        var services = AppServices.Build(new AppMode(IsPreview: false), s =>
        {
            s.AddSingleton<ICredentialVault>(vault);
            s.AddSingleton<IAccountStore>(accounts);
            s.AddSingleton<IAppSettingsStore>(new InMemoryAppSettingsStore());
        });
        try
        {
            services.GetRequiredService<ThemeService>().ApplySaved();
            var vm = services.GetRequiredService<MainWindowViewModel>();
            Assert.False(vm.IsPreviewMode);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            services.GetRequiredService<DialogService>().Attach(window);
            window.Show();
            await vm.InitializeAsync();
            await UiHarness.Settle(800);

            var account = Assert.Single(vm.Accounts);
            Assert.True(account.IsConnected, account.StatusText);
            Assert.Equal(FolderRole.Inbox, vm.SelectedFolder?.Role);
            Assert.All(FolderRoleMapper.StandardRoles, role => Assert.NotNull(account.FindByRole(role)));

            var item = vm.MessageList.Items.First(i => i.Summary.Subject == subject);
            Assert.True(item.HasAttachments);
            vm.MessageList.SelectedItem = item;
            await UiHarness.Settle(1500);

            Assert.True(vm.Reader.HasMessage, vm.Reader.ErrorMessage);
            Assert.True(vm.Reader.ShowHtmlView);
            Assert.True(vm.Reader.ShowRemoteBanner);
            Assert.DoesNotContain("tracker.invalid", vm.Reader.SanitizedHtml);
            Assert.DoesNotContain("document.write", vm.Reader.SanitizedHtml);
            Assert.Single(vm.Reader.InlineResources);
            Assert.Equal("summary.csv", Assert.Single(vm.Reader.Attachments).Name);
            Assert.True(item.IsRead); // marked read on the server after opening
            UiHarness.Capture(window, "09-real-server-html");

            // New mail arrives on the server: nothing changes until the user presses Refresh.
            var later = await AppendHtmlMessageAsync();
            await UiHarness.Settle(1500);
            Assert.DoesNotContain(vm.MessageList.Items, i => i.Summary.Subject == later);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Contains(vm.MessageList.Items, i => i.Summary.Subject == later);
            Assert.Contains("Last refreshed", vm.StatusText);

            window.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            await services.DisposeAsync();
            Environment.SetEnvironmentVariable("HCG_MAIL_DATA_DIR", null);
            Directory.Delete(dataDir, recursive: true);
        }
    });

    [LocalMailServerFact]
    public Task App_recovers_after_network_interruption() => OnUiThread(async () =>
    {
        // IMAP goes through a relay we can cut, simulating Wi-Fi dropping out.
        using var relay = new TcpRelay(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 993), 19993);
        relay.Restore();
        var account = Ola with { Id = "e2e-ui-net", Imap = Ola.Imap with { Port = relay.Port } };

        var dataDir = Directory.CreateTempSubdirectory("hcg-mail-e2e-net-").FullName;
        Environment.SetEnvironmentVariable("HCG_MAIL_DATA_DIR", dataDir);
        var vault = new SessionCredentialVault();
        await vault.SetAsync(account.CredentialKey, "ola-test-password");
        var accounts = new InMemoryAccountStore();
        accounts.Save([account]);
        var services = AppServices.Build(new AppMode(IsPreview: false), s =>
        {
            s.AddSingleton<ICredentialVault>(vault);
            s.AddSingleton<IAccountStore>(accounts);
            s.AddSingleton<IAppSettingsStore>(new InMemoryAppSettingsStore());
        });
        try
        {
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            services.GetRequiredService<DialogService>().Attach(window);
            window.Show();
            await vm.InitializeAsync();
            await UiHarness.Settle(500);
            var avm = vm.Accounts[0];
            Assert.True(avm.IsConnected, avm.StatusText);
            var count = vm.MessageList.Items.Count;
            Assert.True(count > 0);

            relay.Cut();
            await vm.RefreshCommand.ExecuteAsync(null);
            await UiHarness.Settle(300);
            Assert.True(avm.HasError, $"expected error state, got {avm.Status}: {avm.StatusText}");
            Assert.Equal(count, vm.MessageList.Items.Count); // the list stays visible while offline

            relay.Restore();
            await vm.RefreshCommand.ExecuteAsync(null);
            await UiHarness.Settle(500);
            Assert.True(avm.IsConnected, avm.StatusText);
            Assert.Equal(count, vm.MessageList.Items.Count);

            // The list must be using the new session: further refreshes and reads work.
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(avm.IsConnected, avm.StatusText);
            vm.MessageList.SelectedItem = vm.MessageList.Items[0];
            await UiHarness.Settle(1200);
            Assert.True(vm.Reader.HasMessage, vm.Reader.ErrorMessage);

            window.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            await services.DisposeAsync();
            Environment.SetEnvironmentVariable("HCG_MAIL_DATA_DIR", null);
            Directory.Delete(dataDir, recursive: true);
        }
    });
}
