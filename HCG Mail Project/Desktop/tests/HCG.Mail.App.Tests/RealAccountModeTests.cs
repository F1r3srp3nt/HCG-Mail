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

    private static async Task<(ServiceProvider Services, MainWindow Window, MainWindowViewModel Vm)> StartNormalAsync(string dataDir,
        Action<IServiceCollection>? configure = null, double width = 1440, double height = 900)
    {
        var paths = new AppPaths(dataDir);
        var services = AppServices.Build(new AppMode(IsPreview: false), s =>
        {
            s.AddSingleton(paths);
            s.AddSingleton<ICredentialVault>(new SessionCredentialVault()); // empty: no password stored
            s.AddSingleton<IAccountStore>(new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance));
            s.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance));
            configure?.Invoke(s);
        });
        services.GetRequiredService<ThemeService>().ApplySaved();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
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
            // This test build has no Microsoft or Google client ID: neither sign-in is offered, and the welcome text does
            // not mention them (Settings says they are not set up in this build).
            Assert.False(HasVisibleButton(window, "AddMicrosoftAccountButton"));
            Assert.False(HasVisibleButton(window, "AddGoogleAccountButton"));
            Assert.DoesNotContain("Microsoft", vm.WelcomeText);
            Assert.DoesNotContain("Google", vm.WelcomeText);
            Assert.Contains("IMAP and SMTP", vm.WelcomeText);
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

    [AvaloniaFact]
    public async Task Public_beta_hides_microsoft_and_google_and_keeps_an_existing_microsoft_account_unchanged()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-public-beta-").FullName;
        try
        {
            var paths = new AppPaths(dir);
            var existing = new MailAccount
            {
                Id = "ms-1", DisplayName = "Kari", EmailAddress = "kari@example.com", Username = "kari@example.com",
                Imap = MailAccount.GraphEndpoint, Smtp = MailAccount.GraphEndpoint,
                Provider = MailProviderKind.MicrosoftGraph, MicrosoftAccountId = "home-account-id",
            };
            new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Save([existing]);
            var before = File.ReadAllBytes(paths.AccountsFile);

            // Microsoft is configured in this build, but the release capability (Public Beta) still hides it.
            Assert.False(AppServices.Capabilities.MicrosoftSignIn);
            var (services, window, vm) = await StartNormalAsync(dir);
            var settings = new SettingsViewModel(services.GetRequiredService<AppSettingsService>(), services.GetRequiredService<ThemeService>(),
                services.GetRequiredService<IDialogService>(), paths, services.GetRequiredService<ICredentialVault>(), vm);

            Assert.False(vm.ShowMicrosoftSignIn);
            Assert.False(vm.ShowGoogleSignIn);
            Assert.False(settings.ShowMicrosoftSignIn);
            Assert.False(HasVisibleButton(window, "AddMicrosoftAccountButton"));
            Assert.False(HasVisibleButton(window, "AddGoogleAccountButton"));

            var account = Assert.Single(vm.Accounts);
            Assert.Equal(MailProviderKind.MicrosoftGraph, account.Account.Provider);
            Assert.Contains("not available in HCG Mail 1.0 Public Beta", account.StatusText);
            Assert.Contains("kept unchanged", account.StatusText);
            Assert.Equal(before, File.ReadAllBytes(paths.AccountsFile)); // not deleted, not converted

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
    public async Task A_configured_sign_in_provider_is_offered_and_named()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-configured-").FullName;
        try
        {
            var microsoft = Substitute.For<IMicrosoftAuth>();
            microsoft.IsConfigured.Returns(true);
            // An internal development build: release capabilities allow the provider.
            var (services, window, vm) = await StartNormalAsync(dir, s => s.AddSingleton(microsoft).AddSingleton(new SignInPresentation(false, AllowMicrosoft: true, AllowGoogle: true)));
            Assert.True(HasVisibleButton(window, "AddMicrosoftAccountButton"));
            Assert.False(HasVisibleButton(window, "AddGoogleAccountButton"));
            Assert.Contains("Microsoft account", vm.WelcomeText);
            Assert.DoesNotContain("Google", vm.WelcomeText);
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
    public void The_drawn_title_bar_shows_only_caption_buttons_under_the_apps_own_title()
    {
        var window = new MainWindow();
        Assert.True(window.MinWidth >= 256 + 400 + 1 + 380, "the window's minimum width must fit the three columns at their default widths");
        Assert.Equal(Avalonia.Controls.Chrome.TitleBarDecorations.MinimizeButton | Avalonia.Controls.Chrome.TitleBarDecorations.MaximizeButton
                     | Avalonia.Controls.Chrome.TitleBarDecorations.CloseButton,
            Avalonia.Controls.Chrome.WindowDrawnDecorations.GetTitleBarDecorations(window));
        Assert.Equal("HCG Mail", window.Title); // still the taskbar and Alt+Tab name
    }

    [AvaloniaTheory]
    [InlineData(1044, 788, false)] // the window size of the Windows prerelease screenshots, which showed clipped text
    [InlineData(1040, 620, false)] // the window's minimum size
    [InlineData(1040, 620, true)]  // with both sign-in providers configured: the longest labels, the tallest card
    public async Task Welcome_card_text_and_labels_fit_inside_the_card(int width, int height, bool providersConfigured)
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-welcome-").FullName;
        try
        {
            var (services, window, vm) = await StartNormalAsync(dir, s =>
            {
                if (!providersConfigured) return;
                var microsoft = Substitute.For<IMicrosoftAuth>(); microsoft.IsConfigured.Returns(true);
                var google = Substitute.For<IGoogleAuth>(); google.IsConfigured.Returns(true);
                s.AddSingleton(microsoft); s.AddSingleton(google);
            }, width, height);
            var visuals = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToList();
            var welcome = visuals.OfType<Avalonia.Controls.TextBlock>().First(t => t.Name == "WelcomeText");
            var card = Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(welcome).OfType<Avalonia.Controls.Border>().First(b => b.Classes.Contains("card"));
            var cardBox = card.Bounds.Deflate(card.Padding);
            // Every text in the card ends inside the card's content box (nothing is cut off at the right edge).
            foreach (var text in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(card).OfType<Avalonia.Controls.TextBlock>().Where(t => t.IsEffectivelyVisible))
            {
                var right = Avalonia.VisualExtensions.TranslatePoint(text, new Avalonia.Point(text.Bounds.Width, 0), card)!.Value.X;
                Assert.True(right <= card.Bounds.Width - card.Padding.Right + 0.5,
                    $"'{text.Text}' ends at {right:0.0}, beyond the card's content edge {card.Bounds.Width - card.Padding.Right:0.0} (window {width}x{height})");
                // A text is never laid out wider than it is allowed to be (that is what was clipped before).
                Assert.True(text.DesiredSize.Width <= text.Bounds.Width + 0.5, $"'{text.Text}' needs {text.DesiredSize.Width:0.0} but has {text.Bounds.Width:0.0}");
            }
            Assert.True(cardBox.Width > 0);
            // The whole card is inside the window horizontally (at 1000 px the reader column used to be pushed past the edge)…
            var cardRight = Avalonia.VisualExtensions.TranslatePoint(card, new Avalonia.Point(card.Bounds.Width, 0), window)!.Value.X;
            Assert.True(cardRight <= width + 0.5, $"the card ends at {cardRight:0.0}, beyond the window's width {width}");
            // …and reachable vertically: on a short window it scrolls instead of being cut off at the bottom.
            var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(card).OfType<Avalonia.Controls.ScrollViewer>().First(v => v.Name == "WelcomeScroller");
            Assert.True(scroller.Extent.Height >= card.Bounds.Height, $"the card ({card.Bounds.Height:0}) is taller than what the scroller can show ({scroller.Extent.Height:0})");
            UiHarness.Capture(window, $"21-welcome-{width}x{height}{(providersConfigured ? "-providers" : "")}");
            window.Close();
            await vm.DisposeAsync();
            await services.DisposeAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool HasVisibleButton(Avalonia.Visual root, string name) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Avalonia.Controls.Button>().Any(b => b.Name == name && b.IsEffectivelyVisible);
}
