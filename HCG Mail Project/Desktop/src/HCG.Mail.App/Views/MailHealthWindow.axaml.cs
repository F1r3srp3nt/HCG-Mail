using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Views;

public partial class MailHealthWindow : Window
{
    public MailHealthWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // Opened from the offline banner: start on the Network Doctor. Otherwise the Connection tab stays first.
            if (DataContext is MailHealthViewModel { StartOnNetworkDoctor: true, HasNetwork: true })
                Tabs.SelectedItem = NetworkTab;
            else if (DataContext is MailHealthViewModel { HasConnection: true })
                Tabs.SelectedItem = ConnectionTab;
        };
    }

    private async void OnCopyNetworkReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MailHealthViewModel { Network: { } network } || Clipboard is null)
            return;
        await Clipboard.SetTextAsync(network.BuildReportText());
        network.StatusText = "Report copied to the clipboard (no passwords, tokens or message content).";
    }

    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MailHealthViewModel vm || Clipboard is null)
            return;
        await Clipboard.SetTextAsync(vm.BuildReportText());
        vm.StatusText = "Report copied to the clipboard.";
    }

    private async void OnCopyDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MailHealthViewModel { Connection: { } connection } || Clipboard is null)
            return;
        await Clipboard.SetTextAsync(connection.BuildDiagnosticsText());
        connection.CheckStatus = "Diagnostics copied to the clipboard (no passwords, tokens or message content).";
    }
}
