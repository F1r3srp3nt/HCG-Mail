using HCG.Mail.Core.Services;
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

public sealed record PriorityOption(MessagePriority Value, string Label)
{
    public override string ToString() => Label;
}

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
    private readonly IUserPrompts _dialogs;
    private readonly INotificationService _notifications;
    private readonly Action<AccountViewModel, FolderRole>? _onStored;
    private readonly OutboxService? _outbox;
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsHighPriority), nameof(IsLowPriority))] private PriorityOption _selectedPriority;

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
        IUserPrompts dialogs,
        INotificationService notifications,
        Action<AccountViewModel, FolderRole>? onStored = null,
        OutboxService? outbox = null)
    {
        _outbox = outbox;
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
        _signature = settings.Current.SignatureFor(from.Account.Id);
        _body = SignatureBlock.Insert(initial.TextBody, _signature, initial.Kind);
        _selectedPriority = PriorityOptions.First(o => o.Value == initial.Priority);
        foreach (var attachment in initial.Attachments)
            Attachments.Add(new ComposeAttachmentViewModel(attachment));
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
        // "Unchanged" means as opened: closing a new message, reply or forward that the user has not edited does not
        // ask about saving a draft (it used to, because new messages started with an empty baseline).
        _savedSnapshot = Snapshot();
    }

    private string? _signature;

    partial void OnSelectedFromChanged(AccountViewModel value)
    {
        // Another sending account: its signature replaces the previous one, unless the user changed that text.
        var next = _settings.Current.SignatureFor(value.Account.Id);
        Body = SignatureBlock.Replace(Body, _signature, next);
        _signature = next;
    }

    public static IReadOnlyList<PriorityOption> PriorityOptions { get; } =
    [
        new(MessagePriority.Low, "Low"),
        new(MessagePriority.Normal, "Normal"),
        new(MessagePriority.High, "High"),
    ];

    public const string PriorityExplanation =
        "Priority is a request to the recipient's mail program. Each mail system decides whether and how to show it (Outlook and Apple Mail mark it; " +
        "Gmail does not show it). It does not affect spam filtering and does not make delivery faster or guaranteed.";

    public bool IsHighPriority => SelectedPriority.Value == MessagePriority.High;
    public bool IsLowPriority => SelectedPriority.Value == MessagePriority.Low;

    public ObservableCollection<AccountViewModel> FromAccounts { get; }
    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasMultipleAccounts => FromAccounts.Count > 1;
    public bool HasStatus => StatusMessage is not null;
    public ComposeKind Kind => _initial.Kind;

    public string WindowTitle => (string.IsNullOrWhiteSpace(Subject) ? HeadingFor(_initial.Kind) : Subject) + " — HCG Mail";
    public string Heading => HeadingFor(_initial.Kind);

    public string? FromWarning => SelectedFrom.IsConnected ? null
        : _outbox is not null ? $"{SelectedFrom.EmailAddress} is offline. Sending puts the message in the Outbox; it goes out automatically when the account reconnects."
        : $"{SelectedFrom.EmailAddress} is not connected ({SelectedFrom.StatusText}).";

    /// <summary>The account cannot reach its server now: send and save go to the Outbox on this device.</summary>
    private bool UseOutbox => _outbox is not null && (SelectedFrom.Session is null || SelectedFrom.Session is IOfflineAwareSession { IsOffline: true } || !SelectedFrom.IsConnected);

    public bool IsDirty => Snapshot() != _savedSnapshot;

    /// <summary>Raised when the window should close (after sending or discarding).</summary>
    public event EventHandler? CloseRequested;

    private bool CanUseServer() => !IsBusy && (SelectedFrom.Session is not null || _outbox is not null);

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

        // The previous attempt may have reached the server: sending again could deliver the message twice.
        if (PossiblyAlreadySent
            && !await _dialogs.ConfirmAsync("This message may already have been sent",
                "The connection was lost while the outgoing server was receiving it, so it is not known whether it was sent. " +
                "Check your Sent folder, or ask a recipient, before sending it again. Sending again may deliver it twice.",
                "Send again", destructive: true))
            return;

        var account = SelectedFrom;
        if (UseOutbox)
        {
            QueueForSending(account, message, "The account is offline");
            return;
        }
        var session = account.Session!;
        IsBusy = true;
        SetStatus("Sending…", error: false);
        try
        {
            var result = await session.SendAsync(message, _settings.Current.SaveCopyToSentFolder);
            var accepted = DescribeAcceptance(result);
            if (result.Warning is not null)
                _notifications.Warning("Message sent", accepted + " " + result.Warning);
            else
                _notifications.Success("Message sent", accepted + (result.SavedToSent ? " A copy was saved to your Sent folder." : ""));
            _onStored?.Invoke(account, FolderRole.Sent);
            if (_draftRef is not null)
                _onStored?.Invoke(account, FolderRole.Drafts);
            _savedSnapshot = Snapshot();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (MailOperationException ex) when (ex.OutcomeUnknown)
        {
            PossiblyAlreadySent = true;
            SetStatus("Sending result unknown: " + ex.UserMessage + " The message may already have been sent. Check your Sent folder before sending again.", error: true);
        }
        catch (MailOperationException ex) when (_outbox is not null && OfflineCapableConnectivity(ex))
        {
            // The connection failed before the server received the message: it waits in the Outbox instead of being lost.
            IsBusy = false;
            QueueForSending(account, message, "The server could not be reached (" + ex.UserMessage + ")");
        }
        catch (MailOperationException ex)
        {
            SetStatus("Not sent: " + ex.UserMessage, error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An attachment that was moved, deleted or locked after it was added: nothing was sent.
            SetStatus("Not sent: an attachment could not be read (" + ex.Message + "). Remove it or attach it again.", error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool OfflineCapableConnectivity(MailOperationException ex) =>
        !ex.OutcomeUnknown && Core.Network.ReconnectPolicy.ModeFor(ex.Kind) != Core.Network.RecoveryMode.Never;

    private void QueueForSending(AccountViewModel account, OutgoingMessage message, string why)
    {
        try
        {
            _outbox!.Enqueue(account.Account.Id, message, OutboxKind.Send, _settings.Current.SaveCopyToSentFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Not sent, and it could not be put in the Outbox (" + ex.Message + "). Keep this window open and try again.", error: true);
            return;
        }
        _notifications.Info("Saved in the Outbox",
            $"{why}. “{(string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject)}” will be sent automatically when {account.EmailAddress} reconnects.");
        _savedSnapshot = Snapshot();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The last send attempt lost its connection after the server started receiving the message.</summary>
    [ObservableProperty]
    private bool _possiblyAlreadySent;

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
        var canSave = SelectedFrom.Session is not null || _outbox is not null;
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

    /// <summary>
    /// Saves unsaved changes as the server draft without being asked (the window calls this every minute), so a crash, a
    /// lost laptop battery or a closed lid does not lose the message. Quiet: it only runs when the account is connected,
    /// nothing else is in progress and every address is valid (a draft would leave invalid ones out).
    /// </summary>
    public async Task<bool> AutosaveAsync()
    {
        if (!IsDirty || IsBusy || (SelectedFrom.Session is null && _outbox is null))
            return false;
        BuildMessage(out var invalid);
        if (invalid.Count > 0)
            return false;
        if (!await SaveDraftCoreAsync())
            return false;
        SetStatus(_lastDraftSavedLocally
            ? $"Offline: draft saved on this device automatically at {DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}. It is copied to your Drafts folder when the account reconnects."
            : $"Draft saved automatically at {DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}.", error: false);
        return true;
    }

    private bool _lastDraftSavedLocally;

    private async Task<bool> SaveDraftCoreAsync()
    {
        _lastDraftSavedLocally = false;
        var message = BuildMessage(out var invalid);
        var account = SelectedFrom;
        if (UseOutbox)
            return SaveDraftLocally(account, message, invalid);
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
        catch (MailOperationException ex) when (_outbox is not null && OfflineCapableConnectivity(ex))
        {
            IsBusy = false;
            return SaveDraftLocally(account, message, invalid);
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

    /// <summary>Offline: the draft is kept on this device and copied to the Drafts folder when the account reconnects.</summary>
    private bool SaveDraftLocally(AccountViewModel account, OutgoingMessage message, List<string> invalid)
    {
        try
        {
            _outbox!.Enqueue(account.Account.Id, message, OutboxKind.Draft, saveCopyToSent: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Draft not saved (" + ex.Message + ").", error: true);
            return false;
        }
        _savedSnapshot = Snapshot();
        _lastDraftSavedLocally = true;
        var note = invalid.Count > 0 ? $" Invalid addresses were left out: {string.Join(", ", invalid)}." : "";
        SetStatus($"Offline: draft saved on this device at {DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}. It is copied to your Drafts folder when the account reconnects.{note}", error: invalid.Count > 0);
        return true;
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
            Priority = SelectedPriority.Value,
            Attachments = Attachments.Select(a => a.Attachment).ToList(),
            MessageId = _messageId,
            DraftRef = _draftRef,
        };
    }

    private string Snapshot() =>
        string.Join("\u001F", To, Cc, Bcc, Subject, Body, SelectedPriority.Value, string.Join("|", Attachments.Select(a => a.Name + a.Attachment.Size)));

    /// <summary>States what the server's reply means — accepted for delivery, not delivered.</summary>
    internal static string DescribeAcceptance(Core.Services.SendResult result)
    {
        if (result.Delivery is not { State: Core.Delivery.DeliveryState.Accepted } d)
            return "Your message was sent.";
        var reply = d.ServerReply is null ? "" : $" ({d.ServerReply})";
        return $"Accepted by your outgoing server {d.SmtpServer}{reply}. Delivery to the recipient is not confirmed and inbox placement is unknown.";
    }

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
