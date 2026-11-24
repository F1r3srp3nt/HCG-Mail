using Foundation;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Html;
using HCG.Mail.Infrastructure;
using HCG.Mail.iOS.Platform;
using UIKit;

namespace HCG.Mail.iOS;

/// <summary>
/// On-device/simulator checks of what headless tests cannot cover: the real iOS Keychain, the app sandbox, the HTML
/// sanitizer under the iOS runtime (AOT/interpreter) and the Microsoft sign-in configuration. Runs only when the
/// process is started with HCG_MAIL_SELF_TEST=1; prints "HCG-SELFTEST: PASS" or "HCG-SELFTEST: FAIL" to the console.
/// Also checks the UIScene life cycle (the window comes from a connected window scene) and the safe-area layout.
/// Uses a throw-away Keychain item and touches no account, password or mailbox.
/// </summary>
public static class SelfTest
{
    public const string Variable = "HCG_MAIL_SELF_TEST";

    public static async Task RunAsync(IosPlatform platform)
    {
        var failures = 0;
        void Report(string name, bool ok, string detail = "")
        {
            if (!ok)
                failures++;
            Console.WriteLine($"HCG-SELFTEST: {(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        try
        {
            Console.WriteLine($"HCG-SELFTEST: start ({platform.Description}, text scale {platform.TextScale:0.00}, tablet {platform.IsTablet})");

            var vault = platform.CredentialVault;
            var key = "hcg-selftest-" + Guid.NewGuid().ToString("N");
            await vault.SetAsync(key, "first");
            await vault.SetAsync(key, "second ✓"); // update path
            var read = await vault.GetAsync(key);
            await vault.DeleteAsync(key);
            var afterDelete = await vault.GetAsync(key);
            await vault.DeleteAsync(key); // deleting a missing item is not an error
            Report("Keychain round trip", read == "second ✓" && afterDelete is null, vault.StorageDescription);

            platform.Paths.EnsureCreated();
            var probe = Path.Combine(platform.Paths.DataDirectory, ".selftest");
            await File.WriteAllTextAsync(probe, "ok");
            File.Delete(probe);
            Report("App data directory", true, platform.Paths.DataDirectory);
            Report("Settings never hold secrets", !File.Exists(platform.Paths.SettingsFile) || !File.ReadAllText(platform.Paths.SettingsFile).Contains("password", StringComparison.OrdinalIgnoreCase));

            // JSON persistence under the iOS linker/AOT: settings and the account list round-trip.
            var jsonDir = Path.Combine(Path.GetTempPath(), "hcg-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new AppPaths(jsonDir);
                var settingsStore = new HCG.Mail.Infrastructure.Storage.JsonAppSettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<HCG.Mail.Infrastructure.Storage.JsonAppSettingsStore>.Instance);
                var saved = settingsStore.Load() with { AutomaticRefreshMinutes = 15, MarkAsReadWhenOpened = false };
                settingsStore.Save(saved);
                var loaded = settingsStore.Load();
                var accounts = new HCG.Mail.Infrastructure.Storage.JsonAccountStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<HCG.Mail.Infrastructure.Storage.JsonAccountStore>.Instance);
                var account = HCG.Mail.App.Preview.PreviewMailSession.SampleAccount;
                accounts.Save([account]);
                Report("Settings and account list persist (JSON)", loaded.AutomaticRefreshMinutes == 15 && !loaded.MarkAsReadWhenOpened
                                                                    && accounts.Load().SingleOrDefault() == account);
            }
            finally
            {
                if (Directory.Exists(jsonDir))
                    Directory.Delete(jsonDir, true);
            }

            var sanitized = new MailHtmlSanitizer().Sanitize(
                "<p>Visible</p><script>alert(1)</script><img src=\"https://tracker.example/p.gif\"><a href=\"javascript:x()\">x</a><img src=\"cid:logo\">",
                new HtmlSanitizeOptions(false));
            Report("HTML sanitizer", !sanitized.Html.Contains("<script", StringComparison.OrdinalIgnoreCase)
                                    && !sanitized.Html.Contains("tracker.example", StringComparison.Ordinal)
                                    && !sanitized.Html.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                                    && sanitized.Html.Contains("cid:logo", StringComparison.Ordinal)
                                    && sanitized.HasVisibleContent);

            var microsoft = platform.MicrosoftAuth;
            Report("Microsoft sign-in configuration", true, microsoft.IsConfigured ? "configured" : "pending — " + microsoft.ConfigurationProblem);

            Report("TLS revocation fallback enabled", new HCG.Mail.Email.MailClientFactory().RevocationFallback is not null);

            // UIScene life cycle (iOS 27 SDK): the window comes from a connected window scene, not from the app delegate.
            // The scene connects after launch, so wait for it before looking at the layout.
            var scene = await WaitForSceneWindowAsync(TimeSpan.FromSeconds(60));
            Report("Scene life cycle", scene.Ok, scene.Detail);

            // Layout: the shell must be inset by the safe area (notch/Dynamic Island, home indicator) exactly once.
            await Task.Delay(1500); // let the first layout pass finish
            var layout = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<(bool Ok, string Detail)>(MeasureSafeAreaLayout);
            Report("Safe area applied once", layout.Ok, layout.Detail);
        }
        catch (Exception ex)
        {
            Report("unexpected exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        Console.WriteLine(failures == 0 ? "HCG-SELFTEST: PASS" : $"HCG-SELFTEST: FAIL ({failures})");
    }

    /// <summary>Polls (on the main thread) until the scene's window shows the Avalonia view, or the time runs out.</summary>
    private static async Task<(bool Ok, string Detail)> WaitForSceneWindowAsync(TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            var result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<(bool Ok, string Detail)>(DescribeSceneWindow);
            if (result.Ok || DateTime.UtcNow - started > timeout)
                return result;
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Info.plist declares the scene manifest, exactly one window scene is connected, its window shows Avalonia's
    /// view controller, and the main view is inside that window (a top level exists).
    /// </summary>
    private static (bool Ok, string Detail) DescribeSceneWindow()
    {
        var manifest = NSBundle.MainBundle.ObjectForInfoDictionary("UIApplicationSceneManifest") is NSDictionary;
        var scenes = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>().ToArray();
        var windows = scenes.SelectMany(scene => scene.Windows).ToArray();
        var window = windows.FirstOrDefault(w => w.IsKeyWindow) ?? windows.FirstOrDefault();
        var controller = window?.RootViewController;
        var attached = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime { MainView: { } view }
                       && Avalonia.Controls.TopLevel.GetTopLevel(view) is not null;
        var ok = manifest && scenes.Length == 1 && window?.WindowScene is not null
                 && controller is Avalonia.iOS.DefaultAvaloniaViewController && attached;
        var detail = FormattableString.Invariant(
            $"scene manifest {(manifest ? "present" : "missing")}; {scenes.Length} window scene(s) {string.Join(", ", scenes.Select(scene => scene.ActivationState))}; window {(window is null ? "none" : $"{controller?.GetType().Name ?? "without controller"}{(window.IsKeyWindow ? ", key" : ", not key")}")}; main view {(attached ? "shown" : "not shown")}");
        return (ok, detail);
    }

    /// <summary>Where the mail area actually sits in the window, compared with the safe area iOS reports.</summary>
    private static (bool Ok, string Detail) MeasureSafeAreaLayout()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime { MainView: { } view }
            || Avalonia.Controls.TopLevel.GetTopLevel(view) is not { } top
            || Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Control>(view, "MailArea") is not { } mailArea
            || Avalonia.VisualExtensions.TranslatePoint(mailArea, new Avalonia.Point(0, 0), top) is not { } origin)
            return (false, "main view or mail area not found");
        var safe = top.InsetsManager?.SafeAreaPadding ?? default;
        var end = Avalonia.VisualExtensions.TranslatePoint(mailArea, new Avalonia.Point(0, mailArea.Bounds.Height), top) ?? origin;
        var bottomGap = top.Bounds.Height - end.Y;
        // The shell itself must fill the window (its bars reach under the status bar and home indicator) and keep the
        // safe area on its own: if Avalonia also padded it, the inset would still be applied once, but the keyboard
        // padding (measured from the screen edge) would leave a gap the height of the home indicator.
        var shellOrigin = Avalonia.VisualExtensions.TranslatePoint(view, new Avalonia.Point(0, 0), top) ?? new Avalonia.Point(-1, -1);
        var shellPadding = (view as Avalonia.Controls.Primitives.TemplatedControl)?.Padding ?? default;
        var fills = Math.Abs(shellOrigin.X) <= 1 && Math.Abs(shellOrigin.Y) <= 1 && Math.Abs(view.Bounds.Height - top.Bounds.Height) <= 1
                    && shellPadding == default;
        var detail = FormattableString.Invariant(
            $"reported top {safe.Top:0.#} bottom {safe.Bottom:0.#}; mail area top {origin.Y:0.#} bottom gap {bottomGap:0.#}; window {top.Bounds.Width:0.#}×{top.Bounds.Height:0.#}, shell at {shellOrigin.Y:0.#} height {view.Bounds.Height:0.#} padding {shellPadding}, top-level padding {top.Padding}, edge-to-edge {top.InsetsManager?.DisplayEdgeToEdgePreference}");
        return (fills && Math.Abs(origin.Y - safe.Top) <= 1 && Math.Abs(bottomGap - safe.Bottom) <= 1, detail);
    }
}
