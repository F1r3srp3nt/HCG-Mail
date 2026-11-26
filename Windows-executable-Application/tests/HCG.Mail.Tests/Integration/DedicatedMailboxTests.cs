using System.Runtime.CompilerServices;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Integration;

/// <summary>
/// Authenticated IMAP and SMTP against a DEDICATED TEST MAILBOX (never a personal or customer mailbox), configured only
/// through environment variables (CI: encrypted repository secrets):
///   HCG_TEST_MAILBOX_ADDRESS, HCG_TEST_MAILBOX_PASSWORD (an app password where the provider requires one),
///   HCG_TEST_MAILBOX_USERNAME (default: the address),
///   HCG_TEST_MAILBOX_IMAP_HOST / _IMAP_PORT (default 993) / _IMAP_SECURITY (SslTls or StartTls, default SslTls),
///   HCG_TEST_MAILBOX_SMTP_HOST / _SMTP_PORT (default 465) / _SMTP_SECURITY (default SslTls).
/// Every message is sent only to the test mailbox itself and deleted afterwards. TLS certificate validation is the app's
/// normal, unmodified validation. Nothing here is printed or logged with the password.
/// </summary>
public sealed class DedicatedMailboxFactAttribute : FactAttribute
{
    public DedicatedMailboxFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (DedicatedMailbox.Account is null)
            Skip = "No dedicated test mailbox configured (HCG_TEST_MAILBOX_ADDRESS, HCG_TEST_MAILBOX_PASSWORD, *_IMAP_HOST, *_SMTP_HOST).";
    }
}

internal static class DedicatedMailbox
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("HCG_TEST_MAILBOX_" + name) is { Length: > 0 } v ? v.Trim() : null;

    public static string? Password => Environment.GetEnvironmentVariable("HCG_TEST_MAILBOX_PASSWORD") is { Length: > 0 } p ? p : null;

    public static MailAccount? Account
    {
        get
        {
            if (Env("ADDRESS") is not { } address || Password is null || Env("IMAP_HOST") is not { } imap || Env("SMTP_HOST") is not { } smtp)
                return null;
            var imapMode = Enum.Parse<MailSecurityMode>(Env("IMAP_SECURITY") ?? "SslTls", ignoreCase: true);
            var smtpMode = Enum.Parse<MailSecurityMode>(Env("SMTP_SECURITY") ?? "SslTls", ignoreCase: true);
            return new MailAccount
            {
                Id = "hcg-dedicated-test-mailbox",
                DisplayName = "HCG Mail test",
                EmailAddress = address,
                Username = Env("USERNAME") ?? address,
                Imap = new ServerEndpoint(imap, int.TryParse(Env("IMAP_PORT"), out var ip) ? ip : PortDefaults.Imap(imapMode), imapMode),
                Smtp = new ServerEndpoint(smtp, int.TryParse(Env("SMTP_PORT"), out var sp) ? sp : PortDefaults.Smtp(smtpMode), smtpMode),
            };
        }
    }
}

[Collection("DedicatedMailbox")]
public class DedicatedMailboxTests
{
    private static readonly MailKitMailSessionFactory Factory = new(new MailClientFactory(), new HtmlToTextConverter(), NullLoggerFactory.Instance);

    private static ConnectionTester Tester() => new(new SystemDnsResolver(), new SystemTcpProbe(), new MailClientFactory(), NullLogger<ConnectionTester>.Instance);

    private static string Describe(ConnectionTestReport r) => string.Join("\n", r.Steps.Select(s => $"{s.Kind}: {s.Status} — {s.Detail}"));

    private static async Task<MessageSummary?> FindAsync(IMailSession session, string folder, string token, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var page = await session.GetMessagesAsync(folder, 0, 100);
            if (page.Messages.FirstOrDefault(m => m.Subject.Contains(token)) is { } found)
                return found;
            await Task.Delay(2000);
        }
        return null;
    }

    [DedicatedMailboxFact]
    public async Task Connection_test_passes_dns_network_tls_imap_sign_in_folders_and_smtp_sign_in()
    {
        var report = await Tester().RunAsync(DedicatedMailbox.Account!, DedicatedMailbox.Password!);
        Assert.True(report.Succeeded, Describe(report));
        Assert.Contains(report.Steps, s => s.Kind == ConnectionTestStepKind.Tls && s.Status == ConnectionTestStatus.Passed);
    }

    [DedicatedMailboxFact]
    public async Task A_wrong_password_is_refused_by_imap_and_smtp()
    {
        // One attempt per protocol only: repeated failures could lock the test mailbox.
        var report = await Tester().RunAsync(DedicatedMailbox.Account!, DedicatedMailbox.Password! + "-wrong");
        Assert.False(report.Succeeded);
        Assert.Equal(ConnectionTestStatus.Passed, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Tls).Status);
        Assert.Equal(ConnectionTestStatus.Failed, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.ImapAuthentication).Status);
        Assert.Equal(ConnectionTestStatus.Failed, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Smtp).Status);
    }

    [DedicatedMailboxFact]
    public async Task Send_to_itself_over_smtp_receive_over_imap_read_and_clean_up()
    {
        var account = DedicatedMailbox.Account!;
        var token = "hcg-e2e-" + Guid.NewGuid().ToString("N")[..10];
        await using var session = Factory.Create(account, DedicatedMailbox.Password!);
        await session.ConnectAsync();

        // SendAsync throws if the server refuses the message; reaching the next line means SMTP accepted it.
        var result = await session.SendAsync(new OutgoingMessage
        {
            To = [new EmailAddress("HCG Mail test", account.EmailAddress)],
            Subject = $"HCG Mail Windows test {token}",
            TextBody = $"Automated HCG Mail test message {token}. Sent to this test mailbox only; it deletes itself.\n",
        }, saveCopyToSent: true);
        Assert.NotNull(result);

        var received = await FindAsync(session, "INBOX", token, seconds: 120);
        Assert.NotNull(received);
        var content = await session.GetMessageAsync(received!.Ref);
        Assert.Contains(token, content.TextBody);
        Assert.Equal(account.EmailAddress, content.From?.Address, ignoreCase: true);

        // Clean up: the received message, and the Sent copy if the server kept one.
        await session.DeleteAsync(received.Ref, permanent: true);
        var sent = (await session.GetFoldersAsync()).FirstOrDefault(f => f.Role == FolderRole.Sent)?.FullName;
        if (sent is not null && await FindAsync(session, sent, token, seconds: 20) is { } copy)
            await session.DeleteAsync(copy.Ref, permanent: true);
        Assert.Null(await FindAsync(session, "INBOX", token, seconds: 4));
    }

    [DedicatedMailboxFact]
    public async Task Password_in_the_platform_credential_store_and_account_survive_a_restart_and_sign_in_again()
    {
        var account = DedicatedMailbox.Account!;
        var dir = Directory.CreateTempSubdirectory("hcg-mailbox-restart-").FullName;
        var saved = account with { Id = "hcg-e2e-restart-" + Guid.NewGuid().ToString("N") };
        var key = saved.CredentialKey;
        try
        {
            // First run: the account is saved as JSON, the password in the OS credential store (Windows Credential Manager).
            var paths = new AppPaths(dir);
            new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Save([saved]);
            var vault = CredentialVaultFactory.CreateForCurrentPlatform();
            // Windows and macOS must have a persistent OS store; elsewhere (Linux CI) there is none to test.
            Assert.SkipUnless(vault.IsPersistent || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "No persistent credential store on this platform.");
            Assert.True(vault.IsPersistent, vault.StorageDescription);
            await vault.SetAsync(key, DedicatedMailbox.Password!);

            // "Restart": new store and vault instances read everything back, and the account signs in with it.
            var reloaded = Assert.Single(new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Load());
            var password = await CredentialVaultFactory.CreateForCurrentPlatform().GetAsync(reloaded.CredentialKey);
            Assert.NotNull(password);
            Assert.DoesNotContain(DedicatedMailbox.Password!, File.ReadAllText(paths.AccountsFile)); // never in the JSON file
            await using var session = Factory.Create(reloaded, password!);
            await session.ConnectAsync();
            Assert.Contains(await session.GetFoldersAsync(), f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await CredentialVaultFactory.CreateForCurrentPlatform().DeleteAsync(key);
            Directory.Delete(dir, recursive: true);
        }
    }
}
