using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace HCG.Mail.Email.Revocation;

public sealed record RevocationFallbackResult(CrlStatus Status, string Detail);

/// <summary>Second opinion on certificates whose revocation status the platform could not determine.</summary>
public interface IRevocationFallback
{
    /// <param name="chain">Validated chain, leaf first, root last.</param>
    /// <param name="elementStatus">Platform status per chain element, same order.</param>
    RevocationFallbackResult Verify(IReadOnlyList<X509Certificate2> chain, IReadOnlyList<X509ChainStatusFlags> elementStatus);
}

public interface ICrlFetcher
{
    /// <summary>Downloads a CRL, or returns null with a reason when it cannot be obtained.</summary>
    byte[]? Fetch(string url, out string? failure);
}

/// <summary>
/// macOS-only fallback (see docs/TLS_REVOCATION.md): .NET on macOS asks Apple's trust service for a
/// positive revocation answer, which it cannot give for certificates that publish only a CRL (no OCSP).
/// For each chain element the platform marked "revocation unknown" — never the root — this downloads the
/// certificate's CRL and verifies it with <see cref="CrlVerifier"/>. The result is Good only when every such
/// element is verified "not revoked"; any listing is Revoked; anything else stays Unknown (still blocked).
/// </summary>
public sealed class CrlRevocationFallback(ICrlFetcher fetcher, TimeProvider? time = null) : IRevocationFallback
{
    private static readonly Lazy<CrlRevocationFallback> SharedInstance = new(() => new CrlRevocationFallback(new HttpCrlFetcher()));
    private readonly ConcurrentDictionary<string, (byte[] Data, DateTimeOffset NextUpdate)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static CrlRevocationFallback Shared => SharedInstance.Value;

    public RevocationFallbackResult Verify(IReadOnlyList<X509Certificate2> chain, IReadOnlyList<X509ChainStatusFlags> elementStatus)
    {
        var now = _time.GetUtcNow();
        var problems = new List<string>();
        var verified = new List<string>();
        var checkedAny = false;

        for (var i = 0; i < chain.Count - 1; i++)
        {
            if ((elementStatus[i] & TlsValidationRecorder.RevocationUnknownFlags) == 0)
                continue; // the platform already completed the revocation check for this certificate
            checkedAny = true;
            var certificate = chain[i];
            var issuer = chain[i + 1];
            var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            var urls = TlsValidationRecorder.ReadCrlDistributionPoints(certificate)
                .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (urls.Count == 0)
            {
                problems.Add($"{name}: the certificate lists no CRL");
                continue;
            }

            CrlCheckResult? result = null;
            foreach (var url in urls)
            {
                var data = GetCrl(url, now, out var failure);
                if (data is null)
                {
                    problems.Add($"{name}: CRL {url} could not be downloaded ({failure})");
                    continue;
                }
                var check = CrlVerifier.Check(data, certificate, issuer, url, now);
                if (check.Status == CrlStatus.Unknown)
                {
                    _cache.TryRemove(url, out _);
                    problems.Add($"{name}: CRL {url} rejected — {check.Detail}");
                    continue;
                }
                _cache[url] = (data, check.NextUpdate ?? now);
                result = check;
                break;
            }

            if (result?.Status == CrlStatus.Revoked)
                return new RevocationFallbackResult(CrlStatus.Revoked, $"{name}: {result.Detail}");
            if (result?.Status == CrlStatus.Good)
                verified.Add($"{name}: {result.Detail}");
        }

        if (!checkedAny)
            return new RevocationFallbackResult(CrlStatus.Unknown, "no certificate needed or allowed a CRL check");
        return problems.Count == 0
            ? new RevocationFallbackResult(CrlStatus.Good, "verified by HCG Mail's CRL check — " + string.Join("; ", verified))
            : new RevocationFallbackResult(CrlStatus.Unknown, string.Join("; ", problems));
    }

    private byte[]? GetCrl(string url, DateTimeOffset now, out string? failure)
    {
        failure = null;
        if (_cache.TryGetValue(url, out var cached) && now < cached.NextUpdate)
            return cached.Data;
        return fetcher.Fetch(url, out failure);
    }
}

/// <summary>Downloads CRLs over HTTP(S): no cookies, limited redirects, size and time limits.</summary>
public sealed class HttpCrlFetcher : ICrlFetcher, IDisposable
{
    public const long MaxCrlBytes = 10 * 1024 * 1024;
    private readonly HttpClient _http;

    public HttpCrlFetcher(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HCGMail-CRL/1.0");
    }

    public byte[]? Fetch(string url, out string? failure)
    {
        failure = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failure = "not an HTTP address";
            return null;
        }
        try
        {
            // The TLS validation callback is synchronous, so this request is too (as is .NET's own revocation fetch).
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                failure = $"HTTP {(int)response.StatusCode}";
                return null;
            }
            if (response.Content.Headers.ContentLength > MaxCrlBytes)
            {
                failure = "the CRL is too large";
                return null;
            }
            using var stream = response.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxCrlBytes)
                {
                    failure = "the CRL is too large";
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or OperationCanceledException)
        {
            failure = ex is TaskCanceledException or OperationCanceledException ? "timed out" : ex.Message;
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
