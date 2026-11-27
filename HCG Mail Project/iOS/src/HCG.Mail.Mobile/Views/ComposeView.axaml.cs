using Avalonia.Controls;
using Avalonia.Threading;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Compose;

namespace HCG.Mail.Mobile.Views;

public partial class ComposeView : UserControl
{
    public ComposeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Replies start in the body; new messages and forwards start with the recipient.
            if (DataContext is ComposeViewModel { Kind: ComposeKind.Reply or ComposeKind.ReplyAll })
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0;
            }
            else
            {
                ToBox.Focus();
            }
        });
    }
}
