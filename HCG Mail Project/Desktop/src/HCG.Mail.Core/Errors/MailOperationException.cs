namespace HCG.Mail.Core.Errors;

public enum MailErrorKind
{
    Unknown,
    Cancelled,
    Dns,
    Network,
    Timeout,
    Tls,
    Certificate,

    /// <summary>The certificate is otherwise valid but its revocation status could not be determined. Not evidence of revocation.</summary>
    RevocationUnavailable,
    Authentication,
    ServerRejected,
    Protocol,
    FolderNotFound,
    MessageNotFound,
    NotSupported,
    LocalFile,

    /// <summary>The provider is limiting requests (HTTP 429/503 with Retry-After). Nothing was changed by the refused request.</summary>
    Throttled,

    /// <summary>The sign-in has expired or consent was revoked; the user must reconnect the account (Microsoft accounts).</summary>
    ReconnectRequired,
}

/// <summary>
/// A mail failure translated into a message that is safe and useful to show the user.
/// <see cref="UserMessage"/> never contains credentials.
/// </summary>
public sealed class MailOperationException : Exception
{
    public MailOperationException(MailErrorKind kind, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        Kind = kind;
        UserMessage = userMessage;
    }

    public MailErrorKind Kind { get; }

    /// <summary>
    /// The request may have reached the server before the connection failed, so whether it took effect is unknown
    /// (for a send: the message may or may not have been accepted). Never retried automatically.
    /// </summary>
    public bool OutcomeUnknown { get; init; }
    public string UserMessage { get; }

    /// <summary>Connection-level failures where reconnecting later may succeed.</summary>
    public bool IsTransient => Kind is MailErrorKind.Network or MailErrorKind.Timeout or MailErrorKind.Dns or MailErrorKind.Throttled;
}
