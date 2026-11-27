using MailKit.Net.Smtp;
using MimeKit;

namespace HCG.Mail.Email.Smtp;

/// <summary>
/// An SMTP client that records how far a mail transaction got. Until the server has accepted a recipient (RCPT TO),
/// no message data can have been transmitted, so a lost connection means "not sent" for certain. After that the server
/// may already hold the message, so the outcome is unknown and HCG Mail must not offer a silent resend.
/// </summary>
public sealed class TransactionTrackingSmtpClient : SmtpClient
{
    public bool RecipientAccepted { get; private set; }

    protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        RecipientAccepted = true;
        base.OnRecipientAccepted(message, mailbox, response);
    }
}
