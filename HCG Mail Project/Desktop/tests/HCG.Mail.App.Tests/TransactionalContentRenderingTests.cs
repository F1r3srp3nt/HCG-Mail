using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Html;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>
/// Essential transactional content (verification codes, amounts, buttons) must be visible in Original and Readable view
/// and present in Plain text. Each case is a construct found to lose the code in an earlier build: branding rendered, the
/// code did not. Visibility is measured by rendering the message twice, with the code and with a different code of the same
/// length in the same place (every digit changed, so the layout stays the same): if both renderings are identical, the
/// reader could not see the code. (Blanking the code is not a valid control: the empty box collapses, and the layout
/// shift below it looks like visible output.)
/// </summary>
public class TransactionalContentRenderingTests
{
    private const string Code = "482913";

    private static string Page(string inner, string head = "") =>
        $"<html><head>{head}</head><body style=\"margin:0;background-color:#ffffff\"><table width=\"100%\"><tr><td align=\"center\">" +
        "<div style=\"font-size:22px;color:#0B3D5C;font-weight:bold\">EVENT 2027</div><p>Your verification code is:</p>" +
        inner + "<p>The code expires in 10 minutes.</p></td></tr></table></body></html>";

    private static string Digits(string code, Func<char, string> cell) => string.Concat(code.Select(cell));

    /// <summary>Name and an HTML template taking the code (or its blank stand-in).</summary>
    public static TheoryData<string> CaseNames => new(Cases.Keys);

    private static readonly Dictionary<string, Func<string, string>> Cases = new()
    {
        // The renderer does not paint the background shorthand: white text on a "box" became white on white.
        ["background shorthand on a cell"] = c => Page($"<table><tr><td style=\"background:#0B3D5C;color:#ffffff;padding:12px\">{c}</td></tr></table>"),
        ["background shorthand in a style rule"] = c => Page($"<div class=\"otp\">{c}</div>", "<style>.otp{background:#0B3D5C;color:#ffffff;font-size:30px;padding:10px}</style>"),
        ["gradient box"] = c => Page($"<div style=\"background:linear-gradient(90deg,#0B3D5C,#1A7FB5);color:#ffffff;padding:12px\">{c}</div>"),
        ["link styled as a button"] = c => Page($"<a href=\"https://example.com/verify\" style=\"background:#0B3D5C;color:#ffffff;padding:12px 24px;display:inline-block;font-size:28px\">{c}</a>"),
        ["gradient link button"] = c => Page($"<a href=\"https://example.com/v\" style=\"background-image:linear-gradient(#1A7FB5,#0B3D5C);color:#fff;padding:12px;display:inline-block;font-size:28px\">{c}</a>"),
        // Legacy background attribute (a remote image, never loaded): the attribute vanished and left white on white.
        ["remote background attribute"] = c => Page($"<table><tr><td background=\"https://example.com/bg.png\" style=\"color:#ffffff;font-size:30px\">{c}</td></tr></table>"),
        ["white text without a background"] = c => Page($"<p style=\"color:#ffffff;font-size:30px\">{c}</p>"),
        // Form controls were removed together with their content.
        ["button"] = c => Page($"<button style=\"background:#0B3D5C;color:#fff;font-size:28px\">{c}</button>"),
        ["read-only input"] = c => Page($"<input type=\"text\" value=\"{c}\" readonly style=\"font-size:28px\">"),
        ["textarea"] = c => Page($"<textarea readonly>{c}</textarea>"),
        // The renderer draws nothing inside display:flex.
        ["flex digit boxes"] = c => Page("<div style=\"display:flex;gap:8px;justify-content:center\">" + Digits(c, d => $"<div style=\"border:1px solid #0B3D5C;font-size:28px;padding:6px\">{d}</div>") + "</div>"),
        ["flex digit boxes from a style rule"] = c => Page("<div class=\"row\">" + Digits(c, d => $"<div class=\"d\">{d}</div>") + "</div>",
            "<style>.row{display:flex;gap:6px}.d{border:1px solid #0B3D5C;font-size:28px;padding:4px}</style>"),
        ["styled with a class"] = c => Page($"<div class=\"code\">{c}</div>", "<style>.code{color:#ffffff;background-color:#0B3D5C;font-size:30px;padding:10px}</style>"),
        // Constructs that already worked stay working.
        ["bgcolor cell"] = c => Page($"<table><tr><td bgcolor=\"#0B3D5C\" style=\"color:#ffffff;padding:12px\">{c}</td></tr></table>"),
        ["table digit boxes"] = c => Page("<table><tr>" + Digits(c, d => $"<td style=\"border:1px solid #0B3D5C;font-size:28px;width:40px\">{d}</td>") + "</tr></table>"),
        ["font colour in a bgcolor cell"] = c => Page($"<table><tr><td bgcolor=\"#0B3D5C\"><center><font color=\"#ffffff\" size=\"6\">{c}</font></center></td></tr></table>"),
        ["outlook conditional comments"] = c => Page($"<!--[if mso]><p>Outlook</p><![endif]--><!--[if !mso]><!--><div style=\"font-size:30px\">{c}</div><!--<![endif]-->"),
        ["rgba and hsl colours"] = c => Page($"<h1 style=\"color:rgba(255,255,255,1);background-color:hsl(203,79%,20%)\">{c}</h1>"),
    };

    private const string OtherCode = "715360"; // differs from Code in every position

    private static byte[] Pixels(WriteableBitmap frame)
    {
        using var fb = frame.Lock();
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    private static async Task<byte[]> RenderAsync(string html, ReaderBodyMode mode, string capture)
    {
        var session = Substitute.For<IMailSession>();
        var content = new MessageContent { Ref = new MessageRef("INBOX", 1, 1), Subject = "Verification", HtmlBody = html };
        session.GetMessageAsync(Arg.Any<MessageRef>(), Arg.Any<CancellationToken>()).Returns(content);
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
        await reader.LoadAsync(session, new MessageItemViewModel(new MessageSummary { Ref = content.Ref, Subject = "s" }, false), _ => Task.CompletedTask);
        reader.SetModeCommand.Execute(mode);
        var window = new Window { Content = new MessageBodyView { DataContext = reader }, Width = 800, Height = 700 };
        window.Show();
        await UiHarness.Settle(150);
        var frame = window.CaptureRenderedFrame()!;
        if (capture.Length > 0)
            UiHarness.Capture(window, capture);
        window.Close();
        return Pixels(frame);
    }

    [AvaloniaTheory]
    [MemberData(nameof(CaseNames))]
    public async Task The_code_is_visible_in_original_and_readable_view(string name)
    {
        await using var ui = await UiHarness.StartAsync();
        var template = Cases[name];
        foreach (var mode in new[] { ReaderBodyMode.Original, ReaderBodyMode.Readable })
        {
            var withCode = await RenderAsync(template(Code), mode, $"transactional-{mode}-{name.Replace(' ', '-')}");
            var withoutCode = await RenderAsync(template(OtherCode), mode, "");
            var changed = 0;
            for (var i = 0; i + 2 < Math.Min(withCode.Length, withoutCode.Length); i += 4)
            {
                if (Math.Abs(withCode[i] - withoutCode[i]) + Math.Abs(withCode[i + 1] - withoutCode[i + 1]) + Math.Abs(withCode[i + 2] - withoutCode[i + 2]) > 90)
                    changed++;
            }
            Assert.True(changed > 150, $"{name}, {mode}: the code is not visible ({changed} pixels differ from a rendering with another code)");
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_code_survives_sanitizing_and_plain_text_extraction(string name)
    {
        var html = Cases[name](Code);
        var sanitizer = new MailHtmlSanitizer();
        foreach (var mode in new[] { MessageViewMode.Original, MessageViewMode.Readable })
        {
            var digits = new string(new HtmlToTextConverter().ToText(sanitizer.Sanitize(html, new HtmlSanitizeOptions(false, mode)).Html).Where(char.IsDigit).ToArray());
            Assert.Contains(Code, digits);
        }
        Assert.Contains(Code, new string(new HtmlToTextConverter().ToText(html).Where(char.IsDigit).ToArray()));
    }
}
