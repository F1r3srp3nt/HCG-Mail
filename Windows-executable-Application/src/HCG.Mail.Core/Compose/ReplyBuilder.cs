using System.Globalization;
using System.Text;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.Core.Compose;

/// <summary>Builds reply, reply-all and forward messages with correct recipients, subjects and threading headers.</summary>
public static class ReplyBuilder
{
    private static readonly string[] ReplyPrefixes = ["re:", "sv:", "aw:", "antw:"];
    private static readonly string[] ForwardPrefixes = ["fwd:", "fw:", "vs:", "wg:"];

    /// <param name="ownAddresses">Addresses belonging to the user; they are never added as reply recipients.</param>
    public static OutgoingMessage Reply(MessageContent original, IReadOnlyCollection<string> ownAddresses, bool replyAll)
    {
        var (to, cc) = ReplyRecipients(original, ownAddresses, replyAll);
        return new OutgoingMessage
        {
            Kind = replyAll ? ComposeKind.ReplyAll : ComposeKind.Reply,
            To = to,
            Cc = cc,
            Subject = AddPrefix(original.Subject, "Re: ", ReplyPrefixes),
            TextBody = "\n\n" + QuoteBody(original),
            InReplyTo = original.MessageId,
            References = BuildReferences(original),
            RepliedMessage = original.Ref,
        };
    }

    public static OutgoingMessage Forward(MessageContent original, IReadOnlyList<OutgoingAttachment> attachments) => new()
    {
        Kind = ComposeKind.Forward,
        Subject = AddPrefix(original.Subject, "Fwd: ", ForwardPrefixes),
        TextBody = "\n\n" + ForwardHeaderBlock(original) + original.TextBody,
        Attachments = attachments,
        References = BuildReferences(original),
    };

    public static (IReadOnlyList<EmailAddress> To, IReadOnlyList<EmailAddress> Cc) ReplyRecipients(
        MessageContent original, IReadOnlyCollection<string> ownAddresses, bool replyAll)
    {
        bool IsOwn(EmailAddress a) => ownAddresses.Any(a.IsSameMailbox);

        var fromIsOwn = original.From is not null && IsOwn(original.From);

        // Replying to a message we sent ourselves (e.g. from the Sent folder) goes back to
        // its original recipients rather than to ourselves.
        List<EmailAddress> primary;
        if (fromIsOwn)
            primary = original.To.Where(a => !IsOwn(a)).ToList();
        else if (original.ReplyTo.Count > 0)
            primary = original.ReplyTo.Where(a => !IsOwn(a)).ToList();
        else
            primary = original.From is null ? [] : [original.From];

        var to = Distinct(primary);
        if (!replyAll)
            return (to, []);

        var extraTo = fromIsOwn ? Enumerable.Empty<EmailAddress>() : original.To.Where(a => !IsOwn(a));
        to = Distinct(to.Concat(extraTo));
        var cc = Distinct(original.Cc.Where(a => !IsOwn(a) && !to.Any(t => t.IsSameMailbox(a))));
        return (to, cc);
    }

    public static string AddPrefix(string subject, string prefix, IReadOnlyList<string> knownPrefixes)
    {
        var trimmed = (subject ?? "").Trim();
        foreach (var known in knownPrefixes)
        {
            if (trimmed.StartsWith(known, StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }
        return prefix + trimmed;
    }

    public static IReadOnlyList<string> BuildReferences(MessageContent original)
    {
        var refs = original.References.ToList();
        if (refs.Count == 0 && !string.IsNullOrEmpty(original.InReplyTo))
            refs.Add(original.InReplyTo);
        if (!string.IsNullOrEmpty(original.MessageId) && !refs.Contains(original.MessageId))
            refs.Add(original.MessageId);
        // Keep header size bounded on very long threads: the first (thread root) and the most recent ones.
        if (refs.Count > 20)
            refs = refs.Take(1).Concat(refs.Skip(refs.Count - 19)).ToList();
        return refs;
    }

    public static string QuoteBody(MessageContent original)
    {
        var sb = new StringBuilder();
        var who = original.From?.ToString() ?? "Unknown sender";
        if (original.Date == DateTimeOffset.MinValue)
            sb.Append(who).Append(" wrote:\n");
        else
            sb.Append("On ").Append(FormatDate(original.Date)).Append(", ").Append(who).Append(" wrote:\n");
        foreach (var line in Lines(original.TextBody))
            sb.Append(line.StartsWith('>') ? ">" : "> ").Append(line).Append('\n');
        return sb.ToString();
    }

    public static string ForwardHeaderBlock(MessageContent original)
    {
        var sb = new StringBuilder();
        sb.Append("---------- Forwarded message ----------\n");
        sb.Append("From: ").Append(original.From?.ToString() ?? "").Append('\n');
        sb.Append("Date: ").Append(FormatDate(original.Date)).Append('\n');
        sb.Append("Subject: ").Append(original.Subject).Append('\n');
        if (original.To.Count > 0)
            sb.Append("To: ").Append(RecipientParser.Format(original.To)).Append('\n');
        if (original.Cc.Count > 0)
            sb.Append("Cc: ").Append(RecipientParser.Format(original.Cc)).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    private static string FormatDate(DateTimeOffset date) =>
        date.ToLocalTime().ToString("ddd, d MMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static List<EmailAddress> Distinct(IEnumerable<EmailAddress> addresses)
    {
        var result = new List<EmailAddress>();
        foreach (var a in addresses)
        {
            if (!result.Any(r => r.IsSameMailbox(a)))
                result.Add(a);
        }
        return result;
    }
}
