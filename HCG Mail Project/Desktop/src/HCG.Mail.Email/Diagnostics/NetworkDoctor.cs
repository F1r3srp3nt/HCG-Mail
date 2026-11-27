using System.Diagnostics;
using System.Net;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Network;
using HCG.Mail.Email.Smtp;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Email.Diagnostics;

/// <summary>Where the Network Doctor compares against. Contacted only while the Network Doctor runs, never in the background.</summary>
public sealed record NetworkDoctorOptions
{
    /// <summary>Plain-HTTP check that answers 204 with an empty body; anything else means a Wi-Fi sign-in page (as Android and Chrome do).</summary>
    public Uri CaptivePortalCheck { get; init; } = new("http://connectivitycheck.gstatic.com/generate_204");

    /// <summary>HTTPS site used to test internet access and whether the network replaces certificates.</summary>
    public Uri InternetCheck { get; init; } = new("https://www.gstatic.com/generate_204");

    public string PublicDnsName { get; init; } = "www.gstatic.com";

    /// <summary>A large public provider's mail ports: only a TCP connection is opened and closed, nothing is sent.</summary>
    public string ReferenceIncomingHost { get; init; } = "imap.gmail.com";
    public string ReferenceOutgoingHost { get; init; } = "smtp.gmail.com";

    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Servers deliberately answer a wrong password slowly (anti-brute-force), so sign-in gets more time.</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public string ContactedHostsText =>
        $"While it runs, the Network Doctor contacts {CaptivePortalCheck.Host} and {InternetCheck.Host} (the same checks Android and Chrome use to detect " +
        $"Wi-Fi sign-in pages) and briefly opens, without sending anything, connections to {ReferenceIncomingHost} and {ReferenceOutgoingHost} to see " +
        "whether this network allows mail ports at all. Your password goes only to your own mail servers, over a verified encrypted connection.";
}

public enum NetworkCheckResult
{
    NotTested,
    Ok,
    Info,
    Warning,
    Problem,
}

public sealed record NetworkCheckRow(string Title, NetworkCheckResult Result, string Detail);

public sealed record NetworkDoctorReport(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    NetworkObservations Observations,
    NetworkDiagnosis Diagnosis,
    IReadOnlyList<NetworkCheckRow> Rows);

/// <summary>
/// The Network Doctor: measures, from the outside in, why mail does or does not work on the current network — device
/// connection, Wi-Fi sign-in page, DNS, internet access and certificate interception, whether mail ports are open on this
/// network at all, then the account's own incoming and outgoing servers (DNS, TCP, TLS with full certificate validation,
/// sign-in), provider-documented alternatives for sending, VPN state, and recent network transitions. Read-only: it never
/// sends mail and never changes settings. The password is only sent after the server's certificate has been verified.
/// </summary>
public sealed class NetworkDoctor(
    IDnsResolver dns,
    ITcpProbe tcp,
    IHttpProbe http,
    IMailClientFactory clients,
    INetworkInterfaces interfaces,
    ILogger<NetworkDoctor> logger,
    NetworkDoctorOptions? options = null)
{
    public NetworkDoctorOptions Options { get; } = options ?? new NetworkDoctorOptions();

    public async Task<NetworkDoctorReport> RunAsync(
        MailAccount account,
        string? password,
        MailErrorKind? lastSessionError,
        bool accountHasConnectedBefore,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var o = Options;

        progress?.Report("Checking this device's network connection…");
        var nics = interfaces.GetActive();

        progress?.Report("Checking internet access, Wi-Fi sign-in and name lookups…");
        var dnsTask = Probe(() => dns.ResolveAsync(o.PublicDnsName, cancellationToken), cancellationToken);
        var portalTask = http.GetAsync(o.CaptivePortalCheck, o.StepTimeout, cancellationToken);
        var internetTask = http.GetAsync(o.InternetCheck, o.StepTimeout, cancellationToken);
        var refInTask = TcpAsync(o.ReferenceIncomingHost, PortDefaults.ImapSslTls, cancellationToken);
        var refOutPort = account.Smtp.Port is PortDefaults.SmtpSslTls or PortDefaults.SmtpStartTls ? account.Smtp.Port : PortDefaults.SmtpStartTls;
        var refOutTask = TcpAsync(o.ReferenceOutgoingHost, refOutPort, cancellationToken);

        progress?.Report("Checking your incoming and outgoing servers…");
        var incomingTask = CheckImapAsync(account, password, cancellationToken);
        var outgoingTask = CheckSmtpAsync(account.Smtp, account.Username, password, "outgoing", cancellationToken);
        await Task.WhenAll(dnsTask, portalTask, internetTask, refInTask, refOutTask, incomingTask, outgoingTask).ConfigureAwait(false);

        var portal = portalTask.Result;
        var internet = internetTask.Result;
        bool? captive = portal.Outcome == ProbeOutcome.Ok ? !(portal.Status == 204 && string.IsNullOrEmpty(portal.Body)) : null;
        var incoming = incomingTask.Result;
        var outgoing = outgoingTask.Result;

        var alternatives = new List<AlternativeEndpoint>();
        if (outgoing.Tcp.Blocked() || outgoing.Tls is not (TlsOutcome.Ok or TlsOutcome.NotTested))
        {
            progress?.Report("Looking for another way to send that works on this network…");
            foreach (var candidate in SmtpAlternatives.For(account.Smtp))
            {
                var r = await CheckSmtpAsync(candidate.Endpoint, account.Username, password, "outgoing", cancellationToken).ConfigureAwait(false);
                var alt = new AlternativeEndpoint(candidate.Endpoint, candidate.Source, candidate.DocumentedByProvider, r.Tcp, r.Tls, r.Auth, r.Detail);
                // A port that is not in the provider's documentation is only offered when the server itself accepted the sign-in on it.
                if (!candidate.DocumentedByProvider && r.Auth != AuthOutcome.Ok && alt.Works)
                    alt = alt with { Auth = AuthOutcome.Failed, Detail = alt.Detail + " Not offered: sign-in on this port could not be confirmed." };
                alternatives.Add(alt);
            }
        }

        var observations = new NetworkObservations
        {
            HasActiveInterface = nics.Count > 0 || internet.Outcome == ProbeOutcome.Ok,
            Interfaces = nics,
            PublicDns = dnsTask.Result,
            Internet = internet.Outcome == ProbeOutcome.Ok ? ProbeOutcome.Ok : internet.Outcome,
            InternetTls = internet.Tls,
            InternetCertificate = internet.Certificate,
            CaptivePortal = captive,
            CaptivePortalAddress = captive == true ? portal.Location : null,
            ReferenceIncomingPort = refInTask.Result,
            ReferenceOutgoingPort = refOutTask.Result,
            Incoming = incoming,
            Outgoing = outgoing,
            Alternatives = alternatives,
            CredentialsNotTested = string.IsNullOrEmpty(password),
            LastSessionErrorKind = lastSessionError,
            AccountHasConnectedBefore = accountHasConnectedBefore,
        };
        var diagnosis = NetworkClassifier.Classify(observations);
        var rows = BuildRows(observations, o, refOutPort);
        logger.LogInformation("Network Doctor for account {AccountId}: {Verdict} ({Category}) in {Ms} ms",
            account.Id, diagnosis.Verdict, diagnosis.Category, clock.ElapsedMilliseconds);
        return new NetworkDoctorReport(started, clock.Elapsed, observations, diagnosis, rows);
    }

    // ---- account servers --------------------------------------------------------------------

    private async Task<EndpointObservation> CheckImapAsync(MailAccount account, string? password, CancellationToken ct)
    {
        var ep = account.Imap;
        var (dnsResult, tcpResult, detail) = await ReachAsync(ep, ct).ConfigureAwait(false);
        if (tcpResult != ProbeOutcome.Ok)
            return new EndpointObservation("incoming", ep, dnsResult, tcpResult, TlsOutcome.NotTested, AuthOutcome.NotTested, detail);

        var recorder = new TlsValidationRecorder { RevocationFallback = (clients as MailClientFactory)?.RevocationFallback };
        using var client = clients.CreateImapClient(recorder);
        var tls = await TlsAsync(() => client.ConnectAsync(ep.Host, ep.Port, SecurityOptions.ToSecureSocketOptions(ep.Security), Limit(ct)),
            () => client.IsSecure, recorder, MailProtocol.Imap, ep).ConfigureAwait(false);
        var auth = AuthOutcome.NotTested;
        var authDetail = "";
        if (tls.Outcome == TlsOutcome.Ok && !string.IsNullOrEmpty(password))
            (auth, authDetail) = await AuthAsync(() => client.AuthenticateAsync(new NetworkCredential(account.Username, password), Limit(ct, Options.SignInTimeout)), MailProtocol.Imap, ep).ConfigureAwait(false);
        await QuietDisconnectAsync(client).ConfigureAwait(false);
        return new EndpointObservation("incoming", ep, dnsResult, tcpResult, tls.Outcome, auth, Join(tls.Detail, authDetail), recorder.Subject is null ? null : recorder.DescribeCertificate());
    }

    private async Task<EndpointObservation> CheckSmtpAsync(ServerEndpoint ep, string username, string? password, string role, CancellationToken ct)
    {
        var (dnsResult, tcpResult, detail) = await ReachAsync(ep, ct).ConfigureAwait(false);
        if (tcpResult != ProbeOutcome.Ok)
            return new EndpointObservation(role, ep, dnsResult, tcpResult, TlsOutcome.NotTested, AuthOutcome.NotTested, detail);

        var recorder = new TlsValidationRecorder { RevocationFallback = (clients as MailClientFactory)?.RevocationFallback };
        using var client = clients.CreateSmtpClient(recorder);
        var tls = await TlsAsync(() => SmtpConnector.ConnectAsync(client, ep, Limit(ct)), () => client.IsSecure, recorder, MailProtocol.Smtp, ep).ConfigureAwait(false);
        var auth = AuthOutcome.NotTested;
        var authDetail = "";
        if (tls.Outcome == TlsOutcome.Ok)
        {
            if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                (auth, authDetail) = (AuthOutcome.Failed, "The server offers no sign-in on this port.");
            else if (!string.IsNullOrEmpty(password))
                (auth, authDetail) = await AuthAsync(() => client.AuthenticateAsync(new NetworkCredential(username, password), Limit(ct, Options.SignInTimeout)), MailProtocol.Smtp, ep).ConfigureAwait(false);
        }
        await QuietDisconnectAsync(client).ConfigureAwait(false);
        return new EndpointObservation(role, ep, dnsResult, tcpResult, tls.Outcome, auth, Join(tls.Detail, authDetail), recorder.Subject is null ? null : recorder.DescribeCertificate());
    }

    private async Task<(ProbeOutcome Dns, ProbeOutcome Tcp, string Detail)> ReachAsync(ServerEndpoint ep, CancellationToken ct)
    {
        var dnsResult = await Probe(() => dns.ResolveAsync(ep.Host, ct), ct).ConfigureAwait(false);
        if (dnsResult != ProbeOutcome.Ok)
            return (dnsResult, ProbeOutcome.NotTested, $"The name '{ep.Host}' could not be looked up.");
        var tcpResult = await TcpAsync(ep.Host, ep.Port, ct).ConfigureAwait(false);
        return (dnsResult, tcpResult, tcpResult == ProbeOutcome.Ok ? "" : $"Port {ep.Port}: {NetworkClassifier.Describe(tcpResult)}.");
    }

    private static async Task<(TlsOutcome Outcome, string Detail)> TlsAsync(Func<Task> connect, Func<bool> isSecure, TlsValidationRecorder recorder, MailProtocol protocol, ServerEndpoint ep)
    {
        try
        {
            await connect().ConfigureAwait(false);
            if (!isSecure())
                return (TlsOutcome.NotEncrypted, "The connection is not encrypted.");
            return (TlsOutcome.Ok, $"Encrypted, certificate valid ({recorder.DescribeCertificate()}).");
        }
        catch (OperationCanceledException)
        {
            return (TlsOutcome.TimedOut, "The secure handshake did not finish in time.");
        }
        catch (Exception ex)
        {
            var translated = MailErrorTranslator.Translate(ex, protocol, ep, recorder);
            var outcome = recorder.HasFailure ? TlsOutcomes.FromRecorder(recorder)
                : translated.Kind == MailErrorKind.Timeout ? TlsOutcome.TimedOut
                : translated.Kind == MailErrorKind.Tls && Has<NotSupportedException>(ex) ? TlsOutcome.NotEncrypted
                : TlsOutcome.HandshakeFailed;
            return (outcome, translated.UserMessage);
        }
    }

    private static async Task<(AuthOutcome, string)> AuthAsync(Func<Task> authenticate, MailProtocol protocol, ServerEndpoint ep)
    {
        try
        {
            await authenticate().ConfigureAwait(false);
            return (AuthOutcome.Ok, "Sign-in accepted.");
        }
        catch (OperationCanceledException)
        {
            return (AuthOutcome.Failed, "Sign-in did not finish in time.");
        }
        catch (Exception ex)
        {
            var translated = MailErrorTranslator.Translate(ex, protocol, ep);
            return (translated.Kind == MailErrorKind.Authentication ? AuthOutcome.Rejected : AuthOutcome.Failed, translated.UserMessage);
        }
    }

    private CancellationToken Limit(CancellationToken ct, TimeSpan? limit = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit ?? Options.StepTimeout + TimeSpan.FromSeconds(5));
        return cts.Token;
    }

    private async Task<ProbeOutcome> TcpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            await tcp.ConnectAsync(host, port, Options.StepTimeout, ct).ConfigureAwait(false);
            return ProbeOutcome.Ok;
        }
        catch (System.Net.Sockets.SocketException se) when (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            return ProbeOutcome.Refused;
        }
        catch (System.Net.Sockets.SocketException se) when (se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut)
        {
            return ProbeOutcome.TimedOut;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ex is OperationCanceledException or TimeoutException ? ProbeOutcome.TimedOut : ProbeOutcome.Failed;
        }
    }

    private async Task<ProbeOutcome> Probe(Func<Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        try
        {
            var task = resolve();
            var done = await Task.WhenAny(task, Task.Delay(Options.StepTimeout, ct)).ConfigureAwait(false);
            if (done != task)
                return ProbeOutcome.TimedOut;
            return (await task.ConfigureAwait(false)).Length > 0 ? ProbeOutcome.Ok : ProbeOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ProbeOutcome.Failed;
        }
    }

    private async Task QuietDisconnectAsync(IMailService client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.DisconnectAsync(true, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while closing a Network Doctor connection");
        }
    }

    private static bool Has<T>(Exception ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is T)
                return true;
        return false;
    }

    private static string Join(string a, string b) => string.IsNullOrEmpty(b) ? a : string.IsNullOrEmpty(a) ? b : a + " " + b;

    // ---- rows for display -------------------------------------------------------------------

    public static IReadOnlyList<NetworkCheckRow> BuildRows(NetworkObservations o, NetworkDoctorOptions options, int referenceOutgoingPort)
    {
        var rows = new List<NetworkCheckRow>();
        var vpns = o.Interfaces.Where(i => i.LooksLikeVpn).ToList();
        rows.Add(new("Network connection", o.HasActiveInterface ? NetworkCheckResult.Ok : NetworkCheckResult.Problem,
            o.Interfaces.Count == 0 ? (o.HasActiveInterface ? "Connected." : "No active network interface.")
                : string.Join(", ", o.Interfaces.Select(i => i.LooksLikeVpn ? $"VPN ({i.Name})" : $"{i.Type} ({i.Name})"))));
        rows.Add(new("Wi-Fi sign-in page (captive portal)",
            o.CaptivePortal switch { true => NetworkCheckResult.Problem, false => NetworkCheckResult.Ok, _ => NetworkCheckResult.NotTested },
            o.CaptivePortal switch
            {
                true => "The network intercepted a web request" + (o.CaptivePortalAddress is { } a ? $" and redirected it to {a}" : "") + ". Sign in to the network in a browser.",
                false => "None: the network passes web traffic through unchanged.",
                _ => $"Could not be checked ({options.CaptivePortalCheck.Host} did not answer).",
            }));
        rows.Add(new("Name lookups (DNS)", Result(o.PublicDns), o.PublicDns == ProbeOutcome.Ok ? $"Public names resolve ({options.PublicDnsName})." : $"Looking up {options.PublicDnsName}: {NetworkClassifier.Describe(o.PublicDns)}."));
        rows.Add(new("Internet access", Result(o.Internet), o.Internet == ProbeOutcome.Ok ? $"{options.InternetCheck.Host} reached over HTTPS." : $"{options.InternetCheck.Host}: {NetworkClassifier.Describe(o.Internet)}."));
        rows.Add(new("Certificate interception",
            o.InternetTls == TlsOutcome.Ok ? NetworkCheckResult.Ok : o.InternetTls.IsCertificateProblem() ? NetworkCheckResult.Problem : NetworkCheckResult.NotTested,
            o.InternetTls == TlsOutcome.Ok ? "None: a well-known site's certificate verified normally."
                : o.InternetTls.IsCertificateProblem() ? $"A well-known site's certificate did not verify ({NetworkClassifier.Describe(o.InternetTls)}{(o.InternetCertificate is { } c ? "; " + c : "")}). Something on this network replaces certificates."
                : "Not determined."));
        rows.Add(new("Mail ports on this network", o.ReferenceIncomingPort.Blocked() || o.ReferenceOutgoingPort.Blocked() ? NetworkCheckResult.Problem
                : o.ReferenceIncomingPort == ProbeOutcome.Ok && o.ReferenceOutgoingPort == ProbeOutcome.Ok ? NetworkCheckResult.Ok : NetworkCheckResult.NotTested,
            $"Port 993 (incoming) at {options.ReferenceIncomingHost}: {NetworkClassifier.Describe(o.ReferenceIncomingPort)}; " +
            $"port {referenceOutgoingPort} (sending) at {options.ReferenceOutgoingHost}: {NetworkClassifier.Describe(o.ReferenceOutgoingPort)}. Nothing was sent."));
        AddEndpoint(rows, "Incoming server (IMAP)", o.Incoming);
        AddEndpoint(rows, "Outgoing server (SMTP)", o.Outgoing);
        foreach (var alt in o.Alternatives)
            rows.Add(new($"Alternative for sending: {alt.Label}", alt.Works ? NetworkCheckResult.Ok : NetworkCheckResult.Warning,
                (alt.Works ? "Works on this network, fully encrypted. " : "") + $"{alt.Source}. " + alt.Detail));
        rows.Add(new("VPN", NetworkCheckResult.Info, vpns.Count > 0
            ? $"Active: {string.Join(", ", vpns.Select(v => v.Name))}. Mail is checked the same way through a VPN; certificate validation is never relaxed."
            : "No VPN active. HCG Mail does not need one."));
        return rows;
    }

    private static void AddEndpoint(List<NetworkCheckRow> rows, string title, EndpointObservation e)
    {
        var where = $"{e.Endpoint.Host} port {e.Endpoint.Port} ({AlternativeEndpoint.SecurityLabel(e.Endpoint.Security)})";
        rows.Add(new($"{title}: reachable", e.Dns != ProbeOutcome.Ok ? NetworkCheckResult.Problem : Result(e.Tcp),
            e.Dns != ProbeOutcome.Ok ? $"{e.Endpoint.Host}: name not found." : $"{where}: {NetworkClassifier.Describe(e.Tcp)}."));
        rows.Add(new($"{title}: encryption and certificate",
            e.Tls == TlsOutcome.Ok ? NetworkCheckResult.Ok : e.Tls == TlsOutcome.NotTested ? NetworkCheckResult.NotTested : NetworkCheckResult.Problem,
            e.Tls == TlsOutcome.NotTested ? "Not tested (server not reached)." : NetworkClassifier.Describe(e.Tls) + (e.CertificateSummary is { } c ? $" — {c}" : "") + "."));
        rows.Add(new($"{title}: sign-in",
            e.Auth switch { AuthOutcome.Ok => NetworkCheckResult.Ok, AuthOutcome.Rejected => NetworkCheckResult.Problem, AuthOutcome.Failed => NetworkCheckResult.Warning, _ => NetworkCheckResult.NotTested },
            e.Auth switch
            {
                AuthOutcome.Ok => "Accepted (only after the certificate was verified).",
                AuthOutcome.Rejected => "The server refused the user name or password.",
                AuthOutcome.Failed => "Could not be completed. " + e.Detail,
                _ => e.Tls == TlsOutcome.Ok ? "Not tested (no stored password)." : "Not tested: your password is never sent before encryption and the certificate are verified.",
            }));
    }

    private static NetworkCheckResult Result(ProbeOutcome p) => p switch
    {
        ProbeOutcome.Ok => NetworkCheckResult.Ok,
        ProbeOutcome.NotTested => NetworkCheckResult.NotTested,
        _ => NetworkCheckResult.Problem,
    };
}
