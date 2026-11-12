using Avalonia.Controls;

namespace HCG.Mail.Mobile.Views;

public partial class ReaderView : UserControl
{
    public ReaderView()
    {
        InitializeComponent();
        // A newly opened message starts at the top.
        SubjectText.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectableTextBlock.TextProperty)
                MessageScroller.Offset = default;
        };
    }
}
