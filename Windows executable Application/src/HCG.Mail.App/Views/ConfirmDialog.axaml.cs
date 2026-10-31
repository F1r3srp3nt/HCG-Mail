using Avalonia.Controls;
using Avalonia.Media;
using HCG.Mail.App.Services;

namespace HCG.Mail.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message, string primary, string? secondary, string cancel, bool destructive) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        CancelButton.Content = cancel;
        if (secondary is not null)
        {
            SecondaryButton.Content = secondary;
            SecondaryButton.IsVisible = true;
        }
        if (destructive)
        {
            DialogIcon.Data = (Geometry?)this.FindResource("Icon.Warning");
            DialogIcon.Foreground = (IBrush?)this.FindResource("Hcg.Danger");
            PrimaryButton.IsDefault = false;
        }

        PrimaryButton.Click += (_, _) => Close(DialogChoice.Primary);
        SecondaryButton.Click += (_, _) => Close(DialogChoice.Secondary);
        CancelButton.Click += (_, _) => Close(DialogChoice.Cancel);
        // Closing via the window chrome returns default(DialogChoice), which is Cancel.
        Opened += (_, _) => (destructive ? CancelButton : PrimaryButton).Focus();
    }
}
