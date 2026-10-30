using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.Services;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.App.ViewModels;

public sealed class ComposeAttachmentViewModel(OutgoingAttachment attachment)
{
    public OutgoingAttachment Attachment { get; } = attachment;
    public string Name => Attachment.FileName;
    public string SizeText => AttachmentViewModel.FormatSize(Attachment.Size);
}

/// <summary>Plain-text message composer for new messages, replies, forwards and drafts.</summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    public const long LargeAttachmentWarningBytes = 20L * 1024 * 1024;

    private readonly OutgoingMessage _initial;
    private readonly AppSettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly Action<AccountViewModel, FolderRole>? _onStored;
    private readonly string _messageId;
    private MessageRef? _draftRef;
    private string _savedSnapshot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SaveDraftCommand))]
    [NotifyPropertyChangedFor(nameof(FromWarning))]
    private AccountViewModel _selectedFrom;

    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _cc = "";
    [ObservableProperty] private string _bcc = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WindowTitle))] private string _subject = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private bool _showCcBcc;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SaveDraftCommand), nameof(AddAttachmentsCommand))]
    private bool _isBusy;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] private string? _statusMessage;
    [ObservableProperty] private bool _statusIsError;

    public ComposeViewModel(
        IReadOnlyList<AccountViewModel> accounts,
        AccountViewModel from,
        OutgoingMessage initial,
        AppSettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        Action<AccountViewModel, FolderRole>? onStored = null)
    {
        _initial = initial;
        _settings = settings;
        _dialogs = dialogs;
        _notifications = notifications;
        _onStored = onStored;
        _messageId = initial.MessageId ?? $"{Guid.NewGuid():N}@{Domain(from.EmailAddress)}";
        _draftRef = initial.DraftRef;

        FromAccounts = new ObservableCollection<AccountViewModel>(accounts);
        _selectedFrom = from;
        _to = RecipientParser.Format(initial.To);
        _cc = RecipientParser.Format(initial.Cc);
        _bcc = RecipientParser.Format(initial.Bcc);
        _showCcBcc = initial.Cc.Count > 0 || initial.Bcc.Count > 0;
        _subject = initial.Subject;
        _body = initial.TextBody;
        foreach (var attachment in initial.Attachments)
            Attachments.Add(new ComposeAttachmentViewModel(attachment));
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
        _savedSnapshot = initial.Kind == ComposeKind.EditDraft ? Snapshot() : "";
    }

    public ObservableCollection<AccountViewModel> FromAccounts { get; }
    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasMultipleAccounts => FromAccounts.Count > 1;
    public bool HasStatus => StatusMessage is not null;
    public ComposeKind Kind => _initial.Kind;

    public string WindowTitle => (string.IsNullOrWhiteSpace(Subject) ? HeadingFor(_initial.Kind) : Subject) + " — HCG Mail";
    public string Heading => HeadingFor(_initial.Kind);

    public string? FromWarning => SelectedFrom.IsConnected ? null : $"{SelectedFrom.EmailAddress} is not connected ({SelectedFrom.StatusText}).";

    public bool IsDirty => Snapshot() != _savedSnapshot;

    /// <summary>Raised when the window should close (after sending or discarding).</summary>
    public event EventHandler? CloseRequested;

    private bool CanUseServer() => !IsBusy && SelectedFrom.Session is not null;

    [RelayCommand(CanExecute = nameof(CanUseServer))]
    private async Task SendAsync()
    {
        var message = BuildMessage(out var invalid);
        if (invalid.Count > 0)
        {
            SetStatus("These recipients are not valid email addresses: " + string.Join(", ", invalid), error: true);
            return;
        }
        if (!message.HasRecipients)
        {
            SetStatus("Add at least one recipient.", error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(message.Subject)
            && !await _dialogs.ConfirmAsync("Send without a subject?", "This message has no subject.", "Send anyway"))
            return;
        if (message.TotalAttachmentSize > LargeAttachmentWarningBytes
            && !await _dialogs.ConfirmAsync("Large attachments",
                $"The attachments total {AttachmentViewModel.FormatSize(message.TotalAttachmentSize)}. Many mail servers reject messages larger than 20–25 MB.",
                "Send anyway"))
            return;

        var account = SelectedFrom;
        var session = account.Session!;
        IsBusy = true;
        SetStatus("Sending…", error: false);
        try
        {
            var result = await session.SendAsync(message, _settings.Current.SaveCopyToSentFolder);
            if (result.Warning is not null)
                _notifications.Warning("Message sent", result.Warning);
            else
                _notifications.Success("Message sent", result.SavedToSent ? "A copy was saved to your Sent folder." : "Your message was sent.");
            _onStored?.Invoke(account, FolderRole.Sent);
            if (_draftRef is not null)
                _onStored?.Invoke(account, FolderRole.Drafts);
            _savedSnapshot = Snapshot();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (MailOperationException ex)
        {
            SetStatus("Not sent: " + ex.UserMessage, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseServer))]
    private async Task SaveDraftAsync() => await SaveDraftCoreAsync();

    [RelayCommand(CanExecute = nameof(CanAddAttachments))]
    private async Task AddAttachmentsAsync()
    {
        var paths = await _dialogs.PickFilesAsync("Attach files");
        foreach (var path in paths)
        {
            try
            {
                Attachments.Add(new ComposeAttachmentViewModel(OutgoingAttachment.FromFile(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Could not attach {Path.GetFileName(path)}: {ex.Message}", error: true);
            }
        }
    }

    private bool CanAddAttachments() => !IsBusy;

    [RelayCommand]
    private void RemoveAttachment(ComposeAttachmentViewModel attachment) => Attachments.Remove(attachment);

    [RelayCommand]
    private void ToggleCcBcc() => ShowCcBcc = !ShowCcBcc;

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (IsDirty && !await _dialogs.ConfirmAsync("Discard this message?", "Your changes will be lost.", "Discard", destructive: true))
            return;
        _savedSnapshot = Snapshot();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks what to do with unsaved changes. Returns true when the window may close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty || IsBusy)
            return !IsBusy;
        var canSave = SelectedFrom.Session is not null;
        var choice = await _dialogs.ChooseAsync(
            "Save this message as a draft?",
            canSave ? "Your message has not been sent." : "Your message has not been sent, and the account is not connected so it cannot be saved as a draft.",
            canSave ? "Save draft" : "Keep editing",
            "Discard",
            "Cancel");
        switch (choice)
        {
            case DialogChoice.Primary when canSave:
                return await SaveDraftCoreAsync();
            case DialogChoice.Secondary:
                _savedSnapshot = Snapshot();
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> SaveDraftCoreAsync()
    {
        var message = BuildMessage(out var invalid);
        var account = SelectedFrom;
        if (account.Session is null)
            return false;
        IsBusy = true;
        SetStatus("Saving draft…", error: false);
        try
        {
            var result = await account.Session.SaveDraftAsync(message);
            _draftRef = result.DraftRef;
            _savedSnapshot = Snapshot();
            var note = invalid.Count > 0 ? $" Invalid addresses were left out: {string.Join(", ", invalid)}." : "";
            SetStatus($"Draft saved to the server at {DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}.{note}", error: invalid.Count > 0);
            _onStored?.Invoke(account, FolderRole.Drafts);
            return true;
        }
        catch (MailOperationException ex)
        {
            SetStatus("Draft not saved: " + ex.UserMessage, error: true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal OutgoingMessage BuildMessage(out List<string> invalid)
    {
        var to = RecipientParser.Parse(To);
        var cc = RecipientParser.Parse(Cc);
        var bcc = RecipientParser.Parse(Bcc);
        invalid = [.. to.Invalid, .. cc.Invalid, .. bcc.Invalid];

        return _initial with
        {
            To = to.Addresses,
            Cc = cc.Addresses,
            Bcc = bcc.Addresses,
            Subject = Subject.Replace('\r', ' ').Replace('\n', ' ').Trim(),
            TextBody = Body,
            Attachments = Attachments.Select(a => a.Attachment).ToList(),
            MessageId = _messageId,
            DraftRef = _draftRef,
        };
    }

    private string Snapshot() =>
        string.Join("\u001F", To, Cc, Bcc, Subject, Body, string.Join("|", Attachments.Select(a => a.Name + a.Attachment.Size)));

    private void SetStatus(string message, bool error)
    {
        StatusMessage = message;
        StatusIsError = error;
    }

    private static string HeadingFor(ComposeKind kind) => kind switch
    {
        ComposeKind.Reply => "Reply",
        ComposeKind.ReplyAll => "Reply all",
        ComposeKind.Forward => "Forward",
        ComposeKind.EditDraft => "Edit draft",
        _ => "New message",
    };

    private static string Domain(string email)
    {
        var at = email.LastIndexOf('@');
        return at >= 0 ? email[(at + 1)..] : "hcg-mail.invalid";
    }
}
