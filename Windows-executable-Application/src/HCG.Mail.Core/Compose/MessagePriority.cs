namespace HCG.Mail.Core.Compose;

/// <summary>
/// Message importance as shown by the recipient's mail program. Recipient systems decide whether and how to
/// display it; it has no effect on spam filtering or delivery guarantees.
/// </summary>
public enum MessagePriority
{
    Low,
    Normal,
    High,
}
