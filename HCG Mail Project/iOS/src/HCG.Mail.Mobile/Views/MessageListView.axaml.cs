using Avalonia.Controls;
using Avalonia.Input;
using HCG.Mail.Mobile.ViewModels;

namespace HCG.Mail.Mobile.Views;

public partial class MessageListView : UserControl
{
    public MessageListView()
    {
        InitializeComponent();
        Messages.Tapped += OnMessageTapped;
        PullToRefresh.RefreshRequested += OnRefreshRequested;
    }

    private MobileShellViewModel? Shell => DataContext as MobileShellViewModel;

    private void OnMessageTapped(object? sender, TappedEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        // Drafts open in the editor, as a double-click does on the desktop.
        if (shell.Mail.IsDraftsFolder && shell.Mail.EditDraftCommand.CanExecute(null))
        {
            shell.Mail.EditDraftCommand.Execute(null);
            return;
        }
        shell.OnMessageChosen();
    }

    /// <summary>Pull to refresh: the same manual refresh as the button; nothing is checked in the background.</summary>
    private async void OnRefreshRequested(object? sender, RefreshRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (Shell is { } shell && shell.Mail.RefreshCommand.CanExecute(null))
                await shell.Mail.RefreshCommand.ExecuteAsync(null);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
