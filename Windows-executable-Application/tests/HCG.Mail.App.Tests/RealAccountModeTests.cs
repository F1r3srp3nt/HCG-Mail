using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>Normal (non-preview) start-up, manual-only refresh and the official logo.</summary>
public class RealAccountModeTests
{
    private static MailAccount ExistingAccount() => new()
    {
        Id = "existing-1",
        DisplayName = "Ola Nordmann",
        EmailAddress = "ola@firma.example",
        Username = "ola@firma.example",
        Imap = new ServerEndpoint("mail.firma.example", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("mail.firma.example", 465, MailSecurityMode.SslTls),
        PresetId = "domene.no",
    };

    private static async Task<(ServiceProvider Services, MainWindow Window, MainWindowViewModel Vm)> StartNormalAsync(string dataDir)
    {
        var paths = new AppPaths(dataDir);
        var services = AppServices.Build(new AppMode(IsPreview: false), s =>
        {
            s.AddSingleton(paths);
            s.AddSingleton<ICredentialVault>(new SessionCredentialVault()); // empty: no password stored
            s.AddSingleton<IAccountStore>(new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance));
            s.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance));
        });
        services.GetRequiredService<ThemeService>().ApplySaved();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        services.GetRequiredService<DialogService>().Attach(window);
        window.Show();
        await vm.InitializeAsync();
        await UiHarness.Settle(400);
        return (services, window, vm);
    }

    [AvaloniaFact]
    public async Task Normal_start_without_accounts_shows_clean_setup_state_and_no_demo_data()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-normal-").FullName;
        try
        {
            var (services, window, vm) = await StartNormalAsync(dir);
            Assert.False(vm.IsPreviewMode);
            Assert.Empty(vm.Accounts);
            Assert.True(vm.ShowWelcome);
            Assert.Empty(vm.MessageList.Items);
            Assert.Null(vm.SelectedAccount);
            Assert.Contains("Manual refresh", vm.StatusText);
            UiHarness.Capture(window, "20-normal-first-run-logo");
            window.Close();
            await vm.DisposeAsync();
            await services.DisposeAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Existing_accounts_and_settings_are_loaded_and_left_untouched()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-existing-").FullName;
        try
        {
            var paths = new AppPaths(dir);
            new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Save([ExistingAccount()]);
            // A settings file written by the previous build (still containing the removed auto-refresh options).
            File.WriteAllText(paths.SettingsFile, """{ "version": 1, "theme": "HcgFuture", "refreshIntervalMinutes": 5, "notifyOnNewMail": true, "connectOnStartup": true, "lastAccountId": "existing-1" }""");
            var accountsBefore = File.ReadAllBytes(paths.AccountsFile);

            var (services, window, vm) = await StartNormalAsync(dir);
            var account = Assert.Single(vm.Accounts);
            Assert.Equal("ola@firma.example", account.EmailAddress);
            Assert.True(account.NeedsPassword); // no stored password in this vault → asks, never connects to anything else
            Assert.Empty(vm.MessageList.Items);
            Assert.False(vm.IsPreviewMode);
            UiHarness.Capture(window, "21-normal-existing-account");
            window.Close();
            await vm.DisposeAsync();
            await services.DisposeAsync();

            Assert.Equal(accountsBefore, File.ReadAllBytes(paths.AccountsFile));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Developer_preview_is_never_enabled_implicitly()
    {
        var previous = Environment.GetEnvironmentVariable("HCG_MAIL_PREVIEW");
        Environment.SetEnvironmentVariable("HCG_MAIL_PREVIEW", "1");
        try
        {
            Assert.False(Program.IsDeveloperPreviewRequested([]));
            Assert.False(Program.IsDeveloperPreviewRequested(["--preview"]));
#if DEBUG
            Assert.True(Program.IsDeveloperPreviewRequested([Program.DeveloperPreviewArgument]));
#else
            Assert.False(Program.IsDeveloperPreviewRequested([Program.DeveloperPreviewArgument]));
#endif
        }
        finally
        {
            Environment.SetEnvironmentVariable("HCG_MAIL_PREVIEW", previous);
        }
    }

    [AvaloniaFact]
    public void Main_window_has_no_periodic_refresh_timer()
    {
        var timers = typeof(MainWindowViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(DispatcherTimer) || f.FieldType == typeof(System.Timers.Timer) || f.FieldType == typeof(System.Threading.Timer) || f.FieldType == typeof(PeriodicTimer))
            .Select(f => f.Name)
            .ToList();
        // The 120 ms debounce used when a message is clicked, and the optional automatic-refresh timer, which only
        // exists when the user turns automatic checking on (see the test below).
        Assert.Equal(["_autoRefresh", "_selectionDelay"], timers.Order());
        // The old setting name (5 minutes in builds that polled) must stay ignored, so upgrading never turns polling on.
        Assert.Null(typeof(AppSettings).GetProperty("RefreshIntervalMinutes"));
        Assert.Equal(0, new AppSettings().AutomaticRefreshMinutes);
    }

    [AvaloniaFact]
    public async Task Automatic_refresh_timer_exists_only_when_enabled_in_settings()
    {
        await using var ui = await UiHarness.StartAsync();
        var field = typeof(MainWindowViewModel).GetField("_autoRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(field.GetValue(ui.ViewModel));

        var settings = ui.Services.GetRequiredService<AppSettingsService>();
        settings.Update(s => s with { AutomaticRefreshMinutes = 15 });
        ui.ViewModel.ApplyRefreshSchedule();
        var timer = Assert.IsType<DispatcherTimer>(field.GetValue(ui.ViewModel));
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Interval);
        Assert.True(timer.IsEnabled);

        settings.Update(s => s with { AutomaticRefreshMinutes = 0 });
        ui.ViewModel.ApplyRefreshSchedule();
        Assert.Null(field.GetValue(ui.ViewModel));
    }

    [Fact]
    public void Old_refresh_interval_in_settings_files_does_not_enable_polling()
    {
        var json = "{\"version\":1,\"refreshIntervalMinutes\":5,\"notifyOnNewMail\":true}";
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;
        Assert.Equal(0, loaded.Normalize().AutomaticRefreshMinutes);
    }

    [AvaloniaFact]
    public async Task Manual_refresh_cannot_run_twice_reports_success_and_can_be_cancelled()
    {
        var notifications = Substitute.For<INotificationService>();
        await using var ui = await UiHarness.StartAsync(s => s.AddSingleton(notifications));
        var vm = ui.ViewModel;

        var first = vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsRefreshing);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        await first;
        Assert.False(vm.IsRefreshing);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        notifications.Received().Success(Arg.Is<string>(t => t.Contains("up to date") || t.Contains("new message")), Arg.Any<string>(), Arg.Any<Action?>());
        Assert.Contains("Last refreshed", vm.StatusText);

        var second = vm.RefreshCommand.ExecuteAsync(null);
        vm.RefreshCancelCommand.Execute(null);
        await second;
        Assert.False(vm.IsRefreshing);
        Assert.True(vm.Accounts[0].IsConnected);
    }

    [AvaloniaFact]
    public void Official_logo_asset_is_embedded_with_original_proportions()
    {
        var uri = new Uri("avares://HCGMail/Assets/Branding/hcg-logo.png");
        Assert.True(AssetLoader.Exists(uri));
        using var bitmap = new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(uri));
        var ratio = bitmap.PixelSize.Width / (double)bitmap.PixelSize.Height;
        Assert.InRange(ratio, 2.1, 2.25);              // supplied artwork crop: 1520 × 700
        Assert.True(bitmap.PixelSize.Height >= 4 * 38); // ≥ 4× the 38 px display height for high-DPI screens
    }
}
