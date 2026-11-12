using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HCG.Mail.Email.Html;

/// <summary>
/// Turns preview text from any source (server PREVIEW, MailKit's partial body, Graph bodyPreview, or the decoded
/// body) into a clean one-line snippet for the message list. It works on already decoded text: markup is converted
/// with the HTML parser, never with regular expressions; the expressions here only remove plain-text noise such as
/// web addresses, image placeholders and invisible padding characters newsletters insert.
/// </summary>
public static partial class PreviewText
{
    public const int MaxLength = 220;

    private static readonly HtmlToTextConverter Html = new();

    public static string Clean(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        if (LooksLikeMarkup(text))
            text = Html.ToText(text);
        else if (text.Contains('&'))
            text = WebUtility.HtmlDecode(text);

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(IsInvisible(c) || c is '\u00A0' || c == (char)0x2028 || c == (char)0x2029 ? ' ' : c);
        text = sb.ToString();

        text = ImagePlaceholder().Replace(text, " ");
        text = BracketedUrl().Replace(text, " ");
        text = BareUrl().Replace(text, " ");
        text = EmptyBrackets().Replace(text, " ");
        text = SeparatorRun().Replace(text, " ");
        text = MarkdownEmphasis().Replace(text, " ");

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ':', '|', '-');
        if (collapsed.Length <= maxLength)
            return collapsed;
        var cut = collapsed.LastIndexOf(' ', maxLength);
        return (cut > maxLength / 2 ? collapsed[..cut] : collapsed[..maxLength]).TrimEnd(',', '.', ';', ':') + "…";
    }

    /// <summary>Zero-width and filler characters (preheader padding, soft hyphens, BOM, combining grapheme joiner).</summary>
    public static bool IsInvisible(char c) =>
        c is '͏' or '­' or '᠎' or '﻿' or '​' or '‌' or '‍' or '‎' or '‏'
          or '⁠' or '⁡' or '⁢' or '⁣' or '⁤' or '⠀' or 'ㅤ' or 'ﾠ';

    /// <summary>Plain text that is really HTML (a server snippet of raw markup, or a mislabelled text/plain part).</summary>
    internal static bool LooksLikeMarkup(string text)
    {
        var lt = text.IndexOf('<');
        return lt >= 0 && MarkupTag().IsMatch(text);
    }

    [GeneratedRegex(@"</?(p|div|br|span|b|i|u|strong|em|a|table|tr|td|th|ul|ol|li|h[1-6]|html|body|head|style|font|center|img)\b[^<>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupTag();

    [GeneratedRegex(@"\[(image|img|cid)\s*:[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePlaceholder();

    [GeneratedRegex(@"[<(\[]\s*(https?://|www\.|mailto:)[^\s>)\]]*\s*[>)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedUrl();

    [GeneratedRegex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"[(\[<]\s*[)\]>]")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"[-=_*~#•·.]{3,}")]
    private static partial Regex SeparatorRun();

    [GeneratedRegex(@"(?<=\s|^)\*{1,2}(?=\s)")]
    private static partial Regex MarkdownEmphasis();
}
