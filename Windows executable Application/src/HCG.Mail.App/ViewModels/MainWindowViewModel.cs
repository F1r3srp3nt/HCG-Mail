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
    [NotifyPropertyChangedFor(nameof(HasSelectedAccount), nameof(ShowNeedsPassword), nameof(ShowAccountError), nameof(ShowConnecting))]
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
        ILoggerFactory loggerFactory)
    {
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
        Reader = new MessageReaderViewModel(sanitizer);
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
    }

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
        StatusText = checkedText + "Manual refresh (F5) — no automatic checking";
    }

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
                $"“{attachment.Name}” is a type of file that can run programs on your computer. HCG Mail will save it but never open it. Only open it later if you trust the sender.",
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
        });
        await _dialogs.ShowAccountSetupAsync(vm);
    }

    [RelayCommand]
    private Task OpenAccountSettingsAsync() =>
        SelectedAccount is null ? AddAccountAsync() : EditAccountAsync(SelectedAccount);

    [RelayCommand]
    private Task EnterPasswordAsync() =>
        SelectedAccount is null ? Task.CompletedTask : EditAccountAsync(SelectedAccount, $"Enter the password for {SelectedAccount.EmailAddress} to connect.");

    public async Task EditAccountAsync(AccountViewModel account, string? message = null)
    {
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

    private async Task RemoveAccountAsync(MailAccount account)
    {
        if (!await _dialogs.ConfirmAsync(
                "Remove this account?",
                $"{account.EmailAddress} will be removed from HCG Mail and its saved password deleted from {_vault.StorageDescription}. No email on the server is deleted.",
                "Remove account",
                destructive: true))
            return;

        var vm = Accounts.FirstOrDefault(a => a.Account.Id == account.Id);
        try
        {
            await _vault.DeleteAsync(account.CredentialKey);
        }
        catch (CredentialVaultException ex)
        {
            _logger.LogWarning(ex, "Could not delete stored password for account {AccountId}", account.Id);
        }
        _accountStore.Save(Accounts.Where(a => a.Account.Id != account.Id).Select(a => a.Account).ToList());
        if (vm is not null)
        {
            Accounts.Remove(vm);
            await vm.DisposeAsync();
        }
        SelectedAccount = Accounts.FirstOrDefault();
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
        foreach (var account in Accounts)
            await account.DisposeAsync();
    }
}
