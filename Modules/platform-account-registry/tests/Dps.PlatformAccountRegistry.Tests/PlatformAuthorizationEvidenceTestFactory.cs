using System.Security.Cryptography;
using Dps.PlatformAccountRegistry.Contracts;
using Dps.PlatformAuthorizationAuthority.Contracts;

namespace Dps.PlatformAccountRegistry.Tests;

internal sealed class PlatformAuthorizationEvidenceTestFactory : IDisposable
{
    internal const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string BindingA = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string BindingB = "db_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string IssuerId = PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerId;
    private const string IssuerKeyId = PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId;
    internal static readonly string ReleaseBomSha256 = new('c', 64);
    internal const long ReleaseGeneration = 11;
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly AdjustableTimeProvider _timeProvider;

    internal PlatformAuthorizationEvidenceTestFactory(DateTimeOffset now)
    {
        Now = now;
        _timeProvider = new AdjustableTimeProvider(now);
        var publicSpki = _signer.ExportSubjectPublicKeyInfo();
        try
        {
            Verifier = PlatformAuthorizationEvidenceVerifier.CreateForTests(
                publicSpki,
                IssuerId,
                IssuerKeyId,
                ReleaseBomSha256,
                ReleaseGeneration,
                _timeProvider);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicSpki);
        }
    }

    internal DateTimeOffset Now { get; }
    internal PlatformAuthorizationEvidenceVerifier Verifier { get; }

    internal void Advance(TimeSpan delta) => _timeProvider.Advance(delta);

    internal AuthorizePlatformAccountCommand Authorize(
        string soulId,
        string bindingId,
        char digest,
        string evidenceLabel,
        string idempotencyLabel,
        DateTimeOffset occurredAt,
        string? platformAccountId = null)
    {
        var traceId = Trace(idempotencyLabel);
        var idempotencyKey = Idempotency(idempotencyLabel);
        var accountId = platformAccountId ?? PlatformAccount(
            soulId + ":" + bindingId + ":" + digest + ":" + evidenceLabel);
        var evidence = CreateEvidence(
            soulId,
            bindingId,
            accountId,
            traceId,
            idempotencyKey,
            occurredAt,
            "approval_" + Normalize(evidenceLabel),
            "fixture",
            new string(digest, 64),
            "tenant-hmac-v1",
            7,
            "authorized",
            1);
        return new AuthorizePlatformAccountCommand(
            soulId,
            bindingId,
            accountId,
            "fixture",
            new string(digest, 64),
            "tenant-hmac-v1",
            7,
            evidence,
            traceId,
            idempotencyKey,
            occurredAt);
    }

    internal ChangePlatformAccountStatusCommand Status(
        PlatformAccountAuthorizedV1 current,
        long expectedRevision,
        string status,
        string evidenceLabel,
        string idempotencyLabel,
        DateTimeOffset occurredAt)
    {
        var traceId = Trace(idempotencyLabel);
        var idempotencyKey = Idempotency(idempotencyLabel);
        var evidence = CreateEvidence(
            current.SoulId,
            current.DeviceBindingId,
            current.PlatformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            "approval_" + Normalize(evidenceLabel),
            current.Platform,
            current.AliasDigest,
            current.AliasKeyId,
            current.AliasKeyEpoch,
            status,
            checked(expectedRevision + 1));
        return new ChangePlatformAccountStatusCommand(
            current.SoulId,
            current.DeviceBindingId,
            current.PlatformAccountId,
            expectedRevision,
            status,
            evidence,
            traceId,
            idempotencyKey,
            occurredAt);
    }

    internal SignedPlatformAuthorizationEvidenceV1 CreateEvidence(
        string soulId,
        string bindingId,
        string accountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string evidenceId,
        string platform,
        string aliasDigest,
        string aliasKeyId,
        long aliasKeyEpoch,
        string targetStatus,
        long authorizationRevision,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        string? releaseBomSha256 = null,
        long? releaseGeneration = null)
    {
        var unsigned = new SignedPlatformAuthorizationEvidenceV1(
            SignedPlatformAuthorizationEvidenceV1.CurrentSchemaVersion,
            SignedPlatformAuthorizationEvidenceV1.CurrentContractId,
            SignedPlatformAuthorizationEvidenceV1.CurrentProducerModule,
            soulId,
            bindingId,
            accountId,
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
            IssuerId,
            IssuerKeyId,
            releaseBomSha256 ?? ReleaseBomSha256,
            releaseGeneration ?? ReleaseGeneration,
            issuedAt ?? Now.AddMinutes(-1),
            expiresAt ?? Now.AddMinutes(10),
            Convert.ToBase64String(new byte[64]));
        var canonical = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(unsigned);
        try
        {
            var signature = _signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            try { return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) }; }
            finally { CryptographicOperations.ZeroMemory(signature); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    internal static string Trace(string label) => "trace_" + Hex(label)[..32];
    internal static string Idempotency(string label) => "idem_" + Hex(label);
    internal static string PlatformAccount(string label) => "pa_" + Hex(label)[..32];

    public void Dispose() => _signer.Dispose();

    private static string Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string Normalize(string value)
    {
        var characters = value.Select(static character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'
                ? character
                : '_').ToArray();
        return new string(characters)[..Math.Min(characters.Length, 100)];
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delta)
        {
            if (delta <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
            _now = _now.Add(delta);
        }
    }
}
