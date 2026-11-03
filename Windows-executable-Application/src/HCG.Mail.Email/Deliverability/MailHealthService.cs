using System.Text.RegularExpressions;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;

namespace HCG.Mail.Email.Deliverability;

public sealed record ReputationLink(string Name, string Url, string Description);

public sealed record MailHealthReport(string Domain, DateTimeOffset CheckedAt, IReadOnlyList<CheckSection> Sections, DmarcRecord? Dmarc);

/// <summary>
/// Assembles the Mail Health dashboard. Everything shown is either a published DNS record, a reply from the
/// account's own servers, or a result the recipient's system wrote into a message — HCG Mail does not compute
/// deliverability scores or inbox-placement rates, because it has no data to compute them from.
/// </summary>
public sealed class MailHealthService(IMailDns dns)
{
    public static readonly IReadOnlyList<ReputationLink> ReputationLinks =
    [
        new("Google Postmaster Tools", "https://postmaster.google.com/",
            "Gmail's own view of your domain's spam rate, reputation and authentication. Requires verifying the domain with a DNS TXT record; data appears only with enough daily volume to Gmail."),
        new("Microsoft SNDS", "https://sendersupport.olc.protection.outlook.com/snds/",
            "Outlook.com data for sending IP addresses. Useful to your provider (Domene.no) who owns the outgoing servers' IP addresses."),
        new("Microsoft sender support", "https://sendersupport.olc.protection.outlook.com/pm/",
            "Request delisting if Outlook.com/Hotmail rejects or junks mail from your provider's servers."),
        new("Spamhaus reputation check", "https://check.spamhaus.org/",
            "Shows whether a domain or IP address is on a Spamhaus blocklist, with delisting instructions."),
        new("Cisco Talos reputation", "https://talosintelligence.com/reputation_center/",
            "Email reputation of IP addresses and domains used by many corporate filters."),
        new("MXToolbox blocklist check", "https://mxtoolbox.com/blacklists.aspx",
            "Checks an IP address or domain against many public blocklists at once."),
    ];

    private readonly DomainAuthenticationChecker _checker = new(dns);

    public async Task<MailHealthReport> RunAsync(
        MailAccount account,
        IReadOnlyCollection<string> dkimSelectors,
        ReceivedMessageAnalysis? analysis,
        IReadOnlyList<DeliveryRecord> history,
        CancellationToken cancellationToken = default)
    {
        var domain = DomainNames.DomainOf(account.EmailAddress) ?? account.EmailAddress;
        var selectors = dkimSelectors.Concat(analysis?.OwnDomainSelectors ?? []).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var spf = await _checker.CheckSpfAsync(domain, cancellationToken).ConfigureAwait(false);
        var dkim = await _checker.CheckDkimAsync(domain, selectors, cancellationToken).ConfigureAwait(false);
        var (dmarcChecks, dmarc) = await _checker.CheckDmarcAsync(domain, cancellationToken).ConfigureAwait(false);
        var mx = await _checker.CheckMxAsync(domain, cancellationToken).ConfigureAwait(false);
        var server = await _checker.CheckSendingServerAsync(domain, account.Smtp.Host, cancellationToken).ConfigureAwait(false);

        var spfSection = new CheckSection("SPF", "Which servers may send mail for your domain (TXT record on the domain).", spf);
        var dkimSection = new CheckSection("DKIM", "Public keys receivers use to verify your provider's signatures (selector._domainkey records).", dkim);

        var alignment = new List<CheckResult>(dmarcChecks);
        if (analysis is null)
            alignment.Add(new("Alignment in delivered mail", CheckStatus.NotTested,
                "Alignment can only be seen in a message as it was received.",
                "Send a message to a Gmail or Outlook.com address you own, open it there, copy the original message and analyze it in the 'Analyze a received message' tab."));
        else
            alignment.AddRange(analysis.Checks.Where(c => c.Title is "DKIM alignment" or "SPF alignment" or "DMARC"));
        var dmarcSection = new CheckSection("DMARC policy and alignment", "Your published policy, and whether SPF/DKIM results match the visible From domain.", alignment);

        var serverSection = new CheckSection("Sending server", $"DNS of your outgoing server {account.Smtp.Host} and your domain's receiving servers.", [mx, .. server]);
        var historySection = new CheckSection("SMTP history", "Replies from your outgoing server to recent sends from this computer.", CheckHistory(history));
        var formatSection = new CheckSection("Message format", "The message HCG Mail builds, checked as it is transmitted (nothing is sent).", MessageFormatValidator.ValidateOwnOutput(account));
        var reputationSection = new CheckSection("Sender reputation", "Reputation is measured by each receiver and by blocklist operators; HCG Mail does not query them.",
            [new("Reputation", CheckStatus.NotTested,
                "HCG Mail has no access to receivers' reputation data and does not estimate it. Use the services below; most need you or your provider to register the domain or IP addresses.")]);

        var summary = new List<CheckResult>
        {
            Summarize("SPF", spfSection),
            Summarize("DKIM", dkimSection),
            Summarize("DMARC", new CheckSection("", "", dmarcChecks)),
            analysis is null
                ? new("As seen by a recipient", CheckStatus.NotTested, "No received message analyzed yet.", "Use 'Analyze a received message' to see what a recipient actually recorded.")
                : analysis.Checks.FirstOrDefault(c => c.Title == "Conclusion") is { } conclusion
                    ? conclusion with { Title = "As seen by a recipient" }
                    : new("As seen by a recipient", CheckStatus.NotTested, "The analyzed message had no recipient results."),
        };

        return new MailHealthReport(domain, DateTimeOffset.Now,
        [
            new CheckSection("Domain authentication", $"Summary for {domain}.", summary),
            spfSection,
            dkimSection,
            dmarcSection,
            serverSection,
            historySection,
            formatSection,
            reputationSection,
        ], dmarc);
    }

    private static CheckResult Summarize(string title, CheckSection section)
    {
        var worst = section.Checks.Where(c => c.Status == section.Summary).Select(c => c.Detail).FirstOrDefault() ?? "";
        return new(title, section.Summary, section.Summary == CheckStatus.Verified ? $"All {title} checks passed." : worst,
            section.Checks.FirstOrDefault(c => c.Action is not null && c.Status == section.Summary)?.Action);
    }

    /// <summary>Interprets the account's recent SMTP replies. Bounces sent later by recipients' servers arrive as e-mail and are not included.</summary>
    public static IReadOnlyList<CheckResult> CheckHistory(IReadOnlyList<DeliveryRecord> history)
    {
        if (history.Count == 0)
            return [new("Recent sends", CheckStatus.NotTested, "No messages have been sent from this account on this computer since delivery history was added.")];

        var results = new List<CheckResult>();
        var counts = history.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        string Count(DeliveryState s) => counts.TryGetValue(s, out var n) ? n.ToString() : "0";
        var failures = history.Count(r => r.IsFailure || r.State == DeliveryState.Submitted);
        results.Add(new("Recent sends", failures == 0 ? CheckStatus.Verified : CheckStatus.Warning,
            $"Last {history.Count} send(s) since {history.Min(r => r.Timestamp):d}: {Count(DeliveryState.Accepted)} accepted, {Count(DeliveryState.Rejected)} rejected, " +
            $"{Count(DeliveryState.Deferred)} deferred, {Count(DeliveryState.Submitted)} with no final reply, {Count(DeliveryState.NotSent)} not sent (connection or sign-in). " +
            "'Accepted' means your outgoing server took the message; delivery to the recipient and inbox placement are not known. " +
            "Later bounces from recipients' servers arrive as e-mail in your Inbox and are not counted here."));

        foreach (var record in history.Where(r => r.State is DeliveryState.Rejected or DeliveryState.Deferred or DeliveryState.Submitted).Take(8))
        {
            var reply = record.ServerReply is null ? "" : $" Server reply: {record.SmtpStatusCode} {record.ServerReply}.";
            results.Add(new($"{record.Timestamp:g} — {record.StateLabel}",
                record.State == DeliveryState.Rejected ? CheckStatus.Failed : CheckStatus.Warning,
                (record.ErrorMessage ?? record.Explanation) + reply,
                ExplainReply(record.SmtpStatusCode, record.ServerReply)));
        }
        return results;
    }

    /// <summary>Plain-language meaning of common SMTP status codes (RFC 3463 enhanced codes), when one is recognizable.</summary>
    public static string? ExplainReply(int? code, string? reply)
    {
        var enhanced = reply is null ? null : Regex.Match(reply, @"\b([245])\.(\d{1,3})\.(\d{1,3})\b") is { Success: true } m ? m.Value : null;
        return enhanced switch
        {
            "5.7.26" or "5.7.27" or "5.7.25" => "The receiver requires SPF/DKIM/DMARC to pass for your domain. Check Mail Health → SPF, DKIM and DMARC.",
            "5.7.1" or "5.7.0" or "5.7.606" or "5.7.511" => "Refused by policy — often a spam or reputation decision. The reply text says which; contact your provider with the full reply.",
            "5.7.8" or "5.7.57" => "The server did not accept the sign-in for sending. Check the account's username and password.",
            "5.1.1" or "5.1.10" => "The recipient address does not exist. Check its spelling.",
            "5.2.2" or "4.2.2" => "The recipient's mailbox is full.",
            "5.3.4" or "5.2.3" => "The message is too large for the server. Send smaller attachments or a link.",
            _ when enhanced?.StartsWith("4.7.", StringComparison.Ordinal) == true =>
                "Temporarily refused by policy (rate limit or greylisting). Wait and send again; repeated deferrals should be reported to your provider.",
            _ when code is >= 400 and < 500 => "A temporary refusal — try again later.",
            _ => null,
        };
    }
}
