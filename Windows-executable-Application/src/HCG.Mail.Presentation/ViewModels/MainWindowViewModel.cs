using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.Services;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;
using HCG.Mail.Core.Settings;
using HCG.Mail.Infrastructure;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.App.ViewModels;

public sealed record AppMode(bool IsPreview);

/// <summary>Coordinates accounts, folders, the message list, the reader and all mailbox actions.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettingsService _settings;
    private readonly MailHealthViewModelFactory? _mailHealth;
    private DispatcherTimer? _autoRefresh;
    private readonly IMicrosoftAuth _microsoftAuth;
    private readonly IDeliveryLog? _deliveryLog;
    private readonly ThemeService _themes;
    private readonly IAccountStore _accountStore;
    private readonly ICredentialVault _vault;
    private readonly IMailSessionFactory _sessionFactory;
    private readonly IConnectionTester _tester;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILinkOpener _links;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    // UI debounce for opening the clicked message — not a network timer. HCG Mail has no background
    // polling, IMAP IDLE or keep-alive: the server is only contacted for user actions.
    private readonly DispatcherTimer _selectionDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private DateTimeOffset? _lastChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAccount), nameof(ShowNeedsPassword), nameof(ShowNeedsSignIn), nameof(ShowAccountError), nameof(ShowConnecting))]
    private AccountViewModel? _selectedAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStandardFolder), nameof(SelectedOtherFolder), nameof(IsDraftsFolder))]
    private FolderViewModel? _selectedFolder;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRefreshing;

    public MainWindowViewModel(
        AppSettingsService settings,
        ThemeService themes,
        IAccountStore accountStore,
        ICredentialVault vault,
        IMailSessionFactory sessionFactory,
        IConnectionTester tester,
        IHtmlSanitizer sanitizer,
        IDialogService dialogs,
        INotificationService notifications,
        ILinkOpener links,
        AppPaths paths,
        AppMode mode,
        ILoggerFactory loggerFactory,
        MailHealthViewModelFactory? mailHealth = null,
        IMicrosoftAuth? microsoftAuth = null,
        IDeliveryLog? deliveryLog = null,
        RemoteImageLoader? remoteImages = null)
    {
        _mailHealth = mailHealth;
        _microsoftAuth = microsoftAuth ?? new UnconfiguredMicrosoftAuth("Microsoft sign-in is not available in this mode.");
        _deliveryLog = deliveryLog;
        _settings = settings;
        _themes = themes;
        _accountStore = accountStore;
        _vault = vault;
        _sessionFactory = sessionFactory;
        _tester = tester;
        _dialogs = dialogs;
        _notifications = notifications;
        _links = links;
        _paths = paths;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainWindowViewModel>();
        IsPreviewMode = mode.IsPreview;

        MessageList = new MessageListViewModel();
        Reader = new MessageReaderViewModel(sanitizer) { RemoteImages = remoteImages };
        Reader.LinkRequested += (_, url) => _ = OpenLinkAsync(url);
        MessageList.PropertyChanged += OnMessageListPropertyChanged;
        MessageList.LoadFailed += (_, ex) => HandleError("Could not load messages", ex);
        Reader.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MessageReaderViewModel.Content))
                RefreshCommandStates();
        };
        Accounts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(ShowWelcome));
            OnPropertyChanged(nameof(HasMultipleAccounts));
        };

        _selectionDelay.Tick += async (_, _) =>
        {
            _selectionDelay.Stop();
            await LoadSelectedMessageAsync();
        };
        UpdateStatusText();
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];
    public MessageListViewModel MessageList { get; }
    public MessageReaderViewModel Reader { get; }
    public bool IsPreviewMode { get; }

    public bool HasAccounts => Accounts.Count > 0;
    public bool HasMultipleAccounts => Accounts.Count > 1;
    public bool ShowWelcome => Accounts.Count == 0;
    public bool HasSelectedAccount => SelectedAccount is not null;
    public bool ShowNeedsPassword => SelectedAccount?.NeedsPassword == true;
    public bool ShowNeedsSignIn => SelectedAccount?.NeedsSignIn == true;

    public string MicrosoftSignInStatus => _microsoftAuth.IsConfigured
        ? "Microsoft sign-in is available for Outlook.com, Hotmail and Microsoft 365 accounts."
        : _microsoftAuth.ConfigurationProblem ?? "Microsoft sign-in is not available.";
    public bool ShowAccountError => SelectedAccount?.HasError == true && MessageList.Folder is null;
    public bool ShowConnecting => SelectedAccount?.IsConnecting == true && MessageList.Folder is null;
    public bool IsDraftsFolder => SelectedFolder?.Role == FolderRole.Drafts;
    public string ReadToggleText => MessageList.SelectedItem?.IsRead == false ? "Mark read" : "Mark unread";
    public string FlagToggleText => MessageList.SelectedItem?.IsFlagged == true ? "Unflag" : "Flag";

    public FolderViewModel? SelectedStandardFolder
    {
        get => SelectedFolder is { IsStandard: true } ? SelectedFolder : null;
        set
        {
            if (value is { IsAvailable: true })
                SelectedFolder = value;
        }
    }

    public FolderViewModel? SelectedOtherFolder
    {
        get => SelectedFolder is { IsStandard: false } ? SelectedFolder : null;
        set
        {
            if (value is { IsAvailable: true })
                SelectedFolder = value;
        }
    }

    // ---- startup ------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        foreach (var account in _accountStore.Load())
            Accounts.Add(CreateAccountViewModel(account));

        await RetryPendingCredentialDeletionsAsync();

        var last = Accounts.FirstOrDefault(a => a.Account.Id == _settings.Current.LastAccountId) ?? Accounts.FirstOrDefault();
        SelectedAccount = last;

        if (_settings.Current.ConnectOnStartup || IsPreviewMode)
        {
            var tasks = Accounts.Select(ConnectAccountAsync).ToList();
            await Task.WhenAll(tasks);
        }
        else if (SelectedAccount is not null)
        {
            StatusText = "Automatic connection is off. Click Refresh to connect.";
        }
        ApplyRefreshSchedule();
    }

    /// <summary>
    /// Manual refresh is the default: no timer exists. Only when the user chooses automatic checking in Settings does
    /// a timer refresh the open folder every N minutes (skipped while a refresh is already running).
    /// </summary>
    public void ApplyRefreshSchedule()
    {
        _autoRefresh?.Stop();
        _autoRefresh = null;
        if (_lastChecked is not null)
            UpdateStatusText();
        var minutes = _settings.Current.AutomaticRefreshMinutes;
        if (minutes <= 0 || IsSuspended)
            return;
        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _autoRefresh.Tick += async (_, _) =>
        {
            if (RefreshCommand.CanExecute(null))
                await RefreshCommand.ExecuteAsync(null);
        };
        _autoRefresh.Start();
    }

    /// <summary>
    /// The app is going to the background (mobile). Stops the optional refresh timer: nothing is checked while the
    /// operating system has suspended the app. Connections the system drops are re-established on the next action.
    /// </summary>
    public void Suspend()
    {
        _autoRefresh?.Stop();
        _autoRefresh = null;
        IsSuspended = true;
    }

    /// <summary>
    /// The app is back on screen. Manual refresh stays manual; only when the user enabled automatic checking is the open
    /// folder refreshed once now, and the timer restarted.
    /// </summary>
    public async Task ResumeAsync()
    {
        if (!IsSuspended)
            return;
        IsSuspended = false;
        ApplyRefreshSchedule();
        if (_settings.Current.AutomaticRefreshMinutes > 0 && SelectedAccount is { IsConnected: true } && RefreshCommand.CanExecute(null))
            await RefreshCommand.ExecuteAsync(null);
    }

    public bool IsSuspended { get; private set; }

    /// <summary>
    /// Mobile: hand a saved attachment straight to the system share sheet (Save to Files, AirDrop, another app) instead
    /// of leaving it in the app's private storage. The file is still never opened by HCG Mail.
    /// </summary>
    public bool ShareSavedAttachments { get; set; }

    /// <summary>Reconnects one account: Microsoft accounts sign in again; IMAP accounts reconnect with the stored password.</summary>
    public Task ReconnectAccountAsync(AccountViewModel account) =>
        account.IsMicrosoft && account.NeedsSignIn ? ReconnectMicrosoftAccountAsync(account) : ConnectAccountAsync(account);

    private AccountViewModel CreateAccountViewModel(MailAccount account)
    {
        var vm = new AccountViewModel(account, _sessionFactory, _vault, _loggerFactory.CreateLogger<AccountViewModel>());
        vm.PropertyChanged += OnAccountPropertyChanged;
        return vm;
    }

    private async Task ConnectAccountAsync(AccountViewModel account)
    {
        var connected = await account.ConnectAsync();
        if (account != SelectedAccount)
            return;
        if (connected)
        {
            var target = SelectedFolder is { } current && account.FindByFullName(current.FullName) is { } same
                ? same
                : account.FindByRole(FolderRole.Inbox) ?? account.AllFolders.FirstOrDefault(f => f.IsAvailable);
            if (target is not null && target == MessageList.Folder)
            {
                // Reconnected to the folder already on screen: keep the list, swap in the new session.
                MessageList.UseSession(account.Session!);
                await MessageList.RefreshAsync();
                _lastChecked = DateTimeOffset.Now;
                UpdateStatusText();
            }
            else
            {
                SelectedFolder = target;
                if (SelectedFolder is not null && MessageList.Folder != SelectedFolder)
                    await LoadFolderAsync(SelectedFolder);
            }
        }
        else
        {
            MessageList.Clear();
            Reader.Clear();
        }
        RefreshCommandStates();
    }

    // ---- selection ----------------------------------------------------------------------------

    partial void OnSelectedAccountChanged(AccountViewModel? value)
    {
        MessageList.Clear();
        Reader.Clear();
        SelectedFolder = null;
        if (value is null)
        {
            RefreshCommandStates();
            return;
        }
        _settings.Update(s => s with { LastAccountId = value.Account.Id });
        if (value.IsConnected)
            SelectedFolder = value.FindByRole(FolderRole.Inbox) ?? value.AllFolders.FirstOrDefault(f => f.IsAvailable);
        RefreshCommandStates();
    }

    partial void OnSelectedFolderChanged(FolderViewModel? value)
    {
        if (value is null || SelectedAccount?.Session is null)
            return;
        _ = LoadFolderAsync(value);
    }

    private async Task LoadFolderAsync(FolderViewModel folder)
    {
        var session = SelectedAccount?.Session;
        if (session is null)
            return;
        Reader.Clear();
        await MessageList.LoadAsync(session, folder);
        _lastChecked = DateTimeOffset.Now;
        UpdateStatusText();
        RefreshCommandStates();
    }

    private void OnMessageListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MessageListViewModel.SelectedItem))
            return;
        RefreshCommandStates();
        _selectionDelay.Stop();
        if (MessageList.SelectedItem is null)
            Reader.Clear();
        else
            _selectionDelay.Start();
    }

    private async Task LoadSelectedMessageAsync()
    {
        var item = MessageList.SelectedItem;
        var account = SelectedAccount;
        if (item is null || account?.Session is not { } session)
            return;
        Reader.DefaultViewMode = _settings.Current.DefaultMessageView;
        Reader.LoadRemoteImagesAutomatically = _settings.Current.LoadRemoteImagesAutomatically;
        await Reader.LoadAsync(session, item, SaveAttachmentAsync);
        RefreshCommandStates();
        if (Reader.Content is not null && Reader.Item == item && !item.IsRead && _settings.Current.MarkAsReadWhenOpened)
            await SetReadAsync(item, read: true);
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != SelectedAccount || e.PropertyName != nameof(AccountViewModel.Status))
            return;
        OnPropertyChanged(nameof(ShowNeedsPassword));
        OnPropertyChanged(nameof(ShowNeedsSignIn));
        OnPropertyChanged(nameof(ShowAccountError));
        OnPropertyChanged(nameof(ShowConnecting));
        RefreshCommandStates();
    }

    // ---- refresh ------------------------------------------------------------------------------

    private bool CanRefresh() => SelectedAccount is not null && !SelectedAccount.IsConnecting && !IsRefreshing;

    /// <summary>
    /// Manual synchronisation of the selected account: folder list and unread counts, then the open
    /// folder. This is the only way HCG Mail checks for new mail — there is no periodic refresh.
    /// Only one refresh runs at a time; cancelling stops the remaining steps (an IMAP command that
    /// is already running finishes first, because aborting it would drop the connection).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh), IncludeCancelCommand = true)]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var account = SelectedAccount;
        if (account is null || IsRefreshing)
            return;

        IsRefreshing = true;
        IsBusy = true;
        StatusText = "Refreshing…";
        try
        {
            if (!account.IsConnected || account.Session is null)
            {
                await ConnectAccountAsync(account);
                if (account.IsConnected)
                    ReportRefreshed(account, newMessages: 0);
                else if (account.HasError)
                    _notifications.Error("Refresh failed", account.StatusText);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await account.RefreshFoldersAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var newMessages = 0;
            if (MessageList.Folder is null && SelectedFolder is not null)
                await LoadFolderAsync(SelectedFolder);
            else
                newMessages = await MessageList.RefreshAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!MessageList.LastFetchFailed)
                ReportRefreshed(account, newMessages);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is MailOperationException { Kind: MailErrorKind.Cancelled })
        {
            _notifications.Info("Refresh cancelled", "The mailbox was not fully refreshed.");
        }
        catch (MailOperationException ex)
        {
            account.ReportError(ex);
            HandleError("Refresh failed", ex);
        }
        finally
        {
            IsRefreshing = false;
            IsBusy = false;
            UpdateStatusText();
            RefreshCommandStates();
        }
    }

    private void ReportRefreshed(AccountViewModel account, int newMessages)
    {
        _lastChecked = DateTimeOffset.Now;
        var folder = string.IsNullOrEmpty(MessageList.FolderTitle) ? "Mailbox" : MessageList.FolderTitle;
        _notifications.Success(
            newMessages switch { 0 => $"{folder} is up to date", 1 => "1 new message", _ => $"{newMessages} new messages" },
            $"Refreshed {account.EmailAddress} at {_lastChecked.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}.");
    }

    private void UpdateStatusText()
    {
        var checkedText = _lastChecked is { } t ? $"Last refreshed {t.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)} · " : "";
        StatusText = checkedText + (_settings.Current.AutomaticRefreshMinutes > 0
            ? $"Checked automatically every {_settings.Current.AutomaticRefreshMinutes} minutes{AutomaticRefreshQualifier} · Refresh ({RefreshHint}) any time"
            : $"Manual refresh ({RefreshHint}) — no automatic checking");
    }

    /// <summary>How the user refreshes on this platform, shown in the status text: "F5" on the desktop, "pull down" on iOS.</summary>
    public string RefreshHint
    {
        get => _refreshHint;
        set
        {
            _refreshHint = value;
            UpdateStatusText();
        }
    }

    private string _refreshHint = "F5";

    /// <summary>Mobile: " while open", because iOS suspends the app in the background.</summary>
    public string AutomaticRefreshQualifier { get; set; } = "";

    // ---- message actions ----------------------------------------------------------------------

    private bool HasSelection() => MessageList.SelectedItem is not null && SelectedAccount?.IsConnected == true;
    private bool HasOpenMessage() => Reader.Content is not null && SelectedAccount?.IsConnected == true;
    private bool CanCompose() => Accounts.Any(a => a.IsConnected);

    private bool CanArchive() =>
        HasSelection() && SelectedAccount!.FindByRole(FolderRole.Archive) is { } archive && archive != SelectedFolder;

    [RelayCommand(CanExecute = nameof(CanCompose))]
    private void Compose()
    {
        var account = SelectedAccount is { IsConnected: true } a ? a : Accounts.First(x => x.IsConnected);
        OpenCompose(account, new OutgoingMessage());
    }

    [RelayCommand(CanExecute = nameof(HasOpenMessage))]
    private void Reply() => OpenReply(replyAll: false);

    [RelayCommand(CanExecute = nameof(HasOpenMessage))]
    private void ReplyAll() => OpenReply(replyAll: true);

    [RelayCommand(CanExecute = nameof(HasOpenMessage))]
    private async Task ForwardAsync()
    {
        var account = SelectedAccount!;
        var content = Reader.Content!;
        var attachments = new List<OutgoingAttachment>();
        try
        {
            IsBusy = true;
            foreach (var attachment in content.Attachments)
            {
                var data = await account.Session!.GetAttachmentBytesAsync(content.Ref, attachment.Index);
                attachments.Add(OutgoingAttachment.FromBytes(attachment.SafeFileName, attachment.ContentType, data));
            }
        }
        catch (MailOperationException ex)
        {
            HandleError("Could not include the attachments", ex);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        OpenCompose(account, ReplyBuilder.Forward(content, attachments));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditDraftAsync()
    {
        var account = SelectedAccount!;
        var item = MessageList.SelectedItem!;
        try
        {
            IsBusy = true;
            var draft = await account.Session!.LoadDraftAsync(item.Ref);
            OpenCompose(account, draft);
        }
        catch (MailOperationException ex)
        {
            HandleError("Could not open the draft", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var account = SelectedAccount!;
        var item = MessageList.SelectedItem!;
        var trash = account.FindByRole(FolderRole.Trash);
        var permanent = trash is null || SelectedFolder?.Role == FolderRole.Trash;
        if (permanent)
        {
            var message = trash is null
                ? "Your mail server has no Trash folder, so this message will be deleted permanently. This cannot be undone."
                : "This message will be deleted permanently. This cannot be undone.";
            if (!await _dialogs.ConfirmAsync("Delete permanently?", message, "Delete permanently", destructive: true))
                return;
        }

        try
        {
            await account.Session!.DeleteAsync(item.Ref, permanent);
            MessageList.Remove(item);
            if (!permanent && trash is not null && !item.IsRead)
                trash.UnreadCount++;
        }
        catch (MailOperationException ex)
        {
            HandleError("Could not delete the message", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanArchive))]
    private async Task ArchiveAsync()
    {
        var account = SelectedAccount!;
        var item = MessageList.SelectedItem!;
        var archive = account.FindByRole(FolderRole.Archive)!;
        try
        {
            await account.Session!.MoveAsync(item.Ref, archive.FullName);
            MessageList.Remove(item);
            _notifications.Success("Archived", $"Moved to {archive.DisplayName}.");
        }
        catch (MailOperationException ex)
        {
            HandleError("Could not archive the message", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ToggleReadAsync(MessageItemViewModel? item = null)
    {
        item ??= MessageList.SelectedItem;
        return item is null ? Task.CompletedTask : SetReadAsync(item, !item.IsRead);
    }

    [RelayCommand]
    private async Task ToggleFlagAsync(MessageItemViewModel? item = null)
    {
        item ??= MessageList.SelectedItem;
        var session = SelectedAccount?.Session;
        if (item is null || session is null)
            return;
        var flag = !item.IsFlagged;
        item.IsFlagged = flag;
        OnPropertyChanged(nameof(FlagToggleText));
        try
        {
            await session.SetFlagsAsync([item.Ref], MessageFlags.Flagged, flag);
        }
        catch (MailOperationException ex)
        {
            item.IsFlagged = !flag;
            OnPropertyChanged(nameof(FlagToggleText));
            HandleError("Could not change the flag", ex);
        }
    }

    private async Task SetReadAsync(MessageItemViewModel item, bool read)
    {
        var session = SelectedAccount?.Session;
        if (session is null || item.IsRead == read)
            return;
        item.IsRead = read;
        MessageList.AdjustUnread(read ? -1 : 1);
        OnPropertyChanged(nameof(ReadToggleText));
        try
        {
            await session.SetFlagsAsync([item.Ref], MessageFlags.Seen, read);
        }
        catch (MailOperationException ex)
        {
            item.IsRead = !read;
            MessageList.AdjustUnread(read ? 1 : -1);
            OnPropertyChanged(nameof(ReadToggleText));
            HandleError("Could not update the message", ex);
        }
    }

    private async Task SaveAttachmentAsync(AttachmentViewModel attachment)
    {
        var account = SelectedAccount;
        var content = Reader.Content;
        if (account?.Session is null || content is null)
            return;

        if (attachment.IsDangerous && !await _dialogs.ConfirmAsync(
                "Save a potentially dangerous file?",
                $"“{attachment.Name}” is a type of file that can run programs on your {PlatformText.Device}. HCG Mail will save it but never open it. Only open it later if you trust the sender.",
                "Save file"))
            return;

        var directory = _settings.Current.DownloadDirectory ?? AppPaths.DefaultDownloadDirectory();
        string? path;
        if (_settings.Current.AskWhereToSaveAttachments)
        {
            path = await _dialogs.PickSaveFileAsync(attachment.Name, directory);
            if (path is null)
                return;
        }
        else
        {
            Directory.CreateDirectory(directory);
            path = AttachmentFileNames.GetUniquePath(directory, attachment.Name);
        }

        var partial = path + ".hcgpart";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                await account.Session.SaveAttachmentAsync(content.Ref, attachment.Info.Index, stream);
            File.Move(partial, path, overwrite: _settings.Current.AskWhereToSaveAttachments);
            var saved = path;
            if (ShareSavedAttachments)
            {
                _links.RevealInFolder(saved);
                return;
            }
            _notifications.Success("Attachment saved", $"{Path.GetFileName(saved)} → {Path.GetDirectoryName(saved)}  (click to show)", () => _links.RevealInFolder(saved));
        }
        catch (MailOperationException ex)
        {
            HandleError("Could not save the attachment", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Error("Could not save the attachment", ex.Message);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public Task OpenLinkAsync(string url)
    {
        if (MailtoParser.TryParse(url) is { } mailto)
        {
            // mailto: links open a new message (cc/bcc from links are ignored); nothing is sent automatically.
            var account = SelectedAccount is { IsConnected: true } a ? a : Accounts.FirstOrDefault(x => x.IsConnected);
            if (account is not null)
                OpenCompose(account, new OutgoingMessage { To = mailto.To, Subject = mailto.Subject, TextBody = mailto.Body });
            return Task.CompletedTask;
        }
        return _links.OpenAsync(url);
    }

    private void OpenReply(bool replyAll)
    {
        var account = SelectedAccount!;
        var message = ReplyBuilder.Reply(Reader.Content!, [account.EmailAddress], replyAll);
        OpenCompose(account, message);
    }

    private void OpenCompose(AccountViewModel account, OutgoingMessage message)
    {
        var compose = new ComposeViewModel(Accounts.Where(a => a.IsConnected || a == account).ToList(), account, message, _settings, _dialogs, _notifications, OnMessageStored);
        _dialogs.ShowCompose(compose);
    }

    private void OnMessageStored(AccountViewModel account, FolderRole role)
    {
        if (account == SelectedAccount && SelectedFolder?.Role == role)
            _ = MessageList.RefreshAsync();
    }

    // ---- accounts & settings ------------------------------------------------------------------

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var vm = new AccountSetupViewModel(_tester, _vault, async account =>
        {
            var all = Accounts.Select(a => a.Account).Append(account).ToList();
            _accountStore.Save(all);
            var avm = CreateAccountViewModel(account);
            Accounts.Add(avm);
            SelectedAccount = avm;
            await ConnectAccountAsync(avm);
        }, onUseMicrosoft: AddMicrosoftAccountAsync);
        await _dialogs.ShowAccountSetupAsync(vm);
    }

    /// <summary>Outlook.com / Hotmail / Microsoft 365 through Microsoft sign-in in the system browser.</summary>
    [RelayCommand]
    private async Task AddMicrosoftAccountAsync()
    {
        var vm = new MicrosoftAccountViewModel(_microsoftAuth, _sessionFactory, async account =>
        {
            _accountStore.Save(Accounts.Select(a => a.Account).Append(account).ToList());
            var avm = CreateAccountViewModel(account);
            Accounts.Add(avm);
            SelectedAccount = avm;
            await ConnectAccountAsync(avm);
        }, IsMicrosoftKeyInUse);
        await _dialogs.ShowMicrosoftAccountAsync(vm);
    }

    private bool IsMicrosoftKeyInUse(string key) =>
        Accounts.Any(a => a.Account.IsMicrosoft && string.Equals(a.Account.MicrosoftAccountId, key, StringComparison.Ordinal));

    private async Task ReconnectMicrosoftAccountAsync(AccountViewModel account)
    {
        var vm = new MicrosoftAccountViewModel(_microsoftAuth, _sessionFactory, async updated =>
        {
            _accountStore.Save(Accounts.Select(a => a.Account.Id == updated.Id ? updated : a.Account).ToList());
            if (account == SelectedAccount)
            {
                MessageList.Clear();
                Reader.Clear();
                SelectedFolder = null;
            }
            await account.UpdateAccountAsync(updated);
            await ConnectAccountAsync(account);
        }, IsMicrosoftKeyInUse, account.Account, RemoveAccountAsync);
        await _dialogs.ShowMicrosoftAccountAsync(vm);
    }

    [RelayCommand]
    private void OpenMailHealth()
    {
        if (SelectedAccount is null || _mailHealth is null)
        {
            _notifications.Info("Mail Health", "Add an account first.");
            return;
        }
        _dialogs.ShowMailHealth(_mailHealth.Create(SelectedAccount.Account));
    }

    [RelayCommand]
    private Task OpenAccountSettingsAsync() =>
        SelectedAccount is null ? AddAccountAsync() : EditAccountAsync(SelectedAccount);

    [RelayCommand]
    private Task EnterPasswordAsync() =>
        SelectedAccount is null ? Task.CompletedTask
        : SelectedAccount.IsMicrosoft ? ReconnectMicrosoftAccountAsync(SelectedAccount)
        : EditAccountAsync(SelectedAccount, $"Enter the password for {SelectedAccount.EmailAddress} to connect.");

    public async Task EditAccountAsync(AccountViewModel account, string? message = null)
    {
        if (account.IsMicrosoft)
        {
            await ReconnectMicrosoftAccountAsync(account);
            return;
        }
        var vm = new AccountSetupViewModel(_tester, _vault, async updated =>
        {
            _accountStore.Save(Accounts.Select(a => a.Account.Id == updated.Id ? updated : a.Account).ToList());
            if (account == SelectedAccount)
            {
                MessageList.Clear();
                Reader.Clear();
                SelectedFolder = null;
            }
            await account.UpdateAccountAsync(updated);
            await ConnectAccountAsync(account);
        }, account.Account, RemoveAccountAsync, message);
        await _dialogs.ShowAccountSetupAsync(vm);
    }

    /// <summary>Longest time removal waits for an account's connection to close before detaching it.</summary>
    internal static TimeSpan SessionShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Removes an account from HCG Mail. Works whatever the account's connection state (connected,
    /// offline, TLS or sign-in failure, never connected) because it never contacts the mail server.
    /// Nothing is deleted on the server and other accounts are untouched. Returns true when the
    /// account was removed.
    /// </summary>
    public async Task<bool> RemoveAccountAsync(MailAccount account)
    {
        var secretText = account.IsMicrosoft
            ? $"its Microsoft sign-in (the tokens HCG Mail keeps in your {PlatformText.Device}'s protected storage) will be deleted"
            : $"its saved password will be deleted from {_vault.StorageDescription}";
        if (!await _dialogs.ConfirmAsync(
                "Remove this account?",
                $"{account.EmailAddress} will be removed from HCG Mail on this {PlatformText.Device}, {secretText}, and its send history on this {PlatformText.Device} will be cleared.\n\n" +
                $"No email is deleted from the mail server — you can add the account again at any time. HCG Mail does not store message contents on this {PlatformText.Device}, so there are no local messages to delete. Your other accounts are not affected." +
                (account.IsMicrosoft ? " To also withdraw HCG Mail's access on Microsoft's side, remove it at account.microsoft.com (personal) or myapps.microsoft.com (work or school)." : ""),
                "Remove account",
                destructive: true))
            return false;

        _logger.LogInformation("Removing account {AccountId}", account.Id);
        var vm = Accounts.FirstOrDefault(a => a.Account.Id == account.Id);

        // 1. Persist first: if the accounts file cannot be written, nothing has changed yet.
        try
        {
            _accountStore.Save(Accounts.Where(a => a.Account.Id != account.Id).Select(a => a.Account).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save accounts file while removing account {AccountId}", account.Id);
            _notifications.Error("Account not removed", $"HCG Mail could not update its settings file ({ex.GetType().Name}: {ex.Message}). Nothing was changed; check that the settings folder is writable and try again.");
            return false;
        }

        // 2. Update the interface immediately and stop the account's operations (never waiting on the server).
        if (vm is not null)
        {
            vm.PropertyChanged -= OnAccountPropertyChanged;
            if (SelectedAccount == vm)
                SelectedAccount = Accounts.FirstOrDefault(a => a != vm);
            Accounts.Remove(vm);
            await ShutDownAccountAsync(vm);
        }

        // 3. Local data of the account: its send history, and (Microsoft) its tokens.
        try
        {
            _deliveryLog?.Clear(account.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clear send history of removed account {AccountId}", account.Id);
        }
        if (account.IsMicrosoft)
        {
            try
            {
                if (account.MicrosoftAccountId is { } key && !IsMicrosoftKeyInUse(key))
                    await _microsoftAuth.SignOutAsync(key);
                _notifications.Success("Account removed", $"{account.EmailAddress} and its Microsoft sign-in were removed from this {PlatformText.Device}. No email was deleted from the server.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove Microsoft tokens of account {AccountId}", account.Id);
                _notifications.Warning("Account removed — sign-in not deleted",
                    $"{account.EmailAddress} was removed, but its Microsoft sign-in could not be deleted from the protected token store ({ex.GetType().Name}).");
            }
            return true;
        }

        // 4. Delete the stored password. A failure is reported honestly and retried at the next start.
        var credentialsDeleted = await TryDeleteCredentialAsync(account.CredentialKey, account.Id);
        if (credentialsDeleted)
        {
            _notifications.Success("Account removed", $"{account.EmailAddress} and its saved password were removed. No email was deleted from the server.");
        }
        else
        {
            _settings.Update(s => s with { PendingCredentialDeletions = s.PendingCredentialDeletions.Append(account.CredentialKey).ToList() });
            _notifications.Warning("Account removed — password not deleted",
                $"{account.EmailAddress} was removed, but its saved password could not be deleted from {_vault.StorageDescription}. " +
                "HCG Mail will try again the next time it starts. To remove it now, open " +
                (OperatingSystem.IsWindows() ? "Credential Manager → Windows Credentials and remove \"HCG Mail/" + account.CredentialKey + "\"."
                    : OperatingSystem.IsMacOS() ? "Keychain Access, search for \"HCG Mail\" and delete the item for " + account.CredentialKey + "."
                    : "your system's credential store and remove the entry " + account.CredentialKey + "."));
        }
        _logger.LogInformation("Removed account {AccountId}; credentials deleted: {CredentialsDeleted}", account.Id, credentialsDeleted);
        return true;
    }

    private async Task ShutDownAccountAsync(AccountViewModel account)
    {
        try
        {
            var dispose = account.DisposeAsync().AsTask();
            if (await Task.WhenAny(dispose, Task.Delay(SessionShutdownTimeout)) != dispose)
            {
                _logger.LogWarning("Connection of removed account {AccountId} did not close within {Timeout}; it was detached and will close in the background", account.Account.Id, SessionShutdownTimeout);
                _ = dispose.ContinueWith(t => _logger.LogDebug(t.Exception, "Background close of removed account finished"), TaskScheduler.Default);
            }
            else
            {
                await dispose;
            }
        }
        catch (Exception ex)
        {
            // Closing a connection must never prevent removal.
            _logger.LogWarning(ex, "Error while closing the connection of removed account {AccountId}", account.Account.Id);
        }
    }

    private async Task<bool> TryDeleteCredentialAsync(string key, string accountId)
    {
        try
        {
            await _vault.DeleteAsync(key);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete stored password for account {AccountId}", accountId);
            return false;
        }
    }

    /// <summary>Retries password deletions that failed when accounts were removed earlier.</summary>
    private async Task RetryPendingCredentialDeletionsAsync()
    {
        var pending = _settings.Current.PendingCredentialDeletions;
        if (pending.Count == 0)
            return;
        var stillPending = new List<string>();
        foreach (var key in pending)
        {
            if (Accounts.Any(a => a.Account.CredentialKey == key))
                continue; // the account was added again; its password is in use
            if (!await TryDeleteCredentialAsync(key, key))
                stillPending.Add(key);
        }
        _settings.Update(s => s with { PendingCredentialDeletions = stillPending });
        if (stillPending.Count > 0)
            _notifications.Warning("Saved password not yet removed", $"{stillPending.Count} password(s) from removed accounts could not be deleted from {_vault.StorageDescription}. HCG Mail will try again next time.");
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (SelectedAccount is not null)
            await ConnectAccountAsync(SelectedAccount);
    }

    [RelayCommand]
    private Task OpenSettingsAsync() =>
        _dialogs.ShowSettingsAsync(new SettingsViewModel(_settings, _themes, _dialogs, _paths, _vault, this));

    [RelayCommand]
    private void SelectAccount(AccountViewModel account) => SelectedAccount = account;

    // ---- helpers ------------------------------------------------------------------------------

    private void HandleError(string title, MailOperationException ex)
    {
        if (ex.Kind == MailErrorKind.Cancelled)
            return;
        SelectedAccount?.ReportError(ex);
        _notifications.Error(title, ex.UserMessage);
    }

    private void RefreshCommandStates()
    {
        ComposeCommand.NotifyCanExecuteChanged();
        ReplyCommand.NotifyCanExecuteChanged();
        ReplyAllCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        ToggleReadCommand.NotifyCanExecuteChanged();
        EditDraftCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ReadToggleText));
        OnPropertyChanged(nameof(FlagToggleText));
        OnPropertyChanged(nameof(ShowAccountError));
        OnPropertyChanged(nameof(ShowConnecting));
    }

    public async ValueTask DisposeAsync()
    {
        _autoRefresh?.Stop();
        foreach (var account in Accounts)
            await account.DisposeAsync();
    }
}
