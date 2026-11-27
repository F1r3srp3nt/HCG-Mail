using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HCG.Mail.Core.Settings;

namespace HCG.Mail.App.Views;

/// <summary>A miniature of the three-pane layout in a skin's colours, used on the Appearance page.</summary>
public sealed class ThemeSwatch : Control
{
    public static readonly StyledProperty<ThemeKind> KindProperty = AvaloniaProperty.Register<ThemeSwatch, ThemeKind>(nameof(Kind));

    static ThemeSwatch() => AffectsRender<ThemeSwatch>(KindProperty);

    public ThemeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var future = Kind == ThemeKind.HcgFuture;
        var w = Bounds.Width;
        var h = Bounds.Height;
        Color C(string hex) => Color.Parse(hex);

        context.FillRectangle(new SolidColorBrush(C(future ? "#05070A" : "#F2F4F7")), new Rect(0, 0, w, h));
        context.FillRectangle(new SolidColorBrush(C(future ? "#0E1319" : "#EAEEF2")), new Rect(0, 0, w * 0.24, h));
        context.FillRectangle(new SolidColorBrush(C(future ? "#090C10" : "#FAFBFC")), new Rect(w * 0.24, 0, w * 0.32, h));
        context.FillRectangle(new SolidColorBrush(C(future ? "#0B0F14" : "#FFFFFF")), new Rect(w * 0.56, 0, w * 0.44, h));

        var accent = new SolidColorBrush(C(future ? "#3FD0F5" : "#1F5C99"));
        var line = new SolidColorBrush(C(future ? "#2A3440" : "#D3DAE2"));
        var text = new SolidColorBrush(C(future ? "#C9D6E0" : "#4A5664"));

        context.DrawRectangle(accent, null, new RoundedRect(new Rect(w * 0.03, h * 0.12, w * 0.18, h * 0.13), 3));
        for (var i = 0; i < 4; i++)
            context.FillRectangle(i == 0 ? accent : line, new Rect(w * 0.04, h * (0.36 + i * 0.13), w * (i == 0 ? 0.14 : 0.12), h * 0.05));
        for (var i = 0; i < 4; i++)
        {
            var y = h * (0.12 + i * 0.21);
            if (i == 0)
                context.FillRectangle(new SolidColorBrush(C(future ? "#1A3FD0F5" : "#DDE8F4")), new Rect(w * 0.25, y - h * 0.04, w * 0.30, h * 0.19));
            context.FillRectangle(text, new Rect(w * 0.27, y, w * 0.16, h * 0.04));
            context.FillRectangle(line, new Rect(w * 0.27, y + h * 0.07, w * 0.24, h * 0.03));
        }
        context.FillRectangle(text, new Rect(w * 0.60, h * 0.14, w * 0.26, h * 0.06));
        for (var i = 0; i < 5; i++)
            context.FillRectangle(line, new Rect(w * 0.60, h * (0.32 + i * 0.11), w * (i == 4 ? 0.2 : 0.34), h * 0.035));
    }
}
