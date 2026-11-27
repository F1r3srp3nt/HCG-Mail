using Avalonia.Headless.XUnit;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
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

/// <summary>Adding, reconnecting and removing Microsoft accounts (fake sign-in; no browser, no Microsoft servers).</summary>
public sealed class MicrosoftAccountTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-mail-ms-").FullName;
    private readonly IMicrosoftAuth _auth = Substitute.For<IMicrosoftAuth>();
    private readonly IMailSessionFactory _sessions = Substitute.For<IMailSessionFactory>();
    private readonly List<MailAccount> _connected = [];

    public MicrosoftAccountTests()
    {
        _auth.IsConfigured.Returns(true);
        _sessions.Create(Arg.Any<MailAccount>(), Arg.Any<string>()).Returns(_ => WorkingSession());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IMailSession WorkingSession()
    {
        var session = Substitute.For<IMailSession>();
        session.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        session.GetFoldersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<MailFolder>>([]));
        return session;
    }

    private static MailAccount MicrosoftAccount(string id, string user, string key) => new()
    {
        Id = id, DisplayName = user, EmailAddress = user, Username = user,
        Imap = MailAccount.GraphEndpoint, Smtp = MailAccount.GraphEndpoint,
        Provider = MailProviderKind.MicrosoftGraph, MicrosoftAccountId = key,
    };

    private MicrosoftAccountViewModel Dialog(MailAccount? existing = null, params string[] keysInUse) =>
        new(_auth, _sessions, a =>
        {
            _connected.Add(a);
            return Task.CompletedTask;
        }, key => keysInUse.Contains(key), existing);

    [AvaloniaFact]
    public async Task Sign_in_adds_a_microsoft_account_without_storing_any_secret()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns(new MicrosoftSignIn("oid.tid", "kari@contoso.test", "Kari Nordmann", "tid"));
        var vm = Dialog();
        var closed = false;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.SignInCommand.ExecuteAsync(null);

        var account = Assert.Single(_connected);
        Assert.Equal((MailProviderKind.MicrosoftGraph, "oid.tid", "kari@contoso.test", "Kari Nordmann"),
            (account.Provider, account.MicrosoftAccountId, account.EmailAddress, account.DisplayName));
        Assert.True(closed);
        await _auth.DidNotReceiveWithAnyArgs().SignOutAsync(default!);
    }

    [AvaloniaFact]
    public async Task Cancelled_sign_in_adds_nothing_and_says_so()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns<MicrosoftSignIn>(_ => throw new MailOperationException(MailErrorKind.Cancelled, "cancelled"));
        var vm = Dialog();
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Empty(_connected);
        Assert.Equal("Sign-in was cancelled. No account was added.", vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Cancel_button_stops_a_pending_browser_sign_in()
    {
        var pending = new TaskCompletionSource<MicrosoftSignIn>();
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Register(() => pending.TrySetException(new MailOperationException(MailErrorKind.Cancelled, "cancelled")));
            return pending.Task;
        });
        var vm = Dialog();
        var signIn = vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.Equal("Cancel sign-in", vm.CancelText);

        vm.CancelCommand.Execute(null);
        await signIn;
        Assert.Empty(_connected);
        Assert.Contains("cancelled", vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task Reconnecting_as_a_different_person_is_refused_and_their_tokens_are_removed()
    {
        var existing = MicrosoftAccount("m1", "kari@contoso.test", "key-kari");
        _auth.SignInAsync("kari@contoso.test", Arg.Any<CancellationToken>()).Returns(new MicrosoftSignIn("key-ola", "ola@contoso.test", "Ola", "tid"));
        var vm = Dialog(existing, "key-kari");

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Empty(_connected);
        Assert.Contains("ola@contoso.test", vm.ErrorText);
        Assert.Contains("kari@contoso.test", vm.ErrorText);
        await _auth.Received(1).SignOutAsync("key-ola");
    }

    [AvaloniaFact]
    public async Task Adding_the_same_account_twice_is_refused_without_touching_its_tokens()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns(new MicrosoftSignIn("key-kari", "kari@contoso.test", null, "tid"));
        var vm = Dialog(null, "key-kari");
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Empty(_connected);
        Assert.Contains("already added", vm.ErrorText);
        await _auth.DidNotReceiveWithAnyArgs().SignOutAsync(default!);
    }

    [AvaloniaFact]
    public async Task Account_without_a_reachable_mailbox_is_explained_and_its_tokens_are_removed()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns(new MicrosoftSignIn("key-x", "x@contoso.test", null, "tid"));
        var session = Substitute.For<IMailSession>();
        session.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => throw new MailOperationException(MailErrorKind.NotSupported, "no mailbox that Microsoft Graph can reach"));
        _sessions.Create(Arg.Any<MailAccount>(), Arg.Any<string>()).Returns(session);
        var vm = Dialog();

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Empty(_connected);
        Assert.Contains("Microsoft Graph can reach", vm.ErrorText);
        await _auth.Received(1).SignOutAsync("key-x");
    }

    [AvaloniaFact]
    public void Unconfigured_build_explains_and_cannot_start_sign_in()
    {
        var auth = new UnconfiguredMicrosoftAuth("No client ID in this build.");
        var vm = new MicrosoftAccountViewModel(auth, _sessions, _ => Task.CompletedTask, _ => false);
        Assert.False(vm.IsConfigured);
        Assert.Equal("No client ID in this build.", vm.ConfigurationProblem);
        Assert.False(vm.SignInCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Dialog_explains_privacy_permissions_roles_and_tenant_consent()
    {
        var vm = Dialog();
        Assert.Contains("never sees your Microsoft password", MicrosoftAccountViewModel.PrivacyText);
        Assert.Contains("HCG Connect", MicrosoftAccountViewModel.RoleText);
        Assert.Contains("Mail.Send", vm.PermissionsText);
        Assert.Contains("administrator", MicrosoftAccountViewModel.TenantText);
        Assert.Contains("shared mailboxes", MicrosoftAccountViewModel.SupportText);
    }

    // ---- Main window: connection state and removal ----

    private async Task<(ServiceProvider Services, MainWindowViewModel Vm, IDialogService Dialogs, ICredentialVault Vault, IDeliveryLog Log)> StartMainAsync(IMailSessionFactory sessions, params MailAccount[] accounts)
    {
        var paths = new AppPaths(_dir);
        new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Save(accounts);
        new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance).Save(new AppSettings { ConnectOnStartup = true });
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
        var vault = Substitute.For<ICredentialVault>();
        vault.GetAsync(Arg.Any<string>()).Returns("imap-password");
        var log = new InMemoryDeliveryLog();
        var services = AppServices.Build(new AppMode(IsPreview: false), s =>
        {
            s.AddSingleton(paths);
            s.AddSingleton(vault);
            s.AddSingleton<IAccountStore>(new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance));
            s.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance));
            s.AddSingleton(sessions);
            s.AddSingleton(dialogs);
            s.AddSingleton(Substitute.For<INotificationService>());
            s.AddSingleton(_auth);
            s.AddSingleton<IDeliveryLog>(log);
        });
        var vm = services.GetRequiredService<MainWindowViewModel>();
        await vm.InitializeAsync();
        await UiHarness.Settle(100);
        return (services, vm, dialogs, vault, log);
    }

    [AvaloniaFact]
    public async Task Expired_microsoft_sign_in_shows_reconnect_instead_of_asking_for_a_password()
    {
        var expired = Substitute.For<IMailSession>();
        expired.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => throw new MailOperationException(MailErrorKind.ReconnectRequired, "Sign in again."));
        var sessions = Substitute.For<IMailSessionFactory>();
        sessions.Create(Arg.Any<MailAccount>(), Arg.Any<string>()).Returns(expired);
        var (services, vm, _, vault, _) = await StartMainAsync(sessions, MicrosoftAccount("m1", "kari@contoso.test", "key-kari"));
        await using var _s = services;

        Assert.True(vm.ShowNeedsSignIn);
        Assert.False(vm.ShowNeedsPassword);
        await vault.DidNotReceiveWithAnyArgs().GetAsync(default!); // Microsoft accounts have no password
        sessions.Received().Create(Arg.Is<MailAccount>(a => a.IsMicrosoft), "");
    }

    [AvaloniaFact]
    public async Task Removing_a_microsoft_account_deletes_its_tokens_and_history_but_nothing_else()
    {
        var kari = MicrosoftAccount("m1", "kari@contoso.test", "key-kari");
        var imap = new MailAccount
        {
            Id = "i1", DisplayName = "Ola", EmailAddress = "ola@firma.no", Username = "ola@firma.no",
            Imap = new ServerEndpoint("mail.firma.no", 993, MailSecurityMode.SslTls), Smtp = new ServerEndpoint("mail.firma.no", 465, MailSecurityMode.SslTls),
        };
        var (services, vm, _, vault, log) = await StartMainAsync(_sessions, kari, imap);
        await using var _s = services;
        log.Add(new DeliveryRecord { Timestamp = DateTimeOffset.Now, AccountId = "m1", AccountAddress = "kari@contoso.test", SmtpServer = "graph", State = DeliveryState.Accepted });
        log.Add(new DeliveryRecord { Timestamp = DateTimeOffset.Now, AccountId = "i1", AccountAddress = "ola@firma.no", SmtpServer = "smtp", State = DeliveryState.Accepted });

        Assert.True(await vm.RemoveAccountAsync(kari));

        await _auth.Received(1).SignOutAsync("key-kari");
        await vault.DidNotReceive().DeleteAsync(kari.CredentialKey);
        Assert.Empty(log.GetRecent("m1"));
        Assert.Single(log.GetRecent("i1"));
        Assert.Equal(["i1"], vm.Accounts.Select(a => a.Account.Id));
        Assert.Equal(["i1"], new JsonAccountStore(new AppPaths(_dir), NullLogger<JsonAccountStore>.Instance).Load().Select(a => a.Id));
    }

    [AvaloniaFact]
    public async Task Settings_change_refresh_mode_message_view_and_remote_images()
    {
        var (services, vm, dialogs, vault, _) = await StartMainAsync(_sessions);
        await using var _s = services;
        var settings = services.GetRequiredService<AppSettingsService>();
        var page = new SettingsViewModel(settings, services.GetRequiredService<ThemeService>(), dialogs, services.GetRequiredService<AppPaths>(), vault, vm);

        Assert.Equal(0, page.SelectedRefresh.Minutes); // manual by default
        page.SelectedRefresh = page.RefreshOptions.Single(o => o.Minutes == 30);
        page.SelectedViewMode = page.ViewModeOptions.Single(o => o.Mode == MessageViewMode.Readable);
        page.LoadRemoteImagesAutomatically = true;

        Assert.Equal(30, settings.Current.AutomaticRefreshMinutes);
        Assert.Equal(MessageViewMode.Readable, settings.Current.DefaultMessageView);
        Assert.True(settings.Current.LoadRemoteImagesAutomatically);
        Assert.Contains("available", page.MicrosoftSignInStatus); // the substitute sign-in here reports itself as configured
    }
}
