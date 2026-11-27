using System.Collections.ObjectModel;
using Avalonia.Headless.XUnit;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Infrastructure.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HCG.Mail.App.Tests;

/// <summary>Send failures: a message must never be sent twice by accident, and must never silently disappear.</summary>
public class ComposeSendingTests
{
    private static async Task<(UiHarness Ui, ComposeViewModel Compose, IMailSession Session, IDialogService Dialogs)> CreateAsync()
    {
        var ui = await UiHarness.StartAsync();
        var session = Substitute.For<IMailSession>();
        session.GetFoldersAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<MailFolder>());
        var factory = Substitute.For<IMailSessionFactory>();
        factory.Create(Arg.Any<MailAccount>(), Arg.Any<string>()).Returns(session);
        var vault = new SessionCredentialVault();
        var account = new MailAccount
        {
            Id = "a1", DisplayName = "Ola", EmailAddress = "ola@example.com", Username = "ola@example.com",
            Imap = new ServerEndpoint("imap.example.com", 993, MailSecurityMode.SslTls), Smtp = new ServerEndpoint("smtp.example.com", 465, MailSecurityMode.SslTls),
        };
        await vault.SetAsync(account.CredentialKey, "pw");
        var vm = new AccountViewModel(account, factory, vault, NullLogger.Instance);
        Assert.True(await vm.ConnectAsync());

        var dialogs = Substitute.For<IDialogService>();
        var compose = new ComposeViewModel(new ObservableCollection<AccountViewModel> { vm }, vm, new OutgoingMessage(),
            ui.Services.GetRequiredService<AppSettingsService>(), dialogs, Substitute.For<INotificationService>())
        {
            To = "kari@example.com",
            Subject = "Contract",
            Body = "Signed copy attached.",
        };
        return (ui, compose, session, dialogs);
    }

    [AvaloniaFact]
    public async Task A_lost_connection_after_the_server_started_receiving_is_never_called_not_sent_and_resending_asks_first()
    {
        var (ui, compose, session, dialogs) = await CreateAsync();
        await using var _ = ui;
        session.SendAsync(Arg.Any<OutgoingMessage>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new MailOperationException(MailErrorKind.Network, "The connection to 'smtp.example.com' was lost.") { OutcomeUnknown = true });
        var closed = false;
        compose.CloseRequested += (_, _) => closed = true;

        await compose.SendCommand.ExecuteAsync(null);

        Assert.False(closed); // the message stays open, nothing is lost
        Assert.True(compose.PossiblyAlreadySent);
        Assert.DoesNotContain("Not sent", compose.StatusMessage);
        Assert.Contains("may already have been sent", compose.StatusMessage);

        // Sending again requires an explicit choice; declining sends nothing.
        dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(false);
        await compose.SendCommand.ExecuteAsync(null);
        await dialogs.Received(1).ConfirmAsync("This message may already have been sent", Arg.Any<string>(), "Send again", true);
        await session.Received(1).SendAsync(Arg.Any<OutgoingMessage>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaFact]
    public async Task A_definite_failure_says_not_sent_and_keeps_the_message()
    {
        var (ui, compose, session, _) = await CreateAsync();
        await using var _ = ui;
        session.SendAsync(Arg.Any<OutgoingMessage>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new MailOperationException(MailErrorKind.Authentication, "The outgoing server rejected the password."));
        var closed = false;
        compose.CloseRequested += (_, _) => closed = true;

        await compose.SendCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.False(compose.PossiblyAlreadySent);
        Assert.StartsWith("Not sent:", compose.StatusMessage);
        Assert.Equal("Signed copy attached.", compose.Body);
    }

    [AvaloniaFact]
    public async Task An_attachment_deleted_before_sending_is_reported_and_nothing_is_sent()
    {
        var (ui, compose, session, _) = await CreateAsync();
        await using var _ = ui;
        session.SendAsync(Arg.Any<OutgoingMessage>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("Could not find file 'contract.pdf'."));

        await compose.SendCommand.ExecuteAsync(null);

        Assert.StartsWith("Not sent: an attachment could not be read", compose.StatusMessage);
        Assert.False(compose.PossiblyAlreadySent);
        Assert.False(compose.IsBusy);
    }

    [AvaloniaFact]
    public async Task Unsaved_changes_are_saved_as_a_draft_automatically_once()
    {
        var (ui, compose, session, _) = await CreateAsync();
        await using var _ = ui;
        session.SaveDraftAsync(Arg.Any<OutgoingMessage>(), Arg.Any<CancellationToken>()).Returns(new DraftSaveResult(new MessageRef("Drafts", 7, 1)));

        Assert.True(await compose.AutosaveAsync());
        Assert.Contains("saved automatically", compose.StatusMessage);
        Assert.False(compose.IsDirty);
        Assert.False(await compose.AutosaveAsync()); // nothing changed since: no second save
        await session.Received(1).SaveDraftAsync(Arg.Any<OutgoingMessage>(), Arg.Any<CancellationToken>());

        compose.To = "kari@example.com, not an address";
        Assert.False(await compose.AutosaveAsync()); // would drop the invalid address: wait for the user
        await session.Received(1).SaveDraftAsync(Arg.Any<OutgoingMessage>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaFact]
    public async Task A_new_message_starts_with_the_accounts_signature_and_is_not_treated_as_edited()
    {
        var (ui, _, _, dialogs) = await CreateAsync();
        await using var _ = ui;
        var settings = ui.Services.GetRequiredService<AppSettingsService>();
        var account = ui.ViewModel.Accounts[0];
        settings.Update(s => s.WithSignature(account.Account.Id, "Ola Nordmann"));

        var compose = new ComposeViewModel(ui.ViewModel.Accounts, account, new OutgoingMessage(), settings, dialogs, Substitute.For<INotificationService>());

        Assert.EndsWith("-- \nOla Nordmann", compose.Body);
        Assert.False(compose.IsDirty); // closing an untouched new message does not ask to save a draft
    }
}
