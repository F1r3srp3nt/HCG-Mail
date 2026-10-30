using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HCG.Mail.App.Views;

public partial class BrandMark : UserControl
{
    /// <summary>Drop the official logo here (PNG, square, ≥ 256 px) to replace the placeholder mark.</summary>
    public static readonly Uri OfficialLogoUri = new("avares://HCGMail/Assets/Branding/hcg-logo.png");

    public BrandMark()
    {
        InitializeComponent();
        if (AssetLoader.Exists(OfficialLogoUri))
        {
            using var stream = AssetLoader.Open(OfficialLogoUri);
            OfficialLogo.Source = new Bitmap(stream);
            OfficialLogo.IsVisible = true;
            PlaceholderMark.IsVisible = false;
        }
    }

    public static bool HasOfficialLogo => AssetLoader.Exists(OfficialLogoUri);
}
