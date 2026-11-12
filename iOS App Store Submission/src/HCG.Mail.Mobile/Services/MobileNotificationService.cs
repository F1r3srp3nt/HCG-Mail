using HCG.Mail.App.Services;
using HCG.Mail.Mobile.ViewModels;

namespace HCG.Mail.Mobile.Services;

/// <summary>In-app banners. HCG Mail sends no push or local notifications.</summary>
public sealed class MobileNotificationService(MobileOverlays overlays) : INotificationService
{
    public void Info(string title, string message, Action? onClick = null) => Show(BannerKind.Info, title, message, onClick);
    public void Success(string title, string message, Action? onClick = null) => Show(BannerKind.Success, title, message, onClick);
    public void Warning(string title, string message) => Show(BannerKind.Warning, title, message, null);
    public void Error(string title, string message) => Show(BannerKind.Error, title, message, null);

    private void Show(BannerKind kind, string title, string message, Action? action) =>
        UiThread.Post(() => overlays.ShowBanner(new BannerViewModel(kind, title, message, action)));
}
