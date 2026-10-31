using Avalonia;

namespace HCG.Mail.App;

internal static class Program
{
    public const string DeveloperPreviewArgument = "--developer-preview";

    /// <summary>Starts HCG Mail with the user's real accounts.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(CredentialStoreCheck.Argument, StringComparer.OrdinalIgnoreCase))
            return CredentialStoreCheck.Run();

        App.Mode = new ViewModels.AppMode(IsDeveloperPreviewRequested(args));

        var builder = BuildAvaloniaApp();
        // Troubleshooting switch for machines whose GPU path does not render (some VMs, RDP, old drivers).
        if (args.Contains("--software-rendering", StringComparer.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("HCG_MAIL_SOFTWARE_RENDERING") == "1")
        {
            builder = builder
                .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
                .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
                .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Software] });
        }
        return builder.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Sample data is never loaded on a normal start. The labelled development preview exists only in
    /// Debug builds and only when explicitly launched with <c>--developer-preview</c>; Release builds
    /// (everything given to users) ignore the switch. No environment variable can enable it.
    /// </summary>
    internal static bool IsDeveloperPreviewRequested(IReadOnlyCollection<string> args)
    {
#if DEBUG
        return args.Contains(DeveloperPreviewArgument, StringComparer.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    // Also used by the Avalonia designer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
