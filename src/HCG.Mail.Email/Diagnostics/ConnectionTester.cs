using System.Net;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Imap;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Email.Diagnostics;

/// <summary>
/// Runs the account connection test as separate, individually reported steps:
/// DNS → TCP → TLS → IMAP login → folder access, and SMTP login. No email is sent.
/// The IMAP and SMTP chains are independent so a problem in one still lets the other be tested.
/// </summary>
public sealed class ConnectionTester(
    IDnsResolver dns,
    ITcpProbe tcp,
    IMailClientFactory clientFactory,
    ILogger<ConnectionTester> logger) : IConnectionTester
{
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<ConnectionTestReport> RunAsync(
        MailAccount account,
        string password,
        IProgress<ConnectionTestStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var steps = Enum.GetValues<ConnectionTestStepKind>()
            .ToDictionary(k => k, k => new ConnectionTestStep(k, ConnectionTestStatus.Pending, ""));

        void Report(ConnectionTestStepKind kind, ConnectionTestStatus status, string detail)
        {
            steps[kind] = new ConnectionTestStep(kind, status, detail);
            progress?.Report(steps[kind]);
        }

        var imapRecorder = new TlsValidationRecorder();
        var smtpRecorder = new TlsValidationRecorder();
        IImapClient? imap = null;
        ISmtpClient? smtp = null;

        try
        {
            // 1. DNS
            Report(ConnectionTestStepKind.Dns, ConnectionTestStatus.Running, "Looking up server names…");
            var imapDns = await TryAsync(() => ResolveAsync(account.Imap.Host, cancellationToken), MailProtocol.Imap, account.Imap, null).ConfigureAwait(false);
            var smtpDns = string.Equals(account.Imap.Host, account.Smtp.Host, StringComparison.OrdinalIgnoreCase)
                ? imapDns
                : await TryAsync(() => ResolveAsync(account.Smtp.Host, cancellationToken), MailProtocol.Smtp, account.Smtp, null).ConfigureAwait(false);
            Report(ConnectionTestStepKind.Dns, Combine(imapDns.Ok, smtpDns.Ok),
                Join($"IMAP {account.Imap.Host}: {imapDns.Detail}", $"SMTP {account.Smtp.Host}: {smtpDns.Detail}"));

            // 2. TCP
            Report(ConnectionTestStepKind.Tcp, ConnectionTestStatus.Running, "Opening network connections…");
            var imapTcp = imapDns.Ok
                ? await TryAsync(() => ProbeAsync(account.Imap, cancellationToken), MailProtocol.Imap, account.Imap, null).ConfigureAwait(false)
                : Outcome.Skip("not tested (DNS lookup failed)");
            var smtpTcp = smtpDns.Ok
                ? await TryAsync(() => ProbeAsync(account.Smtp, cancellationToken), MailProtocol.Smtp, account.Smtp, null).ConfigureAwait(false)
                : Outcome.Skip("not tested (DNS lookup failed)");
            Report(ConnectionTestStepKind.Tcp, Combine(imapTcp, smtpTcp),
                Join($"IMAP port {account.Imap.Port}: {imapTcp.Detail}", $"SMTP port {account.Smtp.Port}: {smtpTcp.Detail}"));

            // 3. TLS
            Report(ConnectionTestStepKind.Tls, ConnectionTestStatus.Running, "Negotiating encrypted connections…");
            var imapTls = Outcome.Skip("not tested (server unreachable)");
            if (imapTcp.Ok)
            {
                imap = clientFactory.CreateImapClient(imapRecorder);
                var client = imap;
                imapTls = await TryAsync(async () =>
                {
                    await client.ConnectAsync(account.Imap.Host, account.Imap.Port, SecurityOptions.ToSecureSocketOptions(account.Imap.Security), cancellationToken).ConfigureAwait(false);
                    return DescribeTls(client, imapRecorder);
                }, MailProtocol.Imap, account.Imap, imapRecorder).ConfigureAwait(false);
            }
            var smtpTls = Outcome.Skip("not tested (server unreachable)");
            if (smtpTcp.Ok)
            {
                smtp = clientFactory.CreateSmtpClient(smtpRecorder);
                var client = smtp;
                smtpTls = await TryAsync(async () =>
                {
                    await client.ConnectAsync(account.Smtp.Host, account.Smtp.Port, SecurityOptions.ToSecureSocketOptions(account.Smtp.Security), cancellationToken).ConfigureAwait(false);
                    return DescribeTls(client, smtpRecorder);
                }, MailProtocol.Smtp, account.Smtp, smtpRecorder).ConfigureAwait(false);
            }
            Report(ConnectionTestStepKind.Tls, Combine(imapTls, smtpTls), Join($"IMAP: {imapTls.Detail}", $"SMTP: {smtpTls.Detail}"));

            // 4. IMAP authentication
            if (imapTls.Ok && imap is not null)
            {
                Report(ConnectionTestStepKind.ImapAuthentication, ConnectionTestStatus.Running, "Signing in…");
                var client = imap;
                var auth = await TryAsync(async () =>
                {
                    await client.AuthenticateAsync(new NetworkCredential(account.Username, password), cancellationToken).ConfigureAwait(false);
                    return $"Signed in as {account.Username}.";
                }, MailProtocol.Imap, account.Imap, imapRecorder).ConfigureAwait(false);
                Report(ConnectionTestStepKind.ImapAuthentication, auth.Status, auth.Detail);

                // 5. Folder access
                if (auth.Ok)
                {
                    Report(ConnectionTestStepKind.FolderAccess, ConnectionTestStatus.Running, "Reading mailbox folders…");
                    var folders = await TryAsync(() => DescribeFoldersAsync(client, cancellationToken), MailProtocol.Imap, account.Imap, imapRecorder).ConfigureAwait(false);
                    Report(ConnectionTestStepKind.FolderAccess, folders.Status, folders.Detail);
                }
                else
                {
                    Report(ConnectionTestStepKind.FolderAccess, ConnectionTestStatus.Skipped, "Not tested because sign-in failed.");
                }
            }
            else
            {
                Report(ConnectionTestStepKind.ImapAuthentication, ConnectionTestStatus.Skipped, "Not tested because no secure IMAP connection was established.");
                Report(ConnectionTestStepKind.FolderAccess, ConnectionTestStatus.Skipped, "Not tested because no secure IMAP connection was established.");
            }

            // 6. SMTP authentication (no message is sent)
            if (smtpTls.Ok && smtp is not null)
            {
                Report(ConnectionTestStepKind.Smtp, ConnectionTestStatus.Running, "Signing in to the outgoing server…");
                var client = smtp;
                var smtpAuth = await TryAsync(async () =>
                {
                    if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                        throw new MailOperationException(MailErrorKind.Authentication, "The outgoing server does not offer authentication on this port.");
                    await client.AuthenticateAsync(new NetworkCredential(account.Username, password), cancellationToken).ConfigureAwait(false);
                    return $"Signed in as {account.Username}. No test email was sent.";
                }, MailProtocol.Smtp, account.Smtp, smtpRecorder).ConfigureAwait(false);
                Report(ConnectionTestStepKind.Smtp, smtpAuth.Status, smtpAuth.Detail);
            }
            else
            {
                Report(ConnectionTestStepKind.Smtp, ConnectionTestStatus.Skipped, "Not tested because no secure SMTP connection was established.");
            }
        }
        finally
        {
            await CloseAsync(imap).ConfigureAwait(false);
            await CloseAsync(smtp).ConfigureAwait(false);
        }

        var report = new ConnectionTestReport(steps.Values.OrderBy(s => s.Kind).ToList());
        logger.LogInformation("Connection test for account {AccountId}: {Result}", account.Id,
            string.Join(", ", report.Steps.Select(s => $"{s.Kind}={s.Status}")));
        return report;
    }

    private async Task<string> ResolveAsync(string host, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StepTimeout);
        IPAddress[] addresses;
        try
        {
            addresses = await dns.ResolveAsync(host, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("DNS lookup timed out.");
        }
        if (addresses.Length == 0)
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        return "found " + string.Join(", ", addresses.Take(3).Select(a => a.ToString()));
    }

    private async Task<string> ProbeAsync(ServerEndpoint endpoint, CancellationToken ct)
    {
        await tcp.ConnectAsync(endpoint.Host, endpoint.Port, StepTimeout, ct).ConfigureAwait(false);
        return "reachable";
    }

    private static string DescribeTls(IMailService client, TlsValidationRecorder recorder)
    {
        if (!client.IsSecure)
            throw new MailOperationException(MailErrorKind.Tls, "The connection is not encrypted.");
        var protocol = (client as MailService)?.SslProtocol.ToString().Replace("Tls", "TLS ").Replace("13", "1.3").Replace("12", "1.2") ?? "TLS";
        return $"{protocol} established, certificate valid ({recorder.DescribeCertificate()}).";
    }

    private static async Task<string> DescribeFoldersAsync(IImapClient client, CancellationToken ct)
    {
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : null;
        var folders = ns is null ? [] : await client.GetFoldersAsync(ns, subscribedOnly: false, ct).ConfigureAwait(false);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var inboxCount = client.Inbox.Count;

        var mapped = FolderRoleMapper.AssignRoles(folders.Select(f => new FolderDescriptor(
            f.FullName, f.Name, f.DirectorySeparator, FlagMapper.ToSpecialUse(f.Attributes), !f.Attributes.HasFlag(FolderAttributes.NoSelect))));
        var roles = mapped.Where(f => f.Role is not (FolderRole.None or FolderRole.Inbox)).Select(f => f.DisplayName).ToList();
        var rolesText = roles.Count > 0 ? $" Special folders: {string.Join(", ", roles)}." : " No Sent/Drafts/Trash folders were identified.";
        return $"Inbox opened ({inboxCount} message{(inboxCount == 1 ? "" : "s")}); {folders.Count} folder{(folders.Count == 1 ? "" : "s")} found.{rolesText}";
    }

    private async Task CloseAsync(IMailService? client)
    {
        if (client is null)
            return;
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
            logger.LogDebug(ex, "Ignoring error while closing test connection");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task<Outcome> TryAsync(Func<Task<string>> action, MailProtocol protocol, ServerEndpoint endpoint, TlsValidationRecorder? recorder)
    {
        try
        {
            return Outcome.Pass(await action().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Outcome.Fail(MailErrorTranslator.Translate(ex, protocol, endpoint, recorder).UserMessage);
        }
    }

    private static ConnectionTestStatus Combine(Outcome a, Outcome b) =>
        a.Status == ConnectionTestStatus.Failed || b.Status == ConnectionTestStatus.Failed ? ConnectionTestStatus.Failed
        : a.Ok && b.Ok ? ConnectionTestStatus.Passed
        : ConnectionTestStatus.Skipped;

    private static ConnectionTestStatus Combine(bool a, bool b) => a && b ? ConnectionTestStatus.Passed : ConnectionTestStatus.Failed;

    private static string Join(string a, string b) => a + "\n" + b;

    private sealed record Outcome(ConnectionTestStatus Status, string Detail)
    {
        public bool Ok => Status == ConnectionTestStatus.Passed;

        public static Outcome Pass(string detail) => new(ConnectionTestStatus.Passed, detail);

        public static Outcome Fail(string detail) => new(ConnectionTestStatus.Failed, detail);

        public static Outcome Skip(string detail) => new(ConnectionTestStatus.Skipped, detail);
    }
}
