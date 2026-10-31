using System.Text;
using HCG.Mail.Core.Security;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;

namespace HCG.Mail.App;

/// <summary>
/// <c>HCGMail --check-credential-store</c>: verifies that the OS credential store works on this
/// machine by writing, reading, updating and deleting a throwaway entry with a dummy secret.
/// No account data is touched. Writes a report to the settings folder and returns 0 on success.
/// </summary>
internal static class CredentialStoreCheck
{
    public const string Argument = "--check-credential-store";

    public static int Run()
    {
        var report = new StringBuilder();
        var ok = true;
        ICredentialVault vault = CredentialVaultFactory.CreateForCurrentPlatform();
        report.AppendLine($"HCG Mail credential store check — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Platform: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        report.AppendLine($"Store: {vault.StorageDescription} (persistent: {vault.IsPersistent})");

        var key = "self-test:" + Guid.NewGuid().ToString("N");
        void Step(string name, Func<bool> check)
        {
            try
            {
                var passed = check();
                ok &= passed;
                report.AppendLine($"{(passed ? "PASS" : "FAIL")}  {name}");
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Step("write", () => { vault.SetAsync(key, "dummy-ÆØÅ-✓").GetAwaiter().GetResult(); return true; });
        Step("read back", () => vault.GetAsync(key).GetAwaiter().GetResult() == "dummy-ÆØÅ-✓");
        Step("update", () => { vault.SetAsync(key, "dummy-2").GetAwaiter().GetResult(); return vault.GetAsync(key).GetAwaiter().GetResult() == "dummy-2"; });
        Step("delete", () => { vault.DeleteAsync(key).GetAwaiter().GetResult(); return vault.GetAsync(key).GetAwaiter().GetResult() is null; });
        report.AppendLine(ok ? "RESULT: OK" : "RESULT: FAILED");

        var text = report.ToString();
        Console.Write(text);
        try
        {
            var paths = AppPaths.ForCurrentUser();
            paths.EnsureCreated();
            File.WriteAllText(Path.Combine(paths.DataDirectory, "credential-store-check.txt"), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return ok ? 0 : 1;
    }
}
