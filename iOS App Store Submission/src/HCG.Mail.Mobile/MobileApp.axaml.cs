using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HCG.Mail.App.Services;
using HCG.Mail.App.Themes;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Mobile.Platform;
using HCG.Mail.Mobile.Services;
using HCG.Mail.Mobile.ViewModels;
using HCG.Mail.Mobile.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Mobile;

/// <summary>
/// The Avalonia application for iPhone and iPad. The iOS head sets <see cref="Platform"/> (and optionally
/// <see cref="Mode"/>) before Avalonia starts; it then runs as a single-view application.
/// </summary>
public partial class MobileApp : Application
{
    public static IMobilePlatform? Platform { get; set; }
    public static AppMode Mode { get; set; } = new(false);
    public static IServiceProvider? Services { get; private set; }

    /// <summary>
    /// Preview mode only (sample mailbox, Debug builds): the screen to open after start-up for store screenshots —
    /// "inbox", "reader" (first message) or "compose". Ignored in normal use; the iOS host sets it from
    /// HCG_MAIL_PREVIEW_SCREEN. When the screen is open, "HCG-PREVIEW: ready &lt;screen&gt;" is printed to the console.
    /// </summary>
    public static string? PreviewScreen { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = HcgThemeVariants.Future;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var platform = Platform ?? throw new InvalidOperationException("MobileApp.Platform must be set by the platform host before start-up.");
            var (shell, view) = CreateShell(platform, Mode);
            singleView.MainView = view;
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Deactivated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Background)
                        shell.OnEnteredBackground();
                };
                activatable.Activated += async (_, e) =>
                {
                    if (e.Kind == ActivationKind.Background)
                        await shell.OnReturnedToForegroundAsync();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds services, the shell view model and its view. Also used by the headless UI tests.</summary>
    public static (MobileShellViewModel Shell, MobileShellView View) CreateShell(IMobilePlatform platform, AppMode mode, Action<IServiceCollection>? configure = null)
    {
        PlatformText.Device = "device";
        var services = MobileServices.Build(platform, mode, configure);
        Services = services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HCG.Mail.Mobile");
        var settings = services.GetRequiredService<AppSettingsService>();
        MobileServices.PrepareStorage(platform, settings, logger);
        InstallErrorHandlers();
        services.GetRequiredService<ThemeService>().ApplySaved();

        var shell = services.GetRequiredService<MobileShellViewModel>();
        var view = new MobileShellView { DataContext = shell };
        services.GetRequiredService<MobileDialogService>().AttachTopLevel(() => TopLevel.GetTopLevel(view));
        var started = false;
        view.AttachedToVisualTree += async (_, _) =>
        {
            if (started)
                return;
            started = true;
            await shell.Mail.InitializeAsync();
            if (mode.IsPreview && PreviewScreen is { Length: > 0 } screen)
                await OpenPreviewScreenAsync(shell, screen);
        };
        return (shell, view);
    }

    private static async Task OpenPreviewScreenAsync(MobileShellViewModel shell, string screen)
    {
        // Simulators on build machines can take a while: wait up to a minute for the sample mailbox.
        for (var i = 0; i < 600 && (shell.Mail.MessageList.Items.Count == 0 || !shell.Mail.ComposeCommand.CanExecute(null)); i++)
            await Task.Delay(100);
        var ready = shell.Mail.MessageList.Items.Count > 0 && shell.Mail.ComposeCommand.CanExecute(null);
        switch (screen)
        {
            case "reader" when ready:
                shell.Mail.MessageList.SelectedItem = shell.Mail.MessageList.Items[0];
                shell.OnMessageChosen(); // iPhone: show the reader page, as a tap does
                break;
            case "compose" when ready:
                shell.Mail.ComposeCommand.Execute(null);
                break;
            case "inbox":
                break;
            default:
                ready = false;
                break;
        }
        await Task.Delay(800); // the screen is laid out and drawn before the screenshot is taken
        // Read by scripts/run-simulator.sh --screen … --screenshot …
        Console.WriteLine(ready
            ? $"HCG-PREVIEW: ready {screen}"
            : $"HCG-PREVIEW: not ready {screen} (messages {shell.Mail.MessageList.Items.Count}, compose {shell.Mail.ComposeCommand.CanExecute(null)})");
    }

    private static bool _errorHandlersInstalled;

    /// <summary>Installed once per process; always reports through the current service provider.</summary>
    private static void InstallErrorHandlers()
    {
        if (_errorHandlersInstalled)
            return;
        _errorHandlersInstalled = true;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (Services is not { } services)
                return;
            services.GetRequiredService<ILoggerFactory>().CreateLogger("HCG.Mail.Mobile").LogError(e.Exception, "Unhandled UI exception");
            services.GetRequiredService<INotificationService>().Error("Something went wrong",
                $"An unexpected error occurred ({e.Exception.GetType().Name}). Nothing was sent or deleted because of it. Details are in the app's log (see Settings › About and diagnostics).");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            (Services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("HCG.Mail.Mobile").LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
