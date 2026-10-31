using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;

namespace HCG.Mail.Email.Diagnostics;

/// <summary>
/// Read-only TLS diagnosis for one mail server (<c>HCGMail --diagnose-tls host:port</c>).
/// It performs the TLS handshake exactly as HCG Mail does, never authenticates and never sends
/// credentials or mail, then explains the certificate chain and the revocation check. The extra
/// chain builds with relaxed revocation settings are for comparison only; they are never used to
/// accept a connection.
/// </summary>
public sealed class TlsDiagnostics(IMailClientFactory clientFactory, HttpClient http)
{
    public async Task<bool> RunAsync(string host, int port, TextWriter output, CancellationToken cancellationToken = default)
    {
        var isSmtp = port is 465 or 587 or 25;
        var security = port is 143 or 587 or 25 ? MailSecurityMode.StartTls : MailSecurityMode.SslTls;
        var endpoint = new ServerEndpoint(host, port, security);
        var protocol = isSmtp ? MailProtocol.Smtp : MailProtocol.Imap;

        output.WriteLine($"HCG Mail TLS diagnosis — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        output.WriteLine($"Server:   {host}:{port} ({(isSmtp ? "SMTP" : "IMAP")}, {security})");
        output.WriteLine($"Platform: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}");
        output.WriteLine("No login is attempted and no credentials are used.");
        output.WriteLine();

        // 1. Exactly the connection HCG Mail makes (strict validation, online revocation checking).
        var recorder = new TlsValidationRecorder();
        var ok = false;
        var sw = Stopwatch.StartNew();
        IMailService client = isSmtp ? clientFactory.CreateSmtpClient(recorder) : clientFactory.CreateImapClient(recorder);
        try
        {
            await client.ConnectAsync(host, port, SecurityOptions.ToSecureSocketOptions(security), cancellationToken).ConfigureAwait(false);
            ok = client.IsSecure;
            output.WriteLine($"1. HCG Mail connection check: PASSED in {sw.ElapsedMilliseconds} ms ({(client as MailService)?.SslProtocol}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var translated = MailErrorTranslator.Translate(ex, protocol, endpoint, recorder);
            output.WriteLine($"1. HCG Mail connection check: FAILED after {sw.ElapsedMilliseconds} ms — {translated.Kind}");
            output.WriteLine($"   {translated.UserMessage}");
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
            client.Dispose();
        }
        output.WriteLine();

        if (recorder.Errors is null)
        {
            output.WriteLine("No certificate was received (the failure happened before or during the handshake). See the message above.");
            return ok;
        }

        // 2. Certificate chain as built by this platform.
        output.WriteLine($"2. Certificate chain (platform policy errors: {recorder.Errors}; chain status: {recorder.ChainStatus}):");
        for (var i = 0; i < recorder.ChainElements.Count; i++)
        {
            var e = recorder.ChainElements[i];
            output.WriteLine($"   [{i}] {e.Subject}");
            output.WriteLine($"       issued by {e.Issuer}; valid {e.NotBefore:yyyy-MM-dd} → {e.NotAfter:yyyy-MM-dd}; status: {(e.Status == X509ChainStatusFlags.NoError ? "OK" : e.Status.ToString())}");
        }
        output.WriteLine($"   Names on the server certificate: {string.Join(", ", recorder.DnsNames)}");
        output.WriteLine();

        // 3. Revocation information published by the server certificate.
        output.WriteLine("3. Revocation information in the server certificate:");
        output.WriteLine($"   OCSP responders: {(recorder.OcspUrls.Count == 0 ? "none" : string.Join(", ", recorder.OcspUrls))}");
        output.WriteLine($"   CRL locations:   {(recorder.CrlUrls.Count == 0 ? "none" : string.Join(", ", recorder.CrlUrls))}");
        output.WriteLine();

        // 4. Comparison chain builds on this platform (diagnosis only).
        if (recorder.ChainElements.Count > 0)
        {
            output.WriteLine("4. Chain rebuilt on this platform with different revocation settings (comparison only — HCG Mail always uses 'online'):");
            foreach (var mode in new[] { X509RevocationMode.Online, X509RevocationMode.Offline, X509RevocationMode.NoCheck })
                output.WriteLine($"   {mode,-8}: {BuildChain(recorder.ChainElements, mode)}");
            output.WriteLine();
        }

        // 5. Can this machine reach the revocation services?
        output.WriteLine("5. Reachability of revocation services from this machine:");
        foreach (var url in recorder.OcspUrls.Concat(recorder.CrlUrls).Distinct())
            output.WriteLine($"   {url} → {await ProbeAsync(url, cancellationToken).ConfigureAwait(false)}");
        if (recorder.OcspUrls.Count + recorder.CrlUrls.Count == 0)
            output.WriteLine("   (none published)");
        output.WriteLine();

        // 6. Interpretation.
        output.WriteLine("6. Interpretation:");
        if (ok)
            output.WriteLine("   The certificate passed every check, including revocation. HCG Mail can connect.");
        else if (recorder.IsValidApartFromRevocation)
        {
            output.WriteLine("   The certificate is valid (trusted issuer, matching name, not expired, not known to be revoked);");
            output.WriteLine("   only its revocation status could not be determined on this machine.");
            if (OperatingSystem.IsMacOS() && recorder.OcspUrls.Count == 0 && recorder.CrlUrls.Count > 0)
                output.WriteLine("   It publishes a CRL but no OCSP responder. macOS checks revocation through OCSP, which explains the result.");
        }
        else
            output.WriteLine("   The certificate failed a check other than revocation — see section 1.");
        return ok;
    }

    private static string BuildChain(IReadOnlyList<ChainElementInfo> elements, X509RevocationMode mode)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(elements[0].RawData);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = mode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
        foreach (var e in elements.Skip(1))
            chain.ChainPolicy.ExtraStore.Add(X509CertificateLoader.LoadCertificate(e.RawData));
        var valid = chain.Build(leaf);
        var status = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (acc, s) => acc | s.Status);
        return valid ? "valid" : $"NOT valid ({status})";
    }

    private async Task<string> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "not an HTTP address";
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return $"reachable (HTTP {(int)response.StatusCode}, {sw.ElapsedMilliseconds} ms)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"NOT reachable ({ex.GetType().Name}: {ex.Message})";
        }
    }
}
