using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HCG.Mail.Tests.Email;

/// <summary>Writes RFC 5280 CRLs with chosen contents, including deliberately invalid ones.</summary>
internal sealed record TestCrl
{
    public required X509Certificate2 Issuer { get; init; }
    public X509Certificate2? Signer { get; init; }
    public IReadOnlyList<X509Certificate2> Revoked { get; init; } = [];
    public DateTimeOffset ThisUpdate { get; init; } = DateTimeOffset.UtcNow.AddHours(-1);
    public DateTimeOffset? NextUpdate { get; init; } = DateTimeOffset.UtcNow.AddDays(7);
    public string? PartitionUrl { get; init; }
    public bool OnlySomeReasons { get; init; }
    public bool DeltaIndicator { get; init; }
    public string? UnknownCriticalExtension { get; init; }
    public byte[]? IssuerNameOverride { get; init; }

    public byte[] Build()
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteInteger(1);
            WriteAlgorithm(w);
            w.WriteEncodedValue(IssuerNameOverride ?? Issuer.SubjectName.RawData);
            w.WriteUtcTime(ThisUpdate);
            if (NextUpdate is { } next)
                w.WriteUtcTime(next);
            if (Revoked.Count > 0)
            {
                using (w.PushSequence())
                {
                    foreach (var cert in Revoked)
                    {
                        using (w.PushSequence())
                        {
                            w.WriteInteger(new BigInteger(cert.SerialNumberBytes.Span, isUnsigned: true, isBigEndian: true));
                            w.WriteUtcTime(ThisUpdate);
                        }
                    }
                }
            }
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (w.PushSequence())
            {
                WriteExtension(w, "2.5.29.20", false, x => x.WriteInteger(1));
                if (PartitionUrl is not null || OnlySomeReasons)
                {
                    WriteExtension(w, "2.5.29.28", true, x =>
                    {
                        using (x.PushSequence())
                        {
                            if (PartitionUrl is not null)
                            {
                                using (x.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                                using (x.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                                    x.WriteCharacterString(UniversalTagNumber.IA5String, PartitionUrl, new Asn1Tag(TagClass.ContextSpecific, 6));
                            }
                            x.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 1)); // onlyContainsUserCerts
                            if (OnlySomeReasons)
                                x.WriteBitString([0x40], 6, new Asn1Tag(TagClass.ContextSpecific, 3));
                        }
                    });
                }
                if (DeltaIndicator)
                    WriteExtension(w, "2.5.29.27", true, x => x.WriteInteger(1));
                if (UnknownCriticalExtension is not null)
                    WriteExtension(w, UnknownCriticalExtension, true, x => x.WriteNull());
            }
        }
        var tbs = w.Encode();
        using var key = (Signer ?? Issuer).GetRSAPrivateKey()!;
        var signature = key.SignData(tbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var outer = new AsnWriter(AsnEncodingRules.DER);
        using (outer.PushSequence())
        {
            outer.WriteEncodedValue(tbs);
            WriteAlgorithm(outer);
            outer.WriteBitString(signature);
        }
        return outer.Encode();
    }

    private static void WriteAlgorithm(AsnWriter w)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier("1.2.840.113549.1.1.11");
            w.WriteNull();
        }
    }

    private static void WriteExtension(AsnWriter w, string oid, bool critical, Action<AsnWriter> value)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            if (critical)
                w.WriteBoolean(true);
            var inner = new AsnWriter(AsnEncodingRules.DER);
            value(inner);
            w.WriteOctetString(inner.Encode());
        }
    }
}
