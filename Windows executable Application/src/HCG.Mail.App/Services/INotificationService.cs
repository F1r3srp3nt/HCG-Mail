using Avalonia.Controls;
using Avalonia.Controls.Notifications;

namespace HCG.Mail.App.Services;

public interface INotificationService
{
    void Info(string title, string message, Action? onClick = null);

    void Success(string title, string message, Action? onClick = null);

    void Warning(string title, string message);

    void Error(string title, string message);
}

/// <summary>In-app toast notifications shown in the main window (system notifications are not used yet).</summary>
public sealed class NotificationService : INotificationService
{
    private WindowNotificationManager? _manager;

    public void Attach(TopLevel host)
    {
        _manager = new WindowNotificationManager(host)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3,
            Margin = new Avalonia.Thickness(0, 0, 8, 8),
        };
    }

    public void Info(string title, string message, Action? onClick = null) => Show(title, message, NotificationType.Information, onClick);

    public void Success(string title, string message, Action? onClick = null) => Show(title, message, NotificationType.Success, onClick);

    public void Warning(string title, string message) => Show(title, message, NotificationType.Warning, null, TimeSpan.FromSeconds(9));

    public void Error(string title, string message) => Show(title, message, NotificationType.Error, null, TimeSpan.FromSeconds(10));

    private void Show(string title, string message, NotificationType type, Action? onClick, TimeSpan? duration = null) =>
        UiThread.Post(() => _manager?.Show(new Notification(title, message, type, duration ?? TimeSpan.FromSeconds(5), onClick)));
}
