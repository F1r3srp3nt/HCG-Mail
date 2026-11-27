using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace HCG.Mail.Tests.Integration;

/// <summary>
/// 1.0.0-beta.1 qualification scenarios against the disposable local Dovecot/Postfix server (real IMAP, SMTP and TLS
/// with certificate validation; see tests/integration-server). Each mailbox is a test user; nothing reaches a real
/// mailbox. Runs only with HCG_MAIL_E2E=1.
/// </summary>
[Collection("LocalMailServer")]
public class QualificationTests
{
    private const string Host = "mail.hcgmail.test";
    private static readonly MailKitMailSessionFactory Factory = new(new MailClientFactory(), new HtmlToTextConverter(), NullLoggerFactory.Instance);

    private static MailAccount Account(string user, string host = Host, int imapPort = 993) => new()
    {
        Id = "q-" + user,
        DisplayName = user,
        EmailAddress = $"{user}@hcgmail.test",
        Username = $"{user}@hcgmail.test",
        Imap = new ServerEndpoint(host, imapPort, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint(host, 465, MailSecurityMode.SslTls),
    };

    private static string Password(string user) => $"{user}-test-password";

    private static async Task<IMailSession> ConnectAsync(string user)
    {
        var session = Factory.Create(Account(user), Password(user));
        await session.ConnectAsync();
        return session;
    }

    private static string Token() => "q" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<MessageSummary> WaitForAsync(IMailSession session, string folder, string token, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var page = await session.GetMessagesAsync(folder, 0, 100);
            if (page.Messages.FirstOrDefault(m => m.Subject.Contains(token)) is { } found)
                return found;
            await Task.Delay(500);
        }
        throw new TimeoutException($"{token} did not arrive in {folder}");
    }

    private static async Task<string> FolderAsync(IMailSession session, FolderRole role) =>
        (await session.GetFoldersAsync()).Single(f => f.Role == role).FullName;

    private static async Task SendAsync(IMailSession from, string to, string subject, string body = "Qualification test", IReadOnlyList<OutgoingAttachment>? attachments = null) =>
        await from.SendAsync(new OutgoingMessage
        {
            To = [new EmailAddress(null, $"{to}@hcgmail.test")],
            Subject = subject,
            TextBody = body,
            Attachments = attachments ?? [],
        }, saveCopyToSent: true);

    /// <summary>Direct IMAP access for test setup only (creating a custom folder, bulk-loading a large mailbox).</summary>
    private static async Task<ImapClient> AdminAsync(string user)
    {
        var client = new ImapClient();
        await client.ConnectAsync(Host, 993, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync($"{user}@hcgmail.test", Password(user));
        return client;
    }

    [LocalMailServerFact]
    public async Task Incremental_sync_shows_a_new_incoming_message_and_updates_unread_counts()
    {
        await using var ola = await ConnectAsync("ola");
        await using var kari = await ConnectAsync("kari");
        var before = await ola.GetMessagesAsync("INBOX", 0, 50);

        var token = Token();
        await SendAsync(kari, "ola", "New while open " + token);
        var arrived = await WaitForAsync(ola, "INBOX", token);
        var after = await ola.GetMessagesAsync("INBOX", 0, 50);

        Assert.Equal(before.TotalCount + 1, after.TotalCount);
        Assert.Equal(before.UnreadCount + 1, after.UnreadCount);
        Assert.False(arrived.Flags.HasFlag(MessageFlags.Seen));
    }

    [LocalMailServerFact]
    public async Task Read_unread_and_flag_changes_persist_on_the_server_across_a_restart()
    {
        await using var kari = await ConnectAsync("kari");
        var token = Token();
        await SendAsync(kari, "ola", "Flags " + token);
        MessageRef reference;
        await using (var ola = await ConnectAsync("ola"))
        {
            reference = (await WaitForAsync(ola, "INBOX", token)).Ref;
            await ola.SetFlagsAsync([reference], MessageFlags.Seen | MessageFlags.Flagged, add: true);
            await ola.SetFlagsAsync([reference], MessageFlags.Seen, add: false); // back to unread, still flagged
        }

        // "Restart": a new session, as after closing and reopening HCG Mail.
        await using var restarted = await ConnectAsync("ola");
        var again = await WaitForAsync(restarted, "INBOX", token);
        Assert.False(again.Flags.HasFlag(MessageFlags.Seen));
        Assert.True(again.Flags.HasFlag(MessageFlags.Flagged));
    }

    [LocalMailServerFact]
    public async Task Forward_carries_the_original_attachment_to_the_new_recipient()
    {
        await using var kari = await ConnectAsync("kari");
        await using var ola = await ConnectAsync("ola");
        var token = Token();
        var file = Path.Combine(Path.GetTempPath(), $"invoice-{token}.pdf");
        await File.WriteAllBytesAsync(file, Encoding.ASCII.GetBytes("%PDF-1.4 qualification"));
        try
        {
            await SendAsync(kari, "ola", "Invoice " + token, attachments: [OutgoingAttachment.FromFile(file)]);
            var original = await ola.GetMessageAsync((await WaitForAsync(ola, "INBOX", token)).Ref);
            var bytes = await ola.GetAttachmentBytesAsync(original.Ref, 0);

            var forward = ReplyBuilder.Forward(original, [OutgoingAttachment.FromBytes(original.Attachments[0].SafeFileName, original.Attachments[0].ContentType, bytes)])
                with { To = [new EmailAddress(null, "kari@hcgmail.test")] };
            await ola.SendAsync(forward, saveCopyToSent: true);

            var received = await kari.GetMessageAsync((await WaitForAsync(kari, "INBOX", "Fwd: Invoice " + token)).Ref);
            Assert.Single(received.Attachments);
            Assert.Equal(bytes, await kari.GetAttachmentBytesAsync(received.Ref, 0));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [LocalMailServerFact]
    public async Task Messages_move_to_junk_and_to_a_custom_folder_and_back()
    {
        using (var admin = await AdminAsync("ola"))
        {
            var personal = admin.GetFolder(admin.PersonalNamespaces[0]);
            if (!(await personal.GetSubfoldersAsync()).Any(f => f.Name == "Projects"))
                await personal.CreateAsync("Projects", true);
            await admin.DisconnectAsync(true);
        }
        await using var kari = await ConnectAsync("kari");
        await using var ola = await ConnectAsync("ola");
        var token = Token();
        await SendAsync(kari, "ola", "Move me " + token);
        var message = await WaitForAsync(ola, "INBOX", token);

        var folders = await ola.GetFoldersAsync();
        var junk = folders.Single(f => f.Role == FolderRole.Junk).FullName;
        var projects = folders.Single(f => f.Name == "Projects").FullName;

        await ola.MoveAsync(message.Ref, junk);
        var inJunk = await WaitForAsync(ola, junk, token);
        await ola.MoveAsync(inJunk.Ref, projects);
        var inProjects = await WaitForAsync(ola, projects, token);
        await ola.MoveAsync(inProjects.Ref, "INBOX");
        await WaitForAsync(ola, "INBOX", token);
        Assert.DoesNotContain((await ola.GetMessagesAsync(projects, 0, 100)).Messages, m => m.Subject.Contains(token));
    }

    [LocalMailServerFact]
    public async Task Two_accounts_work_side_by_side_without_mixing_state()
    {
        await using var ola = await ConnectAsync("ola");
        await using var kari = await ConnectAsync("kari");
        var toKari = Token();
        var toOla = Token();
        await Task.WhenAll(SendAsync(ola, "kari", "To Kari " + toKari), SendAsync(kari, "ola", "To Ola " + toOla));
        await WaitForAsync(kari, "INBOX", toKari);
        await WaitForAsync(ola, "INBOX", toOla);
        Assert.DoesNotContain((await ola.GetMessagesAsync("INBOX", 0, 100)).Messages, m => m.Subject.Contains(toKari));
        Assert.DoesNotContain((await kari.GetMessagesAsync("INBOX", 0, 100)).Messages, m => m.Subject.Contains(toOla));
    }

    [LocalMailServerFact]
    public async Task A_large_mailbox_pages_consistently_and_server_search_reaches_old_messages()
    {
        const int count = 1500;
        var folderName = "Large-" + Token();
        var oldest = Token();
        using (var admin = await AdminAsync("ola"))
        {
            var personal = admin.GetFolder(admin.PersonalNamespaces[0]);
            var folder = await personal.CreateAsync(folderName, true);
            await folder.OpenAsync(MailKit.FolderAccess.ReadWrite);
            var start = DateTimeOffset.UtcNow.AddDays(-count);
            for (var i = 0; i < count; i++)
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Sender", $"sender{i % 40}@example.com"));
                message.To.Add(new MailboxAddress("Ola", "ola@hcgmail.test"));
                message.Subject = i == 0 ? "Oldest " + oldest : $"Bulk message {i}";
                message.Date = start.AddDays(i);
                message.Body = new TextPart("plain") { Text = $"Body {i}" };
                await folder.AppendAsync(new MailKit.AppendRequest(message, MailKit.MessageFlags.Seen));
            }
            await admin.DisconnectAsync(true);
        }

        await using var ola = await ConnectAsync("ola");
        var full = (await ola.GetFoldersAsync()).Single(f => f.Name == folderName).FullName;
        var first = await ola.GetMessagesAsync(full, 0, 150);
        var second = await ola.GetMessagesAsync(full, 150, 150);

        Assert.Equal(count, first.TotalCount);
        Assert.Equal(150, first.Messages.Count);
        Assert.Equal(150, second.Messages.Count);
        Assert.Empty(first.Messages.Select(m => m.Ref.Uid).Intersect(second.Messages.Select(m => m.Ref.Uid))); // no duplicates across pages
        Assert.True(first.Messages.Min(m => m.Date) >= second.Messages.Max(m => m.Date)); // newest first

        var found = await ola.SearchAsync(full, oldest, 100);
        Assert.Contains(found, m => m.Subject.Contains(oldest)); // beyond every loaded page
    }

    [LocalMailServerFact]
    public async Task An_unreachable_server_fails_quickly_with_a_clear_offline_error()
    {
        await using var session = Factory.Create(Account("ola", "127.0.0.1", imapPort: 1), Password("ola"));
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync());
        Assert.True(ex.IsTransient, ex.Kind.ToString());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }
}
