using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.Core.Services;

public enum MailConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

public sealed record SendResult(bool SavedToSent, string? Warning);

public sealed record DraftSaveResult(MessageRef? DraftRef);

/// <summary>
/// One account's live connection to its mail servers. All operations hit the real server;
/// implementations serialize IMAP commands internally and reconnect after a dropped
/// connection. Failures surface as <see cref="Errors.MailOperationException"/>.
/// </summary>
public interface IMailSession : IAsyncDisposable
{
    MailAccount Account { get; }

    MailConnectionState State { get; }

    event EventHandler<MailConnectionState>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the newest <paramref name="count"/> messages, skipping the newest <paramref name="skip"/> (for "load older").</summary>
    Task<FolderPage> GetMessagesAsync(string folderFullName, int skip, int count, CancellationToken cancellationToken = default);

    Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken cancellationToken = default);

    Task SetFlagsAsync(IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add, CancellationToken cancellationToken = default);

    /// <summary>Moves the message to Trash, or deletes it permanently when it is already in Trash or <paramref name="permanent"/> is set.</summary>
    Task DeleteAsync(MessageRef message, bool permanent, CancellationToken cancellationToken = default);

    Task MoveAsync(MessageRef message, string targetFolderFullName, CancellationToken cancellationToken = default);

    Task SaveAttachmentAsync(MessageRef message, int attachmentIndex, Stream destination, CancellationToken cancellationToken = default);

    Task<byte[]> GetAttachmentBytesAsync(MessageRef message, int attachmentIndex, CancellationToken cancellationToken = default);

    /// <summary>Sends via SMTP, then stores a copy in Sent when requested and the server does not already do so.</summary>
    Task<SendResult> SendAsync(OutgoingMessage message, bool saveCopyToSent, CancellationToken cancellationToken = default);

    Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage message, CancellationToken cancellationToken = default);

    Task<OutgoingMessage> LoadDraftAsync(MessageRef draft, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}

public interface IMailSessionFactory
{
    IMailSession Create(MailAccount account, string password);
}
