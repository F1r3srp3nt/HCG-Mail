using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Core.Settings;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Mime;
using HCG.Mail.Mobile.Views;
using MimeKit;
using NSubstitute;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace HCG.Mail.Mobile.Tests;

/// <summary>
/// The shared sanitizer and load interception on the mobile HTML view (HtmlLabel in a touch ScrollViewer), at phone
/// width, for every synthetic fixture in Windows-executable-Application/tests/fixtures/mail.
/// </summary>
public sealed class RenderingTests
{
    internal static string FixtureDir() => Path.Combine(SharedDir(), "tests", "fixtures", "mail");

    /// <summary>The shared HCG Mail folder as resolved by the build (HcgSharedDir in Directory.Build.props).</summary>
    internal static string SharedDir() =>
        typeof(RenderingTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "HcgSharedDir").Value!;

    private static MessageContent Fixture(string file) =>
        MimeConverter.ToContent(MimeMessage.Load(file), new MessageRef("INBOX", 1, 1), default, new HtmlToTextConverter());

    private static MessageItemViewModel Item() => new(new MessageSummary { Ref = new MessageRef("INBOX", 1, 1), Subject = "s" }, false);

    private static async Task<MessageReaderViewModel> LoadAsync(MessageContent content, RemoteImageLoader? remote = null)
    {
        var session = Substitute.For<IMailSession>();
        session.GetMessageAsync(new MessageRef("INBOX", 1, 1), Arg.Any<CancellationToken>()).Returns(content);
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer()) { RemoteImages = remote };
        await reader.LoadAsync(session, Item(), _ => Task.CompletedTask);
        return reader;
    }

    private static double Ink(WriteableBitmap frame)
    {
        using var fb = frame.Lock();
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        var counts = new Dictionary<int, int>();
        for (var i = 0; i + 3 < bytes.Length; i += 4)
        {
            var key = bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var bg = counts.MaxBy(kv => kv.Value).Key;
        long ink = 0;
        for (var i = 0; i + 3 < bytes.Length; i += 4)
        {
            var dr = Math.Abs(bytes[i] - (bg & 0xFF));
            var dg = Math.Abs(bytes[i + 1] - (bg >> 8 & 0xFF));
            var db = Math.Abs(bytes[i + 2] - (bg >> 16 & 0xFF));
            if (dr + dg + db > 60)
                ink++;
        }
        return ink / (bytes.Length / 4.0);
    }

    [AvaloniaFact]
    public async Task Every_fixture_renders_content_or_an_explicit_state_at_iPhone_width()
    {
        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(FixtureDir(), "*.eml").Order())
        {
            foreach (var mode in new[] { ReaderBodyMode.Original, ReaderBodyMode.Readable, ReaderBodyMode.PlainText })
            {
                var reader = await LoadAsync(Fixture(file));
                reader.SetModeCommand.Execute(mode);
                var body = new MobileMessageBodyView { DataContext = reader };
                var window = new Window { Content = new ScrollViewer { Content = body }, Width = Devices.IPhone.W - 32, Height = 700 };
                window.Show();
                await MobileHarness.Settle(200);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var ink = Ink(window.CaptureRenderedFrame()!);
                var name = Path.GetFileNameWithoutExtension(file);
                var dir = Environment.GetEnvironmentVariable("HCG_MAIL_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(dir) && mode == ReaderBodyMode.Original)
                {
                    Directory.CreateDirectory(dir);
                    window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"mobile-render-{name}.png"));
                }
                var state = reader.ShowHtmlView ? "html" : reader.ShowTextView ? "text" : reader.IsEmptyMessage ? "empty" : "NOTHING";
                if (state == "html" && body.Html.Bounds.Height < 10)
                    failures.Add($"{name}/{mode}: HTML has no height");
                if (state == "NOTHING" || ink < 0.0005)
                    failures.Add($"{name}/{mode}: state={state} ink={ink:P3}");
                window.Close();
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaFact]
    public async Task Html_grows_to_its_height_and_wide_newsletters_shrink_to_fit_the_screen()
    {
        var reader = await LoadAsync(Fixture(Path.Combine(FixtureDir(), "05-light-newsletter.eml")));
        var body = new MobileMessageBodyView { DataContext = reader };
        var scroller = new ScrollViewer { Content = body };
        var window = new Window { Content = scroller, Width = 358, Height = 400 };
        window.Show();
        await MobileHarness.Settle(300);
        Assert.True(reader.ShowHtmlView);
        Assert.IsType<HtmlLabel>(body.Html);
        Assert.True(body.Html.Bounds.Height > 0);
        // The 600-pixel newsletter is laid out at its own width and shrunk to the 358-point screen, not cut off.
        Assert.True(body.FitScale < 1);
        Assert.True(body.FitScale >= MobileMessageBodyView.MinFitScale);
        Assert.True(body.HtmlZoom.Bounds.Width <= body.HtmlScroller.Viewport.Width + 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Remote_images_are_not_requested_until_the_user_allows_them()
    {
        var requested = new List<string>();
        var handler = new RecordingHandler(requested);
        var loader = new RemoteImageLoader(handler);
        var reader = await LoadAsync(Fixture(Path.Combine(FixtureDir(), "07-remote-images-only.eml")), loader);
        var body = new MobileMessageBodyView { DataContext = reader };
        var window = new Window { Content = new ScrollViewer { Content = body }, Width = 358, Height = 700 };
        window.Show();
        await MobileHarness.Settle(400);
        Assert.True(reader.ShowRemoteBanner);
        Assert.Empty(requested);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Inline_cid_images_render_without_any_network()
    {
        var reader = await LoadAsync(Fixture(Path.Combine(FixtureDir(), "02-cid-images.eml")));
        Assert.NotEmpty(reader.InlineResources);
        var body = new MobileMessageBodyView { DataContext = reader };
        var window = new Window { Content = new ScrollViewer { Content = body }, Width = 358, Height = 700 };
        window.Show();
        await MobileHarness.Settle(300);
        Assert.True(Ink(window.CaptureRenderedFrame()!) > 0.001);
        window.Close();
    }

    private sealed class RecordingHandler(List<string> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
