using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Errors;
using HCG.Mail.Email;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using HCG.Mail.Email.Smtp;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HCG.Mail.Tests.Email;

/// <summary>
/// A send that loses its connection after the server may have started receiving the message has an unknown outcome:
/// it is recorded as Submitted and reported with <see cref="MailOperationException.OutcomeUnknown"/>, so the composer
/// never offers a silent resend that could deliver the message twice.
/// </summary>
public sealed class AmbiguousSendTests
{
    private static readonly MailAccount Account = new()
    {
        Id = "a1", DisplayName = "Ola", EmailAddress = "ola@example.com", Username = "ola@example.com",
        Imap = new ServerEndpoint("imap.example.com", 993, MailSecurityMode.SslTls), Smtp = new ServerEndpoint("smtp.example.com", 465, MailSecurityMode.SslTls),
    };

    private sealed class Log : IDeliveryLog
    {
        public List<DeliveryRecord> Records { get; } = [];
        public void Add(DeliveryRecord record) => Records.Add(record);
        public IReadOnlyList<DeliveryRecord> GetRecent(string accountId, int max) => Records;
        public void Clear(string accountId) => Records.Clear();
    }

    private static (MailKitMailSession Session, ISmtpClient Smtp, Log Log) Create()
    {
        var smtp = Substitute.For<ISmtpClient>();
        smtp.IsSecure.Returns(true);
        smtp.Capabilities.Returns(SmtpCapabilities.Authentication);
        var factory = Substitute.For<IMailClientFactory>();
        factory.CreateSmtpClient(Arg.Any<TlsValidationRecorder>()).Returns(smtp);
        var log = new Log();
        return (new MailKitMailSession(Account, "pw", factory, new HtmlToTextConverter(), NullLogger<MailKitMailSession>.Instance, log), smtp, log);
    }

    private static OutgoingMessage Message() => new() { To = [new EmailAddress(null, "kari@example.com")], Subject = "Contract", TextBody = "Hi" };

    [Fact]
    public async Task Connection_lost_during_the_transaction_is_unknown_not_failed()
    {
        var (session, smtp, log) = Create();
        smtp.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>()).ThrowsAsync(new IOException("Connection reset by peer"));

        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.SendAsync(Message(), saveCopyToSent: false));

        Assert.True(ex.OutcomeUnknown);
        Assert.Equal(DeliveryState.Submitted, Assert.Single(log.Records).State);
    }

    [Fact]
    public async Task Failures_before_the_transaction_are_definitely_not_sent()
    {
        var (session, smtp, log) = Create();
        smtp.AuthenticateAsync(Arg.Any<System.Net.ICredentials>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthenticationException("535 bad credentials"));

        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.SendAsync(Message(), saveCopyToSent: false));

        Assert.False(ex.OutcomeUnknown);
        Assert.Equal(DeliveryState.NotSent, Assert.Single(log.Records).State);
    }

    [Fact]
    public void The_production_client_tracks_whether_a_recipient_was_accepted()
    {
        using var client = new MailClientFactory().CreateSmtpClient(new TlsValidationRecorder());
        var tracking = Assert.IsType<TransactionTrackingSmtpClient>(client);
        Assert.False(tracking.RecipientAccepted); // nothing accepted yet: a lost connection now means "not sent"
    }
}
