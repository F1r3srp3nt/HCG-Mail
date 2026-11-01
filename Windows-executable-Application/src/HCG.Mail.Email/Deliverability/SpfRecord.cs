using System.Net;
using System.Net.Sockets;

namespace HCG.Mail.Email.Deliverability;

/// <summary>One SPF mechanism (RFC 7208 §5), e.g. <c>-all</c>, <c>include:_spf.example.net</c>, <c>ip4:192.0.2.0/24</c>.</summary>
public sealed record SpfMechanism(char Qualifier, string Name, string? Target, int? Cidr4, int? Cidr6)
{
    public bool CausesDnsLookup => Name is "include" or "a" or "mx" or "ptr" or "exists";

    public override string ToString() =>
        (Qualifier == '+' ? "" : Qualifier.ToString()) + Name + (Target is null ? "" : ":" + Target)
        + (Cidr4 is null ? "" : "/" + Cidr4) + (Cidr6 is null ? "" : "//" + Cidr6);
}

/// <summary>Parsed SPF record. Syntax problems are collected in <see cref="Errors"/>; any error makes the record a permerror.</summary>
public sealed class SpfRecord
{
    private static readonly HashSet<string> MechanismNames = ["all", "include", "a", "mx", "ptr", "ip4", "ip6", "exists"];

    private SpfRecord(string raw) => Raw = raw;

    public string Raw { get; }
    public List<SpfMechanism> Mechanisms { get; } = [];
    public string? Redirect { get; private set; }
    public List<string> Errors { get; } = [];

    /// <summary>True when a term uses macros (%{...}); HCG Mail does not expand macros and reports such records as not evaluated.</summary>
    public bool UsesMacros => Raw.Contains('%');

    public SpfMechanism? All => Mechanisms.FirstOrDefault(m => m.Name == "all");

    public static bool IsSpf(string txt) =>
        txt.Equals("v=spf1", StringComparison.OrdinalIgnoreCase) || txt.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase);

    public static SpfRecord Parse(string txt)
    {
        var record = new SpfRecord(txt.Trim());
        var terms = record.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1);
        foreach (var term in terms)
        {
            var eq = term.IndexOf('=');
            var colon = term.IndexOf(':');
            if (eq > 0 && (colon < 0 || eq < colon) && !MechanismNames.Contains(term[..eq].TrimStart('+', '-', '~', '?').ToLowerInvariant()))
            {
                var name = term[..eq].ToLowerInvariant();
                var value = term[(eq + 1)..];
                if (name == "redirect")
                {
                    if (record.Redirect is not null)
                        record.Errors.Add("more than one redirect= modifier");
                    record.Redirect = value;
                }
                continue; // exp= and unknown modifiers do not affect the result
            }

            var qualifier = '+';
            var body = term;
            if (body.Length > 0 && body[0] is '+' or '-' or '~' or '?')
            {
                qualifier = body[0];
                body = body[1..];
            }

            var nameEnd = body.IndexOfAny([':', '/']);
            var mechanism = (nameEnd < 0 ? body : body[..nameEnd]).ToLowerInvariant();
            if (!MechanismNames.Contains(mechanism))
            {
                record.Errors.Add($"unknown mechanism '{term}'");
                continue;
            }

            var rest = nameEnd < 0 ? "" : body[nameEnd..];
            string? target = null;
            int? cidr4 = null, cidr6 = null;
            if (mechanism is "ip4" or "ip6")
            {
                if (!rest.StartsWith(':'))
                {
                    record.Errors.Add($"'{term}' has no address");
                    continue;
                }
                var value = rest[1..];
                var slash = value.IndexOf('/');
                target = slash < 0 ? value : value[..slash];
                var family = mechanism == "ip4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                if (!IPAddress.TryParse(target, out var ip) || ip.AddressFamily != family)
                {
                    record.Errors.Add($"'{term}' is not a valid {mechanism} address");
                    continue;
                }
                if (slash >= 0)
                {
                    var max = mechanism == "ip4" ? 32 : 128;
                    if (!int.TryParse(value[(slash + 1)..], out var prefix) || prefix < 0 || prefix > max)
                    {
                        record.Errors.Add($"'{term}' has an invalid prefix length");
                        continue;
                    }
                    if (mechanism == "ip4")
                        cidr4 = prefix;
                    else
                        cidr6 = prefix;
                }
            }
            else
            {
                if (rest.StartsWith(':'))
                {
                    var cidrStart = rest.IndexOf('/');
                    target = cidrStart < 0 ? rest[1..] : rest[1..cidrStart];
                    rest = cidrStart < 0 ? "" : rest[cidrStart..];
                    if (target.Length == 0)
                    {
                        record.Errors.Add($"'{term}' has an empty domain");
                        continue;
                    }
                }
                if (mechanism is "include" or "exists" && target is null)
                {
                    record.Errors.Add($"'{term}' needs a domain");
                    continue;
                }
                if (rest.Length > 0)
                {
                    if (mechanism is not ("a" or "mx") || !TryParseDualCidr(rest, out cidr4, out cidr6))
                    {
                        record.Errors.Add($"'{term}' has an invalid prefix length");
                        continue;
                    }
                }
                if (mechanism == "all" && (target is not null || rest.Length > 0))
                {
                    record.Errors.Add("'all' takes no arguments");
                    continue;
                }
            }
            record.Mechanisms.Add(new SpfMechanism(qualifier, mechanism, target, cidr4, cidr6));
        }
        return record;
    }

    private static bool TryParseDualCidr(string text, out int? cidr4, out int? cidr6)
    {
        cidr4 = cidr6 = null;
        // forms: /24   //64   /24//64
        var dbl = text.IndexOf("//", StringComparison.Ordinal);
        var v4Part = dbl < 0 ? text : text[..dbl];
        var v6Part = dbl < 0 ? null : text[(dbl + 2)..];
        if (v4Part.Length > 0)
        {
            if (!v4Part.StartsWith('/') || !int.TryParse(v4Part[1..], out var p4) || p4 is < 0 or > 32)
                return false;
            cidr4 = p4;
        }
        if (v6Part is not null)
        {
            if (!int.TryParse(v6Part, out var p6) || p6 is < 0 or > 128)
                return false;
            cidr6 = p6;
        }
        return true;
    }
}

public enum SpfResult
{
    None,
    Neutral,
    Pass,
    Fail,
    SoftFail,
    TempError,
    PermError,

    /// <summary>The record uses features HCG Mail does not evaluate (macros, ptr); no conclusion.</summary>
    NotEvaluated,
}

public sealed record SpfEvaluation(SpfResult Result, string Detail);

/// <summary>
/// Evaluates RFC 7208 check_host() for an IP address against a domain's SPF record: ip4, ip6, a, mx, include,
/// exists (without macros), all and redirect, with the 10-DNS-lookup limit. Records using macros or ptr are
/// reported as <see cref="SpfResult.NotEvaluated"/> rather than guessed.
/// </summary>
public sealed class SpfEvaluator(IMailDns dns)
{
    public const int MaxDnsLookups = 10;

    public async Task<SpfEvaluation> CheckHostAsync(IPAddress ip, string domain, CancellationToken cancellationToken = default)
    {
        var lookups = 0;
        return await CheckAsync(Normalize(ip), domain.TrimEnd('.'), 0, () => ++lookups, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts the DNS-querying terms reachable from the domain's record (worst case, all branches) — RFC 7208 allows at most 10.</summary>
    public async Task<(int Count, string? Problem)> CountLookupsAsync(string domain, CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        string? problem = null;

        async Task Walk(string name, int depth)
        {
            if (depth > 12 || !visited.Add(name) || count > 50)
                return;
            var record = await GetRecordAsync(name, cancellationToken).ConfigureAwait(false);
            if (record.Record is null)
            {
                if (depth > 0 && record.Result != SpfResult.None)
                    problem ??= $"{name}: {record.Detail}";
                else if (depth > 0)
                    problem ??= $"{name} (included) has no SPF record";
                return;
            }
            foreach (var m in record.Record.Mechanisms)
            {
                if (m.CausesDnsLookup)
                    count++;
                if (m.Name == "include" && m.Target is not null && !m.Target.Contains('%'))
                    await Walk(m.Target, depth + 1).ConfigureAwait(false);
            }
            if (record.Record.Redirect is { } redirect)
            {
                count++;
                if (!redirect.Contains('%'))
                    await Walk(redirect, depth + 1).ConfigureAwait(false);
            }
        }

        await Walk(domain.TrimEnd('.'), 0).ConfigureAwait(false);
        return (count, problem);
    }

    /// <summary>Finds the domain's single SPF record (RFC 7208 §4.5). On success the result is Pass meaning "record found"; Detail is the record.</summary>
    public async Task<(SpfRecord? Record, SpfResult Result, string Detail)> GetRecordAsync(string domain, CancellationToken cancellationToken = default)
    {
        var txt = await dns.GetTxtAsync(domain, cancellationToken).ConfigureAwait(false);
        if (txt.Status == DnsLookupStatus.Failed)
            return (null, SpfResult.TempError, $"DNS lookup for {domain} failed ({txt.Error})");
        var spf = txt.Records.Where(SpfRecord.IsSpf).ToList();
        if (spf.Count == 0)
            return (null, SpfResult.None, $"{domain} has no SPF record");
        if (spf.Count > 1)
            return (null, SpfResult.PermError, $"{domain} has {spf.Count} SPF records; exactly one is allowed, so receivers treat SPF as broken");
        var record = SpfRecord.Parse(spf[0]);
        if (record.Errors.Count > 0)
            return (record, SpfResult.PermError, $"the SPF record of {domain} is invalid: {string.Join("; ", record.Errors)}");
        return (record, SpfResult.Pass, spf[0]);
    }

    private async Task<SpfEvaluation> CheckAsync(IPAddress ip, string domain, int depth, Func<int> countLookup, CancellationToken ct)
    {
        if (depth > 12)
            return new(SpfResult.PermError, "include/redirect nesting is too deep");
        var (record, status, detail) = await GetRecordAsync(domain, ct).ConfigureAwait(false);
        if (record is null || status == SpfResult.PermError)
            return new(status, detail);
        if (record.UsesMacros)
            return new(SpfResult.NotEvaluated, $"the SPF record of {domain} uses macros, which HCG Mail does not evaluate");

        foreach (var m in record.Mechanisms)
        {
            if (m.CausesDnsLookup && countLookup() > MaxDnsLookups)
                return new(SpfResult.PermError, $"more than {MaxDnsLookups} DNS lookups are needed to evaluate SPF for {domain}");

            bool? match;
            switch (m.Name)
            {
                case "all":
                    match = true;
                    break;
                case "ip4":
                case "ip6":
                    match = InRange(ip, IPAddress.Parse(m.Target!), m.Name == "ip4" ? m.Cidr4 ?? 32 : m.Cidr6 ?? 128);
                    break;
                case "a":
                    {
                        var answer = await dns.GetAddressesAsync(m.Target ?? domain, ct).ConfigureAwait(false);
                        if (answer.Status == DnsLookupStatus.Failed)
                            return new(SpfResult.TempError, $"DNS lookup for {m.Target ?? domain} failed");
                        match = answer.Records.Any(a => InRange(ip, a, a.AddressFamily == AddressFamily.InterNetwork ? m.Cidr4 ?? 32 : m.Cidr6 ?? 128));
                        break;
                    }
                case "mx":
                    {
                        var mx = await dns.GetMxAsync(m.Target ?? domain, ct).ConfigureAwait(false);
                        if (mx.Status == DnsLookupStatus.Failed)
                            return new(SpfResult.TempError, $"MX lookup for {m.Target ?? domain} failed");
                        match = false;
                        foreach (var host in mx.Records.Take(10))
                        {
                            var answer = await dns.GetAddressesAsync(host, ct).ConfigureAwait(false);
                            if (answer.Records.Any(a => InRange(ip, a, a.AddressFamily == AddressFamily.InterNetwork ? m.Cidr4 ?? 32 : m.Cidr6 ?? 128)))
                            {
                                match = true;
                                break;
                            }
                        }
                        break;
                    }
                case "exists":
                    {
                        var answer = await dns.GetAddressesAsync(m.Target!, ct).ConfigureAwait(false);
                        if (answer.Status == DnsLookupStatus.Failed)
                            return new(SpfResult.TempError, $"DNS lookup for {m.Target} failed");
                        match = answer.Records.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
                        break;
                    }
                case "include":
                    {
                        var inner = await CheckAsync(ip, m.Target!, depth + 1, countLookup, ct).ConfigureAwait(false);
                        switch (inner.Result)
                        {
                            case SpfResult.Pass:
                                match = true;
                                break;
                            case SpfResult.Fail or SpfResult.SoftFail or SpfResult.Neutral:
                                match = false;
                                break;
                            case SpfResult.None:
                                return new(SpfResult.PermError, $"include:{m.Target} points to a domain without an SPF record");
                            default:
                                return inner;
                        }
                        break;
                    }
                default: // ptr — deprecated (RFC 7208 §5.5) and not evaluated
                    return new(SpfResult.NotEvaluated, $"the SPF record of {domain} uses 'ptr', which HCG Mail does not evaluate");
            }

            if (match == true)
            {
                var result = m.Qualifier switch { '-' => SpfResult.Fail, '~' => SpfResult.SoftFail, '?' => SpfResult.Neutral, _ => SpfResult.Pass };
                return new(result, $"matched '{m}' in the SPF record of {domain}");
            }
        }

        if (record.Redirect is { } redirect)
        {
            if (countLookup() > MaxDnsLookups)
                return new(SpfResult.PermError, $"more than {MaxDnsLookups} DNS lookups are needed to evaluate SPF for {domain}");
            var redirected = await CheckAsync(ip, redirect, depth + 1, countLookup, ct).ConfigureAwait(false);
            return redirected.Result == SpfResult.None
                ? new(SpfResult.PermError, $"redirect={redirect} points to a domain without an SPF record")
                : redirected;
        }
        return new(SpfResult.Neutral, $"no mechanism in the SPF record of {domain} matched");
    }

    internal static bool InRange(IPAddress ip, IPAddress network, int prefix)
    {
        ip = Normalize(ip);
        network = Normalize(network);
        if (ip.AddressFamily != network.AddressFamily)
            return false;
        var a = ip.GetAddressBytes();
        var b = network.GetAddressBytes();
        var full = prefix / 8;
        for (var i = 0; i < full; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        var rem = prefix % 8;
        if (rem == 0)
            return true;
        var mask = (byte)(0xFF << (8 - rem));
        return (a[full] & mask) == (b[full] & mask);
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
