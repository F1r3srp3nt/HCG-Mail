using Avalonia.Headless.XUnit;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Services;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>Guided IMAP/SMTP setup: published provider settings, app-password guidance, honest notices.</summary>
public class OnboardingTests
{
    private static AccountSetupViewModel Setup(Func<MailAccount, Task>? onSaved = null) =>
        new(Substitute.For<IConnectionTester>(), new SessionCredentialVault(), onSaved ?? (_ => Task.CompletedTask));

    [Fact]
    public void A_gmail_address_suggests_googles_servers_and_explains_app_passwords()
    {
        var setup = Setup();
        setup.EmailAddress = "kari.nordmann@gmail.com";

        Assert.Equal(ProviderPresets.Gmail, setup.SelectedPreset);
        Assert.Equal("imap.gmail.com", setup.ImapHost);
        Assert.Equal("993", setup.ImapPort);
        Assert.Equal("smtp.gmail.com", setup.SmtpHost);
        Assert.Equal("465", setup.SmtpPort);
        Assert.True(setup.HasPasswordGuidance);
        Assert.Contains("app password", setup.PasswordGuidance);
        Assert.False(setup.HasProviderNotice);
    }

    [Fact]
    public void Servers_the_user_typed_are_never_replaced_by_a_suggestion()
    {
        var setup = Setup();
        setup.ImapHost = "mail.mycompany.no";
        setup.EmailAddress = "kari@gmail.com";
        Assert.Equal("mail.mycompany.no", setup.ImapHost);
        Assert.NotEqual(ProviderPresets.Gmail, setup.SelectedPreset);
    }

    [Fact]
    public void Switching_away_from_a_provider_clears_only_the_servers_it_filled_in()
    {
        var setup = Setup();
        setup.SelectedPreset = ProviderPresets.Fastmail;
        Assert.Equal("imap.fastmail.com", setup.ImapHost);
        setup.SelectedPreset = ProviderPresets.Custom;
        Assert.Equal("", setup.ImapHost);
        Assert.Equal("", setup.SmtpHost);
        Assert.False(setup.HasPasswordGuidance);
    }

    [Theory]
    [InlineData("ola@outlook.com", "Microsoft sign-in")]
    [InlineData("ola@hotmail.no", "Microsoft sign-in")]
    [InlineData("ola@proton.me", "Proton Mail Bridge")]
    public void Addresses_that_cannot_use_a_password_here_say_why(string address, string expected)
    {
        var setup = Setup();
        setup.EmailAddress = address;
        Assert.True(setup.HasProviderNotice);
        Assert.Contains(expected, setup.ProviderNotice);
    }

    [Fact]
    public async Task The_account_description_is_saved_and_shown_and_old_files_still_load()
    {
        MailAccount? saved = null;
        var setup = Setup(a => { saved = a; return Task.CompletedTask; });
        setup.EmailAddress = "ola@example.com";
        setup.Description = "Work";
        setup.Password = "pw";
        setup.ImapHost = "imap.example.com";
        await setup.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Work", saved!.Description);

        var dir = Directory.CreateTempSubdirectory("hcg-description-").FullName;
        try
        {
            var store = new JsonAccountStore(new AppPaths(dir), NullLogger<JsonAccountStore>.Instance);
            store.Save([saved]);
            var reloaded = Assert.Single(store.Load());
            Assert.Equal("Work", reloaded.Description);
            Assert.Equal("Work · ola@example.com", new AccountViewModel(reloaded, Substitute.For<IMailSessionFactory>(), new SessionCredentialVault(), NullLogger.Instance).AddressLine);

            store.Save([saved with { Description = null }]); // accounts saved by older versions have no description
            Assert.Null(Assert.Single(store.Load()).Description);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Setup_window_shows_the_suggestion_guidance_and_notice()
    {
        await using var ui = await UiHarness.StartAsync();
        var setup = Setup();
        setup.EmailAddress = "kari.nordmann@gmail.com";
        setup.DisplayName = "Kari Nordmann";
        setup.Description = "Personal";
        var window = new AccountSetupWindow { DataContext = setup, Width = 1100, Height = 900 };
        window.Show();
        await UiHarness.Settle(200);
        UiHarness.Capture(window, "onboarding-gmail");
        setup.EmailAddress = "kari@outlook.com";
        await UiHarness.Settle(100);
        UiHarness.Capture(window, "onboarding-outlook-notice");
        window.Close();
    }
}
