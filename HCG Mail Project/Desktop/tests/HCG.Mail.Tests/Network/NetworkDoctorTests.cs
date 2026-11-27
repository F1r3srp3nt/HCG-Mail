using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Network;
using HCG.Mail.Email;
using HCG.Mail.Email.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Network;

/// <summary>
/// The Network Doctor against simulated networks: a school network that closes mail ports, a Wi-Fi sign-in page, a network
/// that intercepts TLS, and a mail server presenting an untrusted certificate (a real TLS handshake, in process). In every
/// case the password is never sent over a connection whose certificate did not verify.
/// </summary>
public sealed class NetworkDoctorTests
{
    private sealed class FakeDns(params string[] failing) : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            failing.Contains(host) ? throw new SocketException((int)SocketError.HostNotFound) : Task.FromResult(new[] { IPAddress.Loopback });
    }

    /// <summary>TCP outcomes per "host:port"; anything not listed is really attempted (for the in-process TLS server).</summary>
    private sealed class FakeTcp(Dictionary<string, ProbeOutcome> outcomes) : ITcpProbe
    {
        public Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            outcomes.TryGetValue($"{host}:{port}", out var o) ? o switch
            {
                ProbeOutcome.Ok => Task.CompletedTask,
                ProbeOutcome.Refused => throw new SocketException((int)SocketError.ConnectionRefused),
                _ => throw new SocketException((int)SocketError.TimedOut),
            } : new SystemTcpProbe().ConnectAsync(host, port, timeout, cancellationToken);
    }

    private sealed class FakeHttp(HttpProbeResult portal, HttpProbeResult internet) : IHttpProbe
    {
        public Task<HttpProbeResult> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(url.Scheme == "http" ? portal : internet);
    }

    private sealed class NoInterfaces(params NetworkInterfaceInfo[] nics) : INetworkInterfaces
    {
        public IReadOnlyList<NetworkInterfaceInfo> GetActive() => nics;
    }

    private static readonly HttpProbeResult PortalClear = new(ProbeOutcome.Ok, 204, null, "", TlsOutcome.NotTested, null);
    private static readonly HttpProbeResult InternetOk = new(ProbeOutcome.Ok, 204, null, "", TlsOutcome.Ok, "www.gstatic.com");

    private static MailAccount Account(string imapHost = "imap.school-test.example", int imapPort = 993, string smtpHost = "smtp.school-test.example") => new()
    {
        Id = "doctor-test",
        DisplayName = "Student",
        EmailAddress = "student@school-test.example",
        Username = "student@school-test.example",
        Imap = new ServerEndpoint(imapHost, imapPort, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint(smtpHost, 465, MailSecurityMode.SslTls),
    };

    private static NetworkDoctor Doctor(Dictionary<string, ProbeOutcome> tcp, HttpProbeResult? portal = null, HttpProbeResult? internet = null, params string[] dnsFailing) =>
        new(new FakeDns(dnsFailing), new FakeTcp(tcp), new FakeHttp(portal ?? PortalClear, internet ?? InternetOk),
            new MailClientFactory { Timeout = TimeSpan.FromSeconds(5) },
            new NoInterfaces(new NetworkInterfaceInfo("wlan0", "Wi-Fi", "Wi-Fi", true, false)), NullLogger<NetworkDoctor>.Instance,
            new NetworkDoctorOptions { StepTimeout = TimeSpan.FromSeconds(3) });

    [Fact]
    public async Task School_network_closing_mail_ports_is_identified_and_nothing_is_sent()
    {
        var tcp = new Dictionary<string, ProbeOutcome>
        {
            ["imap.gmail.com:993"] = ProbeOutcome.TimedOut,
            ["smtp.gmail.com:465"] = ProbeOutcome.TimedOut,
            ["imap.school-test.example:993"] = ProbeOutcome.TimedOut,
            ["smtp.school-test.example:465"] = ProbeOutcome.TimedOut,
            ["smtp.school-test.example:587"] = ProbeOutcome.TimedOut,
        };
        var report = await Doctor(tcp).RunAsync(Account(), "never-sent", lastSessionError: HCG.Mail.Core.Errors.MailErrorKind.Timeout, accountHasConnectedBefore: true);

        Assert.Equal(NetworkVerdict.NetworkBlocksMail, report.Diagnosis.Verdict);
        Assert.Equal(VerdictCategory.NetworkRestriction, report.Diagnosis.Category);
        Assert.Contains(report.Rows, r => r.Title == "Mail ports on this network" && r.Result == NetworkCheckResult.Problem);
        Assert.Contains(report.Rows, r => r.Title == "Internet access" && r.Result == NetworkCheckResult.Ok);
        // The other standard submission port of the same server was tried as a possible way out (and is blocked too).
        var alt = Assert.Single(report.Observations.Alternatives);
        Assert.Equal(587, alt.Endpoint.Port);
        Assert.False(alt.Works);
        Assert.All(report.Rows, r => Assert.DoesNotContain("never-sent", r.Detail));
    }

    [Fact]
    public async Task Wifi_sign_in_page_is_detected()
    {
        var portal = new HttpProbeResult(ProbeOutcome.Ok, 302, "http://login.school-wifi.example/?continue", "", TlsOutcome.NotTested, null);
        var tcp = new Dictionary<string, ProbeOutcome> { ["imap.school-test.example:993"] = ProbeOutcome.TimedOut, ["smtp.school-test.example:465"] = ProbeOutcome.TimedOut };
        var report = await Doctor(tcp, portal: portal).RunAsync(Account(), null, null, true);
        Assert.Equal(NetworkVerdict.CaptivePortal, report.Diagnosis.Verdict);
        Assert.Contains("login.school-wifi.example", report.Diagnosis.Explanation);
    }

    [Fact]
    public async Task Network_replacing_certificates_is_identified_as_interception()
    {
        var intercepted = new HttpProbeResult(ProbeOutcome.Ok, 0, null, "", TlsOutcome.Untrusted, "issued by Campus Inspection CA");
        var tcp = new Dictionary<string, ProbeOutcome> { ["imap.school-test.example:993"] = ProbeOutcome.TimedOut, ["smtp.school-test.example:465"] = ProbeOutcome.TimedOut };
        var report = await Doctor(tcp, internet: intercepted).RunAsync(Account(), null, null, true);
        Assert.Equal(NetworkVerdict.TlsInspection, report.Diagnosis.Verdict);
        Assert.Contains("Campus Inspection CA", report.Diagnosis.Explanation);
    }

    [Fact]
    public async Task Untrusted_mail_server_certificate_stops_before_the_password_is_sent()
    {
        using var server = new UntrustedTlsServer();
        var tcp = new Dictionary<string, ProbeOutcome>
        {
            ["imap.gmail.com:993"] = ProbeOutcome.Ok,
            ["smtp.gmail.com:465"] = ProbeOutcome.Ok,
            ["smtp.school-test.example:465"] = ProbeOutcome.Refused,
            ["smtp.school-test.example:587"] = ProbeOutcome.Refused,
        };
        var report = await Doctor(tcp).RunAsync(Account("127.0.0.1", server.Port), "secret-password-123", null, true);

        Assert.Equal(TlsOutcome.Untrusted, report.Observations.Incoming.Tls);
        Assert.Equal(AuthOutcome.NotTested, report.Observations.Incoming.Auth);
        Assert.Equal(NetworkVerdict.ServerUnreachable, report.Diagnosis.Verdict); // outgoing refused is checked first
        await server.Finished;
        Assert.Equal(0, server.ApplicationBytesReceived); // no credentials crossed the unverified connection
    }

    [Fact]
    public async Task Untrusted_certificate_on_both_servers_is_a_server_certificate_problem()
    {
        using var server = new UntrustedTlsServer();
        var tcp = new Dictionary<string, ProbeOutcome> { ["imap.gmail.com:993"] = ProbeOutcome.Ok, ["smtp.gmail.com:465"] = ProbeOutcome.Ok };
        var account = Account("127.0.0.1", server.Port) with { Smtp = new ServerEndpoint("127.0.0.1", server.Port, MailSecurityMode.SslTls) };
        var report = await Doctor(tcp).RunAsync(account, "secret-password-123", null, true);
        Assert.Equal(NetworkVerdict.ServerCertificateProblem, report.Diagnosis.Verdict);
        Assert.Contains(report.Rows, r => r.Title.Contains("sign-in") && r.Detail.Contains("never sent before encryption and the certificate are verified"));
        await server.Finished;
        Assert.Equal(0, server.ApplicationBytesReceived);
    }

    [Fact]
    public void Privacy_text_names_every_host_the_doctor_contacts()
    {
        var o = new NetworkDoctorOptions();
        foreach (var host in new[] { o.CaptivePortalCheck.Host, o.InternetCheck.Host, o.ReferenceIncomingHost, o.ReferenceOutgoingHost })
            Assert.Contains(host, o.ContactedHostsText);
    }

    /// <summary>A TLS server with a self-signed certificate that records whether any application data ever arrives.</summary>
    private sealed class UntrustedTlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private int _bytes;

        public UntrustedTlsServer()
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=imap.school-test.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("imap.school-test.example");
            san.AddIpAddress(IPAddress.Loopback); // the name matches: only the trust chain is wrong, as with an intercepting proxy
            req.CertificateExtensions.Add(san.Build());
            using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            _cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        return;
                    }
                    _ = Task.Run(() => HandleAsync(client));
                }
            });
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var ssl = new SslStream(client.GetStream()))
            {
                try
                {
                    await ssl.AuthenticateAsServerAsync(_cert);
                    var buffer = new byte[1024];
                    int n;
                    while ((n = await ssl.ReadAsync(buffer)) > 0)
                        Interlocked.Add(ref _bytes, n);
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                {
                    // A TCP probe that closes at once, or a client that refused the certificate.
                }
            }
        }

        /// <summary>Gives the handlers a moment to finish reading after the client has gone.</summary>
        public Task Finished => Task.Delay(300);

        public int Port { get; }
        public int ApplicationBytesReceived => Volatile.Read(ref _bytes);

        public void Dispose()
        {
            _listener.Stop();
            _cert.Dispose();
        }
    }
}
