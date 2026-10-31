using HCG.Mail.Email.Html;

namespace HCG.Mail.Tests.Email;

public class HtmlSanitizerTests
{
    private readonly MailHtmlSanitizer _sanitizer = new();

    [Theory]
    [InlineData("<script>alert(1)</script><p>x</p>", "<script")]
    [InlineData("<p onclick=\"steal()\">x</p>", "onclick")]
    [InlineData("<img src=x onerror=\"steal()\">", "onerror")]
    [InlineData("<a href=\"javascript:steal()\">x</a>", "javascript:")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "<iframe")]
    [InlineData("<object data=\"x.swf\"></object>", "<object")]
    [InlineData("<embed src=\"x\">", "<embed")]
    [InlineData("<form action=\"https://evil.example\"><input name=p></form>", "<form")]
    [InlineData("<form action=\"https://evil.example\"><input name=p></form>", "<input")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">", "refresh")]
    [InlineData("<base href=\"https://evil.example/\">", "<base")]
    [InlineData("<link rel=stylesheet href=\"https://evil.example/x.css\">", "<link")]
    [InlineData("<svg><script>alert(1)</script></svg>", "<svg")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>", "data:text/html")]
    [InlineData("<a href=\"vbscript:msgbox\">x</a>", "vbscript")]
    public void Removes_active_content(string html, string forbidden)
    {
        var result = _sanitizer.Sanitize(html, allowRemoteImages: false);
        Assert.DoesNotContain(forbidden, result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocks_and_counts_remote_images_by_default()
    {
        var result = _sanitizer.Sanitize("<p>Hi</p><img src=\"https://tracker.example/pixel.gif\"><img src=\"http://cdn.example/a.png\">", allowRemoteImages: false);
        Assert.DoesNotContain("tracker.example", result.Html);
        Assert.DoesNotContain("cdn.example", result.Html);
        Assert.Equal(2, result.BlockedRemoteResources);
        Assert.Contains("<p>Hi</p>", result.Html);
    }

    [Fact]
    public void Keeps_remote_images_only_when_allowed()
    {
        var result = _sanitizer.Sanitize("<img src=\"https://cdn.example/a.png\">", allowRemoteImages: true);
        Assert.Contains("https://cdn.example/a.png", result.Html);
        Assert.Equal(0, result.BlockedRemoteResources);
    }

    [Fact]
    public void Blocks_remote_css_backgrounds_even_when_images_are_allowed()
    {
        var html = "<div style=\"background-image: url('https://tracker.example/bg.png'); color: red\">x</div><table background=\"https://tracker.example/t.png\"><tr><td>y</td></tr></table>";
        var result = _sanitizer.Sanitize(html, allowRemoteImages: true);
        Assert.DoesNotContain("tracker.example", result.Html);
    }

    [Fact]
    public void Removes_remote_imports_from_style_elements()
    {
        var html = "<html><head><style>@import url(https://evil.example/a.css); p { color: blue }</style></head><body><p>x</p></body></html>";
        var result = _sanitizer.Sanitize(html, allowRemoteImages: false);
        Assert.DoesNotContain("evil.example", result.Html);
        Assert.DoesNotContain("@import", result.Html);
    }

    [Fact]
    public void Keeps_inline_and_embedded_images()
    {
        var html = "<img src=\"cid:logo@x\"><img src=\"data:image/png;base64,iVBORw0KGgo=\">";
        var result = _sanitizer.Sanitize(html, allowRemoteImages: false);
        Assert.Contains("cid:logo@x", result.Html);
        Assert.Contains("data:image/png;base64", result.Html);
        Assert.Equal(0, result.BlockedRemoteResources);
    }

    [Fact]
    public void Rejects_svg_data_images()
    {
        var result = _sanitizer.Sanitize("<img src=\"data:image/svg+xml;base64,PHN2Zz4=\">", allowRemoteImages: false);
        Assert.DoesNotContain("data:image/svg", result.Html);
    }

    [Fact]
    public void Keeps_safe_links_and_formatting()
    {
        var html = "<p style=\"color:#123456\"><b>Bold</b> <a href=\"https://www.example.com/x\">link</a> <a href=\"mailto:a@example.com\">mail</a></p><table><tr><td>cell</td></tr></table>";
        var result = _sanitizer.Sanitize(html, allowRemoteImages: false);
        Assert.Contains("https://www.example.com/x", result.Html);
        Assert.Contains("mailto:a@example.com", result.Html);
        Assert.Contains("<b>Bold</b>", result.Html);
        Assert.Contains("<td>cell</td>", result.Html);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:a@example.com", true)]
    [InlineData("#section", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("data:text/html,x", false)]
    public void Link_allow_list(string href, bool allowed) => Assert.Equal(allowed, MailHtmlSanitizer.IsAllowedLink(href));
}

public class HtmlToTextTests
{
    private readonly HtmlToTextConverter _converter = new();

    [Fact]
    public void Converts_structure_to_readable_text()
    {
        var text = _converter.ToText("<h1>Title</h1><p>First   paragraph<br>second line</p><p>Visit <a href=\"https://example.com\">our site</a></p><ul><li>A</li><li>B</li></ul>");
        Assert.Equal("Title\nFirst paragraph\nsecond line\nVisit our site <https://example.com>\n• A\n• B", text);
    }

    [Fact]
    public void Ignores_scripts_styles_and_head()
    {
        var text = _converter.ToText("<html><head><title>T</title><style>.x{}</style></head><body><script>bad()</script>Visible</body></html>");
        Assert.Equal("Visible", text);
    }

    [Fact]
    public void Empty_input_gives_empty_text() => Assert.Equal("", _converter.ToText("  "));
}

public class HtmlSanitizerRendererCompatibilityTests
{
    private readonly MailHtmlSanitizer _sanitizer = new();

    [Fact]
    public void Script_and_similar_content_is_dropped_not_shown_as_text()
    {
        var result = _sanitizer.Sanitize("<p>Keep</p><script>alert('secret')</script><noscript>fallback</noscript><title>T</title><object><p>obj</p></object>", false);
        Assert.Contains("Keep", result.Html);
        Assert.DoesNotContain("alert", result.Html);
        Assert.DoesNotContain("secret", result.Html);
        Assert.DoesNotContain("fallback", result.Html);
        Assert.DoesNotContain("obj", result.Html);
    }

    [Fact]
    public void Unknown_wrapper_tags_keep_their_text()
    {
        var result = _sanitizer.Sanitize("<section><article><o:p>Hello</o:p> world</article></section>", false);
        Assert.Contains("Hello", result.Html);
        Assert.Contains("world", result.Html);
    }

    [Theory]
    [InlineData("color: rgba(26, 31, 36, 1)", "color: #1A1F24")]
    [InlineData("background-color: rgb(255,0,16)", "background-color: #FF0010")]
    [InlineData("background: rgba(0, 0, 0, 0)", "background: transparent")]
    [InlineData("color: #123456", "color: #123456")]
    public void Colours_are_converted_to_renderer_compatible_hex(string input, string expected) =>
        Assert.Equal(expected, MailHtmlSanitizer.ToRendererColors(input));

    [Fact]
    public void Sanitized_inline_colours_are_hex()
    {
        var result = _sanitizer.Sanitize("<p style=\"color:#1a1f24\">x</p><style>p { color: rgb(1,2,3) }</style>", false);
        Assert.DoesNotContain("rgba(", result.Html);
        Assert.DoesNotContain("rgb(", result.Html);
        Assert.Contains("#1A1F24", result.Html);
    }
}
