using System.Security.Cryptography;
using System.Text.Json;

namespace Dps.PolicyApproval.Contracts;

public static class ApprovalSubmissionIntentV1Codec
{
    public const int MaximumPayloadBytes = 64 * 1024;
    private const string ContractName = "approval.submission.intent/v1";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "auth_scope",
        "submission_attempt_id", "fence_request_sha256", "approval_id", "proposal_id",
        "command_id", "lease_id", "attempt", "soul_id", "device_binding_id",
        "platform_account_id", "trace_id", "idempotency_key", "approval_sha256",
        "proposal_sha256", "status_revision", "runtime_revision", "runtime_state_sha256",
        "release_bom_sha256", "release_bom_generation", "execution_authorization_sha256",
        "native_request_binding_sha256", "occurred_at", "valid_until", "privacy_class",
        "signature_base64"
    };

    public static byte[] Serialize(ApprovalSubmissionIntentV1 value)
        => ApprovalSubmissionLifecycleJson.Serialize(
            value,
            MaximumPayloadBytes,
            ContractName,
            static item => item.Validate(),
            static (writer, item) =>
            {
                writer.WriteString("schema_version", item.SchemaVersion);
                writer.WriteString("contract_id", item.ContractId);
                writer.WriteString("producer_module", item.ProducerModule);
                writer.WriteString("auth_scope", item.AuthScope);
                writer.WriteString("submission_attempt_id", item.SubmissionAttemptId.ToString("D"));
                writer.WriteString("fence_request_sha256", item.FenceRequestSha256);
                writer.WriteString("approval_id", item.ApprovalId.ToString("D"));
                writer.WriteString("proposal_id", item.ProposalId.ToString("D"));
                writer.WriteString("command_id", item.CommandId.ToString("D"));
                writer.WriteString("lease_id", item.LeaseId.ToString("D"));
                writer.WriteNumber("attempt", item.Attempt);
                writer.WriteString("soul_id", item.SoulId);
                writer.WriteString("device_binding_id", item.DeviceBindingId);
                writer.WriteString("platform_account_id", item.PlatformAccountId);
                writer.WriteString("trace_id", item.TraceId);
                writer.WriteString("idempotency_key", item.IdempotencyKey);
                writer.WriteString("approval_sha256", item.ApprovalSha256);
                writer.WriteString("proposal_sha256", item.ProposalSha256);
                writer.WriteNumber("status_revision", item.StatusRevision);
                writer.WriteNumber("runtime_revision", item.RuntimeRevision);
                writer.WriteString("runtime_state_sha256", item.RuntimeStateSha256);
                writer.WriteString("release_bom_sha256", item.ReleaseBomSha256);
                writer.WriteNumber("release_bom_generation", item.ReleaseBomGeneration);
                writer.WriteString("execution_authorization_sha256", item.ExecutionAuthorizationSha256);
                writer.WriteString("native_request_binding_sha256", item.NativeRequestBindingSha256);
                writer.WriteString("occurred_at", PolicyApprovalContractJson.FormatCanonicalUtc(item.OccurredAt));
                writer.WriteString("valid_until", PolicyApprovalContractJson.FormatCanonicalUtc(item.ValidUntil));
                writer.WriteString("privacy_class", item.PrivacyClass);
                writer.WriteString("signature_base64", item.SignatureBase64);
            });

    public static ApprovalSubmissionIntentV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: true);

    public static ApprovalSubmissionIntentV1 DeserializeSemanticJsonb(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: false);

    private static ApprovalSubmissionIntentV1 DeserializeCore(
        ReadOnlySpan<byte> payloadUtf8,
        bool requireCanonicalWire)
        => ApprovalSubmissionLifecycleJson.Deserialize(
            payloadUtf8,
            MaximumPayloadBytes,
            ContractName,
            ExactFields,
            static fields => new ApprovalSubmissionIntentV1(
                PolicyApprovalContractJson.ReadString(fields, "schema_version", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "contract_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "producer_module", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "auth_scope", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "submission_attempt_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "fence_request_sha256", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "approval_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "proposal_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "command_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "lease_id", ContractName),
                ApprovalSubmissionLifecycleJson.ReadPositiveInt32(fields, "attempt", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "soul_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "device_binding_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "platform_account_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "trace_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "idempotency_key", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "approval_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "proposal_sha256", ContractName),
                PolicyApprovalContractJson.ReadPositiveInt64(fields, "status_revision", ContractName),
                PolicyApprovalContractJson.ReadPositiveInt64(fields, "runtime_revision", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "runtime_state_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "release_bom_sha256", ContractName),
                PolicyApprovalContractJson.ReadPositiveInt64(fields, "release_bom_generation", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "execution_authorization_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "native_request_binding_sha256", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "occurred_at", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "valid_until", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "privacy_class", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "signature_base64", ContractName)),
            static item => item.Validate(),
            Serialize,
            requireCanonicalWire);
}

public static class ApprovalSubmissionAcknowledgementV1Codec
{
    public const int MaximumPayloadBytes = 64 * 1024;
    private const string ContractName = "approval.submission.acknowledgement/v1";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "auth_scope",
        "acknowledgement_id", "submission_attempt_id", "approval_id", "proposal_id",
        "command_id", "lease_id", "attempt", "soul_id", "device_binding_id",
        "platform_account_id", "trace_id", "idempotency_key", "release_bom_sha256",
        "release_bom_generation", "native_request_binding_sha256", "submission_intent_sha256",
        "pending_state_sha256", "submitted_request_sha256", "native_submission_id",
        "completion_handle_id", "native_acknowledgement_sha256", "occurred_at",
        "valid_until", "privacy_class", "signature_base64"
    };

    public static byte[] Serialize(ApprovalSubmissionAcknowledgementV1 value)
        => ApprovalSubmissionLifecycleJson.Serialize(
            value,
            MaximumPayloadBytes,
            ContractName,
            static item => item.Validate(),
            static (writer, item) =>
            {
                writer.WriteString("schema_version", item.SchemaVersion);
                writer.WriteString("contract_id", item.ContractId);
                writer.WriteString("producer_module", item.ProducerModule);
                writer.WriteString("auth_scope", item.AuthScope);
                writer.WriteString("acknowledgement_id", item.AcknowledgementId.ToString("D"));
                writer.WriteString("submission_attempt_id", item.SubmissionAttemptId.ToString("D"));
                ApprovalSubmissionLifecycleJson.WriteAttempt(writer, item.ApprovalId, item.ProposalId, item.CommandId, item.LeaseId, item.Attempt, item.SoulId, item.DeviceBindingId, item.PlatformAccountId, item.TraceId, item.IdempotencyKey);
                writer.WriteString("release_bom_sha256", item.ReleaseBomSha256);
                writer.WriteNumber("release_bom_generation", item.ReleaseBomGeneration);
                writer.WriteString("native_request_binding_sha256", item.NativeRequestBindingSha256);
                writer.WriteString("submission_intent_sha256", item.SubmissionIntentSha256);
                writer.WriteString("pending_state_sha256", item.PendingStateSha256);
                writer.WriteString("submitted_request_sha256", item.SubmittedRequestSha256);
                writer.WriteString("native_submission_id", item.NativeSubmissionId.ToString("D"));
                writer.WriteString("completion_handle_id", item.CompletionHandleId.ToString("D"));
                writer.WriteString("native_acknowledgement_sha256", item.NativeAcknowledgementSha256);
                writer.WriteString("occurred_at", PolicyApprovalContractJson.FormatCanonicalUtc(item.OccurredAt));
                writer.WriteString("valid_until", PolicyApprovalContractJson.FormatCanonicalUtc(item.ValidUntil));
                writer.WriteString("privacy_class", item.PrivacyClass);
                writer.WriteString("signature_base64", item.SignatureBase64);
            });

    public static ApprovalSubmissionAcknowledgementV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: true);

    public static ApprovalSubmissionAcknowledgementV1 DeserializeSemanticJsonb(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: false);

    private static ApprovalSubmissionAcknowledgementV1 DeserializeCore(
        ReadOnlySpan<byte> payloadUtf8,
        bool requireCanonicalWire)
        => ApprovalSubmissionLifecycleJson.Deserialize(
            payloadUtf8,
            MaximumPayloadBytes,
            ContractName,
            ExactFields,
            static fields => new ApprovalSubmissionAcknowledgementV1(
                PolicyApprovalContractJson.ReadString(fields, "schema_version", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "contract_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "producer_module", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "auth_scope", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "acknowledgement_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "submission_attempt_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "approval_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "proposal_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "command_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "lease_id", ContractName),
                ApprovalSubmissionLifecycleJson.ReadPositiveInt32(fields, "attempt", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "soul_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "device_binding_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "platform_account_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "trace_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "idempotency_key", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "release_bom_sha256", ContractName),
                PolicyApprovalContractJson.ReadPositiveInt64(fields, "release_bom_generation", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "native_request_binding_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "submission_intent_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "pending_state_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "submitted_request_sha256", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "native_submission_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "completion_handle_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "native_acknowledgement_sha256", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "occurred_at", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "valid_until", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "privacy_class", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "signature_base64", ContractName)),
            static item => item.Validate(),
            Serialize,
            requireCanonicalWire);
}

public static class ApprovalSubmissionReconciliationV1Codec
{
    public const int MaximumPayloadBytes = 64 * 1024;
    private const string ContractName = "approval.submission.reconciliation/v1";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "auth_scope", "authority_role",
        "reconciliation_id", "submission_attempt_id", "approval_id", "proposal_id",
        "command_id", "lease_id", "attempt", "soul_id", "device_binding_id",
        "platform_account_id", "trace_id", "idempotency_key", "submission_intent_sha256",
        "pending_state_sha256", "finding", "evidence_sha256", "occurred_at", "valid_until",
        "privacy_class", "signature_base64"
    };

    public static byte[] Serialize(ApprovalSubmissionReconciliationV1 value)
        => ApprovalSubmissionLifecycleJson.Serialize(
            value,
            MaximumPayloadBytes,
            ContractName,
            static item => item.Validate(),
            static (writer, item) =>
            {
                writer.WriteString("schema_version", item.SchemaVersion);
                writer.WriteString("contract_id", item.ContractId);
                writer.WriteString("producer_module", item.ProducerModule);
                writer.WriteString("auth_scope", item.AuthScope);
                writer.WriteString("authority_role", item.AuthorityRole);
                writer.WriteString("reconciliation_id", item.ReconciliationId.ToString("D"));
                writer.WriteString("submission_attempt_id", item.SubmissionAttemptId.ToString("D"));
                ApprovalSubmissionLifecycleJson.WriteAttempt(writer, item.ApprovalId, item.ProposalId, item.CommandId, item.LeaseId, item.Attempt, item.SoulId, item.DeviceBindingId, item.PlatformAccountId, item.TraceId, item.IdempotencyKey);
                writer.WriteString("submission_intent_sha256", item.SubmissionIntentSha256);
                writer.WriteString("pending_state_sha256", item.PendingStateSha256);
                writer.WriteString("finding", item.Finding);
                writer.WriteString("evidence_sha256", item.EvidenceSha256);
                writer.WriteString("occurred_at", PolicyApprovalContractJson.FormatCanonicalUtc(item.OccurredAt));
                writer.WriteString("valid_until", PolicyApprovalContractJson.FormatCanonicalUtc(item.ValidUntil));
                writer.WriteString("privacy_class", item.PrivacyClass);
                writer.WriteString("signature_base64", item.SignatureBase64);
            });

    public static ApprovalSubmissionReconciliationV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: true);

    public static ApprovalSubmissionReconciliationV1 DeserializeSemanticJsonb(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: false);

    private static ApprovalSubmissionReconciliationV1 DeserializeCore(
        ReadOnlySpan<byte> payloadUtf8,
        bool requireCanonicalWire)
        => ApprovalSubmissionLifecycleJson.Deserialize(
            payloadUtf8,
            MaximumPayloadBytes,
            ContractName,
            ExactFields,
            static fields => new ApprovalSubmissionReconciliationV1(
                PolicyApprovalContractJson.ReadString(fields, "schema_version", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "contract_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "producer_module", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "auth_scope", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "authority_role", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "reconciliation_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "submission_attempt_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "approval_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "proposal_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "command_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "lease_id", ContractName),
                ApprovalSubmissionLifecycleJson.ReadPositiveInt32(fields, "attempt", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "soul_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "device_binding_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "platform_account_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "trace_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "idempotency_key", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "submission_intent_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "pending_state_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "finding", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "evidence_sha256", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "occurred_at", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "valid_until", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "privacy_class", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "signature_base64", ContractName)),
            static item => item.Validate(),
            Serialize,
            requireCanonicalWire);
}

public static class ApprovalSubmissionRecoveryV1Codec
{
    public const int MaximumPayloadBytes = 64 * 1024;
    private const string ContractName = "approval.submission.recovery/v1";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "auth_scope", "authority_role",
        "recovery_id", "submission_attempt_id", "reconciliation_id", "reconciliation_sha256",
        "approval_id", "proposal_id", "command_id", "previous_lease_id", "previous_attempt",
        "next_submission_attempt_id", "next_lease_id", "next_attempt", "soul_id",
        "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
        "next_release_bom_sha256", "next_release_bom_generation",
        "next_execution_authorization_sha256", "next_native_request_binding_sha256",
        "human_approval_id", "occurred_at", "valid_until", "privacy_class", "signature_base64"
    };

    public static byte[] Serialize(ApprovalSubmissionRecoveryV1 value)
        => ApprovalSubmissionLifecycleJson.Serialize(
            value,
            MaximumPayloadBytes,
            ContractName,
            static item => item.Validate(),
            static (writer, item) =>
            {
                writer.WriteString("schema_version", item.SchemaVersion);
                writer.WriteString("contract_id", item.ContractId);
                writer.WriteString("producer_module", item.ProducerModule);
                writer.WriteString("auth_scope", item.AuthScope);
                writer.WriteString("authority_role", item.AuthorityRole);
                writer.WriteString("recovery_id", item.RecoveryId.ToString("D"));
                writer.WriteString("submission_attempt_id", item.SubmissionAttemptId.ToString("D"));
                writer.WriteString("reconciliation_id", item.ReconciliationId.ToString("D"));
                writer.WriteString("reconciliation_sha256", item.ReconciliationSha256);
                writer.WriteString("approval_id", item.ApprovalId.ToString("D"));
                writer.WriteString("proposal_id", item.ProposalId.ToString("D"));
                writer.WriteString("command_id", item.CommandId.ToString("D"));
                writer.WriteString("previous_lease_id", item.PreviousLeaseId.ToString("D"));
                writer.WriteNumber("previous_attempt", item.PreviousAttempt);
                writer.WriteString("next_submission_attempt_id", item.NextSubmissionAttemptId.ToString("D"));
                writer.WriteString("next_lease_id", item.NextLeaseId.ToString("D"));
                writer.WriteNumber("next_attempt", item.NextAttempt);
                writer.WriteString("soul_id", item.SoulId);
                writer.WriteString("device_binding_id", item.DeviceBindingId);
                writer.WriteString("platform_account_id", item.PlatformAccountId);
                writer.WriteString("trace_id", item.TraceId);
                writer.WriteString("idempotency_key", item.IdempotencyKey);
                writer.WriteString("next_release_bom_sha256", item.NextReleaseBomSha256);
                writer.WriteNumber("next_release_bom_generation", item.NextReleaseBomGeneration);
                writer.WriteString("next_execution_authorization_sha256", item.NextExecutionAuthorizationSha256);
                writer.WriteString("next_native_request_binding_sha256", item.NextNativeRequestBindingSha256);
                writer.WriteString("human_approval_id", item.HumanApprovalId);
                writer.WriteString("occurred_at", PolicyApprovalContractJson.FormatCanonicalUtc(item.OccurredAt));
                writer.WriteString("valid_until", PolicyApprovalContractJson.FormatCanonicalUtc(item.ValidUntil));
                writer.WriteString("privacy_class", item.PrivacyClass);
                writer.WriteString("signature_base64", item.SignatureBase64);
            });

    public static ApprovalSubmissionRecoveryV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: true);

    public static ApprovalSubmissionRecoveryV1 DeserializeSemanticJsonb(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: false);

    private static ApprovalSubmissionRecoveryV1 DeserializeCore(
        ReadOnlySpan<byte> payloadUtf8,
        bool requireCanonicalWire)
        => ApprovalSubmissionLifecycleJson.Deserialize(
            payloadUtf8,
            MaximumPayloadBytes,
            ContractName,
            ExactFields,
            static fields => new ApprovalSubmissionRecoveryV1(
                PolicyApprovalContractJson.ReadString(fields, "schema_version", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "contract_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "producer_module", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "auth_scope", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "authority_role", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "recovery_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "submission_attempt_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "reconciliation_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "reconciliation_sha256", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "approval_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "proposal_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "command_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "previous_lease_id", ContractName),
                ApprovalSubmissionLifecycleJson.ReadPositiveInt32(fields, "previous_attempt", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "next_submission_attempt_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "next_lease_id", ContractName),
                ApprovalSubmissionLifecycleJson.ReadPositiveInt32(fields, "next_attempt", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "soul_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "device_binding_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "platform_account_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "trace_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "idempotency_key", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "next_release_bom_sha256", ContractName),
                PolicyApprovalContractJson.ReadPositiveInt64(fields, "next_release_bom_generation", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "next_execution_authorization_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "next_native_request_binding_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "human_approval_id", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "occurred_at", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "valid_until", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "privacy_class", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "signature_base64", ContractName)),
            static item => item.Validate(),
            Serialize,
            requireCanonicalWire);
}

public static class ApprovalSubmissionStateV1Codec
{
    public const int MaximumPayloadBytes = 64 * 1024;
    private const string ContractName = "approval.submission.state/v1";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "state_event_id",
        "submission_attempt_id", "approval_id", "proposal_id", "command_id", "lease_id",
        "attempt", "soul_id", "device_binding_id", "platform_account_id", "trace_id",
        "idempotency_key", "release_bom_sha256", "release_bom_generation",
        "native_request_binding_sha256", "submission_intent_sha256", "state",
        "predecessor_state_sha256", "evidence_sha256", "occurred_at", "privacy_class",
        "state_sha256", "signature_base64"
    };

    public static byte[] Serialize(ApprovalSubmissionStateV1 value)
        => ApprovalSubmissionLifecycleJson.Serialize(
            value,
            MaximumPayloadBytes,
            ContractName,
            static item => item.Validate(),
            static (writer, item) =>
            {
                writer.WriteString("schema_version", item.SchemaVersion);
                writer.WriteString("contract_id", item.ContractId);
                writer.WriteString("producer_module", item.ProducerModule);
                writer.WriteString("state_event_id", item.StateEventId.ToString("D"));
                writer.WriteString("submission_attempt_id", item.SubmissionAttemptId.ToString("D"));
                ApprovalSubmissionLifecycleJson.WriteAttempt(writer, item.ApprovalId, item.ProposalId, item.CommandId, item.LeaseId, item.Attempt, item.SoulId, item.DeviceBindingId, item.PlatformAccountId, item.TraceId, item.IdempotencyKey);
                writer.WriteString("release_bom_sha256", item.ReleaseBomSha256);
                writer.WriteNumber("release_bom_generation", item.ReleaseBomGeneration);
                writer.WriteString("native_request_binding_sha256", item.NativeRequestBindingSha256);
                writer.WriteString("submission_intent_sha256", item.SubmissionIntentSha256);
                writer.WriteString("state", item.State);
                if (item.PredecessorStateSha256 is null)
                    writer.WriteNull("predecessor_state_sha256");
                else
                    writer.WriteString("predecessor_state_sha256", item.PredecessorStateSha256);
                writer.WriteString("evidence_sha256", item.EvidenceSha256);
                writer.WriteString("occurred_at", PolicyApprovalContractJson.FormatCanonicalUtc(item.OccurredAt));
                writer.WriteString("privacy_class", item.PrivacyClass);
                writer.WriteString("state_sha256", item.StateSha256);
                writer.WriteString("signature_base64", item.SignatureBase64);
            });

    public static ApprovalSubmissionStateV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: true);

    public static ApprovalSubmissionStateV1 DeserializeSemanticJsonb(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: false);

    private static ApprovalSubmissionStateV1 DeserializeCore(
        ReadOnlySpan<byte> payloadUtf8,
        bool requireCanonicalWire)
        => ApprovalSubmissionLifecycleJson.Deserialize(
            payloadUtf8,
            MaximumPayloadBytes,
            ContractName,
            ExactFields,
            static fields => new ApprovalSubmissionStateV1(
                PolicyApprovalContractJson.ReadString(fields, "schema_version", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "contract_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "producer_module", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "state_event_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "submission_attempt_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "approval_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "proposal_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "command_id", ContractName),
                PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "lease_id", ContractName),
                ApprovalSubmissionLifecycleJson.ReadPositiveInt32(fields, "attempt", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "soul_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "device_binding_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "platform_account_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "trace_id", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "idempotency_key", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "release_bom_sha256", ContractName),
                PolicyApprovalContractJson.ReadPositiveInt64(fields, "release_bom_generation", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "native_request_binding_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "submission_intent_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "state", ContractName),
                PolicyApprovalContractJson.ReadNullableString(fields, "predecessor_state_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "evidence_sha256", ContractName),
                PolicyApprovalContractJson.ReadCanonicalUtc(fields, "occurred_at", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "privacy_class", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "state_sha256", ContractName),
                PolicyApprovalContractJson.ReadString(fields, "signature_base64", ContractName)),
            static item => item.Validate(),
            Serialize,
            requireCanonicalWire);
}

internal static class ApprovalSubmissionLifecycleJson
{
    internal static byte[] Serialize<T>(
        T value,
        int maximumPayloadBytes,
        string contractName,
        Action<T> validate,
        Action<Utf8JsonWriter, T> writeFields)
    {
        ArgumentNullException.ThrowIfNull(value);
        validate(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writeFields(writer, value);
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > maximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentException(
                $"{contractName} payload exceeds its byte budget.",
                nameof(value));
        }
        return payload;
    }

    internal static T Deserialize<T>(
        ReadOnlySpan<byte> payloadUtf8,
        int maximumPayloadBytes,
        string contractName,
        IReadOnlySet<string> exactFields,
        Func<IReadOnlyDictionary<string, JsonElement>, T> materialize,
        Action<T> validate,
        Func<T, byte[]> serialize,
        bool requireCanonicalWire)
    {
        PolicyApprovalContractJson.RequirePayload(
            payloadUtf8,
            maximumPayloadBytes,
            contractName);
        using var document = JsonDocument.Parse(
            payloadUtf8.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
        var fields = PolicyApprovalContractJson.ReadExactFields(
            document.RootElement,
            exactFields,
            exactFields,
            contractName);
        var value = materialize(fields);
        validate(value);
        if (requireCanonicalWire)
        {
            PolicyApprovalContractJson.RequireCanonicalWire(
                payloadUtf8,
                serialize(value),
                contractName);
        }
        return value;
    }

    internal static int ReadPositiveInt32(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string contractName)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed)
            || parsed < 1)
        {
            throw new ArgumentException(
                $"{contractName} field '{name}' must be a positive Int32.");
        }
        return parsed;
    }

    internal static void WriteAttempt(
        Utf8JsonWriter writer,
        Guid approvalId,
        Guid proposalId,
        Guid commandId,
        Guid leaseId,
        int attempt,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey)
    {
        writer.WriteString("approval_id", approvalId.ToString("D"));
        writer.WriteString("proposal_id", proposalId.ToString("D"));
        writer.WriteString("command_id", commandId.ToString("D"));
        writer.WriteString("lease_id", leaseId.ToString("D"));
        writer.WriteNumber("attempt", attempt);
        writer.WriteString("soul_id", soulId);
        writer.WriteString("device_binding_id", deviceBindingId);
        writer.WriteString("platform_account_id", platformAccountId);
        writer.WriteString("trace_id", traceId);
        writer.WriteString("idempotency_key", idempotencyKey);
    }
}
