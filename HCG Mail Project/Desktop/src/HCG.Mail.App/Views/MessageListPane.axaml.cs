using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Views;

public partial class MessageListPane : UserControl
{
    public MessageListPane()
    {
        InitializeComponent();
        Messages.DoubleTapped += OnMessageDoubleTapped;
        // The context menu acts on the message that was right-clicked, so select it first.
        Messages.ContextRequested += (_, e) =>
        {
            if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } item })
                Messages.SelectedItem = item;
            else if (Messages.SelectedItem is null)
                e.Handled = true; // nothing to act on: no menu
        };
    }

    public void FocusSearch() => SearchBox.Focus();

    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsDraftsFolder: true } vm && vm.EditDraftCommand.CanExecute(null))
            vm.EditDraftCommand.Execute(null);
    }
}
