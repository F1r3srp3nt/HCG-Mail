using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;

namespace HCG.Mail.TestSupport;

/// <summary>An IMAP-like session whose server can be switched off, and that counts how often it was used.</summary>
internal sealed class SwitchableSession(MailAccount account) : IMailSession, IStaleConnectionAware
{
    public static readonly MessageRef Msg = new("INBOX", 7, 1);

    public MailErrorKind? FailWith { get; set; }
    public int Calls { get; private set; }
    public int StaleMarks { get; private set; }
    public List<OutgoingMessage> Sent { get; } = [];
    public List<OutgoingMessage> Drafts { get; } = [];
    public bool OutcomeUnknownOnSend { get; set; }

    public MailAccount Account { get; } = account;
    public MailConnectionState State => MailConnectionState.Connected;
    public event EventHandler<MailConnectionState>? StateChanged;

    private Task<T> Run<T>(Func<T> result)
    {
        Calls++;
        if (FailWith is { } kind)
            throw new MailOperationException(kind, $"Simulated {kind} failure.");
        StateChanged?.Invoke(this, MailConnectionState.Connected);
        return Task.FromResult(result());
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Run(() => true);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<MailFolder>>(() => [new MailFolder("INBOX", "INBOX", '/', FolderRole.Inbox, true, 0, 1, 1)]);

    public Task<FolderPage> GetMessagesAsync(string folderFullName, int skip, int count, CancellationToken cancellationToken = default) =>
        Run(() => new FolderPage(folderFullName, [new MessageSummary { Ref = Msg, Subject = "Lesson plan", Preview = "Room 204", From = new EmailAddress("Teacher", "teacher@example.no") }], 1, 1, 1, false));

    public Task<IReadOnlyList<MessageSummary>> SearchAsync(string folderFullName, string query, int maxResults, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<MessageSummary>>(() => []);

    public Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken cancellationToken = default) =>
        Run(() => new MessageContent { Ref = message, Subject = "Lesson plan", TextBody = "Bring the lab notebook.", InlineResources = [new InlineResource("logo", "image/png", [1, 2, 3])] });

    public Task SetFlagsAsync(IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add, CancellationToken cancellationToken = default) => Run(() => true);
    public Task DeleteAsync(MessageRef message, bool permanent, CancellationToken cancellationToken = default) => Run(() => true);
    public Task MoveAsync(MessageRef message, string targetFolderFullName, CancellationToken cancellationToken = default) => Run(() => true);
    public Task SaveAttachmentAsync(MessageRef message, int attachmentIndex, Stream destination, CancellationToken cancellationToken = default) => Run(() => true);
    public Task<byte[]> GetAttachmentBytesAsync(MessageRef message, int attachmentIndex, CancellationToken cancellationToken = default) => Run(() => new byte[] { 1 });

    public Task<SendResult> SendAsync(OutgoingMessage message, bool saveCopyToSent, CancellationToken cancellationToken = default)
    {
        if (OutcomeUnknownOnSend)
        {
            Calls++;
            throw new MailOperationException(MailErrorKind.Network, "Connection lost during DATA.") { OutcomeUnknown = true };
        }
        return Run(() =>
        {
            Sent.Add(message);
            return new SendResult(saveCopyToSent, null);
        });
    }

    public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage message, CancellationToken cancellationToken = default) => Run(() =>
    {
        Drafts.Add(message);
        return new DraftSaveResult(new MessageRef("Drafts", (uint)Drafts.Count, 1));
    });

    public Task<OutgoingMessage> LoadDraftAsync(MessageRef draft, CancellationToken cancellationToken = default) => Run(() => new OutgoingMessage());
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void MarkConnectionStale() => StaleMarks++;
}

