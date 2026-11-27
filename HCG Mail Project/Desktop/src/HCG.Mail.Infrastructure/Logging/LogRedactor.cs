using System.Text.RegularExpressions;

namespace HCG.Mail.Infrastructure.Logging;

/// <summary>
/// Last line of defence for the log file. HCG Mail's own log messages contain account ids, hosts and error types
/// only, but exception text can quote server replies ("550 5.1.1 &lt;name@example.com&gt;: unknown user"), library
/// diagnostics or URLs. Before anything is written, e-mail addresses keep only their domain, and bearer tokens,
/// JWTs, SASL/AUTH payloads and password-like key/value pairs are removed.
/// </summary>
public static partial class LogRedactor
{
    public const string Redacted = "[redacted]";

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        text = AuthCommand().Replace(text, m => m.Groups["head"].Value + Redacted);
        text = Bearer().Replace(text, "Bearer " + Redacted);
        text = Jwt().Replace(text, Redacted);
        text = Secret().Replace(text, m => m.Groups["key"].Value + m.Groups["sep"].Value + Redacted);
        text = Email().Replace(text, m => "***@" + m.Groups["domain"].Value);
        return text;
    }

    // "AUTHENTICATE PLAIN AGFsaWNl…", "AUTH LOGIN dXNlcg==", "AUTH XOAUTH2 dXNlcj1…"
    [GeneratedRegex(@"(?<head>\bAUTH(?:ENTICATE)?\s+(?:PLAIN|LOGIN|XOAUTH2|OAUTHBEARER|CRAM-MD5)\s+)[A-Za-z0-9+/=._-]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex AuthCommand();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}")]
    private static partial Regex Jwt();

    // password=…, "pwd": "…", access_token=…, refresh_token: …, client_secret=…
    [GeneratedRegex(@"(?<key>\b(?:pass(?:word|wd)?|pwd|secret|client_secret|access_token|refresh_token|id_token|token)\b)(?<sep>\s*[""']?\s*[:=]\s*[""']?)[^\s""'&,;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@(?<domain>[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?)+)")]
    private static partial Regex Email();
}
