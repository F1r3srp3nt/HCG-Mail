using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HCG.Mail.Core.Network;

namespace HCG.Mail.Email.Diagnostics;

/// <param name="Status">HTTP status code, or 0 when no response was received.</param>
public sealed record HttpProbeResult(ProbeOutcome Outcome, int Status, string? Location, string Body, TlsOutcome Tls, string? Certificate);

/// <summary>One HTTP(S) request without following redirects, used for the internet and Wi-Fi sign-in checks.</summary>
public interface IHttpProbe
{
    Task<HttpProbeResult> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Uses the system's proxy settings, like a browser would, with normal certificate validation: a certificate that does not
/// verify fails the request (the outcome is only recorded, never accepted). No cookies, no credentials, nothing but a GET.
/// </summary>
public sealed class SystemHttpProbe : IHttpProbe
{
    public async Task<HttpProbeResult> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var recorder = new TlsValidationRecorder();
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = timeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    recorder.Validate(sender, cert, chain, errors); // records what failed; the result decides
                    return errors == SslPolicyErrors.None;
                },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("HCG-Mail-Network-Doctor");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var body = await ReadPrefixAsync(response, cts.Token).ConfigureAwait(false);
            return new HttpProbeResult(ProbeOutcome.Ok, (int)response.StatusCode, response.Headers.Location?.ToString(), body,
                url.Scheme == Uri.UriSchemeHttps ? TlsOutcome.Ok : TlsOutcome.NotTested, recorder.Subject is null ? null : recorder.DescribeCertificate());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(ProbeOutcome.TimedOut, recorder);
        }
        catch (HttpRequestException ex)
        {
            if (recorder.HasFailure)
                return new HttpProbeResult(ProbeOutcome.Ok, 0, null, "", TlsOutcomes.FromRecorder(recorder), recorder.DescribeCertificate());
            if (ex.InnerException is AuthenticationException)
                return Failed(ProbeOutcome.Ok, recorder) with { Tls = TlsOutcome.HandshakeFailed };
            var outcome = ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? ProbeOutcome.Refused
                : ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut } ? ProbeOutcome.TimedOut
                : ProbeOutcome.Failed;
            return Failed(outcome, recorder);
        }
    }

    private static HttpProbeResult Failed(ProbeOutcome outcome, TlsValidationRecorder recorder) =>
        new(outcome, 0, null, "", TlsOutcome.NotTested, recorder.Subject is null ? null : recorder.DescribeCertificate());

    private static async Task<string> ReadPrefixAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[2048];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false)) > 0)
            read += n;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}

public static class TlsOutcomes
{
    /// <summary>The kind of certificate failure the recorder saw (validation is never overridden, only described).</summary>
    public static TlsOutcome FromRecorder(TlsValidationRecorder recorder)
    {
        if (!recorder.HasFailure)
            return TlsOutcome.Ok;
        if (recorder.IsValidApartFromRevocation)
            return TlsOutcome.RevocationUnknown;
        var errors = recorder.Errors!.Value;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            return TlsOutcome.NameMismatch;
        if (recorder.ChainStatus.HasFlag(X509ChainStatusFlags.Revoked))
            return TlsOutcome.Revoked;
        if (recorder.ChainStatus.HasFlag(X509ChainStatusFlags.NotTimeValid))
            return TlsOutcome.Expired;
        return TlsOutcome.Untrusted;
    }
}
