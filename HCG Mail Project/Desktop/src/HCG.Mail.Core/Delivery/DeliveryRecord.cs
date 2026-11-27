using System.Text.Json.Serialization;
using HCG.Mail.Core.Compose;

namespace HCG.Mail.Core.Delivery;

/// <summary>
/// What HCG Mail actually knows about a send. SMTP only tells the client whether the account's own outgoing
/// server took the message; it says nothing about delivery to the recipient's mailbox or inbox placement.
/// </summary>
public enum DeliveryState
{
    /// <summary>The message never reached the SMTP transaction (connection, TLS or sign-in failed).</summary>
    NotSent,

    /// <summary>The message was handed to the SMTP server but no final reply arrived — the outcome is unknown.</summary>
    Submitted,

    /// <summary>The SMTP server replied 2xx after the message data: it took responsibility for delivery.</summary>
    Accepted,

    /// <summary>The SMTP server replied 4xx: temporarily refused, not sent.</summary>
    Deferred,

    /// <summary>The SMTP server replied 5xx: permanently refused, not sent.</summary>
    Rejected,
}

/// <summary>One send attempt. Contains no subject, body or attachment data.</summary>
public sealed record DeliveryRecord
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string AccountId { get; init; }
    public required string AccountAddress { get; init; }

    /// <summary>The account's outgoing server as host:port.</summary>
    public required string SmtpServer { get; init; }

    public required DeliveryState State { get; init; }
    public string? MessageId { get; init; }
    public int RecipientCount { get; init; }
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;

    /// <summary>The SMTP reply code of the final response (e.g. 250, 451, 550), when one was received.</summary>
    public int? SmtpStatusCode { get; init; }

    /// <summary>The server's reply text as received (truncated), e.g. "2.0.0 Ok: queued as 4Xy9" or a rejection reason.</summary>
    public string? ServerReply { get; init; }

    /// <summary>For failures: the explanation shown to the user.</summary>
    public string? ErrorMessage { get; init; }

    [JsonIgnore]
    public bool IsFailure => State is DeliveryState.NotSent or DeliveryState.Deferred or DeliveryState.Rejected;

    [JsonIgnore]
    public string StateLabel => Label(State);

    [JsonIgnore]
    public string Explanation => Explain(State);

    public static string Label(DeliveryState state) => state switch
    {
        DeliveryState.NotSent => "Not sent",
        DeliveryState.Submitted => "Submitted — no reply from server",
        DeliveryState.Accepted => "Accepted by outgoing server",
        DeliveryState.Deferred => "Deferred (temporary refusal)",
        DeliveryState.Rejected => "Rejected",
        _ => state.ToString(),
    };

    public static string Explain(DeliveryState state) => state switch
    {
        DeliveryState.NotSent =>
            "The message was not handed to the outgoing server (connection, security or sign-in failed). Nothing was sent.",
        DeliveryState.Submitted =>
            "The message was handed to the outgoing server, but the connection ended before the server replied. It may or may not " +
            "have been accepted — check the Sent folder or with the recipient before sending it again.",
        DeliveryState.Accepted =>
            "Your outgoing server accepted the message and is now responsible for delivering it. This is not confirmation that it reached " +
            "the recipient: delivery confirmation is not available for this account, and inbox or spam placement is unknown.",
        DeliveryState.Deferred =>
            "Your outgoing server temporarily refused the message (a 4xx reply). It was not sent. HCG Mail does not retry automatically; try again later.",
        DeliveryState.Rejected =>
            "Your outgoing server permanently refused the message (a 5xx reply). It was not sent. The server's reason is shown.",
        _ => "",
    };

    /// <summary>Delivery confirmation requires provider events (e.g. a delivery API); none is supported for IMAP/SMTP accounts.</summary>
    public const string DeliveryConfirmationText = "Not available — standard SMTP gives no delivery confirmation and no supported provider integration is configured.";

    public const string InboxPlacementText = "Unknown — only the recipient's mail system knows whether a message went to the inbox or spam.";
}

/// <summary>Recent send attempts, kept locally for the Mail Health view.</summary>
public interface IDeliveryLog
{
    void Add(DeliveryRecord record);

    /// <summary>Newest first.</summary>
    IReadOnlyList<DeliveryRecord> GetRecent(string? accountId = null, int max = 100);

    void Clear(string? accountId = null);
}

public sealed class InMemoryDeliveryLog : IDeliveryLog
{
    private readonly List<DeliveryRecord> _records = [];
    private readonly Lock _lock = new();

    public void Add(DeliveryRecord record)
    {
        lock (_lock)
            _records.Add(record);
    }

    public IReadOnlyList<DeliveryRecord> GetRecent(string? accountId = null, int max = 100)
    {
        lock (_lock)
            return _records.Where(r => accountId is null || r.AccountId == accountId).OrderByDescending(r => r.Timestamp).Take(max).ToList();
    }

    public void Clear(string? accountId = null)
    {
        lock (_lock)
            _records.RemoveAll(r => accountId is null || r.AccountId == accountId);
    }
}
