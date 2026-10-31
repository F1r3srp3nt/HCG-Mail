using System.Net;
using System.Net.Sockets;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Services;
using HCG.Mail.Email;
using HCG.Mail.Email.Diagnostics;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HCG.Mail.Tests.Email;

public class ConnectionTesterTests
{
    private static readonly MailAccount Account = new()
    {
        Id = "t",
        DisplayName = "Test",
        EmailAddress = "ola@firma.no",
        Username = "ola@firma.no",
        Imap = new ServerEndpoint("imap.firma.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.firma.no", 465, MailSecurityMode.SslTls),
    };

    private readonly IDnsResolver _dns = Substitute.For<IDnsResolver>();
    private readonly ITcpProbe _tcp = Substitute.For<ITcpProbe>();
    private readonly IMailClientFactory _factory = Substitute.For<IMailClientFactory>();
    private readonly IImapClient _imap = Substitute.For<IImapClient>();
    private readonly ISmtpClient _smtp = Substitute.For<ISmtpClient>();

    public ConnectionTesterTests()
    {
        _dns.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([IPAddress.Parse("192.0.2.10")]);
        _factory.CreateImapClient(Arg.Any<TlsValidationRecorder>()).Returns(_imap);
        _factory.CreateSmtpClient(Arg.Any<TlsValidationRecorder>()).Returns(_smtp);

        _imap.IsSecure.Returns(true);
        _imap.IsConnected.Returns(true);
        var namespaces = new FolderNamespaceCollection { new FolderNamespace('/', "") };
        _imap.PersonalNamespaces.Returns(namespaces);
        var inbox = Substitute.For<IMailFolder>();
        inbox.Count.Returns(12);
        _imap.Inbox.Returns(inbox);
        var sent = Substitute.For<IMailFolder>();
        sent.FullName.Returns("Sent");
        sent.Name.Returns("Sent");
        sent.DirectorySeparator.Returns('/');
        sent.Attributes.Returns(FolderAttributes.Sent);
        _imap.GetFoldersAsync(Arg.Any<FolderNamespace>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new List<IMailFolder> { inbox, sent });

        _smtp.IsSecure.Returns(true);
        _smtp.IsConnected.Returns(true);
        _smtp.Capabilities.Returns(SmtpCapabilities.Authentication);
    }

    private ConnectionTester Tester() => new(_dns, _tcp, _factory, NullLogger<ConnectionTester>.Instance);

    private static ConnectionTestStatus StatusOf(ConnectionTestReport report, ConnectionTestStepKind kind) =>
        report.Steps.Single(s => s.Kind == kind).Status;

    [Fact]
    public async Task All_steps_pass_and_no_email_is_sent()
    {
        var progress = new List<ConnectionTestStep>();
        var report = await Tester().RunAsync(Account, "pw", new SynchronousProgress(progress.Add));

        Assert.True(report.Succeeded, string.Join("\n", report.Steps.Select(s => $"{s.Kind}: {s.Status} {s.Detail}")));
        Assert.Equal(6, report.Steps.Count);
        Assert.Contains(progress, p => p.Status == ConnectionTestStatus.Running);
        await _imap.Received(1).ConnectAsync("imap.firma.no", 993, SecureSocketOptions.SslOnConnect, Arg.Any<CancellationToken>());
        await _smtp.Received(1).ConnectAsync("smtp.firma.no", 465, SecureSocketOptions.SslOnConnect, Arg.Any<CancellationToken>());
        await _smtp.Received(1).AuthenticateAsync(Arg.Is<ICredentials>(c => c.GetCredential(new Uri("smtp://x"), "")!.Password == "pw"), Arg.Any<CancellationToken>());
        await _smtp.DidNotReceiveWithAnyArgs().SendAsync(default(MimeMessage)!, default, default);
        Assert.Contains("No test email was sent", report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Smtp).Detail);
        Assert.Contains("Sent", report.Steps.Single(s => s.Kind == ConnectionTestStepKind.FolderAccess).Detail);
    }

    [Fact]
    public async Task Dns_failure_skips_dependent_steps()
    {
        _dns.ResolveAsync("imap.firma.no", Arg.Any<CancellationToken>()).ThrowsAsync(new SocketException((int)SocketError.HostNotFound));

        var report = await Tester().RunAsync(Account, "pw");

        Assert.Equal(ConnectionTestStatus.Failed, StatusOf(report, ConnectionTestStepKind.Dns));
        Assert.Contains("could not be found", report.Steps[0].Detail);
        Assert.Equal(ConnectionTestStatus.Skipped, StatusOf(report, ConnectionTestStepKind.ImapAuthentication));
        Assert.Equal(ConnectionTestStatus.Skipped, StatusOf(report, ConnectionTestStepKind.FolderAccess));
        // SMTP is independent and still tested.
        Assert.Equal(ConnectionTestStatus.Passed, StatusOf(report, ConnectionTestStepKind.Smtp));
        _factory.DidNotReceive().CreateImapClient(Arg.Any<TlsValidationRecorder>());
    }

    [Fact]
    public async Task Tcp_failure_is_reported()
    {
        _tcp.ConnectAsync("smtp.firma.no", 465, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));

        var report = await Tester().RunAsync(Account, "pw");

        Assert.Equal(ConnectionTestStatus.Failed, StatusOf(report, ConnectionTestStepKind.Tcp));
        Assert.Contains("refused the connection on port 465", report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Tcp).Detail);
        Assert.Equal(ConnectionTestStatus.Passed, StatusOf(report, ConnectionTestStepKind.FolderAccess));
        Assert.Equal(ConnectionTestStatus.Skipped, StatusOf(report, ConnectionTestStepKind.Smtp));
    }

    [Fact]
    public async Task Tls_failure_stops_before_credentials_are_sent()
    {
        _imap.ConnectAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<SecureSocketOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SslHandshakeException("bad certificate"));

        var report = await Tester().RunAsync(Account, "pw");

        Assert.Equal(ConnectionTestStatus.Failed, StatusOf(report, ConnectionTestStepKind.Tls));
        Assert.Equal(ConnectionTestStatus.Skipped, StatusOf(report, ConnectionTestStepKind.ImapAuthentication));
        await _imap.DidNotReceiveWithAnyArgs().AuthenticateAsync(default(ICredentials)!, default);
    }

    [Fact]
    public async Task Wrong_password_fails_authentication_and_skips_folders()
    {
        _imap.AuthenticateAsync(Arg.Any<ICredentials>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthenticationException());
        _smtp.AuthenticateAsync(Arg.Any<ICredentials>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthenticationException());

        var report = await Tester().RunAsync(Account, "wrong");

        Assert.Equal(ConnectionTestStatus.Failed, StatusOf(report, ConnectionTestStepKind.ImapAuthentication));
        Assert.Contains("rejected the username or password", report.Steps.Single(s => s.Kind == ConnectionTestStepKind.ImapAuthentication).Detail);
        Assert.Equal(ConnectionTestStatus.Skipped, StatusOf(report, ConnectionTestStepKind.FolderAccess));
        Assert.Equal(ConnectionTestStatus.Failed, StatusOf(report, ConnectionTestStepKind.Smtp));
        Assert.False(report.Succeeded);
        Assert.DoesNotContain(report.Steps, s => s.Detail.Contains("wrong"));
    }

    [Fact]
    public async Task Smtp_without_authentication_is_a_failure()
    {
        _smtp.Capabilities.Returns(SmtpCapabilities.None);
        var report = await Tester().RunAsync(Account, "pw");
        Assert.Equal(ConnectionTestStatus.Failed, StatusOf(report, ConnectionTestStepKind.Smtp));
    }

    [Fact]
    public async Task Test_connections_are_always_closed()
    {
        await Tester().RunAsync(Account, "pw");
        _imap.Received(1).Dispose();
        _smtp.Received(1).Dispose();
    }

    private sealed class SynchronousProgress(Action<ConnectionTestStep> report) : IProgress<ConnectionTestStep>
    {
        public void Report(ConnectionTestStep value) => report(value);
    }
}
