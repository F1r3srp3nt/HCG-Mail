using HCG.Mail.Core.Errors;

namespace HCG.Mail.Core.Network;

/// <summary>When HCG Mail reconnects an account by itself after a failure.</summary>
public enum RecoveryMode
{
    /// <summary>Only the user reconnects: the password was refused (retrying could lock the account) or HCG Mail itself failed.</summary>
    Never,

    /// <summary>Retried once when the network changes (another Wi-Fi, a captive portal signed in, a VPN started), never on a timer.</summary>
    OnNetworkChange,

    /// <summary>Retried with bounded exponential backoff, and at once when the network changes or the computer wakes up.</summary>
    Backoff,
}

/// <summary>
/// Bounded exponential backoff for reconnecting: 5 s, 10 s, 20 s … doubling up to 5 minutes, each delay varied by ±20 % so
/// accounts (and many computers behind one school network) do not reconnect in lock-step. It never grows beyond the cap.
/// </summary>
public sealed record ReconnectPolicy(TimeSpan Initial, TimeSpan Maximum, double Jitter = 0.2)
{
    public static ReconnectPolicy Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

    /// <param name="attempt">0 for the first retry after a failure.</param>
    /// <param name="random01">A number in [0, 1) used for the jitter.</param>
    public TimeSpan DelayFor(int attempt, double random01)
    {
        var exponent = Math.Clamp(attempt, 0, 30);
        var nominal = Math.Min(Initial.TotalMilliseconds * Math.Pow(2, exponent), Maximum.TotalMilliseconds);
        var factor = 1 + Jitter * (2 * Math.Clamp(random01, 0, 1) - 1);
        var delay = Math.Min(nominal * factor, Maximum.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(delay, Math.Min(Initial.TotalMilliseconds, 1000)));
    }

    /// <summary>
    /// How a failure of this kind is recovered. <c>null</c> is a connection the session lost by itself.
    /// Credentials are never retried automatically: repeated wrong-password sign-ins can lock an account.
    /// </summary>
    public static RecoveryMode ModeFor(MailErrorKind? kind) => kind switch
    {
        null or MailErrorKind.Network or MailErrorKind.Dns or MailErrorKind.Timeout or MailErrorKind.Throttled => RecoveryMode.Backoff,
        // The certificate authority's revocation service is often what a restricted network blocks; nothing is sent before the
        // handshake succeeds, so retrying is safe.
        MailErrorKind.RevocationUnavailable => RecoveryMode.Backoff,
        // A certificate that does not verify is often a captive portal or inspecting proxy in the way: try again on another network.
        MailErrorKind.Certificate or MailErrorKind.Tls or MailErrorKind.Protocol => RecoveryMode.OnNetworkChange,
        _ => RecoveryMode.Never,
    };
}
