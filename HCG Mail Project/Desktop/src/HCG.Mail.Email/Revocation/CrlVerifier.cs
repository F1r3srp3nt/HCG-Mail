using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HCG.Mail.Email.Revocation;

public enum CrlStatus
{
    /// <summary>A valid, current, correctly scoped CRL from the certificate's issuer does not list it.</summary>
    Good,

    /// <summary>A valid CRL from the certificate's issuer lists the certificate.</summary>
    Revoked,

    /// <summary>No trustworthy answer: the CRL could not be obtained or did not pass every check.</summary>
    Unknown,
}

public sealed record CrlCheckResult(CrlStatus Status, string Detail, DateTimeOffset? NextUpdate = null)
{
    public static CrlCheckResult Unknown(string detail) => new(CrlStatus.Unknown, detail);
}

/// <summary>
/// Verifies an X.509 CRL (RFC 5280) for one certificate. Fails closed: anything not fully understood
/// or not fully verified yields <see cref="CrlStatus.Unknown"/>, never <see cref="CrlStatus.Good"/>.
/// Checks: DER structure, matching inner/outer signature algorithm, signature by the issuer's key,
/// issuer name, issuer key usage (cRLSign), thisUpdate/nextUpdate freshness, Issuing Distribution Point
/// scope (partition URL, user/CA scope), and rejects delta, indirect and reason-limited CRLs and
/// unknown critical extensions.
/// </summary>
public static class CrlVerifier
{
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private const string OidCrlNumber = "2.5.29.20";
    private const string OidReasonCode = "2.5.29.21";
    private const string OidInvalidityDate = "2.5.29.24";
    private const string OidDeltaCrlIndicator = "2.5.29.27";
    private const string OidIssuingDistributionPoint = "2.5.29.28";
    private const string OidCertificateIssuer = "2.5.29.29";
    private const string OidAuthorityKeyIdentifier = "2.5.29.35";
    private const string OidFreshestCrl = "2.5.29.46";

    private static readonly Asn1Tag Context0Constructed = new(TagClass.ContextSpecific, 0, true);

    public static CrlCheckResult Check(byte[] crlData, X509Certificate2 certificate, X509Certificate2 issuer, string distributionPointUrl, DateTimeOffset now)
    {
        try
        {
            return CheckCore(crlData, certificate, issuer, distributionPointUrl, now);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException or FormatException or InvalidOperationException or OverflowException)
        {
            return CrlCheckResult.Unknown($"the CRL could not be read ({ex.GetType().Name})");
        }
    }

    private static CrlCheckResult CheckCore(byte[] crlData, X509Certificate2 certificate, X509Certificate2 issuer, string distributionPointUrl, DateTimeOffset now)
    {
        // A certificate that announces delta CRLs cannot be fully checked with the base CRL alone.
        if (certificate.Extensions[OidFreshestCrl] is not null)
            return CrlCheckResult.Unknown("the certificate requires delta CRLs, which HCG Mail does not support");
        if (!issuer.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            return CrlCheckResult.Unknown("the issuing certificate does not match the certificate's issuer");
        if (issuer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } keyUsage && !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign))
            return CrlCheckResult.Unknown("the issuer is not permitted to sign CRLs");

        var der = DecodePem(crlData);
        var outer = new AsnReader(der, AsnEncodingRules.DER);
        var certificateList = outer.ReadSequence();
        outer.ThrowIfNotEmpty();
        var tbsBytes = certificateList.ReadEncodedValue().ToArray();
        var signatureAlgorithm = ReadAlgorithm(certificateList);
        var signature = certificateList.ReadBitString(out var unusedBits);
        certificateList.ThrowIfNotEmpty();
        if (unusedBits != 0)
            return CrlCheckResult.Unknown("the CRL signature is malformed");

        // Verify the signature before trusting anything inside the CRL.
        if (!VerifySignature(tbsBytes, signature, signatureAlgorithm.Oid, issuer))
            return CrlCheckResult.Unknown("the CRL signature does not verify against the issuing certificate");

        var tbs = new AsnReader(tbsBytes, AsnEncodingRules.DER).ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
        {
            if (tbs.ReadInteger() != BigInteger.One)
                return CrlCheckResult.Unknown("unsupported CRL version");
        }
        var innerAlgorithm = ReadAlgorithm(tbs);
        if (innerAlgorithm != signatureAlgorithm)
            return CrlCheckResult.Unknown("the CRL's signature algorithm fields disagree");
        var issuerName = tbs.ReadEncodedValue();
        if (!issuerName.Span.SequenceEqual(certificate.IssuerName.RawData))
            return CrlCheckResult.Unknown("the CRL was issued by a different authority");

        var thisUpdate = ReadTime(tbs);
        DateTimeOffset? nextUpdate = null;
        if (tbs.HasData && IsTime(tbs.PeekTag()))
            nextUpdate = ReadTime(tbs);
        if (nextUpdate is null)
            return CrlCheckResult.Unknown("the CRL has no nextUpdate time");
        if (thisUpdate > now + ClockSkew)
            return CrlCheckResult.Unknown($"the CRL is not valid yet (thisUpdate {thisUpdate:u})");
        if (now > nextUpdate.Value + ClockSkew)
            return CrlCheckResult.Unknown($"the CRL is out of date (nextUpdate {nextUpdate:u})");

        var serial = TrimLeadingZeros(certificate.SerialNumberBytes.Span);
        var listed = false;
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            var revoked = tbs.ReadSequence();
            while (revoked.HasData)
            {
                var entry = revoked.ReadSequence();
                var entrySerial = entry.ReadIntegerBytes();
                ReadTime(entry);
                if (entry.HasData)
                {
                    var extensions = entry.ReadSequence();
                    while (extensions.HasData)
                    {
                        var (oid, critical, _) = ReadExtension(extensions);
                        if (oid == OidCertificateIssuer)
                            return CrlCheckResult.Unknown("indirect CRLs are not supported");
                        if (critical && oid is not (OidReasonCode or OidInvalidityDate))
                            return CrlCheckResult.Unknown($"the CRL entry has an unsupported critical extension ({oid})");
                    }
                }
                entry.ThrowIfNotEmpty();
                if (TrimLeadingZeros(entrySerial.Span).SequenceEqual(serial))
                    listed = true;
            }
        }

        if (tbs.HasData)
        {
            var wrapper = tbs.ReadSequence(Context0Constructed);
            var extensions = wrapper.ReadSequence();
            wrapper.ThrowIfNotEmpty();
            while (extensions.HasData)
            {
                var (oid, critical, value) = ReadExtension(extensions);
                switch (oid)
                {
                    case OidIssuingDistributionPoint:
                        if (CheckIssuingDistributionPoint(value, certificate, distributionPointUrl) is { } problem)
                            return CrlCheckResult.Unknown(problem);
                        break;
                    case OidDeltaCrlIndicator:
                        return CrlCheckResult.Unknown("this is a delta CRL, not a complete CRL");
                    case OidFreshestCrl:
                        return CrlCheckResult.Unknown("the CRL requires delta CRLs, which HCG Mail does not support");
                    case OidCrlNumber:
                    case OidAuthorityKeyIdentifier:
                        break;
                    default:
                        if (critical)
                            return CrlCheckResult.Unknown($"the CRL has an unsupported critical extension ({oid})");
                        break;
                }
            }
        }
        tbs.ThrowIfNotEmpty();

        return listed
            ? new CrlCheckResult(CrlStatus.Revoked, "the certificate is listed as revoked in its issuer's CRL", nextUpdate)
            : new CrlCheckResult(CrlStatus.Good, $"not listed in the CRL at {distributionPointUrl} (valid until {nextUpdate:u})", nextUpdate);
    }

    /// <summary>Returns a problem description, or null when the CRL's scope covers the certificate.</summary>
    private static string? CheckIssuingDistributionPoint(byte[] value, X509Certificate2 certificate, string distributionPointUrl)
    {
        var idp = new AsnReader(value, AsnEncodingRules.DER).ReadSequence();
        var hasNames = false;
        var matched = false;
        if (idp.HasData && idp.PeekTag() == Context0Constructed)
        {
            var name = idp.ReadSequence(Context0Constructed);
            if (name.PeekTag() != Context0Constructed)
                return "the CRL uses an unsupported distribution point name form";
            var generalNames = name.ReadSequence(Context0Constructed);
            name.ThrowIfNotEmpty();
            while (generalNames.HasData)
            {
                var tag = generalNames.PeekTag();
                if (tag == new Asn1Tag(TagClass.ContextSpecific, 6))
                {
                    hasNames = true;
                    var url = generalNames.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                    if (string.Equals(url.Trim(), distributionPointUrl.Trim(), StringComparison.OrdinalIgnoreCase))
                        matched = true;
                }
                else
                {
                    hasNames = true;
                    generalNames.ReadEncodedValue();
                }
            }
        }

        var onlyUserCerts = ReadOptionalBoolean(idp, 1);
        var onlyCaCerts = ReadOptionalBoolean(idp, 2);
        if (idp.HasData && idp.PeekTag() == new Asn1Tag(TagClass.ContextSpecific, 3))
            return "the CRL only covers some revocation reasons";
        if (ReadOptionalBoolean(idp, 4))
            return "indirect CRLs are not supported";
        if (ReadOptionalBoolean(idp, 5))
            return "the CRL only covers attribute certificates";
        idp.ThrowIfNotEmpty();

        // Partitioned CRLs (e.g. Let's Encrypt): the CRL must be the partition the certificate points to.
        // Otherwise a genuinely signed CRL for another partition, which does not list this certificate,
        // could be substituted on the unencrypted HTTP download.
        if (hasNames && !matched)
            return "the CRL is a different partition than the one the certificate points to";

        var isCa = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority == true;
        if (onlyUserCerts && isCa)
            return "the CRL only covers end-entity certificates";
        if (onlyCaCerts && !isCa)
            return "the CRL only covers CA certificates";
        return null;
    }

    private static bool ReadOptionalBoolean(AsnReader reader, int tagNumber)
    {
        var tag = new Asn1Tag(TagClass.ContextSpecific, tagNumber);
        return reader.HasData && reader.PeekTag() == tag && reader.ReadBoolean(tag);
    }

    private static bool VerifySignature(byte[] data, byte[] signature, string algorithmOid, X509Certificate2 issuer)
    {
        (HashAlgorithmName Hash, bool Rsa)? algorithm = algorithmOid switch
        {
            "1.2.840.113549.1.1.11" => (HashAlgorithmName.SHA256, true),
            "1.2.840.113549.1.1.12" => (HashAlgorithmName.SHA384, true),
            "1.2.840.113549.1.1.13" => (HashAlgorithmName.SHA512, true),
            "1.2.840.10045.4.3.2" => (HashAlgorithmName.SHA256, false),
            "1.2.840.10045.4.3.3" => (HashAlgorithmName.SHA384, false),
            "1.2.840.10045.4.3.4" => (HashAlgorithmName.SHA512, false),
            _ => null,
        };
        if (algorithm is not { } a)
            return false;
        if (a.Rsa)
        {
            using var rsa = issuer.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(data, signature, a.Hash, RSASignaturePadding.Pkcs1);
        }
        using var ecdsa = issuer.GetECDsaPublicKey();
        return ecdsa is not null && ecdsa.VerifyData(data, signature, a.Hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    private sealed record Algorithm(string Oid, string Parameters);

    private static Algorithm ReadAlgorithm(AsnReader reader)
    {
        var sequence = reader.ReadSequence();
        var oid = sequence.ReadObjectIdentifier();
        var parameters = sequence.HasData ? Convert.ToHexString(sequence.ReadEncodedValue().Span) : "";
        sequence.ThrowIfNotEmpty();
        return new Algorithm(oid, parameters);
    }

    private static (string Oid, bool Critical, byte[] Value) ReadExtension(AsnReader reader)
    {
        var extension = reader.ReadSequence();
        var oid = extension.ReadObjectIdentifier();
        var critical = extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean) && extension.ReadBoolean();
        var value = extension.ReadOctetString();
        extension.ThrowIfNotEmpty();
        return (oid, critical, value);
    }

    private static bool IsTime(Asn1Tag tag) =>
        tag.HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UtcTime)) || tag.HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.GeneralizedTime));

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UtcTime)) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    private static ReadOnlySpan<byte> TrimLeadingZeros(ReadOnlySpan<byte> value)
    {
        var i = 0;
        while (i < value.Length - 1 && value[i] == 0)
            i++;
        return value[i..];
    }

    private static byte[] DecodePem(byte[] data)
    {
        const string header = "-----BEGIN X509 CRL-----";
        if (data.Length < header.Length || data[0] != (byte)'-')
            return data;
        var text = Encoding.ASCII.GetString(data);
        var fields = PemEncoding.Find(text);
        if (!text[fields.Label].Equals("X509 CRL", StringComparison.Ordinal))
            throw new FormatException("Not a CRL.");
        return Convert.FromBase64String(text[fields.Base64Data]);
    }
}
