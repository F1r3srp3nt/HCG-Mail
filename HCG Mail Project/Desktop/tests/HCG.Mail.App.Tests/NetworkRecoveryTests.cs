using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HCG.Mail.App.Preview;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Email.Offline;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using HCG.Mail.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.App.Tests;

/// <summary>Records what the user would be told.</summary>
internal sealed class RecordingNotifications : INotificationService
{
    public List<(string Kind, string Title, string Message)> Shown { get; } = [];
    public void Info(string title, string message, Action? onClick = null) => Shown.Add(("info", title, message));
    public void Success(string title, string message, Action? onClick = null) => Shown.Add(("success", title, message));
    public void Warning(string title, string message) => Shown.Add(("warning", title, message));
    public void Error(string title, string message) => Shown.Add(("error", title, message));
}

/// <summary>Puts a switch between HCG Mail and one shared preview mailbox: "down" simulates a lost or blocked network.</summary>
internal sealed class NetworkSwitch
{
    public bool Down { get; set; }
    public MailErrorKind Kind { get; set; } = MailErrorKind.Timeout;
    public int Connects { get; set; }
}

internal sealed class SwitchedSession(IMailSession inner, NetworkSwitch sw) : IMailSession
{
    private Task Check()
    {
        if (sw.Down)
            throw new MailOperationException(sw.Kind, sw.Kind == MailErrorKind.Timeout
                ? "Connecting to 'imap.example.com' on port 993 timed out. A firewall may be blocking the port, or the server may be unavailable."
                : "No network connection is available. Check your internet connection.");
        return Task.CompletedTask;
    }

    private async Task<T> Do<T>(Func<Task<T>> f)
    {
        await Check();
        return await f();
    }

    private async Task Do(Func<Task> f)
    {
        await Check();
        await f();
    }

    public MailAccount Account => inner.Account;
    public MailConnectionState State => inner.State;
    public event EventHandler<MailConnectionState>? StateChanged { add => inner.StateChanged += value; remove => inner.StateChanged -= value; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        sw.Connects++;
        return Do(() => inner.ConnectAsync(ct));
    }

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken ct = default) => Do(() => inner.GetFoldersAsync(ct));
    public Task<FolderPage> GetMessagesAsync(string f, int skip, int count, CancellationToken ct = default) => Do(() => inner.GetMessagesAsync(f, skip, count, ct));
    public Task<IReadOnlyList<MessageSummary>> SearchAsync(string f, string q, int max, CancellationToken ct = default) => Do(() => inner.SearchAsync(f, q, max, ct));
    public Task<MessageContent> GetMessageAsync(MessageRef m, CancellationToken ct = default) => Do(() => inner.GetMessageAsync(m, ct));
    public Task SetFlagsAsync(IReadOnlyList<MessageRef> m, MessageFlags flags, bool add, CancellationToken ct = default) => Do(() => inner.SetFlagsAsync(m, flags, add, ct));
    public Task DeleteAsync(MessageRef m, bool permanent, CancellationToken ct = default) => Do(() => inner.DeleteAsync(m, permanent, ct));
    public Task MoveAsync(MessageRef m, string target, CancellationToken ct = default) => Do(() => inner.MoveAsync(m, target, ct));
    public Task SaveAttachmentAsync(MessageRef m, int i, Stream d, CancellationToken ct = default) => Do(() => inner.SaveAttachmentAsync(m, i, d, ct));
    public Task<byte[]> GetAttachmentBytesAsync(MessageRef m, int i, CancellationToken ct = default) => Do(() => inner.GetAttachmentBytesAsync(m, i, ct));
    public Task<SendResult> SendAsync(OutgoingMessage m, bool save, CancellationToken ct = default) => Do(() => inner.SendAsync(m, save, ct));
    public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage m, CancellationToken ct = default) => Do(() => inner.SaveDraftAsync(m, ct));
    public Task<OutgoingMessage> LoadDraftAsync(MessageRef d, CancellationToken ct = default) => Do(() => inner.LoadDraftAsync(d, ct));
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Connectivity loss and recovery in the real application object graph (headless): the offline copy and banner instead of a
/// silent stop, the Outbox, bounded automatic reconnection on a manual clock, recovery when the network returns or changes,
/// and the Network Doctor in Mail Health.
/// </summary>
public sealed class NetworkRecoveryTests
{
    private sealed record Rig(UiHarness Ui, NetworkSwitch Switch, ManualNetworkMonitor Network, ManualTimeProvider Time, PreviewMailSession Mailbox, string Dir) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Ui.DisposeAsync();
            Directory.Delete(Dir, recursive: true);
        }
    }

    private static async Task<Rig> StartAsync(Action<IServiceCollection>? more = null)
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-recovery-").FullName;
        var sw = new NetworkSwitch();
        var network = new ManualNetworkMonitor();
        var time = new ManualTimeProvider();
        var mailbox = new PreviewMailSession(PreviewMailSession.SampleAccount);
        var ui = await UiHarness.StartAsync(s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddSingleton<INetworkMonitor>(network);
            s.AddSingleton<IMailCache>(sp => new JsonMailCache(new AppPaths(dir), () => OfflineCachePolicy.Default,
                new HCG.Mail.Infrastructure.Security.AccountDataProtector(new SessionCredentialVault(), NullLogger<HCG.Mail.Infrastructure.Security.AccountDataProtector>.Instance),
                NullLogger<JsonMailCache>.Instance));
            s.AddSingleton<IMailSessionFactory>(sp => new OfflineCapableSessionFactory(new SharedMailboxFactory(mailbox, sw),
                sp.GetRequiredService<IMailCache>(), network, sp.GetRequiredService<ILoggerFactory>()));
            more?.Invoke(s);
        });
        await UiHarness.Settle(900);
        return new Rig(ui, sw, network, time, mailbox, dir);
    }

    private sealed class SharedMailboxFactory(PreviewMailSession mailbox, NetworkSwitch sw) : IMailSessionFactory
    {
        public IMailSession Create(MailAccount account, string password) => new SwitchedSession(mailbox, sw);
    }

    private static async Task AdvanceAsync(ManualTimeProvider time, TimeSpan by, int settleMs = 900)
    {
        time.Advance(by);
        await UiHarness.Settle(settleMs);
    }

    [AvaloniaFact]
    public async Task Losing_the_network_shows_the_offline_copy_queues_mail_and_recovers_automatically()
    {
        await using var rig = await StartAsync();
        var vm = rig.Ui.ViewModel;
        var account = vm.SelectedAccount!;
        Assert.True(account.IsConnected);
        Assert.NotEmpty(vm.MessageList.Items);
        var first = vm.MessageList.Items[0];
        vm.MessageList.SelectedItem = first;
        await UiHarness.Settle(600);
        Assert.NotNull(vm.Reader.Content);
        var subject = vm.Reader.Content!.Subject;

        // School Wi-Fi drops the mail connection: Refresh does not stop silently.
        rig.Switch.Down = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        await UiHarness.Settle(600);

        Assert.True(account.IsShowingOfflineCopy);
        Assert.True(vm.ShowOfflineBanner);
        Assert.StartsWith("Offline — showing mail saved on this device", vm.OfflineBannerTitle);
        Assert.Contains("timed out", vm.OfflineBannerText);
        Assert.Contains("Reconnecting automatically at", vm.OfflineBannerText);
        Assert.NotEmpty(vm.MessageList.Items); // mail stays readable
        Assert.InRange(rig.Time.NextDue!.Value.TotalSeconds, 4, 6); // first retry after about 5 s
        var banner = rig.Ui.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OfflineBanner");
        Assert.True(banner.IsEffectivelyVisible);
        UiHarness.Capture(rig.Ui.Window, "beta2-offline-banner");

        // The message read earlier opens from the copy.
        vm.MessageList.SelectedItem = null;
        await UiHarness.Settle(200);
        vm.MessageList.SelectedItem = vm.MessageList.Items.First(i => i.Ref == first.Ref);
        await UiHarness.Settle(600);
        Assert.Equal(subject, vm.Reader.Content?.Subject);

        // A message written now waits in the Outbox.
        var outbox = rig.Ui.Services.GetRequiredService<OutboxService>();
        var compose = new ComposeViewModel([account], account, new OutgoingMessage(), rig.Ui.Services.GetRequiredService<AppSettingsService>(),
            rig.Ui.Services.GetRequiredService<IDialogService>(), rig.Ui.Services.GetRequiredService<INotificationService>(), null, outbox)
        {
            To = "kari@example.com",
            Subject = "Homework for Monday",
            Body = "Page 42.",
        };
        var closed = false;
        compose.CloseRequested += (_, _) => closed = true;
        await compose.SendCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal(OutboxState.Waiting, Assert.Single(outbox.Items).State);
        Assert.Equal("1 message in the Outbox", vm.OutboxText);

        // Bounded backoff: the automatic attempt fails while still down, and the next pause is longer.
        var connects = rig.Switch.Connects;
        await AdvanceAsync(rig.Time, TimeSpan.FromSeconds(6));
        Assert.Equal(connects + 1, rig.Switch.Connects);
        // The second pause is 10 s ± 20 %, counted from the moment the first retry fired (up to 2 s before the clock's end).
        Assert.InRange(rig.Time.NextDue!.Value.TotalSeconds, 6, 12);
        Assert.Contains("(attempt 2)", vm.SelectedAccount!.RecoveryText);

        // The network comes back: reconnect within seconds, send the Outbox, leave offline mode.
        rig.Switch.Down = false;
        rig.Network.Raise(NetworkEventKind.Available, "Network connection available: Wi-Fi (wlan0)");
        await UiHarness.Settle(200);
        await AdvanceAsync(rig.Time, ConnectionRecoveryService.NetworkSettleDelay, 1800);

        Assert.True(account.IsConnected);
        Assert.False(account.IsShowingOfflineCopy);
        Assert.False(vm.ShowOfflineBanner);
        Assert.Empty(outbox.Items);
        var sent = await rig.Mailbox.GetMessagesAsync("Sent", 0, 50);
        Assert.Contains(sent.Messages, m => m.Subject == "Homework for Monday");
        Assert.Equal(0, rig.Time.PendingTimers);
    }

    [AvaloniaFact]
    public async Task Without_any_network_nothing_is_attempted_until_a_network_appears()
    {
        await using var rig = await StartAsync();
        var vm = rig.Ui.ViewModel;
        var account = vm.SelectedAccount!;
        rig.Switch.Down = true;
        rig.Switch.Kind = MailErrorKind.Network;
        rig.Network.Raise(NetworkEventKind.Unavailable, "Network connection lost");
        await vm.RefreshCommand.ExecuteAsync(null);
        await UiHarness.Settle(500);

        Assert.Equal(AccountStatus.Error, account.Status);
        Assert.Contains("Waiting for a network connection", account.RecoveryText);
        Assert.Equal(0, rig.Time.PendingTimers);
        var connects = rig.Switch.Connects;
        await AdvanceAsync(rig.Time, TimeSpan.FromMinutes(30), 300);
        Assert.Equal(connects, rig.Switch.Connects);

        rig.Switch.Down = false;
        rig.Network.Raise(NetworkEventKind.Available, "Wi-Fi back");
        await UiHarness.Settle(200);
        await AdvanceAsync(rig.Time, TimeSpan.FromSeconds(2), 1200);
        Assert.True(account.IsConnected);
    }

    [AvaloniaFact]
    public async Task Waking_from_sleep_reconnects_a_failed_account_at_once()
    {
        await using var rig = await StartAsync();
        var vm = rig.Ui.ViewModel;
        var account = vm.SelectedAccount!;
        rig.Switch.Down = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        await UiHarness.Settle(400);
        Assert.Equal(AccountStatus.Error, account.Status);

        rig.Switch.Down = false;
        rig.Network.Raise(NetworkEventKind.Resumed, "Woke up after about 45 minutes; reconnecting");
        await UiHarness.Settle(200);
        Assert.Contains("reconnecting now", account.RecoveryText);
        await AdvanceAsync(rig.Time, TimeSpan.FromSeconds(2), 1200);
        Assert.True(account.IsConnected);
    }

    [AvaloniaFact]
    public async Task A_refused_password_is_explained_and_never_retried_automatically()
    {
        var factory = new SwitchableFactory(MailErrorKind.Authentication);
        var settings = new AppSettingsService(new InMemoryAppSettingsStore());
        var time = new ManualTimeProvider();
        var network = new ManualNetworkMonitor();
        using var recovery = new ConnectionRecoveryService(network, time, settings, NullLogger<ConnectionRecoveryService>.Instance, post: a => a());
        var vault = new SessionCredentialVault();
        var account = new AccountViewModel(Account, factory, vault, NullLogger.Instance);
        await vault.SetAsync(Account.CredentialKey, "wrong");
        var reconnects = 0;
        recovery.Start(a => { reconnects++; return a.ConnectAsync(); });
        recovery.Track(account);

        await account.ConnectAsync();
        Assert.Equal(AccountStatus.Error, account.Status);
        Assert.Contains("does not retry a refused password", account.RecoveryText);
        Assert.Equal(0, time.PendingTimers);
        network.Raise(NetworkEventKind.Changed, "Other Wi-Fi");
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, reconnects);
    }

    [AvaloniaFact]
    public async Task Backoff_grows_to_the_cap_and_resets_after_success()
    {
        var factory = new SwitchableFactory(MailErrorKind.Timeout);
        var settings = new AppSettingsService(new InMemoryAppSettingsStore());
        var time = new ManualTimeProvider();
        using var recovery = new ConnectionRecoveryService(new ManualNetworkMonitor(), time, settings, NullLogger<ConnectionRecoveryService>.Instance,
            random: () => 0.5, post: a => a());
        var vault = new SessionCredentialVault();
        await vault.SetAsync(Account.CredentialKey, "pw");
        var account = new AccountViewModel(Account, factory, vault, NullLogger.Instance);
        recovery.Start(a => a.ConnectAsync());
        recovery.Track(account);

        await account.ConnectAsync();
        var delays = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            var due = time.NextDue!.Value;
            delays.Add(due.TotalSeconds);
            time.Advance(due);
            await Task.Delay(20);
        }
        Assert.Equal([5, 10, 20, 40, 80, 160, 300, 300, 300], delays);

        factory.FailWith = null;
        time.Advance(time.NextDue!.Value);
        await Task.Delay(50);
        Assert.True(account.IsConnected);
        Assert.Equal(0, recovery.AttemptsFor(account));
        Assert.Null(account.RecoveryText);
        Assert.Equal(0, time.PendingTimers);
    }

    [AvaloniaFact]
    public async Task Turning_automatic_reconnection_off_is_respected()
    {
        var factory = new SwitchableFactory(MailErrorKind.Network);
        var settings = new AppSettingsService(new InMemoryAppSettingsStore(new HCG.Mail.Core.Settings.AppSettings { ReconnectAutomatically = false }));
        var time = new ManualTimeProvider();
        using var recovery = new ConnectionRecoveryService(new ManualNetworkMonitor(), time, settings, NullLogger<ConnectionRecoveryService>.Instance, post: a => a());
        var vault = new SessionCredentialVault();
        await vault.SetAsync(Account.CredentialKey, "pw");
        var account = new AccountViewModel(Account, factory, vault, NullLogger.Instance);
        recovery.Start(a => a.ConnectAsync());
        recovery.Track(account);
        await account.ConnectAsync();
        Assert.Contains("turned off", account.RecoveryText);
        Assert.Equal(0, time.PendingTimers);
    }

    [AvaloniaFact]
    public async Task A_network_change_marks_open_connections_stale_so_the_next_action_reconnects_at_once()
    {
        var factory = new SwitchableFactory(null);
        var settings = new AppSettingsService(new InMemoryAppSettingsStore());
        var network = new ManualNetworkMonitor();
        using var recovery = new ConnectionRecoveryService(network, new ManualTimeProvider(), settings, NullLogger<ConnectionRecoveryService>.Instance, post: a => a());
        var vault = new SessionCredentialVault();
        await vault.SetAsync(Account.CredentialKey, "pw");
        var account = new AccountViewModel(Account, factory, vault, NullLogger.Instance);
        recovery.Start(a => a.ConnectAsync());
        recovery.Track(account);
        Assert.True(await account.ConnectAsync());

        network.Raise(NetworkEventKind.Changed, "Wi-Fi → mobile hotspot");
        network.Raise(NetworkEventKind.Resumed, "Woke up");
        Assert.Equal(2, factory.Last!.StaleMarks);
    }

    private static readonly MailAccount Account = new()
    {
        Id = "recovery-test",
        DisplayName = "Student",
        EmailAddress = "student@example.no",
        Username = "student@example.no",
        Imap = new ServerEndpoint("imap.example.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.example.no", 465, MailSecurityMode.SslTls),
    };

    private sealed class SwitchableFactory(MailErrorKind? failWith) : IMailSessionFactory
    {
        public MailErrorKind? FailWith { get; set; } = failWith;
        public SwitchableSession? Last { get; private set; }

        public IMailSession Create(MailAccount account, string password)
        {
            Last = new SwitchableSession(account) { FailWith = FailWith };
            return new OfflineCapableMailSession(Last, new NoMailCache(), new ManualNetworkMonitor(), NullLogger.Instance);
        }
    }

    // ---- Network Doctor in Mail Health ------------------------------------------------------------

    private sealed class AllResolve : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") });
    }

    private sealed class MailPortsBlocked : ITcpProbe
    {
        public Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new SocketException((int)SocketError.TimedOut);
    }

    private sealed class WebWorks : IHttpProbe
    {
        public Task<HttpProbeResult> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpProbeResult(ProbeOutcome.Ok, 204, null, "", url.Scheme == "https" ? TlsOutcome.Ok : TlsOutcome.NotTested, null));
    }

    [AvaloniaFact]
    public async Task Network_doctor_in_mail_health_explains_a_school_network_that_blocks_mail()
    {
        await using var rig = await StartAsync(s =>
        {
            s.AddSingleton<IDnsResolver, AllResolve>();
            s.AddSingleton<ITcpProbe, MailPortsBlocked>();
            s.AddSingleton<IHttpProbe, WebWorks>();
            s.AddSingleton<INetworkInterfaces>(new FixedInterfaces());
        });
        var vm = rig.Ui.ViewModel;
        rig.Network.Raise(NetworkEventKind.Changed, "Network changed: Wi-Fi (wlan0)");
        var health = rig.Ui.Services.GetRequiredService<MailHealthViewModelFactory>().Create(vm.SelectedAccount!, vm.ApplyOutgoingServerAsync,
            rig.Ui.Services.GetRequiredService<OutboxService>());
        health.StartOnNetworkDoctor = true;
        var window = new MailHealthWindow { DataContext = health, Width = 1000, Height = 760 };
        window.Show();
        await health.Network!.RunCommand.ExecuteAsync(null);
        await UiHarness.Settle(500);

        Assert.Equal("This network appears to block email connections", health.Network.Headline);
        Assert.Equal("Likely cause", health.Network.ConfidenceLabel);
        Assert.True(health.Network.IsWarning); // not shown as a confirmed problem
        Assert.Equal("Network restriction", health.Network.CategoryLabel);
        Assert.Contains(health.Network.Rows, r => r.Title == "Mail ports on this network" && r.IsProblem);
        Assert.Contains(health.Network.Events, e => e.Description.Contains("Wi-Fi (wlan0)"));
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal("Network Doctor", (tabs.SelectedItem as TabItem)?.Header);
        var report = health.Network.BuildReportText();
        Assert.DoesNotContain("preview.user@", report); // addresses keep only their domain
        Assert.Contains("NetworkBlocksMail", report);
        UiHarness.Capture(window, "beta2-network-doctor");
        window.Close();
    }

    private sealed class FixedInterfaces : INetworkInterfaces
    {
        public IReadOnlyList<NetworkInterfaceInfo> GetActive() => [new("wlan0", "School Wi-Fi adapter", "Wi-Fi", true, false)];
    }
}

/// <summary>The Outbox never loses a message and never sends one twice by itself.</summary>
public sealed class OutboxServiceTests
{
    private static readonly MailAccount Account = new()
    {
        Id = "outbox-test",
        DisplayName = "Student",
        EmailAddress = "student@example.no",
        Username = "student@example.no",
        Imap = new ServerEndpoint("imap.example.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.example.no", 465, MailSecurityMode.SslTls),
    };

    private sealed class OneSessionFactory(SwitchableSession session) : IMailSessionFactory
    {
        public IMailSession Create(MailAccount account, string password) => session;
    }

    private static async Task<(AccountViewModel Account, SwitchableSession Session)> ConnectedAsync()
    {
        var session = new SwitchableSession(Account);
        var vault = new SessionCredentialVault();
        await vault.SetAsync(Account.CredentialKey, "pw");
        var vm = new AccountViewModel(Account, new OneSessionFactory(session), vault, NullLogger.Instance);
        Assert.True(await vm.ConnectAsync());
        return (vm, session);
    }

    private static OutgoingMessage Message(string subject, string? id = null) =>
        new() { To = [new EmailAddress(null, "kari@example.no")], Subject = subject, TextBody = "x", MessageId = id ?? Guid.NewGuid().ToString("N") + "@example.no" };

    [AvaloniaFact]
    public async Task Waiting_messages_are_sent_in_order_when_the_account_reconnects()
    {
        var (account, session) = await ConnectedAsync();
        var notes = new RecordingNotifications();
        var outbox = new OutboxService(new InMemoryOutboxStore(), notes, NullLogger<OutboxService>.Instance);
        outbox.Enqueue(Account.Id, Message("First"), OutboxKind.Send, true);
        outbox.Enqueue(Account.Id, Message("Second"), OutboxKind.Send, true);

        Assert.Equal(2, await outbox.FlushAsync(account));
        Assert.Equal(["First", "Second"], session.Sent.Select(m => m.Subject));
        Assert.Empty(outbox.Items);
        Assert.Equal(2, notes.Shown.Count(n => n.Title == "Sent from the Outbox"));
    }

    [AvaloniaFact]
    public async Task An_uncertain_send_is_held_for_review_and_never_repeated()
    {
        var (account, session) = await ConnectedAsync();
        var outbox = new OutboxService(new InMemoryOutboxStore(), new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        outbox.Enqueue(Account.Id, Message("Maybe sent"), OutboxKind.Send, true);
        session.OutcomeUnknownOnSend = true;

        await outbox.FlushAsync(account);
        var item = Assert.Single(outbox.Items);
        Assert.Equal(OutboxState.NeedsReview, item.State);
        Assert.Contains("check your Sent folder", item.LastError);

        session.OutcomeUnknownOnSend = false;
        await outbox.FlushAsync(account);
        await outbox.FlushAsync(account);
        Assert.Empty(session.Sent); // only the user can send it again
        outbox.Requeue(item);
        await outbox.FlushAsync(account);
        Assert.Single(session.Sent);
    }

    [AvaloniaFact]
    public async Task A_connection_failure_keeps_the_message_waiting()
    {
        var (account, session) = await ConnectedAsync();
        var outbox = new OutboxService(new InMemoryOutboxStore(), new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        outbox.Enqueue(Account.Id, Message("Later"), OutboxKind.Send, true);
        session.FailWith = MailErrorKind.Timeout;
        Assert.Equal(0, await outbox.FlushAsync(account));
        var item = Assert.Single(outbox.Items);
        Assert.Equal(OutboxState.Waiting, item.State);
        Assert.Equal(1, item.Attempts);
    }

    [AvaloniaFact]
    public async Task A_rejected_recipient_is_kept_as_failed_for_the_user()
    {
        var (account, session) = await ConnectedAsync();
        var notes = new RecordingNotifications();
        var outbox = new OutboxService(new InMemoryOutboxStore(), notes, NullLogger<OutboxService>.Instance);
        outbox.Enqueue(Account.Id, Message("Bad address"), OutboxKind.Send, true);
        session.FailWith = MailErrorKind.ServerRejected;
        await outbox.FlushAsync(account);
        Assert.Equal(OutboxState.Failed, Assert.Single(outbox.Items).State);
        Assert.Contains(notes.Shown, n => n.Kind == "error");
    }

    [AvaloniaFact]
    public void A_message_interrupted_by_a_crash_needs_review_after_restart()
    {
        var store = new InMemoryOutboxStore();
        store.Put(new OutboxItem("abc", Account.Id, OutboxKind.Send, Message("Crashed"), DateTimeOffset.Now, OutboxState.Sending));
        var outbox = new OutboxService(store, new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        outbox.RecoverInterrupted();
        Assert.Equal(OutboxState.NeedsReview, Assert.Single(store.Load()).State);
    }

    [AvaloniaFact]
    public async Task A_newer_draft_replaces_the_older_one_and_sending_supersedes_drafts()
    {
        var (account, session) = await ConnectedAsync();
        var outbox = new OutboxService(new InMemoryOutboxStore(), new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        outbox.Enqueue(Account.Id, Message("Draft v1", "same@example.no"), OutboxKind.Draft, false);
        outbox.Enqueue(Account.Id, Message("Draft v2", "same@example.no"), OutboxKind.Draft, false);
        Assert.Equal("Draft v2", Assert.Single(outbox.Items).Message.Subject);
        outbox.Enqueue(Account.Id, Message("Final", "same@example.no"), OutboxKind.Send, true);
        await outbox.FlushAsync(account);
        Assert.Empty(session.Drafts);
        Assert.Equal("Final", Assert.Single(session.Sent).Subject);
    }
}

/// <summary>Writing mail while offline: nothing is lost and nothing is sent twice.</summary>
public sealed class ComposeOfflineTests
{
    private static readonly MailAccount Account = new()
    {
        Id = "compose-offline",
        DisplayName = "Student",
        EmailAddress = "student@example.no",
        Username = "student@example.no",
        Imap = new ServerEndpoint("imap.example.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.example.no", 465, MailSecurityMode.SslTls),
    };

    private sealed class OneSessionFactory(IMailSession session) : IMailSessionFactory
    {
        public IMailSession Create(MailAccount account, string password) => session;
    }

    private static async Task<(ComposeViewModel Compose, OutboxService Outbox, SwitchableSession Inner)> ComposeAsync(MailErrorKind? failOnSend, bool unknownOutcome = false)
    {
        var inner = new SwitchableSession(Account);
        var session = new OfflineCapableMailSession(inner, new NoMailCache(), new ManualNetworkMonitor(), NullLogger.Instance);
        var vault = new SessionCredentialVault();
        await vault.SetAsync(Account.CredentialKey, "pw");
        var account = new AccountViewModel(Account, new OneSessionFactory(session), vault, NullLogger.Instance);
        Assert.True(await account.ConnectAsync());
        inner.FailWith = failOnSend;
        inner.OutcomeUnknownOnSend = unknownOutcome;
        var outbox = new OutboxService(new InMemoryOutboxStore(), new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        var compose = new ComposeViewModel([account], account, new OutgoingMessage(), new AppSettingsService(new InMemoryAppSettingsStore()),
            new AutoConfirmPrompts(), new RecordingNotifications(), null, outbox)
        {
            To = "kari@example.no",
            Subject = "Test",
            Body = "Hello",
        };
        return (compose, outbox, inner);
    }

    private sealed class AutoConfirmPrompts : IUserPrompts
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) => Task.FromResult(true);
        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string cancelText = "Cancel") => Task.FromResult(DialogChoice.Primary);
        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSaveFileAsync(string suggestedFileName, string? startDirectory) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? startDirectory) => Task.FromResult<string?>(null);
    }

    [AvaloniaTheory]
    [InlineData(MailErrorKind.Timeout)]
    [InlineData(MailErrorKind.Network)]
    [InlineData(MailErrorKind.Certificate)] // e.g. a captive portal in the way: never sent over it, kept for later
    public async Task A_send_that_never_reached_the_server_goes_to_the_outbox(MailErrorKind kind)
    {
        var (compose, outbox, inner) = await ComposeAsync(kind);
        var closed = false;
        compose.CloseRequested += (_, _) => closed = true;
        await compose.SendCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal("Test", Assert.Single(outbox.Items).Message.Subject);
        Assert.Empty(inner.Sent);
    }

    [AvaloniaFact]
    public async Task A_send_that_may_have_reached_the_server_is_not_queued()
    {
        var (compose, outbox, _) = await ComposeAsync(null, unknownOutcome: true);
        await compose.SendCommand.ExecuteAsync(null);
        Assert.Empty(outbox.Items);
        Assert.True(compose.PossiblyAlreadySent);
    }

    [AvaloniaFact]
    public async Task A_refused_password_is_reported_not_queued()
    {
        var (compose, outbox, _) = await ComposeAsync(MailErrorKind.Authentication);
        await compose.SendCommand.ExecuteAsync(null);
        Assert.Empty(outbox.Items);
        Assert.StartsWith("Not sent:", compose.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Drafts_are_kept_on_the_device_while_offline()
    {
        var (compose, outbox, inner) = await ComposeAsync(MailErrorKind.Timeout);
        Assert.True(await compose.AutosaveAsync());
        Assert.Equal(OutboxKind.Draft, Assert.Single(outbox.Items).Kind);
        Assert.Contains("draft saved on this device", compose.StatusMessage);
        Assert.Empty(inner.Drafts);
        Assert.False(compose.IsDirty);
    }
}

/// <summary>
/// Recovery safety at the application level: a refused password is never tried again by itself, an uncertain send is never
/// repeated by any recovery path, and queued mail always leaves through the account it was written from.
/// </summary>
public sealed class RecoverySafetyUiTests
{
    private static MailAccount Account(string id) => new()
    {
        Id = id,
        DisplayName = id,
        EmailAddress = id + "@example.no",
        Username = id + "@example.no",
        Imap = new ServerEndpoint("imap.example.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.example.no", 465, MailSecurityMode.SslTls),
    };

    private sealed class CountingFactory(MailErrorKind? failWith) : IMailSessionFactory
    {
        public int Created { get; private set; }
        public List<SwitchableSession> Sessions { get; } = [];
        public MailErrorKind? FailWith { get; set; } = failWith;

        public IMailSession Create(MailAccount account, string password)
        {
            Created++;
            var s = new SwitchableSession(account) { FailWith = FailWith };
            Sessions.Add(s);
            return new OfflineCapableMailSession(s, new NoMailCache(), new ManualNetworkMonitor(), NullLogger.Instance);
        }
    }

    [AvaloniaFact]
    public async Task A_refused_password_is_never_retried_by_network_changes_wake_up_or_time()
    {
        var factory = new CountingFactory(MailErrorKind.Authentication);
        var network = new ManualNetworkMonitor();
        var time = new ManualTimeProvider();
        using var recovery = new ConnectionRecoveryService(network, time, new AppSettingsService(new InMemoryAppSettingsStore()),
            NullLogger<ConnectionRecoveryService>.Instance, post: a => a());
        var vault = new SessionCredentialVault();
        var account = new AccountViewModel(Account("auth"), factory, vault, NullLogger.Instance);
        await vault.SetAsync(account.Account.CredentialKey, "wrong");
        recovery.Start(a => a.ConnectAsync());
        recovery.Track(account);

        await account.ConnectAsync();
        foreach (var kind in new[] { NetworkEventKind.Unavailable, NetworkEventKind.Available, NetworkEventKind.Changed, NetworkEventKind.Resumed })
            network.Raise(kind);
        time.Advance(TimeSpan.FromHours(6));
        await Task.Delay(50);
        Assert.Equal(1, factory.Created);                 // exactly the one sign-in the user started
        Assert.Equal(1, factory.Sessions[0].Calls);
    }

    [AvaloniaFact]
    public async Task A_refused_password_stops_the_outbox_after_one_attempt()
    {
        var factory = new CountingFactory(null);
        var vault = new SessionCredentialVault();
        var account = new AccountViewModel(Account("auth-outbox"), factory, vault, NullLogger.Instance);
        await vault.SetAsync(account.Account.CredentialKey, "pw");
        Assert.True(await account.ConnectAsync());
        var outbox = new OutboxService(new InMemoryOutboxStore(), new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        for (var i = 0; i < 3; i++)
            outbox.Enqueue(account.Account.Id, new OutgoingMessage { To = [new EmailAddress(null, "kari@example.no")], Subject = "m" + i, MessageId = $"m{i}@x" }, OutboxKind.Send, true);
        factory.Sessions[0].FailWith = MailErrorKind.Authentication;
        var calls = factory.Sessions[0].Calls;

        await outbox.FlushAsync(account);
        Assert.Equal(calls + 1, factory.Sessions[0].Calls); // not once per queued message
    }

    [AvaloniaFact]
    public async Task An_uncertain_send_is_never_repeated_by_recovery_after_network_changes()
    {
        var factory = new CountingFactory(null);
        var network = new ManualNetworkMonitor();
        var time = new ManualTimeProvider();
        var settings = new AppSettingsService(new InMemoryAppSettingsStore());
        using var recovery = new ConnectionRecoveryService(network, time, settings, NullLogger<ConnectionRecoveryService>.Instance, post: a => a());
        var vault = new SessionCredentialVault();
        var account = new AccountViewModel(Account("ambiguous"), factory, vault, NullLogger.Instance);
        await vault.SetAsync(account.Account.CredentialKey, "pw");
        var outbox = new OutboxService(new InMemoryOutboxStore(), new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        recovery.Start(async a =>
        {
            if (await a.ConnectAsync())
                await outbox.FlushAsync(a);
        });
        recovery.Track(account);
        Assert.True(await account.ConnectAsync());

        outbox.Enqueue(account.Account.Id, new OutgoingMessage { To = [new EmailAddress(null, "kari@example.no")], Subject = "Once", MessageId = "once@x" }, OutboxKind.Send, true);
        factory.Sessions[^1].OutcomeUnknownOnSend = true;
        await outbox.FlushAsync(account);
        Assert.Equal(OutboxState.NeedsReview, Assert.Single(outbox.Items).State);

        // The connection drops and comes back several times; every recovery path flushes the Outbox.
        foreach (var s in factory.Sessions)
            s.OutcomeUnknownOnSend = false;
        factory.FailWith = null;
        account.ReportError(new MailOperationException(MailErrorKind.Network, "lost"));
        for (var i = 0; i < 3; i++)
        {
            network.Raise(NetworkEventKind.Changed);
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(30);
            account.ReportError(new MailOperationException(MailErrorKind.Network, "lost"));
        }
        Assert.Empty(factory.Sessions.SelectMany(s => s.Sent));
        Assert.Equal(OutboxState.NeedsReview, Assert.Single(outbox.Items).State);
    }

    [AvaloniaFact]
    public async Task Queued_mail_always_leaves_through_the_account_it_was_written_from()
    {
        var factoryA = new CountingFactory(null);
        var factoryB = new CountingFactory(null);
        var vault = new SessionCredentialVault();
        var a = new AccountViewModel(Account("alice"), factoryA, vault, NullLogger.Instance);
        var b = new AccountViewModel(Account("bob"), factoryB, vault, NullLogger.Instance);
        await vault.SetAsync(a.Account.CredentialKey, "pw");
        await vault.SetAsync(b.Account.CredentialKey, "pw");
        Assert.True(await a.ConnectAsync());
        Assert.True(await b.ConnectAsync());
        var outbox = new OutboxService(new InMemoryOutboxStore(), new RecordingNotifications(), NullLogger<OutboxService>.Instance);
        outbox.Enqueue("alice", new OutgoingMessage { Subject = "From Alice", MessageId = "a@x" }, OutboxKind.Send, true);
        outbox.Enqueue("bob", new OutgoingMessage { Subject = "From Bob", MessageId = "b@x" }, OutboxKind.Send, true);

        await outbox.FlushAsync(a);
        Assert.Equal(["From Alice"], factoryA.Sessions.SelectMany(s => s.Sent).Select(m => m.Subject));
        Assert.Empty(factoryB.Sessions.SelectMany(s => s.Sent));
        Assert.Equal("bob", Assert.Single(outbox.Items).AccountId);

        outbox.RemoveAccount("bob");
        Assert.Empty(outbox.Items);
        await outbox.FlushAsync(b);
        Assert.Empty(factoryB.Sessions.SelectMany(s => s.Sent));
    }
}
