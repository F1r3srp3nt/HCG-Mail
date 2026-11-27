using Avalonia.Controls;
using Avalonia.Media;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;

namespace HCG.Mail.Mobile.Views;

public partial class MobileMessageBodyView : UserControl
{
    /// <summary>Very wide layouts are capped; beyond this the content is panned rather than laid out wider.</summary>
    private const double MaxContentWidth = 1600;

    /// <summary>Shrinking stops here so text stays legible; wider content is panned instead.</summary>
    public const double MinFitScale = 0.55;

    public MobileMessageBodyView()
    {
        InitializeComponent();
        // Same safe loading as the desktop: cid:/data: images only, remote images after consent, no stylesheets.
        MessageHtmlBridge.Attach(Html, () => DataContext as MessageReaderViewModel);
        // Only a change of the available width re-fits; a height change (for example the horizontal scroll bar
        // appearing) must not, or fitting and scrolling would keep triggering each other.
        HtmlScroller.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _fittedViewport) > 1)
                FitWidth(reset: true);
        };
        Html.PropertyChanged += (_, e) =>
        {
            if (e.Property == TheArtOfDev.HtmlRenderer.Avalonia.HtmlControl.TextProperty)
                FitWidth(reset: true);
        };
        Html.LayoutUpdated += (_, _) => FitWidth(reset: false);
    }

    private double _fittedViewport = -1;

    /// <summary>The current shrink-to-fit factor (1 when the message fits the screen).</summary>
    public double FitScale { get; private set; } = 1;

    /// <summary>
    /// Lays the message out at the screen width. If fixed-width content is wider, lays it out at its own width and
    /// scales it down to fit the screen (not below <see cref="MinFitScale"/>; anything still wider can be panned).
    /// </summary>
    private void FitWidth(bool reset)
    {
        var viewport = HtmlScroller.Bounds.Width;
        if (viewport <= 0)
            return;
        _fittedViewport = viewport;
        var width = viewport;
        if (!reset && Html.Container is { } container)
        {
            var content = container.ActualSize.Width;
            if (content > Html.Width + 1 || (!double.IsNaN(Html.Width) && Html.Width > viewport + 1))
                width = Math.Min(Math.Max(content, Html.Width), MaxContentWidth);
        }
        var scale = Math.Max(MinFitScale, Math.Min(1, viewport / width));
        if (double.IsNaN(Html.Width) || Math.Abs(Html.Width - width) > 1)
            Html.Width = width;
        if (Math.Abs(FitScale - scale) > 0.005)
        {
            FitScale = scale;
            HtmlZoom.LayoutTransform = scale < 0.999 ? new ScaleTransform(scale, scale) : null;
        }
    }
}
