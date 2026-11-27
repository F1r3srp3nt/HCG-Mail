using Avalonia.Headless.XUnit;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HCG.Mail.App.Tests;

/// <summary>Mail Health › Connection: states from what happened, results from real checks, safe diagnostics.</summary>
public class ConnectionHealthTests
{
    private const string Password = "s3cret-Pw-123";

    private static readonly MailAccount Account = new()
    {
        Id = "a1", DisplayName = "Ola", EmailAddress = "ola@example.com", Username = "ola@example.com",
        Imap = new ServerEndpoint("imap.example.com", 993, MailSecurityMode.SslTls), Smtp = new ServerEndpoint("smtp.example.com", 465, MailSecurityMode.SslTls),
    };

    private static async Task<(AccountViewModel Account, IMailSession Session)> AccountAsync(Exception? connectFailure = null)
    {
        var session = Substitute.For<IMailSession>();
        session.GetFoldersAsync(Arg.Any<CancellationToken>()).Returns([new MailFolder("INBOX", "Inbox", '/', FolderRole.Inbox, true, 0, 0, 0)]);
        if (connectFailure is not null)
            session.ConnectAsync(Arg.Any<CancellationToken>()).ThrowsAsync(connectFailure);
        var factory = Substitute.For<IMailSessionFactory>();
        factory.Create(Arg.Any<MailAccount>(), Arg.Any<string>()).Returns(session);
        var vault = new SessionCredentialVault();
        await vault.SetAsync(Account.CredentialKey, Password);
        var vm = new AccountViewModel(Account, factory, vault, NullLogger.Instance);
        await vm.ConnectAsync();
        return (vm, session);
    }

    private static ConnectionHealthViewModel Health(AccountViewModel account, IConnectionTester? tester = null, IDeliveryLog? log = null)
    {
        var vault = new SessionCredentialVault();
        vault.SetAsync(Account.CredentialKey, Password).GetAwaiter().GetResult();
        return new ConnectionHealthViewModel(account, tester ?? Substitute.For<IConnectionTester>(), vault, log ?? new InMemoryDeliveryLog(),
            new AppPaths(Directory.CreateTempSubdirectory("hcg-health-").FullName));
    }

    [AvaloniaTheory]
    [InlineData(MailErrorKind.Authentication, AccountHealth.AuthenticationFailed)]
    [InlineData(MailErrorKind.Certificate, AccountHealth.SecurityProblem)]
    [InlineData(MailErrorKind.Tls, AccountHealth.SecurityProblem)]
    [InlineData(MailErrorKind.Network, AccountHealth.Offline)]
    [InlineData(MailErrorKind.Dns, AccountHealth.Offline)]
    public async Task A_failed_connection_is_classified_by_what_failed(MailErrorKind kind, AccountHealth expected)
    {
        var (account, _) = await AccountAsync(new MailOperationException(kind, "failure"));
        Assert.Equal(expected, account.Health);
        Assert.False(Health(account).IsHealthy);
    }

    [AvaloniaFact]
    public async Task Connected_then_an_operation_fails_is_degraded_not_healthy()
    {
        var (account, _) = await AccountAsync();
        Assert.Equal(AccountHealth.Connected, account.Health);
        account.ReportError(new MailOperationException(MailErrorKind.ServerRejected, "The server refused to move the message."));
        Assert.Equal(AccountHealth.Degraded, account.Health);
        Assert.Equal("Connected, with problems", Health(account).StateTitle);
    }

    [AvaloniaFact]
    public async Task Nothing_is_shown_as_healthy_just_because_it_is_configured()
    {
        var (account, _) = await AccountAsync(new MailOperationException(MailErrorKind.Network, "unreachable"));
        var health = Health(account);
        foreach (var title in new[] { "Incoming server sign-in (IMAP)", "Outgoing server sign-in (SMTP)", "Encryption (TLS) and server certificate", "Folder discovery", "Last successful synchronization", "Last successful send" })
            Assert.Equal(HealthResult.NotVerified, health.Rows.Single(r => r.Title == title).Result);
        Assert.Equal(HealthResult.Problem, health.Rows.Single(r => r.Title == "Last connection error").Result);
    }

    [AvaloniaFact]
    public async Task Check_connection_reports_each_step_and_a_recent_send_counts_as_smtp_evidence()
    {
        var (account, _) = await AccountAsync();
        var tester = Substitute.For<IConnectionTester>();
        tester.RunAsync(Account, Password, Arg.Any<IProgress<ConnectionTestStep>>(), Arg.Any<CancellationToken>()).Returns(new ConnectionTestReport([
            new(ConnectionTestStepKind.Dns, ConnectionTestStatus.Passed, "imap.example.com resolves"),
            new(ConnectionTestStepKind.Tcp, ConnectionTestStatus.Passed, "port open"),
            new(ConnectionTestStepKind.Tls, ConnectionTestStatus.Passed, "TLS 1.3, certificate for imap.example.com valid until 2027-05-01"),
            new(ConnectionTestStepKind.ImapAuthentication, ConnectionTestStatus.Passed, "Signed in"),
            new(ConnectionTestStepKind.FolderAccess, ConnectionTestStatus.Passed, "12 folders"),
            new(ConnectionTestStepKind.Smtp, ConnectionTestStatus.Failed, "The outgoing server rejected the password."),
        ]));
        var log = new InMemoryDeliveryLog();
        var health = Health(account, tester, log);

        await health.CheckConnectionCommand.ExecuteAsync(null);

        Assert.Equal(HealthResult.Ok, health.Rows.Single(r => r.Title.StartsWith("Encryption")).Result);
        Assert.Contains("valid until 2027-05-01", health.Rows.Single(r => r.Title.StartsWith("Encryption")).Detail);
        Assert.Equal(HealthResult.Problem, health.Rows.Single(r => r.Title.StartsWith("Outgoing")).Result);
        Assert.Contains("did not pass", health.CheckStatus);
    }

    [AvaloniaFact]
    public async Task Copied_diagnostics_hold_no_password_and_no_full_address()
    {
        var (account, _) = await AccountAsync(new MailOperationException(MailErrorKind.Authentication, "535 5.7.8 Authentication failed for ola@example.com"));
        var text = Health(account).BuildDiagnosticsText();

        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain("ola@example.com", text);
        Assert.Contains("***@example.com", text);
        Assert.Contains("Sign-in failed", text);
    }

    [AvaloniaFact]
    public async Task Mail_health_opens_on_the_connection_page()
    {
        await using var ui = await UiHarness.StartAsync();
        var vm = ui.Services.GetRequiredService<MailHealthViewModelFactory>().Create(ui.ViewModel.Accounts[0]);
        var window = new MailHealthWindow { DataContext = vm, Width = 980, Height = 760 };
        window.Show();
        await UiHarness.Settle(200);
        Assert.True(vm.HasConnection);
        Assert.True(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TabItem>().Single(t => t.Name == "ConnectionTab").IsSelected);
        Assert.NotEmpty(vm.Connection!.Rows);
        UiHarness.Capture(window, "mail-health-connection");
        window.Close();
    }
}
