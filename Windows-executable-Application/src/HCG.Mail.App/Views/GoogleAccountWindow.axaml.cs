using Avalonia.Controls;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Views;

public partial class GoogleAccountWindow : Window
{
    public GoogleAccountWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GoogleAccountViewModel vm)
                vm.CloseRequested += (_, connected) => Close(connected);
        };
        Closing += (_, e) =>
        {
            // Closing the window while the browser sign-in is pending cancels it.
            if (DataContext is GoogleAccountViewModel { IsBusy: true } vm)
            {
                vm.CancelCommand.Execute(null);
            }
        };
    }
}
