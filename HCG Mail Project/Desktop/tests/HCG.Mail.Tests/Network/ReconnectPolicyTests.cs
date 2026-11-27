using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Network;

namespace HCG.Mail.Tests.Network;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void Delays_double_from_5_seconds_and_never_exceed_5_minutes()
    {
        var policy = ReconnectPolicy.Default;
        var delays = Enumerable.Range(0, 12).Select(a => policy.DelayFor(a, 0.5).TotalSeconds).ToList();
        Assert.Equal([5, 10, 20, 40, 80, 160, 300, 300, 300, 300, 300, 300], delays);
        Assert.Equal(300, policy.DelayFor(10_000, 0.5).TotalSeconds);
    }

    [Theory]
    [InlineData(0, 0.0, 4.0)]
    [InlineData(0, 0.999, 6.0)]
    [InlineData(3, 0.0, 32.0)]
    [InlineData(20, 0.999, 300.0)] // jitter never pushes beyond the cap
    public void Jitter_stays_within_20_percent(int attempt, double random, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, ReconnectPolicy.Default.DelayFor(attempt, random).TotalSeconds, 1);

    [Theory]
    [InlineData(MailErrorKind.Network, RecoveryMode.Backoff)]
    [InlineData(MailErrorKind.Dns, RecoveryMode.Backoff)]
    [InlineData(MailErrorKind.Timeout, RecoveryMode.Backoff)]
    [InlineData(MailErrorKind.Throttled, RecoveryMode.Backoff)]
    [InlineData(MailErrorKind.RevocationUnavailable, RecoveryMode.Backoff)]
    [InlineData(MailErrorKind.Certificate, RecoveryMode.OnNetworkChange)]
    [InlineData(MailErrorKind.Tls, RecoveryMode.OnNetworkChange)]
    [InlineData(MailErrorKind.Protocol, RecoveryMode.OnNetworkChange)]
    [InlineData(MailErrorKind.Authentication, RecoveryMode.Never)]
    [InlineData(MailErrorKind.ReconnectRequired, RecoveryMode.Never)]
    [InlineData(MailErrorKind.Unknown, RecoveryMode.Never)]
    [InlineData(MailErrorKind.ServerRejected, RecoveryMode.Never)]
    public void Refused_passwords_and_unexpected_errors_are_never_retried_automatically(MailErrorKind kind, RecoveryMode expected) =>
        Assert.Equal(expected, ReconnectPolicy.ModeFor(kind));

    [Fact]
    public void A_connection_the_session_lost_by_itself_is_retried() => Assert.Equal(RecoveryMode.Backoff, ReconnectPolicy.ModeFor(null));
}
