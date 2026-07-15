using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dps.PolicyApproval.Contracts;

namespace Dps.ControlPlaneHost;

public interface IControlPlaneReconciliationSigningAuthority
{
    byte[] ExportSubjectPublicKeyInfo();

    Task<byte[]> SignReconciliationAsync(
        ApprovalSubmissionReconciliationV1 unsignedReconciliation,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken);
}

public interface IControlPlaneHumanRecoveryApprovalAuthority
{
    byte[] ExportSubjectPublicKeyInfo();

    Task<byte[]> AuthorizeAndSignRecoveryAsync(
        ApprovalSubmissionRecoveryV1 unsignedRecovery,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken);
}

public interface IApprovalSubmissionReconciliationPort
{
    string AuthScope { get; }

    string CredentialAuthorityFingerprintSha256 { get; }

    Task<ReadOnlyMemory<byte>> ReconcileAsync(
        ReadOnlyMemory<byte> canonicalReconciliationWire,
        CancellationToken cancellationToken);
}

public interface IApprovalSubmissionRecoveryPort
{
    string AuthScope { get; }

    string CredentialAuthorityFingerprintSha256 { get; }

    Task<ReadOnlyMemory<byte>> RecoverAsync(
        ReadOnlyMemory<byte> canonicalRecoveryWire,
        CancellationToken cancellationToken);
}

public sealed record ApprovalSubmissionStateExpectation(
    Guid SubmissionAttemptId,
    Guid ApprovalId,
    Guid ProposalId,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string ReleaseBomSha256,
    long ReleaseBomGeneration,
    string NativeRequestBindingSha256,
    string SubmissionIntentSha256,
    string State,
    string? PredecessorStateSha256,
    string EvidenceSha256);

public sealed record ControlPlaneReconciliationRequest(
    Guid ReconciliationId,
    string Finding,
    string EvidenceSha256,
    DateTimeOffset OccurredAt,
    DateTimeOffset ValidUntil);

public sealed record ControlPlaneRecoveryRequest(
    Guid RecoveryId,
    Guid NextSubmissionAttemptId,
    Guid NextLeaseId,
    int NextAttempt,
    string NextReleaseBomSha256,
    long NextReleaseBomGeneration,
    string NextExecutionAuthorizationSha256,
    string NextNativeRequestBindingSha256,
    string HumanApprovalId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ValidUntil);

public sealed class VerifiedApprovalSubmissionState
{
    internal VerifiedApprovalSubmissionState(
        ApprovalSubmissionStateV1 value,
        string authorityFingerprintSha256)
    {
        Value = value;
        AuthorityFingerprintSha256 = authorityFingerprintSha256;
    }

    public ApprovalSubmissionStateV1 Value { get; }

    public string AuthorityFingerprintSha256 { get; }
}

public sealed class SignedControlPlaneLifecycleEnvelope<T>
    where T : class
{
    private readonly byte[] _wireBytes;

    internal SignedControlPlaneLifecycleEnvelope(
        T value,
        byte[] wireBytes,
        string commitmentSha256)
    {
        Value = value;
        _wireBytes = wireBytes;
        CommitmentSha256 = commitmentSha256;
    }

    public T Value { get; }

    public string CommitmentSha256 { get; }

    public byte[] CopyWireBytes() => (byte[])_wireBytes.Clone();
}

public sealed record ControlPlaneReconciliationExchange(
    SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionReconciliationV1> Request,
    VerifiedApprovalSubmissionState State);

public sealed record ControlPlaneRecoveryExchange(
    SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionRecoveryV1> Request,
    VerifiedApprovalSubmissionState State);

public sealed class ControlPlaneSubmissionStateConsumer : IDisposable
{
    private readonly ECDsa _policyStateAuthority;
    private readonly object _signatureGate = new();

    public ControlPlaneSubmissionStateConsumer(
        ReadOnlySpan<byte> policyStateAuthoritySubjectPublicKeyInfo)
    {
        _policyStateAuthority = ImportCanonicalP256(
            policyStateAuthoritySubjectPublicKeyInfo,
            nameof(policyStateAuthoritySubjectPublicKeyInfo));
        AuthorityFingerprintSha256 = ComputePublicKeyFingerprint(_policyStateAuthority);
    }

    public string AuthorityFingerprintSha256 { get; }

    public VerifiedApprovalSubmissionState Consume(
        ReadOnlySpan<byte> canonicalWire,
        ApprovalSubmissionStateExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        var state = ApprovalSubmissionStateV1Codec.Deserialize(canonicalWire);
        var expectedStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(state);
        if (!FixedDigestEquals(expectedStateSha256, state.StateSha256))
            throw new InvalidDataException("Policy submission state commitment is invalid.");

        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalStateBytes(state);
        byte[]? signature = null;
        try
        {
            signature = Convert.FromBase64String(state.SignatureBase64);
            bool verified;
            lock (_signatureGate)
            {
                verified = _policyStateAuthority.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }

            if (!verified)
                throw new UnauthorizedAccessException(
                    "Policy submission state signature verification failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
        }

        RequireExactExpectation(state, expectation);
        return new VerifiedApprovalSubmissionState(
            state,
            AuthorityFingerprintSha256);
    }

    public void Dispose() => _policyStateAuthority.Dispose();

    internal static string ComputePublicKeyFingerprint(ECDsa key)
    {
        var canonical = key.ExportSubjectPublicKeyInfo();
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(canonical));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    internal static ECDsa ImportCanonicalP256(
        ReadOnlySpan<byte> subjectPublicKeyInfo,
        string parameterName)
    {
        if (subjectPublicKeyInfo.Length is < 1 or > 1024)
            throw new ArgumentException(
                "A bounded NIST P-256 SubjectPublicKeyInfo value is required.",
                parameterName);

        var key = ECDsa.Create();
        byte[]? normalized = null;
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length || key.KeySize != 256)
                throw new ArgumentException(
                    "The authority key must be exact NIST P-256 SubjectPublicKeyInfo.",
                    parameterName);
            var parameters = key.ExportParameters(false);
            if (!string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The authority key must use NIST P-256.",
                    parameterName);
            }

            normalized = key.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(
                    normalized,
                    subjectPublicKeyInfo))
            {
                throw new ArgumentException(
                    "The authority key encoding is not canonical SubjectPublicKeyInfo.",
                    parameterName);
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            if (normalized is not null)
                CryptographicOperations.ZeroMemory(normalized);
        }
    }

    internal static bool FixedDigestEquals(string left, string right)
    {
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (leftBytes is not null)
                CryptographicOperations.ZeroMemory(leftBytes);
            if (rightBytes is not null)
                CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static void RequireExactExpectation(
        ApprovalSubmissionStateV1 state,
        ApprovalSubmissionStateExpectation expectation)
    {
        if (state.SubmissionAttemptId != expectation.SubmissionAttemptId
            || state.ApprovalId != expectation.ApprovalId
            || state.ProposalId != expectation.ProposalId
            || state.CommandId != expectation.CommandId
            || state.LeaseId != expectation.LeaseId
            || state.Attempt != expectation.Attempt
            || !string.Equals(state.SoulId, expectation.SoulId, StringComparison.Ordinal)
            || !string.Equals(state.DeviceBindingId, expectation.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(state.PlatformAccountId, expectation.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(state.TraceId, expectation.TraceId, StringComparison.Ordinal)
            || !string.Equals(state.IdempotencyKey, expectation.IdempotencyKey, StringComparison.Ordinal)
            || !FixedDigestEquals(state.ReleaseBomSha256, expectation.ReleaseBomSha256)
            || state.ReleaseBomGeneration != expectation.ReleaseBomGeneration
            || !FixedDigestEquals(
                state.NativeRequestBindingSha256,
                expectation.NativeRequestBindingSha256)
            || !FixedDigestEquals(
                state.SubmissionIntentSha256,
                expectation.SubmissionIntentSha256)
            || !string.Equals(state.State, expectation.State, StringComparison.Ordinal)
            || !NullableDigestEquals(
                state.PredecessorStateSha256,
                expectation.PredecessorStateSha256)
            || !FixedDigestEquals(state.EvidenceSha256, expectation.EvidenceSha256))
        {
            throw new UnauthorizedAccessException(
                "Policy submission state is not bound to the exact expected lifecycle scope.");
        }
    }

    private static bool NullableDigestEquals(string? left, string? right)
        => left is null || right is null
            ? left is null && right is null
            : FixedDigestEquals(left, right);
}

public sealed class ControlPlaneSubmissionLifecycleProducer : IDisposable
{
    private static readonly TimeSpan MaximumAuthorityTimeout = TimeSpan.FromSeconds(5);

    private const string ZeroSignature =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    private readonly IControlPlaneReconciliationSigningAuthority _reconciliationSigner;
    private readonly IControlPlaneHumanRecoveryApprovalAuthority _recoverySigner;
    private readonly ECDsa _reconciliationVerifier;
    private readonly ECDsa _recoveryVerifier;
    private readonly object _reconciliationVerifierGate = new();
    private readonly object _recoveryVerifierGate = new();
    private readonly AuthorityCallState _reconciliationAuthority = new();
    private readonly AuthorityCallState _recoveryAuthority = new();
    private readonly string _policyStateAuthorityFingerprintSha256;
    private readonly TimeSpan _authorityTimeout;

    public ControlPlaneSubmissionLifecycleProducer(
        IControlPlaneReconciliationSigningAuthority reconciliationSigner,
        IControlPlaneHumanRecoveryApprovalAuthority recoverySigner,
        string policyStateAuthorityFingerprintSha256,
        TimeSpan? authorityTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(reconciliationSigner);
        ArgumentNullException.ThrowIfNull(recoverySigner);
        if (ReferenceEquals(reconciliationSigner, recoverySigner))
            throw new InvalidOperationException(
                "Reconciliation and human recovery require separate signing capabilities.");
        RequireSha256(
            policyStateAuthorityFingerprintSha256,
            nameof(policyStateAuthorityFingerprintSha256));
        var effectiveAuthorityTimeout = authorityTimeout ?? MaximumAuthorityTimeout;
        if (effectiveAuthorityTimeout <= TimeSpan.Zero
            || effectiveAuthorityTimeout > MaximumAuthorityTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authorityTimeout),
                "Lifecycle authority timeout must be positive and no greater than five seconds.");
        }

        _reconciliationSigner = reconciliationSigner;
        _recoverySigner = recoverySigner;
        _policyStateAuthorityFingerprintSha256 =
            policyStateAuthorityFingerprintSha256;
        _authorityTimeout = effectiveAuthorityTimeout;

        var reconciliationPublicKey = reconciliationSigner.ExportSubjectPublicKeyInfo();
        var recoveryPublicKey = recoverySigner.ExportSubjectPublicKeyInfo();
        try
        {
            _reconciliationVerifier = ControlPlaneSubmissionStateConsumer.ImportCanonicalP256(
                reconciliationPublicKey,
                nameof(reconciliationSigner));
            try
            {
                _recoveryVerifier = ControlPlaneSubmissionStateConsumer.ImportCanonicalP256(
                    recoveryPublicKey,
                    nameof(recoverySigner));
            }
            catch
            {
                _reconciliationVerifier.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reconciliationPublicKey);
            CryptographicOperations.ZeroMemory(recoveryPublicKey);
        }

        ReconciliationAuthorityFingerprintSha256 =
            ControlPlaneSubmissionStateConsumer.ComputePublicKeyFingerprint(
                _reconciliationVerifier);
        RecoveryAuthorityFingerprintSha256 =
            ControlPlaneSubmissionStateConsumer.ComputePublicKeyFingerprint(
                _recoveryVerifier);
        if (ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                ReconciliationAuthorityFingerprintSha256,
                RecoveryAuthorityFingerprintSha256)
            || ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                ReconciliationAuthorityFingerprintSha256,
                _policyStateAuthorityFingerprintSha256)
            || ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                RecoveryAuthorityFingerprintSha256,
                _policyStateAuthorityFingerprintSha256))
        {
            Dispose();
            throw new InvalidOperationException(
                "Reconciliation, recovery, and policy-state authorities must be pairwise distinct.");
        }
    }

    public string ReconciliationAuthorityFingerprintSha256 { get; }

    public string RecoveryAuthorityFingerprintSha256 { get; }

    public async Task<SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionReconciliationV1>>
        CreateReconciliationAsync(
            VerifiedApprovalSubmissionState verifiedState,
            ControlPlaneReconciliationRequest request,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(verifiedState);
        ArgumentNullException.ThrowIfNull(request);
        RequirePolicyAuthority(verifiedState);

        var state = verifiedState.Value;
        if (state.State == ApprovalSubmissionStateV1.SubmissionPending
            && !ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                state.EvidenceSha256,
                state.SubmissionIntentSha256))
        {
            throw new UnauthorizedAccessException(
                "A PENDING state must carry the exact submission-intent commitment as evidence.");
        }
        var pendingStateSha256 = state.State switch
        {
            ApprovalSubmissionStateV1.SubmissionPending => state.StateSha256,
            ApprovalSubmissionStateV1.UnknownSubmission
                when state.PredecessorStateSha256 is not null
                => state.PredecessorStateSha256,
            _ => throw new UnauthorizedAccessException(
                "Only a verified PENDING or UNKNOWN policy state may be reconciled.")
        };

        var unsigned = new ApprovalSubmissionReconciliationV1(
            ApprovalSubmissionReconciliationV1.CurrentSchemaVersion,
            ApprovalSubmissionReconciliationV1.CurrentContractId,
            ApprovalSubmissionReconciliationV1.CurrentProducerModule,
            ApprovalSubmissionReconciliationV1.CurrentAuthScope,
            ApprovalSubmissionReconciliationV1.CurrentAuthorityRole,
            request.ReconciliationId,
            state.SubmissionAttemptId,
            state.ApprovalId,
            state.ProposalId,
            state.CommandId,
            state.LeaseId,
            state.Attempt,
            state.SoulId,
            state.DeviceBindingId,
            state.PlatformAccountId,
            state.TraceId,
            state.IdempotencyKey,
            state.SubmissionIntentSha256,
            pendingStateSha256,
            request.Finding,
            request.EvidenceSha256,
            request.OccurredAt,
            request.ValidUntil,
            "internal",
            ZeroSignature);

        return await SignReconciliationAsync(
            unsigned,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionRecoveryV1>>
        CreateRecoveryAsync(
            VerifiedApprovalSubmissionState verifiedState,
            SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionReconciliationV1>
                reconciliationEnvelope,
            ControlPlaneRecoveryRequest request,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(verifiedState);
        ArgumentNullException.ThrowIfNull(reconciliationEnvelope);
        ArgumentNullException.ThrowIfNull(request);
        RequirePolicyAuthority(verifiedState);

        var state = verifiedState.Value;
        var reconciliation = VerifyReconciliationEnvelope(reconciliationEnvelope);
        if (state.State != ApprovalSubmissionStateV1.ReconciledNotSubmitted
            || reconciliation.Finding !=
                ApprovalSubmissionReconciliationV1.ConfirmedNotSubmitted
            || !ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                state.EvidenceSha256,
                reconciliationEnvelope.CommitmentSha256)
            || state.SubmissionAttemptId != reconciliation.SubmissionAttemptId
            || state.ApprovalId != reconciliation.ApprovalId
            || state.ProposalId != reconciliation.ProposalId
            || state.CommandId != reconciliation.CommandId
            || state.LeaseId != reconciliation.LeaseId
            || state.Attempt != reconciliation.Attempt
            || !string.Equals(state.SoulId, reconciliation.SoulId, StringComparison.Ordinal)
            || !string.Equals(
                state.DeviceBindingId,
                reconciliation.DeviceBindingId,
                StringComparison.Ordinal)
            || !string.Equals(
                state.PlatformAccountId,
                reconciliation.PlatformAccountId,
                StringComparison.Ordinal)
            || !string.Equals(state.TraceId, reconciliation.TraceId, StringComparison.Ordinal)
            || !string.Equals(
                state.IdempotencyKey,
                reconciliation.IdempotencyKey,
                StringComparison.Ordinal)
            || !ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                state.SubmissionIntentSha256,
                reconciliation.SubmissionIntentSha256))
        {
            throw new UnauthorizedAccessException(
                "Recovery requires the exact verified CONFIRMED_NOT_SUBMITTED reconciliation chain.");
        }

        var unsigned = new ApprovalSubmissionRecoveryV1(
            ApprovalSubmissionRecoveryV1.CurrentSchemaVersion,
            ApprovalSubmissionRecoveryV1.CurrentContractId,
            ApprovalSubmissionRecoveryV1.CurrentProducerModule,
            ApprovalSubmissionRecoveryV1.CurrentAuthScope,
            ApprovalSubmissionRecoveryV1.CurrentAuthorityRole,
            request.RecoveryId,
            state.SubmissionAttemptId,
            reconciliation.ReconciliationId,
            reconciliationEnvelope.CommitmentSha256,
            state.ApprovalId,
            state.ProposalId,
            state.CommandId,
            state.LeaseId,
            state.Attempt,
            request.NextSubmissionAttemptId,
            request.NextLeaseId,
            request.NextAttempt,
            state.SoulId,
            state.DeviceBindingId,
            state.PlatformAccountId,
            state.TraceId,
            state.IdempotencyKey,
            request.NextReleaseBomSha256,
            request.NextReleaseBomGeneration,
            request.NextExecutionAuthorizationSha256,
            request.NextNativeRequestBindingSha256,
            request.HumanApprovalId,
            request.OccurredAt,
            request.ValidUntil,
            "internal",
            ZeroSignature);

        return await SignRecoveryAsync(
            unsigned,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _reconciliationVerifier.Dispose();
        _recoveryVerifier.Dispose();
    }

    private async Task<SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionReconciliationV1>>
        SignReconciliationAsync(
            ApprovalSubmissionReconciliationV1 unsigned,
            CancellationToken cancellationToken)
    {
        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalReconciliationBytes(
            unsigned);
        try
        {
            var signature = await SignAndVerifyAsync(
                (payload, token) => _reconciliationSigner.SignReconciliationAsync(
                    unsigned,
                    payload,
                    token),
                _reconciliationVerifier,
                _reconciliationVerifierGate,
                _reconciliationAuthority,
                canonical,
                "independent reconciliation",
                cancellationToken).ConfigureAwait(false);
            try
            {
                var signed = unsigned with
                {
                    SignatureBase64 = Convert.ToBase64String(signature)
                };
                var wire = ApprovalSubmissionReconciliationV1Codec.Serialize(signed);
                _ = ApprovalSubmissionReconciliationV1Codec.Deserialize(wire);
                return new SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionReconciliationV1>(
                    signed,
                    wire,
                    ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(
                        signed));
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

    private ApprovalSubmissionReconciliationV1 VerifyReconciliationEnvelope(
        SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionReconciliationV1>
            envelope)
    {
        var wire = envelope.CopyWireBytes();
        ApprovalSubmissionReconciliationV1 decoded;
        try
        {
            decoded = ApprovalSubmissionReconciliationV1Codec.Deserialize(wire);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wire);
        }

        var commitment =
            ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(decoded);
        if (!Equals(decoded, envelope.Value)
            || !ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                commitment,
                envelope.CommitmentSha256))
        {
            throw new InvalidDataException(
                "Reconciliation envelope wire, value, and commitment are not identical.");
        }

        var canonical =
            ApprovalSubmissionLifecycleBinding.CanonicalReconciliationBytes(decoded);
        byte[]? signature = null;
        try
        {
            signature = Convert.FromBase64String(decoded.SignatureBase64);
            bool verified;
            lock (_reconciliationVerifierGate)
            {
                verified = _reconciliationVerifier.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            if (!verified)
                throw new UnauthorizedAccessException(
                    "Reconciliation envelope signature no longer matches its authority.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
        }

        return decoded;
    }

    private async Task<SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionRecoveryV1>>
        SignRecoveryAsync(
            ApprovalSubmissionRecoveryV1 unsigned,
            CancellationToken cancellationToken)
    {
        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalRecoveryBytes(unsigned);
        try
        {
            var signature = await SignAndVerifyAsync(
                (payload, token) => _recoverySigner.AuthorizeAndSignRecoveryAsync(
                    unsigned,
                    payload,
                    token),
                _recoveryVerifier,
                _recoveryVerifierGate,
                _recoveryAuthority,
                canonical,
                "human recovery authorization",
                cancellationToken).ConfigureAwait(false);
            try
            {
                var signed = unsigned with
                {
                    SignatureBase64 = Convert.ToBase64String(signature)
                };
                var wire = ApprovalSubmissionRecoveryV1Codec.Serialize(signed);
                _ = ApprovalSubmissionRecoveryV1Codec.Deserialize(wire);
                return new SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionRecoveryV1>(
                    signed,
                    wire,
                    ApprovalSubmissionLifecycleBinding.ComputeRecoverySha256(signed));
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

    private async Task<byte[]> SignAndVerifyAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> sign,
        ECDsa verifier,
        object verifierGate,
        AuthorityCallState authority,
        byte[] canonical,
        string authorityName,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_authorityTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        byte[]? authorityPayload = null;
        byte[]? signature = null;
        Task<byte[]>? authorityTask = null;
        var gateEntered = false;
        var cleanupTransferred = false;
        var signatureReturned = false;
        try
        {
            ThrowIfAuthorityQuarantined(authority, authorityName);
            await authority.Gate.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            gateEntered = true;
            ThrowIfAuthorityQuarantined(authority, authorityName);
            linkedSource.Token.ThrowIfCancellationRequested();

            authorityPayload = (byte[])canonical.Clone();
            authorityTask = sign(authorityPayload, linkedSource.Token)
                ?? throw new InvalidOperationException(
                    $"{authorityName} signer returned no task.");
            signature = await authorityTask.WaitAsync(
                linkedSource.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"{authorityName} signer returned no signature.");
            linkedSource.Token.ThrowIfCancellationRequested();
            if (signature.Length != 64)
            {
                throw new InvalidOperationException(
                    $"{authorityName} signer returned a non-P1363 signature.");
            }

            bool verified;
            lock (verifierGate)
            {
                verified = verifier.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            if (!verified)
            {
                throw new UnauthorizedAccessException(
                    $"{authorityName} signer did not produce a signature from its declared public key.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            signatureReturned = true;
            return signature;
        }
        catch (OperationCanceledException exception)
        {
            if (authorityTask is not null && authorityPayload is not null)
            {
                Interlocked.Exchange(ref authority.Quarantined, 1);
                TransferLateAuthorityCleanup(
                    authorityTask,
                    authorityPayload,
                    authority.Gate);
                cleanupTransferred = true;
                gateEntered = false;
                authorityPayload = null;
                signature = null;
            }

            if (!cancellationToken.IsCancellationRequested
                && timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{authorityName} exceeded the fixed lifecycle authority timeout and was quarantined.",
                    exception);
            }

            throw;
        }
        finally
        {
            if (!cleanupTransferred && authorityPayload is not null)
                CryptographicOperations.ZeroMemory(authorityPayload);
            if (!cleanupTransferred && !signatureReturned && signature is not null)
                CryptographicOperations.ZeroMemory(signature);
            if (gateEntered)
                authority.Gate.Release();
        }
    }

    private static void ThrowIfAuthorityQuarantined(
        AuthorityCallState authority,
        string authorityName)
    {
        if (Volatile.Read(ref authority.Quarantined) != 0)
            throw new InvalidOperationException(
                $"{authorityName} is quarantined after an indeterminate cancellation or timeout.");
    }

    private static void TransferLateAuthorityCleanup(
        Task<byte[]> authorityTask,
        byte[] authorityPayload,
        SemaphoreSlim authorityGate)
    {
        _ = authorityTask.ContinueWith(
            static (completed, state) =>
            {
                var cleanup = (LateAuthorityCleanup)state!;
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        var lateSignature = completed.GetAwaiter().GetResult();
                        if (lateSignature is not null)
                            CryptographicOperations.ZeroMemory(lateSignature);
                    }
                    else if (completed.IsFaulted)
                    {
                        _ = completed.Exception;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(cleanup.Payload);
                    cleanup.Gate.Release();
                }
            },
            new LateAuthorityCleanup(authorityPayload, authorityGate),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class AuthorityCallState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);

        internal int Quarantined;
    }

    private sealed record LateAuthorityCleanup(
        byte[] Payload,
        SemaphoreSlim Gate);

    private void RequirePolicyAuthority(VerifiedApprovalSubmissionState verifiedState)
    {
        if (!ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                verifiedState.AuthorityFingerprintSha256,
                _policyStateAuthorityFingerprintSha256))
        {
            throw new UnauthorizedAccessException(
                "Policy submission state was verified by a different authority topology.");
        }
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value is null || value.Length != 64)
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        try
        {
            var bytes = Convert.FromHexString(value);
            try
            {
                if (!string.Equals(
                        Convert.ToHexStringLower(bytes),
                        value,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "A lowercase SHA-256 digest is required.",
                        parameterName);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A lowercase SHA-256 digest is required.",
                parameterName,
                exception);
        }
    }
}

public sealed class ControlPlaneSubmissionLifecycleCoordinator
{
    private static readonly TimeSpan MaximumPortTimeout = TimeSpan.FromSeconds(5);

    private readonly ControlPlaneSubmissionStateConsumer _stateConsumer;
    private readonly ControlPlaneSubmissionLifecycleProducer _producer;
    private readonly IApprovalSubmissionReconciliationPort _reconciliationPort;
    private readonly IApprovalSubmissionRecoveryPort _recoveryPort;
    private readonly TimeSpan _portTimeout;

    public ControlPlaneSubmissionLifecycleCoordinator(
        ControlPlaneSubmissionStateConsumer stateConsumer,
        ControlPlaneSubmissionLifecycleProducer producer,
        IApprovalSubmissionReconciliationPort reconciliationPort,
        IApprovalSubmissionRecoveryPort recoveryPort,
        TimeSpan? portTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(stateConsumer);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(reconciliationPort);
        ArgumentNullException.ThrowIfNull(recoveryPort);
        if (ReferenceEquals(reconciliationPort, recoveryPort))
            throw new InvalidOperationException(
                "Reconciliation and recovery must use separate authenticated policy ports.");
        RequirePortIdentity(
            reconciliationPort.AuthScope,
            ApprovalSubmissionReconciliationV1.CurrentAuthScope,
            reconciliationPort.CredentialAuthorityFingerprintSha256,
            nameof(reconciliationPort));
        RequirePortIdentity(
            recoveryPort.AuthScope,
            ApprovalSubmissionRecoveryV1.CurrentAuthScope,
            recoveryPort.CredentialAuthorityFingerprintSha256,
            nameof(recoveryPort));
        if (ControlPlaneSubmissionStateConsumer.FixedDigestEquals(
                reconciliationPort.CredentialAuthorityFingerprintSha256,
                recoveryPort.CredentialAuthorityFingerprintSha256))
        {
            throw new InvalidOperationException(
                "Reconciliation and recovery ports require distinct credential authorities.");
        }

        var effectiveTimeout = portTimeout ?? MaximumPortTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > MaximumPortTimeout)
            throw new ArgumentOutOfRangeException(
                nameof(portTimeout),
                "Lifecycle port timeout must be positive and no greater than five seconds.");

        _stateConsumer = stateConsumer;
        _producer = producer;
        _reconciliationPort = reconciliationPort;
        _recoveryPort = recoveryPort;
        _portTimeout = effectiveTimeout;
    }

    public async Task<ControlPlaneReconciliationExchange> ReconcileAsync(
        ReadOnlyMemory<byte> currentStateWire,
        ApprovalSubmissionStateExpectation currentStateExpectation,
        ControlPlaneReconciliationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _stateConsumer.Consume(
            currentStateWire.Span,
            currentStateExpectation);
        var envelope = await _producer.CreateReconciliationAsync(
            current,
            request,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var responseWire = await InvokePortAsync(
            token => _reconciliationPort.ReconcileAsync(
                envelope.CopyWireBytes(),
                token),
            "Policy reconciliation",
            cancellationToken).ConfigureAwait(false);
        if (responseWire.IsEmpty)
            throw new InvalidDataException(
                "Policy reconciliation returned no canonical state wire.");

        var expectedState = request.Finding switch
        {
            ApprovalSubmissionReconciliationV1.ConfirmedNotSubmitted
                => ApprovalSubmissionStateV1.ReconciledNotSubmitted,
            ApprovalSubmissionReconciliationV1.ConfirmedSubmitted
                => ApprovalSubmissionStateV1.ReconciledSubmitted,
            _ => throw new NotSupportedException(
                "Unknown reconciliation finding fails closed.")
        };
        var responseExpectation = TransitionExpectation(
            current.Value,
            expectedState,
            current.Value.StateSha256,
            envelope.CommitmentSha256);
        var response = _stateConsumer.Consume(
            responseWire.Span,
            responseExpectation);
        return new ControlPlaneReconciliationExchange(envelope, response);
    }

    public async Task<ControlPlaneRecoveryExchange> RecoverAsync(
        ControlPlaneReconciliationExchange reconciliation,
        ControlPlaneRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reconciliation);
        var envelope = await _producer.CreateRecoveryAsync(
            reconciliation.State,
            reconciliation.Request,
            request,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var responseWire = await InvokePortAsync(
            token => _recoveryPort.RecoverAsync(
                envelope.CopyWireBytes(),
                token),
            "Policy recovery",
            cancellationToken).ConfigureAwait(false);
        if (responseWire.IsEmpty)
            throw new InvalidDataException(
                "Policy recovery returned no canonical state wire.");

        var responseExpectation = TransitionExpectation(
            reconciliation.State.Value,
            ApprovalSubmissionStateV1.RecoveryAuthorized,
            reconciliation.State.Value.StateSha256,
            envelope.CommitmentSha256);
        var response = _stateConsumer.Consume(
            responseWire.Span,
            responseExpectation);
        return new ControlPlaneRecoveryExchange(envelope, response);
    }

    private async Task<ReadOnlyMemory<byte>> InvokePortAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> invoke,
        string operation,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_portTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            linkedSource.Token.ThrowIfCancellationRequested();
            var task = invoke(linkedSource.Token)
                ?? throw new InvalidOperationException(
                    $"{operation} port returned no task.");
            var response = await task.WaitAsync(
                linkedSource.Token).ConfigureAwait(false);
            linkedSource.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested
                && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{operation} exceeded the fixed lifecycle port timeout.",
                exception);
        }
    }

    private static void RequirePortIdentity(
        string authScope,
        string expectedAuthScope,
        string credentialAuthorityFingerprintSha256,
        string parameterName)
    {
        if (!string.Equals(authScope, expectedAuthScope, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                $"{parameterName} does not expose the exact Policy auth scope.");
        if (credentialAuthorityFingerprintSha256 is null
            || credentialAuthorityFingerprintSha256.Length != 64)
        {
            throw new ArgumentException(
                "A lowercase SHA-256 credential-authority fingerprint is required.",
                parameterName);
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(credentialAuthorityFingerprintSha256);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A lowercase SHA-256 credential-authority fingerprint is required.",
                parameterName,
                exception);
        }

        try
        {
            if (!string.Equals(
                    Convert.ToHexStringLower(decoded),
                    credentialAuthorityFingerprintSha256,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A lowercase SHA-256 credential-authority fingerprint is required.",
                    parameterName);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static ApprovalSubmissionStateExpectation TransitionExpectation(
        ApprovalSubmissionStateV1 prior,
        string state,
        string predecessorStateSha256,
        string evidenceSha256)
        => new(
            prior.SubmissionAttemptId,
            prior.ApprovalId,
            prior.ProposalId,
            prior.CommandId,
            prior.LeaseId,
            prior.Attempt,
            prior.SoulId,
            prior.DeviceBindingId,
            prior.PlatformAccountId,
            prior.TraceId,
            prior.IdempotencyKey,
            prior.ReleaseBomSha256,
            prior.ReleaseBomGeneration,
            prior.NativeRequestBindingSha256,
            prior.SubmissionIntentSha256,
            state,
            predecessorStateSha256,
            evidenceSha256);
}
