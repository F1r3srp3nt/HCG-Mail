using Avalonia.Controls;
using Avalonia.Input;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Views;

public partial class MessageListPane : UserControl
{
    public MessageListPane()
    {
        InitializeComponent();
        Messages.DoubleTapped += OnMessageDoubleTapped;
    }

    public void FocusSearch() => SearchBox.Focus();

    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsDraftsFolder: true } vm && vm.EditDraftCommand.CanExecute(null))
            vm.EditDraftCommand.Execute(null);
    }
}
