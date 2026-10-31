using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace HCG.Mail.Email;

/// <summary>One certificate in the chain the platform built, with the problems reported for it.</summary>
public sealed record ChainElementInfo(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, X509ChainStatusFlags Status, byte[] RawData);

/// <summary>
/// Strict server-certificate validation that also remembers why a certificate was rejected,
/// so the UI can explain the failure. It accepts a certificate only when the platform reports
/// no policy errors at all — exactly the default .NET/MailKit behaviour, never weaker. Everything
/// else recorded here is diagnostic evidence and never influences the accept/reject decision.
/// </summary>
public sealed class TlsValidationRecorder
{
    /// <summary>Chain problems that only mean "revocation status could not be determined".</summary>
    public const X509ChainStatusFlags RevocationUnknownFlags =
        X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    public SslPolicyErrors? Errors { get; private set; }
    public X509ChainStatusFlags ChainStatus { get; private set; }
    public string? Subject { get; private set; }
    public string? Issuer { get; private set; }
    public DateTime? NotAfter { get; private set; }
    public IReadOnlyList<string> DnsNames { get; private set; } = [];
    public IReadOnlyList<string> OcspUrls { get; private set; } = [];
    public IReadOnlyList<string> CrlUrls { get; private set; } = [];
    public IReadOnlyList<ChainElementInfo> ChainElements { get; private set; } = [];

    public bool HasFailure => Errors is { } e && e != SslPolicyErrors.None;

    /// <summary>
    /// True when the only problem is that revocation status could not be determined: the name matches,
    /// the chain reaches a trusted root, nothing is expired and nothing is known to be revoked.
    /// </summary>
    public bool IsValidApartFromRevocation =>
        Errors == SslPolicyErrors.RemoteCertificateChainErrors
        && ChainStatus != X509ChainStatusFlags.NoError
        && (ChainStatus & ~RevocationUnknownFlags) == X509ChainStatusFlags.NoError;

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        Errors = sslPolicyErrors;
        ChainStatus = X509ChainStatusFlags.NoError;
        ChainElements = [];
        if (chain is not null)
        {
            foreach (var status in chain.ChainStatus)
                ChainStatus |= status.Status;
            ChainElements = chain.ChainElements
                .Select(e => new ChainElementInfo(
                    e.Certificate.GetNameInfo(X509NameType.SimpleName, false),
                    e.Certificate.GetNameInfo(X509NameType.SimpleName, true),
                    e.Certificate.NotBefore,
                    e.Certificate.NotAfter,
                    e.ChainElementStatus.Aggregate(X509ChainStatusFlags.NoError, (acc, s) => acc | s.Status),
                    e.Certificate.RawData))
                .ToList();
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
            OcspUrls = cert2.Extensions.OfType<X509AuthorityInformationAccessExtension>()
                .SelectMany(e => e.EnumerateOcspUris())
                .ToList();
            CrlUrls = ReadCrlDistributionPoints(cert2);
        }

        // The decision: exactly the platform's verdict. Nothing above can relax it.
        return sslPolicyErrors == SslPolicyErrors.None;
    }

    public string DescribeCertificate()
    {
        var names = DnsNames.Count > 0 ? string.Join(", ", DnsNames.Take(4)) : Subject;
        var issuer = string.IsNullOrEmpty(Issuer) ? "" : $", issued by {Issuer}";
        var expiry = NotAfter is { } na ? $", valid until {na:yyyy-MM-dd}" : "";
        return $"{names}{issuer}{expiry}";
    }

    /// <summary>Reads URI distribution points from the CRL Distribution Points extension (2.5.29.31).</summary>
    internal static IReadOnlyList<string> ReadCrlDistributionPoints(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions["2.5.29.31"];
        if (extension is null)
            return [];
        var urls = new List<string>();
        try
        {
            var points = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                if (!point.HasData || point.PeekTag() != new Asn1Tag(TagClass.ContextSpecific, 0, true))
                    continue;
                var name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                if (!name.HasData || name.PeekTag() != new Asn1Tag(TagClass.ContextSpecific, 0, true))
                    continue;
                var generalNames = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                while (generalNames.HasData)
                {
                    var tag = generalNames.PeekTag();
                    if (tag == new Asn1Tag(TagClass.ContextSpecific, 6))
                        urls.Add(generalNames.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                    else
                        generalNames.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            // Malformed extension: report what was readable.
        }
        return urls;
    }
}
