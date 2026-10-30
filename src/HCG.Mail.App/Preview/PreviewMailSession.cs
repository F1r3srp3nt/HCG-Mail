using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;

namespace HCG.Mail.App.Preview;

/// <summary>
/// DEVELOPMENT PREVIEW ONLY. An in-memory mailbox with clearly labelled sample messages so the
/// interface can be reviewed without a mail account. It is only used when HCG Mail is started
/// with <c>--preview</c>; it never touches the network and nothing is ever sent.
/// </summary>
public sealed class PreviewMailSession : IMailSession
{
    public static readonly MailAccount SampleAccount = new()
    {
        Id = "preview-account",
        DisplayName = "Preview User",
        EmailAddress = "preview.user@example.com",
        Username = "preview.user@example.com",
        Imap = new ServerEndpoint("imap.example.com", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.example.com", 465, MailSecurityMode.SslTls),
    };

    private const uint UidValidity = 1;
    private readonly List<MailFolder> _folders;
    private readonly Dictionary<string, List<Entry>> _messages = new();
    private uint _nextUid = 100;

    public PreviewMailSession(MailAccount account)
    {
        Account = account;
        _folders =
        [
            Folder("INBOX", FolderRole.Inbox),
            Folder("Drafts", FolderRole.Drafts),
            Folder("Sent", FolderRole.Sent),
            Folder("Archive", FolderRole.Archive),
            Folder("Junk", FolderRole.Junk),
            Folder("Trash", FolderRole.Trash),
            new MailFolder("Projects", "Projects", '/', FolderRole.None, true, 0, 0, 0),
            new MailFolder("Projects/Board 2026", "Board 2026", '/', FolderRole.None, true, 1, 0, 0),
        ];
        foreach (var folder in _folders)
            _messages[folder.FullName] = [];
        Seed();
    }

    public MailAccount Account { get; }
    public MailConnectionState State { get; private set; }
    public event EventHandler<MailConnectionState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        State = MailConnectionState.Connecting;
        StateChanged?.Invoke(this, State);
        await Task.Delay(250, cancellationToken);
        State = MailConnectionState.Connected;
        StateChanged?.Invoke(this, State);
    }

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailFolder>>(_folders
            .Select(f => f with
            {
                UnreadCount = _messages[f.FullName].Count(m => !m.Flags.HasFlag(MessageFlags.Seen)),
                TotalCount = _messages[f.FullName].Count,
            })
            .ToList());

    public async Task<FolderPage> GetMessagesAsync(string folderFullName, int skip, int count, CancellationToken cancellationToken = default)
    {
        await Task.Delay(150, cancellationToken);
        var list = Messages(folderFullName).OrderByDescending(m => m.Content.Date).ToList();
        var page = list.Skip(skip).Take(count).Select(ToSummary).ToList();
        return new FolderPage(folderFullName, page, list.Count, list.Count(m => !m.Flags.HasFlag(MessageFlags.Seen)), UidValidity, skip + count < list.Count);
    }

    public async Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken cancellationToken = default)
    {
        await Task.Delay(80, cancellationToken);
        var entry = Find(message);
        return entry.Content with { Flags = entry.Flags };
    }

    public Task SetFlagsAsync(IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add, CancellationToken cancellationToken = default)
    {
        foreach (var m in messages)
        {
            var entry = Find(m);
            entry.Flags = add ? entry.Flags | flags : entry.Flags & ~flags;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(MessageRef message, bool permanent, CancellationToken cancellationToken = default)
    {
        var entry = Find(message);
        Messages(message.FolderFullName).Remove(entry);
        if (!permanent && message.FolderFullName != "Trash")
            Add("Trash", entry.Content, entry.Flags, entry.Attachments);
        return Task.CompletedTask;
    }

    public Task MoveAsync(MessageRef message, string targetFolderFullName, CancellationToken cancellationToken = default)
    {
        var entry = Find(message);
        Messages(message.FolderFullName).Remove(entry);
        Add(targetFolderFullName, entry.Content, entry.Flags, entry.Attachments);
        return Task.CompletedTask;
    }

    public async Task SaveAttachmentAsync(MessageRef message, int attachmentIndex, Stream destination, CancellationToken cancellationToken = default)
    {
        var data = await GetAttachmentBytesAsync(message, attachmentIndex, cancellationToken);
        await destination.WriteAsync(data, cancellationToken);
    }

    public Task<byte[]> GetAttachmentBytesAsync(MessageRef message, int attachmentIndex, CancellationToken cancellationToken = default)
    {
        var entry = Find(message);
        if (attachmentIndex < 0 || attachmentIndex >= entry.Attachments.Count)
            throw new MailOperationException(MailErrorKind.MessageNotFound, "The attachment could not be found in the message.");
        return Task.FromResult(entry.Attachments[attachmentIndex]);
    }

    public async Task<SendResult> SendAsync(OutgoingMessage message, bool saveCopyToSent, CancellationToken cancellationToken = default)
    {
        await Task.Delay(400, cancellationToken);
        var content = ToContent(message, "Sent");
        Add("Sent", content, MessageFlags.Seen, message.Attachments.Select(ReadAll).ToList());
        if (message.DraftRef is { } draft)
            Messages(draft.FolderFullName).RemoveAll(m => m.Uid == draft.Uid);
        return new SendResult(true, "Development preview mode: nothing was sent. The message was only added to the sample Sent folder.");
    }

    public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        if (message.DraftRef is { } old)
            Messages(old.FolderFullName).RemoveAll(m => m.Uid == old.Uid);
        var entry = Add("Drafts", ToContent(message, "Drafts"), MessageFlags.Seen | MessageFlags.Draft, message.Attachments.Select(ReadAll).ToList());
        return Task.FromResult(new DraftSaveResult(entry.Content.Ref));
    }

    public Task<OutgoingMessage> LoadDraftAsync(MessageRef draft, CancellationToken cancellationToken = default)
    {
        var entry = Find(draft);
        var c = entry.Content;
        return Task.FromResult(new OutgoingMessage
        {
            Kind = ComposeKind.EditDraft,
            To = c.To,
            Cc = c.Cc,
            Bcc = c.Bcc,
            Subject = c.Subject,
            TextBody = c.TextBody,
            DraftRef = draft,
            MessageId = c.MessageId,
            Attachments = c.Attachments.Select((a, i) => OutgoingAttachment.FromBytes(a.SafeFileName, a.ContentType, entry.Attachments[i])).ToList(),
        });
    }

    public Task DisconnectAsync()
    {
        State = MailConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- sample data ----------------------------------------------------------------------------

    private void Seed()
    {
        var now = DateTimeOffset.Now;
        var me = new EmailAddress(Account.DisplayName, Account.EmailAddress);

        Sample("INBOX", now.AddMinutes(-12), new("Ingrid Sample", "ingrid.sample@example.org"), [me], "[Sample] Q3 portfolio review — agenda",
            "Hi,\n\nThis is sample content for the HCG Mail development preview.\n\nAgenda for Thursday:\n1. Portfolio performance\n2. Risk dashboard update\n3. Next steps\n\nBest regards,\nIngrid",
            flags: MessageFlags.None,
            attachments: [("Q3-agenda-sample.txt", "text/plain", Encoding.UTF8.GetBytes("Sample attachment for the HCG Mail preview.\n"))]);

        Sample("INBOX", now.AddMinutes(-47), new("Newsletter (sample)", "news@example.com"), [me], "[Sample] Markets weekly — HTML message with remote images",
            "",
            html: """
                  <html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1a1f24">
                  <h2 style="color:#0b5c7a">Markets weekly (sample)</h2>
                  <p>This sample HTML message demonstrates safe rendering. The image below is hosted remotely and is <b>blocked by default</b>.</p>
                  <p><img src="https://images.example.com/tracking-pixel.png" width="400" height="120" alt="Remote banner"></p>
                  <table border="1" cellpadding="6" style="border-collapse:collapse"><tr><th>Index</th><th>Change</th></tr>
                  <tr><td>Sample A</td><td style="color:#15803d">+1.2%</td></tr><tr><td>Sample B</td><td style="color:#b91c1c">-0.4%</td></tr></table>
                  <p><a href="https://www.example.com/report">Read the full report</a> — links always ask before opening.</p>
                  <script>alert('scripts never run')</script>
                  </body></html>
                  """,
            flags: MessageFlags.None);

        Sample("INBOX", now.AddHours(-3), new("Operations Desk (sample)", "ops@example.org"), [me, new("Kari Example", "kari@example.org")],
            "[Sample] Server maintenance window confirmed",
            "Hello team,\n\nThe maintenance window is confirmed for Saturday 02:00–04:00 CET.\n\nThis is preview sample data.\n\n— Operations",
            flags: MessageFlags.Seen | MessageFlags.Flagged, cc: [new("Lars Example", "lars@example.org")]);

        Sample("INBOX", now.AddDays(-1).AddHours(-2), new("Kari Example", "kari@example.org"), [me], "Re: [Sample] Board pack draft",
            "Thanks — I've added my comments in section 4.\n\nOn Monday, Preview User wrote:\n> Draft attached for review.\n",
            flags: MessageFlags.Seen | MessageFlags.Answered);

        Sample("INBOX", now.AddDays(-2), new("Travel (sample)", "bookings@example.com"), [me], "[Sample] Your itinerary: Oslo → London",
            "Booking reference: SAMPLE-0000\nDeparture: 08:15\n\nThis is sample data for the HCG Mail preview.",
            flags: MessageFlags.Seen,
            attachments: [("itinerary-sample.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n% sample placeholder\n"))]);

        Sample("INBOX", now.AddDays(-5), new("Lars Example", "lars@example.org"), [me], "[Sample] Lunch next week?",
            "Are you free Tuesday or Wednesday?\n\n— Lars", flags: MessageFlags.Seen);

        Sample("INBOX", now.AddDays(-40), new("IT Service Desk (sample)", "servicedesk@example.com"), [me], "[Sample] Attachment safety example",
            "This sample message carries an attachment whose type can run code. HCG Mail warns before saving it and never opens it.",
            flags: MessageFlags.Seen,
            attachments: [("setup-sample.exe", "application/octet-stream", [0x4D, 0x5A, 0x00, 0x00])]);

        Sample("Sent", now.AddDays(-1).AddHours(-5), me, [new("Kari Example", "kari@example.org")], "[Sample] Board pack draft",
            "Draft attached for review.\n\n— Preview User", flags: MessageFlags.Seen);

        Sample("Junk", now.AddDays(-3), new("Prize Center", "winner@example.net"), [me], "[Sample] You have won!!!",
            "Sample junk message.", flags: MessageFlags.None);
    }

    private void Sample(string folder, DateTimeOffset date, EmailAddress from, IReadOnlyList<EmailAddress> to, string subject, string text,
        MessageFlags flags, string? html = null, IReadOnlyList<EmailAddress>? cc = null, IReadOnlyList<(string Name, string Type, byte[] Data)>? attachments = null)
    {
        var atts = attachments ?? [];
        var content = new MessageContent
        {
            Ref = new MessageRef(folder, 0, UidValidity),
            From = from,
            To = to,
            Cc = cc ?? [],
            Subject = subject,
            Date = date,
            MessageId = $"{Guid.NewGuid():N}@example.com",
            TextBody = string.IsNullOrEmpty(text) && html is not null ? "This sample message is HTML only." : text,
            HtmlBody = html,
            Attachments = atts.Select((a, i) => new AttachmentInfo(i, a.Name, AttachmentFileNames.Sanitize(a.Name), a.Type, a.Data.Length,
                AttachmentFileNames.IsPotentiallyDangerous(a.Name))).ToList(),
        };
        Add(folder, content, flags, atts.Select(a => a.Data).ToList());
    }

    private Entry Add(string folder, MessageContent content, MessageFlags flags, List<byte[]> attachments)
    {
        var uid = _nextUid++;
        var entry = new Entry(uid, content with { Ref = new MessageRef(folder, uid, UidValidity) }, attachments) { Flags = flags };
        Messages(folder).Add(entry);
        return entry;
    }

    private MessageContent ToContent(OutgoingMessage message, string folder) => new()
    {
        Ref = new MessageRef(folder, 0, UidValidity),
        From = new EmailAddress(Account.DisplayName, Account.EmailAddress),
        To = message.To,
        Cc = message.Cc,
        Bcc = message.Bcc,
        Subject = message.Subject,
        Date = DateTimeOffset.Now,
        MessageId = message.MessageId,
        TextBody = message.TextBody,
        Attachments = message.Attachments.Select((a, i) => new AttachmentInfo(i, a.FileName, a.FileName, a.ContentType ?? "application/octet-stream", a.Size,
            AttachmentFileNames.IsPotentiallyDangerous(a.FileName))).ToList(),
    };

    private static byte[] ReadAll(OutgoingAttachment attachment)
    {
        using var stream = attachment.OpenRead();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private List<Entry> Messages(string folder) =>
        _messages.TryGetValue(folder, out var list) ? list : throw new MailOperationException(MailErrorKind.FolderNotFound, "Folder not found.");

    private Entry Find(MessageRef message) =>
        Messages(message.FolderFullName).FirstOrDefault(m => m.Uid == message.Uid)
        ?? throw new MailOperationException(MailErrorKind.MessageNotFound, "The message no longer exists.");

    private static MessageSummary ToSummary(Entry e) => new()
    {
        Ref = e.Content.Ref,
        From = e.Content.From,
        To = e.Content.To,
        Subject = e.Content.Subject,
        Date = e.Content.Date,
        Preview = Preview(e.Content),
        Flags = e.Flags,
        HasAttachments = e.Content.Attachments.Count > 0,
        MessageId = e.Content.MessageId,
    };

    private static string Preview(MessageContent c)
    {
        var text = string.Join(' ', c.TextBody.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    private static MailFolder Folder(string name, FolderRole role) => new(name, name, '/', role, true, 0, 0, 0);

    private sealed class Entry(uint uid, MessageContent content, List<byte[]> attachments)
    {
        public uint Uid { get; } = uid;
        public MessageContent Content { get; } = content;
        public List<byte[]> Attachments { get; } = attachments;
        public MessageFlags Flags { get; set; }
    }
}

public sealed class PreviewMailSessionFactory : IMailSessionFactory
{
    public IMailSession Create(MailAccount account, string password) => new PreviewMailSession(account);
}

/// <summary>Connection tester used in preview mode: it does not touch the network.</summary>
public sealed class PreviewConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestReport> RunAsync(MailAccount account, string password, IProgress<ConnectionTestStep>? progress = null, CancellationToken cancellationToken = default)
    {
        var steps = new List<ConnectionTestStep>();
        foreach (var kind in Enum.GetValues<ConnectionTestStepKind>())
        {
            await Task.Delay(120, cancellationToken);
            var step = new ConnectionTestStep(kind, ConnectionTestStatus.Skipped, "Development preview: no network connection was made.");
            steps.Add(step);
            progress?.Report(step);
        }
        return new ConnectionTestReport(steps);
    }
}
