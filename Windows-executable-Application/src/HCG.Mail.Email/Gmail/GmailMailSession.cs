using System.Net;
using System.Text.Json;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Mime;
using Microsoft.Extensions.Logging;
using MimeKit;
using CoreFlags = HCG.Mail.Core.Mail.MessageFlags;

namespace HCG.Mail.Email.Gmail;

/// <summary>
/// A Gmail / Google Workspace mailbox through the Gmail API v1 with the gmail.modify scope. Gmail labels are shown as
/// folders: Inbox, Drafts, Sent, Spam and Trash are system labels; "All Mail" (every message except Spam and Trash)
/// is the Archive, and archiving removes the Inbox label as Gmail does. Bodies are downloaded as raw MIME and go
/// through the same parser, body selection, sanitizer and attachment handling as IMAP mail; messages are sent and
/// drafts saved as MIME too. Writes are never retried automatically.
/// </summary>
/// <remarks>
/// gmail.modify allows moving mail to Trash but not deleting it permanently (Gmail empties Trash after 30 days), so
/// "delete" in Trash is explained instead of performed. Like the other providers, HCG Mail keeps no local message
/// store: each list load asks Gmail for the current state, and older pages load on demand.
/// </remarks>
public sealed class GmailMailSession : IMailSession
{
    /// <summary>The virtual "All Mail" folder: every message except Spam and Trash. Gmail's archive.</summary>
    public const string AllMail = "HCG_ALL_MAIL";

    private const int MessageCacheSize = 6;
    private const int MetadataConcurrency = 8;
    private const string MetadataQuery =
        "format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Message-ID" +
        "&fields=id,labelIds,snippet,internalDate,sizeEstimate,payload(mimeType,headers)";

    internal static readonly (string LabelId, FolderRole Role, string Name)[] SystemFolders =
    [
        ("INBOX", FolderRole.Inbox, "Inbox"), ("DRAFT", FolderRole.Drafts, "Drafts"), ("SENT", FolderRole.Sent, "Sent"),
        (AllMail, FolderRole.Archive, "All Mail"), ("SPAM", FolderRole.Junk, "Spam"), ("TRASH", FolderRole.Trash, "Trash"),
    ];

    private readonly GmailClient _gmail;
    private readonly IHtmlToTextConverter _htmlToText;
    private readonly ILogger _logger;
    private readonly IDeliveryLog? _deliveryLog;
    private readonly LinkedList<(string Id, MimeMessage Message)> _cache = new();
    private readonly Dictionary<string, FolderIds> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _draftIds = new(StringComparer.Ordinal); // message id → draft id
    private MailConnectionState _state = MailConnectionState.Disconnected;
    private bool _disposed;

    private sealed class FolderIds
    {
        public List<string> Ids { get; } = [];
        public string? NextPageToken { get; set; }
        public bool Complete { get; set; }
    }

    public GmailMailSession(MailAccount account, IGoogleAuth auth, IHtmlToTextConverter htmlToText, ILogger logger,
        IDeliveryLog? deliveryLog = null, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (!account.IsGoogle || string.IsNullOrEmpty(account.GoogleAccountId))
            throw new ArgumentException("Not a connected Google account.", nameof(account));
        Account = account;
        _gmail = new GmailClient(auth, account.GoogleAccountId, logger, handler, delay);
        _htmlToText = htmlToText;
        _logger = logger;
        _deliveryLog = deliveryLog;
    }

    public MailAccount Account { get; }

    public MailConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<MailConnectionState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        State = MailConnectionState.Connecting;
        await GetProfileAsync(cancellationToken).ConfigureAwait(false); // proves the token works and Gmail is enabled
        State = MailConnectionState.Connected;
    }

    /// <summary>The mailbox address as Gmail reports it.</summary>
    public Task<string?> GetProfileAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            using var profile = await _gmail.GetJsonAsync("/profile", ct).ConfigureAwait(false);
            return Str(profile.RootElement, "emailAddress");
        }, cancellationToken);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<MailFolder>>(async ct =>
        {
            using var labels = await _gmail.GetJsonAsync("/labels", ct).ConfigureAwait(false);
            var userLabels = labels.RootElement.TryGetProperty("labels", out var list)
                ? list.EnumerateArray()
                    .Where(l => Str(l, "type") == "user")
                    .Select(l => (Id: Str(l, "id")!, Name: Str(l, "name") ?? "(label)"))
                    .Where(l => l.Id is not null)
                    .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(300)
                    .ToList()
                : [];

            var folders = new List<MailFolder>();
            foreach (var (id, role, name) in SystemFolders)
            {
                var (total, unread) = id == AllMail ? (await AllMailTotalAsync(ct).ConfigureAwait(false), (int?)null) : await CountsAsync(id, ct).ConfigureAwait(false);
                folders.Add(new MailFolder(id, name, '/', role, true, 0, unread, total));
            }
            using var gate = new SemaphoreSlim(MetadataConcurrency);
            var counted = await Task.WhenAll(userLabels.Select(async label =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return (label, counts: await CountsAsync(label.Id, ct).ConfigureAwait(false));
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            foreach (var (label, (total, unread)) in counted)
            {
                // Nested labels are named "Parent/Child" in Gmail.
                var depth = Math.Min(label.Name.Count(c => c == '/'), 3);
                folders.Add(new MailFolder(label.Id, label.Name[(label.Name.LastIndexOf('/') + 1)..], '/', FolderRole.None, true, depth, unread, total));
            }
            return folders;
        }, cancellationToken);

    public Task<FolderPage> GetMessagesAsync(string folderFullName, int skip, int count, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            FolderIds list;
            lock (_lists)
            {
                if (skip == 0 || !_lists.TryGetValue(folderFullName, out list!))
                    _lists[folderFullName] = list = new FolderIds(); // a fresh load starts from the newest message
            }
            while (list.Ids.Count < skip + count && !list.Complete)
            {
                var page = list.Ids.Count == 0 && list.NextPageToken is null ? null : list.NextPageToken;
                using var json = await _gmail.GetJsonAsync(ListPath(folderFullName, null, Math.Clamp(skip + count - list.Ids.Count, 1, 500), page), ct).ConfigureAwait(false);
                if (json.RootElement.TryGetProperty("messages", out var messages))
                {
                    // A message that moved between pages while paging must not appear twice.
                    foreach (var id in messages.EnumerateArray().Select(m => Str(m, "id")))
                    {
                        if (id is not null && !list.Ids.Contains(id))
                            list.Ids.Add(id);
                    }
                }
                list.NextPageToken = Str(json.RootElement, "nextPageToken");
                list.Complete = list.NextPageToken is null;
            }

            var window = list.Ids.Skip(skip).Take(count).ToList();
            var summaries = await MetadataAsync(window, folderFullName, ct).ConfigureAwait(false);
            var (total, unread) = folderFullName == AllMail
                ? (await AllMailTotalAsync(ct).ConfigureAwait(false), (int?)0)
                : await CountsAsync(folderFullName, ct).ConfigureAwait(false);
            var hasOlder = !list.Complete || skip + window.Count < list.Ids.Count;
            return new FolderPage(folderFullName, summaries, total ?? list.Ids.Count, unread ?? 0, 0, hasOlder);
        }, cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> SearchAsync(string folderFullName, string query, int maxResults, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<MessageSummary>>(async ct =>
        {
            // Gmail's own search syntax over the whole label on Google's side.
            using var json = await _gmail.GetJsonAsync(ListPath(folderFullName, query.Trim(), Math.Clamp(maxResults, 1, 250), null), ct).ConfigureAwait(false);
            var ids = json.RootElement.TryGetProperty("messages", out var messages)
                ? messages.EnumerateArray().Select(m => Str(m, "id")).Where(i => i is not null).Select(i => i!).ToList()
                : [];
            return await MetadataAsync(ids, folderFullName, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var (mime, labels) = await GetRawAsync(IdOf(message), ct).ConfigureAwait(false);
            return MimeConverter.ToContent(mime, message, FlagsOf(labels), _htmlToText);
        }, cancellationToken);

    public Task SetFlagsAsync(IReadOnlyList<MessageRef> messages, CoreFlags flags, bool add, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var addLabels = new List<string>();
            var removeLabels = new List<string>();
            if (flags.HasFlag(CoreFlags.Seen))
                (add ? removeLabels : addLabels).Add("UNREAD"); // Gmail marks unread messages, not read ones
            if (flags.HasFlag(CoreFlags.Flagged))
                (add ? addLabels : removeLabels).Add("STARRED");
            if (addLabels.Count == 0 && removeLabels.Count == 0 || messages.Count == 0)
                return true; // \\Answered and \\Draft have no Gmail label HCG Mail writes
            // Setting labels to a value is idempotent, so a rate-limited request may be retried.
            await _gmail.WriteAsync(HttpMethod.Post, "/messages/batchModify",
                new { ids = messages.Select(IdOf).ToArray(), addLabelIds = addLabels, removeLabelIds = removeLabels }, ct, idempotent: true).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task DeleteAsync(MessageRef message, bool permanent, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var id = IdOf(message);
            if (message.FolderFullName == "DRAFT")
            {
                // A draft is deleted as a draft (gmail.modify allows this); it is not moved to Trash.
                await DeleteDraftAsync(id, ct).ConfigureAwait(false);
            }
            else if (permanent || message.FolderFullName == "TRASH")
            {
                throw new MailOperationException(MailErrorKind.NotSupported,
                    "Gmail deletes messages in Trash automatically after 30 days. HCG Mail only has permission to move Gmail messages to Trash, " +
                    "not to delete them permanently; to delete one now, use Gmail on the web.");
            }
            else
            {
                await _gmail.WriteAsync(HttpMethod.Post, $"/messages/{Esc(id)}/trash", null, ct).ConfigureAwait(false);
            }
            Forget(id);
            return true;
        }, cancellationToken);

    public Task MoveAsync(MessageRef message, string targetFolderFullName, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var id = IdOf(message);
            var source = message.FolderFullName;
            if (targetFolderFullName == source)
                return true;
            if (targetFolderFullName == "TRASH")
            {
                await _gmail.WriteAsync(HttpMethod.Post, $"/messages/{Esc(id)}/trash", null, ct).ConfigureAwait(false);
            }
            else
            {
                if (source == "TRASH")
                    await _gmail.WriteAsync(HttpMethod.Post, $"/messages/{Esc(id)}/untrash", null, ct).ConfigureAwait(false);
                var addLabels = targetFolderFullName is AllMail ? Array.Empty<string>() : [targetFolderFullName];
                // Archiving (to All Mail) or moving away from a label removes that label, as Gmail's "Move to" does.
                var removeLabels = source is AllMail or "TRASH" or "DRAFT" or "SENT" ? Array.Empty<string>() : [source];
                if (addLabels.Length > 0 || removeLabels.Length > 0)
                    await _gmail.WriteAsync(HttpMethod.Post, $"/messages/{Esc(id)}/modify", new { addLabelIds = addLabels, removeLabelIds = removeLabels }, ct, idempotent: true).ConfigureAwait(false);
            }
            Forget(id);
            return true;
        }, cancellationToken);

    public async Task SaveAttachmentAsync(MessageRef message, int attachmentIndex, Stream destination, CancellationToken cancellationToken = default)
    {
        var (mime, _) = await RunAsync(ct => GetRawAsync(IdOf(message), ct), cancellationToken).ConfigureAwait(false);
        var attachments = MimeConverter.GetAttachments(mime);
        if (attachmentIndex < 0 || attachmentIndex >= attachments.Count)
            throw new MailOperationException(MailErrorKind.MessageNotFound, "The attachment no longer exists in this message.");
        await MimeConverter.WriteAttachmentAsync(attachments[attachmentIndex], destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetAttachmentBytesAsync(MessageRef message, int attachmentIndex, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await SaveAttachmentAsync(message, attachmentIndex, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task<SendResult> SendAsync(OutgoingMessage message, bool saveCopyToSent, CancellationToken cancellationToken = default)
    {
        if (!message.HasRecipients)
            throw new MailOperationException(MailErrorKind.ServerRejected, "Add at least one recipient.");
        var mime = MimeConverter.ToMimeMessage(message, Account);
        var record = new DeliveryRecord
        {
            Timestamp = DateTimeOffset.Now,
            AccountId = Account.Id,
            AccountAddress = Account.EmailAddress,
            SmtpServer = "gmail.googleapis.com (Gmail API)",
            State = DeliveryState.NotSent,
            MessageId = mime.MessageId,
            RecipientCount = message.To.Count + message.Cc.Count + message.Bcc.Count,
            Priority = message.Priority,
        };

        try
        {
            // Gmail reads Bcc from the MIME, removes it from delivered copies and files the message in Sent. One attempt.
            await RunAsync(async ct =>
            {
                using var _ = await _gmail.WriteAsync(HttpMethod.Post, "/messages/send", new { raw = Raw(mime) }, ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (MailOperationException ex)
        {
            Record(record with
            {
                State = ex.Kind switch
                {
                    MailErrorKind.Throttled => DeliveryState.Deferred,
                    MailErrorKind.ServerRejected or MailErrorKind.Authentication => DeliveryState.Rejected,
                    _ when ex.OutcomeUnknown => DeliveryState.Submitted,
                    _ => DeliveryState.NotSent,
                },
                ErrorMessage = ex.UserMessage,
            });
            throw;
        }

        var accepted = record with { State = DeliveryState.Accepted, SmtpStatusCode = 200, ServerReply = "200 Accepted by the Gmail API" };
        Record(accepted);

        var warnings = new List<string>();
        if (!saveCopyToSent)
            warnings.Add("Gmail keeps a copy of sent mail in Sent for this account; HCG Mail cannot turn that off.");
        if (message.DraftRef is { } draft)
        {
            try
            {
                await RunAsync(async ct =>
                {
                    await DeleteDraftAsync(IdOf(draft), ct).ConfigureAwait(false);
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove sent draft for account {AccountId}", Account.Id);
                warnings.Add("The draft could not be removed from Drafts.");
            }
        }
        return new SendResult(true, warnings.Count == 0 ? null : string.Join(" ", warnings), accepted);
    }

    public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage message, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var mime = MimeConverter.ToMimeMessage(message with { MessageId = message.MessageId ?? MimeConverter.NewMessageId(Account) }, Account);
            var body = new { message = new { raw = Raw(mime) } };
            var previousDraftId = message.DraftRef is { } previous ? await FindDraftIdAsync(IdOf(previous), ct).ConfigureAwait(false) : null;
            using var saved = previousDraftId is null
                ? await _gmail.WriteAsync(HttpMethod.Post, "/drafts", body, ct).ConfigureAwait(false)
                : await _gmail.WriteAsync(HttpMethod.Put, $"/drafts/{Esc(previousDraftId)}", body, ct).ConfigureAwait(false);
            var draftId = saved is null ? null : Str(saved.RootElement, "id");
            var messageId = saved is not null && saved.RootElement.TryGetProperty("message", out var m) ? Str(m, "id") : null;
            if (draftId is null || messageId is null)
                throw new MailOperationException(MailErrorKind.Protocol, "Gmail did not return the saved draft.");
            lock (_draftIds)
                _draftIds[messageId] = draftId;
            if (message.DraftRef is { } old)
                Forget(IdOf(old));
            return new DraftSaveResult(new MessageRef("DRAFT", 0, 0, messageId));
        }, cancellationToken);

    public Task<OutgoingMessage> LoadDraftAsync(MessageRef draft, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var (mime, _) = await GetRawAsync(IdOf(draft), ct).ConfigureAwait(false);
            return await MimeConverter.ToOutgoingAsync(mime, draft, _htmlToText, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task DisconnectAsync()
    {
        lock (_cache)
            _cache.Clear();
        lock (_lists)
            _lists.Clear();
        State = MailConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_cache)
            _cache.Clear();
        return ValueTask.CompletedTask;
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new MailOperationException(MailErrorKind.Network, "This connection has been closed. Reconnect the account and try again.");
        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            if (State is MailConnectionState.Failed)
                State = MailConnectionState.Connected;
            return result;
        }
        catch (MailOperationException ex) when (ex.Kind is MailErrorKind.ReconnectRequired or MailErrorKind.Network)
        {
            State = MailConnectionState.Failed;
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new MailOperationException(MailErrorKind.Cancelled, "Cancelled.");
        }
    }

    internal static string ListPath(string folder, string? query, int max, string? pageToken)
    {
        var path = $"/messages?maxResults={max}";
        if (folder != AllMail)
            path += "&labelIds=" + Esc(folder);
        if (folder is "SPAM" or "TRASH")
            path += "&includeSpamTrash=true";
        if (!string.IsNullOrEmpty(query))
            path += "&q=" + Esc(query);
        if (pageToken is not null)
            path += "&pageToken=" + Esc(pageToken);
        return path;
    }

    private async Task<(int? Total, int? Unread)> CountsAsync(string labelId, CancellationToken ct)
    {
        try
        {
            using var label = await _gmail.GetJsonAsync($"/labels/{Esc(labelId)}?fields=messagesTotal,messagesUnread", ct).ConfigureAwait(false);
            return (Int(label.RootElement, "messagesTotal"), Int(label.RootElement, "messagesUnread"));
        }
        catch (MailOperationException ex) when (ex.Kind == MailErrorKind.MessageNotFound)
        {
            return (null, null); // label deleted meanwhile
        }
    }

    private async Task<int?> AllMailTotalAsync(CancellationToken ct)
    {
        using var profile = await _gmail.GetJsonAsync("/profile", ct).ConfigureAwait(false);
        return Int(profile.RootElement, "messagesTotal");
    }

    private async Task<IReadOnlyList<MessageSummary>> MetadataAsync(IReadOnlyList<string> ids, string folder, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MetadataConcurrency);
        var results = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var json = await _gmail.GetJsonAsync($"/messages/{Esc(id)}?{MetadataQuery}", ct).ConfigureAwait(false);
                return ToSummary(json.RootElement, folder);
            }
            catch (MailOperationException ex) when (ex.Kind == MailErrorKind.MessageNotFound)
            {
                return null; // deleted between listing and reading
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).OrderByDescending(r => r.Date).ToList();
    }

    internal static MessageSummary ToSummary(JsonElement m, string folder)
    {
        var headers = m.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Select(x => (Name: Str(x, "name") ?? "", Value: Str(x, "value") ?? "")).ToList()
            : [];
        string? Header(string name) => headers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
        var labels = Labels(m);
        var mimeType = Str(payload, "mimeType") ?? "";
        var date = long.TryParse(Str(m, "internalDate"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;
        return new MessageSummary
        {
            Ref = new MessageRef(folder, 0, 0, Str(m, "id")),
            From = Addresses(Header("From")).FirstOrDefault(),
            To = Addresses(Header("To")),
            Subject = Header("Subject") is { } subject ? MimeKit.Utils.Rfc2047.DecodeText(System.Text.Encoding.UTF8.GetBytes(subject)) : "",
            Date = date,
            Preview = PreviewText.Clean(WebUtility.HtmlDecode(Str(m, "snippet"))), // Gmail's snippet is HTML-escaped
            Flags = FlagsOf(labels),
            // A multipart/mixed message carries attachments (metadata does not list the parts themselves).
            HasAttachments = mimeType.Equals("multipart/mixed", StringComparison.OrdinalIgnoreCase),
            Size = Int(m, "sizeEstimate") ?? 0,
            MessageId = Header("Message-ID")?.Trim().Trim('<', '>'),
        };
    }

    private static IReadOnlyList<EmailAddress> Addresses(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !InternetAddressList.TryParse(header, out var list))
            return [];
        return list.Mailboxes.Select(MimeConverter.ToAddress).ToList();
    }

    private static IReadOnlyList<string> Labels(JsonElement m) =>
        m.TryGetProperty("labelIds", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];

    internal static CoreFlags FlagsOf(IReadOnlyList<string> labels)
    {
        var flags = CoreFlags.None;
        if (!labels.Contains("UNREAD"))
            flags |= CoreFlags.Seen;
        if (labels.Contains("STARRED"))
            flags |= CoreFlags.Flagged;
        if (labels.Contains("DRAFT"))
            flags |= CoreFlags.Draft;
        return flags;
    }

    private async Task<(MimeMessage Mime, IReadOnlyList<string> Labels)> GetRawAsync(string id, CancellationToken ct)
    {
        // Labels change (read, starred), so they are always fetched; the MIME body is cached.
        using var json = await _gmail.GetJsonAsync($"/messages/{Esc(id)}?format=raw", ct).ConfigureAwait(false);
        var labels = Labels(json.RootElement);
        lock (_cache)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == id)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return (node.Value.Message, labels);
                }
            }
        }
        var raw = Str(json.RootElement, "raw") ?? throw new MailOperationException(MailErrorKind.Protocol, "Gmail returned a message without its content.");
        var message = await MimeMessage.LoadAsync(new MemoryStream(FromBase64Url(raw)), ct).ConfigureAwait(false);
        lock (_cache)
        {
            _cache.AddFirst((id, message));
            while (_cache.Count > MessageCacheSize)
                _cache.RemoveLast();
        }
        return (message, labels);
    }

    private async Task<string?> FindDraftIdAsync(string messageId, CancellationToken ct)
    {
        lock (_draftIds)
        {
            if (_draftIds.TryGetValue(messageId, out var known))
                return known;
        }
        string? page = null;
        for (var i = 0; i < 10; i++)
        {
            using var json = await _gmail.GetJsonAsync("/drafts?maxResults=500" + (page is null ? "" : "&pageToken=" + Esc(page)), ct).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("drafts", out var drafts))
            {
                foreach (var d in drafts.EnumerateArray())
                {
                    if (d.TryGetProperty("message", out var dm) && Str(dm, "id") is { } mid && Str(d, "id") is { } did)
                    {
                        lock (_draftIds)
                            _draftIds[mid] = did;
                    }
                }
            }
            lock (_draftIds)
            {
                if (_draftIds.TryGetValue(messageId, out var found))
                    return found;
            }
            page = Str(json.RootElement, "nextPageToken");
            if (page is null)
                break;
        }
        return null;
    }

    private async Task DeleteDraftAsync(string messageId, CancellationToken ct)
    {
        var draftId = await FindDraftIdAsync(messageId, ct).ConfigureAwait(false);
        if (draftId is null)
            return; // already sent or deleted elsewhere
        try
        {
            await _gmail.WriteAsync(HttpMethod.Delete, $"/drafts/{Esc(draftId)}", null, ct).ConfigureAwait(false);
        }
        catch (MailOperationException ex) when (ex.Kind == MailErrorKind.MessageNotFound)
        {
        }
        lock (_draftIds)
            _draftIds.Remove(messageId);
    }

    private void Forget(string id)
    {
        lock (_cache)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == id)
                {
                    _cache.Remove(node);
                    break;
                }
            }
        }
        lock (_lists)
        {
            foreach (var list in _lists.Values)
                list.Ids.Remove(id);
        }
    }

    private void Record(DeliveryRecord record)
    {
        try
        {
            _deliveryLog?.Add(record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record delivery history for account {AccountId}", Account.Id);
        }
    }

    private static string IdOf(MessageRef message) =>
        message.ServerId ?? throw new MailOperationException(MailErrorKind.MessageNotFound, "This message reference does not belong to a Google account.");

    internal static string Raw(MimeMessage mime)
    {
        using var stream = new MemoryStream();
        mime.WriteTo(FormatOptions.Default, stream);
        return Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string Esc(string id) => Uri.EscapeDataString(id);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
