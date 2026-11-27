using HCG.Mail.Core.Errors;

namespace HCG.Mail.Core.Network;

/// <summary>
/// Turns Network Doctor observations into one verdict that separates a restricted network from wrong credentials, a server
/// outage and a defect in HCG Mail. The rules go from the outside in: the device's own connection, the network's internet
/// access (captive portal, DNS, certificate interception), whether mail ports in general are open on this network (measured
/// against a large public provider), then the account's own servers, their certificates and finally the sign-in.
/// </summary>
public static class NetworkClassifier
{
    private const string NoBypass =
        "HCG Mail never turns off certificate checks, never falls back to an unencrypted connection and never sends your password " +
        "over a connection it could not verify.";

    public static NetworkDiagnosis Classify(NetworkObservations o)
    {
        var inc = o.Incoming;
        var outg = o.Outgoing;
        var working = o.Alternatives.Where(a => a.Works).ToList();
        var vpnActive = o.Interfaces.Any(i => i.LooksLikeVpn);
        var otherNetwork = "Try another network, for example a mobile hotspot, to confirm.";

        // 1. The device itself
        if (!o.HasActiveInterface)
            return Make(NetworkVerdict.NoNetwork, VerdictCategory.NoConnection,
                "This device is not connected to a network",
                "No active Wi-Fi, cable or mobile connection was found.",
                ["Connect to Wi-Fi or a cable. HCG Mail reconnects automatically when the network is back, and sends anything waiting in the Outbox."]);

        // 2. Wi-Fi sign-in page
        if (o.CaptivePortal == true)
            return Make(NetworkVerdict.CaptivePortal, VerdictCategory.NetworkRestriction,
                "This Wi-Fi needs you to sign in first",
                "The network answered a simple web request with its own page" + At(o.CaptivePortalAddress) +
                ". Until you sign in or accept its terms, it blocks other traffic, including mail. " + NoBypass,
                ["Open a web browser and complete the network's sign-in or terms page (school, hotel or guest Wi-Fi).",
                 "HCG Mail reconnects by itself once the network lets traffic through."]);

        // 3. Name resolution and internet access
        if (o.PublicDns.Blocked() && o.Internet != ProbeOutcome.Ok)
            return Make(NetworkVerdict.DnsFailure, VerdictCategory.NoConnection,
                "The network cannot look up internet names",
                "Looking up a well-known public name failed, so no server on the internet can be found from this network.",
                ["Reconnect to the Wi-Fi or restart the router.", otherNetwork]);

        if (o.Internet.Blocked())
            return Make(NetworkVerdict.NoInternet, VerdictCategory.NoConnection,
                "Connected to a network, but it does not reach the internet",
                "A request to a well-known website did not get through. The Wi-Fi or cable is connected, but the network itself is offline or very restricted.",
                ["Check whether websites open in a browser.", otherNetwork]);

        // 4. Something on the network replaces certificates (inspecting proxy, content filter)
        if (o.InternetTls.IsCertificateProblem())
            return Make(NetworkVerdict.TlsInspection, VerdictCategory.NetworkRestriction,
                "This network intercepts encrypted connections",
                "Even a well-known website presented a certificate that does not verify" + Cert(o.InternetCertificate) +
                ". Networks with content filters or security inspection (common at schools and workplaces) do this. " + NoBypass,
                ["Use a different network for mail, such as a mobile hotspot.",
                 "Ask the network's administrator whether mail servers can be excluded from inspection.",
                 "Do not install a certificate from an unknown network to make warnings go away."]);

        // 5. The account's server names
        if (inc.Dns.Blocked() || outg.Dns.Blocked())
        {
            var which = inc.Dns.Blocked() && outg.Dns.Blocked() ? $"'{inc.Endpoint.Host}' and '{outg.Endpoint.Host}'"
                : inc.Dns.Blocked() ? $"'{inc.Endpoint.Host}'" : $"'{outg.Endpoint.Host}'";
            return o.AccountHasConnectedBefore
                ? Make(NetworkVerdict.ServerNameNotFound, VerdictCategory.NetworkRestriction,
                    "This network cannot find your mail server",
                    $"Other internet names resolve, but {which} does not. This account has worked before, so the network's name service (DNS) is probably filtering it, or the provider's DNS is having problems.",
                    [otherNetwork, "If it fails on every network, check the server name with your provider."])
                : Make(NetworkVerdict.ServerNameNotFound, VerdictCategory.ServerProblem,
                    "Your mail server's name was not found",
                    $"Other internet names resolve, but {which} does not. The server name in the account settings may be misspelled.",
                    ["Check the server names your provider gives (Settings › Accounts › Edit).", otherNetwork]);
        }

        // 6. Can mail ports be opened at all on this network?
        var incBlocked = inc.Tcp.Blocked();
        var outBlocked = outg.Tcp.Blocked();
        var refIncBlocked = o.ReferenceIncomingPort.Blocked();
        var refOutBlocked = o.ReferenceOutgoingPort.Blocked();
        var vpnHint = vpnActive
            ? "A VPN seems to be active; the VPN may be what blocks the ports. Try with the VPN on and off."
            : "If your school or organization allows it, a VPN can carry mail past the restriction (HCG VPN will be supported when it is available). HCG Mail works without any VPN.";

        if (incBlocked && outBlocked)
        {
            if (refIncBlocked || refOutBlocked)
                return Make(NetworkVerdict.NetworkBlocksMail, VerdictCategory.NetworkRestriction,
                    "This network blocks email connections",
                    $"Websites work, but the mail ports are closed: your incoming server (port {inc.Endpoint.Port}) and outgoing server (port {outg.Endpoint.Port}) " +
                    "cannot be reached, and neither can the standard mail ports of a large public mail provider. School, guest and some public Wi-Fi networks do this. " +
                    "Your password and server settings are not the problem.",
                    [otherNetwork, "Ask the network's administrator to allow IMAP (port 993) and SMTP submission (port 465 or 587).", vpnHint,
                     "Messages you send now wait in the Outbox and go out automatically when mail can be reached again."]);
            if (o.ReferenceIncomingPort == ProbeOutcome.Ok || o.ReferenceOutgoingPort == ProbeOutcome.Ok)
                return Make(NetworkVerdict.ServerUnreachable, VerdictCategory.ServerProblem,
                    "Your mail server is not responding",
                    $"This network reaches other mail servers, but '{inc.Endpoint.Host}' does not answer on port {inc.Endpoint.Port} and '{outg.Endpoint.Host}' does not answer on port {outg.Endpoint.Port}. " +
                    "The provider is probably having an outage, or the server settings are wrong.",
                    ["Check your provider's status page.", "HCG Mail keeps retrying and reconnects automatically.", "If it lasts, compare the server names and ports with your provider's instructions."]);
            return Make(NetworkVerdict.Inconclusive, VerdictCategory.Unknown,
                "Your mail servers cannot be reached",
                "Websites work, but neither mail server answers. Either this network blocks mail ports or the provider is down; the comparison test could not decide.",
                [otherNetwork, vpnHint]);
        }

        if (outBlocked)
        {
            var alt = working.Count > 0
                ? new[] { $"Use {working[0].Label} for sending instead ({working[0].Source}). It is reachable from this network and still fully encrypted." }
                : [];
            if (refOutBlocked || working.Count > 0)
                return Make(NetworkVerdict.NetworkBlocksSending, VerdictCategory.NetworkRestriction,
                    "This network blocks sending email on your outgoing port",
                    $"Receiving works, but the outgoing server '{outg.Endpoint.Host}' cannot be reached on port {outg.Endpoint.Port}" +
                    (refOutBlocked ? ", and the same port is closed for other mail providers too." : ", while another port of the same server works.") +
                    " Messages you send wait in the Outbox until sending works.",
                    [.. alt, otherNetwork, vpnHint]);
            return Make(NetworkVerdict.ServerUnreachable, VerdictCategory.ServerProblem,
                "Your outgoing mail server is not responding",
                $"'{outg.Endpoint.Host}' does not answer on port {outg.Endpoint.Port}, although this network reaches other mail servers. Messages you send wait in the Outbox.",
                ["Check the outgoing server and port with your provider.", "HCG Mail retries automatically."]);
        }

        if (incBlocked)
        {
            if (refIncBlocked)
                return Make(NetworkVerdict.NetworkBlocksIncoming, VerdictCategory.NetworkRestriction,
                    "This network blocks receiving email",
                    $"The incoming server '{inc.Endpoint.Host}' cannot be reached on port {inc.Endpoint.Port}, and the same port is closed for other mail providers too. " +
                    "Mail already on this device can still be read.",
                    [otherNetwork, "Ask the network's administrator to allow IMAP (port 993).", vpnHint]);
            return Make(NetworkVerdict.ServerUnreachable, VerdictCategory.ServerProblem,
                "Your incoming mail server is not responding",
                $"'{inc.Endpoint.Host}' does not answer on port {inc.Endpoint.Port}, although this network reaches other mail servers. Mail already on this device can still be read.",
                ["Check your provider's status page.", "HCG Mail keeps retrying and reconnects automatically."]);
        }

        // 7. Encryption and certificates of the account's servers
        var tlsResults = new[] { inc, outg };
        if (tlsResults.Any(e => e.Tls == TlsOutcome.RevocationUnknown))
            return Make(NetworkVerdict.RevocationServiceBlocked, VerdictCategory.NetworkRestriction,
                "This network blocks the certificate check",
                "Your mail server's certificate is otherwise valid, but its certificate authority's revocation service could not be reached from this network, " +
                "so HCG Mail cannot confirm that the certificate has not been revoked. Restricted networks often block these services. " + NoBypass,
                ["Ask the network's administrator to allow the certificate authority's OCSP and CRL addresses (listed under Incoming/Outgoing below).", otherNetwork]);

        if (tlsResults.FirstOrDefault(e => e.Tls.IsCertificateProblem()) is { } badCert)
            return Make(NetworkVerdict.ServerCertificateProblem, VerdictCategory.ServerProblem,
                "Your mail server's certificate does not verify",
                $"The {badCert.Role} server presented a certificate that failed verification ({Describe(badCert.Tls)}{Cert(badCert.CertificateSummary)}). " +
                "Other secure websites verify normally on this network, so the problem is the server or its name in your settings. " + NoBypass,
                ["Check that the server name matches what your provider gives.", "If it is right, contact your provider: their certificate needs renewing or fixing."]);

        if (tlsResults.FirstOrDefault(e => e.Tls == TlsOutcome.NotEncrypted) is { } plain)
            return Make(NetworkVerdict.ServerSettingsProblem, VerdictCategory.ServerProblem,
                "The server does not offer encryption with these settings",
                $"The {plain.Role} server on port {plain.Endpoint.Port} did not offer an encrypted connection. HCG Mail does not continue without encryption.",
                [$"Use the encrypted port your provider documents (usually SSL/TLS on {(plain.Role == "incoming" ? "993" : "465")}, or STARTTLS on {(plain.Role == "incoming" ? "143" : "587")})."]);

        if (tlsResults.FirstOrDefault(e => e.Tls is TlsOutcome.HandshakeFailed or TlsOutcome.TimedOut) is { } cut)
        {
            var alt = cut.Role == "outgoing" && working.Count > 0
                ? new[] { $"Use {working[0].Label} for sending instead ({working[0].Source})." }
                : [];
            return Make(NetworkVerdict.SecureHandshakeBlocked, VerdictCategory.NetworkRestriction,
                "The secure connection is cut off on this network",
                $"The {cut.Role} server accepts the connection, but the encrypted handshake never completes. Firewalls that inspect or filter traffic do this; " +
                "a wrong security setting for the port can too. " + NoBypass,
                [.. alt, otherNetwork, "Check that the security setting matches the port (SSL/TLS on 993/465, STARTTLS on 143/587)."]);
        }

        // 8. Sign-in
        if (inc.Auth == AuthOutcome.Rejected || outg.Auth == AuthOutcome.Rejected)
            return Make(NetworkVerdict.CredentialsRejected, VerdictCategory.Credentials,
                "The server refused the user name or password",
                "The network and the server work, and the connection was encrypted and verified, but the sign-in was refused. " +
                "HCG Mail does not retry a refused password by itself, because repeated attempts can lock the account.",
                ["Update the password in Settings › Accounts. Many providers need an app password instead of your normal password.",
                 "If you recently changed your password, enter the new one."]);

        if (tlsResults.FirstOrDefault(e => e.Auth == AuthOutcome.Failed) is { } unfinished)
            return Make(NetworkVerdict.Inconclusive, VerdictCategory.Unknown,
                $"Sign-in to the {unfinished.Role} server could not be completed",
                $"The network and the encrypted connection work, but the {unfinished.Role} server did not finish the sign-in: {unfinished.Detail}",
                ["Try again in a moment. If it keeps happening, check the user name and password in Settings › Accounts, or contact your provider."]);

        // 9. Everything measurable works
        if (o.LastSessionErrorKind is MailErrorKind.Unknown or MailErrorKind.Protocol or MailErrorKind.NotSupported)
            return Make(NetworkVerdict.ApplicationProblem, VerdictCategory.ApplicationProblem,
                "The network and server work — this looks like a problem in HCG Mail",
                "Every network, certificate and sign-in check passed, yet the account's connection failed with an unexpected error. " +
                "That points to a defect in HCG Mail rather than the network, your password or the provider.",
                ["Click Reconnect. If it happens again, use Copy report and send it to HCG support (no passwords or message content are included)."]);

        if (o.CredentialsNotTested)
            return Make(NetworkVerdict.Healthy, VerdictCategory.Healthy,
                "The network reaches your mail servers",
                "Internet access, name lookups, mail ports, encryption and certificates all work. Sign-in was not tested because no password is stored.",
                []);

        return Make(NetworkVerdict.Healthy, VerdictCategory.Healthy,
            "Mail works on this network",
            "Internet access, name lookups, mail ports, encryption, certificates and sign-in all passed.",
            []);

        NetworkDiagnosis Make(NetworkVerdict verdict, VerdictCategory category, string headline, string explanation, IReadOnlyList<string> actions) =>
            new(verdict, category, headline, explanation, actions, working);
    }

    private static string At(string? address) => string.IsNullOrWhiteSpace(address) ? "" : $" ({address})";

    private static string Cert(string? summary) => string.IsNullOrWhiteSpace(summary) ? "" : $"; {summary}";

    public static string Describe(TlsOutcome tls) => tls switch
    {
        TlsOutcome.Ok => "encrypted, certificate valid",
        TlsOutcome.Untrusted => "not issued by a trusted authority",
        TlsOutcome.NameMismatch => "issued for a different name",
        TlsOutcome.Expired => "expired or not yet valid",
        TlsOutcome.Revoked => "revoked",
        TlsOutcome.RevocationUnknown => "revocation status could not be checked",
        TlsOutcome.HandshakeFailed => "secure handshake failed",
        TlsOutcome.TimedOut => "secure handshake timed out",
        TlsOutcome.NotEncrypted => "no encryption offered",
        _ => "not tested",
    };

    public static string Describe(ProbeOutcome probe) => probe switch
    {
        ProbeOutcome.Ok => "reachable",
        ProbeOutcome.TimedOut => "no answer (timed out)",
        ProbeOutcome.Refused => "connection refused",
        ProbeOutcome.Failed => "failed",
        _ => "not tested",
    };
}
