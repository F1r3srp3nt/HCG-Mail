using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HCG.Mail.Core.Services;

namespace HCG.Mail.Email.Html;

/// <summary>Converts HTML email into readable plain text for previews, quoting and the text-only view.</summary>
public sealed partial class HtmlToTextConverter : IHtmlToTextConverter
{
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "blockquote", "pre", "table", "tr", "ul", "ol",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "address", "center", "dl", "dt", "dd", "figure", "main", "nav",
    };

    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "noscript", "template", "svg", "object", "iframe",
        "form", "select", "option", "button", "textarea", "input", "canvas", "audio", "video",
    };

    public string ToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        var sb = new StringBuilder();
        if (document.Body is not null)
            Walk(document.Body, sb);

        var text = sb.ToString().Replace(" ", " ");
        text = TrailingSpaces().Replace(text, "\n");
        text = ExcessBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    AppendCollapsed(sb, text.Data);
                    break;
                case IElement element:
                    var name = element.LocalName;
                    if (SkippedElements.Contains(name) || IsHidden(element))
                        break;
                    if (name == "br")
                    {
                        sb.Append('\n');
                        break;
                    }
                    if (name == "img")
                    {
                        var alt = element.GetAttribute("alt");
                        if (!string.IsNullOrWhiteSpace(alt))
                            sb.Append('[').Append(alt.Trim()).Append(']');
                        break;
                    }

                    var isBlock = BlockElements.Contains(name);
                    if (isBlock)
                        EnsureNewLine(sb);
                    if (name == "li")
                    {
                        EnsureNewLine(sb);
                        sb.Append("• ");
                    }
                    if (name is "td" or "th" && sb.Length > 0 && sb[^1] != '\n')
                        sb.Append('\t');
                    if (name == "hr")
                    {
                        sb.Append("――――――――――\n");
                        break;
                    }

                    Walk(element, sb);

                    if (name == "a")
                    {
                        var href = element.GetAttribute("href")?.Trim();
                        var label = element.TextContent.Trim();
                        if (!string.IsNullOrEmpty(href) && href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(href, label, StringComparison.OrdinalIgnoreCase) && label.Length > 0)
                            sb.Append(" <").Append(href).Append('>');
                    }
                    if (isBlock || name == "li")
                        EnsureNewLine(sb);
                    break;
            }
        }
    }

    /// <summary>
    /// True when the HTML shows something: text outside hidden elements, or an image. A part that parses to
    /// nothing visible (an empty alternative some mailers generate) must not hide a real plain-text body.
    /// </summary>
    public static bool HasVisibleContent(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return false;
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        if (document.Body is null)
            return false;
        var text = new StringBuilder();
        Walk(document.Body, text);
        if (text.ToString().Any(c => !char.IsWhiteSpace(c) && !PreviewText.IsInvisible(c)))
            return true;
        return document.Body.QuerySelectorAll("img").Any(img => !IsHidden(img) && img.ParentElement is { } p && !HasHiddenAncestor(p));
    }

    /// <summary>Inline <c>display:none</c> / <c>visibility:hidden</c> (preheaders, Outlook-only blocks), checked on the parsed attribute.</summary>
    internal static bool IsHidden(IElement element)
    {
        if (element.HasAttribute("hidden"))
            return true;
        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
            return false;
        var compact = string.Concat(style.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        return compact.Contains("display:none") || compact.Contains("visibility:hidden");
    }

    private static bool HasHiddenAncestor(IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if (IsHidden(e))
                return true;
        }
        return false;
    }

    private static void AppendCollapsed(StringBuilder sb, string text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) && c != ' ')
            {
                if (sb.Length > 0 && sb[^1] is not (' ' or '\n' or '\t'))
                    sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
    }

    private static void EnsureNewLine(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
            sb.Append('\n');
    }

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();
}
