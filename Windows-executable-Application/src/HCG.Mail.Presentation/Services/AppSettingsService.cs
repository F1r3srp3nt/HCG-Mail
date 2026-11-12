using HCG.Mail.Core.Settings;

namespace HCG.Mail.App.Services;

/// <summary>Holds the current settings, persists every change immediately and notifies listeners.</summary>
public sealed class AppSettingsService
{
    private readonly IAppSettingsStore _store;

    public AppSettingsService(IAppSettingsStore store)
    {
        _store = store;
        Current = store.Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var updated = change(Current).Normalize();
        if (updated == Current)
            return;
        Current = updated;
        _store.Save(updated);
        Changed?.Invoke(this, updated);
    }
}
