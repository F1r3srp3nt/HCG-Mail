using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>Account removal must work in every connection state and never touch the server or other accounts.</summary>
public sealed class AccountRemovalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-mail-removal-").FullName;
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly FakeSessionFactory _sessions = new();

    public AccountRemovalTests()
    {
        _dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppPaths Paths => new(_dir);

    private static MailAccount Account(string id, string email) => new()
    {
        Id = id,
        DisplayName = email,
        EmailAddress = email,
        Username = email,
        Imap = new ServerEndpoint("mail.hakmicapitalgroup.example", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("mail.hakmicapitalgroup.example", 465, MailSecurityMode.SslTls),
    };

    private async Task<(ServiceProvider Services, MainWindowViewModel Vm)> StartAsync(ICredentialVault vault, IAccountStore? accountStore = null, bool connectOnStartup = true)
    {
        var settingsStore = new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance);
        if (!File.Exists(Paths.SettingsFile))
            settingsStore.Save(new AppSettings { ConnectOnStartup = connectOnStartup });
        var services = AppServices.Build(new AppMode(IsPreview: false), s =>
        {
            s.AddSingleton(Paths);
            s.AddSingleton(vault);
            s.AddSingleton(accountStore ?? new JsonAccountStore(Paths, NullLogger<JsonAccountStore>.Instance));
            s.AddSingleton<IAppSettingsStore>(settingsStore);
            s.AddSingleton<IMailSessionFactory>(_sessions);
            s.AddSingleton(_dialogs);
            s.AddSingleton(_notifications);
        });
        var vm = services.GetRequiredService<MainWindowViewModel>();
        await vm.InitializeAsync();
        await UiHarness.Settle(150);
        return (services, vm);
    }

    private void SeedAccounts(params MailAccount[] accounts) =>
        new JsonAccountStore(Paths, NullLogger<JsonAccountStore>.Instance).Save(accounts);

    private IReadOnlyList<MailAccount> StoredAccounts() =>
        new JsonAccountStore(Paths, NullLogger<JsonAccountStore>.Instance).Load();

    private static async Task<SessionCredentialVault> VaultWith(params MailAccount[] accounts)
    {
        var vault = new SessionCredentialVault();
        foreach (var a in accounts)
            await vault.SetAsync(a.CredentialKey, "pw-" + a.Id);
        return vault;
    }

    private async Task AssertRemovedCleanlyAsync(MainWindowViewModel vm, ICredentialVault vault, MailAccount account)
    {
        Assert.DoesNotContain(vm.Accounts, a => a.Account.Id == account.Id);
        Assert.DoesNotContain(StoredAccounts(), a => a.Id == account.Id);
        Assert.Null(await vault.GetAsync(account.CredentialKey));
        _notifications.Received().Success("Account removed", Arg.Any<string>(), Arg.Any<Action?>());
        Assert.Empty(new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance).Load().PendingCredentialDeletions);
    }

    [AvaloniaFact]
    public void Destructive_confirmation_dialog_opens_and_uses_the_danger_style()
    {
        // Regression: the dialog used to throw InvalidCastException in its constructor, which made
        // "Remove account", "Delete permanently" and "Discard" fail with "Something went wrong".
        var dialog = new ConfirmDialog("Remove this account?", "Message", "Remove account", null, "Cancel", destructive: true);
        dialog.Show();
        var icon = dialog.FindControl<Controls.IconView>("DialogIcon")!;
        Assert.Contains("destructive", icon.Classes);
        Assert.NotNull(icon.Data);
        Assert.True(dialog.TryFindResource("Hcg.Danger", dialog.ActualThemeVariant, out var danger));
        Assert.Equal(((ISolidColorBrush)danger!).Color, ((ISolidColorBrush)icon.Foreground!).Color);
        UiHarness.Capture(dialog, "30-remove-account-confirmation");
        dialog.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Account_dialog_closes_only_when_the_account_was_removed(bool removed)
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        var setup = new AccountSetupViewModel(Substitute.For<IConnectionTester>(), new SessionCredentialVault(), _ => Task.CompletedTask, a, _ => Task.FromResult(removed));
        var closed = false;
        setup.CloseRequested += (_, _) => closed = true;
        await setup.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(removed, closed);
    }

    [AvaloniaFact]
    public async Task Removing_a_connected_account()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var vault = await VaultWith(a);
        var (services, vm) = await StartAsync(vault);
        Assert.True(vm.Accounts[0].IsConnected);
        var session = _sessions.Created.Single();

        Assert.True(await vm.RemoveAccountAsync(a));

        await AssertRemovedCleanlyAsync(vm, vault, a);
        Assert.True(session.Disposed);
        Assert.Null(vm.SelectedAccount);
        Assert.True(vm.ShowWelcome);
        Assert.Equal(0, session.ServerWritesAfterConnect); // nothing was deleted or changed on the server
        await services.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Removing_an_offline_account()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var vault = await VaultWith(a);
        _sessions.ConnectFailure = new MailOperationException(MailErrorKind.Network, "No network connection is available.");
        var (services, vm) = await StartAsync(vault);
        Assert.True(vm.Accounts[0].HasError);

        Assert.True(await vm.RemoveAccountAsync(a));
        await AssertRemovedCleanlyAsync(vm, vault, a);
        await services.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Removing_an_account_with_a_tls_failure()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var vault = await VaultWith(a);
        _sessions.ConnectFailure = new MailOperationException(MailErrorKind.RevocationUnavailable, "revocation status could not be checked (CRL yr1.c.lencr.org)");
        var (services, vm) = await StartAsync(vault);
        Assert.True(vm.Accounts[0].HasError);

        Assert.True(await vm.RemoveAccountAsync(a));
        await AssertRemovedCleanlyAsync(vm, vault, a);
        await services.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Removing_an_account_with_invalid_credentials()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var vault = await VaultWith(a);
        _sessions.ConnectFailure = new MailOperationException(MailErrorKind.Authentication, "The incoming (IMAP) server rejected the username or password.");
        var (services, vm) = await StartAsync(vault);

        Assert.True(await vm.RemoveAccountAsync(a));
        await AssertRemovedCleanlyAsync(vm, vault, a);
        await services.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Removing_an_account_that_never_synchronized()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var vault = new SessionCredentialVault(); // no password ever stored
        var (services, vm) = await StartAsync(vault, connectOnStartup: false);
        Assert.Null(vm.Accounts[0].Session);
        Assert.Empty(_sessions.Created);

        Assert.True(await vm.RemoveAccountAsync(a));
        await AssertRemovedCleanlyAsync(vm, vault, a);
        await services.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Removing_one_account_does_not_affect_another()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        var b = Account("b", "post@other.example");
        SeedAccounts(a, b);
        var vault = await VaultWith(a, b);
        var (services, vm) = await StartAsync(vault);
        vm.SelectedAccount = vm.Accounts.Single(x => x.Account.Id == "a");

        Assert.True(await vm.RemoveAccountAsync(a));

        var remaining = Assert.Single(vm.Accounts);
        Assert.Equal("b", remaining.Account.Id);
        Assert.True(remaining.IsConnected);
        Assert.Same(remaining, vm.SelectedAccount);
        Assert.Equal(["b"], StoredAccounts().Select(x => x.Id));
        Assert.Equal("pw-b", await vault.GetAsync(b.CredentialKey));
        Assert.False(_sessions.Created.Single(s => s.Account.Id == "b").Disposed);
        await services.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Cancelling_the_confirmation_changes_nothing()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var vault = await VaultWith(a);
        _dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(false);
        var (services, vm) = await StartAsync(vault);

        Assert.False(await vm.RemoveAccountAsync(a));
        Assert.Single(vm.Accounts);
        Assert.Single(StoredAccounts());
        Assert.Equal("pw-a", await vault.GetAsync(a.CredentialKey));
        await services.DisposeAsync();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Credential_store_failure_is_reported_honestly_and_retried_after_restart(bool vaultException)
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var inner = await VaultWith(a);
        var failing = new FailingDeleteVault(inner, vaultException
            ? new CredentialVaultException("Could not remove the password from the Keychain (OSStatus -25293).")
            : new InvalidOperationException("Keychain unavailable"));
        var (services, vm) = await StartAsync(failing);

        Assert.True(await vm.RemoveAccountAsync(a));

        Assert.Empty(vm.Accounts);
        Assert.Empty(StoredAccounts());
        Assert.Equal("pw-a", await inner.GetAsync(a.CredentialKey)); // genuinely still stored
        _notifications.DidNotReceive().Success("Account removed", Arg.Any<string>(), Arg.Any<Action?>());
        _notifications.Received().Warning("Account removed — password not deleted", Arg.Is<string>(m => m.Contains("will try again")));
        Assert.Equal([a.CredentialKey], new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance).Load().PendingCredentialDeletions);
        await services.DisposeAsync();

        // Restart with a working credential store: the leftover password is deleted automatically.
        failing.Fail = false;
        var (services2, vm2) = await StartAsync(failing);
        Assert.Empty(vm2.Accounts);
        Assert.Null(await inner.GetAsync(a.CredentialKey));
        Assert.Empty(new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance).Load().PendingCredentialDeletions);
        await services2.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Removal_persists_across_restart()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        var b = Account("b", "post@other.example");
        SeedAccounts(a, b);
        var vault = await VaultWith(a, b);
        var (services, vm) = await StartAsync(vault);
        Assert.True(await vm.RemoveAccountAsync(a));
        await vm.DisposeAsync();
        await services.DisposeAsync();

        var (services2, vm2) = await StartAsync(vault);
        Assert.Equal(["b"], vm2.Accounts.Select(x => x.Account.Id));
        await services2.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Removal_does_not_wait_for_a_connection_that_will_not_close()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        SeedAccounts(a);
        var vault = await VaultWith(a);
        _sessions.HangOnDispose = true;
        var previous = MainWindowViewModel.SessionShutdownTimeout;
        MainWindowViewModel.SessionShutdownTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var (services, vm) = await StartAsync(vault);
            var started = DateTime.UtcNow;
            Assert.True(await vm.RemoveAccountAsync(a));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            await AssertRemovedCleanlyAsync(vm, vault, a);
            _sessions.ReleaseHungDisposals();
            await services.DisposeAsync();
        }
        finally
        {
            MainWindowViewModel.SessionShutdownTimeout = previous;
        }
    }

    [AvaloniaFact]
    public async Task Unwritable_accounts_file_leaves_everything_unchanged()
    {
        var a = Account("a", "founder@hakmicapitalgroup.example");
        var vault = await VaultWith(a);
        var store = Substitute.For<IAccountStore>();
        store.Load().Returns([a]);
        var (services, vm) = await StartAsync(vault, store);
        store.When(s => s.Save(Arg.Any<IReadOnlyList<MailAccount>>())).Do(_ => throw new IOException("disk full"));

        Assert.False(await vm.RemoveAccountAsync(a));
        Assert.Single(vm.Accounts);
        Assert.Equal("pw-a", await vault.GetAsync(a.CredentialKey));
        _notifications.Received().Error("Account not removed", Arg.Is<string>(m => m.Contains("disk full")));
        await services.DisposeAsync();
    }

    [AvaloniaFact]
    public void Unexpected_error_message_names_the_failure_and_log_location_without_details()
    {
        var message = App.UnexpectedErrorMessage(new InvalidCastException("secret-looking detail"), "/Users/ola/Library/Application Support/HCG Mail/logs");
        Assert.Contains("InvalidCastException", message);
        Assert.Contains("/Users/ola/Library/Application Support/HCG Mail/logs", message);
        Assert.DoesNotContain("secret-looking detail", message);
    }

    // ---- fakes --------------------------------------------------------------------------------------

    private sealed class FailingDeleteVault(ICredentialVault inner, Exception error) : ICredentialVault
    {
        public bool Fail { get; set; } = true;
        public bool IsPersistent => inner.IsPersistent;
        public string StorageDescription => "macOS Keychain";
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);
        public Task SetAsync(string key, string secret, CancellationToken ct = default) => inner.SetAsync(key, secret, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => Fail ? Task.FromException(error) : inner.DeleteAsync(key, ct);
    }

    private sealed class FakeSessionFactory : IMailSessionFactory
    {
        private readonly TaskCompletionSource _release = new();
        public List<FakeSession> Created { get; } = [];
        public MailOperationException? ConnectFailure { get; set; }
        public bool HangOnDispose { get; set; }

        public IMailSession Create(MailAccount account, string password)
        {
            var session = new FakeSession(account, ConnectFailure, HangOnDispose ? _release.Task : Task.CompletedTask);
            Created.Add(session);
            return session;
        }

        public void ReleaseHungDisposals() => _release.TrySetResult();
    }

    private sealed class FakeSession(MailAccount account, MailOperationException? connectFailure, Task disposeGate) : IMailSession
    {
        public MailAccount Account { get; } = account;
        public MailConnectionState State { get; private set; }
        public bool Disposed { get; private set; }
        public int ServerWritesAfterConnect { get; private set; }
        public event EventHandler<MailConnectionState>? StateChanged;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            if (connectFailure is not null)
            {
                State = MailConnectionState.Failed;
                StateChanged?.Invoke(this, State);
                throw connectFailure;
            }
            State = MailConnectionState.Connected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([new MailFolder("INBOX", "INBOX", '/', FolderRole.Inbox, true, 0, 0, 0)]);

        public Task<FolderPage> GetMessagesAsync(string folder, int skip, int count, CancellationToken ct = default) =>
            Task.FromResult(new FolderPage(folder, [], 0, 0, 1, false));

        public Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetFlagsAsync(IReadOnlyList<MessageRef> m, MessageFlags f, bool add, CancellationToken ct = default) { ServerWritesAfterConnect++; return Task.CompletedTask; }
        public Task DeleteAsync(MessageRef m, bool permanent, CancellationToken ct = default) { ServerWritesAfterConnect++; return Task.CompletedTask; }
        public Task MoveAsync(MessageRef m, string target, CancellationToken ct = default) { ServerWritesAfterConnect++; return Task.CompletedTask; }
        public Task SaveAttachmentAsync(MessageRef m, int i, Stream d, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> GetAttachmentBytesAsync(MessageRef m, int i, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<SendResult> SendAsync(OutgoingMessage m, bool save, CancellationToken ct = default) { ServerWritesAfterConnect++; return Task.FromResult(new SendResult(false, null)); }
        public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage m, CancellationToken ct = default) { ServerWritesAfterConnect++; return Task.FromResult(new DraftSaveResult(null)); }
        public Task<OutgoingMessage> LoadDraftAsync(MessageRef d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            await disposeGate;
            Disposed = true;
        }
    }
}
