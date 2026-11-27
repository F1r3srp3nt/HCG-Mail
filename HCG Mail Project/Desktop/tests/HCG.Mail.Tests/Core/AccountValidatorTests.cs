using HCG.Mail.Core.Accounts;

namespace HCG.Mail.Tests.Core;

public class AccountValidatorTests
{
    private static AccountDraft Valid() => new()
    {
        DisplayName = "Ola Nordmann",
        EmailAddress = "ola@firma.no",
        Username = "ola@firma.no",
        Password = "secret",
        ImapHost = "mail.firma.no",
        ImapPort = "993",
        ImapSecurity = MailSecurityMode.SslTls,
        SmtpHost = "mail.firma.no",
        SmtpPort = "465",
        SmtpSecurity = MailSecurityMode.SslTls,
    };

    [Fact]
    public void Valid_draft_has_no_errors()
    {
        Assert.Empty(AccountValidator.Validate(Valid()));
    }

    [Fact]
    public void Missing_fields_are_reported_per_field()
    {
        var errors = AccountValidator.Validate(new AccountDraft());
        var fields = errors.Select(e => e.Field).ToHashSet();
        Assert.Contains(AccountField.EmailAddress, fields);
        Assert.Contains(AccountField.Username, fields);
        Assert.Contains(AccountField.Password, fields);
        Assert.Contains(AccountField.ImapHost, fields);
        Assert.Contains(AccountField.ImapPort, fields);
        Assert.Contains(AccountField.SmtpHost, fields);
        Assert.Contains(AccountField.SmtpPort, fields);
    }

    [Fact]
    public void Password_is_optional_when_editing()
    {
        var errors = AccountValidator.Validate(Valid() with { Password = "" }, requirePassword: false);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("imaps://mail.firma.no")]
    [InlineData("mail firma.no")]
    [InlineData("-mail.firma.no")]
    [InlineData("mail..firma.no")]
    [InlineData("mail_firma.no")]
    public void Invalid_hostnames_are_rejected(string host)
    {
        var errors = AccountValidator.Validate(Valid() with { ImapHost = host });
        Assert.Contains(errors, e => e.Field == AccountField.ImapHost);
    }

    [Theory]
    [InlineData("mail.firma.no")]
    [InlineData("MAIL.Firma.NO.")]
    [InlineData("192.0.2.10")]
    [InlineData("localhost")]
    public void Valid_hostnames_are_accepted(string host)
    {
        Assert.True(AccountValidator.IsValidHostname(host));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("99 3")]
    public void Invalid_ports_are_rejected(string port)
    {
        var errors = AccountValidator.Validate(Valid() with { SmtpPort = port });
        Assert.Contains(errors, e => e.Field == AccountField.SmtpPort);
    }

    [Fact]
    public void Header_injection_characters_are_rejected()
    {
        var errors = AccountValidator.Validate(Valid() with { DisplayName = "Ola\r\nBcc: x@evil.example", Username = "ola\n" });
        Assert.Contains(errors, e => e.Field == AccountField.DisplayName);
        Assert.Contains(errors, e => e.Field == AccountField.Username);
    }

    [Fact]
    public void ToAccount_normalizes_hosts_and_keeps_id()
    {
        var account = AccountValidator.ToAccount(Valid() with { ImapHost = " Mail.Firma.NO. " }, existingId: "abc", presetId: "domene.no");
        Assert.Equal("mail.firma.no", account.Imap.Host);
        Assert.Equal(993, account.Imap.Port);
        Assert.Equal("abc", account.Id);
        Assert.Equal("domene.no", account.PresetId);
        Assert.Equal("account:abc", account.CredentialKey);
    }

    [Fact]
    public void Draft_round_trips_through_account()
    {
        var account = AccountValidator.ToAccount(Valid());
        var draft = AccountValidator.FromAccount(account);
        Assert.Equal("mail.firma.no", draft.SmtpHost);
        Assert.Equal("465", draft.SmtpPort);
        Assert.Equal("", draft.Password);
    }

    [Fact]
    public void Domene_preset_uses_implicit_tls_ports_and_no_hostname()
    {
        var preset = ProviderPresets.DomeneNo;
        Assert.Equal(993, preset.ImapPort);
        Assert.Equal(MailSecurityMode.SslTls, preset.ImapSecurity);
        Assert.Equal(465, preset.SmtpPort);
        Assert.Equal(MailSecurityMode.SslTls, preset.SmtpSecurity);
        // The hostname is provider-/domain-specific and must be entered by the user, never assumed.
        Assert.DoesNotMatch(@"\b(imap|smtp|mail)\.[a-z]", preset.HostnameHint);
    }

    [Theory]
    [InlineData(MailSecurityMode.SslTls, 993, 465)]
    [InlineData(MailSecurityMode.StartTls, 143, 587)]
    public void Default_ports_match_security_mode(MailSecurityMode mode, int imap, int smtp)
    {
        Assert.Equal(imap, PortDefaults.Imap(mode));
        Assert.Equal(smtp, PortDefaults.Smtp(mode));
    }
}
