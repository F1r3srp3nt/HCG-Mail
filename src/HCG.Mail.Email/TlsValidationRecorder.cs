using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace HCG.Mail.Email;

/// <summary>
/// Strict server-certificate validation that also remembers why a certificate was rejected,
/// so the UI can explain the failure. It accepts a certificate only when the platform reports
/// no policy errors at all — exactly the default .NET/MailKit behaviour, never weaker.
/// </summary>
public sealed class TlsValidationRecorder
{
    public SslPolicyErrors? Errors { get; private set; }
    public X509ChainStatusFlags ChainStatus { get; private set; }
    public string? Subject { get; private set; }
    public string? Issuer { get; private set; }
    public DateTime? NotAfter { get; private set; }
    public IReadOnlyList<string> DnsNames { get; private set; } = [];

    public bool HasFailure => Errors is { } e && e != SslPolicyErrors.None;

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        Errors = sslPolicyErrors;
        ChainStatus = X509ChainStatusFlags.NoError;
        if (chain is not null)
        {
            foreach (var status in chain.ChainStatus)
                ChainStatus |= status.Status;
        }

        if (certificate is not null)
        {
            using var cert2 = new X509Certificate2(certificate);
            Subject = cert2.GetNameInfo(X509NameType.SimpleName, false);
            Issuer = cert2.GetNameInfo(X509NameType.SimpleName, true);
            NotAfter = cert2.NotAfter;
            DnsNames = cert2.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                .SelectMany(e => e.EnumerateDnsNames())
                .ToList();
        }

        return sslPolicyErrors == SslPolicyErrors.None;
    }

    public string DescribeCertificate()
    {
        var names = DnsNames.Count > 0 ? string.Join(", ", DnsNames.Take(4)) : Subject;
        var issuer = string.IsNullOrEmpty(Issuer) ? "" : $", issued by {Issuer}";
        var expiry = NotAfter is { } na ? $", valid until {na:yyyy-MM-dd}" : "";
        return $"{names}{issuer}{expiry}";
    }
}
