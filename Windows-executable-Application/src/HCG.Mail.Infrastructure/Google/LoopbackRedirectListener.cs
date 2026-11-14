using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HCG.Mail.Infrastructure.Google;

/// <summary>
/// Receives the OAuth redirect on http://127.0.0.1:&lt;random port&gt;/ — the loopback flow Google requires for
/// installed apps. Listens on the loopback interface only, answers exactly one request to "/" (others, such as the
/// browser's favicon request, get 404) and then closes. No external process can receive the code.
/// </summary>
internal sealed class LoopbackRedirectListener : IDisposable
{
    private readonly TcpListener _listener;

    private LoopbackRedirectListener(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://127.0.0.1:{Port}/";

    public static LoopbackRedirectListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackRedirectListener(listener);
    }

    /// <summary>Waits for the browser to arrive at the redirect URI and returns its query parameters.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForRedirectAsync(Func<IReadOnlyDictionary<string, string>, string> responsePage, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var requestLine = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);
            // "GET /?code=…&state=… HTTP/1.1"
            var parts = requestLine?.Split(' ');
            if (parts is not { Length: >= 2 } || parts[0] != "GET")
            {
                await RespondAsync(stream, 400, "Bad request", ct).ConfigureAwait(false);
                continue;
            }
            var target = parts[1];
            var path = target.Split('?')[0];
            if (path != "/")
            {
                await RespondAsync(stream, 404, "Not found", ct).ConfigureAwait(false);
                continue;
            }
            var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
            await RespondAsync(stream, 200, responsePage(query), ct).ConfigureAwait(false);
            return query;
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\r\n", StringComparison.Ordinal))
                return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        }
        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }

    public void Dispose() => _listener.Stop();
}
