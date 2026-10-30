using Avalonia;

namespace HCG.Mail.App;

internal static class Program
{
    /// <summary>Starts HCG Mail. Pass <c>--preview</c> to run with labelled sample data and no network access.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(CredentialStoreCheck.Argument, StringComparer.OrdinalIgnoreCase))
            return CredentialStoreCheck.Run();

        var preview = args.Contains("--preview", StringComparer.OrdinalIgnoreCase)
                      || Environment.GetEnvironmentVariable("HCG_MAIL_PREVIEW") == "1";
        App.Mode = new ViewModels.AppMode(preview);

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

    // Also used by the Avalonia designer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
