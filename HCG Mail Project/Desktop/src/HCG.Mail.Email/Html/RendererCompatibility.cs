using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;

namespace HCG.Mail.Email.Html;

/// <summary>
/// Adapts sanitized email HTML to what the reading pane's CSS engine (HtmlRenderer, CSS 2 level) actually draws, so that
/// content the sender made visible stays visible. Found by rendering transactional mail patterns headlessly: the engine
/// does not paint the <c>background:</c> shorthand or gradients (white text on a "button" became white on white, the
/// branding stayed but the verification code vanished), and it draws nothing inside <c>display:flex</c> or <c>grid</c>.
/// Nothing is rewritten in place: an explicit <c>background-color</c> or a drawable <c>display</c> is appended after the
/// sender's declaration, inline or as a follow-up rule with the same selector. Security is unaffected: no URL, script or
/// remote resource is introduced.
/// </summary>
internal static partial class RendererCompatibility
{
    public sealed record StyleRule(string Selector, string? Background, string? Color);

    public static void Apply(IDocument document, CssParser css, int viewportWidth)
    {
        foreach (var styleElement in document.QuerySelectorAll("style").ToList())
            KeepApplicableMediaRules(styleElement, css, viewportWidth);

        foreach (var element in document.QuerySelectorAll("[style]").ToList())
        {
            var style = element.GetAttribute("style")!;
            var additions = new StringBuilder();
            if (MissingBackgroundColor(style, css) is { } color)
                additions.Append(";background-color:").Append(color);
            if (DrawableDisplay(style) is { } display)
            {
                additions.Append(";display:").Append(display.Value);
                if (display.ChildrenInline)
                {
                    foreach (var child in element.Children.Where(c => !HasDisplay(c.GetAttribute("style"))))
                        child.SetAttribute("style", "display:inline-block;" + (child.GetAttribute("style") ?? ""));
                    if (JustifyCenter().IsMatch(style) && !TextAlign().IsMatch(style))
                        additions.Append(";text-align:center");
                }
            }
            if (additions.Length > 0)
                element.SetAttribute("style", style + additions);
        }

        foreach (var styleElement in document.QuerySelectorAll("style").ToList())
        {
            var sheet = Parse(css, styleElement.TextContent);
            if (sheet is null)
                continue;
            var followUps = new StringBuilder();
            foreach (var rule in sheet.Rules.OfType<ICssStyleRule>())
            {
                var text = rule.Style.CssText;
                var fix = new StringBuilder();
                if (MissingBackgroundColor(text, css) is { } color)
                    fix.Append("background-color:").Append(color).Append(';');
                var inlineChildren = false;
                if (DrawableDisplay(text) is { } display)
                {
                    fix.Append("display:").Append(display.Value).Append(';');
                    inlineChildren = display.ChildrenInline;
                    if (inlineChildren && JustifyCenter().IsMatch(text) && !TextAlign().IsMatch(text))
                        fix.Append("text-align:center;");
                }
                if (fix.Length > 0)
                    followUps.Append('\n').Append(rule.SelectorText).Append('{').Append(fix).Append('}');
                if (inlineChildren) // the items of a row lay out side by side, as they did in the flex/grid row
                    followUps.Append('\n').Append(string.Join(", ", rule.SelectorText.Split(',').Select(sel => sel.Trim() + " > *"))).Append("{display:inline-block}");
            }
            if (followUps.Length > 0)
                styleElement.TextContent += followUps.ToString();
        }
    }

    /// <summary>
    /// The renderer applies every rule inside <c>@media</c>, whatever the condition, so a dark-mode block
    /// (<c>prefers-color-scheme: dark</c>) turned a light card dark, and phone breakpoints would apply in a wide pane.
    /// HCG Mail draws messages on the email's own light canvas: rules whose condition this reading surface does not meet
    /// are removed, the others are kept as they are.
    /// </summary>
    private static void KeepApplicableMediaRules(IElement styleElement, CssParser css, int viewportWidth)
    {
        if (!styleElement.TextContent.Contains("@media", StringComparison.OrdinalIgnoreCase) || Parse(css, styleElement.TextContent) is not { } sheet)
            return;
        var kept = new StringBuilder();
        var removed = false;
        foreach (var rule in sheet.Rules)
        {
            if (rule is ICssMediaRule media && !MediaApplies(media.ConditionText, viewportWidth))
            {
                removed = true;
                continue;
            }
            kept.Append(rule.CssText).Append('\n');
        }
        if (removed)
            styleElement.TextContent = MailHtmlSanitizer.ToRendererColors(kept.ToString());
    }

    internal static bool MediaApplies(string condition, int viewportWidth)
    {
        var c = string.Concat(condition.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();
        if (c.Contains("prefers-color-scheme:dark") || c.Contains("prefers-contrast:more") && c.Contains("dark"))
            return false;
        if (c.StartsWith("print") || c.StartsWith("onlyprint") || c.StartsWith("speech"))
            return false;
        foreach (Match m in WidthFeature().Matches(c))
        {
            var px = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) * (m.Groups[4].Value is "em" or "rem" ? 16 : 1);
            var isMax = m.Groups[1].Value == "max";
            if (isMax ? viewportWidth > px : viewportWidth < px)
                return false;
        }
        return true;
    }

    /// <summary>Top-level style rules that give elements a background or text colour (what the renderer applies).</summary>
    public static IReadOnlyList<StyleRule> BackgroundRules(IDocument document, CssParser css)
    {
        var rules = new List<StyleRule>();
        foreach (var styleElement in document.QuerySelectorAll("style"))
        {
            if (Parse(css, styleElement.TextContent) is not { } sheet)
                continue;
            foreach (var rule in sheet.Rules.OfType<ICssStyleRule>())
            {
                var background = Color(rule.Style.GetPropertyValue("background-color"));
                var color = Color(rule.Style.GetPropertyValue("color"));
                if ((background is not null || color is not null) && !rule.SelectorText.Contains(':'))
                    rules.Add(new StyleRule(rule.SelectorText, background, color));
            }
        }
        return rules;
    }

    /// <summary>
    /// False when the element's own text would be drawn in (nearly) the colour painted behind it: the backdrop the
    /// renderer paints (inline <c>background-color</c>, <c>bgcolor</c>, matching style rules), else the message canvas,
    /// else white. A contrast ratio under 1.5:1 is effectively invisible.
    /// </summary>
    public static bool IsLegible(IElement element, string? inlineTextColor, CssParser css, string? canvas, IReadOnlyList<StyleRule> rules)
    {
        var text = MailHtmlSanitizer.ParseColor(inlineTextColor ?? RuleColor(element, rules, r => r.Color)) ?? (0, 0, 0);
        var backdrop = PaintedBackground(element, css, rules) ?? MailHtmlSanitizer.ParseColor(canvas) ?? (255, 255, 255);
        return ContrastRatio(text, backdrop) >= 1.5;
    }

    internal static double ContrastRatio((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((byte R, byte G, byte B) c)
    {
        static double Linear(byte channel)
        {
            var v = channel / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    private static (byte R, byte G, byte B)? PaintedBackground(IElement element, CssParser css, IReadOnlyList<StyleRule> rules)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            var style = e.GetAttribute("style");
            if (style is not null && BackgroundColorLonghand().Matches(style).LastOrDefault() is { } m && MailHtmlSanitizer.ParseColor(m.Groups[1].Value.Trim()) is { } inline)
                return inline;
            if (MailHtmlSanitizer.ParseColor(e.GetAttribute("bgcolor")) is { } attribute)
                return attribute;
            if (MailHtmlSanitizer.ParseColor(RuleColor(e, rules, r => r.Background, inheritedFromAncestors: false)) is { } rule)
                return rule;
        }
        return null;
    }

    /// <summary>The last matching rule's value; for text colour, also inherited from the nearest ancestor that has one.</summary>
    private static string? RuleColor(IElement element, IReadOnlyList<StyleRule> rules, Func<StyleRule, string?> pick, bool inheritedFromAncestors = true)
    {
        if (rules.Count == 0)
            return null;
        for (var e = element; e is not null; e = inheritedFromAncestors ? e.ParentElement : null)
        {
            string? found = null;
            foreach (var rule in rules)
            {
                if (pick(rule) is { } value && Matches(e, rule.Selector))
                    found = value;
            }
            if (found is not null)
                return found;
        }
        return null;
    }

    private static bool Matches(IElement element, string selector)
    {
        try
        {
            return element.Matches(selector);
        }
        catch (Exception)
        {
            return false; // selector AngleSharp cannot evaluate
        }
    }

    /// <summary>The colour to declare explicitly when the background is set only by the shorthand or as a gradient.</summary>
    private static string? MissingBackgroundColor(string declarations, CssParser css)
    {
        if (!declarations.Contains("background", StringComparison.OrdinalIgnoreCase) || BackgroundColorLonghand().IsMatch(declarations))
            return null;
        var parsed = css.ParseDeclaration(declarations);
        if (parsed is null)
            return null;
        if (Color(parsed.GetPropertyValue("background-color")) is { } color)
            return color;
        // A gradient the renderer cannot draw: its first colour stop is the closest solid colour.
        var image = parsed.GetPropertyValue("background-image");
        if (image.Contains("gradient", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match stop in ColourToken().Matches(image))
            {
                if (Color(stop.Value) is { } first)
                    return first;
            }
        }
        return null;
    }

    private static (string Value, bool ChildrenInline)? DrawableDisplay(string declarations)
    {
        var match = DisplayDeclaration().Matches(declarations).LastOrDefault();
        if (match is null)
            return null;
        var value = match.Groups[1].Value.ToLowerInvariant();
        var column = FlexColumn().IsMatch(declarations);
        return value switch
        {
            "flex" or "grid" => ("block", !column),
            "inline-flex" or "inline-grid" => ("inline-block", !column),
            "contents" or "flow-root" => ("block", false),
            _ => null,
        };
    }

    private static bool HasDisplay(string? style) => style is not null && DisplayAny().IsMatch(style);

    private static string? Color(string? value) =>
        MailHtmlSanitizer.ParseColor(value) is { } rgb ? $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}" : null;

    private static ICssStyleSheet? Parse(CssParser css, string text)
    {
        try
        {
            return css.ParseStyleSheet(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(max|min)-(width|device-width):(\d+(?:\.\d+)?)(px|em|rem)?")]
    private static partial Regex WidthFeature();

    [GeneratedRegex(@"(?:^|;)\s*background-color\s*:\s*([^;!]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundColorLonghand();

    [GeneratedRegex(@"(?:^|;)\s*display\s*:\s*(inline-flex|inline-grid|flex|grid|contents|flow-root)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayDeclaration();

    [GeneratedRegex(@"(?:^|;)\s*display\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayAny();

    [GeneratedRegex(@"(?:^|;)\s*(?:flex-direction\s*:\s*column|flex-flow\s*:\s*column)", RegexOptions.IgnoreCase)]
    private static partial Regex FlexColumn();

    [GeneratedRegex(@"(?:^|;)\s*justify-content\s*:\s*center", RegexOptions.IgnoreCase)]
    private static partial Regex JustifyCenter();

    [GeneratedRegex(@"(?:^|;)\s*text-align\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex TextAlign();

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)|\b(?:white|black|navy|blue|red|green|gray|grey|silver)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ColourToken();
}
