using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Mime;
using MimeKit;
using MimeKit.Utils;

namespace HCG.Mail.Tests.Email;

public class MimeConverterTests
{
    private static readonly MessageRef Ref = new("INBOX", 5, 1);
    private static readonly HtmlToTextConverter HtmlToText = new();

    private static MailAccount Account() => new()
    {
        Id = "a1",
        DisplayName = "Ola Nordmann",
        EmailAddress = "ola@firma.no",
        Username = "ola@firma.no",
        Imap = new ServerEndpoint("mail.firma.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("mail.firma.no", 465, MailSecurityMode.SslTls),
    };

    private static MimeMessage Parse(string raw) => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw.Replace("\n", "\r\n"))));

    [Fact]
    public void Parses_alternative_body_attachments_and_inline_images()
    {
        var builder = new BodyBuilder { TextBody = "Plain body" };
        var image = builder.LinkedResources.Add("logo.png", [0x89, 0x50, 0x4E, 0x47], new ContentType("image", "png"));
        image.ContentId = MimeUtils.GenerateMessageId();
        builder.HtmlBody = $"<p>Hello <img src=\"cid:{image.ContentId}\"></p>";
        builder.Attachments.Add("../../report.pdf", Encoding.ASCII.GetBytes("%PDF-1.4"), new ContentType("application", "pdf"));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress("Ola", "ola@firma.no"));
        message.Cc.Add(new MailboxAddress(null, "carol@example.com"));
        message.ReplyTo.Add(new MailboxAddress("Desk", "desk@example.com"));
        message.Subject = "Report";
        message.MessageId = "id-1@example.com";
        message.References.Add("id-0@example.com");
        message.Body = builder.ToMessageBody();

        var content = MimeConverter.ToContent(message, Ref, MessageFlags.Seen, HtmlToText);

        Assert.Equal(new EmailAddress("Alice", "alice@example.com"), content.From);
        Assert.Equal("ola@firma.no", Assert.Single(content.To).Address);
        Assert.Equal("carol@example.com", Assert.Single(content.Cc).Address);
        Assert.Equal("desk@example.com", Assert.Single(content.ReplyTo).Address);
        Assert.Equal("Plain body", content.TextBody.Trim());
        Assert.Contains("cid:", content.HtmlBody);
        Assert.Equal("id-1@example.com", content.MessageId);
        Assert.Equal(["id-0@example.com"], content.References);

        var attachment = Assert.Single(content.Attachments);
        Assert.Equal("report.pdf", attachment.SafeFileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.False(attachment.IsPotentiallyDangerous);

        var inline = Assert.Single(content.InlineResources);
        Assert.Equal(image.ContentId, inline.ContentId);
        Assert.Equal(4, inline.Data.Length);
    }

    [Fact]
    public void Html_only_message_gets_text_derived_from_html()
    {
        var message = Parse("""
            From: a@example.com
            To: b@example.com
            Subject: Html only
            MIME-Version: 1.0
            Content-Type: text/html; charset=utf-8

            <html><head><style>p{color:red}</style><script>evil()</script></head><body><p>Hello <b>world</b></p><ul><li>One</li><li>Two</li></ul></body></html>
            """);
        var content = MimeConverter.ToContent(message, Ref, MessageFlags.None, HtmlToText);
        Assert.NotNull(content.HtmlBody);
        Assert.Contains("Hello world", content.TextBody);
        Assert.Contains("• One", content.TextBody);
        Assert.DoesNotContain("evil", content.TextBody);
        Assert.DoesNotContain("color:red", content.TextBody);
    }

    [Fact]
    public void Plain_text_message_has_no_html_and_decodes_charset()
    {
        var message = Parse("""
            From: =?utf-8?q?Kj=C3=A6re_Venn?= <venn@example.no>
            To: b@example.com
            Subject: =?utf-8?q?Bl=C3=A5b=C3=A6r?=
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8
            Content-Transfer-Encoding: quoted-printable

            Hei p=C3=A5 deg
            """);
        var content = MimeConverter.ToContent(message, Ref, MessageFlags.None, HtmlToText);
        Assert.Null(content.HtmlBody);
        Assert.Equal("Blåbær", content.Subject);
        Assert.Equal("Kjære Venn", content.From!.Name);
        Assert.Equal("Hei på deg", content.TextBody.Trim());
        Assert.Empty(content.Attachments);
    }

    [Fact]
    public void Attached_message_and_unreferenced_inline_image_are_listed_as_attachments()
    {
        var inner = new MimeMessage();
        inner.From.Add(MailboxAddress.Parse("x@example.com"));
        inner.Subject = "Inner";
        inner.Body = new TextPart("plain") { Text = "inner" };

        var mixed = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "outer" },
            new MessagePart { Message = inner },
            new MimePart("image", "png") { ContentId = "orphan@x", Content = new MimeContent(new MemoryStream([1, 2, 3])) },
        };
        var message = new MimeMessage { Body = mixed, Subject = "Outer" };

        var content = MimeConverter.ToContent(message, Ref, MessageFlags.None, HtmlToText);
        Assert.Equal(2, content.Attachments.Count);
        Assert.Equal("Inner.eml", content.Attachments[0].SafeFileName);
        Assert.Empty(content.InlineResources);
    }

    [Fact]
    public void Dangerous_attachment_is_flagged()
    {
        var builder = new BodyBuilder { TextBody = "x" };
        builder.Attachments.Add("invoice.pdf.exe", [0x4D, 0x5A]);
        var message = new MimeMessage { Body = builder.ToMessageBody() };
        var content = MimeConverter.ToContent(message, Ref, MessageFlags.None, HtmlToText);
        Assert.True(Assert.Single(content.Attachments).IsPotentiallyDangerous);
    }

    [Fact]
    public async Task Attachment_content_is_decoded_when_saved()
    {
        var data = Encoding.UTF8.GetBytes("attachment payload æøå");
        var builder = new BodyBuilder { TextBody = "x" };
        builder.Attachments.Add("notes.txt", data);
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        using var output = new MemoryStream();
        await MimeConverter.WriteAttachmentAsync(MimeConverter.GetAttachments(message)[0], output, CancellationToken.None);
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void Builds_outgoing_message_with_headers_recipients_and_attachments()
    {
        var outgoing = new OutgoingMessage
        {
            To = [new EmailAddress("Alice", "alice@example.com")],
            Cc = [new EmailAddress(null, "carol@example.com")],
            Bcc = [new EmailAddress(null, "hidden@example.com")],
            Subject = "Re: Plan",
            TextBody = "Hei på deg",
            InReplyTo = "orig@example.com",
            References = ["root@example.com", "orig@example.com"],
            MessageId = "fixed@firma.no",
            Attachments = [OutgoingAttachment.FromBytes("plan.pdf", null, [1, 2, 3])],
        };

        var mime = MimeConverter.ToMimeMessage(outgoing, Account());

        Assert.Equal("\"Ola Nordmann\" <ola@firma.no>", mime.From.ToString());
        Assert.Equal("alice@example.com", mime.To.Mailboxes.Single().Address);
        Assert.Equal("carol@example.com", mime.Cc.Mailboxes.Single().Address);
        Assert.Equal("hidden@example.com", mime.Bcc.Mailboxes.Single().Address);
        Assert.Equal("fixed@firma.no", mime.MessageId);
        Assert.Equal("orig@example.com", mime.InReplyTo);
        Assert.Equal(["root@example.com", "orig@example.com"], mime.References);
        Assert.Equal("Hei på deg", mime.TextBody);
        var attachment = Assert.Single(mime.Attachments.OfType<MimePart>());
        Assert.Equal("plan.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
    }

    [Fact]
    public void Generated_message_id_uses_sender_domain()
    {
        var mime = MimeConverter.ToMimeMessage(new OutgoingMessage { To = [new EmailAddress(null, "a@example.com")] }, Account());
        Assert.EndsWith("@firma.no", mime.MessageId);
    }

    [Fact]
    public async Task Draft_round_trips_recipients_subject_body_and_attachments()
    {
        var outgoing = new OutgoingMessage
        {
            To = [new EmailAddress("Alice", "alice@example.com")],
            Bcc = [new EmailAddress(null, "hidden@example.com")],
            Subject = "Draft",
            TextBody = "Work in progress",
            MessageId = "draft@firma.no",
            Attachments = [OutgoingAttachment.FromBytes("a.txt", "text/plain", Encoding.UTF8.GetBytes("abc"))],
        };
        var mime = MimeConverter.ToMimeMessage(outgoing, Account());
        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream);
        stream.Position = 0;
        var reloaded = await MimeMessage.LoadAsync(stream);

        var draftRef = new MessageRef("Drafts", 9, 3);
        var draft = await MimeConverter.ToOutgoingAsync(reloaded, draftRef, HtmlToText, CancellationToken.None);

        Assert.Equal(ComposeKind.EditDraft, draft.Kind);
        Assert.Equal(draftRef, draft.DraftRef);
        Assert.Equal("alice@example.com", Assert.Single(draft.To).Address);
        Assert.Equal("hidden@example.com", Assert.Single(draft.Bcc).Address);
        Assert.Equal("Draft", draft.Subject);
        Assert.Equal("Work in progress", draft.TextBody.Trim());
        Assert.Equal("draft@firma.no", draft.MessageId);
        var att = Assert.Single(draft.Attachments);
        Assert.Equal("a.txt", att.FileName);
        Assert.Equal("abc", Encoding.UTF8.GetString(att.Data!));
    }
}
