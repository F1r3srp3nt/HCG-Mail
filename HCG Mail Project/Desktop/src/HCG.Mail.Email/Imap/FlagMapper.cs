using MailKit;
using CoreFlags = HCG.Mail.Core.Mail.MessageFlags;
using CoreSpecialUse = HCG.Mail.Core.Mail.FolderSpecialUse;

namespace HCG.Mail.Email.Imap;

internal static class FlagMapper
{
    public static CoreFlags ToCore(MessageFlags? flags)
    {
        var f = flags ?? MessageFlags.None;
        var result = CoreFlags.None;
        if (f.HasFlag(MessageFlags.Seen)) result |= CoreFlags.Seen;
        if (f.HasFlag(MessageFlags.Flagged)) result |= CoreFlags.Flagged;
        if (f.HasFlag(MessageFlags.Answered)) result |= CoreFlags.Answered;
        if (f.HasFlag(MessageFlags.Draft)) result |= CoreFlags.Draft;
        if (f.HasFlag(MessageFlags.Deleted)) result |= CoreFlags.Deleted;
        return result;
    }

    public static MessageFlags ToMailKit(CoreFlags flags)
    {
        var result = MessageFlags.None;
        if (flags.HasFlag(CoreFlags.Seen)) result |= MessageFlags.Seen;
        if (flags.HasFlag(CoreFlags.Flagged)) result |= MessageFlags.Flagged;
        if (flags.HasFlag(CoreFlags.Answered)) result |= MessageFlags.Answered;
        if (flags.HasFlag(CoreFlags.Draft)) result |= MessageFlags.Draft;
        if (flags.HasFlag(CoreFlags.Deleted)) result |= MessageFlags.Deleted;
        return result;
    }

    public static CoreSpecialUse ToSpecialUse(FolderAttributes attributes)
    {
        var result = CoreSpecialUse.None;
        if (attributes.HasFlag(FolderAttributes.Inbox)) result |= CoreSpecialUse.Inbox;
        if (attributes.HasFlag(FolderAttributes.Drafts)) result |= CoreSpecialUse.Drafts;
        if (attributes.HasFlag(FolderAttributes.Sent)) result |= CoreSpecialUse.Sent;
        if (attributes.HasFlag(FolderAttributes.Archive)) result |= CoreSpecialUse.Archive;
        if (attributes.HasFlag(FolderAttributes.Junk)) result |= CoreSpecialUse.Junk;
        if (attributes.HasFlag(FolderAttributes.Trash)) result |= CoreSpecialUse.Trash;
        if (attributes.HasFlag(FolderAttributes.All)) result |= CoreSpecialUse.All;
        if (attributes.HasFlag(FolderAttributes.Flagged)) result |= CoreSpecialUse.Flagged;
        if (attributes.HasFlag(FolderAttributes.Important)) result |= CoreSpecialUse.Important;
        return result;
    }
}
