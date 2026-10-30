using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;

namespace HCG.Mail.App.ViewModels;

/// <summary>The right pane: one message, rendered as sanitized HTML or as plain text.</summary>
public sealed partial class MessageReaderViewModel : ObservableObject
{
    private readonly IHtmlSanitizer _sanitizer;
    private int _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage), nameof(HasHtml), nameof(ShowHtmlView), nameof(ShowTextView), nameof(ShowRemoteBanner),
        nameof(Subject), nameof(FromName), nameof(FromAddress), nameof(ToLine), nameof(CcLine), nameof(HasCc), nameof(DateText), nameof(Initials),
        nameof(TextBody), nameof(HasAttachments), nameof(ShowPlaceholder))]
    private MessageContent? _content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowPlaceholder))]
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
    [NotifyPropertyChangedFor(nameof(ShowHtmlView), nameof(ShowTextView), nameof(ShowRemoteBanner), nameof(ViewModeText))]
    private bool _showPlainText;

    public MessageReaderViewModel(IHtmlSanitizer sanitizer)
    {
        _sanitizer = sanitizer;
    }

    public MessageItemViewModel? Item { get; private set; }
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];
    public IReadOnlyList<InlineResource> InlineResources => Content?.InlineResources ?? [];

    public bool HasMessage => Content is not null;
    public bool HasError => ErrorMessage is not null;
    public bool ShowPlaceholder => Content is null && !IsLoading && ErrorMessage is null;
    public bool HasHtml => Content?.HtmlBody is not null;
    public bool ShowHtmlView => HasHtml && !ShowPlainText && SanitizedHtml is not null;
    public bool ShowTextView => HasMessage && !ShowHtmlView;
    public bool ShowRemoteBanner => ShowHtmlView && BlockedRemoteCount > 0 && !RemoteImagesAllowed;
    public bool HasAttachments => Attachments.Count > 0;
    public string ViewModeText => ShowPlainText ? "Show formatted" : "Show plain text";
    public string RemoteBannerText => BlockedRemoteCount == 1
        ? "1 remote image was blocked to protect your privacy."
        : $"{BlockedRemoteCount} remote images or resources were blocked to protect your privacy.";

    public string Subject => string.IsNullOrWhiteSpace(Content?.Subject) ? "(no subject)" : Content!.Subject;
    public string FromName => Content?.From?.DisplayName ?? "(unknown sender)";
    public string FromAddress => Content?.From is { } f && !string.IsNullOrWhiteSpace(f.Name) ? f.Address : "";
    public string Initials => AccountViewModel.MakeInitials(Content?.From?.Name ?? Content?.From?.Address ?? "?");
    public string ToLine => Content is null ? "" : FormatList(Content.To);
    public string CcLine => Content is null ? "" : FormatList(Content.Cc);
    public bool HasCc => Content?.Cc.Count > 0;
    public string DateText => Content?.Date.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) ?? "";
    public string TextBody => Content?.TextBody is { Length: > 0 } t ? t : "(This message has no text content.)";

    public async Task LoadAsync(IMailSession session, MessageItemViewModel item, Func<AttachmentViewModel, Task> saveAttachment)
    {
        var version = ++_version;
        Item = item;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var content = await session.GetMessageAsync(item.Ref);
            if (version != _version)
                return;
            RemoteImagesAllowed = false;
            ShowPlainText = false;
            Attachments.Clear();
            foreach (var attachment in content.Attachments)
                Attachments.Add(new AttachmentViewModel(attachment, saveAttachment));
            Sanitize(content, allowRemote: false);
            Content = content;
        }
        catch (MailOperationException ex)
        {
            if (version != _version)
                return;
            Content = null;
            Attachments.Clear();
            ErrorMessage = ex.UserMessage;
        }
        finally
        {
            if (version == _version)
                IsLoading = false;
        }
    }

    public void Clear()
    {
        _version++;
        Item = null;
        Content = null;
        SanitizedHtml = null;
        Attachments.Clear();
        ErrorMessage = null;
        IsLoading = false;
        BlockedRemoteCount = 0;
    }

    [RelayCommand]
    private void LoadRemoteImages()
    {
        if (Content is null)
            return;
        RemoteImagesAllowed = true;
        Sanitize(Content, allowRemote: true);
    }

    [RelayCommand]
    private void ToggleViewMode() => ShowPlainText = !ShowPlainText;

    private void Sanitize(MessageContent content, bool allowRemote)
    {
        if (content.HtmlBody is null)
        {
            SanitizedHtml = null;
            BlockedRemoteCount = 0;
            return;
        }
        var result = _sanitizer.Sanitize(content.HtmlBody, allowRemote);
        SanitizedHtml = result.Html;
        if (!allowRemote)
            BlockedRemoteCount = result.BlockedRemoteResources;
    }

    private static string FormatList(IReadOnlyList<EmailAddress> addresses) =>
        string.Join(", ", addresses.Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Address : $"{a.Name} <{a.Address}>"));
}
