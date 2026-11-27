using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;

namespace HCG.Mail.Core.Network;

/// <summary>Result of one network probe (DNS lookup, TCP connection, HTTP request).</summary>
public enum ProbeOutcome
{
    NotTested,
    Ok,
    Failed,
    TimedOut,
    Refused,
}

/// <summary>Result of a TLS handshake with full certificate validation (never relaxed).</summary>
public enum TlsOutcome
{
    NotTested,
    Ok,

    /// <summary>The certificate chain does not lead to a trusted authority: self-signed, or replaced by something on the network.</summary>
    Untrusted,
    NameMismatch,
    Expired,
    Revoked,

    /// <summary>Otherwise valid, but the certificate authority's revocation service could not be reached.</summary>
    RevocationUnknown,

    /// <summary>The connection was established but the secure handshake failed or was cut off.</summary>
    HandshakeFailed,
    TimedOut,

    /// <summary>The server offered no encryption (for example no STARTTLS); HCG Mail does not continue without it.</summary>
    NotEncrypted,
}

public enum AuthOutcome
{
    NotTested,
    Ok,
    Rejected,
    Failed,
}

public static class ProbeOutcomes
{
    public static bool Blocked(this ProbeOutcome o) => o is ProbeOutcome.TimedOut or ProbeOutcome.Refused or ProbeOutcome.Failed;

    public static bool IsCertificateProblem(this TlsOutcome o) => o is TlsOutcome.Untrusted or TlsOutcome.NameMismatch or TlsOutcome.Expired or TlsOutcome.Revoked;
}

/// <summary>What the Network Doctor measured for one mail server (incoming or outgoing).</summary>
public sealed record EndpointObservation(
    string Role,
    ServerEndpoint Endpoint,
    ProbeOutcome Dns,
    ProbeOutcome Tcp,
    TlsOutcome Tls,
    AuthOutcome Auth,
    string Detail,
    string? CertificateSummary = null)
{
    public static EndpointObservation NotTested(string role, ServerEndpoint endpoint) =>
        new(role, endpoint, ProbeOutcome.NotTested, ProbeOutcome.NotTested, TlsOutcome.NotTested, AuthOutcome.NotTested, "Not tested.");

    public bool FullyWorking => Dns == ProbeOutcome.Ok && Tcp == ProbeOutcome.Ok && Tls == TlsOutcome.Ok && Auth is AuthOutcome.Ok or AuthOutcome.NotTested;
}

/// <summary>A different, provider-documented (or server-confirmed) way to reach the outgoing server, and whether it works here.</summary>
public sealed record AlternativeEndpoint(
    ServerEndpoint Endpoint,
    string Source,
    bool DocumentedByProvider,
    ProbeOutcome Tcp,
    TlsOutcome Tls,
    AuthOutcome Auth,
    string Detail)
{
    /// <summary>Reachable from this network with a valid certificate and encryption (and the sign-in accepted when it was tested).</summary>
    public bool Works => Tcp == ProbeOutcome.Ok && Tls == TlsOutcome.Ok && Auth is AuthOutcome.Ok or AuthOutcome.NotTested;

    public string Label => $"{Endpoint.Host} port {Endpoint.Port} ({SecurityLabel(Endpoint.Security)})";

    public static string SecurityLabel(MailSecurityMode mode) => mode == MailSecurityMode.StartTls ? "STARTTLS" : "SSL/TLS";
}

/// <summary>Everything the Network Doctor observed. Facts only; <see cref="NetworkClassifier"/> turns them into a verdict.</summary>
public sealed record NetworkObservations
{
    public bool HasActiveInterface { get; init; } = true;
    public IReadOnlyList<NetworkInterfaceInfo> Interfaces { get; init; } = [];

    /// <summary>Looking up a well-known public name.</summary>
    public ProbeOutcome PublicDns { get; init; }

    /// <summary>An HTTPS request to a well-known site (internet access beyond the local network).</summary>
    public ProbeOutcome Internet { get; init; }

    /// <summary>Certificate validation of that HTTPS site: a failure here means something on the network replaces certificates.</summary>
    public TlsOutcome InternetTls { get; init; }
    public string? InternetCertificate { get; init; }

    /// <summary>True when a plain HTTP check was answered by something other than the expected server (a Wi-Fi sign-in page).</summary>
    public bool? CaptivePortal { get; init; }
    public string? CaptivePortalAddress { get; init; }

    /// <summary>Whether the standard mail ports of a large public provider can be opened from this network (TCP only, nothing sent).</summary>
    public ProbeOutcome ReferenceIncomingPort { get; init; }
    public ProbeOutcome ReferenceOutgoingPort { get; init; }

    public required EndpointObservation Incoming { get; init; }
    public required EndpointObservation Outgoing { get; init; }
    public IReadOnlyList<AlternativeEndpoint> Alternatives { get; init; } = [];

    /// <summary>True when no stored password was available, so sign-in was not tested.</summary>
    public bool CredentialsNotTested { get; init; }

    /// <summary>The last error the account's connection reported, if any (for telling an HCG Mail defect apart).</summary>
    public MailErrorKind? LastSessionErrorKind { get; init; }
    public bool AccountHasConnectedBefore { get; init; }
}

public enum NetworkVerdict
{
    Healthy,
    NoNetwork,
    NoInternet,
    CaptivePortal,
    DnsFailure,
    ServerNameNotFound,
    TlsInspection,
    NetworkBlocksMail,
    NetworkBlocksIncoming,
    NetworkBlocksSending,
    SecureHandshakeBlocked,
    RevocationServiceBlocked,
    ServerUnreachable,
    ServerCertificateProblem,
    ServerSettingsProblem,
    CredentialsRejected,
    ApplicationProblem,
    Inconclusive,
}

/// <summary>Who or what has to change for mail to work.</summary>
public enum VerdictCategory
{
    Healthy,

    /// <summary>The device has no working connection at all.</summary>
    NoConnection,

    /// <summary>The network (school, hotel, guest Wi-Fi, firewall) restricts or intercepts mail traffic.</summary>
    NetworkRestriction,

    /// <summary>The mail server is down, unreachable or misconfigured, or the account's server settings are wrong.</summary>
    ServerProblem,

    /// <summary>The server refused the user name or password.</summary>
    Credentials,

    /// <summary>The network and server work; HCG Mail itself failed.</summary>
    ApplicationProblem,

    Unknown,
}

public sealed record NetworkDiagnosis(
    NetworkVerdict Verdict,
    VerdictCategory Category,
    string Headline,
    string Explanation,
    IReadOnlyList<string> Actions,
    IReadOnlyList<AlternativeEndpoint> WorkingAlternatives)
{
    public string CategoryLabel => Category switch
    {
        VerdictCategory.Healthy => "Working",
        VerdictCategory.NoConnection => "No connection",
        VerdictCategory.NetworkRestriction => "Network restriction",
        VerdictCategory.ServerProblem => "Server problem",
        VerdictCategory.Credentials => "Password refused",
        VerdictCategory.ApplicationProblem => "HCG Mail problem",
        _ => "Not determined",
    };
}
