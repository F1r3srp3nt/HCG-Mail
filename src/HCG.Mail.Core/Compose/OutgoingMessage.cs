using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.Core.Compose;

public enum ComposeKind
{
    New,
    Reply,
    ReplyAll,
    Forward,
    EditDraft,
}

public sealed class OutgoingAttachment
{
    private OutgoingAttachment(string fileName, string? contentType, string? filePath, byte[]? data, long size)
    {
        FileName = AttachmentFileNames.Sanitize(fileName);
        ContentType = contentType;
        FilePath = filePath;
        Data = data;
        Size = size;
    }

    public string FileName { get; }

    /// <summary>MIME type, or null to infer it from the file name when the message is built.</summary>
    public string? ContentType { get; }

    /// <summary>Set for files chosen from disk; the file is read when the message is sent.</summary>
    public string? FilePath { get; }

    /// <summary>Set for attachments carried over from another message (forward, draft).</summary>
    public byte[]? Data { get; }

    public long Size { get; }

    public static OutgoingAttachment FromFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The attachment file was not found.", path);
        return new OutgoingAttachment(info.Name, null, info.FullName, null, info.Length);
    }

    public static OutgoingAttachment FromBytes(string fileName, string? contentType, byte[] data) =>
        new(fileName, contentType, null, data, data.LongLength);

    public Stream OpenRead() =>
        Data is not null ? new MemoryStream(Data, writable: false) : File.OpenRead(FilePath!);
}

public sealed record OutgoingMessage
{
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
    public string Subject { get; init; } = "";
    public string TextBody { get; init; } = "";
    public IReadOnlyList<OutgoingAttachment> Attachments { get; init; } = [];

    public ComposeKind Kind { get; init; } = ComposeKind.New;
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The message being replied to, so it can be flagged \Answered after sending.</summary>
    public MessageRef? RepliedMessage { get; init; }

    /// <summary>The server draft this message replaces, deleted after a successful send or re-save.</summary>
    public MessageRef? DraftRef { get; init; }

    /// <summary>Stable Message-ID for this compose session so re-saved drafts and the sent copy can be matched.</summary>
    public string? MessageId { get; init; }

    public bool HasRecipients => To.Count + Cc.Count + Bcc.Count > 0;

    public long TotalAttachmentSize => Attachments.Sum(a => a.Size);
}
