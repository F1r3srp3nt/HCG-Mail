using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.Mail.App.Tests;

/// <summary>Builds the real application object graph in preview mode and hosts the main window headlessly.</summary>
internal sealed class UiHarness : IAsyncDisposable
{
    private UiHarness(ServiceProvider services, MainWindow window, MainWindowViewModel viewModel)
    {
        Services = services;
        Window = window;
        ViewModel = viewModel;
    }

    public ServiceProvider Services { get; }
    public MainWindow Window { get; }
    public MainWindowViewModel ViewModel { get; }

    public static async Task<UiHarness> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var services = AppServices.Build(new AppMode(IsPreview: true), configure);
        services.GetRequiredService<ThemeService>().ApplySaved();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        services.GetRequiredService<DialogService>().Attach(window);
        window.Show();
        await vm.InitializeAsync();
        await Settle();
        return new UiHarness(services, window, vm);
    }

    /// <summary>Lets queued async work, timers and layout complete.</summary>
    public static async Task Settle(int milliseconds = 400)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Saves a PNG when HCG_MAIL_SCREENSHOT_DIR is set (used for visual review; not required by CI).</summary>
    public static string? Capture(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Environment.GetEnvironmentVariable("HCG_MAIL_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir))
            return null;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        frame!.Save(path);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await ViewModel.DisposeAsync();
        await Services.DisposeAsync();
    }
}
