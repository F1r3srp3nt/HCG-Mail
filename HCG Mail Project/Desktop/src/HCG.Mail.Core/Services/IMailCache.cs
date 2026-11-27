using HCG.Mail.Core.Mail;

namespace HCG.Mail.Core.Services;

public sealed record CachedValue<T>(T Value, DateTimeOffset SavedAt);

/// <summary>
/// The copy of each account's mail kept on this device for reading without a connection: the folder list, the newest page of
/// each opened folder and the messages that were opened. Stored in the user's private HCG Mail data folder, removed with the
/// account, and not kept at all when the user turns offline copies off in Settings. Attachments themselves are not stored.
/// </summary>
public interface IMailCache
{
    bool IsEnabled { get; }

    void SaveFolders(string accountId, IReadOnlyList<MailFolder> folders);
    CachedValue<IReadOnlyList<MailFolder>>? LoadFolders(string accountId);

    void SavePage(string accountId, FolderPage page);
    CachedValue<FolderPage>? LoadPage(string accountId, string folderFullName);

    void SaveMessage(string accountId, MessageContent content);
    CachedValue<MessageContent>? LoadMessage(string accountId, MessageRef message);

    /// <summary>Keeps cached flags in step with a change made on the server.</summary>
    void UpdateFlags(string accountId, IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add);

    /// <summary>Drops a message that was deleted or moved on the server.</summary>
    void Remove(string accountId, MessageRef message);

    void DeleteAccount(string accountId);

    void Clear();
}

/// <summary>For preview mode and tests: keeps nothing.</summary>
public sealed class NoMailCache : IMailCache
{
    public bool IsEnabled => false;
    public void SaveFolders(string accountId, IReadOnlyList<MailFolder> folders) { }
    public CachedValue<IReadOnlyList<MailFolder>>? LoadFolders(string accountId) => null;
    public void SavePage(string accountId, FolderPage page) { }
    public CachedValue<FolderPage>? LoadPage(string accountId, string folderFullName) => null;
    public void SaveMessage(string accountId, MessageContent content) { }
    public CachedValue<MessageContent>? LoadMessage(string accountId, MessageRef message) => null;
    public void UpdateFlags(string accountId, IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add) { }
    public void Remove(string accountId, MessageRef message) { }
    public void DeleteAccount(string accountId) { }
    public void Clear() { }
}

/// <summary>
/// A session that can keep working from the device's copy when the server cannot be reached, and tell the app so.
/// </summary>
public interface IOfflineAwareSession
{
    /// <summary>True from a connection failure until a connection succeeds again; reads are then served from the device's copy.</summary>
    bool IsOffline { get; }

    /// <summary>The failure that made the session go offline.</summary>
    Errors.MailOperationException? OfflineReason { get; }

    /// <summary>When the copy that was last shown was saved (null when nothing was served from the copy).</summary>
    DateTimeOffset? CopySavedAt { get; }

    event EventHandler? OfflineChanged;

    /// <summary>
    /// The network changed or the computer woke up: the open connection is probably dead (NAT and Wi-Fi switches drop it
    /// silently). The next operation reconnects at once instead of waiting for a timeout.
    /// </summary>
    void MarkConnectionStale();
}

/// <summary>Implemented by sessions that hold a long-lived connection which can go stale.</summary>
public interface IStaleConnectionAware
{
    void MarkConnectionStale();
}
