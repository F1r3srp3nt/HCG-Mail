using System.Collections.ObjectModel;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Services;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.App.Services;

/// <summary>
/// Messages and drafts that could not reach the server yet. A message goes to the Outbox only when it certainly did not reach
/// the server (offline, or the connection failed before the server received it); it is sent automatically, one at a time,
/// when its account reconnects. Anything whose outcome is uncertain — the connection broke while the server was receiving it,
/// or HCG Mail stopped mid-send — is never sent again by itself: it waits for the user to check Sent and decide.
/// </summary>
public sealed class OutboxService
{
    private readonly IOutboxStore _store;
    private readonly INotificationService _notifications;
    private readonly ILogger _logger;
    private readonly HashSet<string> _flushing = [];

    public OutboxService(IOutboxStore store, INotificationService notifications, ILogger<OutboxService> logger)
    {
        _store = store;
        _notifications = notifications;
        _logger = logger;
        Reload();
    }

    public ObservableCollection<OutboxItem> Items { get; } = [];

    public event EventHandler? Changed;

    public int CountFor(string accountId) => Items.Count(i => i.AccountId == accountId);

    public IReadOnlyList<OutboxItem> ItemsFor(string accountId) => Items.Where(i => i.AccountId == accountId).ToList();

    /// <summary>At start-up: anything that was being sent when HCG Mail stopped may have been sent, so it waits for review.</summary>
    public void RecoverInterrupted()
    {
        foreach (var item in Items.Where(i => i.State == OutboxState.Sending).ToList())
        {
            Save(item with
            {
                State = OutboxState.NeedsReview,
                LastError = "HCG Mail stopped while this message was being sent, so it may already have been sent. Check your Sent folder, then send it again or discard it.",
            });
        }
    }

    /// <summary>Puts a message or draft in the Outbox. A newer draft of the same message replaces the older one; sending replaces a waiting draft.</summary>
    public OutboxItem Enqueue(string accountId, OutgoingMessage message, OutboxKind kind, bool saveCopyToSent)
    {
        // Attachments are read now, so the Outbox does not depend on the original files any more.
        var materialized = message with
        {
            Attachments = message.Attachments.Select(a => a.Data is not null ? a : OutgoingAttachment.FromBytes(a.FileName, a.ContentType, File.ReadAllBytes(a.FilePath!))).ToList(),
        };
        if (message.MessageId is { } id)
        {
            foreach (var older in Items.Where(i => i.AccountId == accountId && i.Kind == OutboxKind.Draft && i.State == OutboxState.Waiting && i.Message.MessageId == id).ToList())
                Remove(older);
        }
        var item = new OutboxItem(Guid.NewGuid().ToString("N"), accountId, kind, materialized, DateTimeOffset.Now, SaveCopyToSent: saveCopyToSent);
        Save(item);
        _logger.LogInformation("Queued {Kind} in the Outbox for account {AccountId}", kind, accountId);
        return item;
    }

    public void Discard(OutboxItem item) => Remove(item);

    /// <summary>The user checked Sent and wants a message that needs review (or failed) sent again.</summary>
    public void Requeue(OutboxItem item) => Save(item with { State = OutboxState.Waiting, LastError = null });

    /// <summary>Hands everything waiting for this account to the server, oldest first. Stops at the first connection problem.</summary>
    public async Task<int> FlushAsync(AccountViewModel account)
    {
        var session = account.Session;
        if (session is null || (session as IOfflineAwareSession)?.IsOffline == true)
            return 0;
        var accountId = account.Account.Id;
        if (!_flushing.Add(accountId))
            return 0;
        var handed = 0;
        try
        {
            foreach (var item in ItemsFor(accountId).Where(i => i.State == OutboxState.Waiting).OrderBy(i => i.QueuedAt).ToList())
            {
                if (!Items.Contains(item))
                    continue;
                // Skip a draft when the same message is waiting to be sent anyway.
                if (item.Kind == OutboxKind.Draft && Items.Any(i => i.Kind == OutboxKind.Send && i.AccountId == accountId && i.Message.MessageId == item.Message.MessageId))
                {
                    Remove(item);
                    continue;
                }

                var sending = item with { State = OutboxState.Sending, Attempts = item.Attempts + 1 };
                Save(sending); // persisted before the attempt: a crash now leaves it for review, never for a blind resend
                try
                {
                    if (item.Kind == OutboxKind.Send)
                    {
                        var result = await session.SendAsync(item.Message, item.SaveCopyToSent);
                        Remove(sending);
                        handed++;
                        _notifications.Success("Sent from the Outbox",
                            $"“{item.Description}” was handed to the outgoing server." + (result.Warning is { } w ? " " + w : ""));
                    }
                    else
                    {
                        await session.SaveDraftAsync(item.Message);
                        Remove(sending);
                        handed++;
                        _notifications.Info("Draft saved", $"“{item.Description}” was copied to your Drafts folder.");
                    }
                }
                catch (MailOperationException ex) when (ex.OutcomeUnknown)
                {
                    Save(sending with { State = OutboxState.NeedsReview, LastError = ex.UserMessage + " It may already have been sent; check your Sent folder before sending it again." });
                    _notifications.Warning("Outbox: sending result unknown", $"“{item.Description}” may already have been sent. It was not sent again; check your Sent folder.");
                    return handed;
                }
                catch (MailOperationException ex) when (ReconnectPolicy.ModeFor(ex.Kind) != RecoveryMode.Never)
                {
                    Save(sending with { State = OutboxState.Waiting, LastError = ex.UserMessage });
                    return handed; // connection gone again: try later
                }
                catch (MailOperationException ex)
                {
                    Save(sending with { State = OutboxState.Failed, LastError = ex.UserMessage });
                    _notifications.Error("Outbox: not sent", $"“{item.Description}”: {ex.UserMessage}");
                    if (ex.Kind is MailErrorKind.Authentication or MailErrorKind.ReconnectRequired)
                        return handed;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Save(sending with { State = OutboxState.Failed, LastError = "An attachment could not be read: " + ex.Message });
                }
            }
        }
        finally
        {
            _flushing.Remove(accountId);
        }
        return handed;
    }

    public void RemoveAccount(string accountId)
    {
        foreach (var item in Items.Where(i => i.AccountId == accountId).ToList())
            Remove(item);
    }

    private void Reload()
    {
        Items.Clear();
        foreach (var item in _store.Load())
            Items.Add(item);
    }

    private void Save(OutboxItem item)
    {
        _store.Put(item);
        var index = IndexOf(item.Id);
        if (index >= 0)
            Items[index] = item;
        else
            Items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Remove(OutboxItem item)
    {
        _store.Remove(item.Id);
        var index = IndexOf(item.Id);
        if (index >= 0)
            Items.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Id == id)
                return i;
        return -1;
    }
}
