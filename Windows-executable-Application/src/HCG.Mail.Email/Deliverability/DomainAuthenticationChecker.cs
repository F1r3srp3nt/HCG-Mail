using System.Net;

namespace HCG.Mail.Email.Deliverability;

/// <summary>
/// Read-only DNS checks for a sending domain: SPF, DKIM keys, DMARC policy, MX and the outgoing server's DNS.
/// HCG Mail can only report what is published; changing DNS is done at the domain's DNS host (e.g. Domene.no).
/// </summary>
public sealed class DomainAuthenticationChecker(IMailDns dns)
{
    private readonly SpfEvaluator _spf = new(dns);

    // ---- SPF ----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CheckResult>> CheckSpfAsync(string domain, CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>();
        var (record, status, detail) = await _spf.GetRecordAsync(domain, cancellationToken).ConfigureAwait(false);
        switch (status)
        {
            case SpfResult.TempError:
                results.Add(new("SPF record", CheckStatus.NotTested, $"Could not look up DNS: {detail}.", "Check the internet connection and try again."));
                return results;
            case SpfResult.None:
                results.Add(new("SPF record", CheckStatus.Failed, $"No SPF record (TXT \"v=spf1 …\") is published for {domain}.",
                    $"At your DNS host, add one TXT record on {domain} that authorizes your mail provider's outgoing servers and ends in \"-all\" or \"~all\". " +
                    "Ask your mail provider (Domene.no) for the exact include: value to use — do not guess it."));
                return results;
            case SpfResult.PermError when record is null:
                results.Add(new("SPF record", CheckStatus.Failed, detail + ".", $"Merge them into a single TXT record on {domain}; receivers ignore SPF when there is more than one."));
                return results;
            case SpfResult.PermError:
                results.Add(new("SPF record", CheckStatus.Failed, $"{detail}. Record: {record!.Raw}", "Correct the syntax of the SPF record at your DNS host."));
                return results;
        }

        results.Add(new("SPF record", CheckStatus.Verified, $"One SPF record is published: {record!.Raw}"));

        var all = record.All;
        if (all is null && record.Redirect is null)
            results.Add(new("SPF default policy", CheckStatus.Warning, "The record does not end in an 'all' mechanism, so mail from unlisted servers is 'neutral'.",
                "End the record with \"~all\" (soft fail) or \"-all\" (fail) once all legitimate sending services are listed."));
        else if (all is not null)
        {
            var (s, text, action) = all.Qualifier switch
            {
                '-' => (CheckStatus.Verified, "'-all': mail from servers that are not listed fails SPF.", (string?)null),
                '~' => (CheckStatus.Verified, "'~all': mail from servers that are not listed soft-fails SPF (a common, safe setting together with DMARC).", null),
                '?' => (CheckStatus.Warning, "'?all': mail from servers that are not listed is neutral — SPF gives receivers no protection.", "Change '?all' to '~all' or '-all'."),
                _ => (CheckStatus.Failed, "'+all' authorizes every server on the internet to send as this domain.", "Replace '+all' with '~all' or '-all' immediately."),
            };
            results.Add(new("SPF default policy", s, text, action));
        }
        else
            results.Add(new("SPF default policy", CheckStatus.Verified, $"The policy is delegated with redirect={record.Redirect}."));

        if (record.Mechanisms.Any(m => m.Name == "ptr"))
            results.Add(new("SPF 'ptr' mechanism", CheckStatus.Warning, "The record uses 'ptr', which RFC 7208 deprecates; some receivers skip it.", "Replace 'ptr' with ip4:/ip6:/include: terms."));
        if (record.UsesMacros)
            results.Add(new("SPF macros", CheckStatus.NotTested, "The record uses macros (%{…}); HCG Mail does not evaluate them, so lookup counting and IP checks may be incomplete."));

        var (count, problem) = await _spf.CountLookupsAsync(domain, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
            results.Add(new("SPF includes", CheckStatus.Failed, $"An included record is broken: {problem}.", "Fix or remove that include: term."));
        results.Add(count > SpfEvaluator.MaxDnsLookups
            ? new("SPF DNS-lookup limit", CheckStatus.Failed, $"Evaluating the record can take {count} DNS lookups; receivers stop at {SpfEvaluator.MaxDnsLookups} and treat SPF as broken (permerror).",
                "Remove unused include: terms or replace them with ip4:/ip6: ranges.")
            : new("SPF DNS-lookup limit", CheckStatus.Verified, $"Evaluating the record needs at most {count} DNS lookup(s); receivers allow {SpfEvaluator.MaxDnsLookups}."));
        return results;
    }

    /// <summary>Evaluates SPF for specific addresses (e.g. the outgoing server's), as a receiver would.</summary>
    public async Task<CheckResult> CheckSpfAuthorizesAsync(string domain, string serverName, IReadOnlyList<IPAddress> addresses, CancellationToken cancellationToken = default)
    {
        if (addresses.Count == 0)
            return new("SPF authorizes the outgoing server", CheckStatus.NotTested, $"The address of {serverName} is unknown.");
        var lines = new List<string>();
        var worst = CheckStatus.Verified;
        foreach (var ip in addresses)
        {
            var evaluation = await _spf.CheckHostAsync(ip, domain, cancellationToken).ConfigureAwait(false);
            lines.Add($"{ip}: {evaluation.Result.ToString().ToLowerInvariant()} ({evaluation.Detail})");
            var status = evaluation.Result switch
            {
                SpfResult.Pass => CheckStatus.Verified,
                SpfResult.NotEvaluated or SpfResult.TempError => CheckStatus.NotTested,
                _ => CheckStatus.Warning,
            };
            if (status == CheckStatus.Warning || (status == CheckStatus.NotTested && worst == CheckStatus.Verified))
                worst = status;
        }
        return new("SPF authorizes the outgoing server", worst,
            $"{serverName} — {string.Join("; ", lines)}. Note: providers often relay outgoing mail through other servers than the one you connect to; " +
            "the authoritative result is the 'spf=' value in the headers of a message you sent, as received.",
            worst == CheckStatus.Warning ? "Analyze a received message (Mail Health → Analyze a received message). If it shows spf=fail or softfail, ask your provider which servers to authorize." : null);
    }

    // ---- DKIM ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CheckResult>> CheckDkimAsync(string domain, IReadOnlyCollection<string> selectors, CancellationToken cancellationToken = default)
    {
        if (selectors.Count == 0)
        {
            return
            [
                new("DKIM key", CheckStatus.NotTested,
                    "DKIM keys are published under a selector name that cannot be discovered from DNS alone.",
                    "Analyze a message you sent, as received by another mailbox — HCG Mail reads the selector from its DKIM-Signature — or enter the selector " +
                    "your provider gave you. If your messages carry no DKIM-Signature at all, ask Domene.no to enable DKIM signing for " + domain + "."),
            ];
        }

        var results = new List<CheckResult>();
        foreach (var selector in selectors)
        {
            var name = $"{selector}._domainkey.{domain}";
            var txt = await dns.GetTxtAsync(name, cancellationToken).ConfigureAwait(false);
            if (txt.Status == DnsLookupStatus.Failed)
            {
                results.Add(new($"DKIM key '{selector}'", CheckStatus.NotTested, $"Could not look up {name}: {txt.Error}."));
                continue;
            }
            var keys = txt.Records.Where(r => r.Contains("p=", StringComparison.OrdinalIgnoreCase)).ToList();
            if (keys.Count == 0)
            {
                results.Add(new($"DKIM key '{selector}'", CheckStatus.Failed, $"No DKIM key is published at {name}.",
                    $"Publish the DKIM public key your provider gives you as a TXT (or CNAME, if the provider hosts it) record at {name}."));
                continue;
            }
            var key = DkimKeyRecord.Parse(keys[0]);
            if (key.Error is not null)
                results.Add(new($"DKIM key '{selector}'", CheckStatus.Failed, $"The key at {name} is invalid: {key.Error}.", "Republish the key exactly as your provider supplies it."));
            else if (key.Revoked)
                results.Add(new($"DKIM key '{selector}'", CheckStatus.Failed, $"The key at {name} is revoked (empty p=); signatures with this selector fail.",
                    "Ask your provider which selector is currently in use and make sure its key is published."));
            else
            {
                var bits = key.KeyBits;
                var (status, note, action) = key.KeyType == "ed25519"
                    ? (CheckStatus.Verified, "Ed25519 key.", (string?)null)
                    : bits switch
                    {
                        < 1024 => (CheckStatus.Failed, $"{bits}-bit RSA key — too short; many receivers ignore it.", "Ask your provider for a 2048-bit key."),
                        < 2048 => (CheckStatus.Warning, $"{bits}-bit RSA key — accepted, but 2048 bits is recommended.", "Ask your provider to rotate to a 2048-bit key."),
                        _ => (CheckStatus.Verified, $"{bits}-bit RSA key.", null),
                    };
                results.Add(new($"DKIM key '{selector}'", status, $"Published at {name}. {note}", action));
                if (key.Testing)
                    results.Add(new($"DKIM test mode '{selector}'", CheckStatus.Warning, "The key is flagged t=y (testing): receivers may treat signatures as unsigned.",
                        "Remove t=y once signing works."));
            }
        }
        return results;
    }

    // ---- DMARC --------------------------------------------------------------------------------

    public async Task<(IReadOnlyList<CheckResult> Results, DmarcRecord? Record)> CheckDmarcAsync(string domain, CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>();
        var queried = domain;
        var txt = await dns.GetTxtAsync($"_dmarc.{domain}", cancellationToken).ConfigureAwait(false);
        var org = DomainNames.OrganizationalDomain(domain);
        if (txt.Status != DnsLookupStatus.Failed && !txt.Records.Any(DmarcRecord.IsDmarc) && org != domain.ToLowerInvariant())
        {
            queried = org;
            txt = await dns.GetTxtAsync($"_dmarc.{org}", cancellationToken).ConfigureAwait(false);
        }
        if (txt.Status == DnsLookupStatus.Failed)
        {
            results.Add(new("DMARC record", CheckStatus.NotTested, $"Could not look up DNS: {txt.Error}.", "Check the internet connection and try again."));
            return (results, null);
        }

        var records = txt.Records.Where(DmarcRecord.IsDmarc).ToList();
        if (records.Count == 0)
        {
            results.Add(new("DMARC record", CheckStatus.Failed, $"No DMARC record is published at _dmarc.{domain}.",
                $"At your DNS host, add a TXT record named _dmarc.{domain} with the value \"v=DMARC1; p=none; rua=mailto:<an address you read>\". " +
                "Start with p=none to collect reports; move to p=quarantine once SPF and DKIM pass and align for all your legitimate mail."));
            return (results, null);
        }
        if (records.Count > 1)
        {
            results.Add(new("DMARC record", CheckStatus.Failed, $"_dmarc.{queried} has {records.Count} DMARC records; receivers ignore DMARC when there is more than one.",
                "Keep exactly one DMARC TXT record."));
            return (results, null);
        }

        var record = DmarcRecord.TryParse(records[0], out var error);
        if (record is null)
        {
            results.Add(new("DMARC record", CheckStatus.Failed, $"The DMARC record at _dmarc.{queried} is invalid: {error}. Record: {records[0]}", "Correct the record at your DNS host."));
            return (results, null);
        }

        var inherited = queried != domain ? $" (inherited from the organizational domain {queried}; approximate — HCG Mail does not use the Public Suffix List)" : "";
        results.Add(new("DMARC record", CheckStatus.Verified, $"Published{inherited}: {record.Raw}"));
        results.Add(record.Policy switch
        {
            "reject" => new CheckResult("DMARC policy", CheckStatus.Verified, "p=reject: receivers are asked to reject mail that fails DMARC."),
            "quarantine" => new CheckResult("DMARC policy", CheckStatus.Verified, "p=quarantine: receivers are asked to treat mail that fails DMARC as suspicious."),
            _ => new CheckResult("DMARC policy", CheckStatus.Warning,
                "p=none: monitoring only. This does not hurt delivery of your own authenticated mail, but gives no protection against spoofing.",
                "After the aggregate reports show that all legitimate mail passes, change to p=quarantine."),
        });
        if (record.Percent < 100)
            results.Add(new("DMARC coverage", CheckStatus.Warning, $"pct={record.Percent}: the policy applies to only {record.Percent}% of failing mail.", "Raise pct to 100 when ready."));
        results.Add(record.AggregateReports.Count == 0
            ? new("DMARC reports", CheckStatus.Warning, "No rua= address: you receive no aggregate reports showing who sends mail as your domain and whether it passes.",
                "Add rua=mailto:<address> to the record.")
            : new("DMARC reports", CheckStatus.Verified, $"Aggregate reports go to {string.Join(", ", record.AggregateReports)}."));
        results.Add(new("DMARC alignment mode", CheckStatus.Verified,
            $"DKIM alignment {(record.DkimAlignment == 's' ? "strict" : "relaxed")}, SPF alignment {(record.SpfAlignment == 's' ? "strict" : "relaxed")}."));
        return (results, record);
    }

    // ---- MX and the outgoing server -------------------------------------------------------------

    public async Task<CheckResult> CheckMxAsync(string domain, CancellationToken cancellationToken = default)
    {
        var mx = await dns.GetMxAsync(domain, cancellationToken).ConfigureAwait(false);
        return mx.Status switch
        {
            DnsLookupStatus.Found => new("Receiving servers (MX)", CheckStatus.Verified, $"{domain} receives mail at {string.Join(", ", mx.Records)}."),
            DnsLookupStatus.NotFound => new("Receiving servers (MX)", CheckStatus.Warning, $"{domain} publishes no MX record; some receivers distrust sender domains that cannot receive replies.",
                "Publish the MX records your mail provider specifies."),
            _ => new("Receiving servers (MX)", CheckStatus.NotTested, $"Could not look up DNS: {mx.Error}."),
        };
    }

    public async Task<IReadOnlyList<CheckResult>> CheckSendingServerAsync(string domain, string smtpHost, CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>();
        var addresses = await dns.GetAddressesAsync(smtpHost, cancellationToken).ConfigureAwait(false);
        if (addresses.Status != DnsLookupStatus.Found)
        {
            results.Add(new("Outgoing server address", addresses.Status == DnsLookupStatus.Failed ? CheckStatus.NotTested : CheckStatus.Failed,
                addresses.Status == DnsLookupStatus.Failed ? $"Could not look up {smtpHost}: {addresses.Error}." : $"{smtpHost} does not resolve to an address.",
                "Check the outgoing server name in the account settings."));
            return results;
        }
        results.Add(new("Outgoing server address", CheckStatus.Verified, $"{smtpHost} → {string.Join(", ", addresses.Records)}"));

        foreach (var ip in addresses.Records.Take(4))
        {
            var ptr = await dns.GetPtrAsync(ip, cancellationToken).ConfigureAwait(false);
            if (ptr.Status == DnsLookupStatus.Failed)
            {
                results.Add(new($"Reverse DNS of {ip}", CheckStatus.NotTested, $"Lookup failed: {ptr.Error}."));
                continue;
            }
            if (ptr.Records.Count == 0)
            {
                results.Add(new($"Reverse DNS of {ip}", CheckStatus.Warning, $"{ip} has no reverse DNS (PTR) name. Receivers distrust mail from such addresses.",
                    "This is configured by your mail provider, not in your domain's DNS — report it to Domene.no."));
                continue;
            }
            var confirmed = false;
            foreach (var name in ptr.Records)
            {
                var forward = await dns.GetAddressesAsync(name, cancellationToken).ConfigureAwait(false);
                if (forward.Records.Any(a => a.Equals(ip)))
                {
                    confirmed = true;
                    break;
                }
            }
            results.Add(confirmed
                ? new($"Reverse DNS of {ip}", CheckStatus.Verified, $"{ip} ↔ {string.Join(", ", ptr.Records)} (forward-confirmed).")
                : new($"Reverse DNS of {ip}", CheckStatus.Warning, $"{ip} → {string.Join(", ", ptr.Records)}, but that name does not resolve back to {ip}.",
                    "Configured by your mail provider — report it to Domene.no."));
        }

        results.Add(await CheckSpfAuthorizesAsync(domain, smtpHost, addresses.Records, cancellationToken).ConfigureAwait(false));
        return results;
    }
}
