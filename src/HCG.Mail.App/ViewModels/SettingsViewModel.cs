using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.Services;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;

namespace HCG.Mail.App.ViewModels;

public sealed record RefreshOption(int Minutes, string Label)
{
    public static IReadOnlyList<RefreshOption> All { get; } = AppSettings.AllowedRefreshIntervals
        .Select(m => new RefreshOption(m, m switch
        {
            0 => "Manually only",
            1 => "Every minute",
            60 => "Every hour",
            _ => $"Every {m} minutes",
        }))
        .ToList();

    public override string ToString() => Label;
}

public sealed record ThemeOption(ThemeKind Kind, string Name, string Description);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly ThemeService _themes;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ICredentialVault _vault;
    private readonly MainWindowViewModel _main;

    [ObservableProperty] private ThemeOption _selectedTheme;
    [ObservableProperty] private RefreshOption _selectedRefresh;
    [ObservableProperty] private bool _notifyOnNewMail;
    [ObservableProperty] private bool _markAsReadWhenOpened;
    [ObservableProperty] private bool _saveCopyToSentFolder;
    [ObservableProperty] private bool _confirmBeforeOpeningLinks;
    [ObservableProperty] private bool _connectOnStartup;
    [ObservableProperty] private bool _askWhereToSaveAttachments;
    [ObservableProperty] private string _downloadDirectory;

    public SettingsViewModel(AppSettingsService settings, ThemeService themes, IDialogService dialogs, AppPaths paths, ICredentialVault vault, MainWindowViewModel main)
    {
        _settings = settings;
        _themes = themes;
        _dialogs = dialogs;
        _paths = paths;
        _vault = vault;
        _main = main;

        var s = settings.Current;
        _selectedTheme = Themes.First(t => t.Kind == s.Theme);
        _selectedRefresh = RefreshOption.All.First(o => o.Minutes == s.RefreshIntervalMinutes);
        _notifyOnNewMail = s.NotifyOnNewMail;
        _markAsReadWhenOpened = s.MarkAsReadWhenOpened;
        _saveCopyToSentFolder = s.SaveCopyToSentFolder;
        _confirmBeforeOpeningLinks = s.ConfirmBeforeOpeningLinks;
        _connectOnStartup = s.ConnectOnStartup;
        _askWhereToSaveAttachments = s.AskWhereToSaveAttachments;
        _downloadDirectory = s.DownloadDirectory ?? AppPaths.DefaultDownloadDirectory();
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeKind.HcgFuture, "HCG Future", "Graphite and black surfaces with cyan highlights. The signature HCG workstation look."),
        new(ThemeKind.HcgClassic, "HCG Classic", "A clean, restrained light design for bright offices and long reading sessions."),
    ];

    public IReadOnlyList<RefreshOption> RefreshOptions => RefreshOption.All;
    public ObservableCollection<AccountViewModel> Accounts => _main.Accounts;

    public string Version { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    public string Runtime { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
    public string DataDirectory => _paths.DataDirectory;
    public string CredentialStorage => _vault.IsPersistent ? _vault.StorageDescription : "Session only — " + _vault.StorageDescription;
    public bool IsPreviewMode => _main.IsPreviewMode;

    public string RefreshExplanation => SelectedRefresh.Minutes == 0
        ? "HCG Mail only contacts your mail server when you open a folder or click Refresh."
        : $"While HCG Mail is open it checks the selected folder {SelectedRefresh.Label.ToLowerInvariant()}. The status bar shows when the last check happened.";

    partial void OnSelectedThemeChanged(ThemeOption value) => _themes.SetTheme(value.Kind);

    partial void OnSelectedRefreshChanged(RefreshOption value)
    {
        _settings.Update(s => s with { RefreshIntervalMinutes = value.Minutes });
        OnPropertyChanged(nameof(RefreshExplanation));
    }

    partial void OnNotifyOnNewMailChanged(bool value) => _settings.Update(s => s with { NotifyOnNewMail = value });
    partial void OnMarkAsReadWhenOpenedChanged(bool value) => _settings.Update(s => s with { MarkAsReadWhenOpened = value });
    partial void OnSaveCopyToSentFolderChanged(bool value) => _settings.Update(s => s with { SaveCopyToSentFolder = value });
    partial void OnConfirmBeforeOpeningLinksChanged(bool value) => _settings.Update(s => s with { ConfirmBeforeOpeningLinks = value });
    partial void OnConnectOnStartupChanged(bool value) => _settings.Update(s => s with { ConnectOnStartup = value });
    partial void OnAskWhereToSaveAttachmentsChanged(bool value) => _settings.Update(s => s with { AskWhereToSaveAttachments = value });

    [RelayCommand]
    private async Task BrowseDownloadDirectoryAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose where attachments are saved", DownloadDirectory);
        if (folder is null)
            return;
        DownloadDirectory = folder;
        _settings.Update(s => s with { DownloadDirectory = folder });
    }

    [RelayCommand]
    private void ResetDownloadDirectory()
    {
        DownloadDirectory = AppPaths.DefaultDownloadDirectory();
        _settings.Update(s => s with { DownloadDirectory = null });
    }

    [RelayCommand]
    private Task AddAccountAsync() => _main.AddAccountCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task EditAccountAsync(AccountViewModel account) => _main.EditAccountAsync(account);
}
