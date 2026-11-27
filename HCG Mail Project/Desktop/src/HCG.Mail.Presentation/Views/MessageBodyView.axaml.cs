using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;

namespace HCG.Mail.App.Views;

/// <summary>Desktop message body: sanitized HTML in a scrolling panel, plain text, or the empty state.</summary>
public partial class MessageBodyView : UserControl
{
    public MessageBodyView()
    {
        InitializeComponent();
        MessageHtmlBridge.Attach(Html, () => DataContext as MessageReaderViewModel);
    }
}

/// <summary>
/// Connects an HTML control (desktop <see cref="HtmlPanel"/> or the touch-scrolling <see cref="HtmlLabel"/> used on
/// phones and tablets) to a message reader with every outbound load intercepted. HtmlRenderer has no script engine;
/// on top of the sanitizer, this only supplies inline (cid:/data:) images, remote images after the user explicitly
/// allows them, never stylesheets, and hands link clicks to the reader instead of opening them.
/// </summary>
public sealed class MessageHtmlBridge
{
    private const int MaxInlineImageBytes = 10 * 1024 * 1024;
    private readonly Func<MessageReaderViewModel?> _reader;

    private MessageHtmlBridge(Func<MessageReaderViewModel?> reader) => _reader = reader;

    public static MessageHtmlBridge Attach(HtmlControl html, Func<MessageReaderViewModel?> reader)
    {
        var bridge = new MessageHtmlBridge(reader);
        html.ImageLoad += bridge.OnImageLoad;
        html.StylesheetLoad += bridge.OnStylesheetLoad;
        html.LinkClicked += bridge.OnLinkClicked;
        html.RenderError += bridge.OnRenderError;
        return bridge;
    }

    private MessageReaderViewModel? Reader => _reader();

    private void OnRenderError(object? sender, HtmlRendererRoutedEventArgs<HtmlRenderErrorEventArgs> e)
    {
        // Image load failures are reported here too; only a failure of the document itself switches to text.
        if (e.Event.Type is HtmlRenderErrorType.Image)
            return;
        var reader = Reader;
        Dispatcher.UIThread.Post(() => reader?.OnHtmlRenderFailed(e.Event.Message));
    }


    private void OnStylesheetLoad(object? sender, HtmlRendererRoutedEventArgs<HtmlStylesheetLoadEventArgs> e)
    {
        // External stylesheets are never fetched.
        e.Event.SetSrc = null!;
        e.Event.SetStyleSheet = "";
    }

    private void OnLinkClicked(object? sender, HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs> e)
    {
        e.Event.Handled = true;
        var link = e.Event.Link;
        if (string.IsNullOrWhiteSpace(link) || link.StartsWith('#'))
            return;
        Reader?.RequestOpenLink(link);
    }

    private void OnImageLoad(object? sender, HtmlRendererRoutedEventArgs<HtmlImageLoadEventArgs> e)
    {
        var args = e.Event;
        args.Handled = true;
        var src = args.Src?.Trim() ?? "";

        try
        {
            if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                var id = src[4..].Trim('<', '>', ' ');
                var resource = Reader?.InlineResources.FirstOrDefault(r => string.Equals(r.ContentId.Trim('<', '>'), id, StringComparison.OrdinalIgnoreCase));
                if (resource is not null && resource.Data.Length <= MaxInlineImageBytes)
                {
                    args.Callback(DecodeBitmap(resource.Data));
                    return;
                }
            }
            else if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !src.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma > 0 && src[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                {
                    var data = Convert.FromBase64String(src[(comma + 1)..]);
                    if (data.Length <= MaxInlineImageBytes)
                    {
                        args.Callback(DecodeBitmap(data));
                        return;
                    }
                }
            }
            else if (Reader?.RemoteImagesAllowed == true
                     && (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                LoadRemote(Reader.RemoteImages, src, args);
                return;
            }
        }
        catch (Exception)
        {
            // Malformed or undecodable image data from the sender: render nothing.
        }

        args.Callback();
    }

    private static void LoadRemote(RemoteImageLoader? loader, string url, HtmlImageLoadEventArgs args)
    {
        if (loader is null)
        {
            args.Callback();
            return;
        }
        _ = Task.Run(async () =>
        {
            var data = await loader.TryLoadAsync(url, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    if (data is not null)
                        args.Callback(DecodeBitmap(data));
                    else
                        args.Callback();
                }
                catch (Exception)
                {
                    args.Callback();
                }
            });
        });
    }

    private static Bitmap DecodeBitmap(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        return new Bitmap(stream);
    }
}
