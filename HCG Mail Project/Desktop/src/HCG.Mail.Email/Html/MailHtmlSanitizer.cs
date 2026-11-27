using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using HCG.Mail.Core.Services;

namespace HCG.Mail.Email.Html;

/// <summary>
/// Allow-list sanitizer for HTML email. Everything not explicitly allowed is removed:
/// scripts, event handlers, forms, frames, objects, SVG, meta refresh, base, link and so on.
/// Remote content is blocked by default; even when the user allows remote images, remote
/// stylesheets, fonts and CSS backgrounds stay blocked.
/// </summary>
public sealed partial class MailHtmlSanitizer : Core.Services.IHtmlSanitizer
{
    private static readonly string[] AllowedTags =
    [
        "html", "head", "body", "style",
        "a", "abbr", "address", "b", "bdi", "bdo", "big", "blockquote", "br", "caption", "center", "cite", "code",
        "col", "colgroup", "dd", "del", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img", "ins", "kbd", "li", "mark", "ol", "p", "pre", "q",
        "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "tt", "u", "ul", "var", "wbr",
    ];

    private static readonly string[] AllowedAttributes =
    [
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "class", "color", "colspan", "dir",
        "face", "height", "href", "hspace", "lang", "rowspan", "size", "src", "style", "title", "valign",
        "vspace", "width", "start", "type", "cite", "datetime", "span", "nowrap",
    ];

    // Elements whose content must disappear together with the element (otherwise script source
    // or fallback markup would end up as visible text when the tag itself is stripped).
    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "noscript", "template", "title", "iframe", "frame", "frameset", "object", "embed", "applet",
        "svg", "math", "canvas", "audio", "video", "select", "textarea", "button", "xml",
    };

    private static readonly string[] SafeDataImageTypes =
        ["data:image/png", "data:image/jpeg", "data:image/jpg", "data:image/gif", "data:image/webp", "data:image/bmp"];

    private const string BlockedBackgroundMarker = "hcg-bg-blocked";
    private const string PlaceholderMarker = "data-hcg-placeholder";

    /// <summary>Stand-in for a remote background image that was blocked, under text that needs a dark backdrop.</summary>
    internal const string BlockedBackgroundStandIn = "#3D434A";

    public SanitizedHtml Sanitize(string html, bool allowRemoteImages) => Sanitize(html, new HtmlSanitizeOptions(allowRemoteImages));

    public SanitizedHtml Sanitize(string html, HtmlSanitizeOptions options)
    {
        var allowRemoteImages = options.AllowRemoteImages;
        var blocked = 0;
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(AllowedTags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(AllowedAttributes, StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(HtmlSanitizerDefaults.AllowedCssProperties, StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(["http", "https", "mailto", "cid", "data"], StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href", "src", "background", "cite", "action", "longdesc", "poster", "srcset"], StringComparer.OrdinalIgnoreCase),
            // @import and @font-face would load remote resources; only @media is kept.
            AllowedAtRules = new HashSet<AngleSharp.Css.Dom.CssRuleType>([AngleSharp.Css.Dom.CssRuleType.Media]),
        })
        {
            KeepChildNodes = true,
        };

        sanitizer.RemovingTag += (_, e) =>
        {
            if (DropWithContent.Contains(e.Tag.LocalName))
                e.Tag.TextContent = string.Empty;
        };

        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.SanitizedUrl is null)
                return;
            var url = e.SanitizedUrl.Trim();
            var tag = e.Tag?.LocalName ?? "";

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (tag != "img" || !SafeDataImageTypes.Any(t => url.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
                    e.SanitizedUrl = null;
                return;
            }

            if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                if (tag != "img")
                    e.SanitizedUrl = null;
                return;
            }

            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                if (tag != "a")
                    e.SanitizedUrl = null;
                return;
            }

            // http(s): links are kept (opening them always requires a click and confirmation);
            // remote images only when allowed; any other remote load is removed.
            if (tag is "a")
                return;
            if (tag == "img" && allowRemoteImages)
                return;
            blocked++;
            e.SanitizedUrl = null;
            // A CSS url() on an element's own style (background image): remember the element, so text that relied on
            // that image as its backdrop can be kept readable in PostProcess.
            if (e.Tag is not null && tag is not ("img" or "style"))
                e.Tag.ClassList.Add(BlockedBackgroundMarker);
        };

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element)
                return;

            // Remote url() references in inline styles (backgrounds, list images).
            var style = element.GetAttribute("style");
            if (style is not null)
            {
                if (RemoteCssUrl().IsMatch(style))
                {
                    blocked += RemoteCssUrl().Matches(style).Count;
                    style = RemoteCssUrl().Replace(style, "none");
                    element.ClassList.Add(BlockedBackgroundMarker); // class survives attribute filtering; removed again in PostProcess
                }
                element.SetAttribute("style", ToRendererColors(style));
            }
            if (element.LocalName == "style")
                element.TextContent = ToRendererColors(RemoteCssUrl().Replace(element.TextContent, "none"));

            if (element.LocalName == "a")
            {
                var href = element.GetAttribute("href");
                if (href is not null && !IsAllowedLink(href))
                    element.RemoveAttribute("href");
            }
        };

        var output = sanitizer.SanitizeDocument(html);
        return PostProcess(output, blocked, options.Mode);
    }

    /// <summary>
    /// Second pass over the sanitized document (parsed with AngleSharp, styles read with its CSS parser): makes
    /// blocked images visible as labelled placeholders, keeps text readable where a blocked background image was
    /// its only backdrop, reports the email's own page colour and low-contrast risks, and builds Readable view.
    /// </summary>
    private static SanitizedHtml PostProcess(string sanitized, int blocked, MessageViewMode mode)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(sanitized);
        var body = document.Body;
        if (body is null)
            return new SanitizedHtml(sanitized, blocked) { HasVisibleContent = false };

        // Images whose source was removed (remote while blocked, or an unsafe scheme).
        foreach (var img in body.QuerySelectorAll("img").ToList())
        {
            if (!string.IsNullOrWhiteSpace(img.GetAttribute("src")))
                continue;
            var alt = img.GetAttribute("alt")?.Trim();
            if (string.IsNullOrEmpty(alt) || IsTiny(img))
            {
                img.Remove(); // spacer or tracking pixel: nothing to show
                continue;
            }
            var label = document.CreateElement("span");
            label.SetAttribute("style", "display:inline-block;border:1px dashed #9AA4AE;color:#4B5560;background-color:#F3F5F7;padding:6px 10px;margin:2px;font-size:9pt;");
            label.SetAttribute(PlaceholderMarker, "1");
            label.TextContent = "Image: " + alt;
            img.Replace(label);
        }

        var cssParser = new CssParser();
        var canvas = mode == MessageViewMode.Original ? CanvasColorOf(body, cssParser) ?? CanvasColorOf(document.DocumentElement, cssParser) : null;
        var lowContrast = false;

        foreach (var element in body.QuerySelectorAll("*").Prepend(body).ToList())
        {
            if (element.ClassList.Contains(BlockedBackgroundMarker))
            {
                element.ClassList.Remove(BlockedBackgroundMarker);
                if (element.ClassList.Length == 0)
                    element.RemoveAttribute("class");
                if (mode == MessageViewMode.Original && IsLight(TextColorOf(element, cssParser)) && BackgroundColorOf(element, cssParser) is null)
                    element.SetAttribute("style", (element.GetAttribute("style") ?? "") + $";background-color:{BlockedBackgroundStandIn}");
            }

            if (element.HasAttribute(PlaceholderMarker))
            {
                element.RemoveAttribute(PlaceholderMarker);
                continue;
            }

            if (mode == MessageViewMode.Readable)
            {
                foreach (var attribute in new[] { "style", "class", "bgcolor", "color", "face", "size", "background" })
                    element.RemoveAttribute(attribute);
                continue;
            }

            if (!lowContrast && HasOwnText(element) && IsLight(OwnTextColor(element, cssParser)) && !HasBackdrop(element, cssParser, canvas))
                lowContrast = true;
        }

        if (mode == MessageViewMode.Readable)
        {
            foreach (var style in document.QuerySelectorAll("style").ToList())
                style.Remove();
        }

        var visible = HtmlToTextConverter.HasVisibleContent(document.DocumentElement.OuterHtml);
        return new SanitizedHtml(document.DocumentElement.OuterHtml, blocked)
        {
            CanvasColor = canvas,
            LowContrastRisk = lowContrast,
            HasVisibleContent = visible,
        };
    }

    private static bool IsTiny(IElement img) =>
        int.TryParse(img.GetAttribute("width"), out var w) && w <= 2 || int.TryParse(img.GetAttribute("height"), out var h) && h <= 2;

    private static bool HasOwnText(IElement element) =>
        element.ChildNodes.OfType<IText>().Any(t => t.Data.Any(c => !char.IsWhiteSpace(c))) && !HtmlToTextConverter.IsHidden(element);

    private static string? OwnTextColor(IElement element, CssParser css)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            var color = Declared(e, css, "color") ?? (e.LocalName == "font" ? e.GetAttribute("color") : null);
            if (color is not null)
                return color;
        }
        return null;
    }

    private static string? TextColorOf(IElement element, CssParser css) => OwnTextColor(element, css);

    /// <summary>True when the element or an ancestor paints a background (colour, or a background image) behind its text.</summary>
    private static bool HasBackdrop(IElement element, CssParser css, string? canvas)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if (BackgroundColorOf(e, css) is not null)
                return true;
            var image = Declared(e, css, "background-image");
            if (image is not null && !image.Equals("none", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return canvas is not null && !IsLight(canvas);
    }

    private static string? BackgroundColorOf(IElement element, CssParser css)
    {
        var color = Declared(element, css, "background-color") ?? element.GetAttribute("bgcolor");
        return color is null || color.Equals("transparent", StringComparison.OrdinalIgnoreCase) || color.Equals("initial", StringComparison.OrdinalIgnoreCase) ? null : color;
    }

    private static string? CanvasColorOf(IElement? element, CssParser css)
    {
        if (element is null)
            return null;
        var color = BackgroundColorOf(element, css);
        return ParseColor(color) is { } rgb ? $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}" : null;
    }

    private static string? Declared(IElement element, CssParser css, string property)
    {
        var style = element.GetAttribute("style");
        if (string.IsNullOrWhiteSpace(style))
            return null;
        var value = css.ParseDeclaration(style)?.GetPropertyValue(property);
        return string.IsNullOrWhiteSpace(value) ? null : ToRendererColors(value.Trim());
    }

    /// <summary>Relative luminance above 0.75 (white, near-white and very light greys).</summary>
    internal static bool IsLight(string? color) =>
        ParseColor(color) is { } c && 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B) > 0.75;

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    internal static (byte R, byte G, byte B)? ParseColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        color = ToRendererColors(color.Trim()).ToLowerInvariant();
        switch (color)
        {
            case "white": return (255, 255, 255);
            case "black": return (0, 0, 0);
            case "silver": return (192, 192, 192);
            case "gray" or "grey": return (128, 128, 128);
            case "whitesmoke": return (245, 245, 245);
            case "gainsboro": return (220, 220, 220);
            case "lightgray" or "lightgrey": return (211, 211, 211);
        }
        if (!color.StartsWith('#'))
            return null;
        var hex = color[1..];
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return null;
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>Links may only point to web pages, mail addresses, or anchors within the message.</summary>
    public static bool IsAllowedLink(string href)
    {
        href = href.Trim();
        return href.StartsWith('#')
               || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sanitizer re-serializes colours as rgba(), which the CSS2-level renderer cannot parse
    /// (text would become invisible). Convert them to hex; fully transparent becomes "transparent".
    /// </summary>
    internal static string ToRendererColors(string css) =>
        RgbaColor().Replace(css, m =>
        {
            var alpha = m.Groups[4].Success ? m.Groups[4].Value : "1";
            if (alpha is "0" or "0.0" or "0%")
                return "transparent";
            static int Channel(string v) => Math.Clamp(int.Parse(v, System.Globalization.CultureInfo.InvariantCulture), 0, 255);
            return $"#{Channel(m.Groups[1].Value):X2}{Channel(m.Groups[2].Value):X2}{Channel(m.Groups[3].Value):X2}";
        });

    [GeneratedRegex(@"url\(\s*['""]?\s*(https?:)?//[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteCssUrl();

    [GeneratedRegex(@"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([\d.]+%?)\s*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex RgbaColor();
}
