using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Core.Settings;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Mime;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>Reading pane states, rendering of every fixture, selection races and searching.</summary>
public class ReaderAndRenderingTests
{
    internal static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "mail")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "mail");
    }

    private static MessageContent Fixture(string name, MessageRef? reference = null) =>
        MimeConverter.ToContent(MimeMessage.Load(Path.Combine(FixtureDir(), name)), reference ?? new MessageRef("INBOX", 1, 1), default, new HtmlToTextConverter());

    private static MessageItemViewModel Item(uint uid) => new(new MessageSummary { Ref = new MessageRef("INBOX", uid, 1), Subject = "s" + uid }, false);

    private static IMailSession SessionReturning(params (uint Uid, MessageContent Content)[] messages)
    {
        var session = Substitute.For<IMailSession>();
        foreach (var (uid, content) in messages)
            session.GetMessageAsync(new MessageRef("INBOX", uid, 1), Arg.Any<CancellationToken>()).Returns(content);
        return session;
    }

    /// <summary>Share of pixels that differ clearly from the dominant colour: 0 means nothing is drawn.</summary>
    private static double Ink(WriteableBitmap frame)
    {
        using var fb = frame.Lock();
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        var counts = new Dictionary<int, int>();
        for (var i = 0; i + 3 < bytes.Length; i += 4)
            counts[bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16] = counts.GetValueOrDefault(bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16) + 1;
        var bg = counts.MaxBy(kv => kv.Value).Key;
        long ink = 0;
        for (var i = 0; i + 3 < bytes.Length; i += 4)
        {
            if (Math.Abs(bytes[i] - (bg & 0xFF)) + Math.Abs(bytes[i + 1] - ((bg >> 8) & 0xFF)) + Math.Abs(bytes[i + 2] - ((bg >> 16) & 0xFF)) > 120)
                ink++;
        }
        return ink / (bytes.Length / 4.0);
    }

    [AvaloniaFact]
    public async Task Every_fixture_shows_content_or_an_explicit_state_in_both_skins_and_modes()
    {
        await using var ui = await UiHarness.StartAsync();
        var themes = ui.Services.GetRequiredService<ThemeService>();
        var failures = new List<string>();
        foreach (var theme in new[] { ThemeKind.HcgFuture, ThemeKind.HcgClassic })
        {
            themes.SetTheme(theme);
            foreach (var file in Directory.GetFiles(FixtureDir(), "*.eml").Order())
            {
                foreach (var mode in new[] { ReaderBodyMode.Original, ReaderBodyMode.Readable, ReaderBodyMode.PlainText })
                {
                    var name = Path.GetFileName(file);
                    var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
                    await reader.LoadAsync(SessionReturning((1, Fixture(name))), Item(1), _ => Task.CompletedTask);
                    reader.SetModeCommand.Execute(mode);
                    var window = new Window { Content = new MessageBodyView { DataContext = reader }, Width = 800, Height = 700 };
                    window.Show();
                    await UiHarness.Settle(200);
                    var frame = window.CaptureRenderedFrame()!;
                    var ink = Ink(frame);
                    UiHarness.Capture(window, $"render-{theme}-{mode}-{Path.GetFileNameWithoutExtension(file)}");
                    window.Close();

                    var state = reader.ShowHtmlView ? "html" : reader.ShowTextView ? "text" : reader.IsEmptyMessage ? "empty" : "NOTHING";
                    if (state == "NOTHING" || ink < 0.0005)
                        failures.Add($"{theme}/{mode}/{name}: state={state} ink={ink:P3}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaFact]
    public async Task Rapid_switching_never_shows_an_older_message_that_finished_later()
    {
        var slowA = new TaskCompletionSource<MessageContent>();
        var session = Substitute.For<IMailSession>();
        session.GetMessageAsync(new MessageRef("INBOX", 1, 1), Arg.Any<CancellationToken>()).Returns(slowA.Task);
        session.GetMessageAsync(new MessageRef("INBOX", 2, 1), Arg.Any<CancellationToken>()).Returns(Fixture("04-unicode.eml", new MessageRef("INBOX", 2, 1)));
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());

        var loadA = reader.LoadAsync(session, Item(1), _ => Task.CompletedTask);
        await reader.LoadAsync(session, Item(2), _ => Task.CompletedTask);
        slowA.SetResult(Fixture("01-nested-mixed-alternative-related.eml"));
        await loadA;

        Assert.Equal(2u, reader.Item!.Ref.Uid);
        Assert.StartsWith("Unicode", reader.Subject);
        Assert.DoesNotContain("Kvartalsrapport", reader.SanitizedHtml);
        Assert.False(reader.IsLoading);
    }

    [AvaloniaFact]
    public async Task Failed_load_shows_error_and_retry_loads_the_message()
    {
        var session = Substitute.For<IMailSession>();
        session.GetMessageAsync(Arg.Any<MessageRef>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new MailOperationException(MailErrorKind.Network, "Connection lost."), _ => Task.FromResult(Fixture("04-unicode.eml")));
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());

        await reader.LoadAsync(session, Item(1), _ => Task.CompletedTask);
        Assert.Equal("Connection lost.", reader.ErrorMessage);
        Assert.False(reader.ShowPlaceholder);
        Assert.True(reader.RetryCommand.CanExecute(null));

        await reader.RetryCommand.ExecuteAsync(null);
        Assert.Null(reader.ErrorMessage);
        Assert.True(reader.ShowHtmlView);
    }

    [AvaloniaFact]
    public async Task Unexpected_exceptions_also_become_an_error_state_not_a_blank_pane()
    {
        var session = Substitute.For<IMailSession>();
        session.GetMessageAsync(Arg.Any<MessageRef>(), Arg.Any<CancellationToken>()).Returns<MessageContent>(_ => throw new InvalidOperationException("boom"));
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
        await reader.LoadAsync(session, Item(1), _ => Task.CompletedTask);
        Assert.Contains("could not be read", reader.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task Html_render_failure_falls_back_to_plain_text_with_a_notice_and_formatted_can_be_retried()
    {
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
        await reader.LoadAsync(SessionReturning((1, Fixture("05-light-newsletter.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.True(reader.ShowHtmlView);

        reader.OnHtmlRenderFailed("layout error");
        Assert.True(reader.ShowTextView);
        Assert.True(reader.HasRenderNotice);
        Assert.Contains("Weekly summary", reader.TextBody);

        reader.SetModeCommand.Execute(ReaderBodyMode.Original);
        Assert.True(reader.ShowHtmlView);
        Assert.False(reader.HasRenderNotice);
    }

    [AvaloniaFact]
    public async Task Sanitizer_failure_shows_plain_text_with_a_notice()
    {
        var sanitizer = Substitute.For<IHtmlSanitizer>();
        sanitizer.Sanitize(Arg.Any<string>(), Arg.Any<HtmlSanitizeOptions>()).Returns<SanitizedHtml>(_ => throw new InvalidOperationException("parser"));
        var reader = new MessageReaderViewModel(sanitizer);
        await reader.LoadAsync(SessionReturning((1, Fixture("05-light-newsletter.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.True(reader.ShowTextView);
        Assert.Contains("could not be prepared", reader.RenderNotice);
    }

    [AvaloniaFact]
    public async Task Empty_message_and_empty_html_alternative_are_handled_explicitly()
    {
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
        await reader.LoadAsync(SessionReturning((1, Fixture("09-genuinely-empty.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.True(reader.IsEmptyMessage);
        Assert.Equal("This message has no content.", reader.EmptyMessageText);
        Assert.False(reader.ShowHtmlView || reader.ShowTextView);

        await reader.LoadAsync(SessionReturning((2, Fixture("08-empty-html-alternative.eml"))), Item(2), _ => Task.CompletedTask);
        Assert.False(reader.IsEmptyMessage);
        Assert.True(reader.ShowTextView);
        Assert.StartsWith("The real content", reader.TextBody);
    }

    [AvaloniaFact]
    public async Task Original_uses_the_emails_own_background_readable_uses_white_and_neither_uses_the_app_theme()
    {
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
        await reader.LoadAsync(SessionReturning((1, Fixture("05-light-newsletter.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.Equal(Color.Parse("#F4F4F4"), ((ISolidColorBrush)reader.HtmlBackground).Color);
        Assert.Equal(MessageReaderViewModel.OriginalStylesheet, reader.HtmlStylesheet);

        reader.SetModeCommand.Execute(ReaderBodyMode.Readable);
        Assert.Equal(Colors.White, ((ISolidColorBrush)reader.HtmlBackground).Color);
        Assert.Equal(MessageReaderViewModel.ReadableStylesheet, reader.HtmlStylesheet);
        Assert.DoesNotContain("style=", reader.SanitizedHtml);

        await reader.LoadAsync(SessionReturning((2, Fixture("04-unicode.eml"))), Item(2), _ => Task.CompletedTask);
        Assert.Equal(Colors.White, ((ISolidColorBrush)reader.HtmlBackground).Color);
    }

    [AvaloniaFact]
    public async Task Low_contrast_layout_suggests_readable_view()
    {
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
        await reader.LoadAsync(SessionReturning((1, Fixture("11-light-text-without-background.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.True(reader.ShowContrastHint);
        reader.SetModeCommand.Execute(ReaderBodyMode.Readable);
        Assert.False(reader.ShowContrastHint);
    }

    [AvaloniaFact]
    public async Task Remote_images_stay_blocked_until_the_user_loads_them_or_enables_the_setting()
    {
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer());
        await reader.LoadAsync(SessionReturning((1, Fixture("07-remote-images-only.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.True(reader.ShowRemoteBanner);
        Assert.DoesNotContain("cdn.example.com", reader.SanitizedHtml);
        Assert.Contains("IP address", MessageReaderViewModel.RemoteImagesPrivacyText);

        reader.LoadRemoteImagesCommand.Execute(null);
        Assert.False(reader.ShowRemoteBanner);
        Assert.Contains("https://cdn.example.com/banner.jpg", reader.SanitizedHtml);

        var automatic = new MessageReaderViewModel(new MailHtmlSanitizer()) { LoadRemoteImagesAutomatically = true };
        await automatic.LoadAsync(SessionReturning((1, Fixture("07-remote-images-only.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.Contains("https://cdn.example.com/banner.jpg", automatic.SanitizedHtml);
    }

    [AvaloniaFact]
    public async Task Default_view_mode_comes_from_settings()
    {
        var reader = new MessageReaderViewModel(new MailHtmlSanitizer()) { DefaultViewMode = MessageViewMode.Readable };
        await reader.LoadAsync(SessionReturning((1, Fixture("05-light-newsletter.eml"))), Item(1), _ => Task.CompletedTask);
        Assert.True(reader.IsReadableMode);
    }

    [AvaloniaFact]
    public async Task Search_filters_loaded_messages_and_can_search_the_whole_folder_on_the_server()
    {
        var session = Substitute.For<IMailSession>();
        MessageSummary S(uint uid, string subject) => new() { Ref = new MessageRef("INBOX", uid, 1), Subject = subject, Date = DateTimeOffset.Now.AddMinutes(-uid) };
        session.GetMessagesAsync("INBOX", Arg.Is(0), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FolderPage("INBOX", [S(1, "Faktura mai"), S(2, "Lunsj")], 900, 0, 1, true));
        session.SearchAsync("INBOX", "faktura", MessageListViewModel.ServerSearchLimit, Arg.Any<CancellationToken>())
            .Returns([S(1, "Faktura mai"), S(500, "Faktura januar")]);
        var list = new MessageListViewModel();
        await list.LoadAsync(session, new FolderViewModel(new MailFolder("INBOX", "INBOX", '/', FolderRole.Inbox, true, 0, 0, 900)));

        list.SearchText = "faktura";
        Assert.Single(list.Items);
        Assert.Contains("already loaded", list.SearchScopeText);
        Assert.True(list.CanOfferServerSearch);

        await list.SearchServerCommand.ExecuteAsync(null);
        Assert.True(list.IsServerSearch);
        Assert.Equal(["Faktura mai", "Faktura januar"], list.Items.Select(i => i.Subject));
        Assert.Contains("found on the server", list.CountText);
        Assert.Contains("whole Inbox folder", list.SearchScopeText);

        list.SearchText = "faktura j";
        Assert.False(list.IsServerSearch); // editing returns to filtering the loaded list
        Assert.Empty(list.Items);
    }
}
