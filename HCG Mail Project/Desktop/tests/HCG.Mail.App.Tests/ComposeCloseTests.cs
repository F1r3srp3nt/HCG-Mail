using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Compose;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>An unsent message is never discarded without the user choosing to, including when HCG Mail is closed.</summary>
public class ComposeCloseTests
{
    [AvaloniaFact]
    public async Task Closing_the_main_window_keeps_an_unsent_message_and_asks()
    {
        await using var ui = await UiHarness.StartAsync();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ChooseAsync(default!, default!, default!, default, default!).ReturnsForAnyArgs(DialogChoice.Cancel); // "Cancel": keep editing
        var compose = new ComposeViewModel(ui.ViewModel.Accounts, ui.ViewModel.Accounts[0], new OutgoingMessage(),
            ui.Services.GetRequiredService<AppSettingsService>(), dialogs, Substitute.For<INotificationService>())
        { To = "kari@example.com", Body = "Half-written reply" };
        Assert.True(compose.IsDirty);

        var main = new Window { Width = 400, Height = 300 };
        main.Show();
        var composer = new ComposeWindow { DataContext = compose };
        composer.Show(main);
        await UiHarness.Settle(100);

        main.Close();
        await UiHarness.Settle(200);

        Assert.True(composer.IsVisible, "the composer with an unsent message was closed without asking");
        Assert.True(main.IsVisible, "HCG Mail closed while a message was still unsent");
        await dialogs.ReceivedWithAnyArgs(1).ChooseAsync(default!, default!, default!, default, default!);
        Assert.Equal("Half-written reply", compose.Body);
    }
}
