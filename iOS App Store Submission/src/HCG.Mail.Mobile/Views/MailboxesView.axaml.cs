using Avalonia.Controls;
using Avalonia.Input;
using HCG.Mail.Mobile.ViewModels;

namespace HCG.Mail.Mobile.Views;

public partial class MailboxesView : UserControl
{
    public MailboxesView()
    {
        InitializeComponent();
        StandardFolders.Tapped += OnFolderTapped;
        OtherFolders.Tapped += OnFolderTapped;
    }

    private void OnFolderTapped(object? sender, TappedEventArgs e) => (DataContext as MobileShellViewModel)?.OnFolderChosen();
}
