using System.Security.Cryptography;
using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;

namespace Dps.PolicyApproval;

public sealed record PolicyEvaluationEnvelope(
    string CallerModule,
    string AuthScope,
    Guid ProposalId,
    string ProposalSha256,
    string ReleaseBomSha256,
    DateTimeOffset ValidUntil,
    string SignatureBase64,
    string RequestedMode = PolicyEvaluationEnvelope.Shadow,
    ActionExecutionPromotionV1? ExecutionPromotion = null)
{
    public const string Shadow = "SHADOW";
    public const string Execute = "EXECUTE";
}

public sealed record PolicyRuntimeState(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string PolicyVersion,
    IReadOnlySet<string> EnabledPolicyIds,
    bool KillSwitchEnabled,
    int RemainingRateBudget,
    bool PlatformAuthorized,
    string? PlatformAuthorizationId,
    DateTimeOffset EvaluatedAt,
    bool ExecutionEnabled = false,
    string ReleaseBomSha256 = "",
    long RuntimeRevision = 0,
    string RuntimeStateSha256 = "",
    DateTimeOffset RuntimeValidUntil = default);

public sealed record VerifiedPolicyEvaluationContext(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string PolicyVersion,
    IReadOnlySet<string> EnabledPolicyIds,
    bool KillSwitchEnabled,
    int RemainingRateBudget,
    bool PlatformAuthorized,
    string? PlatformAuthorizationId,
    DateTimeOffset EvaluatedAt,
    string TrustEvidenceSha256,
    bool ExecutionAuthorized = false,
    string? ExecutionPromotionSha256 = null,
    string ReleaseBomSha256 = "",
    long RuntimeRevision = 0,
    string RuntimeStateSha256 = "",
    DateTimeOffset AuthorizationValidUntil = default,
    DateTimeOffset RuntimeValidUntil = default);

public interface IPolicyTrustProvider
{
    ValueTask<VerifiedPolicyEvaluationContext> ResolveVerifiedContextAsync(
        ActionProposalV1 proposal,
        PolicyEvaluationEnvelope envelope,
        CancellationToken cancellationToken);
}

public interface IPolicyRuntimeStateSource
{
    ValueTask<PolicyRuntimeState> ReadVerifiedStateAsync(ActionProposalV1 proposal, CancellationToken cancellationToken);
}

internal sealed record VerifiedPolicyEvaluationResult(
    ApprovalDecisionV1 Decision,
    VerifiedPolicyEvaluationContext Context);

internal static class PolicyEcdsaGuard
{
    internal const int P256SignatureByteLength = 64;
    private const int P256SignatureBase64Length = 88;
    private const int MaximumSubjectPublicKeyInfoLength = 512;

    internal static void ImportP256SubjectPublicKeyInfo(
        ECDsa key,
        ReadOnlySpan<byte> subjectPublicKeyInfo,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (subjectPublicKeyInfo.Length is < 1 or > MaximumSubjectPublicKeyInfoLength)
            throw new ArgumentException("ECDSA SubjectPublicKeyInfo must be a bounded P-256 key.", parameterName);
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
        if (bytesRead != subjectPublicKeyInfo.Length)
            throw new ArgumentException("ECDSA SubjectPublicKeyInfo contains trailing bytes.", parameterName);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (key.KeySize != 256
            || !string.Equals(
                parameters.Curve.Oid.Value,
                ECCurve.NamedCurves.nistP256.Oid.Value,
                StringComparison.Ordinal))
            throw new ArgumentException("Only the NIST P-256 ECDSA curve is accepted.", parameterName);
    }

    internal static byte[] DecodeCanonicalP1363Signature(string? signatureBase64)
    {
        if (signatureBase64 is null || signatureBase64.Length != P256SignatureBase64Length)
            throw new UnauthorizedAccessException("ECDSA signature must be canonical Base64 for a 64-byte P-256 P1363 signature.");
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64); }
        catch (FormatException exception)
        {
            throw new UnauthorizedAccessException("ECDSA signature is not valid canonical Base64.", exception);
        }
        if (signature.Length != P256SignatureByteLength
            || !string.Equals(Convert.ToBase64String(signature), signatureBase64, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(signature);
            throw new UnauthorizedAccessException("ECDSA signature is not canonical P-256 P1363.");
        }
        return signature;
    }

    internal static void RequireDistinctPublicKeys(
        ECDsa first,
        string firstName,
        ECDsa second,
        string secondName)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        byte[]? firstEncoding = null;
        byte[]? secondEncoding = null;
        byte[]? firstFingerprint = null;
        byte[]? secondFingerprint = null;
        try
        {
            firstEncoding = first.ExportSubjectPublicKeyInfo();
            secondEncoding = second.ExportSubjectPublicKeyInfo();
            firstFingerprint = SHA256.HashData(firstEncoding);
            secondFingerprint = SHA256.HashData(secondEncoding);
            if (CryptographicOperations.FixedTimeEquals(
                    firstFingerprint,
                    secondFingerprint))
            {
                throw new ArgumentException(
                    $"{firstName} and {secondName} must use distinct P-256 authorities.",
                    secondName);
            }
        }
        finally
        {
            if (firstEncoding is not null)
                CryptographicOperations.ZeroMemory(firstEncoding);
            if (secondEncoding is not null)
                CryptographicOperations.ZeroMemory(secondEncoding);
            if (firstFingerprint is not null)
                CryptographicOperations.ZeroMemory(firstFingerprint);
            if (secondFingerprint is not null)
                CryptographicOperations.ZeroMemory(secondFingerprint);
        }
    }
}

public static class PolicyAuthorizationBinding
{
    public const string ProposalDomain = "dps.policy-approval.action-proposal-sha256/v1";

    public static string ComputeProposalSha256(ActionProposalV1 proposal)
    {
        var snapshot = PolicyCanonicalization.SnapshotProposal(proposal);
        return PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field(ProposalDomain);
            writer.Field(snapshot.SchemaVersion);
            writer.Field(snapshot.ContractId);
            writer.Field(snapshot.ProducerModule);
            writer.Field(snapshot.ProposalId);
            writer.Field(snapshot.SoulId);
            writer.Field(snapshot.DeviceBindingId);
            writer.Field(snapshot.PlatformAccountId);
            writer.Field(snapshot.TraceId);
            writer.Field(snapshot.IdempotencyKey);
            writer.Field(snapshot.OccurredAt);
            writer.Field(snapshot.PrivacyClass);
            writer.Field(snapshot.ActionKind);
            writer.Field(snapshot.IsSideEffect);
            writer.Field(snapshot.ShadowOnly);
            writer.Field(snapshot.Parameters.Count);
            foreach (var pair in snapshot.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.Field(pair.Key);
                writer.Field(pair.Value);
            }
            writer.Field(snapshot.EvidenceRefs.Count);
            foreach (var evidenceReference in snapshot.EvidenceRefs.Order(StringComparer.Ordinal))
            {
                writer.Field(evidenceReference);
            }
        });
    }
}

public sealed class DeterministicPolicyEvaluator
{
    private readonly IPolicyTrustProvider _trustProvider;

    public DeterministicPolicyEvaluator(IPolicyTrustProvider trustProvider)
    {
        ArgumentNullException.ThrowIfNull(trustProvider);
        _trustProvider = trustProvider;
    }

    public static readonly IReadOnlySet<string> KnownPolicies = new HashSet<string>(
        ["SOUL-ISO-001", "CMD-IDEMP-001", "RESULT-VERIFY-001", "GBRAIN-READBACK-001", "EDGE-NORESTART-001"],
        StringComparer.Ordinal);

    public async ValueTask<ApprovalDecisionV1> EvaluateAsync(
        ActionProposalV1 proposal,
        PolicyEvaluationEnvelope envelope,
        CancellationToken cancellationToken = default)
        => (await EvaluateVerifiedAsync(proposal, envelope, cancellationToken)).Decision;

    internal async ValueTask<VerifiedPolicyEvaluationResult> EvaluateVerifiedAsync(
        ActionProposalV1 proposal,
        PolicyEvaluationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(envelope);
        proposal = PolicyCanonicalization.SnapshotProposal(proposal);
        if (string.IsNullOrWhiteSpace(envelope.SignatureBase64)) throw new UnauthorizedAccessException("Authenticated Control Plane authorization is required.");
        var context = await _trustProvider.ResolveVerifiedContextAsync(proposal, envelope, cancellationToken);
        ArgumentNullException.ThrowIfNull(context);
        try { PolicyCanonicalization.RequireSha256(context.TrustEvidenceSha256, nameof(context.TrustEvidenceSha256)); }
        catch (ArgumentException exception) { throw new UnauthorizedAccessException("Trusted policy context evidence is invalid.", exception); }
        if ((envelope.ExecutionPromotion is null) != (context.ExecutionPromotionSha256 is null))
            throw new UnauthorizedAccessException("Trusted policy context promotion evidence does not match the signed envelope.");
        if (context.ExecutionPromotionSha256 is not null)
        {
            try { PolicyCanonicalization.RequireSha256(context.ExecutionPromotionSha256, nameof(context.ExecutionPromotionSha256)); }
            catch (ArgumentException exception) { throw new UnauthorizedAccessException("Trusted execution-promotion evidence is invalid.", exception); }
        }
        if (context.ExecutionAuthorized && envelope.ExecutionPromotion is null)
            throw new UnauthorizedAccessException("Execution cannot be authorized without an independent promotion contract.");
        ApprovalContractGuard.RequireUtc(context.EvaluatedAt, nameof(context.EvaluatedAt));
        if (!string.Equals(proposal.SoulId, context.SoulId, StringComparison.Ordinal) || !string.Equals(proposal.DeviceBindingId, context.DeviceBindingId, StringComparison.Ordinal) || !string.Equals(proposal.PlatformAccountId, context.PlatformAccountId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("SOUL-ISO-001: proposal and policy scope differ.");
        var enabledPolicies = new HashSet<string>(context.EnabledPolicyIds, StringComparer.Ordinal);
        if (enabledPolicies.Count != context.EnabledPolicyIds.Count) throw new UnauthorizedAccessException("Trusted policy context exposes inconsistent policy cardinality.");
        if (enabledPolicies.Any(policy => !KnownPolicies.Contains(policy))) throw new NotSupportedException("Unknown policy ID fails closed.");

        var reasons = new List<string>();
        if (!KnownPolicies.IsSubsetOf(enabledPolicies)) reasons.Add("REQUIRED_POLICY_DISABLED");
        var promoted = context.ExecutionAuthorized && envelope.ExecutionPromotion is not null;
        if (proposal.ShadowOnly && !promoted) reasons.Add("SHADOW_ONLY");
        if (context.KillSwitchEnabled) reasons.Add("KILL_SWITCH_ACTIVE");
        if (context.RemainingRateBudget <= 0) reasons.Add("RATE_BUDGET_EXHAUSTED");
        if (proposal.IsSideEffect && (!context.PlatformAuthorized || string.IsNullOrWhiteSpace(context.PlatformAuthorizationId))) reasons.Add("PLATFORM_AUTHORIZATION_REQUIRED");
        var decision = reasons.Count == 0 ? ApprovalDecisionV1.Approved : ApprovalDecisionV1.Denied;
        var approval = new ApprovalDecisionV1(
            ApprovalDecisionV1.CurrentSchemaVersion,
            ApprovalDecisionV1.CurrentContractId,
            ApprovalDecisionV1.CurrentProducerModule,
            DeterministicApprovalId(proposal, context, decision),
            proposal.ProposalId,
            proposal.SoulId,
            proposal.DeviceBindingId,
            proposal.PlatformAccountId,
            proposal.TraceId,
            proposal.IdempotencyKey,
            context.EvaluatedAt,
            "internal",
            proposal.ActionKind,
            proposal.IsSideEffect,
            proposal.ShadowOnly && !promoted,
            new Dictionary<string, string>(proposal.Parameters, StringComparer.Ordinal),
            decision,
            ApprovalDecisionV1.DeterministicAuthority,
            context.PolicyVersion,
            enabledPolicies.Order(StringComparer.Ordinal).ToArray(),
            context.PlatformAuthorized ? context.PlatformAuthorizationId : null,
            reasons.Order(StringComparer.Ordinal).ToArray());
        approval.Validate();
        return new VerifiedPolicyEvaluationResult(
            PolicyCanonicalization.SnapshotDecision(approval),
            context);
    }

    private static Guid DeterministicApprovalId(
        ActionProposalV1 proposal,
        VerifiedPolicyEvaluationContext context,
        string decision)
    {
        var digestHex = PolicyCanonicalHash.Compute(writer =>
        {
            writer.Field("dps.policy-approval.approval-id/v1");
            writer.Field(proposal.ProposalId);
            writer.Field(proposal.SoulId);
            writer.Field(proposal.DeviceBindingId);
            writer.Field(proposal.PlatformAccountId);
            writer.Field(proposal.TraceId);
            writer.Field(proposal.IdempotencyKey);
            writer.Field(context.PolicyVersion);
            writer.Field(context.TrustEvidenceSha256);
            writer.Field(context.ExecutionAuthorized);
            writer.NullableField(context.ExecutionPromotionSha256);
            writer.NullableField(context.PlatformAuthorizationId);
            writer.Field(decision);
        });
        var digest = Convert.FromHexString(digestHex);
        try { return new Guid(digest.AsSpan(0, 16)); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }
}

public sealed class EcdsaPolicyTrustProvider : IPolicyTrustProvider, IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _publicKey;
    private readonly ECDsa _promotionPublicKey;
    private readonly IPolicyRuntimeStateSource _runtimeStateSource;

    public EcdsaPolicyTrustProvider(
        ReadOnlySpan<byte> subjectPublicKeyInfo,
        ReadOnlySpan<byte> promotionSubjectPublicKeyInfo,
        IPolicyRuntimeStateSource runtimeStateSource)
    {
        ArgumentNullException.ThrowIfNull(runtimeStateSource);
        _runtimeStateSource = runtimeStateSource;
        _publicKey = ECDsa.Create();
        _promotionPublicKey = ECDsa.Create();
        try
        {
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                _publicKey, subjectPublicKeyInfo, nameof(subjectPublicKeyInfo));
            PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(
                _promotionPublicKey,
                promotionSubjectPublicKeyInfo,
                nameof(promotionSubjectPublicKeyInfo));
            PolicyEcdsaGuard.RequireDistinctPublicKeys(
                _publicKey,
                nameof(subjectPublicKeyInfo),
                _promotionPublicKey,
                nameof(promotionSubjectPublicKeyInfo));
        }
        catch
        {
            _publicKey.Dispose();
            _promotionPublicKey.Dispose();
            throw;
        }
    }

    public async ValueTask<VerifiedPolicyEvaluationContext> ResolveVerifiedContextAsync(ActionProposalV1 proposal, PolicyEvaluationEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        proposal = PolicyCanonicalization.SnapshotProposal(proposal);
        ApprovalContractGuard.RequireUtc(envelope.ValidUntil, nameof(envelope.ValidUntil));
        try
        {
            PolicyCanonicalization.RequireSha256(envelope.ProposalSha256, nameof(envelope.ProposalSha256));
            PolicyCanonicalization.RequireSha256(envelope.ReleaseBomSha256, nameof(envelope.ReleaseBomSha256));
            PolicyCanonicalization.RequireStrictUtf8(envelope.CallerModule, envelope.AuthScope, envelope.RequestedMode);
        }
        catch (ArgumentException exception) { throw new UnauthorizedAccessException("Policy envelope commitments are invalid.", exception); }
        if (envelope.RequestedMode is not (PolicyEvaluationEnvelope.Shadow or PolicyEvaluationEnvelope.Execute)) throw new UnauthorizedAccessException("Unknown policy execution mode fails closed.");
        if (!string.Equals(envelope.CallerModule, "control-plane-host", StringComparison.Ordinal)
            || !string.Equals(envelope.AuthScope, "policy:evaluate", StringComparison.Ordinal)
            || envelope.ProposalId != proposal.ProposalId
            || envelope.ValidUntil <= proposal.OccurredAt)
            throw new UnauthorizedAccessException("Policy envelope caller, scope, proposal, or lifetime is invalid.");
        if (!FixedSha256Equals(envelope.ProposalSha256, PolicyAuthorizationBinding.ComputeProposalSha256(proposal))) throw new UnauthorizedAccessException("Policy envelope is not bound to the exact proposal payload.");
        byte[]? signature = null;
        byte[]? canonical = null;
        try
        {
            signature = PolicyEcdsaGuard.DecodeCanonicalP1363Signature(envelope.SignatureBase64);
            canonical = CanonicalBytes(envelope);
            bool verified;
            lock (_sync)
                verified = _publicKey.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!verified) throw new UnauthorizedAccessException("Policy authorization signature verification failed.");
            var state = await _runtimeStateSource.ReadVerifiedStateAsync(proposal, cancellationToken);
            ArgumentNullException.ThrowIfNull(state);
            ApprovalContractGuard.RequireUtc(state.EvaluatedAt, nameof(state.EvaluatedAt));
            ApprovalContractGuard.RequireUtc(state.RuntimeValidUntil, nameof(state.RuntimeValidUntil));
            PolicyCanonicalization.RequireSha256(state.ReleaseBomSha256, nameof(state.ReleaseBomSha256));
            PolicyCanonicalization.RequireSha256(state.RuntimeStateSha256, nameof(state.RuntimeStateSha256));
            if (state.RuntimeRevision <= 0) throw new UnauthorizedAccessException("Trusted policy runtime revision is invalid.");
            if (state.EvaluatedAt >= envelope.ValidUntil)
                throw new UnauthorizedAccessException("Policy authorization expired before trusted database time.");
            if (state.EvaluatedAt >= state.RuntimeValidUntil)
                throw new UnauthorizedAccessException("Policy runtime state expired before trusted database time.");
            if (!FixedSha256Equals(envelope.ReleaseBomSha256, state.ReleaseBomSha256)) throw new UnauthorizedAccessException("The signed release BOM is not the active policy runtime BOM.");
            var policies = new HashSet<string>(state.EnabledPolicyIds, StringComparer.Ordinal);
            if (policies.Count != state.EnabledPolicyIds.Count) throw new UnauthorizedAccessException("Trusted policy state exposes inconsistent policy cardinality.");
            string? executionPromotionSha256 = null;
            var authorizationValidUntil = envelope.ValidUntil;
            if (envelope.RequestedMode == PolicyEvaluationEnvelope.Shadow
                && envelope.ExecutionPromotion is not null)
                throw new UnauthorizedAccessException("A SHADOW evaluation cannot carry an execution promotion.");
            if (envelope.RequestedMode == PolicyEvaluationEnvelope.Execute
                && envelope.ExecutionPromotion is not null)
            {
                executionPromotionSha256 = VerifyExecutionPromotion(
                    proposal,
                    envelope,
                    state,
                    envelope.ExecutionPromotion);
                if (envelope.ExecutionPromotion.ValidUntil < authorizationValidUntil)
                    authorizationValidUntil = envelope.ExecutionPromotion.ValidUntil;
            }
            var executionAuthorized = envelope.RequestedMode == PolicyEvaluationEnvelope.Execute
                && executionPromotionSha256 is not null
                && state.ExecutionEnabled;
            var evidence = PolicyCanonicalHash.Compute(writer =>
            {
                writer.Field("dps.policy-approval.trust-evidence-sha256/v1");
                writer.Field(canonical);
                writer.Field(state.SoulId);
                writer.Field(state.DeviceBindingId);
                writer.Field(state.PlatformAccountId);
                writer.Field(state.PolicyVersion);
                writer.Field(policies.Count);
                foreach (var policy in policies.Order(StringComparer.Ordinal)) writer.Field(policy);
                writer.Field(state.KillSwitchEnabled);
                writer.Field(state.RemainingRateBudget);
                writer.Field(state.PlatformAuthorized);
                writer.NullableField(state.PlatformAuthorizationId);
                writer.Field(state.EvaluatedAt);
                writer.Field(state.ExecutionEnabled);
                writer.Field(state.ReleaseBomSha256);
                writer.Field(state.RuntimeRevision);
                writer.Field(state.RuntimeStateSha256);
                writer.Field(state.RuntimeValidUntil);
                writer.Field(executionAuthorized);
                writer.NullableField(executionPromotionSha256);
            });
            return new VerifiedPolicyEvaluationContext(
                state.SoulId,
                state.DeviceBindingId,
                state.PlatformAccountId,
                state.PolicyVersion,
                policies,
                state.KillSwitchEnabled,
                state.RemainingRateBudget,
                state.PlatformAuthorized,
                state.PlatformAuthorizationId,
                state.EvaluatedAt,
                evidence,
                executionAuthorized,
                executionPromotionSha256,
                state.ReleaseBomSha256,
                state.RuntimeRevision,
                state.RuntimeStateSha256,
                authorizationValidUntil,
                state.RuntimeValidUntil);
        }
        finally
        {
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (canonical is not null) CryptographicOperations.ZeroMemory(canonical);
        }
    }

    public static byte[] CanonicalBytes(PolicyEvaluationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return PolicyCanonicalHash.Bytes(writer =>
        {
            writer.Field("dps.policy-approval.evaluation-envelope/v1");
            writer.Field(envelope.CallerModule);
            writer.Field(envelope.AuthScope);
            writer.Field(envelope.ProposalId);
            writer.Field(envelope.ProposalSha256);
            writer.Field(envelope.ReleaseBomSha256);
            writer.Field(envelope.ValidUntil);
            writer.Field(envelope.RequestedMode);
            writer.NullableField(
                envelope.ExecutionPromotion is null
                    ? null
                        : ActionExecutionPromotionV1Canonical.ComputeSignedSha256(envelope.ExecutionPromotion));
        });
    }

    private string VerifyExecutionPromotion(
        ActionProposalV1 proposal,
        PolicyEvaluationEnvelope envelope,
        PolicyRuntimeState state,
        ActionExecutionPromotionV1 promotion)
    {
        try { promotion.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new UnauthorizedAccessException("Execution promotion contract is invalid.", exception);
        }
        var expectedProposalSha256 = PolicyAuthorizationBinding.ComputeProposalSha256(proposal);
        if (promotion.ProposalId != proposal.ProposalId
            || !string.Equals(promotion.SoulId, proposal.SoulId, StringComparison.Ordinal)
            || !string.Equals(promotion.DeviceBindingId, proposal.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(promotion.PlatformAccountId, proposal.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(promotion.TraceId, proposal.TraceId, StringComparison.Ordinal)
            || !string.Equals(promotion.IdempotencyKey, proposal.IdempotencyKey, StringComparison.Ordinal)
            || !FixedSha256Equals(promotion.ProposalSha256, expectedProposalSha256)
            || !FixedSha256Equals(promotion.ProposalSha256, envelope.ProposalSha256)
            || !FixedSha256Equals(promotion.ReleaseBomSha256, envelope.ReleaseBomSha256)
            || !FixedSha256Equals(promotion.ReleaseBomSha256, state.ReleaseBomSha256)
            || promotion.ExpectedRuntimeRevision != state.RuntimeRevision
            || promotion.OccurredAt < proposal.OccurredAt
            || promotion.OccurredAt > state.EvaluatedAt
            || state.EvaluatedAt >= promotion.ValidUntil)
            throw new UnauthorizedAccessException("Execution promotion is not bound to the exact proposal, runtime generation, database time, and Release BOM.");

        byte[]? signature = null;
        byte[]? canonical = null;
        try
        {
            signature = PolicyEcdsaGuard.DecodeCanonicalP1363Signature(promotion.SignatureBase64);
            canonical = ActionExecutionPromotionV1Canonical.CanonicalBytes(promotion);
            bool verified;
            lock (_sync)
                verified = _promotionPublicKey.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!verified)
                throw new UnauthorizedAccessException("Independent execution-promotion signature verification failed.");
            return ActionExecutionPromotionV1Canonical.ComputeSignedSha256(promotion);
        }
        finally
        {
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (canonical is not null) CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static bool FixedSha256Equals(string left, string right)
    {
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException) { return false; }
        finally
        {
            if (leftBytes is not null) CryptographicOperations.ZeroMemory(leftBytes);
            if (rightBytes is not null) CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    public void Dispose()
    {
        _publicKey.Dispose();
        _promotionPublicKey.Dispose();
    }
}
