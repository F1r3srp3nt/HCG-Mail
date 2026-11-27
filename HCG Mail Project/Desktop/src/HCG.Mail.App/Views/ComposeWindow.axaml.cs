using Avalonia.Controls;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Compose;

namespace HCG.Mail.App.Views;

public partial class ComposeWindow : Window
{
    private bool _closeConfirmed;
    private readonly Avalonia.Threading.DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMinutes(1) };

    public ComposeWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ComposeViewModel vm)
                vm.CloseRequested += (_, _) =>
                {
                    _closeConfirmed = true;
                    Close();
                };
        };
        Opened += (_, _) =>
        {
            if (DataContext is ComposeViewModel { Kind: ComposeKind.Reply or ComposeKind.ReplyAll })
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0;
            }
            else
            {
                ToBox.Focus();
            }
        };
        Closing += OnClosing;
        _autosave.Tick += async (_, _) =>
        {
            if (DataContext is ComposeViewModel vm)
                await vm.AutosaveAsync();
        };
        Opened += (_, _) => _autosave.Start();
        Closed += (_, _) => _autosave.Stop();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || DataContext is not ComposeViewModel vm || !vm.IsDirty)
            return;
        e.Cancel = true;
        if (await vm.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
