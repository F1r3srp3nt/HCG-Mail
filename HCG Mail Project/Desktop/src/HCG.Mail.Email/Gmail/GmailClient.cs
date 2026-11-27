using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Email.Gmail;

/// <summary>
/// Minimal Gmail API v1 REST client for one account. Adds the access token, refreshes it once on 401, retries reads
/// (never writes) after rate limiting, and turns Google errors into messages the user can act on. The token is only
/// ever sent to gmail.googleapis.com. Standard TLS validation with online revocation checking.
/// </summary>
public sealed class GmailClient
{
    public const string BaseUrl = "https://gmail.googleapis.com/gmail/v1/users/me";
    internal static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(10);
    private const int MaxReadRetries = 3;

    private static readonly Lazy<HttpMessageHandler> SharedHandler = new(() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 8,
        SslOptions = new SslClientAuthenticationOptions { CertificateRevocationCheckMode = X509RevocationMode.Online },
    });

    private readonly HttpClient _http;
    private readonly IGoogleAuth _auth;
    private readonly string _accountKey;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GmailClient(IGoogleAuth auth, string accountKey, ILogger logger, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
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

    /// <summary>A write. Not retried automatically unless <paramref name="idempotent"/> (setting labels to a value).</summary>
    public async Task<JsonDocument?> WriteAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool idempotent = false)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(method, Url(path))
        {
            Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        }, idempotent, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
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
                throw new MailOperationException(MailErrorKind.Timeout, "Google did not answer in time. Check the connection and try again.", ex)
                {
                    OutcomeUnknown = !idempotent,
                };
            }
            catch (HttpRequestException ex)
            {
                var notSent = ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError;
                throw new MailOperationException(MailErrorKind.Network,
                    notSent
                        ? "Could not reach Google (gmail.googleapis.com). Check the internet connection; the request was not sent."
                        : "The connection to Google failed while waiting for its reply. Check the connection before trying again.", ex)
                {
                    OutcomeUnknown = !notSent && !idempotent,
                };
            }

            if (response.IsSuccessStatusCode)
                return response;

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                response.Dispose();
                refreshed = true; // the access token may have been revoked or expired early: try one fresh token
                continue;
            }

            var error = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            if (IsRateLimit(response.StatusCode, error.Reason) && idempotent && attempt < MaxReadRetries)
            {
                var wait = RetryAfter(response, attempt);
                if (wait <= MaxRetryWait)
                {
                    response.Dispose();
                    _logger.LogInformation("Gmail API rate-limited a read; retrying after {Seconds} s", wait.TotalSeconds);
                    await _delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
            }

            using (response)
                throw ToException(response, error);
        }
    }

    internal readonly record struct GoogleError(string? Reason, string? Status, string? Message);

    private static async Task<GoogleError> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.Object)
                return default;
            string? reason = null;
            if (e.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                reason = details.EnumerateArray().Select(d => d.TryGetProperty("reason", out var r) ? r.GetString() : null).FirstOrDefault(r => r is not null);
            if (reason is null && e.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                reason = errors.EnumerateArray().Select(d => d.TryGetProperty("reason", out var r) ? r.GetString() : null).FirstOrDefault(r => r is not null);
            return new GoogleError(reason,
                e.TryGetProperty("status", out var s) ? s.GetString() : null,
                e.TryGetProperty("message", out var m) ? m.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return default;
        }
    }

    private static bool IsRateLimit(HttpStatusCode status, string? reason) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
        || (status == HttpStatusCode.Forbidden && reason is "rateLimitExceeded" or "userRateLimitExceeded" or "RATE_LIMIT_EXCEEDED");

    internal static TimeSpan RetryAfter(HttpResponseMessage response, int attempt)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1, 2, 4 s
    }

    internal static MailOperationException ToException(HttpResponseMessage response, GoogleError error)
    {
        var status = (int)response.StatusCode;
        var detail = string.IsNullOrWhiteSpace(error.Message) ? $"HTTP {status}" : $"{Trim(error.Message)} (HTTP {status})";
        return (response.StatusCode, error.Reason) switch
        {
            (HttpStatusCode.Unauthorized, _) => new(MailErrorKind.ReconnectRequired,
                "Google no longer accepts the sign-in for this account — it expired or access was removed. Reconnect the account to continue."),
            (HttpStatusCode.Forbidden, "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT") => new(MailErrorKind.ReconnectRequired,
                "HCG Mail does not have permission to use Gmail for this account (it was not allowed at sign-in, or was removed). Reconnect and allow access to Gmail."),
            (HttpStatusCode.Forbidden, "accessNotConfigured" or "SERVICE_DISABLED") => new(MailErrorKind.NotSupported,
                "The Gmail API is not enabled in HCG Mail's Google Cloud project. This is a registration problem — see docs/GOOGLE_ACCOUNTS.md."),
            (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests, "rateLimitExceeded" or "userRateLimitExceeded" or "RATE_LIMIT_EXCEEDED")
                or (HttpStatusCode.TooManyRequests, _) => new(MailErrorKind.Throttled,
                "Google is limiting requests for this mailbox. Try again in a minute. Nothing was changed by the refused request."),
            (HttpStatusCode.Forbidden, _) => new(MailErrorKind.Authentication, $"Google denied access to this mailbox: {detail}."),
            (HttpStatusCode.BadRequest, "failedPrecondition" or "FAILED_PRECONDITION") => new(MailErrorKind.NotSupported,
                $"This Google account cannot be used with Gmail ({detail}) — for example Gmail is turned off for it by its organisation."),
            (HttpStatusCode.NotFound, _) => new(MailErrorKind.MessageNotFound, "The message or label no longer exists in Gmail. Refresh the folder."),
            (HttpStatusCode.RequestEntityTooLarge, _) => new(MailErrorKind.ServerRejected, $"Google refused the message because it is too large: {detail}."),
            _ when status >= 500 => new(MailErrorKind.ServerRejected, $"Gmail returned an error: {detail}. Try again later."),
            _ => new(MailErrorKind.ServerRejected, $"Gmail refused the request: {detail}."),
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
            throw new MailOperationException(MailErrorKind.Protocol, "Gmail returned a response HCG Mail could not read.", ex);
        }
    }

    /// <summary>Relative API paths only, always on gmail.googleapis.com — the token is never sent anywhere else.</summary>
    internal static string Url(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
            throw new MailOperationException(MailErrorKind.Protocol, "Refused to send the Google sign-in token to a host other than gmail.googleapis.com.");
        return BaseUrl + path;
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
