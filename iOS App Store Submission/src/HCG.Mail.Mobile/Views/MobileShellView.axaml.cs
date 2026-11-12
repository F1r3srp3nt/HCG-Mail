using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HCG.Mail.Mobile.ViewModels;

namespace HCG.Mail.Mobile.Views;

/// <summary>
/// Arranges the three mail panes for the current layout and connects the shell to the platform: safe area,
/// on-screen keyboard, text size and the hardware-keyboard Escape key (iPad).
/// </summary>
public partial class MobileShellView : UserControl
{
    public const double SidebarWidth = 300;
    public const double ListWidth = 360;
    public const double SheetCardMaxWidth = 720;

    private MobileShellViewModel? _shell;
    private IInsetsManager? _insets;
    private IInputPane? _inputPane;

    public MobileShellView()
    {
        InitializeComponent();
        Root.SizeChanged += (_, e) => _shell?.UpdateSize(e.NewSize.Width, e.NewSize.Height);
        SidebarScrim.PointerPressed += (_, _) => _shell?.CloseSidebarCommand.Execute(null);
        KeyDown += OnKeyDown;
        ActualThemeVariantChanged += (_, _) => UpdateSystemBar();
        DataContextChanged += (_, _) => Attach(DataContext as MobileShellViewModel);
    }

    private void Attach(MobileShellViewModel? shell)
    {
        if (_shell is not null)
            _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = shell;
        if (shell is null)
            return;
        shell.PropertyChanged += OnShellPropertyChanged;
        ApplyScale();
        ApplyLayout();
        ApplyOverlayMargins();
        if (Root.Bounds.Width > 0)
            shell.UpdateSize(Root.Bounds.Width, Root.Bounds.Height);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        _insets = top?.InsetsManager;
        if (_insets is not null)
        {
            // HCG Mail draws its own bars, so it handles the safe area itself instead of being letter-boxed.
            _insets.DisplayEdgeToEdgePreference = true;
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            UpdateSafeArea();
            UpdateSystemBar();
        }
        _inputPane = top?.InputPane;
        if (_inputPane is not null)
            _inputPane.StateChanged += OnInputPaneChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
        if (_inputPane is not null)
            _inputPane.StateChanged -= OnInputPaneChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => UpdateSafeArea();

    private void UpdateSafeArea()
    {
        if (_shell is not null && _insets is not null)
            _shell.SafeArea = _insets.SafeAreaPadding;
    }

    /// <summary>Status bar text follows the HCG skin (light text on HCG Future, dark text on HCG Classic).</summary>
    private void UpdateSystemBar()
    {
        if (_insets is not null && this.TryFindResource("Hcg.Bg.TitleBar", ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid)
            _insets.SystemBarColor = solid.Color;
    }

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        if (_shell is not null)
            _shell.KeyboardHeight = e.NewState == InputPaneState.Open ? e.EndRect.Height : 0;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _shell is not null)
            e.Handled = await _shell.GoBackAsync();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MobileShellViewModel.Layout):
                ApplyLayout();
                ApplyOverlayMargins();
                break;
            case nameof(MobileShellViewModel.TextScale):
                ApplyScale();
                break;
            case nameof(MobileShellViewModel.ContentPadding):
                ApplyOverlayMargins();
                break;
        }
    }

    private void ApplyScale()
    {
        var scale = _shell?.TextScale ?? 1;
        Zoom.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? null : new ScaleTransform(scale, scale);
    }

    /// <summary>iPhone: one pane at a time. iPad portrait: list + reader with a slide-over. iPad landscape: three panes.</summary>
    internal void ApplyLayout()
    {
        var layout = _shell?.Layout ?? ShellLayout.Compact;
        Panes.ColumnDefinitions.Clear();
        Control[] all = [MailboxesPane, ListPane, ReaderPane, SidebarScrim];
        foreach (var pane in all)
        {
            Grid.SetColumn(pane, 0);
            Grid.SetColumnSpan(pane, 1);
            pane.Width = double.NaN;
            pane.HorizontalAlignment = HorizontalAlignment.Stretch;
            pane.ZIndex = 0;
        }

        switch (layout)
        {
            case ShellLayout.Compact:
                Panes.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                break;
            case ShellLayout.Regular:
                Panes.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(ListWidth)));
                Panes.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                Grid.SetColumn(ReaderPane, 1);
                Grid.SetColumnSpan(SidebarScrim, 2);
                SidebarScrim.ZIndex = 1;
                Grid.SetColumnSpan(MailboxesPane, 2);
                MailboxesPane.Width = SidebarWidth;
                MailboxesPane.HorizontalAlignment = HorizontalAlignment.Left;
                MailboxesPane.ZIndex = 2;
                break;
            case ShellLayout.Wide:
                Panes.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(SidebarWidth)));
                Panes.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(ListWidth)));
                Panes.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                Grid.SetColumn(ListPane, 1);
                Grid.SetColumn(ReaderPane, 2);
                break;
        }
    }

    /// <summary>Sheets fill the iPhone screen inside the safe area; on iPad they float as a card that stays above the keyboard.</summary>
    private void ApplyOverlayMargins()
    {
        if (_shell is null)
            return;
        var pad = _shell.ContentPadding;
        if (_shell.ShowSheetAsCard)
        {
            SheetFrame.Padding = default;
            SheetFrame.MaxWidth = SheetCardMaxWidth;
            SheetFrame.Margin = new Thickness(pad.Left + 24, pad.Top + 24, pad.Right + 24, pad.Bottom + 24);
        }
        else
        {
            SheetFrame.MaxWidth = double.PositiveInfinity;
            SheetFrame.Margin = default;
            SheetFrame.Padding = pad;
        }
        PromptCard.Margin = new Thickness(pad.Left + 28, pad.Top + 28, pad.Right + 28, pad.Bottom + 28);
        BannerHost.Margin = new Thickness(pad.Left + 12, _shell.TopInset + 8, pad.Right + 12, 0);
    }
}
