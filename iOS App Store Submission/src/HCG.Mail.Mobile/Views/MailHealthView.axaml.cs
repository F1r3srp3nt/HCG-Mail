using Avalonia.Controls;
using Avalonia.Interactivity;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.Mobile.Views;

public partial class MailHealthView : UserControl
{
    public MailHealthView() => InitializeComponent();

    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MailHealthViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(vm.BuildReportText());
        vm.StatusText = "Report copied. Paste it into a message to your mail provider's support.";
    }
}
