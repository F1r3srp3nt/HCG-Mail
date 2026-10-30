using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Infrastructure;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-mail-store-").FullName;
    private AppPaths Paths => new(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static MailAccount Account(string id = "acc1") => new()
    {
        Id = id,
        DisplayName = "Ola",
        EmailAddress = "ola@firma.no",
        Username = "ola@firma.no",
        Imap = new ServerEndpoint("mail.firma.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.firma.no", 587, MailSecurityMode.StartTls),
        PresetId = "domene.no",
    };

    [Fact]
    public void Theme_and_settings_persist_across_store_instances()
    {
        var store = new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance);
        Assert.Equal(ThemeKind.HcgFuture, store.Load().Theme);

        store.Save(new AppSettings { Theme = ThemeKind.HcgClassic, RefreshIntervalMinutes = 15, NotifyOnNewMail = false, DownloadDirectory = "/tmp/x" });

        var reloaded = new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance).Load();
        Assert.Equal(ThemeKind.HcgClassic, reloaded.Theme);
        Assert.Equal(15, reloaded.RefreshIntervalMinutes);
        Assert.False(reloaded.NotifyOnNewMail);
        Assert.Equal("/tmp/x", reloaded.DownloadDirectory);
        Assert.Contains("\"HcgClassic\"", File.ReadAllText(Paths.SettingsFile));
    }

    [Fact]
    public void Corrupted_settings_file_falls_back_to_defaults_and_is_kept_for_inspection()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Paths.SettingsFile, "{ this is not json");
        var settings = new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance).Load();
        Assert.Equal(new AppSettings(), settings);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Out_of_range_values_are_normalized()
    {
        var store = new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance);
        store.Save(new AppSettings { RefreshIntervalMinutes = 7, Theme = (ThemeKind)42 });
        var loaded = store.Load();
        Assert.Equal(5, loaded.RefreshIntervalMinutes);
        Assert.Equal(ThemeKind.HcgFuture, loaded.Theme);
    }

    [Fact]
    public void Accounts_round_trip_without_any_secret()
    {
        var store = new JsonAccountStore(Paths, NullLogger<JsonAccountStore>.Instance);
        store.Save([Account(), Account("acc2") with { EmailAddress = "kari@firma.no", Username = "kari" }]);

        var loaded = new JsonAccountStore(Paths, NullLogger<JsonAccountStore>.Instance).Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(Account(), loaded[0]);
        Assert.Equal(MailSecurityMode.StartTls, loaded[0].Smtp.Security);

        var json = File.ReadAllText(Paths.AccountsFile);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_account_entries_are_skipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Paths.AccountsFile, """
            { "version": 1, "accounts": [
              { "id": "ok", "displayName": "A", "emailAddress": "a@firma.no", "username": "a@firma.no",
                "imap": { "host": "mail.firma.no", "port": 993, "security": "SslTls" },
                "smtp": { "host": "mail.firma.no", "port": 465, "security": "SslTls" } },
              { "id": "bad", "emailAddress": "b@firma.no", "username": "b", "imap": { "host": "", "port": 0 } }
            ] }
            """);
        var loaded = new JsonAccountStore(Paths, NullLogger<JsonAccountStore>.Instance).Load();
        Assert.Equal("ok", Assert.Single(loaded).Id);
    }

    [Fact]
    public void Missing_files_mean_empty_state()
    {
        Assert.Empty(new JsonAccountStore(Paths, NullLogger<JsonAccountStore>.Instance).Load());
        Assert.Equal(new AppSettings(), new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance).Load());
    }

    [Fact]
    public void Data_files_are_private_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        new JsonAppSettingsStore(Paths, NullLogger<JsonAppSettingsStore>.Instance).Save(new AppSettings());
        var mode = File.GetUnixFileMode(Paths.SettingsFile);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}

public class CredentialVaultTests
{
    [Fact]
    public async Task Session_vault_stores_in_memory_only()
    {
        var vault = new SessionCredentialVault();
        Assert.False(vault.IsPersistent);
        Assert.Null(await vault.GetAsync("account:x"));

        await vault.SetAsync("account:x", "s3cret");
        Assert.Equal("s3cret", await vault.GetAsync("account:x"));

        await vault.SetAsync("account:x", "changed");
        Assert.Equal("changed", await vault.GetAsync("account:x"));

        await vault.DeleteAsync("account:x");
        Assert.Null(await vault.GetAsync("account:x"));
        await vault.DeleteAsync("account:missing");
    }

    [Fact]
    public void Factory_selects_os_vault()
    {
        var vault = CredentialVaultFactory.CreateForCurrentPlatform();
        if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsCredentialVault>(vault);
        else if (OperatingSystem.IsMacOS())
            Assert.IsType<MacKeychainCredentialVault>(vault);
        else
            Assert.IsType<SessionCredentialVault>(vault);
    }

    /// <summary>
    /// Exercises the real OS vault (Windows Credential Manager / macOS Keychain) with a throwaway key.
    /// Runs only on those platforms; on Linux the secure store is not implemented and this is a no-op.
    /// </summary>
    [Fact]
    public async Task Os_vault_round_trip_when_available()
    {
        var vault = CredentialVaultFactory.CreateForCurrentPlatform();
        if (!vault.IsPersistent)
            return;
        var key = "test:" + Guid.NewGuid().ToString("N");
        try
        {
            await vault.SetAsync(key, "pässwörd-✓");
            Assert.Equal("pässwörd-✓", await vault.GetAsync(key));
            await vault.SetAsync(key, "second");
            Assert.Equal("second", await vault.GetAsync(key));
        }
        finally
        {
            await vault.DeleteAsync(key);
        }
        Assert.Null(await vault.GetAsync(key));
    }

    [Fact]
    public void Credential_key_is_derived_from_account_id_only()
    {
        Assert.Equal("account:abc", CredentialKeys.ForAccount("abc"));
    }
}
