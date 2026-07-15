using System.Security.Cryptography;
using Dps.PlatformAccountRegistry;
using Dps.PlatformAccountRegistry.Contracts;
using Dps.PlatformAuthorizationAuthority.Contracts;

namespace Dps.Binding.Tests;

internal sealed class BindingPlatformAuthorizationEvidenceFactory : IDisposable
{
    private const string ExternalSignerFileEnvironment = "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE";
    internal static readonly string ReleaseBomSha256 = new('c', 64);
    internal const long ReleaseGeneration = 11;
    private readonly ECDsa _signer;

    private BindingPlatformAuthorizationEvidenceFactory(ECDsa signer)
        => _signer = signer;

    internal static BindingPlatformAuthorizationEvidenceFactory LoadExternal()
    {
        var path = Environment.GetEnvironmentVariable(ExternalSignerFileEnvironment);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"{ExternalSignerFileEnvironment} is required for binding Integration tests; " +
                "the independently supplied pinned-root signer is external infrastructure, not a skip or repository fixture.");
        }

        var privateKey = ReadExternalPrivateKeyFile(path);
        if (privateKey.Length is < 32 or > 4096)
        {
            CryptographicOperations.ZeroMemory(privateKey);
            throw new InvalidOperationException("The external platform-authorization signer payload has an invalid bounded size.");
        }

        try
        {
            var signer = ECDsa.Create();
            try
            {
                signer.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
                if (bytesRead != privateKey.Length || signer.KeySize != 256)
                    throw new InvalidOperationException("The external platform-authorization signer is not an exact P-256 PKCS#8 key.");
                var spki = signer.ExportSubjectPublicKeyInfo();
                try
                {
                    var actualPin = SHA256.HashData(spki);
                    var expectedPin = Convert.FromHexString(PlatformAuthorizationAuthorityTrustMetadata.PinnedRootSpkiSha256);
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(actualPin, expectedPin))
                            throw new InvalidOperationException("The external platform-authorization signer does not match the compiled trust-root pin.");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(actualPin);
                        CryptographicOperations.ZeroMemory(expectedPin);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(spki);
                }
                return new BindingPlatformAuthorizationEvidenceFactory(signer);
            }
            catch
            {
                signer.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    internal AuthorizePlatformAccountCommand Authorize(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string platform,
        string aliasDigest,
        string aliasKeyId,
        long aliasKeyEpoch,
        string evidenceLabel,
        string idempotencyLabel,
        DateTimeOffset occurredAt)
    {
        var traceId = Trace(idempotencyLabel);
        var idempotencyKey = Idempotency(idempotencyLabel);
        var evidence = Sign(
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            EvidenceId(evidenceLabel),
            platform,
            aliasDigest,
            aliasKeyId,
            aliasKeyEpoch,
            "authorized",
            1);
        return new AuthorizePlatformAccountCommand(
            soulId,
            deviceBindingId,
            platformAccountId,
            platform,
            aliasDigest,
            aliasKeyId,
            aliasKeyEpoch,
            evidence,
            traceId,
            idempotencyKey,
            occurredAt);
    }

    internal ChangePlatformAccountStatusCommand Status(
        PlatformAccountAuthorizedV1 current,
        string status,
        string evidenceLabel,
        string idempotencyLabel,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        var traceId = Trace(idempotencyLabel);
        var idempotencyKey = Idempotency(idempotencyLabel);
        var evidence = Sign(
            current.SoulId,
            current.DeviceBindingId,
            current.PlatformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            EvidenceId(evidenceLabel),
            current.Platform,
            current.AliasDigest,
            current.AliasKeyId,
            current.AliasKeyEpoch,
            status,
            checked(current.AuthorizationRevision + 1));
        return new ChangePlatformAccountStatusCommand(
            current.SoulId,
            current.DeviceBindingId,
            current.PlatformAccountId,
            current.AuthorizationRevision,
            status,
            evidence,
            traceId,
            idempotencyKey,
            occurredAt);
    }

    internal static string PlatformAccount(string label) => "pa_" + Digest("platform-account", label)[..32];

    internal static byte[] ReadExternalPrivateKeyFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                "The external platform-authorization signer could not be loaded; no key path or key material is reported.");
        }
    }

    public void Dispose() => _signer.Dispose();

    private SignedPlatformAuthorizationEvidenceV1 Sign(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string evidenceId,
        string platform,
        string aliasDigest,
        string aliasKeyId,
        long aliasKeyEpoch,
        string targetStatus,
        long authorizationRevision)
    {
        var issuedAt = TimeProvider.System.GetUtcNow().AddSeconds(-5);
        var expiresAt = issuedAt.AddMinutes(15);
        if (occurredAt < issuedAt || occurredAt > expiresAt)
            throw new InvalidOperationException("The test command timestamp is outside the external signer's bounded 15-minute evidence window.");
        var unsigned = new SignedPlatformAuthorizationEvidenceV1(
            SignedPlatformAuthorizationEvidenceV1.CurrentSchemaVersion,
            SignedPlatformAuthorizationEvidenceV1.CurrentContractId,
            SignedPlatformAuthorizationEvidenceV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            "sensitive",
            evidenceId,
            platform,
            aliasDigest,
            aliasKeyId,
            aliasKeyEpoch,
            targetStatus,
            authorizationRevision,
            PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerId,
            PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId,
            ReleaseBomSha256,
            ReleaseGeneration,
            issuedAt,
            expiresAt,
            Convert.ToBase64String(new byte[64]));
        var canonical = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(unsigned);
        try
        {
            var signature = _signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            try
            {
                return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static string Trace(string label) => "trace_" + Digest("trace", label)[..32];
    private static string Idempotency(string label) => "idem_" + Digest("idempotency", label);
    internal static string EvidenceId(string label)
    {
        var normalized = new string(label.Select(static character => character switch
        {
            >= 'A' and <= 'Z' => (char)(character + ('a' - 'A')),
            >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' => character,
            _ => '_'
        }).ToArray());
        if (normalized.Length == 0) throw new ArgumentException("The evidence label is empty.", nameof(label));
        return "approval_" + normalized[..Math.Min(normalized.Length, 119)];
    }

    private static string Digest(string domain, string label)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(domain + ":" + label)));
}
