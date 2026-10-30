using System.Text.RegularExpressions;
using AngleSharp.Dom;
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

    public SanitizedHtml Sanitize(string html, bool allowRemoteImages)
    {
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
        return new SanitizedHtml(output, blocked);
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
