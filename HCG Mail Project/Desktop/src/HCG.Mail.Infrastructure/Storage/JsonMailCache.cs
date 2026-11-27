using System.Security.Cryptography;
using System.Text;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>
/// The device's copy of mail for offline reading, as JSON files in the private data folder:
/// <c>offline/&lt;account&gt;/folders.json</c>, one file per folder's newest page and one per opened message. File names are
/// hashes, so folder names and subjects do not appear in the file system. Bounded: the most recently opened
/// <see cref="MaxMessagesPerAccount"/> messages are kept; inline images of very large messages are left out.
/// </summary>
public sealed class JsonMailCache : IMailCache
{
    public const int MaxMessagesPerAccount = 400;
    public const long MaxInlineBytes = 4 * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly Func<bool> _enabled;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private int _savesSincePrune;

    public JsonMailCache(AppPaths paths, Func<bool> enabled, ILogger<JsonMailCache> logger)
    {
        _paths = paths;
        _enabled = enabled;
        _logger = logger;
    }

    public bool IsEnabled => _enabled();

    public void SaveFolders(string accountId, IReadOnlyList<MailFolder> folders) =>
        Write(FoldersFile(accountId), new CachedValue<IReadOnlyList<MailFolder>>(folders, DateTimeOffset.Now), MailStorageJsonContext.Default.CachedValueIReadOnlyListMailFolder);

    public CachedValue<IReadOnlyList<MailFolder>>? LoadFolders(string accountId) =>
        Read(FoldersFile(accountId), MailStorageJsonContext.Default.CachedValueIReadOnlyListMailFolder);

    public void SavePage(string accountId, FolderPage page) =>
        Write(PageFile(accountId, page.FolderFullName), new CachedValue<FolderPage>(page, DateTimeOffset.Now), MailStorageJsonContext.Default.CachedValueFolderPage);

    public CachedValue<FolderPage>? LoadPage(string accountId, string folderFullName) =>
        Read(PageFile(accountId, folderFullName), MailStorageJsonContext.Default.CachedValueFolderPage);

    public void SaveMessage(string accountId, MessageContent content)
    {
        if (content.InlineResources.Sum(r => (long)r.Data.Length) > MaxInlineBytes)
            content = content with { InlineResources = [] };
        Write(MessageFile(accountId, content.Ref), new CachedValue<MessageContent>(content, DateTimeOffset.Now), MailStorageJsonContext.Default.CachedValueMessageContent);
        if (Interlocked.Increment(ref _savesSincePrune) % 25 == 0)
            Prune(accountId);
    }

    public CachedValue<MessageContent>? LoadMessage(string accountId, MessageRef message) =>
        Read(MessageFile(accountId, message), MailStorageJsonContext.Default.CachedValueMessageContent);

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
                Write(PageFile(accountId, group.Key), page with { Value = updated }, MailStorageJsonContext.Default.CachedValueFolderPage);
            }
        }
        foreach (var message in messages)
        {
            if (LoadMessage(accountId, message) is { } cached)
                Write(MessageFile(accountId, message), cached with { Value = cached.Value with { Flags = Apply(cached.Value.Flags) } }, MailStorageJsonContext.Default.CachedValueMessageContent);
        }
    }

    public void Remove(string accountId, MessageRef message)
    {
        if (LoadPage(accountId, message.FolderFullName) is { } page)
        {
            var updated = page.Value with { Messages = page.Value.Messages.Where(s => s.Ref.Uid != message.Uid || s.Ref.ServerId != message.ServerId).ToList() };
            Write(PageFile(accountId, message.FolderFullName), page with { Value = updated }, MailStorageJsonContext.Default.CachedValueFolderPage);
        }
        TryDelete(MessageFile(accountId, message));
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
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                if (Directory.Exists(_paths.OfflineDirectory))
                    Directory.Delete(_paths.OfflineDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not delete offline copies ({Error})", ex.GetType().Name);
            }
        }
    }

    private void Prune(string accountId)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.Combine(AccountDir(accountId), "messages");
                if (!Directory.Exists(dir))
                    return;
                var files = new DirectoryInfo(dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                foreach (var old in files.Skip(MaxMessagesPerAccount))
                    old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not prune the offline copy");
            }
        }
    }

    private string AccountDir(string accountId) => Path.Combine(_paths.OfflineDirectory, Hash(accountId));
    private string FoldersFile(string accountId) => Path.Combine(AccountDir(accountId), "folders.json");
    private string PageFile(string accountId, string folder) => Path.Combine(AccountDir(accountId), "pages", Hash(folder) + ".json");
    private string MessageFile(string accountId, MessageRef m) =>
        Path.Combine(AccountDir(accountId), "messages", Hash($"{m.FolderFullName}\u001F{m.UidValidity}\u001F{m.Uid}\u001F{m.ServerId}") + ".json");

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        if (!IsEnabled)
            return;
        lock (_lock)
        {
            try
            {
                JsonFile.Write(path, value, type);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The copy is a convenience: a full or read-only disk must never break reading mail online.
                _logger.LogWarning("Could not save the offline copy ({Error})", ex.GetType().Name);
            }
        }
    }

    private CachedValue<T>? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<CachedValue<T>> type)
    {
        if (!IsEnabled)
            return null;
        lock (_lock)
        {
            try
            {
                return JsonFile.Read(path, type, _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
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
