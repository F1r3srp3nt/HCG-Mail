using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Headless;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Settings;
using HCG.Mail.Email;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Email.Imap;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Network;
using HCG.Mail.Infrastructure.Storage;
using HCG.Mail.TestSupport;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.App.Tests;

/// <summary>
/// Restrictive networks, Wi-Fi drops, sleep and silently dead connections, against the disposable local Dovecot/Postfix
/// server (never a real mailbox). Relays between HCG Mail and the server simulate the network: refusing connections, black-
/// holing traffic like a filtering firewall, or killing connections without telling either end, as a NAT does after sleep.
/// </summary>
[Collection("local mail server relays")]
public sealed class RestrictedNetworkE2ETests
{
    private const string Password = "ola-test-password";

    private static MailAccount Account(string id, int imapPort, int smtpPort) => new()
    {
        Id = id,
        DisplayName = "Ola Test",
        EmailAddress = "ola@hcgmail.test",
        Username = "ola@hcgmail.test",
        Imap = new ServerEndpoint("mail.hcgmail.test", imapPort, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("mail.hcgmail.test", smtpPort, MailSecurityMode.SslTls),
    };

    private static Task OnUiThread(Func<Task> test) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly()).Dispatch(() =>
        {
            var task = test();
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("End-to-end network test did not complete.");
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(5);
            }
            task.GetAwaiter().GetResult();
        }, CancellationToken.None);

    private sealed class App : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required MainWindowViewModel Vm { get; init; }
        public required MainWindow Window { get; init; }
        public required ManualNetworkMonitor Network { get; init; }
        public required ManualTimeProvider Time { get; init; }
        public required string DataDir { get; init; }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Vm.DisposeAsync();
            await Services.DisposeAsync();
            Environment.SetEnvironmentVariable("HCG_MAIL_DATA_DIR", null);
            Directory.Delete(DataDir, recursive: true);
        }
    }

    private static async Task<App> StartAsync(MailAccount account)
    {
        var dataDir = Directory.CreateTempSubdirectory("hcg-mail-e2e-network-").FullName;
        Environment.SetEnvironmentVariable("HCG_MAIL_DATA_DIR", dataDir);
        var vault = new SessionCredentialVault();
        await vault.SetAsync(account.CredentialKey, Password);
        var accounts = new InMemoryAccountStore();
        accounts.Save([account]);
        var network = new ManualNetworkMonitor();
        var time = new ManualTimeProvider();
        var services = AppServices.Build(new AppMode(IsPreview: false), s =>
        {
            s.AddSingleton<ICredentialVault>(vault);
            s.AddSingleton<IAccountStore>(accounts);
            s.AddSingleton<IAppSettingsStore>(new InMemoryAppSettingsStore());
            s.AddSingleton<INetworkMonitor>(network);
            s.AddSingleton<TimeProvider>(time);
        });
        services.GetRequiredService<ThemeService>().ApplySaved();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        services.GetRequiredService<DialogService>().Attach(window);
        window.Show();
        await vm.InitializeAsync();
        await UiHarness.Settle(800);
        Assert.True(vm.Accounts[0].IsConnected, vm.Accounts[0].StatusText);
        return new App { Services = services, Vm = vm, Window = window, Network = network, Time = time, DataDir = dataDir };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit, string what)
    {
        var until = DateTime.UtcNow + limit;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Timed out waiting for: " + what);
            await UiHarness.Settle(100);
        }
    }

    private static async Task<int> CountInKariInboxAsync(string subject)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync("mail.hcgmail.test", 993, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync("kari@hcgmail.test", "kari-test-password");
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var found = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
        await imap.DisconnectAsync(true);
        return found.Count;
    }

    [LocalMailServerFact]
    public Task Wifi_drop_keeps_mail_readable_queues_sending_and_delivers_exactly_once_after_recovery() => OnUiThread(async () =>
    {
        using var imapRelay = new TcpRelay(new IPEndPoint(IPAddress.Loopback, 993), 19994);
        using var smtpRelay = new TcpRelay(new IPEndPoint(IPAddress.Loopback, 465), 19466);
        imapRelay.Restore();
        smtpRelay.Restore();
        await using var app = await StartAsync(Account("e2e-wifi-drop", imapRelay.Port, smtpRelay.Port));
        var vm = app.Vm;
        var account = vm.Accounts[0];
        Assert.NotEmpty(vm.MessageList.Items);
        vm.MessageList.SelectedItem = vm.MessageList.Items[0];
        await WaitUntilAsync(() => vm.Reader.HasMessage, TimeSpan.FromSeconds(20), "the first message to open");
        var firstSubject = vm.Reader.Content!.Subject;

        // The Wi-Fi drops.
        imapRelay.Cut();
        smtpRelay.Cut();
        await vm.RefreshCommand.ExecuteAsync(null);
        await UiHarness.Settle(400);
        Assert.True(account.IsShowingOfflineCopy, account.StatusText);
        Assert.True(vm.ShowOfflineBanner);
        Assert.Contains("Reconnecting automatically", account.RecoveryText);
        Assert.NotEmpty(vm.MessageList.Items);
        vm.MessageList.SelectedItem = null;
        await UiHarness.Settle(150);
        vm.MessageList.SelectedItem = vm.MessageList.Items[0];
        await WaitUntilAsync(() => vm.Reader.HasMessage, TimeSpan.FromSeconds(10), "the saved copy to open");
        Assert.Equal(firstSubject, vm.Reader.Content!.Subject);

        // A message written offline waits in the Outbox.
        var subject = "Offline homework " + Guid.NewGuid().ToString("N")[..8];
        var outbox = app.Services.GetRequiredService<OutboxService>();
        var compose = new ComposeViewModel([account], account, new OutgoingMessage(), app.Services.GetRequiredService<AppSettingsService>(),
            app.Services.GetRequiredService<IDialogService>(), app.Services.GetRequiredService<INotificationService>(), null, outbox)
        {
            To = "kari@hcgmail.test",
            Subject = subject,
            Body = "Written while the network was down.",
        };
        await compose.SendCommand.ExecuteAsync(null);
        Assert.Single(outbox.Items);
        Assert.Equal(0, await CountInKariInboxAsync(subject));

        // The Wi-Fi comes back.
        imapRelay.Restore();
        smtpRelay.Restore();
        app.Network.Raise(NetworkEventKind.Available, "Network connection available: Wi-Fi");
        await UiHarness.Settle(200);
        app.Time.Advance(ConnectionRecoveryService.NetworkSettleDelay);
        await WaitUntilAsync(() => account.IsConnected && outbox.Items.Count == 0, TimeSpan.FromSeconds(60), "reconnect and Outbox delivery");
        Assert.False(vm.ShowOfflineBanner);

        // Poll asynchronously: blocking the UI thread on IMAP would deadlock its own continuations.
        for (var i = 0; await CountInKariInboxAsync(subject) == 0; i++)
        {
            Assert.True(i < 60, "the message was not delivered to Kari");
            await UiHarness.Settle(500);
        }
        await vm.RefreshCommand.ExecuteAsync(null); // a later refresh never sends it again
        await UiHarness.Settle(1500);
        Assert.Equal(1, await CountInKariInboxAsync(subject));
    });

    [LocalMailServerFact]
    public Task After_sleep_a_dead_connection_is_replaced_at_once_instead_of_waiting_for_a_timeout() => OnUiThread(async () =>
    {
        using var imapRelay = new TcpRelay(new IPEndPoint(IPAddress.Loopback, 993), 19995);
        imapRelay.Restore();
        await using var app = await StartAsync(Account("e2e-sleep", imapRelay.Port, 465));
        var account = app.Vm.Accounts[0];
        var accepted = imapRelay.Accepted;

        imapRelay.KillSilently(); // the NAT mapping is gone after sleep; neither end knows
        app.Network.Raise(NetworkEventKind.Resumed, "Woke up after about 40 minutes; reconnecting");
        var clock = Stopwatch.StartNew();
        await app.Vm.RefreshCommand.ExecuteAsync(null);
        await UiHarness.Settle(200);

        Assert.True(account.IsConnected, account.StatusText);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:0.0} s (the command timeout is 45 s)");
        Assert.True(imapRelay.Accepted > accepted, "a new connection was opened");
    });

    [LocalMailServerFact]
    public Task A_connection_that_died_silently_while_idle_is_detected_within_seconds() => OnUiThread(async () =>
    {
        var idleAfter = MailKitMailSession.IdleCheckAfter;
        var idleTimeout = MailKitMailSession.IdleCheckTimeout;
        MailKitMailSession.IdleCheckAfter = TimeSpan.Zero;
        MailKitMailSession.IdleCheckTimeout = TimeSpan.FromSeconds(3);
        try
        {
            using var imapRelay = new TcpRelay(new IPEndPoint(IPAddress.Loopback, 993), 19996);
            imapRelay.Restore();
            await using var app = await StartAsync(Account("e2e-idle", imapRelay.Port, 465));
            var account = app.Vm.Accounts[0];

            imapRelay.KillSilently(); // school Wi-Fi dropped the idle connection without a reset
            var clock = Stopwatch.StartNew();
            await app.Vm.RefreshCommand.ExecuteAsync(null);
            await UiHarness.Settle(200);

            Assert.True(account.IsConnected, account.StatusText);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed.TotalSeconds:0.0} s");
        }
        finally
        {
            MailKitMailSession.IdleCheckAfter = idleAfter;
            MailKitMailSession.IdleCheckTimeout = idleTimeout;
        }
    });

    // ---- Network Doctor against the real server ----------------------------------------------------

    private sealed class ReferencePorts(bool blocked) : ITcpProbe
    {
        public Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            host is "imap.gmail.com" or "smtp.gmail.com"
                ? blocked ? throw new SocketException((int)SocketError.TimedOut) : Task.CompletedTask
                : new SystemTcpProbe().ConnectAsync(host, port, timeout, cancellationToken);
    }

    private sealed class WebWorks : IHttpProbe
    {
        public Task<HttpProbeResult> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpProbeResult(ProbeOutcome.Ok, 204, null, "", url.Scheme == "https" ? TlsOutcome.Ok : TlsOutcome.NotTested, null));
    }

    private static NetworkDoctor Doctor(bool referencesBlocked) =>
        new(new SystemDnsResolver(), new ReferencePorts(referencesBlocked), new WebWorks(), new MailClientFactory { Timeout = TimeSpan.FromSeconds(6) },
            new SystemNetworkInterfaces(), NullLogger<NetworkDoctor>.Instance, new NetworkDoctorOptions { StepTimeout = TimeSpan.FromSeconds(4), SignInTimeout = TimeSpan.FromSeconds(60) });

    [LocalMailServerFact]
    public async Task Network_doctor_tells_a_restrictive_network_apart_from_an_outage_and_a_wrong_password()
    {
        using var imapRelay = new TcpRelay(new IPEndPoint(IPAddress.Loopback, 993), 19997);
        using var smtpRelay = new TcpRelay(new IPEndPoint(IPAddress.Loopback, 465), 19467);
        imapRelay.Restore();
        smtpRelay.Restore();
        var account = Account("e2e-doctor", imapRelay.Port, smtpRelay.Port);

        var healthy = await Doctor(false).RunAsync(account, Password, null, true);
        Assert.Equal(NetworkVerdict.Healthy, healthy.Diagnosis.Verdict);
        Assert.Equal(AuthOutcome.Ok, healthy.Observations.Incoming.Auth);
        Assert.Equal(AuthOutcome.Ok, healthy.Observations.Outgoing.Auth);

        var wrong = await Doctor(false).RunAsync(account, "not-the-password", null, true);
        Assert.True(wrong.Diagnosis.Category == VerdictCategory.Credentials,
            $"{wrong.Diagnosis.Verdict}: incoming {wrong.Observations.Incoming.Auth} ({wrong.Observations.Incoming.Detail}); outgoing {wrong.Observations.Outgoing.Auth} ({wrong.Observations.Outgoing.Detail})");

        // A filtering firewall: connections are accepted but the secure handshake never completes.
        imapRelay.BlackHole();
        smtpRelay.BlackHole();
        var filtered = await Doctor(true).RunAsync(account, Password, MailErrorKindTimeout, true);
        Assert.Equal(NetworkVerdict.SecureHandshakeBlocked, filtered.Diagnosis.Verdict);
        Assert.Equal(VerdictCategory.NetworkRestriction, filtered.Diagnosis.Category);
        Assert.Equal(AuthOutcome.NotTested, filtered.Observations.Incoming.Auth); // the password was never sent

        // Mail ports closed on this network (other providers' ports too): a restriction, not the server.
        imapRelay.Open();
        smtpRelay.Open();
        imapRelay.Cut();
        smtpRelay.Cut();
        var blocked = await Doctor(true).RunAsync(account, Password, MailErrorKindTimeout, true);
        Assert.Equal(NetworkVerdict.NetworkBlocksMail, blocked.Diagnosis.Verdict);

        // Same symptoms while other mail servers answer: the provider is down.
        var outage = await Doctor(false).RunAsync(account, Password, MailErrorKindTimeout, true);
        Assert.Equal(NetworkVerdict.ServerUnreachable, outage.Diagnosis.Verdict);
        Assert.Equal(VerdictCategory.ServerProblem, outage.Diagnosis.Category);
    }

    private const HCG.Mail.Core.Errors.MailErrorKind MailErrorKindTimeout = HCG.Mail.Core.Errors.MailErrorKind.Timeout;
}
