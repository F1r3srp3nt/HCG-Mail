using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Email.Mime;
using MimeKit;
using MessagePriority = HCG.Mail.Core.Compose.MessagePriority;

namespace HCG.Mail.Email.Deliverability;

/// <summary>
/// RFC 5322 / MIME checks on a serialized message: required and single-instance header fields, line limits, bare
/// line breaks, 7-bit transport safety (DKIM robustness), hidden Bcc and consistent priority headers.
/// </summary>
public static class MessageFormatValidator
{
    /// <summary>Header fields RFC 5322 §3.6 allows at most once (plus the importance fields HCG Mail writes).</summary>
    private static readonly string[] SingleInstance =
        ["From", "Sender", "Reply-To", "To", "Cc", "Bcc", "Subject", "Date", "Message-ID", "In-Reply-To", "References", "MIME-Version", "Content-Type",
         "Content-Transfer-Encoding", "Importance", "X-Priority", "Priority"];

    /// <summary>
    /// Serializes a message exactly as MailKit transmits it over SMTP (CRLF line endings, Bcc not transmitted).
    /// </summary>
    public static byte[] SerializeForTransport(MimeMessage message)
    {
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        options.HiddenHeaders.Add(HeaderId.Bcc);
        options.HiddenHeaders.Add(HeaderId.ResentBcc);
        using var stream = new MemoryStream();
        message.WriteTo(options, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Builds the message HCG Mail would send for this account (with non-ASCII text, a long line, an attachment and the
    /// given priority) and validates it as transmitted. Nothing is sent.
    /// </summary>
    public static IReadOnlyList<CheckResult> ValidateOwnOutput(MailAccount account, MessagePriority priority = MessagePriority.High)
    {
        var sample = new OutgoingMessage
        {
            To = [new EmailAddress(account.DisplayName, account.EmailAddress)],
            Bcc = [new EmailAddress(null, account.EmailAddress)],
            Subject = "HCG Mail format check — æøå",
            TextBody = "Hei,\r\ndette er en formatsjekk med æ, ø og å.\rEn linje som er lengre enn tillatt: " + new string('x', 1100) + "\n\nMvh",
            Attachments = [OutgoingAttachment.FromBytes("årsrapport 2026.pdf", "application/pdf", new byte[2048])],
            Priority = priority,
        };
        var mime = MimeConverter.ToMimeMessage(sample, account);
        var raw = SerializeForTransport(mime);
        var results = Validate(MimeMessage.Load(new MemoryStream(raw)), raw).ToList();
        results.Add(new("EHLO name", CheckStatus.Verified,
            "HCG Mail introduces itself to the outgoing server with the address literal of the connection (e.g. EHLO [192.168.1.20]), as RFC 5321 requires " +
            "when a computer has no public host name; the computer's name is not sent."));
        results.Add(new("Tracking and receipts", CheckStatus.Verified,
            "No tracking pixels, read-receipt (Disposition-Notification-To) or return-receipt headers are added."));
        return results;
    }

    public static IReadOnlyList<CheckResult> Validate(MimeMessage message, byte[] raw)
    {
        var results = new List<CheckResult>();
        var headers = message.Headers;

        // From / Sender
        var from = message.From.Mailboxes.ToList();
        results.Add(from.Count switch
        {
            1 => new CheckResult("From", CheckStatus.Verified, $"One sender address: {from[0].Address}."),
            0 => new CheckResult("From", CheckStatus.Failed, "The message has no From address."),
            _ when headers.Contains(HeaderId.Sender) => new CheckResult("From", CheckStatus.Warning, "Several From addresses (with a Sender field) — unusual and often filtered."),
            _ => new CheckResult("From", CheckStatus.Failed, "Several From addresses without a Sender field (RFC 5322 §3.6.2)."),
        });

        // Date
        results.Add(headers.Contains(HeaderId.Date) && message.Date != DateTimeOffset.MinValue
            ? new("Date", CheckStatus.Verified, $"Date: {headers[HeaderId.Date]?.Trim()} (time zone offset {message.Date:zzz}).")
            : new("Date", CheckStatus.Failed, "The Date field is missing or cannot be read (RFC 5322 §3.6.1 requires it)."));

        // Message-ID
        var messageId = message.MessageId;
        var fromDomain = DomainNames.DomainOf(from.FirstOrDefault()?.Address);
        if (string.IsNullOrEmpty(messageId) || !messageId.Contains('@'))
            results.Add(new("Message-ID", CheckStatus.Failed, "The Message-ID is missing or malformed; many filters score this as spam-like."));
        else
        {
            var idDomain = DomainNames.DomainOf(messageId);
            results.Add(fromDomain is not null && idDomain is not null && DomainNames.OrganizationalDomain(idDomain) != DomainNames.OrganizationalDomain(fromDomain)
                ? new("Message-ID", CheckStatus.Warning, $"<{messageId}> uses the domain {idDomain}, not the sender's domain {fromDomain}.")
                : new("Message-ID", CheckStatus.Verified, $"<{messageId}> (unique, on the sender's domain)."));
        }

        // Duplicated single-instance fields
        var duplicated = SingleInstance.Where(name => headers.Count(h => h.Field.Equals(name, StringComparison.OrdinalIgnoreCase)) > 1).ToList();
        results.Add(duplicated.Count == 0
            ? new("Header fields", CheckStatus.Verified, "No header field that may appear only once is duplicated.")
            : new("Header fields", CheckStatus.Failed, $"Duplicated header fields: {string.Join(", ", duplicated)} (RFC 5322 §3.6)."));

        // Subject
        results.Add(string.IsNullOrWhiteSpace(message.Subject)
            ? new("Subject", CheckStatus.Warning, "The subject is empty; spam filters treat empty subjects as a weak spam signal.")
            : new("Subject", CheckStatus.Verified, "A subject is present (non-ASCII text is RFC 2047 encoded)."));

        // MIME-Version
        results.Add(headers.Contains(HeaderId.MimeVersion)
            ? new("MIME-Version", CheckStatus.Verified, "MIME-Version: 1.0 is present.")
            : new("MIME-Version", CheckStatus.Warning, "MIME-Version is missing although the message uses MIME."));

        // Bcc must never be transmitted
        results.Add(headers.Contains(HeaderId.Bcc)
            ? new("Bcc", CheckStatus.Failed, "A Bcc header is present in the transmitted message — hidden recipients are visible to everyone.")
            : new("Bcc", CheckStatus.Verified, "Bcc recipients are not included in the transmitted headers."));

        // Raw-format checks
        var (longest, bareBreaks, eightBit) = ScanRaw(raw);
        results.Add(longest > 998
            ? new("Line length", CheckStatus.Failed, $"A line is {longest} characters long; SMTP allows at most 998 (RFC 5322 §2.1.1).")
            : new("Line length", CheckStatus.Verified, $"Longest line: {longest} characters (limit 998)."));
        results.Add(bareBreaks
            ? new("Line endings", CheckStatus.Failed, "The message contains a bare CR or LF; many servers now reject these (SMTP smuggling protection).")
            : new("Line endings", CheckStatus.Verified, "All lines end in CRLF."));
        results.Add(eightBit
            ? new("Transfer encoding", CheckStatus.Warning, "The message contains raw 8-bit data. A relay that converts it to 7-bit would invalidate the DKIM signature.")
            : new("Transfer encoding", CheckStatus.Verified, "7-bit clean (quoted-printable/base64): DKIM signatures survive any relay."));

        // Priority headers
        var importance = headers[HeaderId.Importance]?.Trim().ToLowerInvariant();
        var xPriority = headers[HeaderId.XPriority]?.Trim();
        if (importance is null && xPriority is null)
            results.Add(new("Priority headers", CheckStatus.Verified, "No importance headers (shown as normal priority)."));
        else
        {
            var fromX = xPriority is { Length: > 0 } x ? x[0] switch { '1' or '2' => "high", '4' or '5' => "low", _ => "normal" } : null;
            results.Add(importance is not null && fromX is not null && importance != fromX
                ? new("Priority headers", CheckStatus.Warning, $"Importance: {importance} contradicts X-Priority: {xPriority}.")
                : new("Priority headers", CheckStatus.Verified, $"Importance: {importance ?? "—"}, X-Priority: {xPriority ?? "—"}, Priority: {headers[HeaderId.Priority]?.Trim() ?? "—"}."));
        }

        // Read receipts / tracking requests
        if (headers.Contains(HeaderId.DispositionNotificationTo) || headers.Contains("Return-Receipt-To"))
            results.Add(new("Read receipts", CheckStatus.Warning, "The message requests a read receipt."));

        return results;
    }

    private static (int Longest, bool BareBreaks, bool EightBit) ScanRaw(byte[] raw)
    {
        var longest = 0;
        var current = 0;
        var bare = false;
        var eight = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    longest = Math.Max(longest, current);
                    current = 0;
                    i++;
                    continue;
                }
                bare = true;
            }
            else if (b == '\n')
            {
                bare = true;
                longest = Math.Max(longest, current);
                current = 0;
                continue;
            }
            if (b > 127)
                eight = true;
            current++;
        }
        return (Math.Max(longest, current), bare, eight);
    }

    /// <summary>Reads pasted or opened message text as bytes, normalizing line endings (copy/paste changes them).</summary>
    public static byte[] NormalizeInput(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        return Encoding.UTF8.GetBytes(normalized);
    }
}
