using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace HCG.Mail.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        TitleBar.DoubleTapped += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        KeyDown += OnKeyDown;

        // Leave room for the macOS traffic-light buttons in the extended title bar.
        if (OperatingSystem.IsMacOS())
            TitleContent.Margin = new Thickness(84, 0, 0, 0);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            MessageListPane.FocusSearch();
            e.Handled = true;
        }
    }
}
