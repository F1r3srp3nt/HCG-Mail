using System.Diagnostics;
using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Integration;

/// <summary>
/// Runs only when HCG_MAIL_E2E=1, against the disposable local Dovecot/Postfix server created by
/// tests/integration-server/setup-local-mail-server.sh. Never point these tests at a real mailbox.
/// </summary>
public sealed class LocalMailServerFactAttribute : FactAttribute
{
    public LocalMailServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HCG_MAIL_E2E") != "1")
            Skip = "Set HCG_MAIL_E2E=1 and start tests/integration-server/setup-local-mail-server.sh to run end-to-end tests.";
    }
}

[Collection("LocalMailServer")]
public class LocalMailServerTests
{
    private const string Host = "mail.hcgmail.test";
    private static readonly MailKitMailSessionFactory Factory = new(new MailClientFactory(), new HtmlToTextConverter(), NullLoggerFactory.Instance);

    private static MailAccount Account(string user, MailSecurityMode mode = MailSecurityMode.SslTls, string host = Host) => new()
    {
        Id = "e2e-" + user,
        DisplayName = char.ToUpperInvariant(user[0]) + user[1..] + " Test",
        EmailAddress = $"{user}@hcgmail.test",
        Username = $"{user}@hcgmail.test",
        Imap = new ServerEndpoint(host, PortDefaults.Imap(mode), mode),
        Smtp = new ServerEndpoint(host, PortDefaults.Smtp(mode), mode),
    };

    private static string Password(string user) => $"{user}-test-password";

    private static ConnectionTester Tester() => new(new SystemDnsResolver(), new SystemTcpProbe(), new MailClientFactory(), NullLogger<ConnectionTester>.Instance);

    private static string Describe(ConnectionTestReport r) => string.Join("\n", r.Steps.Select(s => $"{s.Kind}: {s.Status} — {s.Detail}"));

    private static async Task<MessageSummary> WaitForAsync(IMailSession session, string folder, Func<MessageSummary, bool> match, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var page = await session.GetMessagesAsync(folder, 0, 100);
            var found = page.Messages.FirstOrDefault(match);
            if (found is not null)
                return found;
            await Task.Delay(500);
        }
        throw new TimeoutException("Message did not arrive in " + folder);
    }

    private static async Task<string> FolderAsync(IMailSession session, FolderRole role) =>
        (await session.GetFoldersAsync()).Single(f => f.Role == role).FullName;

    // ---- connection test ---------------------------------------------------------------------

    [LocalMailServerFact]
    public async Task Connection_test_passes_with_ssl_tls()
    {
        var report = await Tester().RunAsync(Account("ola"), Password("ola"));
        Assert.True(report.Succeeded, Describe(report));
        Assert.Contains("TLS 1.3", report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Tls).Detail);
        Assert.Contains("Sent", report.Steps.Single(s => s.Kind == ConnectionTestStepKind.FolderAccess).Detail);
    }

    [LocalMailServerFact]
    public async Task Connection_test_passes_with_starttls()
    {
        var report = await Tester().RunAsync(Account("ola", MailSecurityMode.StartTls), Password("ola"));
        Assert.True(report.Succeeded, Describe(report));
    }

    [LocalMailServerFact]
    public async Task Invalid_password_is_reported_for_imap_and_smtp()
    {
        var report = await Tester().RunAsync(Account("ola"), "wrong-password");
        Assert.Equal(ConnectionTestStatus.Passed, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Tls).Status);
        Assert.Equal(ConnectionTestStatus.Failed, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.ImapAuthentication).Status);
        Assert.Equal(ConnectionTestStatus.Failed, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Smtp).Status);
        Assert.Contains("rejected the username or password", Describe(report));
    }

    [LocalMailServerFact]
    public async Task Invalid_hostname_fails_at_dns()
    {
        var report = await Tester().RunAsync(Account("ola", host: "does-not-exist.hcgmail.test"), Password("ola"));
        Assert.Equal(ConnectionTestStatus.Failed, report.Steps[0].Status);
        Assert.Contains("could not be found", report.Steps[0].Detail);
    }

    [LocalMailServerFact]
    public async Task Certificate_for_another_name_is_refused()
    {
        // 127.0.0.1 reaches the same server, but its certificate is issued for mail.hcgmail.test.
        var report = await Tester().RunAsync(Account("ola", host: "127.0.0.1"), Password("ola"));
        var tls = report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Tls);
        Assert.Equal(ConnectionTestStatus.Failed, tls.Status);
        Assert.Contains("does not match", tls.Detail);
        Assert.Equal(ConnectionTestStatus.Skipped, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.ImapAuthentication).Status);
    }

    [LocalMailServerFact]
    public async Task Security_mode_that_does_not_match_the_port_fails_safely()
    {
        var account = Account("ola") with { Imap = new ServerEndpoint(Host, 143, MailSecurityMode.SslTls) };
        var report = await Tester().RunAsync(account, Password("ola"));
        Assert.Equal(ConnectionTestStatus.Failed, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.Tls).Status);
        Assert.Equal(ConnectionTestStatus.Skipped, report.Steps.Single(s => s.Kind == ConnectionTestStepKind.ImapAuthentication).Status);
    }

    // ---- receiving, sending, folders ---------------------------------------------------------

    [LocalMailServerFact]
    public async Task Send_receive_read_attachment_and_sent_copy_without_duplicates()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await using var ola = Factory.Create(Account("ola"), Password("ola"));
        await using var kari = Factory.Create(Account("kari"), Password("kari"));
        await ola.ConnectAsync();
        await kari.ConnectAsync();

        var folders = await ola.GetFoldersAsync();
        foreach (var role in FolderRoleMapper.StandardRoles)
            Assert.Contains(folders, f => f.Role == role);

        var payload = Encoding.UTF8.GetBytes("Kvartalsrapport æøå " + token);
        var result = await ola.SendAsync(new OutgoingMessage
        {
            To = [new EmailAddress("Kari Test", "kari@hcgmail.test")],
            Cc = [new EmailAddress(null, "ola@hcgmail.test")],
            Subject = $"E2E {token} — rapport",
            TextBody = "Hei Kari,\n\nVedlagt.\n",
            Attachments = [OutgoingAttachment.FromBytes("../rapport.txt", null, payload)],
        }, saveCopyToSent: true);
        Assert.True(result.SavedToSent, result.Warning);
        Assert.Null(result.Warning);

        var received = await WaitForAsync(kari, "INBOX", m => m.Subject.Contains(token));
        Assert.False(received.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(received.HasAttachments);
        Assert.Equal("ola@hcgmail.test", received.From?.Address);
        Assert.Contains("Vedlagt", received.Preview);

        var content = await kari.GetMessageAsync(received.Ref);
        Assert.Equal($"E2E {token} — rapport", content.Subject);
        Assert.Contains("Hei Kari", content.TextBody);
        Assert.Equal("ola@hcgmail.test", Assert.Single(content.Cc).Address);
        var attachment = Assert.Single(content.Attachments);
        Assert.Equal("rapport.txt", attachment.SafeFileName);
        Assert.Equal(payload, await kari.GetAttachmentBytesAsync(received.Ref, attachment.Index));

        // Reading via BODY.PEEK must not mark the message read by itself.
        var afterRead = await WaitForAsync(kari, "INBOX", m => m.Subject.Contains(token));
        Assert.False(afterRead.Flags.HasFlag(MessageFlags.Seen));

        var sent = await FolderAsync(ola, FolderRole.Sent);
        var sentPage = await ola.GetMessagesAsync(sent, 0, 200);
        var copy = Assert.Single(sentPage.Messages, m => m.Subject.Contains(token));
        Assert.True(copy.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(copy.HasAttachments);
    }

    [LocalMailServerFact]
    public async Task Reply_all_threads_and_flags_original_as_answered()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await using var ola = Factory.Create(Account("ola"), Password("ola"));
        await using var kari = Factory.Create(Account("kari"), Password("kari"));

        await ola.SendAsync(new OutgoingMessage
        {
            To = [new EmailAddress(null, "kari@hcgmail.test")],
            Subject = $"Plan {token}",
            TextBody = "Original text",
        }, saveCopyToSent: false);

        var inKari = await WaitForAsync(kari, "INBOX", m => m.Subject == $"Plan {token}");
        var original = await kari.GetMessageAsync(inKari.Ref);
        var reply = ReplyBuilder.Reply(original, ["kari@hcgmail.test"], replyAll: true);
        Assert.Equal(["ola@hcgmail.test"], reply.To.Select(a => a.Address));
        await kari.SendAsync(reply, saveCopyToSent: true);

        var inOla = await WaitForAsync(ola, "INBOX", m => m.Subject == $"Re: Plan {token}");
        var replyContent = await ola.GetMessageAsync(inOla.Ref);
        Assert.Equal(original.MessageId, replyContent.InReplyTo);
        Assert.Contains(original.MessageId!, replyContent.References);
        Assert.Contains("> Original text", replyContent.TextBody);

        var answered = await WaitForAsync(kari, "INBOX", m => m.Subject == $"Plan {token}");
        Assert.True(answered.Flags.HasFlag(MessageFlags.Answered));
    }

    [LocalMailServerFact]
    public async Task Flags_trash_permanent_delete_and_archive()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await using var ola = Factory.Create(Account("ola"), Password("ola"));
        await using var kari = Factory.Create(Account("kari"), Password("kari"));
        foreach (var n in new[] { 1, 2 })
            await ola.SendAsync(new OutgoingMessage { To = [new EmailAddress(null, "kari@hcgmail.test")], Subject = $"Flags {token} #{n}", TextBody = "x" }, false);

        var first = await WaitForAsync(kari, "INBOX", m => m.Subject == $"Flags {token} #1");
        var second = await WaitForAsync(kari, "INBOX", m => m.Subject == $"Flags {token} #2");

        await kari.SetFlagsAsync([first.Ref], MessageFlags.Seen | MessageFlags.Flagged, add: true);
        var updated = await WaitForAsync(kari, "INBOX", m => m.Ref == first.Ref);
        Assert.True(updated.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(updated.Flags.HasFlag(MessageFlags.Flagged));
        await kari.SetFlagsAsync([first.Ref], MessageFlags.Seen, add: false);
        Assert.False((await WaitForAsync(kari, "INBOX", m => m.Ref == first.Ref)).Flags.HasFlag(MessageFlags.Seen));

        // Delete → Trash, then delete in Trash → gone for good.
        await kari.DeleteAsync(first.Ref, permanent: false);
        var trash = await FolderAsync(kari, FolderRole.Trash);
        var inTrash = await WaitForAsync(kari, trash, m => m.Subject == $"Flags {token} #1");
        Assert.DoesNotContain((await kari.GetMessagesAsync("INBOX", 0, 200)).Messages, m => m.Subject == $"Flags {token} #1");
        await kari.DeleteAsync(inTrash.Ref, permanent: true);
        Assert.DoesNotContain((await kari.GetMessagesAsync(trash, 0, 200)).Messages, m => m.Subject == $"Flags {token} #1");

        // Archive (move).
        var archive = await FolderAsync(kari, FolderRole.Archive);
        await kari.MoveAsync(second.Ref, archive);
        await WaitForAsync(kari, archive, m => m.Subject == $"Flags {token} #2");

        // Unread counts are reported per folder.
        var folders = await kari.GetFoldersAsync();
        Assert.All(folders.Where(f => f.IsSelectable), f => Assert.NotNull(f.UnreadCount));

        // Acting on a message that is gone gives a clear error.
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => kari.GetMessageAsync(first.Ref));
        Assert.Equal(MailErrorKind.MessageNotFound, ex.Kind);
    }

    [LocalMailServerFact]
    public async Task Drafts_are_stored_on_server_replaced_on_resave_and_removed_on_send()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await using var ola = Factory.Create(Account("ola"), Password("ola"));
        await using var kari = Factory.Create(Account("kari"), Password("kari"));
        var drafts = await FolderAsync(ola, FolderRole.Drafts);

        var draft = new OutgoingMessage
        {
            To = [new EmailAddress(null, "kari@hcgmail.test")],
            Subject = $"Draft {token}",
            TextBody = "v1",
            MessageId = $"{token}@hcgmail.test",
            Attachments = [OutgoingAttachment.FromBytes("notes.txt", "text/plain", [65, 66])],
        };
        var saved1 = await ola.SaveDraftAsync(draft);
        Assert.NotNull(saved1.DraftRef);

        var saved2 = await ola.SaveDraftAsync(draft with { TextBody = "v2", DraftRef = saved1.DraftRef });
        var stored = (await ola.GetMessagesAsync(drafts, 0, 200)).Messages.Where(m => m.Subject == $"Draft {token}").ToList();
        var only = Assert.Single(stored);
        Assert.True(only.Flags.HasFlag(MessageFlags.Draft));
        Assert.Equal(saved2.DraftRef, only.Ref);

        var loaded = await ola.LoadDraftAsync(only.Ref);
        Assert.Equal(ComposeKind.EditDraft, loaded.Kind);
        Assert.Equal("v2", loaded.TextBody.Trim());
        Assert.Equal("kari@hcgmail.test", Assert.Single(loaded.To).Address);
        Assert.Equal("notes.txt", Assert.Single(loaded.Attachments).FileName);

        await ola.SendAsync(loaded, saveCopyToSent: true);
        Assert.DoesNotContain((await ola.GetMessagesAsync(drafts, 0, 200)).Messages, m => m.Subject == $"Draft {token}");
        await WaitForAsync(kari, "INBOX", m => m.Subject == $"Draft {token}");
    }

    [LocalMailServerFact]
    public async Task Session_reconnects_after_the_server_drops_the_connection()
    {
        await using var ola = Factory.Create(Account("ola"), Password("ola"));
        await ola.GetFoldersAsync();
        Assert.Equal(MailConnectionState.Connected, ola.State);
        var states = new List<MailConnectionState>();
        ola.StateChanged += (_, s) => states.Add(s);

        // Forcibly disconnect every IMAP session of this user on the server.
        using (var kick = Process.Start(new ProcessStartInfo("doveadm", "-c /srv/hcgmail-test/dovecot.conf kick ola@hcgmail.test")
               { RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await kick.WaitForExitAsync();
            Assert.Equal(0, kick.ExitCode);
        }
        await Task.Delay(500);

        var page = await ola.GetMessagesAsync("INBOX", 0, 10);
        Assert.True(page.TotalCount >= 0);
        Assert.Equal(MailConnectionState.Connected, ola.State);
        Assert.Contains(MailConnectionState.Connecting, states);
    }

    [LocalMailServerFact]
    public async Task Wrong_password_session_reports_authentication_error()
    {
        await using var session = Factory.Create(Account("ola"), "definitely-wrong");
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync());
        Assert.Equal(MailErrorKind.Authentication, ex.Kind);
        Assert.Equal(MailConnectionState.Failed, session.State);
    }

    [LocalMailServerFact]
    public async Task Rejected_recipient_is_reported_as_a_failed_send_with_the_server_reason()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await using var ola = Factory.Create(Account("ola"), Password("ola"));
        // The test server refuses every non-local recipient (nothing ever leaves the machine).
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => ola.SendAsync(new OutgoingMessage
        {
            To = [new EmailAddress(null, "someone@example.com")],
            Subject = $"External {token}",
            TextBody = "x",
        }, saveCopyToSent: true));

        Assert.Equal(MailErrorKind.ServerRejected, ex.Kind);
        Assert.Contains("someone@example.com", ex.UserMessage);
        Assert.Contains("Recipient address rejected", ex.UserMessage);
        var sent = await FolderAsync(ola, FolderRole.Sent);
        Assert.DoesNotContain((await ola.GetMessagesAsync(sent, 0, 200)).Messages, m => m.Subject.Contains(token));
    }
}
