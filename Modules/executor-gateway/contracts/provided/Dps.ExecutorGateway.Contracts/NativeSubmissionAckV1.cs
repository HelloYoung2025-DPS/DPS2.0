using System.Text.Json.Serialization;

namespace Dps.ExecutorGateway.Contracts;

public sealed record NativeSubmissionAck(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("submission_id")] Guid SubmissionId,
    [property: JsonPropertyName("completion_handle_id")] Guid CompletionHandleId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("durability")] string Durability,
    [property: JsonPropertyName("command_sha256")] string CommandSha256,
    [property: JsonPropertyName("authorization_sha256")] string AuthorizationSha256,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("submission_intent_sha256")] string SubmissionIntentSha256,
    [property: JsonPropertyName("pending_state_sha256")] string PendingStateSha256,
    [property: JsonPropertyName("active_release_bom_sha256")] string ActiveReleaseBomSha256,
    [property: JsonPropertyName("active_release_bom_generation")] long ActiveReleaseBomGeneration,
    [property: JsonPropertyName("active_release_bom_token_sha256")] string ActiveReleaseBomTokenSha256,
    [property: JsonPropertyName("submitted_request_sha256")] string SubmittedRequestSha256,
    [property: JsonPropertyName("acknowledgement_sha256")] string AcknowledgementSha256)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "native.submission.ack/v1";
    public const string CurrentProducerModule = "windows-edge-worker";
    public const string CurrentPrivacyClass = "internal";
    public const string DurableFlush = "REQUEST_AND_STATE_FLUSHED";

    public void ValidatePayload()
    {
        NativeContractGuard.RequireMajor(SchemaVersion, 1);
        NativeContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        NativeContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        NativeContractGuard.RequireGuid(SubmissionId, nameof(SubmissionId));
        NativeContractGuard.RequireGuid(CompletionHandleId, nameof(CompletionHandleId));
        NativeContractGuard.RequireGuid(CommandId, nameof(CommandId));
        NativeContractGuard.RequireGuid(LeaseId, nameof(LeaseId));
        if (Attempt is < 1 or > 3) throw new InvalidOperationException("Submission attempt must be between one and three.");
        NativeContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        NativeContractGuard.RequireTraceId(TraceId);
        NativeContractGuard.RequireIdempotencyKey(IdempotencyKey);
        NativeContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        NativeContractGuard.RequireExact(PrivacyClass, CurrentPrivacyClass, nameof(PrivacyClass));
        NativeContractGuard.RequireExact(Durability, DurableFlush, nameof(Durability));
        NativeContractGuard.RequireSha256(CommandSha256, nameof(CommandSha256));
        NativeContractGuard.RequireSha256(AuthorizationSha256, nameof(AuthorizationSha256));
        NativeContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        NativeContractGuard.RequireSha256(SubmissionIntentSha256, nameof(SubmissionIntentSha256));
        NativeContractGuard.RequireSha256(PendingStateSha256, nameof(PendingStateSha256));
        NativeContractGuard.RequireSha256(ActiveReleaseBomSha256, nameof(ActiveReleaseBomSha256));
        if (ActiveReleaseBomGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ActiveReleaseBomGeneration));
        NativeContractGuard.RequireSha256(ActiveReleaseBomTokenSha256, nameof(ActiveReleaseBomTokenSha256));
        NativeContractGuard.RequireSha256(SubmittedRequestSha256, nameof(SubmittedRequestSha256));
    }

    public void Validate()
    {
        ValidatePayload();
        NativeContractGuard.RequireSha256(AcknowledgementSha256, nameof(AcknowledgementSha256));
    }
}
