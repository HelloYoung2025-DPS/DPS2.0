using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.PlatformAuthorizationAuthority.Contracts;

namespace Dps.PlatformAuthorizationAuthority;

public sealed class PlatformAuthorizationEvidenceIssuer
{
    private const int MaximumRawProofBytes = 64 * 1024;
    private static readonly TimeSpan MaximumOccurrenceClockSkew = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IPlatformAuthorizationProofVerifier _proofVerifier;
    private readonly ITrustedPlatformAuthorizationRuntimeContextProvider _runtimeContextProvider;
    private readonly IDurablePlatformAuthorizationEvidenceReceiptStore _receiptStore;
    private readonly IPlatformAuthorizationEvidenceSigner _signer;
    private readonly TimeProvider _timeProvider;
    private readonly string _verifierId;
    private readonly string _proofFormat;
    private readonly string _storeId;
    private readonly long _storeTrustEpoch;

    private PlatformAuthorizationEvidenceIssuer(
        IPlatformAuthorizationProofVerifier proofVerifier,
        ITrustedPlatformAuthorizationRuntimeContextProvider runtimeContextProvider,
        IDurablePlatformAuthorizationEvidenceReceiptStore receiptStore,
        IPlatformAuthorizationEvidenceSigner signer,
        TimeProvider timeProvider)
    {
        _proofVerifier = proofVerifier ?? throw new ArgumentNullException(nameof(proofVerifier));
        _runtimeContextProvider = runtimeContextProvider ?? throw new ArgumentNullException(nameof(runtimeContextProvider));
        _receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        PlatformAuthorizationContractValidation.RequireIdentifier(proofVerifier.VerifierId, nameof(proofVerifier.VerifierId));
        PlatformAuthorizationContractValidation.RequireIdentifier(proofVerifier.ProofFormat, nameof(proofVerifier.ProofFormat));
        PlatformAuthorizationContractValidation.RequireIdentifier(receiptStore.StoreId, nameof(receiptStore.StoreId));
        if (receiptStore.TrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(receiptStore.TrustEpoch));
        _verifierId = proofVerifier.VerifierId;
        _proofFormat = proofVerifier.ProofFormat;
        _storeId = receiptStore.StoreId;
        _storeTrustEpoch = receiptStore.TrustEpoch;
    }

    // Production composition is intentionally not a public caller-selected factory.
    // A future trusted host must live in this module (or a separately reviewed friend
    // assembly) and bind attested concrete implementations before this can be exposed.
    internal static PlatformAuthorizationEvidenceIssuer CreateProduction(
        IPlatformAuthorizationProofVerifier proofVerifier,
        ITrustedPlatformAuthorizationRuntimeContextProvider runtimeContextProvider,
        IDurablePlatformAuthorizationEvidenceReceiptStore receiptStore,
        IExternalP256SignatureProvider externalSigner,
        TimeProvider? timeProvider = null) =>
        new(
            proofVerifier,
            runtimeContextProvider,
            receiptStore,
            new PinnedExternalP256EvidenceSigner(externalSigner),
            timeProvider ?? TimeProvider.System);

    internal static PlatformAuthorizationEvidenceIssuer CreateForTests(
        IPlatformAuthorizationProofVerifier proofVerifier,
        ITrustedPlatformAuthorizationRuntimeContextProvider runtimeContextProvider,
        IDurablePlatformAuthorizationEvidenceReceiptStore receiptStore,
        IPlatformAuthorizationEvidenceSigner signer,
        TimeProvider timeProvider) =>
        new(proofVerifier, runtimeContextProvider, receiptStore, signer, timeProvider);

    public async ValueTask<IssuedPlatformAuthorizationEvidence> IssueAsync(
        PlatformAuthorizationEvidenceIssueRequest request,
        ReadOnlyMemory<byte> rawExternalProof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureComposedDependenciesAreStable();
        if (!string.Equals(request.ProofFormat, _proofFormat, StringComparison.Ordinal))
            throw new PlatformAuthorizationIssuanceException("The requested proof format is not handled by the composed trusted verifier.");
        if (rawExternalProof.IsEmpty || rawExternalProof.Length > MaximumRawProofBytes)
            throw new PlatformAuthorizationIssuanceException($"Raw platform proof must contain between 1 and {MaximumRawProofBytes} bytes.");

        var proofCopy = rawExternalProof.ToArray();
        try
        {
            var rawProofSha256 = Convert.ToHexStringLower(SHA256.HashData(proofCopy));
            var receiptRuntime = await _runtimeContextProvider.GetActiveAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new PlatformAuthorizationIssuanceException("The trusted runtime context provider returned no active context while binding the durable receipt.");
            receiptRuntime.Validate();
            var payloadSha256 = ComputeIssuePayloadSha256(request, rawProofSha256, receiptRuntime);
            var key = new PlatformAuthorizationEvidenceReceiptKey(
                request.SoulId,
                request.DeviceBindingId,
                request.PlatformAccountId,
                request.IdempotencyKey);
            key.Validate();

            var receipt = await _receiptStore.GetOrCreateExactAsync(
                    key,
                    payloadSha256,
                    token => CreateSignedEnvelopeAsync(request, proofCopy, rawProofSha256, receiptRuntime, token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (receipt is null || !FixedTimeHexEquals(receipt.PayloadSha256, payloadSha256))
                throw new PlatformAuthorizationIssuanceException("The durable receipt store returned an envelope for a different payload.");
            if (receipt.EnvelopeUtf8.IsEmpty || receipt.EnvelopeUtf8.Length > MaximumRawProofBytes)
                throw new PlatformAuthorizationIssuanceException("The durable receipt store returned an invalid envelope length.");

            var exactBytes = receipt.EnvelopeUtf8.ToArray();
            try
            {
                var evidence = PlatformAuthorizationAuthorityContractJson.DeserializeEvidenceStrict(StrictUtf8.GetString(exactBytes));
                ValidateReturnedEvidence(evidence, request);
                var activeRuntime = await _runtimeContextProvider.GetActiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new PlatformAuthorizationIssuanceException("The trusted runtime context provider returned no active context while reading the durable receipt.");
                activeRuntime.Validate();
                if (!RuntimeContextsMatch(receiptRuntime, activeRuntime))
                    throw new PlatformAuthorizationIssuanceException("The active runtime trust context changed while reading the durable signed envelope.");
                if (evidence.ReleaseGeneration != activeRuntime.ReleaseGeneration ||
                    !FixedTimeHexEquals(evidence.ReleaseBomSha256, activeRuntime.ReleaseBomSha256))
                    throw new PlatformAuthorizationIssuanceException("The durable signed envelope does not target the active Release BOM generation.");
                var now = _timeProvider.GetUtcNow();
                if (now < evidence.IssuedAt || now > evidence.ExpiresAt)
                    throw new PlatformAuthorizationIssuanceException("The durable signed envelope is not currently valid.");
                var canonicalBytes = PlatformAuthorizationAuthorityContractJson.SerializeEvidenceStrict(evidence);
                try
                {
                    if (!exactBytes.AsSpan().SequenceEqual(canonicalBytes))
                        throw new PlatformAuthorizationIssuanceException("The durable receipt is not the exact canonical signed envelope.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(canonicalBytes);
                }

                var signedPayload = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(evidence);
                var signature = Convert.FromBase64String(evidence.SignatureBase64);
                try
                {
                    await _signer.VerifyAsync(signedPayload, signature, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(signedPayload);
                    CryptographicOperations.ZeroMemory(signature);
                }

                var envelopeSha256 = Convert.ToHexStringLower(SHA256.HashData(exactBytes));
                return new IssuedPlatformAuthorizationEvidence(
                    evidence,
                    exactBytes,
                    receipt.Replayed,
                    envelopeSha256);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exactBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proofCopy);
        }
    }

    private async ValueTask<byte[]> CreateSignedEnvelopeAsync(
        PlatformAuthorizationEvidenceIssueRequest request,
        ReadOnlyMemory<byte> rawProof,
        string rawProofSha256,
        TrustedPlatformAuthorizationRuntimeContext runtimeBeforeSigning,
        CancellationToken cancellationToken)
    {
        EnsureComposedDependenciesAreStable();
        var now = _timeProvider.GetUtcNow();
        if (request.OccurredAt < now - MaximumOccurrenceClockSkew ||
            request.OccurredAt > now + MaximumOccurrenceClockSkew)
            throw new PlatformAuthorizationIssuanceException("occurred_at is outside the two-minute issuance clock-skew window.");

        var verificationContext = new PlatformAuthorizationProofVerificationContext(
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.AuthorizationEvidenceId,
            request.Platform,
            request.AliasDigest,
            request.AliasKeyId,
            request.AliasKeyEpoch,
            request.TargetStatus,
            request.AuthorizationRevision,
            request.ProofFormat,
            rawProofSha256);
        var verified = await _proofVerifier.VerifyAsync(verificationContext, rawProof, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformAuthorizationIssuanceException("The platform-proof verifier returned no verified decision.");
        verified.Validate();
        ValidateVerifiedProof(verified, request, rawProofSha256, _verifierId, now);

        runtimeBeforeSigning.Validate();

        var expiresAt = request.OccurredAt.AddMinutes(15);
        if (verified.ValidUntil < expiresAt) expiresAt = verified.ValidUntil;
        if (expiresAt <= request.OccurredAt)
            throw new PlatformAuthorizationIssuanceException("The verified raw proof expires before the evidence can become valid.");

        var unsigned = new SignedPlatformAuthorizationEvidenceV1(
            SignedPlatformAuthorizationEvidenceV1.CurrentSchemaVersion,
            SignedPlatformAuthorizationEvidenceV1.CurrentContractId,
            SignedPlatformAuthorizationEvidenceV1.CurrentProducerModule,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            "sensitive",
            request.AuthorizationEvidenceId,
            request.Platform,
            request.AliasDigest,
            request.AliasKeyId,
            request.AliasKeyEpoch,
            request.TargetStatus,
            request.AuthorizationRevision,
            PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerId,
            PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId,
            runtimeBeforeSigning.ReleaseBomSha256,
            runtimeBeforeSigning.ReleaseGeneration,
            request.OccurredAt,
            expiresAt,
            Convert.ToBase64String(new byte[64]));
        var canonical = PlatformAuthorizationEvidenceCanonicalizer.Canonicalize(unsigned);
        byte[]? signature = null;
        try
        {
            signature = await _signer.SignAsync(canonical, cancellationToken).ConfigureAwait(false);
            if (signature is null || signature.Length != 64)
                throw new PlatformAuthorizationIssuanceException("The evidence signer did not return an exact 64-byte P-256 P1363 signature.");
            var evidence = unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
            evidence.Validate();
            await _signer.VerifyAsync(canonical, signature, cancellationToken).ConfigureAwait(false);

            var runtimeAfterSigning = await _runtimeContextProvider.GetActiveAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new PlatformAuthorizationIssuanceException("The trusted runtime context disappeared during signing.");
            runtimeAfterSigning.Validate();
            if (!RuntimeContextsMatch(runtimeBeforeSigning, runtimeAfterSigning))
                throw new PlatformAuthorizationIssuanceException("The active Release BOM generation changed during evidence issuance.");
            if (_timeProvider.GetUtcNow() > evidence.ExpiresAt)
                throw new PlatformAuthorizationIssuanceException("The evidence expired before the exact envelope could be persisted.");
            return PlatformAuthorizationAuthorityContractJson.SerializeEvidenceStrict(evidence);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private void EnsureComposedDependenciesAreStable()
    {
        if (!string.Equals(_proofVerifier.VerifierId, _verifierId, StringComparison.Ordinal) ||
            !string.Equals(_proofVerifier.ProofFormat, _proofFormat, StringComparison.Ordinal) ||
            !string.Equals(_receiptStore.StoreId, _storeId, StringComparison.Ordinal) ||
            _receiptStore.TrustEpoch != _storeTrustEpoch)
            throw new PlatformAuthorizationIssuanceException("A trusted verifier or durable receipt-store identity changed after composition.");
    }

    private static void ValidateVerifiedProof(
        VerifiedPlatformAuthorizationProof verified,
        PlatformAuthorizationEvidenceIssueRequest request,
        string rawProofSha256,
        string expectedVerifierId,
        DateTimeOffset now)
    {
        if (!Matches(verified.VerifierId, expectedVerifierId) ||
            !Matches(verified.RawProofSha256, rawProofSha256, fixedTimeHex: true) ||
            !Matches(verified.SoulId, request.SoulId) ||
            !Matches(verified.DeviceBindingId, request.DeviceBindingId) ||
            !Matches(verified.PlatformAccountId, request.PlatformAccountId) ||
            !Matches(verified.AuthorizationEvidenceId, request.AuthorizationEvidenceId) ||
            !Matches(verified.Platform, request.Platform) ||
            !Matches(verified.AliasDigest, request.AliasDigest, fixedTimeHex: true) ||
            !Matches(verified.AliasKeyId, request.AliasKeyId) ||
            verified.AliasKeyEpoch != request.AliasKeyEpoch ||
            !Matches(verified.TargetStatus, request.TargetStatus) ||
            verified.AuthorizationRevision != request.AuthorizationRevision ||
            !Matches(verified.ProofFormat, request.ProofFormat))
            throw new PlatformAuthorizationIssuanceException("The verified platform proof does not match the exact requested authorization scope.");
        if (verified.VerifiedAt > now || now > verified.ValidUntil)
            throw new PlatformAuthorizationIssuanceException("The verified platform proof is not currently valid.");
    }

    private static void ValidateReturnedEvidence(
        SignedPlatformAuthorizationEvidenceV1 evidence,
        PlatformAuthorizationEvidenceIssueRequest request)
    {
        if (!Matches(evidence.SoulId, request.SoulId) ||
            !Matches(evidence.DeviceBindingId, request.DeviceBindingId) ||
            !Matches(evidence.PlatformAccountId, request.PlatformAccountId) ||
            !Matches(evidence.TraceId, request.TraceId) ||
            !Matches(evidence.IdempotencyKey, request.IdempotencyKey) ||
            evidence.OccurredAt != request.OccurredAt ||
            !Matches(evidence.AuthorizationEvidenceId, request.AuthorizationEvidenceId) ||
            !Matches(evidence.Platform, request.Platform) ||
            !Matches(evidence.AliasDigest, request.AliasDigest, fixedTimeHex: true) ||
            !Matches(evidence.AliasKeyId, request.AliasKeyId) ||
            evidence.AliasKeyEpoch != request.AliasKeyEpoch ||
            !Matches(evidence.TargetStatus, request.TargetStatus) ||
            evidence.AuthorizationRevision != request.AuthorizationRevision ||
            evidence.IssuedAt != request.OccurredAt)
            throw new PlatformAuthorizationIssuanceException("The durable signed envelope does not match the exact issuance request.");
    }

    private static string ComputeIssuePayloadSha256(
        PlatformAuthorizationEvidenceIssueRequest request,
        string rawProofSha256,
        TrustedPlatformAuthorizationRuntimeContext runtimeContext)
    {
        runtimeContext.Validate();
        using var stream = new MemoryStream();
        Append(stream, "DPS:PLATFORM-AUTHORIZATION-ISSUE-REQUEST:V2");
        Append(stream, request.SoulId);
        Append(stream, request.DeviceBindingId);
        Append(stream, request.PlatformAccountId);
        Append(stream, request.TraceId);
        Append(stream, request.IdempotencyKey);
        Append(stream, request.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        Append(stream, request.AuthorizationEvidenceId);
        Append(stream, request.Platform);
        Append(stream, request.AliasDigest);
        Append(stream, request.AliasKeyId);
        Append(stream, request.AliasKeyEpoch.ToString(CultureInfo.InvariantCulture));
        Append(stream, request.TargetStatus);
        Append(stream, request.AuthorizationRevision.ToString(CultureInfo.InvariantCulture));
        Append(stream, request.ProofFormat);
        Append(stream, rawProofSha256);
        Append(stream, runtimeContext.ReleaseBomSha256);
        Append(stream, runtimeContext.ReleaseGeneration.ToString(CultureInfo.InvariantCulture));
        Append(stream, runtimeContext.TrustEpoch.ToString(CultureInfo.InvariantCulture));
        Append(stream, runtimeContext.RuntimeContextSha256);
        var buffer = stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
        try { return Convert.ToHexStringLower(SHA256.HashData(buffer)); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
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

    private static bool RuntimeContextsMatch(
        TrustedPlatformAuthorizationRuntimeContext left,
        TrustedPlatformAuthorizationRuntimeContext right) =>
        left.ReleaseGeneration == right.ReleaseGeneration &&
        left.TrustEpoch == right.TrustEpoch &&
        FixedTimeHexEquals(left.ReleaseBomSha256, right.ReleaseBomSha256) &&
        FixedTimeHexEquals(left.RuntimeContextSha256, right.RuntimeContextSha256);

    private static bool Matches(string left, string right, bool fixedTimeHex = false) =>
        fixedTimeHex ? FixedTimeHexEquals(left, right) : string.Equals(left, right, StringComparison.Ordinal);

    private static bool FixedTimeHexEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64) return false;
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
