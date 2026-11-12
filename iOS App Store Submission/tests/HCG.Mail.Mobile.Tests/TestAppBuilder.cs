using Avalonia;
using Avalonia.Headless;
using HCG.Mail.Mobile;

[assembly: AvaloniaTestApplication(typeof(HCG.Mail.Mobile.Tests.TestAppBuilder))]
// The mobile shell, theme and platform statics are process-wide; run UI tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HCG.Mail.Mobile.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MobileApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
