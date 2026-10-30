using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.App;

public partial class App : Application
{
    internal static AppMode Mode { get; set; } = new(false);

    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // HCG resources exist only for the HCG skins, so never run on a plain Light/Dark variant.
        RequestedThemeVariant = Themes.HcgThemeVariants.Future;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Build(Mode);
            Services = services;
            InstallErrorHandlers(services);

            services.GetRequiredService<ThemeService>().ApplySaved();
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            services.GetRequiredService<DialogService>().Attach(window);
            window.Opened += async (_, _) =>
            {
                services.GetRequiredService<NotificationService>().Attach(window);
                await viewModel.InitializeAsync();
            };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                try
                {
                    viewModel.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                }
                catch (Exception)
                {
                    // Best effort: connections are closed by the OS when the process exits.
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InstallErrorHandlers(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HCG.Mail.App");
        var notifications = services.GetRequiredService<INotificationService>();

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled UI exception");
            notifications.Error("Something went wrong", "An unexpected error occurred. Details were written to the log file.");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception");
    }
}
