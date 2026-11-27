using System.Net;
using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Email.Deliverability;
using HCG.Mail.Email.Imap;
using HCG.Mail.Email.Mime;
using HCG.Mail.Email.Smtp;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Storage;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MessagePriority = HCG.Mail.Core.Compose.MessagePriority;

namespace HCG.Mail.Tests.Email;

/// <summary>Priority headers, MIME output, Message-IDs, EHLO name and delivery-state classification.</summary>
public sealed class SendingTests
{
    private static readonly MailAccount Account = new()
    {
        Id = "a1",
        DisplayName = "Yasin Hakmi",
        EmailAddress = "post@hakmicapitalgroup.test",
        Username = "post@hakmicapitalgroup.test",
        Imap = new ServerEndpoint("mail.provider.test", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("mail.provider.test", 465, MailSecurityMode.SslTls),
    };

    private static OutgoingMessage Message(MessagePriority priority = MessagePriority.Normal, string body = "Hei Ola,\nmøtet er flyttet.") => new()
    {
        To = [new EmailAddress("Ola Nordmann", "ola@example.test")],
        Bcc = [new EmailAddress(null, "skjult@example.test")],
        Subject = "Møte på fredag",
        TextBody = body,
        Priority = priority,
    };

    private static string Transmitted(MimeMessage message) => Encoding.ASCII.GetString(MessageFormatValidator.SerializeForTransport(message));

    private static List<string> HeaderLines(string raw, string name) =>
        raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n").Where(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)).ToList();

    // ---- priority ----

    [Theory]
    [InlineData(MessagePriority.High, "high", "1", "urgent")]
    [InlineData(MessagePriority.Normal, "normal", "3", null)]
    [InlineData(MessagePriority.Low, "low", "5", null)]
    public void Priority_headers_have_exact_values_and_appear_once(MessagePriority priority, string importance, string xPriority, string? rfc2156)
    {
        var raw = Transmitted(MimeConverter.ToMimeMessage(Message(priority), Account));

        Assert.Equal([$"Importance: {importance}"], HeaderLines(raw, "Importance"));
        Assert.Equal([$"X-Priority: {xPriority}"], HeaderLines(raw, "X-Priority"));
        Assert.Equal(rfc2156 is null ? [] : [$"Priority: {rfc2156}"], HeaderLines(raw, "Priority"));
    }

    [Fact]
    public void Changing_priority_replaces_headers_without_duplicates()
    {
        var mime = MimeConverter.ToMimeMessage(Message(MessagePriority.High), Account);
        MimeConverter.ApplyPriority(mime, MessagePriority.Low);
        MimeConverter.ApplyPriority(mime, MessagePriority.Low);
        var raw = Transmitted(mime);
        Assert.Equal(["Importance: low"], HeaderLines(raw, "Importance"));
        Assert.Equal(["X-Priority: 5"], HeaderLines(raw, "X-Priority"));
        Assert.Empty(HeaderLines(raw, "Priority"));
    }

    [Theory]
    [InlineData("Importance: High", MessagePriority.High)]
    [InlineData("X-Priority: 1 (Highest)", MessagePriority.High)]  // Thunderbird / Apple Mail style
    [InlineData("X-Priority: 2 (High)", MessagePriority.High)]
    [InlineData("X-Priority: 5 (Lowest)", MessagePriority.Low)]
    [InlineData("Priority: non-urgent", MessagePriority.Low)]
    [InlineData("Priority: urgent", MessagePriority.High)]
    [InlineData("Subject: none", MessagePriority.Normal)]
    public void Priority_written_by_other_clients_is_read(string header, MessagePriority expected)
    {
        var mime = MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes($"From: a@x.test\r\n{header}\r\n\r\nbody\r\n")));
        Assert.Equal(expected, MimeConverter.ReadPriority(mime));
    }

    [Fact]
    public async Task Draft_keeps_its_priority()
    {
        var mime = MimeConverter.ToMimeMessage(Message(MessagePriority.High), Account);
        var reopened = await MimeConverter.ToOutgoingAsync(mime, new HCG.Mail.Core.Mail.MessageRef("Drafts", 1, 1), new HCG.Mail.Email.Html.HtmlToTextConverter(), default);
        Assert.Equal(MessagePriority.High, reopened.Priority);
    }

    [Fact]
    public void Default_priority_is_normal()
    {
        Assert.Equal(MessagePriority.Normal, new OutgoingMessage().Priority);
        Assert.Equal(["Importance: normal"], HeaderLines(Transmitted(MimeConverter.ToMimeMessage(new OutgoingMessage { To = [new EmailAddress(null, "b@x.test")] }, Account)), "Importance"));
    }

    // ---- MIME formatting ----

    [Fact]
    public void Transmitted_message_is_7bit_with_canonical_line_endings_and_no_bcc()
    {
        var raw = Transmitted(MimeConverter.ToMimeMessage(Message(body: "Linje 1\rLinje 2\r\nLinje 3\nærlig talt"), Account));

        Assert.All(raw, c => Assert.True(c < 128, "8-bit byte in transmitted message"));
        Assert.DoesNotContain("=0D", raw);                                      // a lone CR becomes a real line break
        Assert.Contains("Linje 1\r\nLinje 2\r\nLinje 3\r\n", raw);
        Assert.Contains("Content-Transfer-Encoding: quoted-printable", raw);
        Assert.Empty(HeaderLines(raw, "Bcc"));
        Assert.Equal(raw.Replace("\r\n", "").Length, raw.Replace("\r\n", "").Replace("\r", "").Replace("\n", "").Length); // no bare CR/LF
    }

    [Fact]
    public void Headers_are_rfc5322_complete()
    {
        var mime = MimeConverter.ToMimeMessage(Message(), Account);
        var raw = Transmitted(mime);
        Assert.Equal(["From: Yasin Hakmi <post@hakmicapitalgroup.test>"], HeaderLines(raw, "From"));
        Assert.Single(HeaderLines(raw, "Date"));
        Assert.Single(HeaderLines(raw, "Message-Id"));
        Assert.Equal(["MIME-Version: 1.0"], HeaderLines(raw, "MIME-Version"));
        Assert.Empty(HeaderLines(raw, "Sender"));                               // envelope sender = From; nothing to override
        Assert.Empty(HeaderLines(raw, "Disposition-Notification-To"));          // no read receipts requested
        Assert.Empty(HeaderLines(raw, "Return-Receipt-To"));
        Assert.Equal(DateTimeOffset.Now.Offset, mime.Date.Offset);              // local time zone, not UTC-shifted
    }

    [Fact]
    public void Multipart_with_attachment_is_well_formed()
    {
        var outgoing = Message() with { Attachments = [OutgoingAttachment.FromBytes("årsrapport 2026.pdf", "application/pdf", new byte[5000])] };
        var mime = MimeConverter.ToMimeMessage(outgoing, Account);
        var raw = MessageFormatValidator.SerializeForTransport(mime);
        var parsed = MimeMessage.Load(new MemoryStream(raw));
        var multipart = Assert.IsType<Multipart>(parsed.Body);
        Assert.Equal("multipart/mixed", multipart.ContentType.MimeType);
        Assert.Equal(2, multipart.Count);
        Assert.Equal("årsrapport 2026.pdf", parsed.Attachments.Single().ContentDisposition!.FileName);
        Assert.All(MessageFormatValidator.Validate(parsed, raw), r => Assert.Equal(CheckStatus.Verified, r.Status));
    }

    [Fact]
    public void Message_ids_are_unique_and_on_the_sender_domain()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => MimeConverter.NewMessageId(Account)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.EndsWith("@hakmicapitalgroup.test", id));
        Assert.All(ids, id => Assert.Matches(@"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+$", id));
    }

    [Fact]
    public void Each_new_message_gets_its_own_id_but_a_draft_keeps_its_id()
    {
        var a = MimeConverter.ToMimeMessage(Message(), Account).MessageId;
        var b = MimeConverter.ToMimeMessage(Message(), Account).MessageId;
        Assert.NotEqual(a, b);
        Assert.Equal("fixed@hakmicapitalgroup.test", MimeConverter.ToMimeMessage(Message() with { MessageId = "fixed@hakmicapitalgroup.test" }, Account).MessageId);
    }

    // ---- EHLO ----

    [Theory]
    [InlineData("192.168.1.20", "[192.168.1.20]")]
    [InlineData("::ffff:10.0.0.5", "[10.0.0.5]")]
    [InlineData("2001:db8::7", "[IPv6:2001:db8::7]")]
    [InlineData("fe80::1%5", "[IPv6:fe80::1]")]
    public void Ehlo_name_is_an_rfc5321_address_literal(string local, string expected) =>
        Assert.Equal(expected, SmtpConnector.EhloName(new IPEndPoint(IPAddress.Parse(local), 50000)));

    [Fact]
    public async Task Ehlo_sends_the_address_literal_not_the_computer_name()
    {
        // Plain-text loopback server that records the EHLO line and then ends the session (no credentials are sent).
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? ehlo = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 test ESMTP");
            ehlo = await reader.ReadLineAsync();
            await writer.WriteLineAsync("250 test");
            await reader.ReadLineAsync();
        });

        using var smtp = new SmtpClient { Timeout = 5000 };
        // StartTls on a server without STARTTLS fails after EHLO — HCG Mail never continues without encryption.
        await Assert.ThrowsAnyAsync<Exception>(() => SmtpConnector.ConnectAsync(smtp, new ServerEndpoint("127.0.0.1", port, MailSecurityMode.StartTls), default));
        listener.Stop();

        Assert.Equal("EHLO [127.0.0.1]", ehlo);
        Assert.DoesNotContain(Environment.MachineName, ehlo!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- delivery states ----

    private static DeliveryRecord Pending() => new()
    {
        Timestamp = DateTimeOffset.Now,
        AccountId = "a1",
        AccountAddress = "post@x.test",
        SmtpServer = "smtp:465",
        State = DeliveryState.NotSent,
    };

    [Theory]
    [InlineData(SmtpStatusCode.MailboxUnavailable, DeliveryState.Rejected)]          // 550
    [InlineData(SmtpStatusCode.TransactionFailed, DeliveryState.Rejected)]           // 554
    [InlineData(SmtpStatusCode.MailboxBusy, DeliveryState.Deferred)]                 // 450
    [InlineData(SmtpStatusCode.ErrorInProcessing, DeliveryState.Deferred)]           // 451
    [InlineData(SmtpStatusCode.InsufficientStorage, DeliveryState.Deferred)]         // 452
    public void Smtp_replies_map_to_deferred_or_rejected(SmtpStatusCode code, DeliveryState expected)
    {
        var ex = new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, code, "5.7.1 policy");
        var record = MailKitMailSession.ClassifyFailure(Pending(), ex, transactionStarted: true);
        Assert.Equal(expected, record.State);
        Assert.Equal((int)code, record.SmtpStatusCode);
        Assert.Equal("5.7.1 policy", record.ServerReply);
    }

    [Fact]
    public void Lost_connection_during_the_transaction_is_unknown_not_delivered()
    {
        Assert.Equal(DeliveryState.Submitted, MailKitMailSession.ClassifyFailure(Pending(), new IOException("reset"), transactionStarted: true).State);
        Assert.Equal(DeliveryState.NotSent, MailKitMailSession.ClassifyFailure(Pending(), new IOException("reset"), transactionStarted: false).State);
        Assert.Equal(DeliveryState.NotSent, MailKitMailSession.ClassifyFailure(Pending(), new MailKit.Security.AuthenticationException("bad"), transactionStarted: false).State);
    }

    [Fact]
    public void Accepted_explanation_never_claims_delivery()
    {
        var text = DeliveryRecord.Explain(DeliveryState.Accepted);
        Assert.Contains("not confirmation", text);
        Assert.Contains("unknown", text);
        Assert.Contains("Unknown", DeliveryRecord.InboxPlacementText);
    }

    [Fact]
    public void Delivery_history_is_persisted_capped_and_holds_no_content()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hcg-delivery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var log = new JsonDeliveryLog(paths, NullLogger<JsonDeliveryLog>.Instance);
            for (var i = 0; i < JsonDeliveryLog.MaxRecords + 5; i++)
                log.Add(Pending() with { State = DeliveryState.Accepted, MessageId = $"m{i}@x.test", Timestamp = DateTimeOffset.Now.AddSeconds(i), ServerReply = "2.0.0 Ok" });
            log.Add(Pending() with { AccountId = "other", Timestamp = DateTimeOffset.Now.AddDays(1) });

            var reopened = new JsonDeliveryLog(paths, NullLogger<JsonDeliveryLog>.Instance);
            Assert.Equal(JsonDeliveryLog.MaxRecords, reopened.GetRecent(max: 1000).Count);
            Assert.Equal($"m{JsonDeliveryLog.MaxRecords + 4}@x.test", reopened.GetRecent("a1", 1).Single().MessageId);
            Assert.Single(reopened.GetRecent("other"));

            var json = File.ReadAllText(paths.DeliveryHistoryFile);
            Assert.DoesNotContain("explanation", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("subject", json, StringComparison.OrdinalIgnoreCase);

            reopened.Clear("other");
            Assert.Empty(new JsonDeliveryLog(paths, NullLogger<JsonDeliveryLog>.Instance).GetRecent("other"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
