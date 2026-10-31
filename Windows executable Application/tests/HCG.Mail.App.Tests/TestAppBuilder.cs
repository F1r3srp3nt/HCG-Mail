using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(HCG.Mail.App.Tests.TestAppBuilder))]

namespace HCG.Mail.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
