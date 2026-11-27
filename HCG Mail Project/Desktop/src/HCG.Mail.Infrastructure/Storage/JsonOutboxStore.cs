using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;
using HCG.Mail.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>
/// The Outbox on disk: one private file per waiting message or draft, written atomically (a crash never leaves half a
/// message), with its attachments inside so the originals can be moved or deleted. The message is encrypted with the
/// account's own Outbox key (<see cref="AccountDataProtector"/>, kept only in the credential store); the envelope shows only
/// the item id and the account it belongs to, and the encryption binds the message to both, so an item cannot be read,
/// or sent, as another account's.
/// </summary>
public sealed class JsonOutboxStore(AppPaths paths, AccountDataProtector protector, ILogger<JsonOutboxStore> logger) : IOutboxStore
{
    public const string Purpose = "outbox";
    private const int FileVersion = 2;
    private readonly object _lock = new();

    public IReadOnlyList<OutboxItem> Load()
    {
        lock (_lock)
        {
            if (!Directory.Exists(paths.OutboxDirectory))
                return [];
            var items = new List<OutboxItem>();
            foreach (var file in Directory.EnumerateFiles(paths.OutboxDirectory, "*.json"))
            {
                try
                {
                    if (JsonFile.Read(file, MailStorageJsonContext.Default.OutboxEnvelope, logger) is not { } envelope)
                        continue;
                    var plain = protector.Unprotect(Purpose, envelope.AccountId, envelope.Id, Convert.FromBase64String(envelope.Payload));
                    if (plain is null)
                    {
                        logger.LogWarning("An Outbox item of account {AccountId} cannot be decrypted (its key is gone); it is not sent", envelope.AccountId);
                        continue;
                    }
                    var f = System.Text.Json.JsonSerializer.Deserialize(plain, MailStorageJsonContext.Default.OutboxFile);
                    if (f is null || f.Id != envelope.Id || f.AccountId != envelope.AccountId)
                        continue; // never trust an item whose inner identity differs from its envelope
                    items.Add(ToItem(f));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException
                                               or Core.Security.CredentialVaultException)
                {
                    logger.LogWarning("Could not read an Outbox item ({Error})", ex.GetType().Name);
                }
            }
            return items.OrderBy(i => i.QueuedAt).ToList();
        }
    }

    public void Put(OutboxItem item)
    {
        var path = PathFor(item.Id);
        var inner = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(ToFile(item), MailStorageJsonContext.Default.OutboxFile);
        var envelope = new OutboxEnvelope(FileVersion, item.Id, item.AccountId, Convert.ToBase64String(protector.Protect(Purpose, item.AccountId, item.Id, inner)));
        lock (_lock)
            JsonFile.Write(path, envelope, MailStorageJsonContext.Default.OutboxEnvelope);
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var path = PathFor(id);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public void DeleteAccount(string accountId)
    {
        foreach (var item in Load().Where(i => i.AccountId == accountId))
            Remove(item.Id);
        protector.DeleteKey(Purpose, accountId);
    }

    internal sealed record OutboxEnvelope(int Version, string Id, string AccountId, string Payload);

    private string PathFor(string id)
    {
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid Outbox id.", nameof(id));
        return Path.Combine(paths.OutboxDirectory, id + ".json");
    }

    internal sealed record AttachmentDto(string FileName, string? ContentType, byte[] Data);

    internal sealed record MessageDto(
        List<EmailAddress> To,
        List<EmailAddress> Cc,
        List<EmailAddress> Bcc,
        string Subject,
        string TextBody,
        List<AttachmentDto> Attachments,
        MessagePriority Priority,
        ComposeKind Kind,
        string? InReplyTo,
        List<string> References,
        MessageRef? RepliedMessage,
        MessageRef? DraftRef,
        string? MessageId);

    internal sealed record OutboxFile(
        int Version,
        string Id,
        string AccountId,
        OutboxKind Kind,
        DateTimeOffset QueuedAt,
        OutboxState State,
        string? LastError,
        int Attempts,
        bool SaveCopyToSent,
        MessageDto Message);

    private static OutboxFile ToFile(OutboxItem i)
    {
        var m = i.Message;
        var attachments = m.Attachments.Select(a =>
        {
            using var stream = a.OpenRead();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new AttachmentDto(a.FileName, a.ContentType, buffer.ToArray());
        }).ToList();
        return new OutboxFile(FileVersion, i.Id, i.AccountId, i.Kind, i.QueuedAt, i.State, i.LastError, i.Attempts, i.SaveCopyToSent,
            new MessageDto([.. m.To], [.. m.Cc], [.. m.Bcc], m.Subject, m.TextBody, attachments, m.Priority, m.Kind, m.InReplyTo,
                [.. m.References], m.RepliedMessage, m.DraftRef, m.MessageId));
    }

    private static OutboxItem ToItem(OutboxFile f)
    {
        var d = f.Message;
        var message = new OutgoingMessage
        {
            To = d.To ?? [],
            Cc = d.Cc ?? [],
            Bcc = d.Bcc ?? [],
            Subject = d.Subject ?? "",
            TextBody = d.TextBody ?? "",
            Attachments = (d.Attachments ?? []).Select(a => OutgoingAttachment.FromBytes(a.FileName, a.ContentType, a.Data ?? [])).ToList(),
            Priority = d.Priority,
            Kind = d.Kind,
            InReplyTo = d.InReplyTo,
            References = d.References ?? [],
            RepliedMessage = d.RepliedMessage,
            DraftRef = d.DraftRef,
            MessageId = d.MessageId,
        };
        return new OutboxItem(f.Id, f.AccountId, f.Kind, message, f.QueuedAt, f.State, f.LastError, f.Attempts, f.SaveCopyToSent);
    }
}
