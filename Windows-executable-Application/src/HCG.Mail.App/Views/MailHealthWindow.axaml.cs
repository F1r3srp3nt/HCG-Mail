using Avalonia.Controls;
using Avalonia.Interactivity;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Views;

public partial class MailHealthWindow : Window
{
    public MailHealthWindow() => InitializeComponent();

    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MailHealthViewModel vm || Clipboard is null)
            return;
        await Clipboard.SetTextAsync(vm.BuildReportText());
        vm.StatusText = "Report copied to the clipboard.";
    }
}
