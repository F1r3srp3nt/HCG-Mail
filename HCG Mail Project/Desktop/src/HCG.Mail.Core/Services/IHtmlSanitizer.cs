namespace HCG.Mail.Core.Services;

/// <param name="Html">Sanitized HTML, safe to hand to the renderer.</param>
/// <param name="BlockedRemoteResources">Remote images, backgrounds and other loads that were removed.</param>
public sealed record SanitizedHtml(string Html, int BlockedRemoteResources)
{
    /// <summary>The page background the email itself sets on &lt;body&gt; (#RRGGBB), or null for the default white page.</summary>
    public string? CanvasColor { get; init; }

    /// <summary>Light text was found without a background of its own; it may be faint on a light page. Suggest Readable view.</summary>
    public bool LowContrastRisk { get; init; }

    /// <summary>False when the HTML shows nothing (no visible text, no images).</summary>
    public bool HasVisibleContent { get; init; } = true;
}

/// <summary>How a message's HTML is presented. Neither mode inverts colours.</summary>
public enum MessageViewMode
{
    /// <summary>The sender's layout, colours and fonts (made safe).</summary>
    Original,

    /// <summary>The same content, images and links with the sender's colours, backgrounds and fonts removed.</summary>
    Readable,
}

public sealed record HtmlSanitizeOptions(bool AllowRemoteImages, MessageViewMode Mode = MessageViewMode.Original);

/// <summary>
/// Removes scripts, event handlers, forms, frames, embedded objects and other active content
/// from sender HTML. Remote resources (images, stylesheets, fonts) are removed unless
/// remote images are allowed, in which case only http(s) images are kept.
/// </summary>
public interface IHtmlSanitizer
{
    SanitizedHtml Sanitize(string html, HtmlSanitizeOptions options);

    SanitizedHtml Sanitize(string html, bool allowRemoteImages) => Sanitize(html, new HtmlSanitizeOptions(allowRemoteImages));
}

public interface IHtmlToTextConverter
{
    string ToText(string html);
}
