using System.Security.Cryptography;

namespace HCG.Mail.Email.Deliverability;

/// <summary>A DMARC policy record (RFC 7489 §6.3) published at <c>_dmarc.&lt;domain&gt;</c>.</summary>
public sealed record DmarcRecord(
    string Raw,
    string Policy,
    string? SubdomainPolicy,
    int Percent,
    char DkimAlignment,
    char SpfAlignment,
    IReadOnlyList<string> AggregateReports,
    IReadOnlyList<string> ForensicReports)
{
    public static bool IsDmarc(string txt) => txt.TrimStart().StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)
        && (txt.TrimStart().Length == 8 || txt.TrimStart()[8] is ';' or ' ');

    public static DmarcRecord? TryParse(string txt, out string? error)
    {
        error = null;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var first = true;
        foreach (var part in txt.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                error = $"'{part}' is not a tag=value pair";
                return null;
            }
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (first && !(name.Equals("v", StringComparison.OrdinalIgnoreCase) && value.Equals("DMARC1", StringComparison.OrdinalIgnoreCase)))
            {
                error = "the record must start with v=DMARC1";
                return null;
            }
            first = false;
            tags.TryAdd(name, value);
        }

        if (!tags.TryGetValue("p", out var p) || !IsPolicy(p))
        {
            error = "the required policy tag p= (none, quarantine or reject) is missing or invalid";
            return null;
        }
        var sp = tags.TryGetValue("sp", out var s) && IsPolicy(s) ? s.ToLowerInvariant() : null;
        var pct = tags.TryGetValue("pct", out var pctText) && int.TryParse(pctText, out var n) && n is >= 0 and <= 100 ? n : 100;
        return new DmarcRecord(
            txt.Trim(),
            p.ToLowerInvariant(),
            sp,
            pct,
            Alignment(tags, "adkim"),
            Alignment(tags, "aspf"),
            Uris(tags, "rua"),
            Uris(tags, "ruf"));
    }

    private static bool IsPolicy(string value) => value.ToLowerInvariant() is "none" or "quarantine" or "reject";

    private static char Alignment(Dictionary<string, string> tags, string name) =>
        tags.TryGetValue(name, out var v) && v.Equals("s", StringComparison.OrdinalIgnoreCase) ? 's' : 'r';

    private static List<string> Uris(Dictionary<string, string> tags, string name) =>
        tags.TryGetValue(name, out var v) ? v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() : [];
}

/// <summary>A DKIM public-key record (RFC 6376 §3.6.1) published at <c>&lt;selector&gt;._domainkey.&lt;domain&gt;</c>.</summary>
public sealed record DkimKeyRecord(string Raw, string KeyType, bool Revoked, bool Testing, int? KeyBits, string? Error)
{
    public static DkimKeyRecord Parse(string txt)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in txt.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
                tags.TryAdd(part[..eq].Trim(), string.Concat(part[(eq + 1)..].Where(c => !char.IsWhiteSpace(c))));
        }

        var keyType = tags.TryGetValue("k", out var k) ? k.ToLowerInvariant() : "rsa";
        var testing = tags.TryGetValue("t", out var t) && t.Split(':').Any(f => f.Equals("y", StringComparison.OrdinalIgnoreCase));
        if (tags.TryGetValue("v", out var v) && !v.Equals("DKIM1", StringComparison.OrdinalIgnoreCase))
            return new(txt, keyType, false, testing, null, $"unsupported version v={v}");
        if (!tags.TryGetValue("p", out var p))
            return new(txt, keyType, false, testing, null, "the record has no public key (p=)");
        if (p.Length == 0)
            return new(txt, keyType, true, testing, null, null);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(p);
        }
        catch (FormatException)
        {
            return new(txt, keyType, false, testing, null, "the public key is not valid base64");
        }

        if (keyType == "ed25519")
            return new(txt, keyType, false, testing, key.Length == 32 ? 256 : null, key.Length == 32 ? null : "an Ed25519 key must be 32 bytes");
        if (keyType != "rsa")
            return new(txt, keyType, false, testing, null, $"unknown key type k={keyType}");

        try
        {
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportSubjectPublicKeyInfo(key, out _);
            }
            catch (CryptographicException)
            {
                rsa.ImportRSAPublicKey(key, out _); // some publishers use the bare PKCS#1 form
            }
            return new(txt, keyType, false, testing, rsa.KeySize, null);
        }
        catch (CryptographicException)
        {
            return new(txt, keyType, false, testing, null, "the public key could not be read as an RSA key");
        }
    }
}

/// <summary>Domain helpers for DMARC alignment.</summary>
public static class DomainNames
{
    /// <summary>
    /// Organizational domain approximated as the last two labels (e.g. mail.example.com → example.com). HCG Mail
    /// does not ship the Public Suffix List, so for names under multi-label suffixes (example.co.uk) this is approximate.
    /// </summary>
    public static string OrganizationalDomain(string domain)
    {
        var labels = domain.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        return labels.Length <= 2 ? string.Join('.', labels) : string.Join('.', labels[^2..]);
    }

    public static bool Aligned(string? authenticated, string fromDomain, char mode)
    {
        if (string.IsNullOrWhiteSpace(authenticated))
            return false;
        var a = authenticated.Trim().TrimEnd('.').ToLowerInvariant();
        var f = fromDomain.Trim().TrimEnd('.').ToLowerInvariant();
        return mode == 's' ? a == f : OrganizationalDomain(a) == OrganizationalDomain(f);
    }

    public static string? DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        var at = address.LastIndexOf('@');
        var domain = (at >= 0 ? address[(at + 1)..] : address).Trim().Trim('<', '>', '"', ' ').TrimEnd('.');
        return domain.Length == 0 ? null : domain.ToLowerInvariant();
    }
}
