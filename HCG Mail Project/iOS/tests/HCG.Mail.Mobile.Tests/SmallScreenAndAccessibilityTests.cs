using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HCG.Mail.App.Controls;
using HCG.Mail.App.ViewModels;

namespace HCG.Mail.Mobile.Tests;

/// <summary>The smallest supported iPhone, larger text, and names for controls that have only an icon.</summary>
public sealed class SmallScreenAndAccessibilityTests
{
    /// <summary>Visible controls that stick out of the window horizontally (cut off on a real phone).</summary>
    private static List<string> Overflowing(MobileHarness ui)
    {
        var width = ui.Window.Bounds.Width;
        var problems = new List<string>();
        foreach (var control in ui.View.GetVisualDescendants().OfType<Control>())
        {
            if (control is not (Button or TextBlock or TextBox or ComboBox) || !control.IsEffectivelyVisible || control.Bounds.Width <= 0)
                continue;
            if (control.FindAncestorOfType<ScrollViewer>() is { HorizontalScrollBarVisibility: not Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled })
                continue; // inside something that scrolls sideways on purpose (wide e-mail content)
            var origin = control.TranslatePoint(new Point(0, 0), ui.Window);
            if (origin is { } p && (p.X < -1 || p.X + control.Bounds.Width > width + 1))
                problems.Add($"{control.GetType().Name} '{control.Name ?? (control as TextBlock)?.Text ?? (control as Button)?.Content?.ToString()}' x={p.X:F0} w={control.Bounds.Width:F0}");
        }
        return problems;
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.3)]
    public async Task Main_screens_fit_the_smallest_iPhone(double textScale)
    {
        var platform = new FakeMobilePlatform { TextScale = textScale };
        await using var ui = await MobileHarness.StartAsync(Devices.IPhoneSE, platform);
        var problems = new List<string>();

        problems.AddRange(Overflowing(ui).Select(p => "inbox: " + p));
        ui.Capture($"se-{textScale:0.0}-inbox");

        ui.Shell.ShowMailboxesCommand.Execute(null);
        await MobileHarness.Settle();
        problems.AddRange(Overflowing(ui).Select(p => "mailboxes: " + p));
        ui.Shell.BackToMessagesCommand.Execute(null);

        ui.Mail.MessageList.SelectedItem = ui.Mail.MessageList.Items.First(i => i.Subject.Contains("HTML", StringComparison.Ordinal));
        ui.Shell.OnMessageChosen();
        await MobileHarness.WaitUntil(() => ui.Mail.Reader.HasMessage, "message");
        await MobileHarness.Settle(300);
        problems.AddRange(Overflowing(ui).Select(p => "reader: " + p));
        ui.Capture($"se-{textScale:0.0}-reader");

        ui.Mail.ComposeCommand.Execute(null);
        await MobileHarness.Settle();
        problems.AddRange(Overflowing(ui).Select(p => "compose: " + p));
        ui.Capture($"se-{textScale:0.0}-compose");
        await ui.Shell.CloseSheetCommand.ExecuteAsync(null);

        var adding = ui.Mail.AddAccountCommand.ExecuteAsync(null);
        await MobileHarness.Settle();
        problems.AddRange(Overflowing(ui).Select(p => "account setup: " + p));
        await ui.Shell.CloseSheetCommand.ExecuteAsync(null);
        await adding;

        ui.Mail.OpenSettingsCommand.Execute(null);
        await MobileHarness.Settle();
        problems.AddRange(Overflowing(ui).Select(p => "settings: " + p));

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [AvaloniaFact]
    public async Task Every_icon_only_button_has_a_name_for_VoiceOver()
    {
        await using var ui = await MobileHarness.StartAsync(Devices.IPadLandscape); // all three panes at once
        await ui.OpenFirstMessageAsync();
        var unnamed = ui.View.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Content is IconView && b.TemplatedParent is null)
            .Where(b => string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)))
            .Select(b => b.Name ?? b.GetType().Name)
            .ToList();
        Assert.True(unnamed.Count == 0, "icon-only buttons without an accessible name: " + string.Join(", ", unnamed));
    }
}
