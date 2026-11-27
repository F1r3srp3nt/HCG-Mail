using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Network;

namespace HCG.Mail.Tests.Network;

public sealed class SmtpAlternativesTests
{
    [Fact]
    public void Gmail_on_465_is_offered_the_documented_587_with_STARTTLS()
    {
        var alt = Assert.Single(SmtpAlternatives.For(new ServerEndpoint("smtp.gmail.com", 465, MailSecurityMode.SslTls)));
        Assert.Equal(new ServerEndpoint("smtp.gmail.com", 587, MailSecurityMode.StartTls), alt.Endpoint);
        Assert.True(alt.DocumentedByProvider);
        Assert.Contains("Google", alt.Source);
    }

    [Fact]
    public void ICloud_documents_only_587_so_nothing_else_is_offered() =>
        Assert.Empty(SmtpAlternatives.For(new ServerEndpoint("smtp.mail.me.com", 587, MailSecurityMode.StartTls)));

    [Fact]
    public void Other_servers_get_the_other_standard_submission_port_marked_as_needing_confirmation()
    {
        var alt = Assert.Single(SmtpAlternatives.For(new ServerEndpoint("send.example.no", 465, MailSecurityMode.SslTls)));
        Assert.Equal(new ServerEndpoint("send.example.no", 587, MailSecurityMode.StartTls), alt.Endpoint);
        Assert.False(alt.DocumentedByProvider);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(2525)]
    public void Port_25_and_unknown_ports_get_no_alternative(int port) =>
        Assert.Empty(SmtpAlternatives.For(new ServerEndpoint("send.example.no", port, MailSecurityMode.StartTls)));

    [Fact]
    public void Every_alternative_is_encrypted()
    {
        foreach (var host in new[] { "smtp.gmail.com", "smtp.mail.yahoo.com", "smtp.fastmail.com", "x.example" })
            foreach (var port in new[] { 465, 587 })
                foreach (var alt in SmtpAlternatives.For(new ServerEndpoint(host, port, port == 465 ? MailSecurityMode.SslTls : MailSecurityMode.StartTls)))
                    Assert.True(alt.Endpoint.Security is MailSecurityMode.SslTls or MailSecurityMode.StartTls);
    }
}
