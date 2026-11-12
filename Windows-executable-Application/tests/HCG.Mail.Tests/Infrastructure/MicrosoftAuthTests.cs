using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Microsoft;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

namespace HCG.Mail.Tests.Infrastructure;

public sealed class MicrosoftAuthTests
{
    [Theory]
    [InlineData("authentication_canceled", "User canceled authentication.", MailErrorKind.Cancelled)]
    [InlineData("access_denied", "The user declined consent.", MailErrorKind.Cancelled)]
    [InlineData("invalid_grant", "AADSTS90094: The grant requires admin permission.", MailErrorKind.Authentication)]
    [InlineData("invalid_client", "AADSTS700016: Application with identifier was not found in the directory.", MailErrorKind.NotSupported)]
    [InlineData("invalid_request", "AADSTS50011: The redirect URI does not match.", MailErrorKind.NotSupported)]
    [InlineData("access_denied", "AADSTS53003: Access has been blocked by Conditional Access policies.", MailErrorKind.Authentication)]
    [InlineData("access_denied", "AADSTS90094: Admin approval required.", MailErrorKind.Authentication)]
    [InlineData("network_not_available", "No network.", MailErrorKind.Network)]
    public void Msal_errors_become_actionable_messages(string code, string message, MailErrorKind kind)
    {
        var translated = MsalMicrosoftAuth.Translate(new MsalServiceException(code, message), interactive: true);
        Assert.Equal(kind, translated.Kind);
        Assert.DoesNotContain("token", translated.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Conditional_access_and_admin_consent_are_named()
    {
        Assert.Contains("administrator", MsalMicrosoftAuth.Translate(new MsalServiceException("invalid_grant", "AADSTS65001: consent required"), true).UserMessage);
        Assert.Contains("Conditional Access", MsalMicrosoftAuth.Translate(new MsalServiceException("interaction_required", "AADSTS53003: blocked"), true).UserMessage);
    }

    [Fact]
    public void Revoked_consent_or_expired_refresh_token_requires_reconnect()
    {
        var translated = MsalMicrosoftAuth.Translate(new MsalUiRequiredException("invalid_grant", "AADSTS700082: The refresh token has expired."), interactive: false);
        Assert.Equal(MailErrorKind.ReconnectRequired, translated.Kind);
        Assert.Contains("Reconnect", translated.UserMessage);
    }

    [Fact]
    public void Requested_permissions_are_the_minimum_for_the_features()
    {
        Assert.Equal(["User.Read", "Mail.ReadWrite", "Mail.Send"], MsalMicrosoftAuth.MailScopes);
        Assert.DoesNotContain(MsalMicrosoftAuth.MailScopes, s => s.Contains("All", StringComparison.Ordinal) || s.Contains("Shared", StringComparison.Ordinal));
    }

    [Fact]
    public void Client_id_comes_from_the_environment_or_build_and_must_be_a_guid()
    {
        var previous = Environment.GetEnvironmentVariable(MicrosoftAuthOptions.ClientIdVariable);
        try
        {
            Environment.SetEnvironmentVariable(MicrosoftAuthOptions.ClientIdVariable, null);
            Assert.Null(MicrosoftAuthOptions.ResolveClientId(typeof(MicrosoftAuthTests).Assembly)); // no client ID is invented
            Environment.SetEnvironmentVariable(MicrosoftAuthOptions.ClientIdVariable, " 11111111-2222-3333-4444-555555555555 ");
            Assert.Equal("11111111-2222-3333-4444-555555555555", MicrosoftAuthOptions.ResolveClientId(typeof(MicrosoftAuthTests).Assembly));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MicrosoftAuthOptions.ClientIdVariable, previous);
        }
        Assert.False(MicrosoftAuthOptions.IsValidClientId("not-a-guid"));
        Assert.False(MicrosoftAuthOptions.IsValidClientId(Guid.Empty.ToString()));
        Assert.True(MicrosoftAuthOptions.IsValidClientId(Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => new MsalMicrosoftAuth("not-a-guid", new AppPaths(Path.GetTempPath()), NullLogger<MsalMicrosoftAuth>.Instance));
    }

    [Fact]
    public async Task Unconfigured_build_explains_instead_of_signing_in()
    {
        var auth = new UnconfiguredMicrosoftAuth(MicrosoftAuthOptions.NotConfiguredMessage);
        Assert.False(auth.IsConfigured);
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => auth.SignInAsync(null, default));
        Assert.Contains("MICROSOFT_ACCOUNTS.md", ex.UserMessage);
    }

    [Fact]
    public void Personal_accounts_are_recognised_by_tenant()
    {
        Assert.True(new MicrosoftSignIn("k", "a@outlook.com", null, MicrosoftSignIn.ConsumerTenantId).IsPersonalAccount);
        Assert.False(new MicrosoftSignIn("k", "a@contoso.com", null, Guid.NewGuid().ToString()).IsPersonalAccount);
    }

    [Fact]
    public void Microsoft_accounts_persist_without_secrets_and_older_files_still_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hcg-accounts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var store = new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance);
            var microsoft = new MailAccount
            {
                Id = "m1", DisplayName = "Kari", EmailAddress = "kari@contoso.test", Username = "kari@contoso.test",
                Imap = MailAccount.GraphEndpoint, Smtp = MailAccount.GraphEndpoint,
                Provider = MailProviderKind.MicrosoftGraph, MicrosoftAccountId = "oid.tid",
            };
            var imap = new MailAccount
            {
                Id = "i1", DisplayName = "Ola", EmailAddress = "ola@firma.no", Username = "ola@firma.no",
                Imap = new ServerEndpoint("mail.firma.no", 993, MailSecurityMode.SslTls), Smtp = new ServerEndpoint("mail.firma.no", 465, MailSecurityMode.SslTls),
            };
            store.Save([microsoft, imap]);

            var json = File.ReadAllText(paths.AccountsFile);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal([microsoft, imap], new JsonAccountStore(paths, NullLogger<JsonAccountStore>.Instance).Load());

            // A file written by the previous version (no provider field) loads as IMAP.
            File.WriteAllText(paths.AccountsFile, """
                {"version":1,"accounts":[{"id":"x","displayName":"X","emailAddress":"x@y.no","username":"x@y.no",
                 "imap":{"host":"imap.y.no","port":993,"security":"SslTls"},"smtp":{"host":"smtp.y.no","port":465,"security":"SslTls"}}]}
                """);
            Assert.Equal(MailProviderKind.ImapSmtp, Assert.Single(store.Load()).Provider);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
