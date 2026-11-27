using System.Runtime.CompilerServices;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Infrastructure;

/// <summary>
/// People who installed 1.0.0-beta.1 keep their accounts and settings in a newer version: the files beta.1 wrote
/// (tests/fixtures/beta1-user-data) are read unchanged, and reading them does not rewrite them.
/// </summary>
public sealed class StorageCompatibilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-mail-beta1-data-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string FixtureDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "fixtures", "beta1-user-data"));

    [Fact]
    public void Data_written_by_beta_1_is_read_unchanged()
    {
        foreach (var name in new[] { "accounts.json", "settings.json" })
            File.Copy(Path.Combine(FixtureDir(), name), Path.Combine(_dir, name));
        var paths = new AppPaths(_dir);
        var accountsBefore = File.ReadAllBytes(paths.AccountsFile);
        var settingsBefore = File.ReadAllBytes(Path.Combine(_dir, "settings.json"));

        var account = Assert.Single(new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Load());
        Assert.Equal("beta1-upgrade-test", account.Id);
        Assert.Equal("test@example.com", account.EmailAddress);
        Assert.Equal("Upgrade test (sanitized)", account.Description);
        Assert.Equal(new ServerEndpoint("mail.example.com", 993, MailSecurityMode.SslTls), account.Imap);
        Assert.Equal(new ServerEndpoint("mail.example.com", 465, MailSecurityMode.SslTls), account.Smtp);
        Assert.Equal(MailProviderKind.ImapSmtp, account.Provider);
        Assert.Equal("account:beta1-upgrade-test", account.CredentialKey);   // the saved password is found under the same name

        var settings = new JsonAppSettingsStore(paths, NullLogger<JsonAppSettingsStore>.Instance).Load();
        Assert.Equal(ThemeKind.HcgClassic, settings.Theme);
        Assert.False(settings.LoadRemoteImagesAutomatically);

        Assert.Equal(accountsBefore, File.ReadAllBytes(paths.AccountsFile));
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(_dir, "settings.json")));
    }
}
