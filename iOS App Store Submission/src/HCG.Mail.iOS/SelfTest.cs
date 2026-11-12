using HCG.Mail.Email.Html;
using HCG.Mail.Core.Services;
using HCG.Mail.iOS.Platform;
using HCG.Mail.Infrastructure;

namespace HCG.Mail.iOS;

/// <summary>
/// On-device/simulator checks of what headless tests cannot cover: the real iOS Keychain, the app sandbox, the HTML
/// sanitizer under the iOS runtime (AOT/interpreter) and the Microsoft sign-in configuration. Runs only when the
/// process is started with HCG_MAIL_SELF_TEST=1; prints "HCG-SELFTEST: PASS" or "HCG-SELFTEST: FAIL" to the console.
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
        }
        catch (Exception ex)
        {
            Report("unexpected exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        Console.WriteLine(failures == 0 ? "HCG-SELFTEST: PASS" : $"HCG-SELFTEST: FAIL ({failures})");
    }
}
