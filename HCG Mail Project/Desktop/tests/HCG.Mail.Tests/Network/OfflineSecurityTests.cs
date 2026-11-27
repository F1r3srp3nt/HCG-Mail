using System.Text;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Security;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Network;

/// <summary>
/// Offline copies and the Outbox are protected at rest: encrypted per account with keys that exist only in the credential
/// store, isolated between accounts, bounded by retention and size, and gone (keys included) when the user removes them.
/// </summary>
public sealed class OfflineSecurityTests : IDisposable
{
    private const string Secret = "Exam results: Kari Nordmann 5/6";
    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-mail-offline-security-").FullName;
    private readonly SessionCredentialVault _vault = new();
    private OfflineCachePolicy _policy = OfflineCachePolicy.Default;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AccountDataProtector Protector() => new(_vault, NullLogger<AccountDataProtector>.Instance);

    private JsonMailCache Cache(AccountDataProtector? protector = null) =>
        new(new AppPaths(_dir), () => _policy, protector ?? Protector(), NullLogger<JsonMailCache>.Instance);

    private static MessageContent Message(uint uid, string body = Secret) =>
        new() { Ref = new MessageRef("INBOX", uid, 1), Subject = "Grades " + uid, TextBody = body, From = new EmailAddress("Teacher", "teacher@school.example") };

    private IEnumerable<string> AllFiles() =>
        Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public void Cached_mail_is_encrypted_at_rest_and_the_key_is_never_on_disk()
    {
        var cache = Cache();
        cache.SaveMessage("acct-a", Message(1));
        cache.SavePage("acct-a", new FolderPage("INBOX", [new MessageSummary { Ref = new MessageRef("INBOX", 1, 1), Subject = "Grades 1", Preview = Secret }], 1, 1, 1, false));
        cache.SaveFolders("acct-a", [new MailFolder("INBOX", "INBOX", '/', FolderRole.Inbox, true, 0, 1, 1)]);

        var key = Convert.FromBase64String(_vault.GetAsync(CredentialKeysFor("acct-a")).Result!);
        Assert.Equal(32, key.Length);
        foreach (var file in AllFiles())
        {
            var bytes = File.ReadAllBytes(file);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("Exam results", text);
            Assert.DoesNotContain("Grades", text);
            Assert.DoesNotContain("teacher@school.example", text);
            Assert.DoesNotContain(Convert.ToBase64String(key), text);
            Assert.False(bytes.AsSpan().IndexOf(key) >= 0, "the key is stored next to the data");
        }
        Assert.Equal(Secret, cache.LoadMessage("acct-a", new MessageRef("INBOX", 1, 1))!.Value.TextBody);
    }

    private static string CredentialKeysFor(string accountId) => HCG.Mail.Core.Accounts.CredentialKeys.ForOfflineCache(accountId);

    [Fact]
    public void Each_account_has_its_own_key_and_cannot_read_another_accounts_copy()
    {
        var cache = Cache();
        cache.SaveMessage("acct-a", Message(1));
        cache.SaveMessage("acct-b", Message(1, "B's own mail"));
        Assert.NotEqual(_vault.GetAsync(CredentialKeysFor("acct-a")).Result, _vault.GetAsync(CredentialKeysFor("acct-b")).Result);

        // Copy A's encrypted file over B's file for the same message: B must not see A's mail.
        var aFile = Directory.GetFiles(Path.Combine(_dir, "offline", JsonMailCache.AccountFolderName("acct-a"), "messages")).Single();
        var bFile = Directory.GetFiles(Path.Combine(_dir, "offline", JsonMailCache.AccountFolderName("acct-b"), "messages")).Single();
        Assert.Equal(Path.GetFileName(aFile), Path.GetFileName(bFile)); // same message slot in both accounts
        File.Copy(aFile, bFile, overwrite: true);

        Assert.Null(cache.LoadMessage("acct-b", new MessageRef("INBOX", 1, 1)));
        Assert.False(File.Exists(bFile)); // a foreign or altered file is deleted, never shown
        Assert.Equal(Secret, cache.LoadMessage("acct-a", new MessageRef("INBOX", 1, 1))!.Value.TextBody);
    }

    [Fact]
    public void A_file_moved_to_another_slot_of_the_same_account_does_not_decrypt()
    {
        var cache = Cache();
        cache.SaveMessage("acct-a", Message(1));
        cache.SaveMessage("acct-a", Message(2, "second"));
        var files = Directory.GetFiles(Directory.GetDirectories(Path.Combine(_dir, "offline")).Single() + "/messages");
        File.Copy(files[0], files[1], overwrite: true);
        var loaded = new[] { cache.LoadMessage("acct-a", new MessageRef("INBOX", 1, 1)), cache.LoadMessage("acct-a", new MessageRef("INBOX", 2, 1)) };
        Assert.Single(loaded, l => l is not null); // the swapped one is refused
    }

    [Fact]
    public void Removing_an_account_deletes_its_copy_and_its_key_so_leftovers_are_unreadable()
    {
        var cache = Cache();
        cache.SaveMessage("acct-a", Message(1));
        cache.SaveMessage("acct-b", Message(1));
        var leftover = Directory.EnumerateFiles(Path.Combine(_dir, "offline"), "*.bin", SearchOption.AllDirectories).First();
        var copy = Path.Combine(_dir, "leftover.bin");
        File.Copy(leftover, copy);

        cache.DeleteAccount("acct-a");
        cache.DeleteAccount("acct-b");
        Assert.Null(_vault.GetAsync(CredentialKeysFor("acct-a")).Result);
        Assert.Equal(0, cache.SizeOf("acct-a"));
        // Even a copy taken before removal cannot be decrypted any more (crypto-shredding).
        var protector = Protector();
        Assert.Null(protector.Unprotect(JsonMailCache.Purpose, "acct-a", "messages/x", File.ReadAllBytes(copy)));
    }

    [Fact]
    public void Retention_deletes_old_copies()
    {
        var cache = Cache();
        cache.SaveMessage("acct-a", Message(1));
        cache.SaveMessage("acct-a", Message(2));
        var files = Directory.EnumerateFiles(Path.Combine(_dir, "offline"), "*.bin", SearchOption.AllDirectories).Where(f => f.Contains("messages")).ToList();
        File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddDays(-40));
        _policy = _policy with { RetentionDays = 30 };
        cache.EnforcePolicy(["acct-a"]);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "offline"), "*.bin", SearchOption.AllDirectories).Where(f => f.Contains("messages")));
    }

    [Fact]
    public void The_storage_limit_removes_the_oldest_messages_first_and_keeps_the_folder_list()
    {
        _policy = _policy with { StorageLimitMegabytes = 1 };
        var cache = Cache();
        cache.SaveFolders("acct-a", [new MailFolder("INBOX", "INBOX", '/', FolderRole.Inbox, true, 0, 1, 1)]);
        var big = new string('x', 300_000);
        var messages = Path.Combine(_dir, "offline", JsonMailCache.AccountFolderName("acct-a"), "messages");
        for (uint i = 1; i <= 6; i++)
        {
            var before = Directory.Exists(messages) ? Directory.GetFiles(messages).ToHashSet() : [];
            cache.SaveMessage("acct-a", Message(i, big + i));
            var added = Directory.GetFiles(messages).Single(f => !before.Contains(f));
            File.SetLastWriteTimeUtc(added, DateTime.UtcNow.AddMinutes((int)i - 10)); // message 1 is the oldest
        }
        cache.EnforcePolicy(["acct-a"]);
        Assert.True(cache.SizeOf("acct-a") <= 1024 * 1024);
        Assert.NotNull(cache.LoadFolders("acct-a"));
        Assert.NotNull(cache.LoadMessage("acct-a", new MessageRef("INBOX", 6, 1)));
        Assert.Null(cache.LoadMessage("acct-a", new MessageRef("INBOX", 1, 1)));
    }

    [Fact]
    public void Turning_offline_copies_off_deletes_them_and_their_keys()
    {
        var cache = Cache();
        cache.SaveMessage("acct-a", Message(1));
        _policy = _policy with { Enabled = false };
        cache.EnforcePolicy(["acct-a"]);
        Assert.Empty(AllFiles());
        Assert.Null(_vault.GetAsync(CredentialKeysFor("acct-a")).Result);
        cache.SaveMessage("acct-a", Message(1));
        Assert.Empty(AllFiles()); // nothing is written while off
    }

    [Fact]
    public void Copies_of_accounts_that_no_longer_exist_are_removed_at_start_up()
    {
        var cache = Cache();
        cache.SaveMessage("removed-earlier", Message(1));
        cache.SaveMessage("acct-a", Message(1));
        cache.EnforcePolicy(["acct-a"]);
        Assert.Equal(0, cache.SizeOf("removed-earlier"));
        Assert.True(cache.SizeOf("acct-a") > 0);
    }

    [Fact]
    public void Without_the_key_a_copy_from_an_earlier_session_is_never_shown()
    {
        Cache().SaveMessage("acct-a", Message(1));
        // A new session whose credential store no longer has the key (session-only store, or the key was removed).
        var fresh = new JsonMailCache(new AppPaths(_dir), () => _policy, new AccountDataProtector(new SessionCredentialVault(), NullLogger<AccountDataProtector>.Instance),
            NullLogger<JsonMailCache>.Instance);
        Assert.Null(fresh.LoadMessage("acct-a", new MessageRef("INBOX", 1, 1)));
        Assert.False(fresh.SurvivesRestart);
    }

    [Fact]
    public void The_offline_store_has_no_network_dependency()
    {
        // Offline copies are local only: the cache type depends on paths, policy, the key protector and a logger — nothing
        // that could send data anywhere (no HttpClient, socket or remote service).
        var parameters = typeof(JsonMailCache).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name).ToList();
        Assert.Equal(["AppPaths", "Func`1", "AccountDataProtector", "ILogger`1"], parameters);
        var fields = typeof(JsonMailCache).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Select(f => f.FieldType.Namespace ?? "");
        Assert.DoesNotContain(fields, n => n.StartsWith("System.Net", StringComparison.Ordinal));
    }

    // ---- Outbox ------------------------------------------------------------------------------------

    private static OutgoingMessage Outgoing(string subject) =>
        new() { To = [new EmailAddress("Kari", "kari@example.no")], Subject = subject, TextBody = Secret, Attachments = [OutgoingAttachment.FromBytes("report.pdf", null, [0x25, 0x50, 0x44, 0x46])], MessageId = "m1@example.no" };

    [Fact]
    public void Queued_messages_are_encrypted_and_survive_a_restart_with_their_attachments()
    {
        var store = new JsonOutboxStore(new AppPaths(_dir), Protector(), NullLogger<JsonOutboxStore>.Instance);
        store.Put(new OutboxItem("0123456789abcdef", "acct-a", OutboxKind.Send, Outgoing("Report"), DateTimeOffset.Now));
        foreach (var file in AllFiles())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Exam results", text);
            Assert.DoesNotContain("kari@example.no", text);
            Assert.DoesNotContain("Report", text);
        }

        var loaded = Assert.Single(new JsonOutboxStore(new AppPaths(_dir), Protector(), NullLogger<JsonOutboxStore>.Instance).Load());
        Assert.Equal("acct-a", loaded.AccountId);
        Assert.Equal(Secret, loaded.Message.TextBody);
        Assert.Equal([0x25, 0x50, 0x44, 0x46], Assert.Single(loaded.Message.Attachments).Data);
    }

    [Fact]
    public void An_outbox_item_relabelled_to_another_account_is_never_loaded()
    {
        var store = new JsonOutboxStore(new AppPaths(_dir), Protector(), NullLogger<JsonOutboxStore>.Instance);
        store.Put(new OutboxItem("aaaa", "acct-a", OutboxKind.Send, Outgoing("A"), DateTimeOffset.Now));
        store.Put(new OutboxItem("bbbb", "acct-b", OutboxKind.Send, Outgoing("B"), DateTimeOffset.Now));
        var file = Path.Combine(_dir, "outbox", "aaaa.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"acct-a\"", "\"acct-b\""));

        var items = store.Load();
        Assert.Equal("bbbb", Assert.Single(items).Id); // the tampered item is neither sent as B nor as A
    }

    [Fact]
    public void Removing_an_account_discards_only_its_outbox_and_key()
    {
        var store = new JsonOutboxStore(new AppPaths(_dir), Protector(), NullLogger<JsonOutboxStore>.Instance);
        store.Put(new OutboxItem("aaaa", "acct-a", OutboxKind.Send, Outgoing("A"), DateTimeOffset.Now));
        store.Put(new OutboxItem("bbbb", "acct-b", OutboxKind.Send, Outgoing("B"), DateTimeOffset.Now));
        store.DeleteAccount("acct-a");
        Assert.Equal("acct-b", Assert.Single(store.Load()).AccountId);
        Assert.Null(_vault.GetAsync(AccountDataProtector.KeyName(JsonOutboxStore.Purpose, "acct-a")).Result);
        Assert.NotNull(_vault.GetAsync(AccountDataProtector.KeyName(JsonOutboxStore.Purpose, "acct-b")).Result);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Outbox_ids_cannot_point_outside_the_outbox(string id)
    {
        var store = new JsonOutboxStore(new AppPaths(_dir), Protector(), NullLogger<JsonOutboxStore>.Instance);
        Assert.Throws<ArgumentException>(() => store.Put(new OutboxItem(id, "acct", OutboxKind.Draft, new OutgoingMessage(), DateTimeOffset.Now)));
    }

    [Fact]
    public void Clearing_the_offline_copies_never_touches_the_outbox_key()
    {
        var protector = Protector();
        var cache = Cache(protector);
        var store = new JsonOutboxStore(new AppPaths(_dir), protector, NullLogger<JsonOutboxStore>.Instance);
        cache.SaveMessage("acct-a", Message(1));
        store.Put(new OutboxItem("aaaa", "acct-a", OutboxKind.Send, Outgoing("Keep me"), DateTimeOffset.Now));
        cache.Clear(["acct-a"]);
        Assert.Equal("Keep me", Assert.Single(store.Load()).Message.Subject); // unsent mail survives deleting offline copies
    }

    /// <summary>
    /// The key lifecycle in the real operating-system store (Windows Credential Manager on the Windows runners, the login
    /// Keychain on macOS). Skipped on Linux, which has no persistent store HCG Mail can use.
    /// </summary>
    [Fact]
    public async Task Keys_live_only_in_the_os_store_and_are_deleted_with_the_account()
    {
        var vault = CredentialVaultFactory.CreateForCurrentPlatform();
        if (!vault.IsPersistent)
            return;
        var account = "lifecycle-test-" + Guid.NewGuid().ToString("N");
        var cacheKey = AccountDataProtector.KeyName(JsonMailCache.Purpose, account);
        var outboxKey = AccountDataProtector.KeyName(JsonOutboxStore.Purpose, account);
        try
        {
            var protector = new AccountDataProtector(vault, NullLogger<AccountDataProtector>.Instance);
            var cache = new JsonMailCache(new AppPaths(_dir), () => _policy, protector, NullLogger<JsonMailCache>.Instance);
            var outbox = new JsonOutboxStore(new AppPaths(_dir), protector, NullLogger<JsonOutboxStore>.Instance);
            cache.SaveMessage(account, Message(1));
            outbox.Put(new OutboxItem("lifecycle0001", account, OutboxKind.Send, Outgoing("Pending"), DateTimeOffset.Now));

            // Two different 256-bit keys, both in the OS store, neither in any file.
            var keyA = Convert.FromBase64String((await vault.GetAsync(cacheKey))!);
            var keyB = Convert.FromBase64String((await vault.GetAsync(outboxKey))!);
            Assert.Equal(32, keyA.Length);
            Assert.Equal(32, keyB.Length);
            Assert.False(keyA.AsSpan().SequenceEqual(keyB));
            foreach (var file in AllFiles())
            {
                var bytes = File.ReadAllBytes(file);
                Assert.False(bytes.AsSpan().IndexOf(keyA) >= 0 || bytes.AsSpan().IndexOf(keyB) >= 0, "a key is stored in " + file);
                Assert.DoesNotContain(Convert.ToBase64String(keyA), File.ReadAllText(file));
            }

            // A fresh process (new protector, nothing cached in memory) reads both through the OS store.
            var fresh = new AccountDataProtector(vault, NullLogger<AccountDataProtector>.Instance);
            Assert.Equal(Secret, new JsonMailCache(new AppPaths(_dir), () => _policy, fresh, NullLogger<JsonMailCache>.Instance)
                .LoadMessage(account, new MessageRef("INBOX", 1, 1))!.Value.TextBody);
            Assert.Single(new JsonOutboxStore(new AppPaths(_dir), fresh, NullLogger<JsonOutboxStore>.Instance).Load(), i => i.AccountId == account);

            // Removing the account: the copy's files and key go; a copy of a file taken before cannot be decrypted afterwards.
            var saved = Directory.EnumerateFiles(Path.Combine(_dir, "offline"), "*.bin", SearchOption.AllDirectories).First();
            var stolen = File.ReadAllBytes(saved);
            var slot = "messages/" + Path.GetFileNameWithoutExtension(saved);
            cache.DeleteAccount(account);
            outbox.DeleteAccount(account);
            Assert.Null(await vault.GetAsync(cacheKey));
            Assert.Null(await vault.GetAsync(outboxKey));
            Assert.Equal(0, cache.SizeOf(account));
            Assert.Empty(new JsonOutboxStore(new AppPaths(_dir), fresh, NullLogger<JsonOutboxStore>.Instance).Load().Where(i => i.AccountId == account));
            var afterRemoval = new AccountDataProtector(vault, NullLogger<AccountDataProtector>.Instance);
            Assert.Null(afterRemoval.Unprotect(JsonMailCache.Purpose, account, slot, stolen));
        }
        finally
        {
            await vault.DeleteAsync(cacheKey);
            await vault.DeleteAsync(outboxKey);
        }
    }
}
