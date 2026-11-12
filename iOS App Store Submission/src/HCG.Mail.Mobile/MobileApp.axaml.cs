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
        };
        return (shell, view);
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
