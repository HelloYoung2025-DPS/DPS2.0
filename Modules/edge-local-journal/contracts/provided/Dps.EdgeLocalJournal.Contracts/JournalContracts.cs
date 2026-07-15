using System.Text.Json.Serialization;

namespace Dps.EdgeLocalJournal;

public sealed record JournalAppendRequest(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    string CommandId,
    string EntryId,
    string EntryType,
    string TraceId,
    string IdempotencyKey,
    string PrivacyClass,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string PayloadJson,
    string PayloadSha256,
    DateTimeOffset OccurredAt);

public sealed record JournalReceipt(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    string RequestProducerModule,
    string CommandId,
    string EntryId,
    string EntryType,
    string TraceId,
    string IdempotencyKey,
    string PrivacyClass,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string OccurredAt,
    long Sequence,
    string PayloadSha256,
    string PreviousChecksum,
    string EntryChecksum,
    bool Durable,
    bool Duplicate);

public sealed record JournalQuarantineStatus(
    string MarkerSha256,
    string Reason,
    string EntryId,
    string DetectedAt,
    long JournalHeadSequence,
    string JournalHeadChecksum);

public sealed record JournalDrainAttestationRequest(
    string RequestId,
    string CommandId,
    string EntryId,
    string WorkerArtifactSha256,
    string WorkerVersion,
    string WorkerSlot,
    string JournalArtifactSha256,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    long RoutingEpoch,
    bool IntakeStopped,
    bool WorkerDrained,
    int RemainingInFlight,
    string WorkerReceiptWireSha256,
    TimeSpan ValidFor);

public sealed record JournalDrainOwnerReceipt(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("request_producer_module"), JsonRequired] string RequestProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] string OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("command_id"), JsonRequired] string CommandId,
    [property: JsonPropertyName("entry_id"), JsonRequired] string EntryId,
    [property: JsonPropertyName("entry_type"), JsonRequired] string EntryType,
    [property: JsonPropertyName("payload_sha256"), JsonRequired] string PayloadSha256,
    [property: JsonPropertyName("sequence"), JsonRequired] long Sequence,
    [property: JsonPropertyName("previous_checksum"), JsonRequired] string PreviousChecksum,
    [property: JsonPropertyName("entry_checksum"), JsonRequired] string EntryChecksum,
    [property: JsonPropertyName("durable"), JsonRequired] bool Durable,
    [property: JsonPropertyName("duplicate"), JsonRequired] bool Duplicate);

public sealed record JournalDrainAttestation(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("request_producer_module"), JsonRequired] string RequestProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] string OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("request_id"), JsonRequired] string RequestId,
    [property: JsonPropertyName("drain_id"), JsonRequired] string DrainId,
    [property: JsonPropertyName("command_id"), JsonRequired] string CommandId,
    [property: JsonPropertyName("entry_id"), JsonRequired] string EntryId,
    [property: JsonPropertyName("entry_type"), JsonRequired] string EntryType,
    [property: JsonPropertyName("entry_sequence")] long EntrySequence,
    [property: JsonPropertyName("entry_checksum"), JsonRequired] string EntryChecksum,
    [property: JsonPropertyName("entry_payload_sha256"), JsonRequired] string EntryPayloadSha256,
    [property: JsonPropertyName("journal_id"), JsonRequired] string JournalId,
    [property: JsonPropertyName("journal_file_sha256"), JsonRequired] string JournalFileSha256,
    [property: JsonPropertyName("journal_file_identity_sha256"), JsonRequired] string JournalFileIdentitySha256,
    [property: JsonPropertyName("journal_head_sequence")] long JournalHeadSequence,
    [property: JsonPropertyName("journal_head_checksum"), JsonRequired] string JournalHeadChecksum,
    [property: JsonPropertyName("checksum_encoding"), JsonRequired] string ChecksumEncoding,
    [property: JsonPropertyName("range_start_sequence")] long RangeStartSequence,
    [property: JsonPropertyName("range_end_sequence")] long RangeEndSequence,
    [property: JsonPropertyName("range_entry_count")] long RangeEntryCount,
    [property: JsonPropertyName("entry_set_sha256"), JsonRequired] string EntrySetSha256,
    [property: JsonPropertyName("quarantine_state"), JsonRequired] string QuarantineState,
    [property: JsonPropertyName("recovery_state"), JsonRequired] string RecoveryState,
    [property: JsonPropertyName("state_artifact_set_sha256"), JsonRequired] string StateArtifactSetSha256,
    [property: JsonPropertyName("worker_artifact_sha256"), JsonRequired] string WorkerArtifactSha256,
    [property: JsonPropertyName("worker_version"), JsonRequired] string WorkerVersion,
    [property: JsonPropertyName("worker_slot"), JsonRequired] string WorkerSlot,
    [property: JsonPropertyName("journal_artifact_sha256"), JsonRequired] string JournalArtifactSha256,
    [property: JsonPropertyName("release_bom_sha256"), JsonRequired] string ReleaseBomSha256,
    [property: JsonPropertyName("protected_policy_sha256"), JsonRequired] string ProtectedPolicySha256,
    [property: JsonPropertyName("routing_epoch")] long RoutingEpoch,
    [property: JsonPropertyName("intake_stopped"), JsonRequired] bool IntakeStopped,
    [property: JsonPropertyName("worker_drained"), JsonRequired] bool WorkerDrained,
    [property: JsonPropertyName("remaining_in_flight")] int RemainingInFlight,
    [property: JsonPropertyName("worker_receipt_wire_sha256"), JsonRequired] string WorkerReceiptWireSha256,
    [property: JsonPropertyName("journal_receipt_sha256"), JsonRequired] string JournalReceiptSha256,
    [property: JsonPropertyName("journal_receipt"), JsonRequired] JournalDrainOwnerReceipt JournalReceipt,
    [property: JsonPropertyName("issued_at"), JsonRequired] string IssuedAt,
    [property: JsonPropertyName("expires_at"), JsonRequired] string ExpiresAt,
    [property: JsonPropertyName("canonicalization"), JsonRequired] string Canonicalization,
    [property: JsonPropertyName("signature_key_id"), JsonRequired] string SignatureKeyId,
    [property: JsonPropertyName("signature_algorithm"), JsonRequired] string SignatureAlgorithm,
    [property: JsonPropertyName("statement_sha256"), JsonRequired] string StatementSha256,
    [property: JsonPropertyName("signature"), JsonRequired] string Signature);

public interface IJournalDrainAttestationProvider
{
    Task<JournalDrainAttestation> IssueDrainAttestationAsync(
        JournalDrainAttestationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IJournalAppendClient
{
    Task<JournalReceipt> AppendAsync(
        JournalAppendRequest request,
        CancellationToken cancellationToken = default);
}

public interface IJournalReadiness
{
    int Count { get; }

    bool IsQuarantined { get; }
}

public interface IJournalQuarantineAdministration
{
    Task<JournalQuarantineStatus?> GetQuarantineStatusAsync(
        CancellationToken cancellationToken = default);

    Task RecoverFromQuarantineAsync(
        string expectedMarkerSha256,
        CancellationToken cancellationToken = default);
}

public sealed class JournalConflictException(string message) : InvalidOperationException(message);

public sealed class JournalCorruptionException(string message) : IOException(message);

public sealed class JournalQuarantinedException(string message) : InvalidOperationException(message);

public sealed class JournalAttestationUnavailableException(string message) : InvalidOperationException(message);

public sealed class JournalAttestationStateChangedException(string message) : IOException(message);
