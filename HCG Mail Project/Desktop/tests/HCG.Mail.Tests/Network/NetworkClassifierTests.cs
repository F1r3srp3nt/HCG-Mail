using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Network;

namespace HCG.Mail.Tests.Network;

/// <summary>
/// The Network Doctor's verdict tells a restricted network apart from a wrong password, a server outage and a defect in HCG
/// Mail — the four causes that all look like "mail stopped working" to the user.
/// </summary>
public sealed class NetworkClassifierTests
{
    private static readonly ServerEndpoint Imap = new("imap.example.no", 993, MailSecurityMode.SslTls);
    private static readonly ServerEndpoint Smtp = new("smtp.example.no", 465, MailSecurityMode.SslTls);

    private static EndpointObservation Working(string role, ServerEndpoint ep) =>
        new(role, ep, ProbeOutcome.Ok, ProbeOutcome.Ok, TlsOutcome.Ok, AuthOutcome.Ok, "ok");

    private static NetworkObservations Healthy() => new()
    {
        HasActiveInterface = true,
        PublicDns = ProbeOutcome.Ok,
        Internet = ProbeOutcome.Ok,
        InternetTls = TlsOutcome.Ok,
        CaptivePortal = false,
        ReferenceIncomingPort = ProbeOutcome.Ok,
        ReferenceOutgoingPort = ProbeOutcome.Ok,
        Incoming = Working("incoming", Imap),
        Outgoing = Working("outgoing", Smtp),
        AccountHasConnectedBefore = true,
    };

    private static EndpointObservation Blocked(EndpointObservation e, ProbeOutcome how = ProbeOutcome.TimedOut) =>
        e with { Tcp = how, Tls = TlsOutcome.NotTested, Auth = AuthOutcome.NotTested };

    [Fact]
    public void Everything_working_is_healthy()
    {
        var d = NetworkClassifier.Classify(Healthy());
        Assert.Equal(NetworkVerdict.Healthy, d.Verdict);
        Assert.Equal(VerdictCategory.Healthy, d.Category);
    }

    [Fact]
    public void School_wifi_that_closes_mail_ports_is_a_network_restriction_not_the_password_or_server()
    {
        var o = Healthy() with
        {
            ReferenceIncomingPort = ProbeOutcome.TimedOut,
            ReferenceOutgoingPort = ProbeOutcome.TimedOut,
            Incoming = Blocked(Working("incoming", Imap)),
            Outgoing = Blocked(Working("outgoing", Smtp)),
        };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.NetworkBlocksMail, d.Verdict);
        Assert.Equal(VerdictCategory.NetworkRestriction, d.Category);
        Assert.Contains("password and server settings are not the problem", d.Explanation);
        Assert.Contains(d.Actions, a => a.Contains("Outbox"));
        Assert.Contains(d.Actions, a => a.Contains("HCG VPN") && a.Contains("works without any VPN"));
    }

    [Fact]
    public void Mail_server_down_while_other_mail_servers_answer_is_a_server_problem()
    {
        var o = Healthy() with
        {
            Incoming = Blocked(Working("incoming", Imap), ProbeOutcome.Refused),
            Outgoing = Blocked(Working("outgoing", Smtp), ProbeOutcome.Refused),
        };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.ServerUnreachable, d.Verdict);
        Assert.Equal(VerdictCategory.ServerProblem, d.Category);
    }

    [Fact]
    public void Refused_password_over_a_verified_connection_is_credentials_and_is_not_retried()
    {
        var o = Healthy() with { Incoming = Working("incoming", Imap) with { Auth = AuthOutcome.Rejected } };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.CredentialsRejected, d.Verdict);
        Assert.Equal(VerdictCategory.Credentials, d.Category);
        Assert.Contains("does not retry a refused password", d.Explanation);
    }

    [Theory]
    [InlineData(MailErrorKind.Unknown)]
    [InlineData(MailErrorKind.Protocol)]
    public void Everything_passing_while_the_account_failed_unexpectedly_points_to_HCG_Mail(MailErrorKind kind)
    {
        var d = NetworkClassifier.Classify(Healthy() with { LastSessionErrorKind = kind });
        Assert.Equal(NetworkVerdict.ApplicationProblem, d.Verdict);
        Assert.Equal(VerdictCategory.ApplicationProblem, d.Category);
    }

    [Fact]
    public void A_cleared_timeout_is_healthy_not_an_application_problem() =>
        Assert.Equal(NetworkVerdict.Healthy, NetworkClassifier.Classify(Healthy() with { LastSessionErrorKind = MailErrorKind.Timeout }).Verdict);

    [Fact]
    public void Wifi_sign_in_page_is_reported_first()
    {
        var o = Healthy() with
        {
            CaptivePortal = true,
            CaptivePortalAddress = "http://portal.school.example/login",
            Incoming = Working("incoming", Imap) with { Tls = TlsOutcome.Untrusted, Auth = AuthOutcome.NotTested },
        };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.CaptivePortal, d.Verdict);
        Assert.Contains("portal.school.example", d.Explanation);
        Assert.Contains("never sends your password", d.Explanation);
    }

    [Fact]
    public void Certificate_replaced_even_for_a_well_known_site_is_interception_by_the_network()
    {
        var o = Healthy() with
        {
            InternetTls = TlsOutcome.Untrusted,
            InternetCertificate = "issued by School Firewall CA",
            Incoming = Working("incoming", Imap) with { Tls = TlsOutcome.Untrusted, Auth = AuthOutcome.NotTested },
            Outgoing = Working("outgoing", Smtp) with { Tls = TlsOutcome.Untrusted, Auth = AuthOutcome.NotTested },
        };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.TlsInspection, d.Verdict);
        Assert.Equal(VerdictCategory.NetworkRestriction, d.Category);
        Assert.Contains("School Firewall CA", d.Explanation);
        Assert.Contains(d.Actions, a => a.Contains("Do not install a certificate"));
    }

    [Fact]
    public void Bad_certificate_only_on_the_mail_server_is_a_server_problem()
    {
        var o = Healthy() with { Incoming = Working("incoming", Imap) with { Tls = TlsOutcome.Expired, Auth = AuthOutcome.NotTested } };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.ServerCertificateProblem, d.Verdict);
        Assert.Equal(VerdictCategory.ServerProblem, d.Category);
    }

    [Fact]
    public void Unreachable_revocation_service_is_a_network_restriction_and_validation_stays_on()
    {
        var o = Healthy() with { Incoming = Working("incoming", Imap) with { Tls = TlsOutcome.RevocationUnknown, Auth = AuthOutcome.NotTested } };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.RevocationServiceBlocked, d.Verdict);
        Assert.Contains("never turns off certificate checks", d.Explanation);
    }

    [Fact]
    public void Blocked_sending_port_offers_the_working_documented_alternative()
    {
        var alt = new AlternativeEndpoint(new ServerEndpoint("smtp.gmail.com", 587, MailSecurityMode.StartTls), "documented by Google", true,
            ProbeOutcome.Ok, TlsOutcome.Ok, AuthOutcome.Ok, "ok");
        var o = Healthy() with
        {
            ReferenceOutgoingPort = ProbeOutcome.TimedOut,
            Outgoing = Blocked(Working("outgoing", new ServerEndpoint("smtp.gmail.com", 465, MailSecurityMode.SslTls))),
            Alternatives = [alt],
        };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.NetworkBlocksSending, d.Verdict);
        Assert.Same(alt, Assert.Single(d.WorkingAlternatives));
        Assert.Contains(d.Actions, a => a.Contains("smtp.gmail.com port 587 (STARTTLS)"));
    }

    [Fact]
    public void No_interface_means_no_network()
    {
        var d = NetworkClassifier.Classify(Healthy() with { HasActiveInterface = false });
        Assert.Equal(NetworkVerdict.NoNetwork, d.Verdict);
        Assert.Equal(VerdictCategory.NoConnection, d.Category);
    }

    [Fact]
    public void Broken_name_lookups_are_reported_before_server_checks()
    {
        var o = Healthy() with { PublicDns = ProbeOutcome.Failed, Internet = ProbeOutcome.Failed, Incoming = Blocked(Working("incoming", Imap)) };
        Assert.Equal(NetworkVerdict.DnsFailure, NetworkClassifier.Classify(o).Verdict);
    }

    [Fact]
    public void Server_name_that_does_not_resolve_on_a_previously_working_account_suggests_dns_filtering()
    {
        var o = Healthy() with { Incoming = Working("incoming", Imap) with { Dns = ProbeOutcome.Failed, Tcp = ProbeOutcome.NotTested, Tls = TlsOutcome.NotTested, Auth = AuthOutcome.NotTested } };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.ServerNameNotFound, d.Verdict);
        Assert.Equal(VerdictCategory.NetworkRestriction, d.Category);
        Assert.Equal(VerdictCategory.ServerProblem, NetworkClassifier.Classify(o with { AccountHasConnectedBefore = false }).Category);
    }

    [Fact]
    public void Handshake_cut_off_after_connecting_is_reported_as_filtering()
    {
        var o = Healthy() with { Incoming = Working("incoming", Imap) with { Tls = TlsOutcome.TimedOut, Auth = AuthOutcome.NotTested } };
        Assert.Equal(NetworkVerdict.SecureHandshakeBlocked, NetworkClassifier.Classify(o).Verdict);
    }

    [Fact]
    public void A_sign_in_that_did_not_finish_is_never_reported_as_healthy()
    {
        var o = Healthy() with { Outgoing = Working("outgoing", Smtp) with { Auth = AuthOutcome.Failed, Detail = "The server offers no sign-in on this port." } };
        var d = NetworkClassifier.Classify(o);
        Assert.Equal(NetworkVerdict.Inconclusive, d.Verdict);
        Assert.Contains("no sign-in on this port", d.Explanation);
    }
}
