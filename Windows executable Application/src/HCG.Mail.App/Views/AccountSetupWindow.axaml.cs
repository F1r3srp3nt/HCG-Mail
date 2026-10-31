using Avalonia.Controls;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Views;

public partial class AccountSetupWindow : Window
{
    public AccountSetupWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AccountSetupViewModel vm)
                vm.CloseRequested += (_, saved) => Close(saved);
        };
    }
}
