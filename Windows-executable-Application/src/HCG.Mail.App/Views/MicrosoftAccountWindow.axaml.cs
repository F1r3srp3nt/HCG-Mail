using Avalonia.Controls;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Views;

public partial class MicrosoftAccountWindow : Window
{
    public MicrosoftAccountWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MicrosoftAccountViewModel vm)
                vm.CloseRequested += (_, connected) => Close(connected);
        };
        Closing += (_, e) =>
        {
            // Closing the window while the browser sign-in is pending cancels it.
            if (DataContext is MicrosoftAccountViewModel { IsBusy: true } vm)
            {
                vm.CancelCommand.Execute(null);
            }
        };
    }
}
