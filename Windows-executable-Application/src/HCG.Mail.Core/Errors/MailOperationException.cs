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
    public string UserMessage { get; }

    /// <summary>Connection-level failures where reconnecting later may succeed.</summary>
    public bool IsTransient => Kind is MailErrorKind.Network or MailErrorKind.Timeout or MailErrorKind.Dns;
}
