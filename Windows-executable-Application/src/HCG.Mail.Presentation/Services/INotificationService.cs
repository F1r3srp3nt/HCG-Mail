namespace HCG.Mail.App.Services;

public interface INotificationService
{
    void Info(string title, string message, Action? onClick = null);

    void Success(string title, string message, Action? onClick = null);

    void Warning(string title, string message);

    void Error(string title, string message);
}
