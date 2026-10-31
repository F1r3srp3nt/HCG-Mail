using Avalonia.Threading;

namespace HCG.Mail.App.Services;

internal static class UiThread
{
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
