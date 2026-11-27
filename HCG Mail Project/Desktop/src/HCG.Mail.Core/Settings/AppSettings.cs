namespace HCG.Mail.Core.Settings;

public enum ThemeKind
{
    HcgFuture,
    HcgClassic,
}

/// <remarks>
/// Refresh is manual unless the user turns on <see cref="AutomaticRefreshMinutes"/>. Settings files from
/// older builds that contain "refreshIntervalMinutes" or "notifyOnNewMail" still load and those fields stay
/// ignored, so upgrading never switches background checking on by itself.
/// </remarks>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public ThemeKind Theme { get; init; } = ThemeKind.HcgFuture;

    /// <summary>Where attachments are saved when the user chooses "Save" without picking a location; null means the user's Downloads folder.</summary>
    public string? DownloadDirectory { get; init; }

    /// <summary>Always ask where to save each attachment instead of using the download folder.</summary>
    public bool AskWhereToSaveAttachments { get; init; }

    public bool ConnectOnStartup { get; init; } = true;
    public bool MarkAsReadWhenOpened { get; init; } = true;
    public bool SaveCopyToSentFolder { get; init; } = true;
    public bool ConfirmBeforeOpeningLinks { get; init; } = true;

    /// <summary>How HTML messages open: the sender's layout (default) or Readable view.</summary>
    public Services.MessageViewMode DefaultMessageView { get; init; } = Services.MessageViewMode.Original;

    /// <summary>
    /// Load remote images in every message without asking. Off by default: loading them tells the sender that and
    /// when the message was opened, and reveals the computer's IP address.
    /// </summary>
    public bool LoadRemoteImagesAutomatically { get; init; }

    /// <summary>0 = manual refresh only (default). Otherwise the open folder is checked every N minutes while HCG Mail runs.</summary>
    public int AutomaticRefreshMinutes { get; init; }

    public static readonly IReadOnlyList<int> AutomaticRefreshChoices = [0, 5, 15, 30, 60];

    /// <summary>
    /// After a connection problem (no network, a timeout, a blocked port, sleep), reconnect by itself with increasing pauses
    /// (5 s up to 5 minutes) and at once when the network changes, then send what waits in the Outbox. A refused password is
    /// never retried automatically. This only repairs a failed connection; it does not check for new mail on a timer.
    /// </summary>
    public bool ReconnectAutomatically { get; init; } = true;

    /// <summary>Keep a copy of the folder list, recent messages and opened messages on this device for reading while offline.</summary>
    public bool KeepOfflineCopies { get; init; } = true;
    public string? LastAccountId { get; init; }

    /// <summary>
    /// Credential-store keys of removed accounts whose password could not be deleted. HCG Mail retries
    /// the deletion at every start until it succeeds. Contains key names only, never secrets.
    /// </summary>
    public IReadOnlyList<string> PendingCredentialDeletions { get; init; } = [];

    /// <summary>Per-account plain-text signatures, inserted into new messages, replies and forwards from that account.</summary>
    public IReadOnlyList<AccountSignature> Signatures { get; init; } = [];

    public string? SignatureFor(string accountId) => Signatures.FirstOrDefault(s => s.AccountId == accountId)?.Text;

    public AppSettings WithSignature(string accountId, string? text) => this with
    {
        Signatures = [.. Signatures.Where(s => s.AccountId != accountId),
            .. string.IsNullOrWhiteSpace(text) ? Array.Empty<AccountSignature>() : [new AccountSignature(accountId, text.Replace("\r\n", "\n").TrimEnd())]],
    };

    // Returns the same instance when nothing changes, so value equality of AppSettings (used to skip
    // needless saves) is not broken by a fresh list allocation.
    private static IReadOnlyList<string> NormalizeKeys(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0)
            return [];
        var cleaned = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        return cleaned.Count == keys.Count ? keys : cleaned;
    }

    public AppSettings Normalize() => this with
    {
        Version = CurrentVersion,
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeKind.HcgFuture,
        DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? null : DownloadDirectory,
        DefaultMessageView = Enum.IsDefined(DefaultMessageView) ? DefaultMessageView : Services.MessageViewMode.Original,
        AutomaticRefreshMinutes = AutomaticRefreshChoices.Contains(AutomaticRefreshMinutes) ? AutomaticRefreshMinutes : 0,
        PendingCredentialDeletions = NormalizeKeys(PendingCredentialDeletions),
    };
}

public sealed record AccountSignature(string AccountId, string Text);

public interface IAppSettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

public interface IAccountStore
{
    IReadOnlyList<Accounts.MailAccount> Load();

    void Save(IReadOnlyList<Accounts.MailAccount> accounts);
}
