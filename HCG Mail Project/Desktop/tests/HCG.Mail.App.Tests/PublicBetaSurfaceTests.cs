using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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

namespace HCG.Mail.App.Tests;

/// <summary>
/// What a person sees in the Public Beta, window by window (physical Windows feedback on 1.0.0-beta.1): no Microsoft or
/// Google sign-in anywhere (the integrations are kept in the code, hidden by the release capability), IMAP/SMTP as the way
/// to add a mailbox, and exactly one HCG Mail wordmark in the main window.
/// </summary>
public sealed class PublicBetaSurfaceTests
{
    private static readonly string[] Forbidden =
    [
        "Add Microsoft", "Add Google", "Microsoft sign-in", "Google sign-in", "Microsoft sign in", "Google sign in",
        "Sign in with Microsoft", "Sign in with Google", "sign in with a Microsoft", "sign in with a Google", "OAuth",
    ];

    private static IReadOnlyList<string> VisibleTexts(Avalonia.Visual root) =>
        root.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => c switch
            {
                TextBlock t => t.Text,
                ContentControl { Content: string s } => s,
                TextBox t => t.Watermark,
                _ => null,
            })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

    private static void AssertNoSignInOffer(Avalonia.Visual root, string where)
    {
        var offending = VisibleTexts(root).Where(t => Forbidden.Any(f => t.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(offending.Count == 0, $"{where} shows: {string.Join(" | ", offending)}");
        var buttons = root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Name is { } n &&
            (n.Contains("Microsoft", StringComparison.Ordinal) || n.Contains("Google", StringComparison.Ordinal))).Select(b => b.Name).ToList();
        Assert.True(buttons.Count == 0, $"{where} shows the buttons {string.Join(", ", buttons)}");
    }

    [AvaloniaFact]
    public async Task Public_beta_offers_no_microsoft_or_google_sign_in_anywhere_and_shows_one_wordmark()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-public-surface-").FullName;
        try
        {
            Assert.False(AppServices.Capabilities.MicrosoftSignIn);
            Assert.False(AppServices.Capabilities.GoogleSignIn);
            var paths = new AppPaths(dir);
            await using var services = AppServices.Build(new AppMode(IsPreview: false), s =>
            {
                s.AddSingleton(paths);
                s.AddSingleton<ICredentialVault>(new SessionCredentialVault());
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

            // Main window, welcome card: IMAP/SMTP only, one wordmark (the sidebar brand mark).
            Assert.True(vm.ShowWelcome);
            AssertNoSignInOffer(window, "The main window");
            Assert.Contains("IMAP and SMTP", vm.WelcomeText);
            var wordmarks = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text == "HCG MAIL").ToList();
            Assert.Single(wordmarks);
            Assert.True(wordmarks[0].FindAncestorOfType<BrandMark>() is not null, "the wordmark is the sidebar brand mark");
            UiHarness.Capture(window, "beta2-welcome-future");
            AssertBrandMarkFitsTheSidebar(window);

            // Every Settings page.
            var settings = new SettingsViewModel(services.GetRequiredService<AppSettingsService>(), services.GetRequiredService<ThemeService>(),
                services.GetRequiredService<IDialogService>(), paths, services.GetRequiredService<ICredentialVault>(), vm);
            var settingsWindow = new SettingsWindow { DataContext = settings, Width = 900, Height = 660 };
            settingsWindow.Show();
            await UiHarness.Settle(200);
            var tabs = settingsWindow.GetVisualDescendants().OfType<TabControl>().First();
            for (var i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                await UiHarness.Settle(80);
                AssertNoSignInOffer(settingsWindow, $"Settings › {(tabs.SelectedItem as TabItem)?.Header}");
            }
            settingsWindow.Close();

            // Add account, as opened from the welcome card, including the notice for an Outlook.com address.
            _ = vm.AddAccountCommand.ExecuteAsync(null);
            await UiHarness.Settle(300);
            var setupWindow = Assert.Single(window.OwnedWindows.OfType<AccountSetupWindow>());
            AssertNoSignInOffer(setupWindow, "Add account");
            var setup = (AccountSetupViewModel)setupWindow.DataContext!;
            setup.EmailAddress = "someone@outlook.com";
            await UiHarness.Settle(120);
            Assert.NotNull(setup.ProviderNotice);
            AssertNoSignInOffer(setupWindow, "Add account with an Outlook.com address");
            UiHarness.Capture(setupWindow, "beta2-account-setup-outlook-notice");
            setupWindow.Close();

            window.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The whole wordmark ends inside the sidebar: at its default width and at its minimum width (220).</summary>
    private static void AssertBrandMarkFitsTheSidebar(MainWindow window)
    {
        var nav = window.GetVisualDescendants().OfType<NavigationPane>().Single();
        var subtitle = nav.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "HAKMI CAPITAL GROUP");
        foreach (var width in new[] { 256.0, 220.0 })
        {
            nav.Width = width;
            window.UpdateLayout();
            var right = subtitle.TranslatePoint(new Avalonia.Point(subtitle.Bounds.Width, 0), nav)!.Value.X;
            Assert.True(right <= width - 10, $"at a {width} px sidebar the subtitle ends at {right:F1} px (clipped)");
        }
        nav.Width = double.NaN;
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public async Task Light_theme_shows_the_logo_without_a_backing_plate_and_one_wordmark()
    {
        var dir = Directory.CreateTempSubdirectory("hcg-mail-public-classic-").FullName;
        try
        {
            var paths = new AppPaths(dir);
            new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance).Save(new AppSettings { Theme = ThemeKind.HcgClassic });
            await using var services = AppServices.Build(new AppMode(IsPreview: false), s =>
            {
                s.AddSingleton(paths);
                s.AddSingleton<ICredentialVault>(new SessionCredentialVault());
                s.AddSingleton<IAccountStore>(new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance));
                s.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance));
            });
            services.GetRequiredService<ThemeService>().ApplySaved();
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            await vm.InitializeAsync();
            await UiHarness.Settle(400);

            var brand = window.GetVisualDescendants().OfType<BrandMark>().Single(b => b.IsEffectivelyVisible);
            var plate = brand.GetVisualDescendants().OfType<Border>().First();
            Assert.True(plate.Background is null || (plate.Background is Avalonia.Media.ISolidColorBrush b && b.Color.A == 0),
                "the logo sits directly on the sidebar, without a dark backing plate");
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "HCG MAIL");
            UiHarness.Capture(window, "beta2-welcome-classic");
            window.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
