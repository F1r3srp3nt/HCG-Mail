using HCG.Mail.Core.Accounts;

namespace HCG.Mail.Core.Services;

public enum ConnectionTestStepKind
{
    Dns,
    Tcp,
    Tls,
    ImapAuthentication,
    FolderAccess,
    Smtp,
}

public enum ConnectionTestStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Skipped,
}

public sealed record ConnectionTestStep(ConnectionTestStepKind Kind, ConnectionTestStatus Status, string Detail)
{
    public string Title => Kind switch
    {
        ConnectionTestStepKind.Dns => "DNS resolution",
        ConnectionTestStepKind.Tcp => "TCP connectivity",
        ConnectionTestStepKind.Tls => "TLS negotiation",
        ConnectionTestStepKind.ImapAuthentication => "IMAP authentication",
        ConnectionTestStepKind.FolderAccess => "Mailbox folder access",
        ConnectionTestStepKind.Smtp => "SMTP connection and authentication",
        _ => Kind.ToString(),
    };
}

public sealed record ConnectionTestReport(IReadOnlyList<ConnectionTestStep> Steps)
{
    public bool Succeeded => Steps.All(s => s.Status == ConnectionTestStatus.Passed);
}

/// <summary>
/// Verifies an account's settings step by step without sending any email.
/// </summary>
public interface IConnectionTester
{
    Task<ConnectionTestReport> RunAsync(
        MailAccount account,
        string password,
        IProgress<ConnectionTestStep>? progress = null,
        CancellationToken cancellationToken = default);
}
