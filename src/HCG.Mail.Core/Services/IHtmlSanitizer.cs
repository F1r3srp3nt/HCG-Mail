namespace HCG.Mail.Core.Services;

public sealed record SanitizedHtml(string Html, int BlockedRemoteResources);

/// <summary>
/// Removes scripts, event handlers, forms, frames, embedded objects and other active content
/// from sender HTML. Remote resources (images, stylesheets, fonts) are removed unless
/// <paramref name="allowRemoteImages"/> is set, in which case only http(s) images are kept.
/// </summary>
public interface IHtmlSanitizer
{
    SanitizedHtml Sanitize(string html, bool allowRemoteImages);
}

public interface IHtmlToTextConverter
{
    string ToText(string html);
}
