using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Mobile.ViewModels;
using HCG.Mail.Mobile.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.Mail.Mobile.Tests;

/// <summary>Device sizes in points (what iOS reports to the app).</summary>
public static class Devices
{
    public static readonly (double W, double H) IPhone = (390, 844);           // iPhone 15/16
    public static readonly (double W, double H) IPhoneLandscape = (844, 390);
    public static readonly (double W, double H) IPadPortrait = (820, 1180);    // iPad Air 11"
    public static readonly (double W, double H) IPadLandscape = (1180, 820);
    public static readonly (double W, double H) IPadSplitNarrow = (320, 1180); // one-third split view
}

/// <summary>Hosts the real mobile shell headlessly in preview mode (sample mailbox, no network).</summary>
public sealed class MobileHarness : IAsyncDisposable
{
    private MobileHarness(FakeMobilePlatform platform, MobileShellViewModel shell, MobileShellView view, Window window)
    {
        Platform = platform;
        Shell = shell;
        View = view;
        Window = window;
    }

    public FakeMobilePlatform Platform { get; }
    public MobileShellViewModel Shell { get; }
    public MainWindowViewModel Mail => Shell.Mail;
    public MobileOverlays Overlays => Shell.Overlays;
    public MobileShellView View { get; }
    public Window Window { get; }
    public IServiceProvider Services => MobileApp.Services!;

    public static async Task<MobileHarness> StartAsync((double W, double H) size, FakeMobilePlatform? platform = null, Action<IServiceCollection>? configure = null)
    {
        platform ??= new FakeMobilePlatform();
        var (shell, view) = MobileApp.CreateShell(platform, new AppMode(IsPreview: true), configure);
        var window = new Window { Content = view, Width = size.W, Height = size.H };
        window.Show();
        await Settle(600);
        return new MobileHarness(platform, shell, view, window);
    }

    public async Task ResizeAsync((double W, double H) size)
    {
        Window.Width = size.W;
        Window.Height = size.H;
        await Settle();
    }

    public static async Task Settle(int milliseconds = 350)
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

    /// <summary>Waits until a condition holds (async loads, debounced selection) or fails after a timeout.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                Assert.Fail("Timed out waiting for " + what);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    /// <summary>Opens the first message in the current folder the way a tap does.</summary>
    public async Task OpenFirstMessageAsync()
    {
        await WaitUntil(() => Mail.MessageList.Items.Count > 0, "messages");
        Mail.MessageList.SelectedItem = Mail.MessageList.Items[0];
        Shell.OnMessageChosen();
        await WaitUntil(() => Mail.Reader.HasMessage, "the message to load");
        await Settle(200);
    }

    /// <summary>Saves a PNG when HCG_MAIL_SCREENSHOT_DIR is set (visual review; CI uploads them as artifacts).</summary>
    public string? Capture(string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Environment.GetEnvironmentVariable("HCG_MAIL_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir))
            return null;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mobile-" + name + ".png");
        frame!.Save(path);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await Mail.DisposeAsync();
        if (MobileApp.Services is IAsyncDisposable services)
            await services.DisposeAsync();
        Platform.Dispose();
    }
}
