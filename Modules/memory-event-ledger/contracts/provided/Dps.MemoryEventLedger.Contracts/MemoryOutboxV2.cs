using System.Text.Json.Serialization;

namespace Dps.MemoryEventLedger.Contracts;

public sealed record MemoryOutboxV2(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("outbox_id")] Guid OutboxId,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("payload_sha256")] string PayloadSha256,
    [property: JsonPropertyName("soul_sequence")] long SoulSequence,
    [property: JsonPropertyName("previous_chain_sha256")] string PreviousChainSha256,
    [property: JsonPropertyName("chain_sha256")] string ChainSha256)
{
    public const string CurrentSchemaVersion = "2.0.0";
    public const string CurrentContractId = "memory.outbox/v2";
    public const string CurrentProducerModule = "memory-event-ledger";

    public void Validate()
    {
        MemoryContractValidationV2.RequireMajor(SchemaVersion, 2);
        MemoryContractValidationV2.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        MemoryContractValidationV2.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        MemoryContractValidationV2.RequireNonEmpty(OutboxId, nameof(OutboxId));
        MemoryContractValidationV2.RequireNonEmpty(EventId, nameof(EventId));
        MemoryContractValidationV2.RequireSoulId(SoulId, nameof(SoulId));
        MemoryContractValidationV2.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        MemoryContractValidationV2.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        MemoryContractValidationV2.RequireTraceId(TraceId, nameof(TraceId));
        MemoryContractValidationV2.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        MemoryContractValidationV2.RequireUtc(OccurredAt, nameof(OccurredAt));
        MemoryContractValidationV2.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        MemoryContractValidationV2.RequireExact(Topic, MemoryEventV2.CurrentContractId, nameof(Topic));
        MemoryContractValidationV2.RequireSha256(PayloadSha256, nameof(PayloadSha256));
        if (SoulSequence < 1) throw new ArgumentOutOfRangeException(nameof(SoulSequence));
        MemoryContractValidationV2.RequireSha256(PreviousChainSha256, nameof(PreviousChainSha256));
        MemoryContractValidationV2.RequireSha256(ChainSha256, nameof(ChainSha256));
    }
}
