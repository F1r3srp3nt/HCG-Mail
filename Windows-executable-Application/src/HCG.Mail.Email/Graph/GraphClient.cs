using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Email.Graph;

/// <summary>
/// Minimal Microsoft Graph v1.0 REST client for one account. Adds the delegated access token, refreshes it once on
/// 401, honours Retry-After for idempotent reads only (never for sends, drafts, moves or deletes, which could be
/// duplicated), and turns Graph errors into messages the user can act on. Uses .NET's standard TLS validation with
/// online revocation checking; there is no custom certificate callback.
/// </summary>
public sealed class GraphClient
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0";
    internal static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(10);
    private const int MaxReadRetries = 2;

    private static readonly Lazy<HttpMessageHandler> SharedHandler = new(() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = true, // $value downloads may redirect to Microsoft storage
        MaxAutomaticRedirections = 3,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        SslOptions = new SslClientAuthenticationOptions { CertificateRevocationCheckMode = X509RevocationMode.Online },
    });

    private readonly HttpClient _http;
    private readonly IMicrosoftAuth _auth;
    private readonly string _accountKey;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GraphClient(IMicrosoftAuth auth, string accountKey, ILogger logger, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _auth = auth;
        _accountKey = accountKey;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _http = new HttpClient(handler ?? SharedHandler.Value, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HCGMail", "0.1"));
    }

    public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), idempotent: true, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), idempotent: true, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A write. Never retried automatically: a lost reply would make a retry repeat the change.</summary>
    public async Task<JsonDocument?> WriteAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct, bool idempotent = false)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(method, Url(path)) { Content = content }, idempotent, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
            return null;
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, bool idempotent, CancellationToken ct)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            var token = await _auth.GetAccessTokenAsync(_accountKey, forceRefresh: refreshed, ct).ConfigureAwait(false);
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new MailOperationException(MailErrorKind.Timeout, "Microsoft did not answer in time. Check the connection and try again.", ex)
                {
                    OutcomeUnknown = !idempotent,
                };
            }
            catch (HttpRequestException ex)
            {
                // Name resolution and connection failures happen before anything is sent; other failures can happen
                // after Microsoft received the request, so a write's outcome is then unknown.
                var notSent = ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError;
                throw new MailOperationException(MailErrorKind.Network,
                    notSent
                        ? "Could not reach Microsoft (graph.microsoft.com). Check the internet connection; the request was not sent."
                        : "The connection to Microsoft failed while waiting for its reply. Check the connection before trying again.", ex)
                {
                    OutcomeUnknown = !notSent && !idempotent,
                };
            }

            if (response.IsSuccessStatusCode)
                return response;

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                response.Dispose();
                refreshed = true; // the token may have been revoked or expired early: try one fresh token
                continue;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = RetryAfter(response);
                if (idempotent && attempt < MaxReadRetries && wait <= MaxRetryWait)
                {
                    response.Dispose();
                    _logger.LogInformation("Microsoft Graph throttled a read; retrying after {Seconds} s", wait.TotalSeconds);
                    await _delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
            }

            using (response)
                throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
        }
    }

    internal static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return TimeSpan.FromSeconds(5);
    }

    internal static async Task<MailOperationException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null, message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
        }

        var status = (int)response.StatusCode;
        var detail = string.IsNullOrWhiteSpace(message) ? $"HTTP {status}" : $"{Trim(message)} (HTTP {status})";
        return (response.StatusCode, code) switch
        {
            (HttpStatusCode.Unauthorized, _) => new(MailErrorKind.ReconnectRequired,
                "Microsoft no longer accepts the sign-in for this account — it expired or access was revoked. Reconnect the account to continue."),
            (HttpStatusCode.Forbidden, _) => new(MailErrorKind.Authentication,
                $"Microsoft denied access to this mailbox: {detail}. The permission may have been removed or your organisation may block this app; reconnect, or ask your Microsoft 365 administrator."),
            (HttpStatusCode.NotFound, "MailboxNotEnabledForRESTAPI" or "MailboxNotSupportedForRESTAPI" or "ResourceNotFound" or "ErrorMailboxNotFound") => new(MailErrorKind.NotSupported,
                "This Microsoft account has no mailbox that Microsoft Graph can reach — for example a mailbox on an on-premises Exchange server, an account without an Exchange Online licence, or a mailbox type HCG Mail does not support (shared mailboxes are not supported)."),
            (HttpStatusCode.NotFound, "ErrorItemNotFound") => new(MailErrorKind.MessageNotFound, "The message no longer exists on the server. Refresh the folder."),
            (HttpStatusCode.NotFound, _) => new(MailErrorKind.FolderNotFound, $"Microsoft could not find the item: {detail}. Refresh the folder."),
            (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable, _) => new(MailErrorKind.Throttled,
                $"Microsoft is limiting requests for this mailbox. Try again in about {Math.Max(1, (int)Math.Ceiling(RetryAfter(response).TotalSeconds))} seconds. Nothing was changed by the refused request."),
            (HttpStatusCode.RequestEntityTooLarge, _) or (_, "ErrorMessageSizeExceeded") => new(MailErrorKind.ServerRejected,
                $"Microsoft refused the message because it is too large: {detail}."),
            _ when status >= 500 => new(MailErrorKind.ServerRejected, $"Microsoft's service returned an error: {detail}. Try again later."),
            _ => new(MailErrorKind.ServerRejected, $"Microsoft refused the request: {detail}."),
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new MailOperationException(MailErrorKind.Protocol, "Microsoft returned a response HCG Mail could not read.", ex);
        }
    }

    /// <summary>Relative Graph paths, or absolute links Graph itself returned — only ever on graph.microsoft.com, so the token goes nowhere else.</summary>
    internal static string Url(string path)
    {
        if (!path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return BaseUrl + path;
        if (!path.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
            throw new MailOperationException(MailErrorKind.Protocol, "Refused to send the Microsoft sign-in token to a host other than graph.microsoft.com.");
        return path;
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
