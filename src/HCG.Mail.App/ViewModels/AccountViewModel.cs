using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HCG.Mail.App.Services;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.App.ViewModels;

public enum AccountStatus
{
    Offline,
    NeedsPassword,
    Connecting,
    Connected,
    Error,
}

/// <summary>One configured account: its live session, connection status and folder tree.</summary>
public sealed partial class AccountViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IMailSessionFactory _sessionFactory;
    private readonly ICredentialVault _vault;
    private readonly ILogger _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsConnecting), nameof(NeedsPassword), nameof(HasError), nameof(CanReconnect))]
    private AccountStatus _status = AccountStatus.Offline;

    [ObservableProperty] private string _statusText = "Not connected";

    public AccountViewModel(MailAccount account, IMailSessionFactory sessionFactory, ICredentialVault vault, ILogger logger)
    {
        Account = account;
        _sessionFactory = sessionFactory;
        _vault = vault;
        _logger = logger;
    }

    public MailAccount Account { get; private set; }
    public IMailSession? Session { get; private set; }

    public ObservableCollection<FolderViewModel> StandardFolders { get; } = [];
    public ObservableCollection<FolderViewModel> OtherFolders { get; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.EmailAddress : Account.DisplayName;
    public string EmailAddress => Account.EmailAddress;
    public string Initials => MakeInitials(DisplayName);
    public bool IsConnected => Status == AccountStatus.Connected;
    public bool IsConnecting => Status == AccountStatus.Connecting;
    public bool NeedsPassword => Status == AccountStatus.NeedsPassword;
    public bool HasError => Status == AccountStatus.Error;
    public bool CanReconnect => Status is AccountStatus.Error or AccountStatus.Offline;
    public bool HasOtherFolders => OtherFolders.Count > 0;

    public IEnumerable<FolderViewModel> AllFolders => StandardFolders.Concat(OtherFolders);

    public FolderViewModel? FindByRole(FolderRole role) => StandardFolders.FirstOrDefault(f => f.Role == role && f.IsAvailable);

    public FolderViewModel? FindByFullName(string fullName) => AllFolders.FirstOrDefault(f => f.IsAvailable && f.FullName == fullName);

    /// <summary>Connects using the stored password. Returns false (and sets <see cref="NeedsPassword"/>) when none is stored.</summary>
    public async Task<bool> ConnectAsync()
    {
        string? password;
        try
        {
            password = await _vault.GetAsync(Account.CredentialKey);
        }
        catch (CredentialVaultException ex)
        {
            _logger.LogWarning(ex, "Could not read stored password for account {AccountId}", Account.Id);
            SetStatus(AccountStatus.Error, ex.Message);
            return false;
        }

        if (string.IsNullOrEmpty(password))
        {
            SetStatus(AccountStatus.NeedsPassword, "Password required");
            return false;
        }

        await DisposeSessionAsync();
        var session = _sessionFactory.Create(Account, password);
        session.StateChanged += OnSessionStateChanged;
        Session = session;

        SetStatus(AccountStatus.Connecting, "Connecting…");
        try
        {
            await session.ConnectAsync();
            await RefreshFoldersAsync();
            SetStatus(AccountStatus.Connected, "Connected");
            return true;
        }
        catch (MailOperationException ex)
        {
            SetStatus(AccountStatus.Error, ex.UserMessage);
            return false;
        }
    }

    public async Task RefreshFoldersAsync(CancellationToken cancellationToken = default)
    {
        if (Session is null)
            return;
        var folders = await Session.GetFoldersAsync(cancellationToken);

        var standard = new List<FolderViewModel>();
        foreach (var role in FolderRoleMapper.StandardRoles)
        {
            var folder = folders.FirstOrDefault(f => f.Role == role);
            var existing = StandardFolders.FirstOrDefault(f => f.Role == role);
            if (folder is null)
                standard.Add(existing is { IsAvailable: false } ? existing : new FolderViewModel(role));
            else if (existing is { IsAvailable: true } && existing.FullName == folder.FullName)
            {
                existing.Update(folder);
                standard.Add(existing);
            }
            else
                standard.Add(new FolderViewModel(folder));
        }

        var other = new List<FolderViewModel>();
        foreach (var folder in folders.Where(f => f.Role == FolderRole.None))
        {
            var existing = OtherFolders.FirstOrDefault(f => f.FullName == folder.FullName);
            if (existing is not null)
            {
                existing.Update(folder);
                other.Add(existing);
            }
            else
                other.Add(new FolderViewModel(folder));
        }

        CollectionSync.Sync(StandardFolders, standard);
        CollectionSync.Sync(OtherFolders, other);
        OnPropertyChanged(nameof(HasOtherFolders));
    }

    /// <summary>Applies edited settings (and optionally a new password) and reconnects.</summary>
    public async Task UpdateAccountAsync(MailAccount account)
    {
        Account = account;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(EmailAddress));
        OnPropertyChanged(nameof(Initials));
        await DisposeSessionAsync();
        StandardFolders.Clear();
        OtherFolders.Clear();
        OnPropertyChanged(nameof(HasOtherFolders));
    }

    public void ReportError(MailOperationException ex)
    {
        if (ex.IsTransient || ex.Kind is MailErrorKind.Authentication or MailErrorKind.Certificate or MailErrorKind.Tls)
            SetStatus(AccountStatus.Error, ex.UserMessage);
    }

    public async ValueTask DisposeAsync() => await DisposeSessionAsync();

    private async Task DisposeSessionAsync()
    {
        if (Session is { } session)
        {
            session.StateChanged -= OnSessionStateChanged;
            Session = null;
            await session.DisposeAsync();
        }
        SetStatus(AccountStatus.Offline, "Not connected");
    }

    private void OnSessionStateChanged(object? sender, MailConnectionState state) => UiThread.Post(() =>
    {
        switch (state)
        {
            case MailConnectionState.Connecting:
                SetStatus(AccountStatus.Connecting, "Connecting…");
                break;
            case MailConnectionState.Connected:
                SetStatus(AccountStatus.Connected, "Connected");
                break;
            case MailConnectionState.Failed when Status != AccountStatus.Error:
                SetStatus(AccountStatus.Error, "Connection lost — will retry on the next refresh");
                break;
        }
    });

    private void SetStatus(AccountStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    internal static string MakeInitials(string name)
    {
        var isAddress = name.Contains('@');
        var source = isAddress ? name[..name.IndexOf('@')] : name;
        var parts = source.Split([' ', '.', '_', '-', '(', ')', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => char.IsLetterOrDigit(p[0]))
            .ToList();
        if (parts.Count == 0)
            return "?";
        var first = char.ToUpperInvariant(parts[0][0]);
        return parts.Count > 1 && !isAddress ? $"{first}{char.ToUpperInvariant(parts[1][0])}" : first.ToString();
    }
}
