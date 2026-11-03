using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;

namespace HCG.Mail.Email.Deliverability;

public sealed record AuthenticationMethodResult(string Method, string Result, IReadOnlyDictionary<string, string> Properties, string? Reason)
{
    public string? Property(string name) => Properties.TryGetValue(name, out var v) ? v : null;
}

public sealed record DkimSignatureInfo(string Domain, string Selector, string Algorithm, CheckStatus Verification, string Detail);

public sealed record ReceivedMessageAnalysis
{
    public string? FromAddress { get; init; }
    public string? FromDomain { get; init; }
    public string? ReturnPath { get; init; }
    public string? Subject { get; init; }
    public string? MessageId { get; init; }
    public bool HasBody { get; init; }

    /// <summary>The authserv-id of the topmost Authentication-Results header — the system that received the message.</summary>
    public string? ReportedBy { get; init; }
    public IReadOnlyList<AuthenticationMethodResult> Reported { get; init; } = [];
    public IReadOnlyList<DkimSignatureInfo> Signatures { get; init; } = [];

    /// <summary>Received hops, first hop (closest to the sender) first.</summary>
    public IReadOnlyList<string> Hops { get; init; } = [];

    /// <summary>DKIM selectors on the From domain found in the signatures (to check the published keys).</summary>
    public IReadOnlyList<string> OwnDomainSelectors { get; init; } = [];

    public IReadOnlyList<CheckResult> Checks { get; init; } = [];

    public CheckStatus? Result(string method) =>
        Checks.FirstOrDefault(c => c.Title.Equals(method, StringComparison.OrdinalIgnoreCase))?.Status;
}

/// <summary>
/// Explains the authentication results of a message as its recipient received it. Users paste the original
/// message ("Show original" in Gmail, "View source" in Outlook/Apple Mail) or open a saved .eml file. The
/// recipient's own Authentication-Results are reported verbatim; HCG Mail adds alignment and, when the full message
/// is supplied, an independent DKIM verification. Nothing is sent anywhere except DNS queries for DKIM keys.
/// </summary>
public sealed class ReceivedMessageAnalyzer(IMailDns dns)
{
    private static readonly Regex FromToken = new(@"^\s*from\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ByToken = new(@"\bby\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] SpamHeaders =
        ["X-Spam-Flag", "X-Spam-Status", "X-Spam-Score", "X-Spam-Level", "X-Spamd-Result", "X-Rspamd-Score", "X-Forefront-Antispam-Report",
         "X-MS-Exchange-Organization-SCL", "X-Microsoft-Antispam"];

    public Task<ReceivedMessageAnalysis> AnalyzeTextAsync(string text, DmarcRecord? dmarc = null, CancellationToken cancellationToken = default)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return AnalyzeAsync(MessageFormatValidator.NormalizeInput(trimmed), dmarc, pasted: true, cancellationToken);
    }

    public async Task<ReceivedMessageAnalysis> AnalyzeAsync(byte[] raw, DmarcRecord? dmarc = null, bool pasted = false, CancellationToken cancellationToken = default)
    {
        var hasBody = HasBody(raw);
        if (!hasBody)
            raw = [.. raw, .. "\r\n\r\n"u8.ToArray()];

        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(new MemoryStream(raw), cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            return new ReceivedMessageAnalysis
            {
                Checks = [new("Message", CheckStatus.Failed, "The text could not be read as e-mail headers. Paste the complete original message, starting with its first header line.")],
            };
        }

        if (message.Headers.Count == 0 || (message.From.Count == 0 && !message.Headers.Contains(HeaderId.Received)))
        {
            return new ReceivedMessageAnalysis
            {
                Checks = [new("Message", CheckStatus.Failed, "No e-mail headers were found. Paste the original message (in Gmail: ⋮ → Show original), not the visible text of the message.")],
            };
        }

        var checks = new List<CheckResult>();
        var fromAddress = message.From.Mailboxes.FirstOrDefault()?.Address;
        var fromDomain = DomainNames.DomainOf(fromAddress);
        var returnPath = message.Headers[HeaderId.ReturnPath]?.Trim().Trim('<', '>');

        // 1. What the recipient's system recorded.
        var (reportedBy, reported, otherCount) = ReadAuthenticationResults(message);
        if (reported.Count == 0)
        {
            checks.Add(new("Recipient's authentication results", CheckStatus.NotTested,
                "The message has no Authentication-Results header, so the recipient's system did not record SPF/DKIM/DMARC results in it. " +
                "Gmail, Outlook.com and most hosted mail add one; test with one of those."));
        }
        else
        {
            checks.Add(new("Recipient's authentication results", CheckStatus.Verified,
                $"Reported by {reportedBy ?? "the receiving system"} (the topmost Authentication-Results header; " +
                (otherCount > 0 ? $"{otherCount} older header(s) were added earlier on the way and are not trusted" : "no older headers") + ")."));
            AddMethodChecks(checks, reported, fromDomain, dmarc);
        }

        // 2. DKIM signatures, verified independently when the whole message is available.
        var signatures = await VerifySignaturesAsync(message, hasBody && !pasted ? raw : null, hasBody, pasted, cancellationToken).ConfigureAwait(false);
        if (signatures.Count == 0)
        {
            checks.Add(new("DKIM signature", CheckStatus.Failed, "The message carries no DKIM-Signature: it was not DKIM-signed by the sending side.",
                fromDomain is null ? null : $"Ask Domene.no to enable DKIM signing for {fromDomain}, then publish the DNS record they give you."));
        }
        foreach (var signature in signatures)
            checks.Add(new($"DKIM signature d={signature.Domain} s={signature.Selector}", signature.Verification, signature.Detail));

        // 3. Alignment of the authenticated domains with From (what DMARC evaluates).
        if (fromDomain is not null)
            checks.AddRange(CheckAlignment(reported, signatures, fromDomain, returnPath, dmarc));

        // 4. Spam verdicts the recipient's filters wrote into the message, verbatim.
        foreach (var name in SpamHeaders)
        {
            foreach (var value in message.Headers.Where(h => h.Field.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value.Trim()))
                checks.Add(InterpretSpamHeader(name, value));
        }

        // 5. Received chain and the first hop's EHLO name.
        var hops = message.Headers.Where(h => h.Id == HeaderId.Received).Select(h => Regex.Replace(h.Value, @"\s+", " ").Trim()).Reverse().ToList();
        if (hops.Count > 0)
            checks.Add(CheckFirstHop(hops[0]));

        // 6. Format of the message as received.
        checks.AddRange(MessageFormatValidator.Validate(message, raw).Where(c => c.Title is "From" or "Date" or "Message-ID" or "Header fields" or "Priority headers"));

        // 7. Overall reading.
        checks.Add(Overall(checks));

        var ownSelectors = signatures.Where(s => fromDomain is not null && s.Domain.Equals(fromDomain, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Selector).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ReceivedMessageAnalysis
        {
            FromAddress = fromAddress,
            FromDomain = fromDomain,
            ReturnPath = returnPath,
            Subject = message.Subject,
            MessageId = message.MessageId,
            HasBody = hasBody,
            ReportedBy = reportedBy,
            Reported = reported,
            Signatures = signatures,
            Hops = hops.Select(Describe).ToList(),
            OwnDomainSelectors = ownSelectors,
            Checks = checks,
        };
    }

    private static (string? ReportedBy, List<AuthenticationMethodResult> Results, int OtherHeaders) ReadAuthenticationResults(MimeMessage message)
    {
        var headers = message.Headers.Where(h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).ToList();
        if (headers.Count == 0)
            headers = message.Headers.Where(h => h.Field.Equals("ARC-Authentication-Results", StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
        if (headers.Count == 0)
            return (null, [], 0);

        // Headers are prepended, so the first one in the message was added by the final receiving system. Any
        // others were added earlier on the way (or by the sender) and cannot be trusted.
        var top = headers[0];
        var bytes = top.RawValue;
        var text = Encoding.UTF8.GetString(bytes);
        if (top.Field.StartsWith("ARC-", StringComparison.OrdinalIgnoreCase))
            text = Regex.Replace(text, @"^\s*i=\d+;\s*", "");
        if (!AuthenticationResults.TryParse(Encoding.UTF8.GetBytes(text), out var parsed))
            return (null, [], headers.Count - 1);

        var results = parsed.Results.Select(r =>
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in r.Properties)
                props.TryAdd($"{p.PropertyType}.{p.Property}", p.Value);
            return new AuthenticationMethodResult(r.Method.ToLowerInvariant(), (r.Result ?? "").ToLowerInvariant(), props, r.Reason);
        }).ToList();
        return (parsed.AuthenticationServiceIdentifier, results, headers.Count - 1);
    }

    private static void AddMethodChecks(List<CheckResult> checks, List<AuthenticationMethodResult> reported, string? fromDomain, DmarcRecord? dmarc)
    {
        foreach (var method in new[] { "spf", "dkim", "dmarc" })
        {
            var entries = reported.Where(r => r.Method == method).ToList();
            if (entries.Count == 0)
            {
                checks.Add(new(method.ToUpperInvariant(), CheckStatus.NotTested, $"The recipient did not report a {method.ToUpperInvariant()} result."));
                continue;
            }

            if (method == "dkim")
            {
                var aligned = entries.FirstOrDefault(e => e.Result == "pass" && fromDomain is not null && DomainNames.Aligned(e.Property("header.d") ?? DomainNames.DomainOf(e.Property("header.i")), fromDomain, dmarc?.DkimAlignment ?? 'r'));
                var any = entries.FirstOrDefault(e => e.Result == "pass") ?? entries[0];
                var best = aligned ?? any;
                checks.Add(Interpret("DKIM", best.Result, Describe(best), fromDomain));
                continue;
            }

            var entry = entries[0];
            checks.Add(Interpret(method.ToUpperInvariant(), entry.Result, Describe(entry), fromDomain));
        }

        var compauth = reported.FirstOrDefault(r => r.Method == "compauth");
        if (compauth is not null)
        {
            checks.Add(new("Microsoft composite authentication", compauth.Result == "pass" ? CheckStatus.Verified : CheckStatus.Warning,
                $"compauth={compauth.Result}{(compauth.Reason is null ? "" : $" (reason {compauth.Reason})")} — Microsoft's combined verdict."));
        }
    }

    private static CheckResult Interpret(string method, string result, string detail, string? fromDomain)
    {
        var domain = fromDomain ?? "your domain";
        return (method, result) switch
        {
            (_, "pass") => new(method, CheckStatus.Verified, $"{method.ToLowerInvariant()}=pass. {detail}"),
            ("SPF", "softfail") => new(method, CheckStatus.Warning, $"spf=softfail: the server that delivered the message is not fully authorized by the SPF record. {detail}",
                $"Ask Domene.no which servers send your mail and make sure the SPF record of {domain} includes them."),
            ("SPF", "fail") => new(method, CheckStatus.Failed, $"spf=fail: the delivering server is not authorized by the SPF record. {detail}",
                $"Ask Domene.no which servers send your mail and make sure the SPF record of {domain} includes them."),
            ("SPF", "none") => new(method, CheckStatus.Failed, $"spf=none: no SPF record was found. {detail}", $"Publish an SPF record for {domain} (see Mail Health → SPF)."),
            ("DKIM", "none") => new(method, CheckStatus.Failed, $"dkim=none: the message was not DKIM-signed. {detail}", $"Ask Domene.no to enable DKIM signing for {domain}."),
            ("DKIM", "fail") => new(method, CheckStatus.Failed, $"dkim=fail: the signature did not verify. {detail}",
                "Check that the published DKIM key matches the provider's current key; a relay altering the message also causes this."),
            ("DMARC", "fail") => new(method, CheckStatus.Failed, $"dmarc=fail: neither SPF nor DKIM passed for a domain aligned with the From address. {detail}",
                $"Make DKIM signing use {domain} (not the provider's own domain), or make SPF pass for an envelope sender on {domain}."),
            ("DMARC", "none") => new(method, CheckStatus.Warning, $"dmarc=none: no DMARC record was found. {detail}", $"Publish a DMARC record for {domain} (see Mail Health → DMARC)."),
            (_, "temperror") => new(method, CheckStatus.Warning, $"{method.ToLowerInvariant()}=temperror: a temporary DNS error at the recipient. {detail}"),
            (_, "permerror") => new(method, CheckStatus.Failed, $"{method.ToLowerInvariant()}=permerror: the published record is broken. {detail}", "Fix the DNS record (see the matching Mail Health section)."),
            _ => new(method, CheckStatus.Warning, $"{method.ToLowerInvariant()}={result}. {detail}"),
        };
    }

    private static string Describe(AuthenticationMethodResult r)
    {
        var props = string.Join(", ", r.Properties.Select(p => $"{p.Key}={p.Value}"));
        return (props.Length > 0 ? $"({props})" : "") + (r.Reason is null ? "" : $" reason: {r.Reason}");
    }

    private async Task<List<DkimSignatureInfo>> VerifySignaturesAsync(MimeMessage message, byte[]? exactRaw, bool hasBody, bool pasted, CancellationToken ct)
    {
        var list = new List<DkimSignatureInfo>();
        var verifier = new DkimVerifier(new DnsDkimPublicKeyLocator(dns));
        foreach (var header in message.Headers.Where(h => h.Id == HeaderId.DkimSignature))
        {
            var tags = ParseTags(header.Value);
            var d = tags.GetValueOrDefault("d") ?? "?";
            var s = tags.GetValueOrDefault("s") ?? "?";
            var a = tags.GetValueOrDefault("a") ?? "?";
            if (!hasBody)
            {
                list.Add(new(d, s, a, CheckStatus.NotTested, "Only headers were supplied, so HCG Mail could not verify this signature itself. Open the complete original message (.eml) to verify it."));
                continue;
            }
            try
            {
                var source = exactRaw is null ? message : await MimeMessage.LoadAsync(new MemoryStream(exactRaw), ct).ConfigureAwait(false);
                var signatureHeader = source.Headers.First(h => h.Id == HeaderId.DkimSignature && h.Value == header.Value);
                var ok = await verifier.VerifyAsync(source, signatureHeader, ct).ConfigureAwait(false);
                list.Add(ok
                    ? new(d, s, a, CheckStatus.Verified, $"HCG Mail verified this {a} signature with the key published at {s}._domainkey.{d}.")
                    : new(d, s, a, CheckStatus.Warning,
                        $"HCG Mail could not verify this signature with the key at {s}._domainkey.{d}." +
                        (pasted ? " Pasted text often changes spacing or line breaks; open the saved .eml file instead for an exact check." : " The message may have been altered after signing.") +
                        " The recipient's own DKIM result above is authoritative."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                list.Add(new(d, s, a, CheckStatus.NotTested, $"The signature could not be checked ({Clean(ex.Message)})."));
            }
        }
        return list;
    }

    private static IEnumerable<CheckResult> CheckAlignment(List<AuthenticationMethodResult> reported, List<DkimSignatureInfo> signatures, string fromDomain, string? returnPath, DmarcRecord? dmarc)
    {
        var dkimMode = dmarc?.DkimAlignment ?? 'r';
        var spfMode = dmarc?.SpfAlignment ?? 'r';
        var modeNote = dmarc is null ? " (relaxed alignment assumed; no DMARC record supplied)" : "";

        var dkimDomains = reported.Where(r => r.Method == "dkim" && r.Result == "pass").Select(r => r.Property("header.d") ?? DomainNames.DomainOf(r.Property("header.i")))
            .Concat(reported.Count == 0 ? signatures.Where(s => s.Verification == CheckStatus.Verified).Select(s => s.Domain) : [])
            .Where(d => d is not null).Select(d => d!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var signedDomains = signatures.Select(s => s.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (dkimDomains.Count == 0 && signedDomains.Count == 0)
            yield return new("DKIM alignment", CheckStatus.Failed, "No DKIM signature, so DKIM cannot align with the From domain.", $"Ask Domene.no to sign mail from {fromDomain} with DKIM.");
        else if (dkimDomains.Any(d => DomainNames.Aligned(d, fromDomain, dkimMode)))
            yield return new("DKIM alignment", CheckStatus.Verified, $"A passing DKIM signature uses {dkimDomains.First(d => DomainNames.Aligned(d, fromDomain, dkimMode))}, aligned with From ({fromDomain}){modeNote}.");
        else if (signedDomains.Any(d => DomainNames.Aligned(d, fromDomain, dkimMode)))
            yield return new("DKIM alignment", CheckStatus.NotTested, $"A signature uses {fromDomain}, but its result could not be confirmed{modeNote}.");
        else
            yield return new("DKIM alignment", CheckStatus.Failed,
                $"The message is signed with {string.Join(", ", signedDomains.Concat(dkimDomains).Distinct(StringComparer.OrdinalIgnoreCase))}, not with {fromDomain}. DMARC ignores signatures from other domains{modeNote}.",
                $"Ask Domene.no to sign mail from {fromDomain} with a DKIM key for {fromDomain} (custom-domain DKIM).");

        var mailFrom = reported.FirstOrDefault(r => r.Method == "spf")?.Property("smtp.mailfrom");
        var envelopeDomain = DomainNames.DomainOf(mailFrom) ?? DomainNames.DomainOf(returnPath);
        if (envelopeDomain is null)
            yield return new("SPF alignment", CheckStatus.NotTested, "The envelope sender (Return-Path) is not shown in these headers.");
        else if (DomainNames.Aligned(envelopeDomain, fromDomain, spfMode))
            yield return new("SPF alignment", CheckStatus.Verified, $"The envelope sender domain {envelopeDomain} is aligned with From ({fromDomain}){modeNote}.");
        else
            yield return new("SPF alignment", CheckStatus.Warning,
                $"The envelope sender domain {envelopeDomain} differs from From ({fromDomain}), so SPF cannot satisfy DMARC; only DKIM can{modeNote}.");
    }

    private static CheckResult InterpretSpamHeader(string name, string value)
    {
        var shown = $"{name}: {Clean(value)} (written by the recipient's filter — reported as is, not interpreted as a score by HCG Mail)";
        var lower = value.ToLowerInvariant();
        var spam = name switch
        {
            "X-Spam-Flag" => lower.StartsWith("yes", StringComparison.Ordinal),
            "X-Spam-Status" => lower.StartsWith("yes", StringComparison.Ordinal),
            "X-Forefront-Antispam-Report" => lower.Contains("sfv:spm") || Regex.IsMatch(lower, @"scl:([5-9])"),
            "X-MS-Exchange-Organization-SCL" => int.TryParse(value.Trim(), out var scl) && scl >= 5,
            _ => (bool?)null,
        };
        return spam switch
        {
            true => new($"Spam filter: {name}", CheckStatus.Warning, shown + ". The filter classified the message as spam."),
            false when name is "X-Spam-Flag" or "X-Spam-Status" => new($"Spam filter: {name}", CheckStatus.Verified, shown + ". The filter did not classify it as spam."),
            _ => new($"Spam filter: {name}", CheckStatus.NotTested, shown + "."),
        };
    }

    private static CheckResult CheckFirstHop(string hop)
    {
        var m = FromToken.Match(hop);
        if (!m.Success)
            return new("First hop EHLO name", CheckStatus.NotTested, "The first Received header does not show the sending computer's EHLO name.");
        var helo = m.Groups[1].Value.Trim('(', ')');
        var valid = helo.StartsWith('[') || (helo.Contains('.') && !helo.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !helo.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) && !helo.EndsWith(".home", StringComparison.OrdinalIgnoreCase));
        return valid
            ? new("First hop EHLO name", CheckStatus.Verified, $"The sending program introduced itself as '{helo}' (a domain name or address literal, as RFC 5321 requires).")
            : new("First hop EHLO name", CheckStatus.Warning,
                $"The sending program introduced itself as '{helo}' — not a fully qualified name or address literal (RFC 5321 §4.1.1.1). Some filters score this. " +
                "HCG Mail versions before this fix sent the computer's name here; messages sent now use an address literal.");
    }

    private static CheckResult Overall(List<CheckResult> checks)
    {
        var spf = checks.FirstOrDefault(c => c.Title == "SPF")?.Status;
        var dkim = checks.FirstOrDefault(c => c.Title == "DKIM")?.Status;
        var dmarc = checks.FirstOrDefault(c => c.Title == "DMARC")?.Status;
        if (spf is null && dkim is null && dmarc is null)
            return new("Conclusion", CheckStatus.NotTested, "No recipient authentication results — nothing can be concluded about authentication from these headers.");
        if (dmarc == CheckStatus.Verified && dkim == CheckStatus.Verified && spf is CheckStatus.Verified)
            return new("Conclusion", CheckStatus.Verified,
                "SPF, DKIM and DMARC all passed at the recipient. If this message still went to spam, the cause is the recipient's own assessment of the sending domain's or " +
                "server's reputation, or of the content — which HCG Mail cannot see or change. See Mail Health → Reputation for the tools receivers provide.");
        if (dmarc == CheckStatus.Failed || dkim == CheckStatus.Failed || spf == CheckStatus.Failed)
            return new("Conclusion", CheckStatus.Failed,
                "Authentication failed at the recipient — the most likely reason for spam placement. Fix the failed items above at your DNS host or with your mail provider; HCG Mail cannot change DNS or the provider's signing.");
        return new("Conclusion", CheckStatus.Warning, "Authentication is incomplete at the recipient; see the warnings above.");
    }

    private static string Describe(string hop)
    {
        var from = FromToken.Match(hop);
        var by = ByToken.Match(hop);
        return from.Success || by.Success
            ? $"from {(from.Success ? from.Groups[1].Value : "?")} by {(by.Success ? by.Groups[1].Value.TrimEnd(';') : "?")}"
            : hop.Length > 120 ? hop[..120] + "…" : hop;
    }

    private static Dictionary<string, string> ParseTags(string value)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
                tags.TryAdd(part[..eq].Trim(), Regex.Replace(part[(eq + 1)..], @"\s+", ""));
        }
        return tags;
    }

    private static bool HasBody(byte[] raw)
    {
        var text = Encoding.ASCII.GetString(raw, 0, Math.Min(raw.Length, 2_000_000));
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return split >= 0 && text[(split + 4)..].Trim().Length > 0;
    }

    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}

/// <summary>Looks up DKIM keys through <see cref="IMailDns"/> for MimeKit's verifier.</summary>
internal sealed class DnsDkimPublicKeyLocator(IMailDns dns) : DkimPublicKeyLocatorBase
{
    public override AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector, CancellationToken cancellationToken = default) =>
        LocatePublicKeyAsync(methods, domain, selector, cancellationToken).GetAwaiter().GetResult();

    public override async Task<AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain, string selector, CancellationToken cancellationToken = default)
    {
        if (!methods.Split(':').Any(m => m.Trim().Equals("dns/txt", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException($"unsupported key lookup method '{methods}'");
        var name = $"{selector}._domainkey.{domain}";
        var answer = await dns.GetTxtAsync(name, cancellationToken).ConfigureAwait(false);
        var record = answer.Records.FirstOrDefault(r => r.Contains("p=", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(answer.Status == DnsLookupStatus.Failed ? $"DNS lookup for {name} failed" : $"no key published at {name}");
        return GetPublicKey(record);
    }
}
