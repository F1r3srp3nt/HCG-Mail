using AngleSharp.Dom;

namespace HCG.Mail.Email.Html;

/// <summary>
/// Turns form controls in received HTML into inert text before sanitizing or extracting text. Transactional mail often
/// puts the one thing the reader needs into a control: a verification code in a read-only <c>&lt;input&gt;</c> or a
/// styled <c>&lt;button&gt;</c>, an address in a <c>&lt;textarea&gt;</c>. Removing the control would remove that content
/// (it did: a code shown as <c>&lt;button&gt;482913&lt;/button&gt;</c> disappeared). The replacement is a plain span or
/// div carrying the same text and inline style; nothing can be typed, submitted or activated, and the sanitizer still
/// runs afterwards.
/// </summary>
internal static class FormControls
{
    // Inputs whose value is shown to the reader as text. Hidden and password values are not meant to be read; checkboxes,
    // radios, files and image inputs carry no readable value.
    private static readonly HashSet<string> TextValueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "text", "number", "tel", "email", "url", "search", "submit", "button", "reset",
        "date", "time", "datetime-local", "month", "week",
    };

    public static void Flatten(IDocument document)
    {
        foreach (var control in document.QuerySelectorAll("button, input, textarea, select").ToList())
        {
            if (control.Parent is null)
                continue; // already removed together with an enclosing control

            IElement replacement;
            switch (control.LocalName)
            {
                case "button":
                    replacement = document.CreateElement("span");
                    foreach (var child in control.ChildNodes.ToList())
                        replacement.AppendChild(child);
                    break;
                case "input":
                    var type = control.GetAttribute("type")?.Trim() ?? "";
                    var value = control.GetAttribute("value");
                    if (!TextValueTypes.Contains(type) || string.IsNullOrWhiteSpace(value))
                    {
                        control.Remove();
                        continue;
                    }
                    replacement = document.CreateElement("span");
                    replacement.TextContent = value;
                    break;
                case "textarea":
                    replacement = document.CreateElement("div");
                    replacement.TextContent = control.TextContent;
                    break;
                default: // select: the option a reader would see
                    var option = control.QuerySelector("option[selected]") ?? control.QuerySelector("option");
                    if (option is null || string.IsNullOrWhiteSpace(option.TextContent))
                    {
                        control.Remove();
                        continue;
                    }
                    replacement = document.CreateElement("span");
                    replacement.TextContent = option.TextContent.Trim();
                    break;
            }

            foreach (var name in new[] { "class", "title", "dir", "lang", "hidden" })
            {
                if (control.GetAttribute(name) is { } attribute)
                    replacement.SetAttribute(name, attribute);
            }
            var style = control.GetAttribute("style") ?? "";
            var layout = replacement.LocalName == "div" ? "white-space:pre-wrap;" : "display:inline-block;";
            replacement.SetAttribute("style", layout + style);
            control.Replace(replacement);
        }
    }
}
