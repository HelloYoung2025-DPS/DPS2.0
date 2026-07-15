using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.PlatformAuthorizationAuthority.Contracts;

public static class PlatformAuthorizationEvidenceCanonicalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Canonicalize(SignedPlatformAuthorizationEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.ValidateUnsignedFields();
        using var stream = new MemoryStream();
        Append(stream, "DPS:PLATFORM-AUTHORIZATION-EVIDENCE:V1");
        Append(stream, evidence.SchemaVersion);
        Append(stream, evidence.ContractId);
        Append(stream, evidence.ProducerModule);
        Append(stream, evidence.SoulId);
        Append(stream, evidence.DeviceBindingId);
        Append(stream, evidence.PlatformAccountId);
        Append(stream, evidence.TraceId);
        Append(stream, evidence.IdempotencyKey);
        Append(stream, evidence.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        Append(stream, evidence.PrivacyClass);
        Append(stream, evidence.AuthorizationEvidenceId);
        Append(stream, evidence.Platform);
        Append(stream, evidence.AliasDigest);
        Append(stream, evidence.AliasKeyId);
        Append(stream, evidence.AliasKeyEpoch.ToString(CultureInfo.InvariantCulture));
        Append(stream, evidence.TargetStatus);
        Append(stream, evidence.AuthorizationRevision.ToString(CultureInfo.InvariantCulture));
        Append(stream, evidence.IssuerId);
        Append(stream, evidence.IssuerKeyId);
        Append(stream, evidence.ReleaseBomSha256);
        Append(stream, evidence.ReleaseGeneration.ToString(CultureInfo.InvariantCulture));
        Append(stream, evidence.IssuedAt.ToString("O", CultureInfo.InvariantCulture));
        Append(stream, evidence.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        var canonical = stream.ToArray();
        if (stream.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
        return canonical;
    }

    public static string ComputeSha256(SignedPlatformAuthorizationEvidenceV1 evidence)
    {
        var canonical = Canonicalize(evidence);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static void Append(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
