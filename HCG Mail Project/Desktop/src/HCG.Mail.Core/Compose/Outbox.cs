namespace HCG.Mail.Core.Compose;

public enum OutboxKind
{
    /// <summary>A message to send.</summary>
    Send,

    /// <summary>A draft to copy to the server's Drafts folder.</summary>
    Draft,
}

public enum OutboxState
{
    /// <summary>Waiting for the connection; sent (or uploaded) automatically when the account reconnects.</summary>
    Waiting,

    /// <summary>Being handed to the server right now. Found in this state at start-up, it means HCG Mail stopped mid-send.</summary>
    Sending,

    /// <summary>
    /// The connection failed while the server was receiving it, or HCG Mail stopped mid-send: it may already have been
    /// sent. Never sent again automatically; the user checks Sent and decides.
    /// </summary>
    NeedsReview,

    /// <summary>The server refused it (for example a rejected recipient). Kept for the user to fix or discard.</summary>
    Failed,
}

/// <summary>
/// A message or draft kept on this device until it can be handed to the server. Attachments are stored with it, so moving or
/// deleting the original files afterwards does not matter.
/// </summary>
public sealed record OutboxItem(
    string Id,
    string AccountId,
    OutboxKind Kind,
    OutgoingMessage Message,
    DateTimeOffset QueuedAt,
    OutboxState State = OutboxState.Waiting,
    string? LastError = null,
    int Attempts = 0,
    bool SaveCopyToSent = true)
{
    public string Description => string.IsNullOrWhiteSpace(Message.Subject) ? "(no subject)" : Message.Subject;
}

/// <summary>Persistent store of <see cref="OutboxItem"/>s; survives restarts and crashes.</summary>
public interface IOutboxStore
{
    IReadOnlyList<OutboxItem> Load();

    void Put(OutboxItem item);

    void Remove(string id);

    /// <summary>Removes every item of the account and its Outbox key.</summary>
    void DeleteAccount(string accountId);
}

public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly Dictionary<string, OutboxItem> _items = [];

    public IReadOnlyList<OutboxItem> Load()
    {
        lock (_items)
            return _items.Values.OrderBy(i => i.QueuedAt).ToList();
    }

    public void Put(OutboxItem item)
    {
        lock (_items)
            _items[item.Id] = item;
    }

    public void Remove(string id)
    {
        lock (_items)
            _items.Remove(id);
    }

    public void DeleteAccount(string accountId)
    {
        lock (_items)
            foreach (var id in _items.Values.Where(i => i.AccountId == accountId).Select(i => i.Id).ToList())
                _items.Remove(id);
    }
}
