using System.Text;

namespace HCG.Mail.Core.Addresses;

public sealed record RecipientParseResult(IReadOnlyList<EmailAddress> Addresses, IReadOnlyList<string> Invalid)
{
    public bool IsValid => Invalid.Count == 0;
}

/// <summary>
/// Parses recipient fields such as <c>Ola Nordmann &lt;ola@example.no&gt;, "Doe, Jane" &lt;jane@example.com&gt;; kari@example.no</c>.
/// Commas and semicolons separate recipients unless they appear inside quotes or angle brackets.
/// </summary>
public static class RecipientParser
{
    public static RecipientParseResult Parse(string? input)
    {
        var addresses = new List<EmailAddress>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return new(addresses, invalid);

        foreach (var token in Split(input))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
                continue;
            if (TryParseSingle(trimmed, out var address))
            {
                if (!addresses.Any(a => a.IsSameMailbox(address)))
                    addresses.Add(address);
            }
            else
            {
                invalid.Add(trimmed);
            }
        }
        return new(addresses, invalid);
    }

    public static string Format(IEnumerable<EmailAddress> addresses) => string.Join(", ", addresses.Select(a => a.ToString()));

    public static bool TryParseSingle(string text, out EmailAddress address)
    {
        address = null!;
        text = text.Trim();

        var open = text.LastIndexOf('<');
        if (open >= 0)
        {
            var close = text.IndexOf('>', open);
            if (close < 0 || text[(close + 1)..].Trim().Length > 0)
                return false;
            var addr = text[(open + 1)..close].Trim();
            var name = Unquote(text[..open].Trim());
            if (!EmailAddressValidator.IsValid(addr))
                return false;
            address = new EmailAddress(string.IsNullOrWhiteSpace(name) ? null : name, addr);
            return true;
        }

        if (text.Contains('>') || text.Contains('"'))
            return false;
        if (!EmailAddressValidator.IsValid(text))
            return false;
        address = new EmailAddress(null, text);
        return true;
    }

    private static IEnumerable<string> Split(string input)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        var inAngle = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\\' && inQuotes && i + 1 < input.Length)
            {
                current.Append(c).Append(input[++i]);
                continue;
            }
            if (c == '"' && !inAngle)
                inQuotes = !inQuotes;
            else if (c == '<' && !inQuotes)
                inAngle = true;
            else if (c == '>' && !inQuotes)
                inAngle = false;
            else if ((c == ',' || c == ';' || c == '\n' || c == '\r') && !inQuotes && !inAngle)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        yield return current.ToString();
    }

    private static string Unquote(string name)
    {
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
            name = name[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return name.Trim();
    }
}
