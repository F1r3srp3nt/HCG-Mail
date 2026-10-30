namespace HCG.Mail.Core.Settings;

public enum ThemeKind
{
    HcgFuture,
    HcgClassic,
}

public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public ThemeKind Theme { get; init; } = ThemeKind.HcgFuture;

    /// <summary>Minutes between automatic refreshes of the open folder; 0 means manual refresh only.</summary>
    public int RefreshIntervalMinutes { get; init; } = 5;

    /// <summary>Show an in-app notification when a refresh finds new messages.</summary>
    public bool NotifyOnNewMail { get; init; } = true;

    /// <summary>Where attachments are saved when the user chooses "Save" without picking a location; null means the user's Downloads folder.</summary>
    public string? DownloadDirectory { get; init; }

    /// <summary>Always ask where to save each attachment instead of using the download folder.</summary>
    public bool AskWhereToSaveAttachments { get; init; }

    public bool ConnectOnStartup { get; init; } = true;
    public bool MarkAsReadWhenOpened { get; init; } = true;
    public bool SaveCopyToSentFolder { get; init; } = true;
    public bool ConfirmBeforeOpeningLinks { get; init; } = true;
    public string? LastAccountId { get; init; }

    public static readonly int[] AllowedRefreshIntervals = [0, 1, 2, 5, 10, 15, 30, 60];

    public AppSettings Normalize() => this with
    {
        Version = CurrentVersion,
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeKind.HcgFuture,
        RefreshIntervalMinutes = AllowedRefreshIntervals.Contains(RefreshIntervalMinutes) ? RefreshIntervalMinutes : 5,
        DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? null : DownloadDirectory,
    };
}

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
