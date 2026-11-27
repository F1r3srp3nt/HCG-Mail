using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Security;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Google;

/// <summary>
/// Google sign-in for Gmail on the desktop: OAuth 2.0 authorization code flow with PKCE (S256) and a <c>state</c>
/// check, in the system browser, with a loopback redirect (http://127.0.0.1:&lt;port&gt;/) — Google's "Desktop app"
/// client type. HCG Mail never sees the Google password. The refresh token is stored per Google account in the
/// platform credential store (Windows Credential Manager, macOS Keychain); access tokens are kept in memory only and
/// are never logged.
/// </summary>
public sealed class GoogleOAuth : IGoogleAuth
{
    private static readonly Lazy<HttpMessageHandler> SharedHandler = new(() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        SslOptions = new SslClientAuthenticationOptions { CertificateRevocationCheckMode = X509RevocationMode.Online },
    });

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly ICredentialVault _vault;
    private readonly ILogger _logger;
    private readonly Func<Uri, Task> _openBrowser;
    private readonly GoogleEndpoints _endpoints;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly TimeSpan _signInTimeout;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _accessTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    public GoogleOAuth(string clientId, string clientSecret, ICredentialVault vault, ILogger logger,
        Func<Uri, Task>? openBrowser = null, GoogleEndpoints? endpoints = null, HttpMessageHandler? handler = null,
        TimeProvider? time = null, TimeSpan? signInTimeout = null)
    {
        if (!GoogleAuthOptions.IsValidClientId(clientId))
            throw new ArgumentException("Not a Google OAuth client ID.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("The Google desktop client secret is missing.", nameof(clientSecret));
        _clientId = clientId;
        _clientSecret = clientSecret;
        _vault = vault;
        _logger = logger;
        _openBrowser = openBrowser ?? OpenSystemBrowser;
        _endpoints = endpoints ?? GoogleEndpoints.Production;
        _http = new HttpClient(handler ?? SharedHandler.Value, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HCGMail", "0.1"));
        _time = time ?? TimeProvider.System;
        _signInTimeout = signInTimeout ?? TimeSpan.FromMinutes(5);
    }

    public bool IsConfigured => true;
    public string? ConfigurationProblem => null;
    public IReadOnlyList<string> Scopes => GoogleAuthOptions.Scopes;

    public async Task<GoogleSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken)
    {
        var verifier = RandomUrlSafe(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomUrlSafe(24);

        using var listener = LoopbackRedirectListener.Start();
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["redirect_uri"] = listener.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', GoogleAuthOptions.Scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",  // a refresh token, so the user does not sign in every hour
            ["prompt"] = "select_account consent", // consent: Google issues a refresh token every time
        };
        if (!string.IsNullOrWhiteSpace(loginHint))
            query["login_hint"] = loginHint;
        var authorizeUrl = new Uri(_endpoints.Authorization + "?" + string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_signInTimeout);
        IReadOnlyDictionary<string, string> result;
        try
        {
            var waiting = listener.WaitForRedirectAsync(ResponsePage, timeout.Token);
            await _openBrowser(authorizeUrl).ConfigureAwait(false);
            result = await waiting.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new MailOperationException(MailErrorKind.Cancelled, cancellationToken.IsCancellationRequested
                ? "Google sign-in was cancelled. No account was added."
                : "Google sign-in was not completed within 5 minutes. Try again.");
        }

        if (!result.TryGetValue("state", out var returnedState)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returnedState), Encoding.ASCII.GetBytes(state)))
            throw new MailOperationException(MailErrorKind.Authentication, "The sign-in response did not belong to this sign-in attempt and was ignored. Try again.");
        if (result.TryGetValue("error", out var error))
        {
            throw error == "access_denied"
                ? new MailOperationException(MailErrorKind.Cancelled, "Google sign-in was cancelled or access was not allowed. No account was added.")
                : new MailOperationException(MailErrorKind.Authentication, $"Google sign-in failed ({error}). Try again.");
        }
        if (!result.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            throw new MailOperationException(MailErrorKind.Protocol, "Google did not return a sign-in code. Try again.");

        using var tokens = await PostTokenAsync(new()
        {
            ["code"] = code,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"] = listener.RedirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        }, interactive: true, cancellationToken).ConfigureAwait(false);
        var root = tokens.RootElement;

        // Google lets the user untick individual permissions on the consent screen.
        var granted = (Str(root, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!granted.Contains(GoogleAuthOptions.GmailScope, StringComparer.Ordinal))
        {
            throw new MailOperationException(MailErrorKind.Authentication,
                "HCG Mail was not given access to Gmail. Sign in again and leave “Read, compose, and send emails from your Gmail account” " +
                "(view and modify but not delete your email) ticked — HCG Mail cannot show or send mail without it.");
        }
        var refreshToken = Str(root, "refresh_token")
                           ?? throw new MailOperationException(MailErrorKind.Protocol, "Google did not issue a long-term sign-in (refresh token). Try again.");
        var accessToken = Str(root, "access_token")
                          ?? throw new MailOperationException(MailErrorKind.Protocol, "Google did not issue an access token. Try again.");
        var identity = ReadIdToken(Str(root, "id_token"));

        await _vault.SetAsync(CredentialKeys.ForGoogle(identity.Sub), refreshToken, cancellationToken).ConfigureAwait(false);
        _accessTokens[identity.Sub] = (accessToken, Expiry(root));
        _logger.LogInformation("Google sign-in completed; token stored in {Store}", _vault.StorageDescription);
        return new GoogleSignIn(identity.Sub, identity.Email, identity.Name);
    }

    public async Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _accessTokens.TryGetValue(accountKey, out var cached) && cached.Expires > _time.GetUtcNow().AddMinutes(1))
            return cached.Token;

        var gate = _refreshLocks.GetOrAdd(accountKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _accessTokens.TryGetValue(accountKey, out cached) && cached.Expires > _time.GetUtcNow().AddMinutes(1))
                return cached.Token; // another caller refreshed while we waited

            string? refreshToken;
            try
            {
                refreshToken = await _vault.GetAsync(CredentialKeys.ForGoogle(accountKey), cancellationToken).ConfigureAwait(false);
            }
            catch (CredentialVaultException ex)
            {
                throw new MailOperationException(MailErrorKind.Authentication, ex.Message, ex);
            }
            if (string.IsNullOrEmpty(refreshToken))
                throw new MailOperationException(MailErrorKind.ReconnectRequired,
                    "HCG Mail has no Google sign-in stored for this account on this device. Reconnect the account.");

            using var tokens = await PostTokenAsync(new()
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }, interactive: false, cancellationToken).ConfigureAwait(false);
            var token = Str(tokens.RootElement, "access_token")
                        ?? throw new MailOperationException(MailErrorKind.Protocol, "Google did not issue an access token.");
            _accessTokens[accountKey] = (token, Expiry(tokens.RootElement));
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deletes this device's stored token for the account. Access is not revoked at Google, because that would also
    /// sign HCG Mail out on the user's other devices; the account screen explains where to revoke it.
    /// </summary>
    public async Task SignOutAsync(string accountKey)
    {
        _accessTokens.TryRemove(accountKey, out _);
        await _vault.DeleteAsync(CredentialKeys.ForGoogle(accountKey)).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, bool interactive, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_endpoints.Token, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MailOperationException(MailErrorKind.Timeout, "Google did not answer in time. Check the internet connection and try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MailOperationException(MailErrorKind.Network, "Could not reach Google (oauth2.googleapis.com). Check the internet connection.", ex);
        }

        using (response)
        {
            JsonDocument? json = null;
            try
            {
                json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            catch (JsonException)
            {
            }
            if (response.IsSuccessStatusCode && json is not null)
                return json;

            var error = json is null ? null : Str(json.RootElement, "error");
            json?.Dispose();
            throw (error, interactive) switch
            {
                ("invalid_grant", false) => new MailOperationException(MailErrorKind.ReconnectRequired,
                    "Google no longer accepts the sign-in for this account: it was revoked, the password changed, or it expired " +
                    "(while HCG Mail's Google app is in testing, Google ends sign-ins after 7 days). Reconnect the account."),
                ("invalid_grant", true) => new MailOperationException(MailErrorKind.Authentication,
                    "Google rejected the sign-in code (it may have expired or been used already). Try again."),
                ("invalid_client" or "unauthorized_client", _) => new MailOperationException(MailErrorKind.NotSupported,
                    "HCG Mail's Google registration was refused (unknown client or wrong client type/secret). See docs/GOOGLE_ACCOUNTS.md."),
                _ when (int)response.StatusCode >= 500 => new MailOperationException(MailErrorKind.Network,
                    $"Google's sign-in service returned an error (HTTP {(int)response.StatusCode}). Try again later."),
                _ => new MailOperationException(interactive ? MailErrorKind.Authentication : MailErrorKind.ReconnectRequired,
                    $"Google sign-in failed ({error ?? "HTTP " + (int)response.StatusCode}). {(interactive ? "Try again." : "Reconnect the account.")}"),
            };
        }
    }

    private DateTimeOffset Expiry(JsonElement root) =>
        _time.GetUtcNow().AddSeconds(root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3000);

    /// <summary>
    /// The ID token comes directly from Google's token endpoint over a verified TLS connection, so (per OpenID Connect
    /// Core 3.1.3.7) its signature need not be checked; issuer, audience and the verified address are.
    /// </summary>
    internal (string Sub, string Email, string? Name) ReadIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 })
            throw new MailOperationException(MailErrorKind.Protocol, "Google did not say which account signed in. Try again.");
        using var claims = JsonDocument.Parse(FromBase64Url(parts[1]));
        var c = claims.RootElement;
        var issuer = Str(c, "iss");
        var audience = Str(c, "aud");
        if (issuer is not ("https://accounts.google.com" or "accounts.google.com") || audience != _clientId)
            throw new MailOperationException(MailErrorKind.Authentication, "The sign-in response was not issued by Google for HCG Mail and was ignored.");
        var sub = Str(c, "sub");
        var email = Str(c, "email");
        var verified = c.TryGetProperty("email_verified", out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email) || !verified)
            throw new MailOperationException(MailErrorKind.Authentication, "Google did not confirm the account's e-mail address.");
        return (sub, email, Str(c, "name"));
    }

    private static string ResponsePage(IReadOnlyDictionary<string, string> query) =>
        query.ContainsKey("code")
            ? "<!doctype html><html><head><title>HCG Mail</title></head><body style='font-family:sans-serif'><h2>Signed in</h2><p>You can close this tab and return to HCG Mail.</p></body></html>"
            : "<!doctype html><html><head><title>HCG Mail</title></head><body style='font-family:sans-serif'><h2>Sign-in did not complete</h2><p>Return to HCG Mail for details.</p></body></html>";

    /// <summary>Opens the URL in the user's default browser.</summary>
    public static Task OpenSystemBrowser(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only https URLs are opened.", nameof(url));
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open") { ArgumentList = { url.AbsoluteUri }, UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url.AbsoluteUri }, UseShellExecute = false });
        return Task.CompletedTask;
    }

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
