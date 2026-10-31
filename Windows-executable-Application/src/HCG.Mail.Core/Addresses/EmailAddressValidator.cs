using HCG.Mail.Core.Accounts;

namespace HCG.Mail.Core.Addresses;

/// <summary>
/// Pragmatic address validation for user input. It accepts the addresses real mail servers
/// deliver to (including internationalized local parts and domains) while rejecting common
/// typing mistakes. It does not try to accept every obscure RFC 5322 form such as quoted local parts.
/// </summary>
public static class EmailAddressValidator
{
    private const int MaxAddressLength = 254;
    private const int MaxLocalPartLength = 64;

    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;
        if (address.Length > MaxAddressLength)
            return false;

        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
            return false;
        if (address.IndexOf('@') != at)
            return false;

        var local = address[..at];
        var domain = address[(at + 1)..];
        return IsValidLocalPart(local) && IsValidDomain(domain);
    }

    private static bool IsValidLocalPart(string local)
    {
        if (local.Length > MaxLocalPartLength)
            return false;
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in local)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
            if (c is '"' or '(' or ')' or ',' or ':' or ';' or '<' or '>' or '[' or ']' or '\\')
                return false;
        }
        return true;
    }

    private static bool IsValidDomain(string domain)
    {
        // Address literals such as user@[192.0.2.1] are rare and not accepted for sending.
        if (domain.StartsWith('[') || !domain.Contains('.'))
            return false;

        string ascii;
        try
        {
            ascii = new System.Globalization.IdnMapping().GetAscii(domain);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(ascii, out _))
            return false;
        var tld = ascii[(ascii.LastIndexOf('.') + 1)..];
        if (tld.Length < 2 || tld.All(char.IsAsciiDigit))
            return false;
        return AccountValidator.IsValidHostname(ascii);
    }
}
