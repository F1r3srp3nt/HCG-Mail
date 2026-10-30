using System.Net;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Mime;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;
using CoreFlags = HCG.Mail.Core.Mail.MessageFlags;
using MessageSummary = HCG.Mail.Core.Mail.MessageSummary;
using MailFolder = HCG.Mail.Core.Mail.MailFolder;

namespace HCG.Mail.Email.Imap;

/// <summary>
/// IMAP/SMTP session for one account. IMAP commands are serialized through a single
/// connection (MailKit clients are not thread-safe). A dropped connection is re-established
/// and the read-only operation retried once.
/// </summary>
/// <remarks>
/// Cancellation tokens only cancel work that has not started yet. Cancelling an in-flight
/// MailKit command tears down the connection, so started commands run to completion (bounded
/// by the client timeout) and callers simply ignore results they no longer need.
/// </remarks>
public sealed class MailKitMailSession : IMailSession
{
    private const int MessageCacheSize = 6;
    private const int PreviewLength = 220;

    private static readonly MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure | MessageSummaryItems.Size |
        MessageSummaryItems.PreviewText;

    private readonly string _password;
    private readonly IMailClientFactory _clientFactory;
    private readonly IHtmlToTextConverter _htmlToText;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<(MessageRef Ref, MimeMessage Message)> _messageCache = new();

    private IImapClient? _imap;
    private TlsValidationRecorder? _imapTls;
    private IReadOnlyList<MailFolder>? _folders;
    private MailConnectionState _state = MailConnectionState.Disconnected;
    private bool _disposed;

    public MailKitMailSession(MailAccount account, string password, IMailClientFactory clientFactory, IHtmlToTextConverter htmlToText, ILogger<MailKitMailSession> logger)
    {
        Account = account;
        _password = password;
        _clientFactory = clientFactory;
        _htmlToText = htmlToText;
        _logger = logger;
    }

    public MailAccount Account { get; }

    public MailConnectionState State => _state;

    public event EventHandler<MailConnectionState>? StateChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        RunAsync((_, _) => Task.FromResult(true), retry: false, cancellationToken);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        RunAsync((client, ct) => LoadFoldersAsync(client, ct), retry: true, cancellationToken);

    public Task<FolderPage> GetMessagesAsync(string folderFullName, int skip, int count, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            var folder = await OpenFolderAsync(client, folderFullName, ct).ConfigureAwait(false);
            var total = folder.Count;
            var end = total - 1 - skip;
            IList<IMessageSummary> fetched = [];
            var start = Math.Max(0, end - count + 1);
            if (end >= 0)
                fetched = await folder.FetchAsync(start, end, new FetchRequest(SummaryItems), ct).ConfigureAwait(false);

            var unseen = await folder.SearchAsync(SearchQuery.NotSeen, ct).ConfigureAwait(false);
            var messages = fetched
                .Where(s => s.UniqueId.IsValid)
                .Select(s => ToSummary(s, folder))
                .OrderByDescending(m => m.Date)
                .ThenByDescending(m => m.Ref.Uid)
                .ToList();

            return new FolderPage(folder.FullName, messages, total, unseen.Count, folder.UidValidity, HasOlder: start > 0);
        }, retry: true, cancellationToken);

    public Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            var (folder, uid) = await OpenMessageFolderAsync(client, message, ct).ConfigureAwait(false);
            var mime = await GetMimeAsync(folder, message, uid, ct).ConfigureAwait(false);
            var summaries = await folder.FetchAsync([uid], new FetchRequest(MessageSummaryItems.Flags), ct).ConfigureAwait(false);
            var flags = FlagMapper.ToCore(summaries.FirstOrDefault()?.Flags);
            return MimeConverter.ToContent(mime, message, flags, _htmlToText);
        }, retry: true, cancellationToken);

    public Task SetFlagsAsync(IReadOnlyList<MessageRef> messages, CoreFlags flags, bool add, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            foreach (var group in messages.GroupBy(m => m.FolderFullName))
            {
                var folder = await OpenFolderAsync(client, group.Key, ct).ConfigureAwait(false);
                var uids = group.Where(m => m.UidValidity == folder.UidValidity).Select(m => new UniqueId(m.Uid)).ToList();
                if (uids.Count == 0)
                    throw new MailOperationException(MailErrorKind.MessageNotFound, "The folder changed on the server. Refresh and try again.");
                var request = new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, FlagMapper.ToMailKit(flags)) { Silent = true };
                await folder.StoreAsync(uids, request, ct).ConfigureAwait(false);
            }
            return true;
        }, retry: true, cancellationToken);

    public Task DeleteAsync(MessageRef message, bool permanent, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            var folders = _folders ?? await LoadFoldersAsync(client, ct).ConfigureAwait(false);
            var trash = folders.FirstOrDefault(f => f.Role == FolderRole.Trash);
            var inTrash = trash is not null && trash.FullName == message.FolderFullName;
            var (folder, uid) = await OpenMessageFolderAsync(client, message, ct).ConfigureAwait(false);

            if (!permanent && !inTrash)
            {
                if (trash is null)
                    throw new MailOperationException(MailErrorKind.NotSupported, "This account has no Trash folder. Delete the message permanently instead.");
                var target = await GetFolderAsync(client, trash.FullName, ct).ConfigureAwait(false);
                await MoveCoreAsync(client, folder, uid, target, ct).ConfigureAwait(false);
            }
            else
            {
                await folder.StoreAsync([uid], new StoreFlagsRequest(StoreAction.Add, MailKit.MessageFlags.Deleted) { Silent = true }, ct).ConfigureAwait(false);
                await ExpungeAsync(client, folder, uid, ct).ConfigureAwait(false);
            }
            Forget(message);
            return true;
        }, retry: false, cancellationToken);

    public Task MoveAsync(MessageRef message, string targetFolderFullName, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            var target = await GetFolderAsync(client, targetFolderFullName, ct).ConfigureAwait(false);
            var (folder, uid) = await OpenMessageFolderAsync(client, message, ct).ConfigureAwait(false);
            await MoveCoreAsync(client, folder, uid, target, ct).ConfigureAwait(false);
            Forget(message);
            return true;
        }, retry: false, cancellationToken);

    public Task SaveAttachmentAsync(MessageRef message, int attachmentIndex, Stream destination, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            var (folder, uid) = await OpenMessageFolderAsync(client, message, ct).ConfigureAwait(false);
            var mime = await GetMimeAsync(folder, message, uid, ct).ConfigureAwait(false);
            var attachments = MimeConverter.GetAttachments(mime);
            if (attachmentIndex < 0 || attachmentIndex >= attachments.Count)
                throw new MailOperationException(MailErrorKind.MessageNotFound, "The attachment could not be found in the message.");
            await MimeConverter.WriteAttachmentAsync(attachments[attachmentIndex], destination, ct).ConfigureAwait(false);
            return true;
        }, retry: true, cancellationToken);

    public async Task<byte[]> GetAttachmentBytesAsync(MessageRef message, int attachmentIndex, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await SaveAttachmentAsync(message, attachmentIndex, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task<SendResult> SendAsync(OutgoingMessage message, bool saveCopyToSent, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new MailOperationException(MailErrorKind.Network, "This connection has been closed. Reconnect the account and try again.");
        if (!message.HasRecipients)
            throw new MailOperationException(MailErrorKind.ServerRejected, "Add at least one recipient.");

        var mime = MimeConverter.ToMimeMessage(message, Account);
        await SendViaSmtpAsync(mime, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Message sent for account {AccountId} to {RecipientCount} recipient(s)", Account.Id, message.To.Count + message.Cc.Count + message.Bcc.Count);

        // The message has been delivered to the SMTP server at this point. Anything that fails
        // from here on is reported as a warning, never as a failed send.
        var warnings = new List<string>();
        var saved = false;
        if (saveCopyToSent)
        {
            try
            {
                saved = await SaveSentCopyAsync(mime, CancellationToken.None).ConfigureAwait(false);
                if (!saved)
                    warnings.Add("No Sent folder was found, so no copy was saved.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saving sent copy failed for account {AccountId}", Account.Id);
                warnings.Add("The message was sent, but a copy could not be saved to the Sent folder: " + Describe(ex));
            }
        }

        if (message.RepliedMessage is { } replied)
        {
            try
            {
                await SetFlagsAsync([replied], CoreFlags.Answered, add: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not flag original message as answered");
            }
        }

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

        return new SendResult(saved, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage message, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            var folders = _folders ?? await LoadFoldersAsync(client, ct).ConfigureAwait(false);
            var draftsInfo = folders.FirstOrDefault(f => f.Role == FolderRole.Drafts)
                ?? throw new MailOperationException(MailErrorKind.NotSupported, "This account has no Drafts folder on the server, so the draft cannot be saved.");
            var drafts = await GetFolderAsync(client, draftsInfo.FullName, ct).ConfigureAwait(false);

            var mime = MimeConverter.ToMimeMessage(message with { MessageId = message.MessageId ?? MimeConverter.NewMessageId(Account) }, Account);
            var appended = await drafts.AppendAsync(new AppendRequest(mime, MailKit.MessageFlags.Draft | MailKit.MessageFlags.Seen), ct).ConfigureAwait(false);

            MessageRef? newRef = null;
            if (appended is { } uid)
            {
                newRef = new MessageRef(drafts.FullName, uid.Id, uid.Validity);
            }
            else
            {
                // Server without UIDPLUS: locate the draft we just stored by its Message-ID.
                await drafts.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
                var found = await drafts.SearchAsync(SearchQuery.HeaderContains("Message-ID", mime.MessageId!), ct).ConfigureAwait(false);
                if (found.Count > 0)
                    newRef = new MessageRef(drafts.FullName, found.Max().Id, drafts.UidValidity);
            }

            if (message.DraftRef is { } old && old != newRef)
            {
                try
                {
                    var (oldFolder, oldUid) = await OpenMessageFolderAsync(client, old, ct).ConfigureAwait(false);
                    await oldFolder.StoreAsync([oldUid], new StoreFlagsRequest(StoreAction.Add, MailKit.MessageFlags.Deleted) { Silent = true }, ct).ConfigureAwait(false);
                    await ExpungeAsync(client, oldFolder, oldUid, ct).ConfigureAwait(false);
                    Forget(old);
                }
                catch (MailOperationException ex) when (ex.Kind == MailErrorKind.MessageNotFound)
                {
                    // Already gone (e.g. deleted from another device).
                }
            }

            return new DraftSaveResult(newRef);
        }, retry: false, cancellationToken);

    public Task<OutgoingMessage> LoadDraftAsync(MessageRef draft, CancellationToken cancellationToken = default) =>
        RunAsync(async (client, ct) =>
        {
            var (folder, uid) = await OpenMessageFolderAsync(client, draft, ct).ConfigureAwait(false);
            var mime = await GetMimeAsync(folder, draft, uid, ct).ConfigureAwait(false);
            return await MimeConverter.ToOutgoingAsync(mime, draft, _htmlToText, ct).ConfigureAwait(false);
        }, retry: true, cancellationToken);

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetConnectionAsync(graceful: true).ConfigureAwait(false);
            SetState(MailConnectionState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while disconnecting");
        }
        _disposed = true;
        _gate.Dispose();
    }

    // ---- connection management -------------------------------------------------------------

    private async Task<T> RunAsync<T>(Func<IImapClient, CancellationToken, Task<T>> operation, bool retry, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new MailOperationException(MailErrorKind.Network, "This connection has been closed. Refresh to reconnect.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 0; ; attempt++)
            {
                var hadConnection = _imap is { IsConnected: true, IsAuthenticated: true };
                try
                {
                    var client = await EnsureConnectedAsync().ConfigureAwait(false);
                    return await operation(client, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (retry && attempt == 0 && hadConnection && IsConnectionLoss(ex))
                {
                    _logger.LogInformation("IMAP connection for account {AccountId} was lost ({Error}); reconnecting", Account.Id, ex.GetType().Name);
                    await ResetConnectionAsync(graceful: false).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not MailOperationException || ((MailOperationException)ex).IsTransient)
        {
            var translated = MailErrorTranslator.Translate(ex, MailProtocol.Imap, Account.Imap, _imapTls);
            if (IsConnectionLoss(ex) || translated.Kind is MailErrorKind.Authentication or MailErrorKind.Tls or MailErrorKind.Certificate or MailErrorKind.Dns or MailErrorKind.Timeout or MailErrorKind.Network)
            {
                await ResetConnectionAsync(graceful: false).ConfigureAwait(false);
                SetState(MailConnectionState.Failed);
            }
            _logger.LogWarning("IMAP operation failed for account {AccountId}: {Kind} ({Error})", Account.Id, translated.Kind, ex.GetType().Name);
            throw translated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IImapClient> EnsureConnectedAsync()
    {
        if (_imap is { IsConnected: true, IsAuthenticated: true })
            return _imap;

        await ResetConnectionAsync(graceful: false).ConfigureAwait(false);
        SetState(MailConnectionState.Connecting);

        var recorder = new TlsValidationRecorder();
        _imapTls = recorder;
        var client = _clientFactory.CreateImapClient(recorder);
        try
        {
            var endpoint = Account.Imap;
            await client.ConnectAsync(endpoint.Host, endpoint.Port, SecurityOptions.ToSecureSocketOptions(endpoint.Security)).ConfigureAwait(false);
            if (!client.IsSecure)
                throw new MailOperationException(MailErrorKind.Tls, "The connection to the incoming server is not encrypted. HCG Mail will not send your password over it.");
            await client.AuthenticateAsync(new NetworkCredential(Account.Username, _password)).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _imap = client;
        _logger.LogInformation("IMAP connected for account {AccountId} to {Host}:{Port}", Account.Id, Account.Imap.Host, Account.Imap.Port);
        SetState(MailConnectionState.Connected);
        return client;
    }

    private async Task ResetConnectionAsync(bool graceful)
    {
        var client = _imap;
        _imap = null;
        _folders = null;
        lock (_messageCache)
            _messageCache.Clear();
        if (client is null)
            return;
        try
        {
            if (graceful && client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring error during IMAP disconnect");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static bool IsConnectionLoss(Exception ex) =>
        ex is IOException or ImapProtocolException or ServiceNotConnectedException or System.Net.Sockets.SocketException or ObjectDisposedException
        || (ex is MailOperationException { Kind: MailErrorKind.Network });

    private void SetState(MailConnectionState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    // ---- folders & messages -----------------------------------------------------------------

    private async Task<IReadOnlyList<MailFolder>> LoadFoldersAsync(IImapClient client, CancellationToken ct)
    {
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : null;
        var folders = new List<IMailFolder>();
        if (ns is not null)
            folders.AddRange(await client.GetFoldersAsync(ns, StatusItems.Unread | StatusItems.Count, subscribedOnly: false, ct).ConfigureAwait(false));

        if (!folders.Any(f => string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase)))
        {
            var inbox = client.Inbox;
            await inbox.StatusAsync(StatusItems.Unread | StatusItems.Count, ct).ConfigureAwait(false);
            folders.Insert(0, inbox);
        }

        var descriptors = folders
            .Where(f => f.Exists && !f.Attributes.HasFlag(FolderAttributes.NonExistent))
            .Select(f =>
            {
                var selectable = !f.Attributes.HasFlag(FolderAttributes.NoSelect);
                return new FolderDescriptor(
                    f.FullName,
                    f.Name,
                    f.DirectorySeparator,
                    FlagMapper.ToSpecialUse(f.Attributes),
                    selectable,
                    selectable ? f.Unread : null,
                    selectable ? f.Count : null);
            });

        var mapped = FolderRoleMapper.SortForNavigation(FolderRoleMapper.AssignRoles(descriptors));
        _folders = mapped;
        return mapped;
    }

    private async Task<IMailFolder> GetFolderAsync(IImapClient client, string fullName, CancellationToken ct)
    {
        if (string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase))
            return client.Inbox;
        return await client.GetFolderAsync(fullName, ct).ConfigureAwait(false);
    }

    private async Task<IMailFolder> OpenFolderAsync(IImapClient client, string fullName, CancellationToken ct)
    {
        var folder = await GetFolderAsync(client, fullName, ct).ConfigureAwait(false);
        if (!folder.IsOpen)
            await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        return folder;
    }

    private async Task<(IMailFolder Folder, UniqueId Uid)> OpenMessageFolderAsync(IImapClient client, MessageRef message, CancellationToken ct)
    {
        var folder = await OpenFolderAsync(client, message.FolderFullName, ct).ConfigureAwait(false);
        if (folder.UidValidity != message.UidValidity)
            throw new MailOperationException(MailErrorKind.MessageNotFound, "The folder changed on the server. Refresh the folder and try again.");
        return (folder, new UniqueId(message.UidValidity, message.Uid));
    }

    private async Task<MimeMessage> GetMimeAsync(IMailFolder folder, MessageRef message, UniqueId uid, CancellationToken ct)
    {
        lock (_messageCache)
        {
            var node = _messageCache.First;
            while (node is not null)
            {
                if (node.Value.Ref == message)
                {
                    _messageCache.Remove(node);
                    _messageCache.AddFirst(node);
                    return node.Value.Message;
                }
                node = node.Next;
            }
        }

        // FETCH BODY.PEEK[] — reading a message never changes its \Seen flag implicitly.
        var mime = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
        lock (_messageCache)
        {
            _messageCache.AddFirst((message, mime));
            while (_messageCache.Count > MessageCacheSize)
                _messageCache.RemoveLast();
        }
        return mime;
    }

    private void Forget(MessageRef message)
    {
        lock (_messageCache)
        {
            var node = _messageCache.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Ref == message)
                    _messageCache.Remove(node);
                node = next;
            }
        }
    }

    private static async Task MoveCoreAsync(IImapClient client, IMailFolder source, UniqueId uid, IMailFolder target, CancellationToken ct)
    {
        if (client.Capabilities.HasFlag(ImapCapabilities.Move) || client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            await source.MoveToAsync(uid, target, ct).ConfigureAwait(false);
            return;
        }
        await source.CopyToAsync(uid, target, ct).ConfigureAwait(false);
        await source.StoreAsync([uid], new StoreFlagsRequest(StoreAction.Add, MailKit.MessageFlags.Deleted) { Silent = true }, ct).ConfigureAwait(false);
        await source.ExpungeAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExpungeAsync(IImapClient client, IMailFolder folder, UniqueId uid, CancellationToken ct)
    {
        // UID EXPUNGE removes only this message. Without UIDPLUS a plain EXPUNGE is the only
        // option, which also removes any other messages already marked \Deleted in the folder.
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            await folder.ExpungeAsync([uid], ct).ConfigureAwait(false);
        else
            await folder.ExpungeAsync(ct).ConfigureAwait(false);
    }

    private static MessageSummary ToSummary(IMessageSummary s, IMailFolder folder)
    {
        var envelope = s.Envelope;
        var from = envelope?.From.Mailboxes.FirstOrDefault();
        return new MessageSummary
        {
            Ref = new MessageRef(folder.FullName, s.UniqueId.Id, folder.UidValidity),
            From = from is null ? null : MimeConverter.ToAddress(from),
            To = envelope?.To.Mailboxes.Select(MimeConverter.ToAddress).ToList() ?? [],
            Subject = envelope?.Subject ?? "",
            // Received time as recorded by the server; the sender-supplied Date header is a fallback.
            Date = s.InternalDate ?? envelope?.Date ?? DateTimeOffset.MinValue,
            Preview = NormalizePreview(s.PreviewText),
            Flags = FlagMapper.ToCore(s.Flags),
            HasAttachments = s.Attachments?.Any() == true,
            Size = s.Size ?? 0,
            MessageId = envelope?.MessageId,
        };
    }

    internal static string NormalizePreview(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
            return "";
        var collapsed = string.Join(' ', preview.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > PreviewLength ? collapsed[..PreviewLength] + "…" : collapsed;
    }

    // ---- sending ------------------------------------------------------------------------------

    private async Task SendViaSmtpAsync(MimeMessage mime, CancellationToken cancellationToken)
    {
        var recorder = new TlsValidationRecorder();
        using var smtp = _clientFactory.CreateSmtpClient(recorder);
        var endpoint = Account.Smtp;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await smtp.ConnectAsync(endpoint.Host, endpoint.Port, SecurityOptions.ToSecureSocketOptions(endpoint.Security)).ConfigureAwait(false);
            if (!smtp.IsSecure)
                throw new MailOperationException(MailErrorKind.Tls, "The connection to the outgoing server is not encrypted. HCG Mail will not send over it.");
            if (!smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                throw new MailOperationException(MailErrorKind.Authentication, "The outgoing server does not offer authentication. Check the outgoing server name and port.");
            await smtp.AuthenticateAsync(new NetworkCredential(Account.Username, _password)).ConfigureAwait(false);
            await smtp.SendAsync(mime).ConfigureAwait(false);
            try
            {
                await smtp.DisconnectAsync(true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring error during SMTP disconnect");
            }
        }
        catch (Exception ex) when (ex is not MailOperationException)
        {
            var translated = MailErrorTranslator.Translate(ex, MailProtocol.Smtp, endpoint, recorder);
            _logger.LogWarning("SMTP send failed for account {AccountId}: {Kind} ({Error})", Account.Id, translated.Kind, ex.GetType().Name);
            throw translated;
        }
    }

    /// <returns>True when a copy exists in Sent afterwards (saved by us or by the server).</returns>
    private Task<bool> SaveSentCopyAsync(MimeMessage mime, CancellationToken cancellationToken) =>
        RunAsync(async (client, ct) =>
        {
            // Gmail stores sent messages automatically; appending would create a duplicate.
            if (client.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
                return true;

            var folders = _folders ?? await LoadFoldersAsync(client, ct).ConfigureAwait(false);
            var sentInfo = folders.FirstOrDefault(f => f.Role == FolderRole.Sent);
            if (sentInfo is null)
                return false;
            var sent = await GetFolderAsync(client, sentInfo.FullName, ct).ConfigureAwait(false);

            // Some servers save submitted mail themselves; check by Message-ID before appending.
            await sent.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            var existing = await sent.SearchAsync(SearchQuery.HeaderContains("Message-ID", mime.MessageId!), ct).ConfigureAwait(false);
            if (existing.Count > 0)
                return true;

            await sent.AppendAsync(new AppendRequest(mime, MailKit.MessageFlags.Seen), ct).ConfigureAwait(false);
            return true;
        }, retry: false, cancellationToken);

    private static string Describe(Exception ex) => ex is MailOperationException moe ? moe.UserMessage : ex.GetType().Name;
}

public sealed class MailKitMailSessionFactory(IMailClientFactory clientFactory, IHtmlToTextConverter htmlToText, ILoggerFactory loggerFactory) : IMailSessionFactory
{
    public IMailSession Create(MailAccount account, string password) =>
        new MailKitMailSession(account, password, clientFactory, htmlToText, loggerFactory.CreateLogger<MailKitMailSession>());
}
