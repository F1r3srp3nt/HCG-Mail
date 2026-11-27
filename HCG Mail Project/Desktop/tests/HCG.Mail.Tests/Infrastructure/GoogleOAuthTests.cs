using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Google;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Infrastructure;

/// <summary>
/// Google sign-in end to end without Google: a scripted "browser" follows the real loopback redirect, and a fake
/// token endpoint checks PKCE, the client and the grant. No real Google account or registration is involved.
/// </summary>
public sealed class GoogleOAuthTests
{
    private const string ClientId = "123456789012-testclientid.apps.googleusercontent.com";
    private const string Secret = "test-desktop-secret";

    private static string Jwt(object claims)
    {
        static string Part(object o) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(o)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part(new { alg = "RS256" })}.{Part(claims)}.c2lnbmF0dXJl";
    }

    private static object Claims(string sub, string email, string aud = ClientId) =>
        new { iss = "https://accounts.google.com", aud, sub, email, email_verified = true, name = "Test " + sub };

    /// <summary>A fake Google token endpoint. Remembers the PKCE challenge the browser saw and checks the verifier.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public string? Challenge;
        public string Sub = "111";
        public string Email = "ola@example.com";
        public string Scope = "openid email profile " + GoogleAuthOptions.GmailScope;
        public string? IdTokenAudience;
        public string? RefreshError;
        public bool FailNetwork;
        public int AccessTokenVersion;
        public List<Dictionary<string, string>> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (FailNetwork)
                throw new HttpRequestException("offline", null) { };
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            Requests.Add(form);
            Assert.Equal(ClientId, form["client_id"]);
            Assert.Equal(Secret, form["client_secret"]);
            if (form["grant_type"] == "authorization_code")
            {
                var expected = GoogleOAuth.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
                if (expected != Challenge || form["code"] != "the-code")
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
                return Json(HttpStatusCode.OK, new
                {
                    access_token = "access-" + Sub, expires_in = 3599, refresh_token = "refresh-" + Sub, scope = Scope, token_type = "Bearer",
                    id_token = Jwt(Claims(Sub, Email, IdTokenAudience ?? ClientId)),
                });
            }
            if (RefreshError is not null)
                return Json(HttpStatusCode.BadRequest, new { error = RefreshError });
            AccessTokenVersion++;
            return Json(HttpStatusCode.OK, new { access_token = $"access-{form["refresh_token"]}-v{AccessTokenVersion}", expires_in = 3599, token_type = "Bearer" });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private sealed class CapturingLogger : ILogger
    {
        public StringBuilder Text { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Text.AppendLine(formatter(state, exception) + exception);
    }

    /// <summary>A "browser": checks the authorization request and then visits the redirect like Google would.</summary>
    private static Func<Uri, Task> Browser(FakeGoogle google, Func<Dictionary<string, string>, string>? redirectQuery = null, bool visitFavicon = false) => async url =>
    {
        var q = url.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("https", url.Scheme);
        Assert.Equal("accounts.google.com", url.Host);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.Contains(GoogleAuthOptions.GmailScope, q["scope"].Split(' '));
        Assert.StartsWith("http://127.0.0.1:", q["redirect_uri"]);
        Assert.False(q.ContainsKey("client_secret")); // the secret never goes through the browser
        google.Challenge = q["code_challenge"];
        var redirect = q["redirect_uri"];
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            if (visitFavicon)
                Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(redirect + "favicon.ico")).StatusCode);
            var page = await http.GetStringAsync(redirect + "?" + (redirectQuery?.Invoke(q) ?? $"state={Uri.EscapeDataString(q["state"])}&code=the-code&scope=x"));
            Assert.Contains("HCG Mail", page);
        });
        await Task.CompletedTask;
    };

    private static GoogleOAuth Create(FakeGoogle google, SessionCredentialVault vault, Func<Uri, Task> browser, ILogger? logger = null, TimeSpan? timeout = null) =>
        new(ClientId, Secret, vault, logger ?? NullLogger.Instance, browser, handler: google, signInTimeout: timeout);

    [Fact]
    public async Task Sign_in_uses_PKCE_stores_the_refresh_token_per_account_and_refreshes()
    {
        var google = new FakeGoogle();
        var vault = new SessionCredentialVault();
        var logger = new CapturingLogger();
        var auth = Create(google, vault, Browser(google, visitFavicon: true), logger);

        var signIn = await auth.SignInAsync("ola@example.com", default);
        Assert.Equal(new GoogleSignIn("111", "ola@example.com", "Test 111"), signIn);
        Assert.Equal("refresh-111", await vault.GetAsync(CredentialKeys.ForGoogle("111")));

        Assert.Equal("access-111", await auth.GetAccessTokenAsync("111", false, default)); // cached, no request
        Assert.Single(google.Requests);
        Assert.Equal("access-refresh-111-v1", await auth.GetAccessTokenAsync("111", true, default));
        Assert.Equal("refresh_token", google.Requests[^1]["grant_type"]);

        var log = logger.Text.ToString();
        Assert.DoesNotContain("refresh-111", log);
        Assert.DoesNotContain("access-", log);
        Assert.DoesNotContain(Secret, log);
    }

    [Fact]
    public async Task A_response_for_another_sign_in_attempt_is_rejected_and_nothing_is_stored()
    {
        var google = new FakeGoogle();
        var vault = new SessionCredentialVault();
        var auth = Create(google, vault, Browser(google, _ => "state=forged&code=the-code"));
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => auth.SignInAsync(null, default));
        Assert.Equal(MailErrorKind.Authentication, ex.Kind);
        Assert.Empty(google.Requests);
        Assert.Null(await vault.GetAsync(CredentialKeys.ForGoogle("111")));
    }

    [Fact]
    public async Task Declining_in_the_browser_is_a_cancellation()
    {
        var google = new FakeGoogle();
        var auth = Create(google, new SessionCredentialVault(), Browser(google, q => $"state={Uri.EscapeDataString(q["state"])}&error=access_denied"));
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => auth.SignInAsync(null, default));
        Assert.Equal(MailErrorKind.Cancelled, ex.Kind);
    }

    [Fact]
    public async Task Cancelling_while_the_browser_is_open_stops_waiting()
    {
        var google = new FakeGoogle();
        var auth = Create(google, new SessionCredentialVault(), _ => Task.CompletedTask); // the user never finishes
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => auth.SignInAsync(null, cts.Token));
        Assert.Equal(MailErrorKind.Cancelled, ex.Kind);
        Assert.Contains("cancelled", ex.UserMessage);

        var timedOut = await Assert.ThrowsAsync<MailOperationException>(() =>
            Create(google, new SessionCredentialVault(), _ => Task.CompletedTask, timeout: TimeSpan.FromMilliseconds(200)).SignInAsync(null, default));
        Assert.Contains("within 5 minutes", timedOut.UserMessage);
    }

    [Fact]
    public async Task Unticking_Gmail_access_on_the_consent_screen_is_explained()
    {
        var google = new FakeGoogle { Scope = "openid email profile" };
        var vault = new SessionCredentialVault();
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => Create(google, vault, Browser(google)).SignInAsync(null, default));
        Assert.Equal(MailErrorKind.Authentication, ex.Kind);
        Assert.Contains("Read, compose, and send", ex.UserMessage);
        Assert.Null(await vault.GetAsync(CredentialKeys.ForGoogle("111")));
    }

    [Fact]
    public async Task An_identity_issued_for_another_client_is_refused()
    {
        var google = new FakeGoogle { IdTokenAudience = "999-other.apps.googleusercontent.com" };
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => Create(google, new SessionCredentialVault(), Browser(google)).SignInAsync(null, default));
        Assert.Equal(MailErrorKind.Authentication, ex.Kind);
    }

    [Fact]
    public async Task Revoked_or_expired_access_requires_reconnect_but_a_network_failure_does_not()
    {
        var google = new FakeGoogle();
        var vault = new SessionCredentialVault();
        await vault.SetAsync(CredentialKeys.ForGoogle("111"), "refresh-111");
        var auth = Create(google, vault, Browser(google));

        google.FailNetwork = true;
        Assert.Equal(MailErrorKind.Network, (await Assert.ThrowsAsync<MailOperationException>(() => auth.GetAccessTokenAsync("111", false, default))).Kind);

        google.FailNetwork = false;
        google.RefreshError = "invalid_grant";
        var revoked = await Assert.ThrowsAsync<MailOperationException>(() => auth.GetAccessTokenAsync("111", false, default));
        Assert.Equal(MailErrorKind.ReconnectRequired, revoked.Kind);
        Assert.Contains("7 days", revoked.UserMessage); // explains the testing-mode expiry

        google.RefreshError = "invalid_client";
        Assert.Equal(MailErrorKind.NotSupported, (await Assert.ThrowsAsync<MailOperationException>(() => auth.GetAccessTokenAsync("111", false, default))).Kind);

        Assert.Equal(MailErrorKind.ReconnectRequired, (await Assert.ThrowsAsync<MailOperationException>(() => auth.GetAccessTokenAsync("no-such-account", false, default))).Kind);
    }

    [Fact]
    public async Task Accounts_are_isolated_and_signing_out_one_keeps_the_other()
    {
        var google = new FakeGoogle();
        var vault = new SessionCredentialVault();
        var auth = Create(google, vault, Browser(google));
        await auth.SignInAsync(null, default);
        google.Sub = "222";
        google.Email = "kari@example.com";
        await auth.SignInAsync(null, default);

        Assert.Equal("refresh-111", await vault.GetAsync(CredentialKeys.ForGoogle("111")));
        Assert.Equal("refresh-222", await vault.GetAsync(CredentialKeys.ForGoogle("222")));
        Assert.Equal("access-111", await auth.GetAccessTokenAsync("111", false, default));
        Assert.Equal("access-222", await auth.GetAccessTokenAsync("222", false, default));

        await auth.SignOutAsync("111");
        Assert.Null(await vault.GetAsync(CredentialKeys.ForGoogle("111")));
        Assert.Equal(MailErrorKind.ReconnectRequired, (await Assert.ThrowsAsync<MailOperationException>(() => auth.GetAccessTokenAsync("111", false, default))).Kind);
        Assert.Equal("access-222", await auth.GetAccessTokenAsync("222", false, default));
    }

    [Fact]
    public void Configuration_is_validated_and_never_invented()
    {
        Assert.True(GoogleAuthOptions.IsValidClientId(ClientId));
        Assert.False(GoogleAuthOptions.IsValidClientId("not-a-client"));
        Assert.False(GoogleAuthOptions.IsValidClientId(null));
        Assert.Throws<ArgumentException>(() => new GoogleOAuth(ClientId, "", new SessionCredentialVault(), NullLogger.Instance));
        var previous = Environment.GetEnvironmentVariable(GoogleAuthOptions.ClientIdVariable);
        try
        {
            Environment.SetEnvironmentVariable(GoogleAuthOptions.ClientIdVariable, null);
            Assert.Null(GoogleAuthOptions.ResolveClientId(typeof(GoogleOAuthTests).Assembly));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoogleAuthOptions.ClientIdVariable, previous);
        }
        Assert.Equal(["openid", "email", "profile", "https://www.googleapis.com/auth/gmail.modify"], GoogleAuthOptions.Scopes);
    }
}
