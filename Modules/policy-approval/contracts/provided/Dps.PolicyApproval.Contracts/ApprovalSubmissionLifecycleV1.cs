using System.Text.Json.Serialization;

namespace Dps.PolicyApproval.Contracts;

public sealed record ApprovalSubmissionIntentV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("fence_request_sha256")] string FenceRequestSha256,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("approval_sha256")] string ApprovalSha256,
    [property: JsonPropertyName("proposal_sha256")] string ProposalSha256,
    [property: JsonPropertyName("status_revision")] long StatusRevision,
    [property: JsonPropertyName("runtime_revision")] long RuntimeRevision,
    [property: JsonPropertyName("runtime_state_sha256")] string RuntimeStateSha256,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("execution_authorization_sha256")] string ExecutionAuthorizationSha256,
    [property: JsonPropertyName("native_request_binding_sha256")] string NativeRequestBindingSha256,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("signature_base64")] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.submission.intent/v1";
    public const string CurrentProducerModule = "executor-gateway";
    public const string CurrentAuthScope = "approval:submission:begin";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        SubmissionLifecycleGuard.RequireHeader(SchemaVersion, ContractId, CurrentContractId, ProducerModule, CurrentProducerModule, AuthScope, CurrentAuthScope);
        ApprovalContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        ApprovalContractGuard.RequireSha256(FenceRequestSha256, nameof(FenceRequestSha256));
        SubmissionLifecycleGuard.RequireAttemptBinding(ApprovalId, ProposalId, CommandId, LeaseId, Attempt, SoulId, DeviceBindingId, PlatformAccountId, TraceId, IdempotencyKey);
        ApprovalContractGuard.RequireSha256(ApprovalSha256, nameof(ApprovalSha256));
        ApprovalContractGuard.RequireSha256(ProposalSha256, nameof(ProposalSha256));
        SubmissionLifecycleGuard.RequirePositive(StatusRevision, nameof(StatusRevision));
        SubmissionLifecycleGuard.RequirePositive(RuntimeRevision, nameof(RuntimeRevision));
        ApprovalContractGuard.RequireSha256(RuntimeStateSha256, nameof(RuntimeStateSha256));
        ApprovalContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        SubmissionLifecycleGuard.RequirePositive(ReleaseBomGeneration, nameof(ReleaseBomGeneration));
        ApprovalContractGuard.RequireSha256(ExecutionAuthorizationSha256, nameof(ExecutionAuthorizationSha256));
        ApprovalContractGuard.RequireSha256(NativeRequestBindingSha256, nameof(NativeRequestBindingSha256));
        SubmissionLifecycleGuard.RequireWindow(OccurredAt, ValidUntil, MaximumLifetime);
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        ApprovalContractGuard.RequireP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }
}

public sealed record ApprovalSubmissionAcknowledgementV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("acknowledgement_id")] Guid AcknowledgementId,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("native_request_binding_sha256")] string NativeRequestBindingSha256,
    [property: JsonPropertyName("submission_intent_sha256")] string SubmissionIntentSha256,
    [property: JsonPropertyName("pending_state_sha256")] string PendingStateSha256,
    [property: JsonPropertyName("submitted_request_sha256")] string SubmittedRequestSha256,
    [property: JsonPropertyName("native_submission_id")] Guid NativeSubmissionId,
    [property: JsonPropertyName("completion_handle_id")] Guid CompletionHandleId,
    [property: JsonPropertyName("native_acknowledgement_sha256")] string NativeAcknowledgementSha256,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("signature_base64")] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.submission.acknowledgement/v1";
    public const string CurrentProducerModule = "executor-gateway";
    public const string CurrentAuthScope = "approval:submission:acknowledge";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        SubmissionLifecycleGuard.RequireHeader(SchemaVersion, ContractId, CurrentContractId, ProducerModule, CurrentProducerModule, AuthScope, CurrentAuthScope);
        ApprovalContractGuard.RequireGuid(AcknowledgementId, nameof(AcknowledgementId));
        ApprovalContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        SubmissionLifecycleGuard.RequireAttemptBinding(ApprovalId, ProposalId, CommandId, LeaseId, Attempt, SoulId, DeviceBindingId, PlatformAccountId, TraceId, IdempotencyKey);
        ApprovalContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        SubmissionLifecycleGuard.RequirePositive(ReleaseBomGeneration, nameof(ReleaseBomGeneration));
        ApprovalContractGuard.RequireSha256(NativeRequestBindingSha256, nameof(NativeRequestBindingSha256));
        ApprovalContractGuard.RequireSha256(SubmissionIntentSha256, nameof(SubmissionIntentSha256));
        ApprovalContractGuard.RequireSha256(PendingStateSha256, nameof(PendingStateSha256));
        ApprovalContractGuard.RequireSha256(SubmittedRequestSha256, nameof(SubmittedRequestSha256));
        ApprovalContractGuard.RequireGuid(NativeSubmissionId, nameof(NativeSubmissionId));
        ApprovalContractGuard.RequireGuid(CompletionHandleId, nameof(CompletionHandleId));
        ApprovalContractGuard.RequireSha256(NativeAcknowledgementSha256, nameof(NativeAcknowledgementSha256));
        SubmissionLifecycleGuard.RequireWindow(OccurredAt, ValidUntil, MaximumLifetime);
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        ApprovalContractGuard.RequireP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }
}

public sealed record ApprovalSubmissionReconciliationV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("authority_role")] string AuthorityRole,
    [property: JsonPropertyName("reconciliation_id")] Guid ReconciliationId,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("submission_intent_sha256")] string SubmissionIntentSha256,
    [property: JsonPropertyName("pending_state_sha256")] string PendingStateSha256,
    [property: JsonPropertyName("finding")] string Finding,
    [property: JsonPropertyName("evidence_sha256")] string EvidenceSha256,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("signature_base64")] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.submission.reconciliation/v1";
    public const string CurrentProducerModule = "control-plane-host";
    public const string CurrentAuthScope = "approval:submission:reconcile";
    public const string CurrentAuthorityRole = "independent-reconciler";
    public const string ConfirmedNotSubmitted = "CONFIRMED_NOT_SUBMITTED";
    public const string ConfirmedSubmitted = "CONFIRMED_SUBMITTED";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        SubmissionLifecycleGuard.RequireHeader(SchemaVersion, ContractId, CurrentContractId, ProducerModule, CurrentProducerModule, AuthScope, CurrentAuthScope);
        ApprovalContractGuard.RequireExact(AuthorityRole, CurrentAuthorityRole, nameof(AuthorityRole));
        ApprovalContractGuard.RequireGuid(ReconciliationId, nameof(ReconciliationId));
        ApprovalContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        SubmissionLifecycleGuard.RequireAttemptBinding(ApprovalId, ProposalId, CommandId, LeaseId, Attempt, SoulId, DeviceBindingId, PlatformAccountId, TraceId, IdempotencyKey);
        ApprovalContractGuard.RequireSha256(SubmissionIntentSha256, nameof(SubmissionIntentSha256));
        ApprovalContractGuard.RequireSha256(PendingStateSha256, nameof(PendingStateSha256));
        if (Finding is not ConfirmedNotSubmitted and not ConfirmedSubmitted)
            throw new NotSupportedException($"Unsupported reconciliation finding '{Finding}'.");
        ApprovalContractGuard.RequireSha256(EvidenceSha256, nameof(EvidenceSha256));
        SubmissionLifecycleGuard.RequireWindow(OccurredAt, ValidUntil, MaximumLifetime);
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        ApprovalContractGuard.RequireP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }
}

public sealed record ApprovalSubmissionStateV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("state_event_id")] Guid StateEventId,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("native_request_binding_sha256")] string NativeRequestBindingSha256,
    [property: JsonPropertyName("submission_intent_sha256")] string SubmissionIntentSha256,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("predecessor_state_sha256")] string? PredecessorStateSha256,
    [property: JsonPropertyName("evidence_sha256")] string EvidenceSha256,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("state_sha256")] string StateSha256,
    [property: JsonPropertyName("signature_base64")] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.submission.state/v1";
    public const string CurrentProducerModule = "policy-approval";
    public const string SubmissionPending = "SUBMISSION_PENDING";
    public const string UnknownSubmission = "UNKNOWN_SUBMISSION";
    public const string SubmissionAcknowledged = "SUBMISSION_ACKNOWLEDGED";
    public const string ReconciledNotSubmitted = "RECONCILED_NOT_SUBMITTED";
    public const string ReconciledSubmitted = "RECONCILED_SUBMITTED";
    public const string RecoveryAuthorized = "RECOVERY_AUTHORIZED";

    public void Validate()
    {
        ApprovalContractGuard.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        ApprovalContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ApprovalContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ApprovalContractGuard.RequireGuid(StateEventId, nameof(StateEventId));
        ApprovalContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        SubmissionLifecycleGuard.RequireAttemptBinding(ApprovalId, ProposalId, CommandId, LeaseId, Attempt, SoulId, DeviceBindingId, PlatformAccountId, TraceId, IdempotencyKey);
        ApprovalContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        SubmissionLifecycleGuard.RequirePositive(ReleaseBomGeneration, nameof(ReleaseBomGeneration));
        ApprovalContractGuard.RequireSha256(NativeRequestBindingSha256, nameof(NativeRequestBindingSha256));
        ApprovalContractGuard.RequireSha256(SubmissionIntentSha256, nameof(SubmissionIntentSha256));
        if (State is not SubmissionPending and not UnknownSubmission and not SubmissionAcknowledged and not ReconciledNotSubmitted and not ReconciledSubmitted and not RecoveryAuthorized)
            throw new NotSupportedException($"Unsupported submission state '{State}'.");
        if (State == SubmissionPending)
        {
            if (PredecessorStateSha256 is not null)
                throw new ArgumentException("SUBMISSION_PENDING cannot have a predecessor state.", nameof(PredecessorStateSha256));
        }
        else
        {
            ApprovalContractGuard.RequireSha256(PredecessorStateSha256!, nameof(PredecessorStateSha256));
        }
        ApprovalContractGuard.RequireSha256(EvidenceSha256, nameof(EvidenceSha256));
        ApprovalContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        ApprovalContractGuard.RequireSha256(StateSha256, nameof(StateSha256));
        ApprovalContractGuard.RequireP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }
}

public sealed record ApprovalSubmissionRecoveryV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("authority_role")] string AuthorityRole,
    [property: JsonPropertyName("recovery_id")] Guid RecoveryId,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("reconciliation_id")] Guid ReconciliationId,
    [property: JsonPropertyName("reconciliation_sha256")] string ReconciliationSha256,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("previous_lease_id")] Guid PreviousLeaseId,
    [property: JsonPropertyName("previous_attempt")] int PreviousAttempt,
    [property: JsonPropertyName("next_submission_attempt_id")] Guid NextSubmissionAttemptId,
    [property: JsonPropertyName("next_lease_id")] Guid NextLeaseId,
    [property: JsonPropertyName("next_attempt")] int NextAttempt,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("next_release_bom_sha256")] string NextReleaseBomSha256,
    [property: JsonPropertyName("next_release_bom_generation")] long NextReleaseBomGeneration,
    [property: JsonPropertyName("next_execution_authorization_sha256")] string NextExecutionAuthorizationSha256,
    [property: JsonPropertyName("next_native_request_binding_sha256")] string NextNativeRequestBindingSha256,
    [property: JsonPropertyName("human_approval_id")] string HumanApprovalId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("signature_base64")] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.submission.recovery/v1";
    public const string CurrentProducerModule = "control-plane-host";
    public const string CurrentAuthScope = "approval:submission:recover";
    public const string CurrentAuthorityRole = "human-release-approver";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        SubmissionLifecycleGuard.RequireHeader(SchemaVersion, ContractId, CurrentContractId, ProducerModule, CurrentProducerModule, AuthScope, CurrentAuthScope);
        ApprovalContractGuard.RequireExact(AuthorityRole, CurrentAuthorityRole, nameof(AuthorityRole));
        ApprovalContractGuard.RequireGuid(RecoveryId, nameof(RecoveryId));
        ApprovalContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        ApprovalContractGuard.RequireGuid(ReconciliationId, nameof(ReconciliationId));
        ApprovalContractGuard.RequireSha256(ReconciliationSha256, nameof(ReconciliationSha256));
        SubmissionLifecycleGuard.RequireAttemptBinding(ApprovalId, ProposalId, CommandId, PreviousLeaseId, PreviousAttempt, SoulId, DeviceBindingId, PlatformAccountId, TraceId, IdempotencyKey);
        ApprovalContractGuard.RequireGuid(NextSubmissionAttemptId, nameof(NextSubmissionAttemptId));
        ApprovalContractGuard.RequireGuid(NextLeaseId, nameof(NextLeaseId));
        if (NextSubmissionAttemptId == SubmissionAttemptId)
            throw new ArgumentException("Recovery must authorize a fresh submission-attempt ID.", nameof(NextSubmissionAttemptId));
        if (NextLeaseId == PreviousLeaseId)
            throw new ArgumentException("Recovery must authorize a fresh lease ID.", nameof(NextLeaseId));
        if (NextAttempt != PreviousAttempt + 1 || NextAttempt is < 2 or > 3)
            throw new ArgumentOutOfRangeException(nameof(NextAttempt), "Recovery must authorize only the immediately following bounded attempt.");
        ApprovalContractGuard.RequireSha256(NextReleaseBomSha256, nameof(NextReleaseBomSha256));
        SubmissionLifecycleGuard.RequirePositive(NextReleaseBomGeneration, nameof(NextReleaseBomGeneration));
        ApprovalContractGuard.RequireSha256(NextExecutionAuthorizationSha256, nameof(NextExecutionAuthorizationSha256));
        ApprovalContractGuard.RequireSha256(NextNativeRequestBindingSha256, nameof(NextNativeRequestBindingSha256));
        SubmissionLifecycleGuard.RequirePrefixedDigest(HumanApprovalId, "human_", nameof(HumanApprovalId));
        SubmissionLifecycleGuard.RequireWindow(OccurredAt, ValidUntil, MaximumLifetime);
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        ApprovalContractGuard.RequireP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }
}

internal static class SubmissionLifecycleGuard
{
    internal static void RequireHeader(string schemaVersion, string contractId, string expectedContractId, string producerModule, string expectedProducerModule, string authScope, string expectedAuthScope)
    {
        ApprovalContractGuard.RequireExact(schemaVersion, "1.0.0", nameof(schemaVersion));
        ApprovalContractGuard.RequireExact(contractId, expectedContractId, nameof(contractId));
        ApprovalContractGuard.RequireExact(producerModule, expectedProducerModule, nameof(producerModule));
        ApprovalContractGuard.RequireExact(authScope, expectedAuthScope, nameof(authScope));
    }

    internal static void RequireAttemptBinding(Guid approvalId, Guid proposalId, Guid commandId, Guid leaseId, int attempt, string soulId, string deviceBindingId, string platformAccountId, string traceId, string idempotencyKey)
    {
        ApprovalContractGuard.RequireGuid(approvalId, nameof(approvalId));
        ApprovalContractGuard.RequireGuid(proposalId, nameof(proposalId));
        ApprovalContractGuard.RequireGuid(commandId, nameof(commandId));
        ApprovalContractGuard.RequireGuid(leaseId, nameof(leaseId));
        if (attempt is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(attempt));
        ApprovalContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId);
        ApprovalContractGuard.RequireTraceId(traceId);
        ApprovalContractGuard.RequireIdempotencyKey(idempotencyKey);
    }

    internal static void RequirePositive(long value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    internal static void RequireWindow(DateTimeOffset occurredAt, DateTimeOffset validUntil, TimeSpan maximumLifetime)
    {
        ApprovalContractGuard.RequireUtc(occurredAt, nameof(occurredAt));
        ApprovalContractGuard.RequireUtc(validUntil, nameof(validUntil));
        if (validUntil <= occurredAt || validUntil - occurredAt > maximumLifetime)
            throw new ArgumentException("Signed submission lifecycle validity is empty or exceeds its bound.", nameof(validUntil));
    }

    internal static void RequirePrefixedDigest(string value, string prefix, string name)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be an opaque prefixed digest.", name);
        ApprovalContractGuard.RequireSha256(value[prefix.Length..], name);
    }
}
