using HCG.Mail.Core.Addresses;

namespace HCG.Mail.Core.Mail;

[Flags]
public enum MessageFlags
{
    None = 0,
    Seen = 1 << 0,
    Flagged = 1 << 1,
    Answered = 1 << 2,
    Draft = 1 << 3,
    Deleted = 1 << 4,
}

/// <summary>Identifies a message on the server. UIDs are only meaningful together with the folder's UIDVALIDITY.</summary>
public sealed record MessageRef(string FolderFullName, uint Uid, uint UidValidity);

public sealed record MessageSummary
{
    public required MessageRef Ref { get; init; }
    public EmailAddress? From { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public string Subject { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string Preview { get; init; } = "";
    public MessageFlags Flags { get; init; }
    public bool HasAttachments { get; init; }
    public long Size { get; init; }
    public string? MessageId { get; init; }
}

public sealed record FolderPage(
    string FolderFullName,
    IReadOnlyList<MessageSummary> Messages,
    int TotalCount,
    int UnreadCount,
    uint UidValidity,
    bool HasOlder);

public sealed record AttachmentInfo(
    int Index,
    string OriginalFileName,
    string SafeFileName,
    string ContentType,
    long? SizeEstimate,
    bool IsPotentiallyDangerous);

/// <summary>An image embedded in an HTML body and referenced via <c>cid:</c>.</summary>
public sealed record InlineResource(string ContentId, string ContentType, byte[] Data);

public sealed record MessageContent
{
    public required MessageRef Ref { get; init; }
    public EmailAddress? From { get; init; }
    public IReadOnlyList<EmailAddress> ReplyTo { get; init; } = [];
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
    public string Subject { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string? MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Plain-text body. Derived from the HTML part when the message has no text part.</summary>
    public string TextBody { get; init; } = "";

    /// <summary>Raw, unsanitized HTML from the sender. Must pass through an <c>IHtmlSanitizer</c> before display.</summary>
    public string? HtmlBody { get; init; }

    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
    public IReadOnlyList<InlineResource> InlineResources { get; init; } = [];
    public MessageFlags Flags { get; init; }
}
