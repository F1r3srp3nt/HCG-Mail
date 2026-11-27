using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Services;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Email.Offline;

/// <summary>
/// Wraps an account's live session so mail stays readable without a connection. Every successful read is copied to the
/// device (<see cref="IMailCache"/>). When the server cannot be reached — no network, a timeout, a blocked port, a Wi-Fi
/// sign-in page, a certificate that does not verify — the session goes offline: reads are answered at once from the copy
/// (instead of every click waiting for a timeout), and changes, sending and attachment downloads fail immediately with a
/// clear message (sending goes to the Outbox). Only <see cref="ConnectAsync"/> — the user's Reconnect or the automatic
/// recovery — tries the server again. Nothing here weakens security: the live session still validates certificates and
/// encryption exactly as before, and the copy is only ever read locally.
/// </summary>
public sealed class OfflineCapableMailSession : IMailSession, IOfflineAwareSession
{
    private readonly IMailSession _inner;
    private readonly IMailCache _cache;
    private readonly INetworkMonitor _network;
    private readonly ILogger _logger;
    private volatile bool _offline;
    private MailOperationException? _reason;

    public OfflineCapableMailSession(IMailSession inner, IMailCache cache, INetworkMonitor network, ILogger logger)
    {
        _inner = inner;
        _cache = cache;
        _network = network;
        _logger = logger;
        _inner.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
    }

    public IMailSession Inner => _inner;
    public MailAccount Account => _inner.Account;
    public MailConnectionState State => _inner.State;
    public event EventHandler<MailConnectionState>? StateChanged;

    public bool IsOffline => _offline;
    public MailOperationException? OfflineReason => _reason;
    public DateTimeOffset? CopySavedAt { get; private set; }
    public event EventHandler? OfflineChanged;

    private string AccountId => _inner.Account.Id;

    public void MarkConnectionStale() => (_inner as IStaleConnectionAware)?.MarkConnectionStale();

    /// <summary>Failures that mean "the server cannot be reached safely from here right now", as opposed to a refused password.</summary>
    public static bool IsConnectivityFailure(MailOperationException ex) =>
        !ex.OutcomeUnknown && ReconnectPolicy.ModeFor(ex.Kind) != RecoveryMode.Never;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_network.IsNetworkAvailable)
            throw GoOffline(new MailOperationException(MailErrorKind.Network, "This device has no network connection. HCG Mail reconnects automatically when the network is back."));
        try
        {
            await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            GoOnline();
        }
        catch (MailOperationException ex) when (IsConnectivityFailure(ex))
        {
            throw GoOffline(ex);
        }
    }

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(() => _inner.GetFoldersAsync(cancellationToken),
            () => _cache.LoadFolders(AccountId),
            f => _cache.SaveFolders(AccountId, f),
            "The folder list");

    public Task<FolderPage> GetMessagesAsync(string folderFullName, int skip, int count, CancellationToken cancellationToken = default) =>
        ReadAsync(() => _inner.GetMessagesAsync(folderFullName, skip, count, cancellationToken),
            () => skip == 0 ? _cache.LoadPage(AccountId, folderFullName) : null,
            p =>
            {
                if (skip == 0)
                    _cache.SavePage(AccountId, p);
            },
            skip == 0 ? "This folder" : "Older messages");

    public Task<IReadOnlyList<MessageSummary>> SearchAsync(string folderFullName, string query, int maxResults, CancellationToken cancellationToken = default) =>
        ReadAsync(() => _inner.SearchAsync(folderFullName, query, maxResults, cancellationToken),
            () => _cache.LoadPage(AccountId, folderFullName) is { } page
                ? new CachedValue<IReadOnlyList<MessageSummary>>(SearchCopy(page.Value, query, maxResults), page.SavedAt)
                : null,
            _ => { },
            "Search");

    public Task<MessageContent> GetMessageAsync(MessageRef message, CancellationToken cancellationToken = default) =>
        ReadAsync(() => _inner.GetMessageAsync(message, cancellationToken),
            () => _cache.LoadMessage(AccountId, message),
            m => _cache.SaveMessage(AccountId, m),
            "This message");

    public Task SetFlagsAsync(IReadOnlyList<MessageRef> messages, MessageFlags flags, bool add, CancellationToken cancellationToken = default) =>
        WriteAsync(async () =>
        {
            await _inner.SetFlagsAsync(messages, flags, add, cancellationToken).ConfigureAwait(false);
            _cache.UpdateFlags(AccountId, messages, flags, add);
            return true;
        }, "Changes to messages are possible");

    public Task DeleteAsync(MessageRef message, bool permanent, CancellationToken cancellationToken = default) =>
        WriteAsync(async () =>
        {
            await _inner.DeleteAsync(message, permanent, cancellationToken).ConfigureAwait(false);
            _cache.Remove(AccountId, message);
            return true;
        }, "Deleting is possible");

    public Task MoveAsync(MessageRef message, string targetFolderFullName, CancellationToken cancellationToken = default) =>
        WriteAsync(async () =>
        {
            await _inner.MoveAsync(message, targetFolderFullName, cancellationToken).ConfigureAwait(false);
            _cache.Remove(AccountId, message);
            return true;
        }, "Moving messages is possible");

    public Task SaveAttachmentAsync(MessageRef message, int attachmentIndex, Stream destination, CancellationToken cancellationToken = default) =>
        WriteAsync(async () =>
        {
            await _inner.SaveAttachmentAsync(message, attachmentIndex, destination, cancellationToken).ConfigureAwait(false);
            return true;
        }, "Attachments are downloaded from the server, so saving it is possible");

    public Task<byte[]> GetAttachmentBytesAsync(MessageRef message, int attachmentIndex, CancellationToken cancellationToken = default) =>
        WriteAsync(() => _inner.GetAttachmentBytesAsync(message, attachmentIndex, cancellationToken),
            "Attachments are downloaded from the server, so opening it is possible");

    public Task<SendResult> SendAsync(OutgoingMessage message, bool saveCopyToSent, CancellationToken cancellationToken = default) =>
        WriteAsync(() => _inner.SendAsync(message, saveCopyToSent, cancellationToken), "Sending is possible");

    public Task<DraftSaveResult> SaveDraftAsync(OutgoingMessage message, CancellationToken cancellationToken = default) =>
        WriteAsync(() => _inner.SaveDraftAsync(message, cancellationToken), "Saving drafts to the server is possible");

    public Task<OutgoingMessage> LoadDraftAsync(MessageRef draft, CancellationToken cancellationToken = default) =>
        WriteAsync(() => _inner.LoadDraftAsync(draft, cancellationToken), "Opening drafts from the server is possible");

    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<T> ReadAsync<T>(Func<Task<T>> live, Func<CachedValue<T>?> copy, Action<T> save, string what)
    {
        if (!_network.IsNetworkAvailable && !_offline)
            GoOffline(new MailOperationException(MailErrorKind.Network, "This device has no network connection."));

        if (!_offline)
        {
            try
            {
                var value = await live().ConfigureAwait(false);
                Save(save, value);
                return value;
            }
            catch (MailOperationException ex) when (IsConnectivityFailure(ex))
            {
                GoOffline(ex);
            }
        }

        if (copy() is { } cached)
        {
            CopySavedAt = cached.SavedAt;
            return cached.Value;
        }
        throw new MailOperationException(_reason?.Kind ?? MailErrorKind.Network,
            $"{what} has not been saved on this device yet, and the server cannot be reached. " + Reason());
    }

    private async Task<T> WriteAsync<T>(Func<Task<T>> live, string what)
    {
        if (_offline || !_network.IsNetworkAvailable)
        {
            if (!_offline)
                GoOffline(new MailOperationException(MailErrorKind.Network, "This device has no network connection."));
            throw new MailOperationException(_reason?.Kind ?? MailErrorKind.Network, $"You are offline. {what} once HCG Mail has reconnected. " + Reason());
        }
        try
        {
            return await live().ConfigureAwait(false);
        }
        catch (MailOperationException ex) when (IsConnectivityFailure(ex))
        {
            GoOffline(ex);
            throw;
        }
    }

    private string Reason() => _reason is null ? "" : "(" + _reason.UserMessage + ")";

    private void Save<T>(Action<T> save, T value)
    {
        if (!_cache.IsEnabled)
            return;
        try
        {
            save(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not update the offline copy ({Error})", ex.GetType().Name);
        }
    }

    private static IReadOnlyList<MessageSummary> SearchCopy(FolderPage page, string query, int max)
    {
        var q = query.Trim();
        return page.Messages.Where(m =>
                m.Subject.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                m.Preview.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                (m.From is { } f && (f.Address.Contains(q, StringComparison.OrdinalIgnoreCase) || (f.Name?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false))))
            .Take(Math.Max(1, max))
            .ToList();
    }

    private MailOperationException GoOffline(MailOperationException reason)
    {
        _reason = reason;
        if (!_offline)
        {
            _offline = true;
            _logger.LogInformation("Account {AccountId} is offline ({Kind}); reading from the device's copy", AccountId, reason.Kind);
            OfflineChanged?.Invoke(this, EventArgs.Empty);
        }
        return reason;
    }

    private void GoOnline()
    {
        if (!_offline)
            return;
        _offline = false;
        _reason = null;
        CopySavedAt = null;
        _logger.LogInformation("Account {AccountId} is back online", AccountId);
        OfflineChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Gives every session the offline behaviour; registered in front of the real session factory.</summary>
public sealed class OfflineCapableSessionFactory(IMailSessionFactory inner, IMailCache cache, INetworkMonitor network, ILoggerFactory loggers) : IMailSessionFactory
{
    public IMailSession Create(MailAccount account, string password) =>
        new OfflineCapableMailSession(inner.Create(account, password), cache, network, loggers.CreateLogger<OfflineCapableMailSession>());
}
