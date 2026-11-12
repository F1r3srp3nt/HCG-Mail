using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;

namespace HCG.Mail.App.ViewModels;

/// <summary>How the body of the open message is shown.</summary>
public enum ReaderBodyMode
{
    /// <summary>The sender's HTML layout (sanitized).</summary>
    Original,

    /// <summary>The same HTML with the sender's colours, backgrounds and fonts removed.</summary>
    Readable,

    /// <summary>The text body.</summary>
    PlainText,
}

/// <summary>
/// The right pane: one message. Every state is explicit — nothing selected, loading, failed (with Retry), a
/// message without any content, or the body in one of three modes. A newer selection always wins over an
/// older load that finishes later.
/// </summary>
public sealed partial class MessageReaderViewModel : ObservableObject
{
    public const string OriginalStylesheet = "body { margin: 16px 20px; color: #000000; font-size: 10.5pt; } a { color: #0b63a8; }";
    public const string ReadableStylesheet =
        "body { margin: 18px 22px; color: #1a1f24; background-color: #ffffff; font-size: 11pt; line-height: 150%; } " +
        "a { color: #0b63a8; } td, th { color: #1a1f24; } h1, h2, h3 { color: #10161c; }";

    private readonly IHtmlSanitizer _sanitizer;
    private int _version;
    private IMailSession? _session;
    private Func<AttachmentViewModel, Task>? _saveAttachment;
    private SanitizedHtml? _sanitized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage), nameof(HasHtml), nameof(ShowHtmlView), nameof(ShowTextView), nameof(ShowRemoteBanner),
        nameof(Subject), nameof(FromName), nameof(FromAddress), nameof(ToLine), nameof(CcLine), nameof(HasCc), nameof(DateText), nameof(Initials),
        nameof(TextBody), nameof(HasAttachments), nameof(ShowPlaceholder), nameof(IsEmptyMessage), nameof(EmptyMessageText), nameof(CanShowFormatted))]
    private MessageContent? _content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHtmlView), nameof(ShowTextView))]
    private string? _sanitizedHtml;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoteBanner), nameof(RemoteBannerText))]
    private int _blockedRemoteCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemoteBanner))]
    private bool _remoteImagesAllowed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHtmlView), nameof(ShowTextView), nameof(ShowRemoteBanner), nameof(IsOriginalMode), nameof(IsReadableMode),
        nameof(IsPlainTextMode), nameof(HtmlStylesheet), nameof(HtmlBackground), nameof(ShowContrastHint))]
    private ReaderBodyMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenderNotice))]
    private string? _renderNotice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContrastHint))]
    private bool _lowContrastRisk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HtmlBackground))]
    private string? _canvasColor;

    public MessageReaderViewModel(IHtmlSanitizer sanitizer)
    {
        _sanitizer = sanitizer;
    }

    /// <summary>Mode used when a message opens (from settings).</summary>
    public MessageViewMode DefaultViewMode { get; set; } = MessageViewMode.Original;

    /// <summary>Load remote images without asking (from settings; off by default).</summary>
    public bool LoadRemoteImagesAutomatically { get; set; }

    public MessageItemViewModel? Item { get; private set; }
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];
    public IReadOnlyList<InlineResource> InlineResources => Content?.InlineResources ?? [];

    public bool HasMessage => Content is not null;
    public bool HasError => ErrorMessage is not null;
    public bool ShowPlaceholder => Content is null && !IsLoading && ErrorMessage is null;
    public bool HasHtml => Content?.HtmlBody is not null;
    public bool CanShowFormatted => HasHtml && SanitizedHtml is not null;
    public bool IsEmptyMessage => Content is not null && Content.HtmlBody is null && string.IsNullOrWhiteSpace(Content.TextBody);
    public bool ShowHtmlView => CanShowFormatted && Mode != ReaderBodyMode.PlainText;
    public bool ShowTextView => HasMessage && !ShowHtmlView && !IsEmptyMessage;
    public bool ShowRemoteBanner => ShowHtmlView && BlockedRemoteCount > 0 && !RemoteImagesAllowed;
    public bool ShowContrastHint => ShowHtmlView && Mode == ReaderBodyMode.Original && LowContrastRisk;
    public bool HasRenderNotice => RenderNotice is not null;
    public bool HasAttachments => Attachments.Count > 0;
    public bool IsOriginalMode => Mode == ReaderBodyMode.Original;
    public bool IsReadableMode => Mode == ReaderBodyMode.Readable;
    public bool IsPlainTextMode => Mode == ReaderBodyMode.PlainText;

    public string HtmlStylesheet => Mode == ReaderBodyMode.Readable ? ReadableStylesheet : OriginalStylesheet;

    /// <summary>The page behind the message: the email's own background colour, otherwise white — never the app's theme colour.</summary>
    public IBrush HtmlBackground =>
        Mode == ReaderBodyMode.Original && CanvasColor is not null && Color.TryParse(CanvasColor, out var color)
            ? new SolidColorBrush(color)
            : Brushes.White;

    public string RemoteBannerText => BlockedRemoteCount == 1
        ? "1 remote image or resource was blocked to protect your privacy."
        : $"{BlockedRemoteCount} remote images or resources were blocked to protect your privacy.";

    public const string RemoteImagesPrivacyText =
        "Loading remote images contacts the sender's servers: they learn that and when you opened the message, and see your IP address. " +
        "Images included in the message itself are always shown.";

    public string EmptyMessageText => Attachments.Count switch
    {
        0 => "This message has no content.",
        1 => "This message has no text. It contains 1 attachment.",
        var n => $"This message has no text. It contains {n} attachments.",
    };

    public string Subject => string.IsNullOrWhiteSpace(Content?.Subject) ? "(no subject)" : Content!.Subject;
    public string FromName => Content?.From?.DisplayName ?? "(unknown sender)";
    public string FromAddress => Content?.From is { } f && !string.IsNullOrWhiteSpace(f.Name) ? f.Address : "";
    public string Initials => AccountViewModel.MakeInitials(Content?.From?.Name ?? Content?.From?.Address ?? "?");
    public string ToLine => Content is null ? "" : FormatList(Content.To);
    public string CcLine => Content is null ? "" : FormatList(Content.Cc);
    public bool HasCc => Content?.Cc.Count > 0;
    public string DateText => Content?.Date.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) ?? "";
    public string TextBody => Content?.TextBody ?? "";

    public async Task LoadAsync(IMailSession session, MessageItemViewModel item, Func<AttachmentViewModel, Task> saveAttachment)
    {
        var version = ++_version;
        _session = session;
        _saveAttachment = saveAttachment;
        Item = item;
        IsLoading = true;
        ErrorMessage = null;
        RenderNotice = null;
        try
        {
            var content = await session.GetMessageAsync(item.Ref);
            if (version != _version)
                return; // the user selected another message meanwhile; never show this one
            Show(content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || version == _version)
        {
            if (version != _version)
                return;
            Content = null;
            SanitizedHtml = null;
            Attachments.Clear();
            ErrorMessage = ex is MailOperationException moe ? moe.UserMessage : $"The message could not be read ({ex.GetType().Name}).";
        }
        finally
        {
            if (version == _version)
                IsLoading = false;
        }
    }

    /// <summary>Shows already loaded content (also used after Retry).</summary>
    public void Show(MessageContent content)
    {
        RemoteImagesAllowed = LoadRemoteImagesAutomatically;
        Mode = content.HtmlBody is null ? ReaderBodyMode.PlainText
            : DefaultViewMode == MessageViewMode.Readable ? ReaderBodyMode.Readable : ReaderBodyMode.Original;
        Attachments.Clear();
        foreach (var attachment in content.Attachments)
            Attachments.Add(new AttachmentViewModel(attachment, _saveAttachment ?? (_ => Task.CompletedTask)));
        Content = content;
        Render();
        OnPropertyChanged(nameof(EmptyMessageText));
        OnPropertyChanged(nameof(IsEmptyMessage));
        OnPropertyChanged(nameof(ShowTextView));
    }

    public void Clear()
    {
        _version++;
        Item = null;
        Content = null;
        SanitizedHtml = null;
        _sanitized = null;
        Attachments.Clear();
        ErrorMessage = null;
        RenderNotice = null;
        IsLoading = false;
        BlockedRemoteCount = 0;
        LowContrastRisk = false;
        CanvasColor = null;
    }

    private bool CanRetry() => ErrorMessage is not null && _session is not null && Item is not null;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => LoadAsync(_session!, Item!, _saveAttachment ?? (_ => Task.CompletedTask));

    [RelayCommand]
    private void LoadRemoteImages()
    {
        if (Content is null)
            return;
        RemoteImagesAllowed = true;
        Render();
    }

    [RelayCommand]
    private void SetMode(ReaderBodyMode mode)
    {
        if (mode != ReaderBodyMode.PlainText && !HasHtml)
            return;
        Mode = mode;
        RenderNotice = null;
        Render();
    }

    /// <summary>Kept for the existing toolbar binding and keyboard shortcut.</summary>
    [RelayCommand]
    private void ToggleViewMode() => SetMode(Mode == ReaderBodyMode.PlainText ? ReaderBodyMode.Original : ReaderBodyMode.PlainText);

    /// <summary>
    /// Called by the view when the HTML engine fails on this message: switch to the plain-text body and say so,
    /// with Retry available through the view buttons.
    /// </summary>
    public void OnHtmlRenderFailed(string reason)
    {
        if (Content is null || Mode == ReaderBodyMode.PlainText)
            return;
        Mode = ReaderBodyMode.PlainText;
        RenderNotice = "The formatted version of this message could not be displayed, so its plain text is shown. Choose Original or Readable to try again.";
    }

    private void Render()
    {
        if (Content?.HtmlBody is not { } html)
        {
            SanitizedHtml = null;
            _sanitized = null;
            BlockedRemoteCount = 0;
            LowContrastRisk = false;
            CanvasColor = null;
            return;
        }

        try
        {
            var mode = Mode == ReaderBodyMode.Readable ? MessageViewMode.Readable : MessageViewMode.Original;
            _sanitized = _sanitizer.Sanitize(html, new HtmlSanitizeOptions(RemoteImagesAllowed, mode));
            if (!_sanitized.HasVisibleContent && !string.IsNullOrWhiteSpace(Content.TextBody))
            {
                SanitizedHtml = null;
                Mode = ReaderBodyMode.PlainText;
                return;
            }
            SanitizedHtml = _sanitized.Html;
            if (!RemoteImagesAllowed)
                BlockedRemoteCount = _sanitized.BlockedRemoteResources;
            LowContrastRisk = _sanitized.LowContrastRisk;
            CanvasColor = _sanitized.CanvasColor;
        }
        catch (Exception ex)
        {
            SanitizedHtml = null;
            Mode = ReaderBodyMode.PlainText;
            RenderNotice = $"The formatted version of this message could not be prepared ({ex.GetType().Name}), so its plain text is shown.";
        }
        OnPropertyChanged(nameof(CanShowFormatted));
    }

    private static string FormatList(IReadOnlyList<EmailAddress> addresses) =>
        string.Join(", ", addresses.Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Address : $"{a.Name} <{a.Address}>"));
}
