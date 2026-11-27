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
using HCG.Mail.Infrastructure.Google;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>Adding, reconnecting and removing Google accounts (fake sign-in; no browser, no Google servers).</summary>
public sealed class GoogleAccountTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-mail-google-").FullName;
    private readonly IGoogleAuth _auth = Substitute.For<IGoogleAuth>();
    private readonly IMailSessionFactory _sessions = Substitute.For<IMailSessionFactory>();
    private readonly List<MailAccount> _connected = [];

    public GoogleAccountTests()
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

    private static MailAccount GoogleAccount(string id, string user, string sub) => new()
    {
        Id = id, DisplayName = user, EmailAddress = user, Username = user,
        Imap = MailAccount.GmailEndpoint, Smtp = MailAccount.GmailEndpoint,
        Provider = MailProviderKind.Gmail, GoogleAccountId = sub,
    };

    private static MailAccount ImapAccount() => new()
    {
        Id = "i1", DisplayName = "Ola", EmailAddress = "ola@firma.no", Username = "ola@firma.no",
        Imap = new ServerEndpoint("mail.firma.no", 993, MailSecurityMode.SslTls), Smtp = new ServerEndpoint("mail.firma.no", 465, MailSecurityMode.SslTls),
    };

    private GoogleAccountViewModel Dialog(MailAccount? existing = null, params string[] keysInUse) =>
        new(_auth, _sessions, a =>
        {
            _connected.Add(a);
            return Task.CompletedTask;
        }, key => keysInUse.Contains(key), existing);

    [AvaloniaFact]
    public async Task Sign_in_adds_a_gmail_account_without_a_password()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns(new GoogleSignIn("sub-kari", "kari@gmail.test", "Kari Nordmann"));
        var vm = Dialog();
        var closed = false;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.SignInCommand.ExecuteAsync(null);

        var account = Assert.Single(_connected);
        Assert.Equal((MailProviderKind.Gmail, "sub-kari", "kari@gmail.test", "Kari Nordmann"),
            (account.Provider, account.GoogleAccountId, account.EmailAddress, account.DisplayName));
        Assert.True(account.UsesOAuth);
        Assert.Equal("oauth:google:sub-kari", CredentialKeys.ForGoogle(account.GoogleAccountId!));
        Assert.True(closed);
        await _auth.DidNotReceiveWithAnyArgs().SignOutAsync(default!);
    }

    [AvaloniaFact]
    public async Task Cancelled_or_denied_consent_adds_nothing()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns<GoogleSignIn>(_ => throw new MailOperationException(MailErrorKind.Cancelled, "cancelled"));
        var vm = Dialog();
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Empty(_connected);
        Assert.Equal("Sign-in was cancelled. No account was added.", vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Cancel_button_stops_a_pending_browser_sign_in()
    {
        var pending = new TaskCompletionSource<GoogleSignIn>();
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
    public async Task Reconnecting_as_a_different_google_account_is_refused_and_its_token_removed()
    {
        var existing = GoogleAccount("g1", "kari@gmail.test", "sub-kari");
        _auth.SignInAsync("kari@gmail.test", Arg.Any<CancellationToken>()).Returns(new GoogleSignIn("sub-ola", "ola@gmail.test", "Ola"));
        var vm = Dialog(existing, "sub-kari");

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Empty(_connected);
        Assert.Contains("ola@gmail.test", vm.ErrorText);
        Assert.Contains("kari@gmail.test", vm.ErrorText);
        await _auth.Received(1).SignOutAsync("sub-ola");
        await _auth.DidNotReceive().SignOutAsync("sub-kari");
    }

    [AvaloniaFact]
    public async Task Reconnecting_the_same_account_keeps_its_id_and_name()
    {
        var existing = GoogleAccount("g1", "kari@gmail.test", "sub-kari") with { DisplayName = "Kari (work)" };
        _auth.SignInAsync("kari@gmail.test", Arg.Any<CancellationToken>()).Returns(new GoogleSignIn("sub-kari", "kari@gmail.test", "Kari"));
        var vm = Dialog(existing, "sub-kari");

        await vm.SignInCommand.ExecuteAsync(null);

        var account = Assert.Single(_connected);
        Assert.Equal(("g1", "Kari (work)"), (account.Id, account.DisplayName));
    }

    [AvaloniaFact]
    public async Task Adding_the_same_google_account_twice_is_refused_without_touching_its_token()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns(new GoogleSignIn("sub-kari", "kari@gmail.test", null));
        var vm = Dialog(null, "sub-kari");
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Empty(_connected);
        Assert.Contains("already added", vm.ErrorText);
        await _auth.DidNotReceiveWithAnyArgs().SignOutAsync(default!);
    }

    [AvaloniaFact]
    public async Task Gmail_api_disabled_is_explained_and_the_unused_token_removed()
    {
        _auth.SignInAsync(null, Arg.Any<CancellationToken>()).Returns(new GoogleSignIn("sub-x", "x@gmail.test", null));
        var session = Substitute.For<IMailSession>();
        session.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => throw new MailOperationException(MailErrorKind.NotSupported, "The Gmail API is not enabled for HCG Mail's Google Cloud project."));
        _sessions.Create(Arg.Any<MailAccount>(), Arg.Any<string>()).Returns(session);
        var vm = Dialog();

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Empty(_connected);
        Assert.Contains("Gmail API", vm.ErrorText);
        await _auth.Received(1).SignOutAsync("sub-x");
    }

    [AvaloniaFact]
    public void Unconfigured_build_explains_and_cannot_start_sign_in()
    {
        var vm = new GoogleAccountViewModel(new UnconfiguredGoogleAuth(GoogleAuthOptions.NotConfiguredMessage), _sessions, _ => Task.CompletedTask, _ => false);
        Assert.False(vm.IsConfigured);
        Assert.Contains("GOOGLE_ACCOUNTS.md", vm.ConfigurationProblem);
        Assert.False(vm.SignInCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Window_shows_the_missing_configuration_and_disables_sign_in()
    {
        var vm = new GoogleAccountViewModel(new UnconfiguredGoogleAuth(GoogleAuthOptions.MissingSecretMessage), _sessions, _ => Task.CompletedTask, _ => false);
        var window = await new Views.GoogleAccountWindow { DataContext = vm, Width = 640, Height = 680 }.ShowAndSettleAsync();
        UiHarness.Capture(window, "google-unconfigured");
        Assert.True(window.ConfigurationProblem.IsEffectivelyVisible);
        Assert.Contains("client secret is missing", window.ConfigurationProblem.Text);
        Assert.False(window.SignInButton.IsEffectivelyEnabled);
        window.Close();
    }

    [Theory]
    [InlineData(null, null, GoogleAuthOptions.NotConfiguredMessage)]
    [InlineData("", "secret", GoogleAuthOptions.NotConfiguredMessage)]
    [InlineData("not-a-client-id", "secret", GoogleAuthOptions.InvalidClientIdMessage)]
    [InlineData("123456789012-abcdefghijklmnop.apps.googleusercontent.com", null, GoogleAuthOptions.MissingSecretMessage)]
    [InlineData("123456789012-abcdefghijklmnop.apps.googleusercontent.com", " ", GoogleAuthOptions.MissingSecretMessage)]
    [InlineData("123456789012-abcdefghijklmnop.apps.googleusercontent.com", "placeholder", null)]
    public void Partial_configuration_never_enables_sign_in(string? clientId, string? secret, string? expected) =>
        Assert.Equal(expected, GoogleAuthOptions.ConfigurationProblem(clientId, secret));

    [AvaloniaFact]
    public void Dialog_explains_privacy_permissions_roles_and_testing_limits()
    {
        var vm = Dialog();
        Assert.Contains("never sees your Google password", GoogleAccountViewModel.PrivacyText);
        Assert.Contains("HCG Connect", GoogleAccountViewModel.RoleText);
        Assert.Contains("gmail.modify", vm.PermissionsText);
        Assert.Contains("permanently deleting", vm.PermissionsText);
        Assert.Contains("7 days", GoogleAccountViewModel.TestingText);
        Assert.Contains("test users", GoogleAccountViewModel.TestingText);
    }

    // ---- Main window: connection state, isolation and removal ----

    private async Task<(ServiceProvider Services, MainWindowViewModel Vm, ICredentialVault Vault, IDeliveryLog Log, IMicrosoftAuth Microsoft)> StartMainAsync(IMailSessionFactory sessions, params MailAccount[] accounts)
    {
        var paths = new AppPaths(_dir);
        new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Save(accounts);
        new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance).Save(new AppSettings { ConnectOnStartup = true });
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
        var vault = Substitute.For<ICredentialVault>();
        vault.GetAsync(Arg.Any<string>()).Returns("imap-password");
        vault.StorageDescription.Returns("test vault");
        var microsoft = Substitute.For<IMicrosoftAuth>();
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
            s.AddSingleton(microsoft);
            s.AddSingleton(_auth);
            s.AddSingleton<IDeliveryLog>(log);
        });
        var vm = services.GetRequiredService<MainWindowViewModel>();
        await vm.InitializeAsync();
        await UiHarness.Settle(100);
        return (services, vm, vault, log, microsoft);
    }

    [AvaloniaFact]
    public async Task Revoked_google_access_shows_reconnect_and_never_asks_for_a_password()
    {
        var revoked = Substitute.For<IMailSession>();
        revoked.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => throw new MailOperationException(MailErrorKind.ReconnectRequired, "Google ended HCG Mail's access. Reconnect."));
        var sessions = Substitute.For<IMailSessionFactory>();
        sessions.Create(Arg.Any<MailAccount>(), Arg.Any<string>()).Returns(revoked);
        var (services, vm, vault, _, _) = await StartMainAsync(sessions, GoogleAccount("g1", "kari@gmail.test", "sub-kari"));
        await using var _s = services;

        Assert.True(vm.ShowNeedsSignIn);
        Assert.False(vm.ShowNeedsPassword);
        await vault.DidNotReceiveWithAnyArgs().GetAsync(default!); // the session reads its own token; no password lookup
        sessions.Received().Create(Arg.Is<MailAccount>(a => a.IsGoogle), "");
    }

    [AvaloniaFact]
    public async Task Removing_a_google_account_deletes_only_its_token_and_history()
    {
        var kari = GoogleAccount("g1", "kari@gmail.test", "sub-kari");
        var ola = GoogleAccount("g2", "ola@gmail.test", "sub-ola");
        var imap = ImapAccount();
        var (services, vm, vault, log, microsoft) = await StartMainAsync(_sessions, kari, ola, imap);
        await using var _s = services;
        log.Add(new DeliveryRecord { Timestamp = DateTimeOffset.Now, AccountId = "g1", AccountAddress = "kari@gmail.test", SmtpServer = "gmail", State = DeliveryState.Accepted });
        log.Add(new DeliveryRecord { Timestamp = DateTimeOffset.Now, AccountId = "g2", AccountAddress = "ola@gmail.test", SmtpServer = "gmail", State = DeliveryState.Accepted });
        log.Add(new DeliveryRecord { Timestamp = DateTimeOffset.Now, AccountId = "i1", AccountAddress = "ola@firma.no", SmtpServer = "smtp", State = DeliveryState.Accepted });

        Assert.True(await vm.RemoveAccountAsync(kari));

        await _auth.Received(1).SignOutAsync("sub-kari");
        await _auth.DidNotReceive().SignOutAsync("sub-ola");
        await microsoft.DidNotReceiveWithAnyArgs().SignOutAsync(default!);
        await vault.DidNotReceive().DeleteAsync(imap.CredentialKey);
        Assert.Empty(log.GetRecent("g1"));
        Assert.Single(log.GetRecent("g2"));
        Assert.Single(log.GetRecent("i1"));
        Assert.Equal(["g2", "i1"], vm.Accounts.Select(a => a.Account.Id));
        Assert.Equal(["g2", "i1"], new JsonAccountStore(new AppPaths(_dir), NullLogger<JsonAccountStore>.Instance).Load().Select(a => a.Id));
    }

    [AvaloniaFact]
    public async Task Token_deletion_failure_is_retried_at_next_start()
    {
        var kari = GoogleAccount("g1", "kari@gmail.test", "sub-kari");
        _auth.SignOutAsync("sub-kari").Returns(_ => throw new IOException("keychain locked"));
        var (services, vm, _, _, _) = await StartMainAsync(_sessions, kari, ImapAccount());
        await using var _s = services;

        Assert.True(await vm.RemoveAccountAsync(kari));

        var settings = services.GetRequiredService<AppSettingsService>();
        Assert.Contains("oauth:google:sub-kari", settings.Current.PendingCredentialDeletions);
        Assert.Equal(["i1"], vm.Accounts.Select(a => a.Account.Id));
    }

    [AvaloniaFact]
    public async Task Imap_and_microsoft_accounts_load_unchanged_next_to_gmail()
    {
        var microsoftAccount = new MailAccount
        {
            Id = "m1", DisplayName = "Kari", EmailAddress = "kari@contoso.test", Username = "kari@contoso.test",
            Imap = MailAccount.GraphEndpoint, Smtp = MailAccount.GraphEndpoint, Provider = MailProviderKind.MicrosoftGraph, MicrosoftAccountId = "oid.tid",
        };
        var (services, vm, vault, _, _) = await StartMainAsync(_sessions, ImapAccount(), microsoftAccount, GoogleAccount("g1", "kari@gmail.test", "sub-kari"));
        await using var _s = services;

        var stored = new JsonAccountStore(new AppPaths(_dir), NullLogger<JsonAccountStore>.Instance).Load();
        Assert.Equal(ImapAccount(), stored.Single(a => a.Id == "i1"));
        Assert.Equal(microsoftAccount, stored.Single(a => a.Id == "m1"));
        Assert.Equal(GoogleAccount("g1", "kari@gmail.test", "sub-kari"), stored.Single(a => a.Id == "g1"));
        Assert.Equal([MailProviderKind.MicrosoftGraph, MailProviderKind.Gmail], vm.Accounts.Where(a => a.UsesOAuth).Select(a => a.Account.Provider).Order().ToArray());
        await vault.Received().GetAsync(ImapAccount().CredentialKey); // only the IMAP account reads a password
        await vault.DidNotReceive().GetAsync(Arg.Is<string>(k => k.StartsWith("oauth:")));
    }

    [AvaloniaFact]
    public async Task Settings_page_reports_google_status()
    {
        var (services, vm, vault, _, _) = await StartMainAsync(_sessions);
        await using var _s = services;
        var page = new SettingsViewModel(services.GetRequiredService<AppSettingsService>(), services.GetRequiredService<ThemeService>(),
            services.GetRequiredService<IDialogService>(), services.GetRequiredService<AppPaths>(), vault, vm);
        Assert.Contains("Gmail and Google Workspace", page.GoogleSignInStatus);
    }
}
