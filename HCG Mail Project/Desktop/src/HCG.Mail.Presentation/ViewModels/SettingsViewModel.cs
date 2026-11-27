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

public sealed record ThemeOption(ThemeKind Kind, string Name, string Description);

public sealed record RefreshOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}

public sealed record ViewModeOption(Core.Services.MessageViewMode Mode, string Label, string Description)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly ThemeService _themes;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ICredentialVault _vault;
    private readonly MainWindowViewModel _main;

    [ObservableProperty] private ThemeOption _selectedTheme;
    [ObservableProperty] private bool _markAsReadWhenOpened;
    [ObservableProperty] private bool _saveCopyToSentFolder;
    [ObservableProperty] private bool _confirmBeforeOpeningLinks;
    [ObservableProperty] private bool _connectOnStartup;
    [ObservableProperty] private bool _askWhereToSaveAttachments;
    [ObservableProperty] private string _downloadDirectory;
    [ObservableProperty] private RefreshOption _selectedRefresh;
    [ObservableProperty] private ViewModeOption _selectedViewMode;
    [ObservableProperty] private bool _loadRemoteImagesAutomatically;
    [ObservableProperty] private bool _reconnectAutomatically;
    [ObservableProperty] private bool _keepOfflineCopies;
    [ObservableProperty] private string? _offlineCopiesStatus;

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
        _markAsReadWhenOpened = s.MarkAsReadWhenOpened;
        _saveCopyToSentFolder = s.SaveCopyToSentFolder;
        _confirmBeforeOpeningLinks = s.ConfirmBeforeOpeningLinks;
        _connectOnStartup = s.ConnectOnStartup;
        _signatureAccount = main.SelectedAccount ?? main.Accounts.FirstOrDefault();
        _signatureText = _signatureAccount is null ? "" : s.SignatureFor(_signatureAccount.Account.Id) ?? "";
        _askWhereToSaveAttachments = s.AskWhereToSaveAttachments;
        _downloadDirectory = s.DownloadDirectory ?? AppPaths.DefaultDownloadDirectory();
        _selectedRefresh = RefreshOptions.FirstOrDefault(o => o.Minutes == s.AutomaticRefreshMinutes) ?? RefreshOptions[0];
        _selectedViewMode = ViewModeOptions.First(o => o.Mode == s.DefaultMessageView);
        _loadRemoteImagesAutomatically = s.LoadRemoteImagesAutomatically;
        _reconnectAutomatically = s.ReconnectAutomatically;
        _keepOfflineCopies = s.KeepOfflineCopies;
    }

    public IReadOnlyList<RefreshOption> RefreshOptions { get; } =
        AppSettings.AutomaticRefreshChoices.Select(m => new RefreshOption(m, m == 0 ? "Manually only (Refresh / F5)" : $"Every {m} minutes while HCG Mail is open")).ToList();

    public IReadOnlyList<ViewModeOption> ViewModeOptions { get; } =
    [
        new(Core.Services.MessageViewMode.Original, "Original", "The sender's layout, colours and fonts, made safe. Recommended."),
        new(Core.Services.MessageViewMode.Readable, "Readable", "The same text, images and links with the sender's colours, backgrounds and fonts removed: dark text on white."),
    ];

    public string MicrosoftSignInStatus => _main.MicrosoftSignInStatus;
    public bool ShowMicrosoftSignIn => _main.ShowMicrosoftSignIn;
    public bool ShowGoogleSignIn => _main.ShowGoogleSignIn;
    public string GoogleSignInStatus => _main.GoogleSignInStatus;

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeKind.HcgFuture, "HCG Future", "Graphite and black surfaces with cyan highlights. The signature HCG workstation look."),
        new(ThemeKind.HcgClassic, "HCG Classic", "A clean, restrained light design for bright offices and long reading sessions."),
    ];

    public ObservableCollection<AccountViewModel> Accounts => _main.Accounts;
    public bool HasAccounts => Accounts.Count > 0;

    // ---- Signatures -----------------------------------------------------------------------------

    [ObservableProperty] private AccountViewModel? _signatureAccount;
    [ObservableProperty] private string _signatureText = "";
    [ObservableProperty] private string _signatureStatus = "";

    partial void OnSignatureAccountChanged(AccountViewModel? value)
    {
        SignatureText = value is null ? "" : _settings.Current.SignatureFor(value.Account.Id) ?? "";
        SignatureStatus = "";
    }

    partial void OnSignatureTextChanged(string value) => SignatureStatus = "";

    [RelayCommand]
    private void SaveSignature()
    {
        if (SignatureAccount is not { } account)
            return;
        _settings.Update(s => s.WithSignature(account.Account.Id, SignatureText));
        SignatureStatus = string.IsNullOrWhiteSpace(SignatureText) ? "Signature removed." : "Signature saved.";
    }

    [RelayCommand]
    private void OpenMailHealth() => _main.OpenMailHealthCommand.Execute(null);

    public string Version { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    public string Runtime { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
    public string DataDirectory => _paths.DataDirectory;
    public string CredentialStorage => _vault.IsPersistent ? _vault.StorageDescription : "Session only — " + _vault.StorageDescription;
    public bool IsPreviewMode => _main.IsPreviewMode;

    partial void OnSelectedThemeChanged(ThemeOption value) => _themes.SetTheme(value.Kind);

    partial void OnMarkAsReadWhenOpenedChanged(bool value) => _settings.Update(s => s with { MarkAsReadWhenOpened = value });
    partial void OnSaveCopyToSentFolderChanged(bool value) => _settings.Update(s => s with { SaveCopyToSentFolder = value });
    partial void OnConfirmBeforeOpeningLinksChanged(bool value) => _settings.Update(s => s with { ConfirmBeforeOpeningLinks = value });
    partial void OnConnectOnStartupChanged(bool value) => _settings.Update(s => s with { ConnectOnStartup = value });
    partial void OnAskWhereToSaveAttachmentsChanged(bool value) => _settings.Update(s => s with { AskWhereToSaveAttachments = value });
    partial void OnSelectedViewModeChanged(ViewModeOption value) => _settings.Update(s => s with { DefaultMessageView = value.Mode });
    partial void OnLoadRemoteImagesAutomaticallyChanged(bool value) => _settings.Update(s => s with { LoadRemoteImagesAutomatically = value });
    partial void OnReconnectAutomaticallyChanged(bool value) => _settings.Update(s => s with { ReconnectAutomatically = value });

    partial void OnKeepOfflineCopiesChanged(bool value)
    {
        _settings.Update(s => s with { KeepOfflineCopies = value });
        if (!value)
        {
            // Turning the copies off removes the ones already kept: nothing of the mail stays on this device.
            _main.ClearOfflineCopies();
            OfflineCopiesStatus = "Offline copies were deleted from this " + PlatformText.Device + ".";
        }
    }

    [RelayCommand]
    private void DeleteOfflineCopies()
    {
        _main.ClearOfflineCopies();
        OfflineCopiesStatus = "Offline copies were deleted from this " + PlatformText.Device + ". New ones are kept as you read mail" + (KeepOfflineCopies ? "." : " only if you turn the option on again.");
    }

    partial void OnSelectedRefreshChanged(RefreshOption value)
    {
        _settings.Update(s => s with { AutomaticRefreshMinutes = value.Minutes });
        _main.ApplyRefreshSchedule();
    }

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

    [RelayCommand]
    private Task AddMicrosoftAccountAsync() => _main.AddMicrosoftAccountCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task AddGoogleAccountAsync() => _main.AddGoogleAccountCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task ReconnectAccountAsync(AccountViewModel account) => _main.ReconnectAccountAsync(account);

    [RelayCommand]
    private Task RemoveAccountAsync(AccountViewModel account) => _main.RemoveAccountAsync(account.Account);
}
