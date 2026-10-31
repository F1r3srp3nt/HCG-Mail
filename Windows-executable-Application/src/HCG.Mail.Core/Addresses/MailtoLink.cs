namespace HCG.Mail.Core.Addresses;

public sealed record MailtoLink(IReadOnlyList<EmailAddress> To, string Subject, string Body);

/// <summary>Parses <c>mailto:</c> links (RFC 6068) from message content into a new-message draft. Invalid addresses are dropped.</summary>
public static class MailtoParser
{
    public static MailtoLink? TryParse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !link.TrimStart().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = link.Trim()[7..];
        var query = "";
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        var recipients = new List<string> { Decode(rest) };
        string subject = "", body = "";
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = (eq >= 0 ? pair[..eq] : pair).ToLowerInvariant();
            var value = eq >= 0 ? Decode(pair[(eq + 1)..]) : "";
            switch (key)
            {
                case "to":
                    recipients.Add(value);
                    break;
                case "subject":
                    subject = value.Replace('\r', ' ').Replace('\n', ' ');
                    break;
                case "body":
                    body = value;
                    break;
                // cc/bcc from a link are ignored on purpose: a link must not silently add hidden recipients.
            }
        }

        var parsed = RecipientParser.Parse(string.Join(",", recipients));
        return new MailtoLink(parsed.Addresses, subject, body);
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
