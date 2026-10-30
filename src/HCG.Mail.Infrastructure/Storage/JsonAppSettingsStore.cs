using HCG.Mail.Core.Settings;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

public sealed class JsonAppSettingsStore(AppPaths paths, ILogger<JsonAppSettingsStore> logger) : IAppSettingsStore
{
    private readonly object _lock = new();

    public AppSettings Load()
    {
        lock (_lock)
            return (JsonFile.Read<AppSettings>(paths.SettingsFile, logger) ?? new AppSettings()).Normalize();
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
            JsonFile.Write(paths.SettingsFile, settings.Normalize());
    }
}

/// <summary>Non-persistent store used by development preview mode and tests.</summary>
public sealed class InMemoryAppSettingsStore(AppSettings? initial = null) : IAppSettingsStore
{
    private AppSettings _settings = initial ?? new AppSettings();

    public AppSettings Load() => _settings;

    public void Save(AppSettings settings) => _settings = settings.Normalize();
}
