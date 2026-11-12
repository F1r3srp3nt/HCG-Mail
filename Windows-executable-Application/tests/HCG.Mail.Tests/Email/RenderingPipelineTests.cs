using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using HCG.Mail.Email.Mime;
using MimeKit;

namespace HCG.Mail.Tests.Email;

/// <summary>Synthetic .eml fixtures (tests/fixtures/mail) through MIME parsing, body selection, sanitizing and previews.</summary>
public sealed class RenderingPipelineTests
{
    internal static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "mail")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "mail", name);
    }

    private static readonly MailHtmlSanitizer Sanitizer = new();

    internal static MessageContent Load(string name) =>
        MimeConverter.ToContent(MimeMessage.Load(Fixture(name)), new MessageRef("INBOX", 1, 1), default, new HtmlToTextConverter());

    // ---- MIME and body selection ----

    [Fact]
    public void Nested_mixed_alternative_related_selects_html_inline_image_and_attachment()
    {
        var c = Load("01-nested-mixed-alternative-related.eml");
        Assert.Contains("Kvartalsrapport", c.HtmlBody);
        Assert.Contains("tredje kvartal – med logo", c.TextBody);            // quoted-printable UTF-8 decoded
        var inline = Assert.Single(c.InlineResources);
        Assert.Equal("logo-1@example.com", inline.ContentId);
        Assert.Equal("rapport.pdf", Assert.Single(c.Attachments).OriginalFileName);
    }

    [Fact]
    public void Cid_images_match_case_insensitively_and_unreferenced_inline_image_becomes_attachment()
    {
        var c = Load("02-cid-images.eml");
        Assert.Equal(2, c.InlineResources.Count);
        Assert.Single(c.Attachments); // unused.png is listed rather than hidden
    }

    [Fact]
    public void Malformed_html_with_charset_only_in_meta_is_decoded_and_readable()
    {
        var c = Load("03-malformed-html.eml");
        Assert.NotNull(c.HtmlBody);
        Assert.Contains("ÆØÅ æøå", c.HtmlBody);
        Assert.Contains("Second cell", c.TextBody);
        Assert.True(Sanitizer.Sanitize(c.HtmlBody!, false).HasVisibleContent);
    }

    [Fact]
    public void Unicode_subject_latin1_html_and_utf8_text_are_decoded()
    {
        var c = Load("04-unicode.eml");
        Assert.Equal("Unicode: æøå – مرحبا – 中文 ✅", c.Subject);
        Assert.Contains("Blåbærsyltetøy fra Ålesund", c.HtmlBody);
        Assert.Contains("مرحبا بالعالم", c.TextBody);
        Assert.Contains("🎉", c.TextBody);
    }

    [Fact]
    public void Empty_html_alternative_does_not_hide_the_real_text_body()
    {
        // Root cause of a blank reading pane with a visible list preview.
        var c = Load("08-empty-html-alternative.eml");
        Assert.Null(c.HtmlBody);
        Assert.StartsWith("The real content is only in the plain-text part.", c.TextBody);
    }

    [Fact]
    public void Genuinely_empty_message_is_identified_as_empty()
    {
        var c = Load("09-genuinely-empty.eml");
        Assert.Null(c.HtmlBody);
        Assert.Equal("", c.TextBody);
    }

    [Fact]
    public void Html_only_message_text_excludes_styles_and_title()
    {
        var c = Load("10-html-only-style-heavy.eml");
        Assert.Contains("Thanks for your order #10042.", c.TextBody);
        Assert.DoesNotContain("padding", c.TextBody);
        Assert.DoesNotContain("Receipt", c.TextBody);
    }

    [Theory]
    [InlineData("<html><body><div style=\"display:none\">preheader</div></body></html>", false)]
    [InlineData("<html><body><!-- comment --><p>  </p></body></html>", false)]
    [InlineData("<html><body>͏‌ ͏‌</body></html>", false)]
    [InlineData("<html><body><img src=\"cid:x\"></body></html>", true)]
    [InlineData("<p>Hi</p>", true)]
    [InlineData("<html><head><style>p{color:red}</style><title>T</title></head><body></body></html>", false)]
    public void Visible_content_detection(string html, bool visible) => Assert.Equal(visible, HtmlToTextConverter.HasVisibleContent(html));

    // ---- Sanitizer: isolation and remote content ----

    [Fact]
    public void Remote_images_css_fonts_and_stylesheets_are_blocked_and_images_get_labelled_placeholders()
    {
        var html = Load("07-remote-images-only.eml").HtmlBody!;
        var r = Sanitizer.Sanitize(html, new HtmlSanitizeOptions(false));
        Assert.True(r.BlockedRemoteResources >= 3);
        Assert.DoesNotContain("cdn.example.com", r.Html);         // images, backgrounds, @import, @font-face, <link>, srcset
        Assert.DoesNotContain("track.example.com", r.Html);       // tracking pixel removed entirely
        Assert.DoesNotContain("@import", r.Html);
        Assert.DoesNotContain("@font-face", r.Html);
        Assert.DoesNotContain("<link", r.Html);
        Assert.DoesNotContain("srcset", r.Html);
        Assert.Contains("Image: Autumn sale – 30 % off", r.Html);
        Assert.Contains("href=\"https://shop.example.com/", r.Html); // links stay (opened only after a click and confirmation)
        Assert.True(r.HasVisibleContent);
    }

    [Fact]
    public void Allowing_remote_images_keeps_only_img_sources_never_css_fonts_or_stylesheets()
    {
        var html = Load("07-remote-images-only.eml").HtmlBody!;
        var r = Sanitizer.Sanitize(html, new HtmlSanitizeOptions(true));
        Assert.Contains("src=\"https://cdn.example.com/banner.jpg\"", r.Html);
        Assert.DoesNotContain("hero-bg.jpg", r.Html);
        Assert.DoesNotContain("bg2.jpg", r.Html);
        Assert.DoesNotContain("fonts.css", r.Html);
        Assert.DoesNotContain("x.woff", r.Html);
        Assert.DoesNotContain("mail.css", r.Html);
    }

    [Fact]
    public void Hostile_content_is_removed()
    {
        var r = Sanitizer.Sanitize(Load("12-hostile-content.eml").HtmlBody!, new HtmlSanitizeOptions(true));
        foreach (var bad in new[] { "<script", "alert(", "onerror", "onload", "<form", "<input", "password", "<iframe", "<object", "<svg", "javascript:", "file:", "<base", "http-equiv", "behavior", "-moz-binding", "<button" })
            Assert.DoesNotContain(bad, r.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Visible safe text.", r.Html);
        Assert.Contains("href=\"https://example.com/ok\"", r.Html);
    }

    [Fact]
    public void Light_text_over_a_blocked_background_image_gets_a_dark_stand_in_and_risk_is_reported()
    {
        var r = Sanitizer.Sanitize(Load("11-light-text-without-background.eml").HtmlBody!, new HtmlSanitizeOptions(false));
        Assert.Contains($"background-color:{MailHtmlSanitizer.BlockedBackgroundStandIn}", r.Html);
        Assert.True(r.LowContrastRisk); // white and very light paragraphs without any background
        Assert.DoesNotContain("hcg-bg-blocked", r.Html);
    }

    [Fact]
    public void Ordinary_dark_text_is_not_flagged_and_email_canvas_colour_is_reported()
    {
        var r = Sanitizer.Sanitize(Load("05-light-newsletter.eml").HtmlBody!, new HtmlSanitizeOptions(false));
        Assert.False(r.LowContrastRisk);
        Assert.Equal("#F4F4F4", r.CanvasColor);
        var plain = Sanitizer.Sanitize("<p>Hello</p>", new HtmlSanitizeOptions(false));
        Assert.Null(plain.CanvasColor); // white page by default, never the app theme
    }

    [Fact]
    public void White_on_dark_block_is_not_flagged()
    {
        var r = Sanitizer.Sanitize("<table bgcolor=\"#0f172a\"><tr><td style=\"color:#ffffff\">White on navy</td></tr></table>", new HtmlSanitizeOptions(false));
        Assert.False(r.LowContrastRisk);
    }

    [Fact]
    public void Readable_mode_removes_sender_colours_fonts_and_styles_but_keeps_content_images_and_links()
    {
        var html = Load("06-dark-and-color-scheme.eml").HtmlBody!;
        var r = Sanitizer.Sanitize(html, new HtmlSanitizeOptions(false, MessageViewMode.Readable));
        Assert.DoesNotContain("style=", r.Html);
        Assert.DoesNotContain("<style", r.Html);
        Assert.DoesNotContain("bgcolor", r.Html);
        Assert.Contains("Dark header block", r.Html);
        Assert.Null(r.CanvasColor);

        var withImage = Sanitizer.Sanitize(Load("01-nested-mixed-alternative-related.eml").HtmlBody!, new HtmlSanitizeOptions(false, MessageViewMode.Readable));
        Assert.Contains("cid:logo-1@example.com", withImage.Html);
    }

    [Fact]
    public void Dark_mode_media_rules_never_change_original_colours_inline()
    {
        var r = Sanitizer.Sanitize(Load("06-dark-and-color-scheme.eml").HtmlBody!, new HtmlSanitizeOptions(false));
        Assert.DoesNotContain("prefers-color-scheme", r.Html.Replace("@media", "").Split("<body")[1]); // only in <head><style>, never inline
    }

    // ---- Previews ----

    [Theory]
    [InlineData("View this email in your browser (https://example.com/view?u=TRACKINGTOKEN&id=123) ͏‌ ͏‌ ͏‌ ** Weekly summary ------------------------ You had 3 meetings",
        "View this email in your browser Weekly summary You had 3 meetings")]
    [InlineData("[image: Autumn sale] <https://shop.example.com/?utm_source=mail&uid=TRACKINGTOKEN> Autumn sale – 30 % off everything this week. https://shop.example.com/?uid=X",
        "Autumn sale – 30 % off everything this week.")]
    [InlineData("<p>Hello&nbsp;there,</p><p>Your <b>invoice</b> is attached.</p>", "Hello there, Your invoice is attached.")]
    [InlineData("Price &amp; terms: see www.example.com/terms", "Price & terms: see")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Previews_are_clean_decoded_text(string? raw, string expected) => Assert.Equal(expected, PreviewText.Clean(raw));

    [Fact]
    public void Previews_are_truncated_at_a_word_boundary()
    {
        var text = string.Join(' ', Enumerable.Repeat("ordet", 80));
        var preview = PreviewText.Clean(text);
        Assert.EndsWith("…", preview);
        Assert.True(preview.Length <= PreviewText.MaxLength + 1);
        Assert.DoesNotContain("orde…", preview);
    }

    [Fact]
    public void Imap_preview_normalisation_uses_the_cleaner()
    {
        Assert.Equal("Hi there", MailKitMailSession.NormalizePreview("Hi ‌ there https://track.example.com/x"));
    }
}
