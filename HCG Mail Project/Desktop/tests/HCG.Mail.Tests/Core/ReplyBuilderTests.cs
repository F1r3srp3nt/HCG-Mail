using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Mail;

namespace HCG.Mail.Tests.Core;

public class ReplyBuilderTests
{
    private const string Me = "me@firma.no";
    private static readonly EmailAddress MeAddr = new("Me", Me);
    private static readonly EmailAddress Alice = new("Alice", "alice@example.com");
    private static readonly EmailAddress Bob = new("Bob", "bob@example.com");
    private static readonly EmailAddress Carol = new("Carol", "carol@example.com");
    private static readonly EmailAddress Dave = new(null, "dave@example.com");

    private static MessageContent Original(
        EmailAddress? from = null,
        IReadOnlyList<EmailAddress>? to = null,
        IReadOnlyList<EmailAddress>? cc = null,
        IReadOnlyList<EmailAddress>? replyTo = null,
        string subject = "Quarterly figures") => new()
    {
        Ref = new MessageRef("INBOX", 42, 7),
        From = from ?? Alice,
        To = to ?? [MeAddr, Bob],
        Cc = cc ?? [Carol, new EmailAddress("Me again", "ME@firma.no")],
        ReplyTo = replyTo ?? [],
        Subject = subject,
        Date = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero),
        MessageId = "msg-2@example.com",
        References = ["msg-1@example.com"],
        TextBody = "Line one\nLine two",
    };

    [Fact]
    public void Reply_goes_only_to_sender()
    {
        var reply = ReplyBuilder.Reply(Original(), [Me], replyAll: false);
        Assert.Equal([Alice], reply.To);
        Assert.Empty(reply.Cc);
        Assert.Equal(ComposeKind.Reply, reply.Kind);
    }

    [Fact]
    public void Reply_honours_reply_to()
    {
        var reply = ReplyBuilder.Reply(Original(replyTo: [Dave]), [Me], replyAll: false);
        Assert.Equal([Dave], reply.To);
    }

    [Fact]
    public void Reply_all_includes_everyone_except_self_without_duplicates()
    {
        var reply = ReplyBuilder.Reply(Original(cc: [Carol, Bob, MeAddr]), [Me], replyAll: true);
        Assert.Equal(["alice@example.com", "bob@example.com"], reply.To.Select(a => a.Address));
        Assert.Equal(["carol@example.com"], reply.Cc.Select(a => a.Address));
        Assert.DoesNotContain(reply.To.Concat(reply.Cc), a => a.IsSameMailbox(Me));
    }

    [Fact]
    public void Reply_all_with_reply_to_uses_reply_to_and_keeps_other_recipients()
    {
        var reply = ReplyBuilder.Reply(Original(replyTo: [Dave]), [Me], replyAll: true);
        Assert.Equal(["dave@example.com", "bob@example.com"], reply.To.Select(a => a.Address));
        Assert.Equal(["carol@example.com"], reply.Cc.Select(a => a.Address));
    }

    [Fact]
    public void Replying_to_own_sent_message_targets_original_recipients()
    {
        var sent = Original(from: MeAddr, to: [Bob, Carol], cc: [Dave]);
        var reply = ReplyBuilder.Reply(sent, [Me], replyAll: false);
        Assert.Equal(["bob@example.com", "carol@example.com"], reply.To.Select(a => a.Address));

        var replyAll = ReplyBuilder.Reply(sent, [Me], replyAll: true);
        Assert.Equal(["bob@example.com", "carol@example.com"], replyAll.To.Select(a => a.Address));
        Assert.Equal(["dave@example.com"], replyAll.Cc.Select(a => a.Address));
    }

    [Fact]
    public void Reply_sets_threading_headers()
    {
        var reply = ReplyBuilder.Reply(Original(), [Me], replyAll: false);
        Assert.Equal("msg-2@example.com", reply.InReplyTo);
        Assert.Equal(["msg-1@example.com", "msg-2@example.com"], reply.References);
        Assert.Equal(new MessageRef("INBOX", 42, 7), reply.RepliedMessage);
    }

    [Theory]
    [InlineData("Quarterly figures", "Re: Quarterly figures")]
    [InlineData("Re: Quarterly figures", "Re: Quarterly figures")]
    [InlineData("RE: Quarterly figures", "RE: Quarterly figures")]
    [InlineData("SV: Kvartalstall", "SV: Kvartalstall")]
    [InlineData("", "Re: ")]
    public void Reply_subject_prefix_is_not_duplicated(string subject, string expected)
    {
        var reply = ReplyBuilder.Reply(Original(subject: subject), [Me], replyAll: false);
        Assert.Equal(expected.TrimEnd(), reply.Subject.TrimEnd());
    }

    [Fact]
    public void Reply_quotes_original_body()
    {
        var reply = ReplyBuilder.Reply(Original(), [Me], replyAll: false);
        Assert.Contains("wrote:\n> Line one\n> Line two", reply.TextBody);
        Assert.StartsWith("\n\n", reply.TextBody);
    }

    [Theory]
    [InlineData("Report", "Fwd: Report")]
    [InlineData("Fwd: Report", "Fwd: Report")]
    [InlineData("VS: Rapport", "VS: Rapport")]
    public void Forward_subject_and_body(string subject, string expected)
    {
        var forward = ReplyBuilder.Forward(Original(subject: subject), []);
        Assert.Equal(expected, forward.Subject);
        Assert.Empty(forward.To);
        Assert.Contains("---------- Forwarded message ----------", forward.TextBody);
        Assert.Contains("From: Alice <alice@example.com>", forward.TextBody);
        Assert.Contains("Line one", forward.TextBody);
        Assert.Null(forward.InReplyTo);
        Assert.Equal(ComposeKind.Forward, forward.Kind);
    }

    [Fact]
    public void Forward_carries_attachments()
    {
        var attachment = OutgoingAttachment.FromBytes("report.pdf", "application/pdf", [1, 2, 3]);
        var forward = ReplyBuilder.Forward(Original(), [attachment]);
        Assert.Same(attachment, Assert.Single(forward.Attachments));
    }

    [Fact]
    public void Long_reference_chains_are_bounded()
    {
        var refs = Enumerable.Range(1, 40).Select(i => $"m{i}@x").ToList();
        var original = Original() with { References = refs, MessageId = "m41@x" };
        var built = ReplyBuilder.BuildReferences(original);
        Assert.Equal(20, built.Count);
        Assert.Equal("m1@x", built[0]);
        Assert.Equal("m41@x", built[^1]);
    }
}
