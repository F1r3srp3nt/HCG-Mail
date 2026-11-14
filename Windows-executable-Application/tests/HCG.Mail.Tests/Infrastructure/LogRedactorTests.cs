using HCG.Mail.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Tests.Infrastructure;

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("550 5.1.1 <kari.nordmann@firma.no>: Recipient address rejected", "550 5.1.1 <***@firma.no>: Recipient address rejected")]
    [InlineData("Mailbox ola@hcgmail.test is full", "Mailbox ***@hcgmail.test is full")]
    [InlineData("C: AUTHENTICATE PLAIN AG9sYUBoY2dtYWlsLnRlc3QAc2VjcmV0", "C: AUTHENTICATE PLAIN [redacted]")]
    [InlineData("AUTH LOGIN dXNlcm5hbWU=", "AUTH LOGIN [redacted]")]
    [InlineData("Authorization: Bearer abc.DEF-123_xyz", "Authorization: Bearer [redacted]")]
    [InlineData("token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJlX3Zh rest", "token [redacted] rest")]
    [InlineData("password=hunter2&user=x", "password=[redacted]&user=x")]
    [InlineData("{\"refresh_token\": \"0.ARwA-secret\", \"x\": 1}", "{\"refresh_token\": \"[redacted]\", \"x\": 1}")]
    [InlineData("client_secret: s3cr3t", "client_secret: [redacted]")]
    public void Sensitive_values_are_removed(string input, string expected) => Assert.Equal(expected, LogRedactor.Redact(input));

    [Theory]
    [InlineData("IMAP connected for account a1b2 to imap.domene.no:993")]
    [InlineData("SMTP send failed for account a1b2: Authentication (AuthenticationException)")]
    [InlineData("at HCG.Mail.Email.Imap.MailKitMailSession.SendAsync(OutgoingMessage message) in /src/MailKitMailSession.cs:line 210")]
    [InlineData("Connection test for account x: Passed")]
    public void Ordinary_diagnostics_are_unchanged(string line) => Assert.Equal(line, LogRedactor.Redact(line));

    [Fact]
    public void The_log_file_never_contains_addresses_or_passwords_from_exceptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hcg-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var provider = new FileLoggerProvider(dir);
            var logger = provider.CreateLogger("HCG.Mail.Test");
            logger.LogError(new InvalidOperationException("5.7.8 <ola@example.com> password=hunter2 rejected"), "Send failed for account {AccountId}", "a1");
            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(dir)));
            Assert.Contains("Send failed for account a1", text);
            Assert.Contains("***@example.com", text);
            Assert.DoesNotContain("ola@example.com", text);
            Assert.DoesNotContain("hunter2", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
