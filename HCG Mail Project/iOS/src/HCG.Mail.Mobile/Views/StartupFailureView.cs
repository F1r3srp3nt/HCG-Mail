using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using HCG.Mail.Mobile.Platform;
using HCG.Mail.Mobile.Services;

namespace HCG.Mail.Mobile.Views;

/// <summary>
/// Shown instead of closing when the main view cannot be created: what failed, and the start-up report to share.
/// Built in code from plain controls only, so it does not depend on the theme, styles or XAML that may have failed.
/// Shows the exception type only (no message), and the report holds no passwords, mail or tokens.
/// </summary>
public static class StartupFailureView
{
    public static Control Create(Exception exception, IMobilePlatform? platform)
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24, 72, 24, 24), MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = "HCG Mail could not start", FontSize = 24, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = $"Start-up stopped after \"{(StartupTrace.LastStage is { } s ? StartupTrace.Name(s) : "start")}\" ({exception.GetType().Name}). "
                 + "Your accounts, passwords and settings were not changed.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (StartupTrace.FilePath is { } report)
        {
            panel.Children.Add(new TextBlock { Text = "Start-up report: " + report, TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.75 });
            if (platform is not null)
            {
                var share = new Button { Content = "Share start-up report", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 10) };
                share.Click += async (_, _) =>
                {
                    try { await platform.ShareFileAsync(report); }
                    catch (Exception ex) { StartupTrace.Failure("sharing the start-up report", ex); }
                };
                panel.Children.Add(share);
            }
        }
        var root = new Border { Background = Brushes.White, Child = new ScrollViewer { Content = panel } };
        root.SetValue(TextElement.ForegroundProperty, Brushes.Black);
        return root;
    }
}
