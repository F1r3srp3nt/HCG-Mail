namespace HCG.Mail.Core.Settings;

public enum ThemeKind
{
    HcgFuture,
    HcgClassic,
}

/// <remarks>
/// There is deliberately no refresh-interval setting: HCG Mail never polls the mail server in the
/// background. Settings files from older builds that contain "refreshIntervalMinutes" or
/// "notifyOnNewMail" still load; those fields are simply ignored.
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
    public string? LastAccountId { get; init; }

    public AppSettings Normalize() => this with
    {
        Version = CurrentVersion,
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeKind.HcgFuture,
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
