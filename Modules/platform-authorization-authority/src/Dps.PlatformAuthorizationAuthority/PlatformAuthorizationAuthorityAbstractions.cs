using Dps.PlatformAuthorizationAuthority.Contracts;

namespace Dps.PlatformAuthorizationAuthority;

public sealed record PlatformAuthorizationEvidenceIssueRequest(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string AuthorizationEvidenceId,
    string Platform,
    string AliasDigest,
    string AliasKeyId,
    long AliasKeyEpoch,
    string TargetStatus,
    long AuthorizationRevision,
    string ProofFormat)
{
    internal void Validate()
    {
        PlatformAuthorizationContractValidation.RequireSoulId(SoulId);
        PlatformAuthorizationContractValidation.RequireDeviceBindingId(DeviceBindingId);
        PlatformAuthorizationContractValidation.RequirePlatformAccountId(PlatformAccountId);
        PlatformAuthorizationContractValidation.RequireTraceId(TraceId);
        PlatformAuthorizationContractValidation.RequireIdempotencyKey(IdempotencyKey);
        PlatformAuthorizationContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        PlatformAuthorizationContractValidation.RequireAuthorizationEvidenceId(AuthorizationEvidenceId);
        PlatformAuthorizationContractValidation.RequireIdentifier(Platform, nameof(Platform));
        PlatformAuthorizationContractValidation.RequireSha256(AliasDigest, nameof(AliasDigest));
        PlatformAuthorizationContractValidation.RequireKeyId(AliasKeyId, nameof(AliasKeyId));
        if (AliasKeyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(AliasKeyEpoch));
        PlatformAuthorizationContractValidation.RequireStatus(TargetStatus, nameof(TargetStatus));
        if (AuthorizationRevision < 1) throw new ArgumentOutOfRangeException(nameof(AuthorizationRevision));
        PlatformAuthorizationContractValidation.RequireIdentifier(ProofFormat, nameof(ProofFormat));
    }
}

public sealed record PlatformAuthorizationProofVerificationContext(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string AuthorizationEvidenceId,
    string Platform,
    string AliasDigest,
    string AliasKeyId,
    long AliasKeyEpoch,
    string TargetStatus,
    long AuthorizationRevision,
    string ProofFormat,
    string RawProofSha256);

public sealed record VerifiedPlatformAuthorizationProof(
    string VerifierId,
    string ProofFormat,
    string RawProofSha256,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string AuthorizationEvidenceId,
    string Platform,
    string AliasDigest,
    string AliasKeyId,
    long AliasKeyEpoch,
    string TargetStatus,
    long AuthorizationRevision,
    DateTimeOffset VerifiedAt,
    DateTimeOffset ValidUntil)
{
    internal void Validate()
    {
        PlatformAuthorizationContractValidation.RequireIdentifier(VerifierId, nameof(VerifierId));
        PlatformAuthorizationContractValidation.RequireIdentifier(ProofFormat, nameof(ProofFormat));
        PlatformAuthorizationContractValidation.RequireSha256(RawProofSha256, nameof(RawProofSha256));
        PlatformAuthorizationContractValidation.RequireSoulId(SoulId);
        PlatformAuthorizationContractValidation.RequireDeviceBindingId(DeviceBindingId);
        PlatformAuthorizationContractValidation.RequirePlatformAccountId(PlatformAccountId);
        PlatformAuthorizationContractValidation.RequireAuthorizationEvidenceId(AuthorizationEvidenceId);
        PlatformAuthorizationContractValidation.RequireIdentifier(Platform, nameof(Platform));
        PlatformAuthorizationContractValidation.RequireSha256(AliasDigest, nameof(AliasDigest));
        PlatformAuthorizationContractValidation.RequireKeyId(AliasKeyId, nameof(AliasKeyId));
        if (AliasKeyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(AliasKeyEpoch));
        PlatformAuthorizationContractValidation.RequireStatus(TargetStatus, nameof(TargetStatus));
        if (AuthorizationRevision < 1) throw new ArgumentOutOfRangeException(nameof(AuthorizationRevision));
        PlatformAuthorizationContractValidation.RequireUtc(VerifiedAt, nameof(VerifiedAt));
        PlatformAuthorizationContractValidation.RequireUtc(ValidUntil, nameof(ValidUntil));
        if (ValidUntil <= VerifiedAt || ValidUntil - VerifiedAt > TimeSpan.FromMinutes(15))
            throw new ArgumentException("Verified raw platform proof must have a validity window no longer than fifteen minutes.", nameof(ValidUntil));
    }
}

public interface IPlatformAuthorizationProofVerifier
{
    string VerifierId { get; }
    string ProofFormat { get; }

    ValueTask<VerifiedPlatformAuthorizationProof> VerifyAsync(
        PlatformAuthorizationProofVerificationContext context,
        ReadOnlyMemory<byte> rawProof,
        CancellationToken cancellationToken);
}

public sealed record TrustedPlatformAuthorizationRuntimeContext(
    string ReleaseBomSha256,
    long ReleaseGeneration,
    long TrustEpoch,
    string RuntimeContextSha256)
{
    internal void Validate()
    {
        PlatformAuthorizationContractValidation.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (ReleaseGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ReleaseGeneration));
        if (TrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(TrustEpoch));
        PlatformAuthorizationContractValidation.RequireSha256(RuntimeContextSha256, nameof(RuntimeContextSha256));
    }
}

public interface ITrustedPlatformAuthorizationRuntimeContextProvider
{
    ValueTask<TrustedPlatformAuthorizationRuntimeContext> GetActiveAsync(CancellationToken cancellationToken);
}

public interface IExternalP256SignatureProvider
{
    string ProviderId { get; }
    string IssuerKeyId { get; }

    // The returned buffers transfer ownership to the caller and are zeroed after use.
    ValueTask<byte[]> ExportSubjectPublicKeyInfoAsync(CancellationToken cancellationToken);
    ValueTask<byte[]> SignSha256P1363Async(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken);
}

public sealed record PlatformAuthorizationEvidenceReceiptKey(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string IdempotencyKey)
{
    internal void Validate()
    {
        PlatformAuthorizationContractValidation.RequireSoulId(SoulId);
        PlatformAuthorizationContractValidation.RequireDeviceBindingId(DeviceBindingId);
        PlatformAuthorizationContractValidation.RequirePlatformAccountId(PlatformAccountId);
        PlatformAuthorizationContractValidation.RequireIdempotencyKey(IdempotencyKey);
    }
}

public sealed class PlatformAuthorizationExactEnvelopeReceipt
{
    private readonly byte[] _envelopeUtf8;

    public PlatformAuthorizationExactEnvelopeReceipt(
        string payloadSha256,
        ReadOnlySpan<byte> envelopeUtf8,
        bool replayed)
    {
        PlatformAuthorizationContractValidation.RequireSha256(payloadSha256, nameof(payloadSha256));
        if (envelopeUtf8.IsEmpty || envelopeUtf8.Length > 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(envelopeUtf8));
        PayloadSha256 = payloadSha256;
        _envelopeUtf8 = envelopeUtf8.ToArray();
        Replayed = replayed;
    }

    public string PayloadSha256 { get; }
    public ReadOnlyMemory<byte> EnvelopeUtf8 => _envelopeUtf8.ToArray();
    public bool Replayed { get; }
}

public interface IDurablePlatformAuthorizationEvidenceReceiptStore
{
    string StoreId { get; }
    long TrustEpoch { get; }

    // Implementations must serialize this operation by exact receipt key, persist the
    // newly created envelope before returning it, replay the stored bytes unchanged for
    // the same payload hash, and atomically quarantine/throw for a different payload hash.
    ValueTask<PlatformAuthorizationExactEnvelopeReceipt> GetOrCreateExactAsync(
        PlatformAuthorizationEvidenceReceiptKey key,
        string payloadSha256,
        Func<CancellationToken, ValueTask<byte[]>> createEnvelope,
        CancellationToken cancellationToken);
}

public sealed class IssuedPlatformAuthorizationEvidence
{
    private readonly byte[] _exactEnvelopeUtf8;

    internal IssuedPlatformAuthorizationEvidence(
        SignedPlatformAuthorizationEvidenceV1 evidence,
        ReadOnlySpan<byte> exactEnvelopeUtf8,
        bool replayed,
        string envelopeSha256)
    {
        Evidence = evidence;
        _exactEnvelopeUtf8 = exactEnvelopeUtf8.ToArray();
        Replayed = replayed;
        EnvelopeSha256 = envelopeSha256;
    }

    public SignedPlatformAuthorizationEvidenceV1 Evidence { get; }
    public ReadOnlyMemory<byte> ExactEnvelopeUtf8 => _exactEnvelopeUtf8.ToArray();
    public bool Replayed { get; }
    public string EnvelopeSha256 { get; }
}

public sealed class PlatformAuthorizationIssuanceException(string message) : Exception(message);

public sealed class PlatformAuthorizationIdempotencyConflictException(string message) : Exception(message);
