using HCG.Mail.Core.Mail;

namespace HCG.Mail.Core.Services;

public sealed record CachedValue<T>(T Value, DateTimeOffset SavedAt);

/// <summary>What the user chose for offline copies (Settings › Storage).</summary>
public sealed record OfflineCachePolicy(bool Enabled, int RetentionDays, int StorageLimitMegabytes)
{
    public static readonly IReadOnlyList<int> RetentionChoices = [7, 30, 90, 365];
    public static readonly IReadOnlyList<int> StorageLimitChoices = [50, 100, 250, 500, 1000];
    public static OfflineCachePolicy Default { get; } = new(true, 30, 250);

    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
    public long StorageLimitBytes => StorageLimitMegabytes * 1024L * 1024L;
}

/// <summary>
/// The copy of each account's mail kept on this device for reading without a connection: the folder list, the newest page of
/// each opened folder and the messages that were opened. Stored in the user's private HCG Mail data folder, removed with the
/// account, and not kept at all when the user turns offline copies off in Settings. Attachments themselves are not stored.
/// </summary>
public interface IMailCache
{
    bool IsEnabled { get; }

    /// <summary>Why offline copies cannot be kept on this system, or null when they can.</summary>
    string? UnavailableReason { get; }

    /// <summary>True when copies survive a restart (their keys are in a persistent credential store).</summary>
    bool SurvivesRestart { get; }

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

    /// <summary>Deletes the account's copy and its encryption key.</summary>
    void DeleteAccount(string accountId);

    /// <summary>Deletes the copies and keys of these accounts.</summary>
    void Clear(IEnumerable<string> accountIds);

    /// <summary>Applies retention and storage limits (at start-up and after the user changes them).</summary>
    void EnforcePolicy(IEnumerable<string> accountIds);

    /// <summary>Bytes the account's copy uses on disk.</summary>
    long SizeOf(string accountId);
}

/// <summary>For preview mode and tests: keeps nothing.</summary>
public sealed class NoMailCache : IMailCache
{
    public bool IsEnabled => false;
    public string? UnavailableReason => "Offline copies are not kept in this mode.";
    public bool SurvivesRestart => false;
    public void Clear(IEnumerable<string> accountIds) { }
    public void EnforcePolicy(IEnumerable<string> accountIds) { }
    public long SizeOf(string accountId) => 0;
    public void SaveFolders(string accountId, IReadOnlyList<MailFolder> folders) { }
    public CachedValue<IReadOnlyList<MailFolder>>? LoadFolders(string accountId) => null;
    public void SavePage(string accountId, FolderPage page) { }
    public CachedValue<FolderPage>? LoadPage(string accountId, string folderFullName) => null;
    public void SaveMessage(string accountId, MessageContent content) { }
    public CachedValue<MessageContent>? LoadMessage(string accountId, MessageRef message) => null;
    public void UpdateFlags(string accountId, IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add) { }
    public void Remove(string accountId, MessageRef message) { }
    public void DeleteAccount(string accountId) { }
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
