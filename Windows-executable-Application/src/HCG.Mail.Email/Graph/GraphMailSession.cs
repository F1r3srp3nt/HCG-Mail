using System.Net.Http.Headers;
using System.Text;
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

namespace HCG.Mail.Email.Graph;

/// <summary>
/// A Microsoft (Outlook.com / Hotmail / Microsoft 365) mailbox through Microsoft Graph v1.0 with delegated
/// permissions. Message bodies are downloaded as MIME (<c>/$value</c>) and go through the same parser, body
/// selection, sanitizer and attachment handling as IMAP mail; messages are sent and drafts saved as MIME too.
/// Writes are never retried automatically, so a lost reply can never send or move a message twice.
/// </summary>
/// <remarks>
/// HCG Mail keeps no local message store (bodies are held in memory only), so there is nothing for Graph delta
/// queries to keep in sync: each list load asks the server for the current page, and older pages load on demand.
/// </remarks>
public sealed class GraphMailSession : IMailSession
{
    private const int MessageCacheSize = 6;
    private const string MessageSelect = "id,subject,from,toRecipients,receivedDateTime,bodyPreview,isRead,flag,hasAttachments,internetMessageId,isDraft,parentFolderId";

    internal static readonly (string WellKnownName, FolderRole Role)[] WellKnownFolders =
    [
        ("inbox", FolderRole.Inbox), ("drafts", FolderRole.Drafts), ("sentitems", FolderRole.Sent),
        ("archive", FolderRole.Archive), ("junkemail", FolderRole.Junk), ("deleteditems", FolderRole.Trash),
    ];

    private readonly GraphClient _graph;
    private readonly IHtmlToTextConverter _htmlToText;
    private readonly ILogger _logger;
    private readonly IDeliveryLog? _deliveryLog;
    private readonly LinkedList<(string Id, MimeMessage Message)> _cache = new();
    private Dictionary<FolderRole, string> _roles = [];
    private MailConnectionState _state = MailConnectionState.Disconnected;
    private bool _disposed;

    public GraphMailSession(MailAccount account, IMicrosoftAuth auth, IHtmlToTextConverter htmlToText, ILogger logger,
        IDeliveryLog? deliveryLog = null, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (!account.IsMicrosoft || string.IsNullOrEmpty(account.MicrosoftAccountId))
            throw new ArgumentException("Not a connected Microsoft account.", nameof(account));
        Account = account;
        _graph = new GraphClient(auth, account.MicrosoftAccountId, logger, handler, delay);
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
        await RunAsync(async ct =>
        {
            // Proves the token works and the account has a Graph-reachable mailbox (fails clearly otherwise).
            using var inbox = await _graph.GetJsonAsync("/me/mailFolders/inbox?$select=id", ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        State = MailConnectionState.Connected;
    }

    /// <summary>The mailbox's primary address and display name (the sign-in name can differ from the mail address).</summary>
    public Task<(string? Mail, string? DisplayName, string? UserPrincipalName)> GetProfileAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            using var me = await _graph.GetJsonAsync("/me?$select=mail,displayName,userPrincipalName", ct).ConfigureAwait(false);
            return (Str(me.RootElement, "mail"), Str(me.RootElement, "displayName"), Str(me.RootElement, "userPrincipalName"));
        }, cancellationToken);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<MailFolder>>(async ct =>
        {
            var roles = new Dictionary<FolderRole, string>();
            foreach (var (name, role) in WellKnownFolders)
            {
                try
                {
                    using var json = await _graph.GetJsonAsync($"/me/mailFolders/{name}?$select=id", ct).ConfigureAwait(false);
                    roles[role] = json.RootElement.GetProperty("id").GetString()!;
                }
                catch (MailOperationException ex) when (ex.Kind is MailErrorKind.FolderNotFound or MailErrorKind.NotSupported && role == FolderRole.Archive)
                {
                    // Not every mailbox has an Archive folder.
                }
            }
            _roles = roles;

            var folders = new List<MailFolder>();
            await AddFoldersAsync("/me/mailFolders?$top=100&$select=id,displayName,childFolderCount,unreadItemCount,totalItemCount", 0, folders, roles, ct).ConfigureAwait(false);
            return folders
                .OrderBy(f => f.Role == FolderRole.None ? 1 : 0)
                .ThenBy(f => f.Role == FolderRole.None ? 0 : Array.FindIndex(WellKnownFolders, w => w.Role == f.Role))
                .ToList();
        }, cancellationToken);

    private async Task AddFoldersAsync(string url, int depth, List<MailFolder> folders, Dictionary<FolderRole, string> roles, CancellationToken ct)
    {
        string? next = url;
        while (next is not null && folders.Count < 300)
        {
            using var json = await _graph.GetJsonAsync(next, ct).ConfigureAwait(false);
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var role = roles.FirstOrDefault(r => r.Value == id).Key;
                folders.Add(new MailFolder(id, Str(item, "displayName") ?? "(folder)", '/', role, true, depth,
                    Int(item, "unreadItemCount"), Int(item, "totalItemCount")));
                if (depth < 3 && Int(item, "childFolderCount") > 0)
                    await AddFoldersAsync($"/me/mailFolders/{Esc(id)}/childFolders?$top=100&$select=id,displayName,childFolderCount,unreadItemCount,totalItemCount", depth + 1, folders, roles, ct).ConfigureAwait(false);
            }
            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    public Task<FolderPage> GetMessagesAsync(string folderFullName, int skip, int count, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            using var folder = await _graph.GetJsonAsync($"/me/mailFolders/{Esc(folderFullName)}?$select=totalItemCount,unreadItemCount", ct).ConfigureAwait(false);
            var total = Int(folder.RootElement, "totalItemCount") ?? 0;
            var unread = Int(folder.RootElement, "unreadItemCount") ?? 0;
            using var page = await _graph.GetJsonAsync(
                $"/me/mailFolders/{Esc(folderFullName)}/messages?$top={count}&$skip={skip}&$orderby=receivedDateTime desc&$select={MessageSelect}", ct).ConfigureAwait(false);
            var messages = page.RootElement.GetProperty("value").EnumerateArray().Select(m => ToSummary(m, folderFullName)).ToList();
            return new FolderPage(folderFullName, messages, total, unread, 0, HasOlder: skip + messages.Count < total);
        }, cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> SearchAsync(string folderFullName, string query, int maxResults, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<MessageSummary>>(async ct =>
        {
            // KQL search over the whole folder on Microsoft's side ($search cannot be combined with $orderby).
            var term = Uri.EscapeDataString("\"" + query.Replace("\"", " ").Trim() + "\"");
            using var json = await _graph.GetJsonAsync(
                $"/me/mailFolders/{Esc(folderFullName)}/messages?$search={term}&$top={Math.Clamp(maxResults, 1, 250)}&$select={MessageSelect}", ct).ConfigureAwait(false);
            return json.RootElement.GetProperty("value").EnumerateArray().Select(m => ToSummary(m, folderFullName))
                .OrderByDescending(m => m.Date).ToList();
        }, cancellationToken);

    public Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var id = IdOf(message);
            var mime = await GetMimeAsync(id, ct).ConfigureAwait(false);
            using var state = await _graph.GetJsonAsync($"/me/messages/{Esc(id)}?$select=isRead,flag,isDraft", ct).ConfigureAwait(false);
            return MimeConverter.ToContent(mime, message, FlagsOf(state.RootElement), _htmlToText);
        }, cancellationToken);

    public Task SetFlagsAsync(IReadOnlyList<MessageRef> messages, CoreFlags flags, bool add, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var patch = new Dictionary<string, object>();
            if (flags.HasFlag(CoreFlags.Seen))
                patch["isRead"] = add;
            if (flags.HasFlag(CoreFlags.Flagged))
                patch["flag"] = new Dictionary<string, string> { ["flagStatus"] = add ? "flagged" : "notFlagged" };
            if (patch.Count == 0)
                return true; // \Answered and \Draft have no Graph equivalent HCG Mail writes
            foreach (var message in messages)
            {
                // Setting a flag to a value is idempotent, so a throttled PATCH may be retried.
                using var _ = await _graph.WriteAsync(HttpMethod.Patch, $"/me/messages/{Esc(IdOf(message))}", Json(patch), ct, idempotent: true).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);

    public Task DeleteAsync(MessageRef message, bool permanent, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var id = IdOf(message);
            var inTrash = _roles.TryGetValue(FolderRole.Trash, out var trash) && trash == message.FolderFullName;
            if (permanent || inTrash)
                await _graph.WriteAsync(HttpMethod.Delete, $"/me/messages/{Esc(id)}", null, ct).ConfigureAwait(false);
            else
                await _graph.WriteAsync(HttpMethod.Post, $"/me/messages/{Esc(id)}/move", Json(new { destinationId = "deleteditems" }), ct).ConfigureAwait(false);
            Forget(id);
            return true;
        }, cancellationToken);

    public Task MoveAsync(MessageRef message, string targetFolderFullName, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var id = IdOf(message);
            await _graph.WriteAsync(HttpMethod.Post, $"/me/messages/{Esc(id)}/move", Json(new { destinationId = targetFolderFullName }), ct).ConfigureAwait(false);
            Forget(id);
            return true;
        }, cancellationToken);

    public async Task SaveAttachmentAsync(MessageRef message, int attachmentIndex, Stream destination, CancellationToken cancellationToken = default)
    {
        var mime = await RunAsync(ct => GetMimeAsync(IdOf(message), ct), cancellationToken).ConfigureAwait(false);
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
            SmtpServer = "graph.microsoft.com (Microsoft Graph)",
            State = DeliveryState.NotSent,
            MessageId = mime.MessageId,
            RecipientCount = message.To.Count + message.Cc.Count + message.Bcc.Count,
            Priority = message.Priority,
        };

        try
        {
            // sendMail with a MIME body. Exchange reads Bcc recipients from the MIME and removes the header from
            // delivered copies; the message is saved to Sent Items by Microsoft. One attempt only.
            await RunAsync(async ct =>
            {
                await _graph.WriteAsync(HttpMethod.Post, "/me/sendMail", MimeContent(mime), ct).ConfigureAwait(false);
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
                    _ when ex.OutcomeUnknown => DeliveryState.Submitted, // the request may have reached Microsoft
                    _ => DeliveryState.NotSent,
                },
                ErrorMessage = ex.UserMessage,
            });
            throw;
        }

        Record(record with { State = DeliveryState.Accepted, SmtpStatusCode = 202, ServerReply = "202 Accepted by Microsoft Graph" });

        var warnings = new List<string>();
        if (!saveCopyToSent)
            warnings.Add("Microsoft saves a copy of sent mail in Sent Items for this account; HCG Mail cannot turn that off.");
        if (message.DraftRef is { } draft)
        {
            try
            {
                await DeleteAsync(draft, permanent: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove sent draft for account {AccountId}", Account.Id);
                warnings.Add("The draft could not be removed from the Drafts folder.");
            }
        }
        return new SendResult(true, warnings.Count == 0 ? null : string.Join(" ", warnings),
            record with { State = DeliveryState.Accepted, SmtpStatusCode = 202, ServerReply = "202 Accepted by Microsoft Graph" });
    }

    public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage message, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var mime = MimeConverter.ToMimeMessage(message with { MessageId = message.MessageId ?? MimeConverter.NewMessageId(Account) }, Account);
            using var created = await _graph.WriteAsync(HttpMethod.Post, "/me/messages", MimeContent(mime), ct).ConfigureAwait(false);
            var id = created?.RootElement.GetProperty("id").GetString()
                     ?? throw new MailOperationException(MailErrorKind.Protocol, "Microsoft did not return the saved draft.");
            var folder = Str(created!.RootElement, "parentFolderId") ?? _roles.GetValueOrDefault(FolderRole.Drafts, "drafts");
            if (message.DraftRef is { } previous)
            {
                try
                {
                    await _graph.WriteAsync(HttpMethod.Delete, $"/me/messages/{Esc(IdOf(previous))}", null, ct).ConfigureAwait(false);
                    Forget(IdOf(previous));
                }
                catch (MailOperationException ex) when (ex.Kind is MailErrorKind.MessageNotFound)
                {
                }
            }
            return new DraftSaveResult(new MessageRef(folder, 0, 0, id));
        }, cancellationToken);

    public Task<OutgoingMessage> LoadDraftAsync(MessageRef draft, CancellationToken cancellationToken = default) =>
        RunAsync(async ct =>
        {
            var mime = await GetMimeAsync(IdOf(draft), ct).ConfigureAwait(false);
            return await MimeConverter.ToOutgoingAsync(mime, draft, _htmlToText, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task DisconnectAsync()
    {
        lock (_cache)
            _cache.Clear();
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

    private async Task<MimeMessage> GetMimeAsync(string id, CancellationToken ct)
    {
        lock (_cache)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == id)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return node.Value.Message;
                }
            }
        }
        var bytes = await _graph.GetBytesAsync($"/me/messages/{Esc(id)}/$value", ct).ConfigureAwait(false);
        var message = await MimeMessage.LoadAsync(new MemoryStream(bytes), ct).ConfigureAwait(false);
        lock (_cache)
        {
            _cache.AddFirst((id, message));
            while (_cache.Count > MessageCacheSize)
                _cache.RemoveLast();
        }
        return message;
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
                    return;
                }
            }
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

    internal static MessageSummary ToSummary(JsonElement m, string folderId)
    {
        var from = Address(m, "from");
        var flags = FlagsOf(m);
        return new MessageSummary
        {
            Ref = new MessageRef(Str(m, "parentFolderId") ?? folderId, 0, 0, m.GetProperty("id").GetString()),
            From = from,
            To = m.TryGetProperty("toRecipients", out var to) && to.ValueKind == JsonValueKind.Array
                ? to.EnumerateArray().Select(r => Address(r, null)).Where(a => a is not null).Select(a => a!).ToList()
                : [],
            Subject = Str(m, "subject") ?? "",
            Date = DateTimeOffset.TryParse(Str(m, "receivedDateTime"), out var date) ? date : DateTimeOffset.MinValue,
            Preview = PreviewText.Clean(Str(m, "bodyPreview")),
            Flags = flags,
            HasAttachments = m.TryGetProperty("hasAttachments", out var a) && a.ValueKind == JsonValueKind.True,
            MessageId = Str(m, "internetMessageId")?.Trim('<', '>'),
        };
    }

    private static CoreFlags FlagsOf(JsonElement m)
    {
        var flags = CoreFlags.None;
        if (m.TryGetProperty("isRead", out var read) && read.ValueKind == JsonValueKind.True)
            flags |= CoreFlags.Seen;
        if (m.TryGetProperty("flag", out var flag) && Str(flag, "flagStatus") == "flagged")
            flags |= CoreFlags.Flagged;
        if (m.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True)
            flags |= CoreFlags.Draft;
        return flags;
    }

    private static EmailAddress? Address(JsonElement parent, string? property)
    {
        var holder = parent;
        if (property is not null && !parent.TryGetProperty(property, out holder))
            return null;
        if (holder.ValueKind != JsonValueKind.Object || !holder.TryGetProperty("emailAddress", out var email))
            return null;
        var address = Str(email, "address");
        return string.IsNullOrWhiteSpace(address) ? null : new EmailAddress(Str(email, "name"), address);
    }

    private static string IdOf(MessageRef message) =>
        message.ServerId ?? throw new MailOperationException(MailErrorKind.MessageNotFound, "This message reference does not belong to a Microsoft account.");

    private static HttpContent MimeContent(MimeMessage mime)
    {
        using var stream = new MemoryStream();
        mime.WriteTo(FormatOptions.Default, stream);
        var content = new StringContent(Convert.ToBase64String(stream.ToArray()), Encoding.ASCII);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return content;
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string Esc(string id) => Uri.EscapeDataString(id);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

/// <summary>Chooses the session type for an account: IMAP/SMTP or Microsoft Graph.</summary>
public sealed class MailSessionFactory(
    IMailClientFactory clientFactory,
    IHtmlToTextConverter htmlToText,
    ILoggerFactory loggerFactory,
    IMicrosoftAuth microsoftAuth,
    IDeliveryLog? deliveryLog = null) : IMailSessionFactory
{
    private readonly Imap.MailKitMailSessionFactory _imap = new(clientFactory, htmlToText, loggerFactory, deliveryLog);

    public IMailSession Create(MailAccount account, string password) =>
        account.IsMicrosoft
            ? new GraphMailSession(account, microsoftAuth, htmlToText, loggerFactory.CreateLogger<GraphMailSession>(), deliveryLog)
            : _imap.Create(account, password);
}
