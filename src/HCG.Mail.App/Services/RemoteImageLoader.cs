using System.Net;
using System.Net.Http.Headers;

namespace HCG.Mail.App.Services;

/// <summary>
/// Downloads remote images only after the user clicks "Load images" for a message.
/// No cookies, no referrer, image content types only, bounded size and time.
/// </summary>
public sealed class RemoteImageLoader : IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly HttpClient _http;

    public RemoteImageLoader()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HCGMail", "0.1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
    }

    public async Task<byte[]?> TryLoadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || type.Contains("svg", StringComparison.OrdinalIgnoreCase))
                return null;
            if (response.Content.Headers.ContentLength > MaxBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
