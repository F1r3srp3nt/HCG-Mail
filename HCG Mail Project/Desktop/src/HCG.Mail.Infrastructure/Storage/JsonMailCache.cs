using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>
/// The device's copy of mail for offline reading.
/// <list type="bullet">
/// <item><b>Encrypted at rest</b>: every file is AES-256-GCM encrypted with the account's own key, which lives only in the
/// operating system's credential store (<see cref="AccountDataProtector"/>), never on disk next to the data.</item>
/// <item><b>Isolated per account</b>: one folder and one key per account; a file is bound to its account and its place, so it
/// cannot be read through another account.</item>
/// <item><b>Bounded</b>: copies older than the retention period are deleted, and each account's copy is kept under its
/// storage limit by deleting the oldest messages first.</item>
/// <item><b>Local only</b>: nothing here is sent anywhere. HCG Mail never uploads offline copies to HCG or anyone else.</item>
/// </list>
/// File names are hashes, so folder names and subjects do not appear in the file system. Deleting an account deletes its
/// folder and its key; a copy that cannot be decrypted (key gone, file moved or altered) is deleted when found.
/// </summary>
public sealed class JsonMailCache : IMailCache
{
    public const string Purpose = "offline-cache";
    public const long MaxInlineBytes = 4 * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly Func<OfflineCachePolicy> _policy;
    private readonly AccountDataProtector _protector;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private int _savesSincePrune;

    public JsonMailCache(AppPaths paths, Func<OfflineCachePolicy> policy, AccountDataProtector protector, ILogger<JsonMailCache> logger)
    {
        _paths = paths;
        _policy = policy;
        _protector = protector;
        _logger = logger;
    }

    public bool IsEnabled => _policy().Enabled && UnavailableReason is null;

    public string? UnavailableReason => AccountDataProtector.IsSupported ? null : "This system does not support the encryption HCG Mail requires for offline copies.";

    public bool SurvivesRestart => _protector.KeysArePersistent;

    public void SaveFolders(string accountId, IReadOnlyList<MailFolder> folders) =>
        Write(accountId, "folders", new CachedValue<IReadOnlyList<MailFolder>>(folders, DateTimeOffset.Now), MailStorageJsonContext.Default.CachedValueIReadOnlyListMailFolder);

    public CachedValue<IReadOnlyList<MailFolder>>? LoadFolders(string accountId) =>
        Read(accountId, "folders", MailStorageJsonContext.Default.CachedValueIReadOnlyListMailFolder);

    public void SavePage(string accountId, FolderPage page) =>
        Write(accountId, PageSlot(page.FolderFullName), new CachedValue<FolderPage>(page, DateTimeOffset.Now), MailStorageJsonContext.Default.CachedValueFolderPage);

    public CachedValue<FolderPage>? LoadPage(string accountId, string folderFullName) =>
        Read(accountId, PageSlot(folderFullName), MailStorageJsonContext.Default.CachedValueFolderPage);

    public void SaveMessage(string accountId, MessageContent content)
    {
        if (content.InlineResources.Sum(r => (long)r.Data.Length) > MaxInlineBytes)
            content = content with { InlineResources = [] };
        Write(accountId, MessageSlot(content.Ref), new CachedValue<MessageContent>(content, DateTimeOffset.Now), MailStorageJsonContext.Default.CachedValueMessageContent);
        if (Interlocked.Increment(ref _savesSincePrune) % 20 == 0)
            Prune(accountId);
    }

    public CachedValue<MessageContent>? LoadMessage(string accountId, MessageRef message) =>
        Read(accountId, MessageSlot(message), MailStorageJsonContext.Default.CachedValueMessageContent);

    public void UpdateFlags(string accountId, IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add)
    {
        if (!IsEnabled)
            return;
        MessageFlags Apply(MessageFlags f) => add ? f | flags : f & ~flags;
        foreach (var group in messages.GroupBy(m => m.FolderFullName))
        {
            if (LoadPage(accountId, group.Key) is { } page)
            {
                var uids = group.Select(m => m.Uid).ToHashSet();
                var updated = page.Value with
                {
                    Messages = page.Value.Messages.Select(s => uids.Contains(s.Ref.Uid) ? s with { Flags = Apply(s.Flags) } : s).ToList(),
                };
                Write(accountId, PageSlot(group.Key), page with { Value = updated }, MailStorageJsonContext.Default.CachedValueFolderPage);
            }
        }
        foreach (var message in messages)
        {
            if (LoadMessage(accountId, message) is { } cached)
                Write(accountId, MessageSlot(message), cached with { Value = cached.Value with { Flags = Apply(cached.Value.Flags) } }, MailStorageJsonContext.Default.CachedValueMessageContent);
        }
    }

    public void Remove(string accountId, MessageRef message)
    {
        if (LoadPage(accountId, message.FolderFullName) is { } page)
        {
            var updated = page.Value with { Messages = page.Value.Messages.Where(s => s.Ref.Uid != message.Uid || s.Ref.ServerId != message.ServerId).ToList() };
            Write(accountId, PageSlot(message.FolderFullName), page with { Value = updated }, MailStorageJsonContext.Default.CachedValueFolderPage);
        }
        TryDelete(FileFor(accountId, MessageSlot(message)));
    }

    public void DeleteAccount(string accountId)
    {
        lock (_lock)
        {
            try
            {
                var dir = AccountDir(accountId);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not delete the offline copy of account {AccountId} ({Error})", accountId, ex.GetType().Name);
            }
            // Even if a file could not be deleted, without its key it can no longer be read.
            _protector.DeleteKey(Purpose, accountId);
        }
    }

    public void Clear(IEnumerable<string> accountIds)
    {
        foreach (var id in accountIds)
            DeleteAccount(id);
        lock (_lock)
        {
            try
            {
                // Folders of accounts removed earlier (their keys are already gone, so these are unreadable leftovers).
                if (Directory.Exists(_paths.OfflineDirectory))
                    Directory.Delete(_paths.OfflineDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not delete offline copies ({Error})", ex.GetType().Name);
            }
        }
    }

    public void EnforcePolicy(IEnumerable<string> accountIds)
    {
        var ids = accountIds.ToList();
        if (!_policy().Enabled)
        {
            Clear(ids);
            return;
        }
        foreach (var id in ids)
            Prune(id);
        // Folders that belong to no current account: delete them (their account was removed).
        lock (_lock)
        {
            if (!Directory.Exists(_paths.OfflineDirectory))
                return;
            var known = ids.Select(Hash).ToHashSet(StringComparer.Ordinal);
            foreach (var dir in Directory.EnumerateDirectories(_paths.OfflineDirectory))
            {
                if (!known.Contains(Path.GetFileName(dir)))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Could not delete an orphaned offline copy");
                    }
                }
            }
        }
    }

    public long SizeOf(string accountId)
    {
        lock (_lock)
        {
            var dir = AccountDir(accountId);
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
    }

    /// <summary>Deletes copies older than the retention period, then the oldest messages until the account is under its limit.</summary>
    private void Prune(string accountId)
    {
        var policy = _policy();
        lock (_lock)
        {
            try
            {
                var dir = AccountDir(accountId);
                if (!Directory.Exists(dir))
                    return;
                var cutoff = DateTime.UtcNow - policy.Retention;
                var files = new DirectoryInfo(dir).EnumerateFiles("*.bin", SearchOption.AllDirectories).ToList();
                foreach (var old in files.Where(f => f.LastWriteTimeUtc < cutoff))
                    old.Delete();
                files = files.Where(f => f.Exists && f.LastWriteTimeUtc >= cutoff).ToList();
                var total = files.Sum(f => f.Length);
                // Oldest messages go first; the folder list and pages (small, needed to browse) go last.
                foreach (var file in files.OrderBy(f => f.Name == "folders.bin" ? 2 : f.Directory?.Name == "pages" ? 1 : 0).ThenBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= policy.StorageLimitBytes)
                        break;
                    total -= file.Length;
                    file.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not prune the offline copy");
            }
        }
    }

    private static string PageSlot(string folder) => "pages/" + Hash(folder);

    private static string MessageSlot(MessageRef m) => "messages/" + Hash($"{m.FolderFullName}\u001F{m.UidValidity}\u001F{m.Uid}\u001F{m.ServerId}");

    private string AccountDir(string accountId) => Path.Combine(_paths.OfflineDirectory, Hash(accountId));

    private string FileFor(string accountId, string slot) => Path.Combine(AccountDir(accountId), slot.Replace('/', Path.DirectorySeparatorChar) + ".bin");

    internal static string AccountFolderName(string accountId) => Hash(accountId);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private void Write<T>(string accountId, string slot, T value, JsonTypeInfo<T> type)
    {
        if (!IsEnabled)
            return;
        lock (_lock)
        {
            try
            {
                var data = _protector.Protect(Purpose, accountId, slot, JsonSerializer.SerializeToUtf8Bytes(value, type));
                PrivateFile.WriteAllBytes(FileFor(accountId, slot), data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or CryptographicException
                                           or Core.Security.CredentialVaultException)
            {
                // The copy is a convenience: a full disk or an unavailable key store must never break reading mail online.
                _logger.LogWarning("Could not save the offline copy ({Error})", ex.GetType().Name);
            }
        }
    }

    private CachedValue<T>? Read<T>(string accountId, string slot, JsonTypeInfo<CachedValue<T>> type)
    {
        if (!IsEnabled)
            return null;
        lock (_lock)
        {
            var path = FileFor(accountId, slot);
            try
            {
                if (!File.Exists(path))
                    return null;
                var plain = _protector.Unprotect(Purpose, accountId, slot, File.ReadAllBytes(path));
                if (plain is null)
                {
                    // Not decryptable with this account's key: moved, altered, or its key is gone. Never shown; removed.
                    File.Delete(path);
                    return null;
                }
                var value = JsonSerializer.Deserialize(plain, type);
                if (value is not null && value.SavedAt < DateTimeOffset.Now - _policy().Retention)
                {
                    File.Delete(path);
                    return null;
                }
                return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException
                                           or Core.Security.CredentialVaultException)
            {
                _logger.LogDebug(ex, "Could not read the offline copy");
                return null;
            }
        }
    }

    private void TryDelete(string path)
    {
        lock (_lock)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Atomic writes of private files (readable only by the user on macOS, iOS and Linux).</summary>
internal static class PrivateFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(path)!;
        AppPaths.CreatePrivateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
