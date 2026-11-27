using Avalonia.Styling;
using HCG.Mail.Core.Settings;

namespace HCG.Mail.App.Themes;

/// <summary>
/// HCG skins are Avalonia theme variants. Each inherits from Dark or Light so Fluent controls
/// pick sensible defaults, while HCG resources (Themes/Palette.axaml) are keyed per skin.
/// Switching skins only changes resources: no window is recreated and no connection is touched.
/// </summary>
public static class HcgThemeVariants
{
    public static readonly ThemeVariant Future = new("HcgFuture", ThemeVariant.Dark);
    public static readonly ThemeVariant Classic = new("HcgClassic", ThemeVariant.Light);

    public static ThemeVariant For(ThemeKind kind) => kind == ThemeKind.HcgClassic ? Classic : Future;
}
