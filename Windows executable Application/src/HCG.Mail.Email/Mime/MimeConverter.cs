using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using MimeKit;
using MimeKit.Utils;
using CoreFlags = HCG.Mail.Core.Mail.MessageFlags;

namespace HCG.Mail.Email.Mime;

/// <summary>Converts between MimeKit messages and the Core mail model.</summary>
public static class MimeConverter
{
    private sealed record Classification(
        TextPart? Text,
        TextPart? Html,
        IReadOnlyList<MimeEntity> Attachments,
        IReadOnlyList<MimePart> Inline);

    public static MessageContent ToContent(MimeMessage message, MessageRef messageRef, CoreFlags flags, IHtmlToTextConverter htmlToText)
    {
        var parts = Classify(message);
        var html = parts.Html?.Text;
        var text = parts.Text?.Text;
        if (string.IsNullOrWhiteSpace(text) && html is not null)
            text = htmlToText.ToText(html);

        return new MessageContent
        {
            Ref = messageRef,
            From = message.From.Mailboxes.Select(ToAddress).FirstOrDefault(),
            ReplyTo = message.ReplyTo.Mailboxes.Select(ToAddress).ToList(),
            To = message.To.Mailboxes.Select(ToAddress).ToList(),
            Cc = message.Cc.Mailboxes.Select(ToAddress).ToList(),
            Bcc = message.Bcc.Mailboxes.Select(ToAddress).ToList(),
            Subject = message.Subject ?? "",
            Date = message.Date,
            MessageId = message.MessageId,
            InReplyTo = message.InReplyTo,
            References = message.References.ToList(),
            TextBody = text ?? "",
            HtmlBody = html,
            Attachments = parts.Attachments.Select((entity, i) => ToAttachmentInfo(entity, i)).ToList(),
            InlineResources = parts.Inline.Select(ToInlineResource).Where(r => r is not null).Select(r => r!).ToList(),
            Flags = flags,
        };
    }

    /// <summary>Attachment entities in a stable order; <see cref="AttachmentInfo.Index"/> indexes into this list.</summary>
    public static IReadOnlyList<MimeEntity> GetAttachments(MimeMessage message) => Classify(message).Attachments;

    public static async Task WriteAttachmentAsync(MimeEntity entity, Stream destination, CancellationToken cancellationToken)
    {
        switch (entity)
        {
            case MessagePart mp when mp.Message is not null:
                await mp.Message.WriteToAsync(destination, cancellationToken).ConfigureAwait(false);
                break;
            case MimePart part when part.Content is not null:
                await part.Content.DecodeToAsync(destination, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("The attachment has no content.");
        }
    }

    public static MimeMessage ToMimeMessage(OutgoingMessage outgoing, MailAccount account)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(account.DisplayName) ? null : account.DisplayName, account.EmailAddress));
        message.To.AddRange(outgoing.To.Select(ToMailbox));
        message.Cc.AddRange(outgoing.Cc.Select(ToMailbox));
        message.Bcc.AddRange(outgoing.Bcc.Select(ToMailbox));
        message.Subject = outgoing.Subject;
        message.Date = DateTimeOffset.Now;
        message.MessageId = outgoing.MessageId ?? NewMessageId(account);
        if (!string.IsNullOrEmpty(outgoing.InReplyTo))
            message.InReplyTo = outgoing.InReplyTo;
        foreach (var reference in outgoing.References)
            message.References.Add(reference);

        var builder = new BodyBuilder { TextBody = outgoing.TextBody };
        foreach (var attachment in outgoing.Attachments)
        {
            using var stream = attachment.OpenRead();
            var contentType = ContentType.Parse(attachment.ContentType ?? MimeTypes.GetMimeType(attachment.FileName));
            builder.Attachments.Add(attachment.FileName, stream, contentType);
        }
        message.Body = builder.ToMessageBody();
        return message;
    }

    public static async Task<OutgoingMessage> ToOutgoingAsync(MimeMessage message, MessageRef draftRef, IHtmlToTextConverter htmlToText, CancellationToken cancellationToken)
    {
        var parts = Classify(message);
        var text = parts.Text?.Text;
        if (string.IsNullOrWhiteSpace(text) && parts.Html is not null)
            text = htmlToText.ToText(parts.Html.Text);

        var attachments = new List<OutgoingAttachment>();
        for (var i = 0; i < parts.Attachments.Count; i++)
        {
            var entity = parts.Attachments[i];
            using var buffer = new MemoryStream();
            await WriteAttachmentAsync(entity, buffer, cancellationToken).ConfigureAwait(false);
            attachments.Add(OutgoingAttachment.FromBytes(FileNameOf(entity, i), entity.ContentType.MimeType, buffer.ToArray()));
        }

        return new OutgoingMessage
        {
            Kind = ComposeKind.EditDraft,
            To = message.To.Mailboxes.Select(ToAddress).ToList(),
            Cc = message.Cc.Mailboxes.Select(ToAddress).ToList(),
            Bcc = message.Bcc.Mailboxes.Select(ToAddress).ToList(),
            Subject = message.Subject ?? "",
            TextBody = text ?? "",
            Attachments = attachments,
            InReplyTo = message.InReplyTo,
            References = message.References.ToList(),
            MessageId = message.MessageId,
            DraftRef = draftRef,
        };
    }

    public static string NewMessageId(MailAccount account)
    {
        var at = account.EmailAddress.LastIndexOf('@');
        var domain = at >= 0 ? account.EmailAddress[(at + 1)..] : "hcg-mail.invalid";
        return MimeUtils.GenerateMessageId(domain);
    }

    public static EmailAddress ToAddress(MailboxAddress mailbox) =>
        new(string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name, mailbox.Address);

    public static MailboxAddress ToMailbox(EmailAddress address) => new(address.Name, address.Address);

    private static Classification Classify(MimeMessage message)
    {
        TextPart? text = null;
        TextPart? html = null;
        var candidates = new List<MimeEntity>();
        var images = new List<MimePart>();

        foreach (var entity in message.BodyParts)
        {
            if (entity is MessagePart)
            {
                candidates.Add(entity);
                continue;
            }
            if (entity is not MimePart part)
                continue;

            if (part is TextPart tp && !tp.IsAttachment)
            {
                if (tp.IsHtml && html is null)
                {
                    html = tp;
                    continue;
                }
                if (tp.IsPlain && text is null && tp.FileName is null)
                {
                    text = tp;
                    continue;
                }
            }

            if (!part.IsAttachment && !string.IsNullOrEmpty(part.ContentId)
                && part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(part);
                continue;
            }

            candidates.Add(part);
        }

        // Inline images that the HTML body actually references are shown in place; any others
        // are listed as attachments so nothing the sender included is hidden.
        var inline = new List<MimePart>();
        foreach (var image in images)
        {
            if (html is not null && html.Text.Contains("cid:" + image.ContentId, StringComparison.OrdinalIgnoreCase))
                inline.Add(image);
            else
                candidates.Add(image);
        }

        return new Classification(text, html, candidates, inline);
    }

    private static AttachmentInfo ToAttachmentInfo(MimeEntity entity, int index)
    {
        var original = FileNameOf(entity, index);
        var safe = AttachmentFileNames.Sanitize(original);
        long? size = entity is MimePart { Content.Stream: { CanSeek: true } s } part
            ? part.ContentTransferEncoding == ContentEncoding.Base64 ? s.Length * 3 / 4 : s.Length
            : null;
        return new AttachmentInfo(index, original, safe, entity.ContentType.MimeType, size, AttachmentFileNames.IsPotentiallyDangerous(safe));
    }

    private static string FileNameOf(MimeEntity entity, int index)
    {
        switch (entity)
        {
            case MimePart part when !string.IsNullOrWhiteSpace(part.FileName):
                return part.FileName;
            case MessagePart mp:
                var subject = mp.Message?.Subject;
                return (string.IsNullOrWhiteSpace(subject) ? "attached message" : subject) + ".eml";
            default:
                var ext = MimeTypes.TryGetExtension(entity.ContentType.MimeType, out var e) ? e : ".bin";
                return $"attachment-{index + 1}{ext}";
        }
    }

    private static InlineResource? ToInlineResource(MimePart part)
    {
        if (part.Content is null || string.IsNullOrEmpty(part.ContentId))
            return null;
        using var ms = new MemoryStream();
        part.Content.DecodeTo(ms);
        return new InlineResource(part.ContentId, part.ContentType.MimeType, ms.ToArray());
    }
}
