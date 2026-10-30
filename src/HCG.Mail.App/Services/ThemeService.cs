using Avalonia;
using HCG.Mail.App.Themes;
using HCG.Mail.Core.Settings;

namespace HCG.Mail.App.Services;

/// <summary>Applies and persists the HCG skin. Switching is a live resource change; nothing restarts or reconnects.</summary>
public sealed class ThemeService
{
    private readonly AppSettingsService _settings;

    public ThemeService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public ThemeKind Current => _settings.Current.Theme;

    public void ApplySaved() => ApplyToApplication(_settings.Current.Theme);

    public void SetTheme(ThemeKind kind)
    {
        _settings.Update(s => s with { Theme = kind });
        ApplyToApplication(kind);
    }

    private static void ApplyToApplication(ThemeKind kind)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = HcgThemeVariants.For(kind);
    }
}
