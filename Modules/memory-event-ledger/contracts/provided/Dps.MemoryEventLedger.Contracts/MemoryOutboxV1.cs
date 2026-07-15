using System.Globalization;
using System.Text.Json.Serialization;

namespace Dps.MemoryEventLedger.Contracts;

public sealed record MemoryOutboxV1(
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
    [property: JsonPropertyName("payload_sha256")] string PayloadSha256)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "memory.outbox/v1";
    public const string CurrentProducerModule = "memory-event-ledger";

    public void Validate()
    {
        ContractValidation.RequireMajor(SchemaVersion, 1);
        ContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ContractValidation.RequireNonEmpty(OutboxId, nameof(OutboxId));
        ContractValidation.RequireNonEmpty(EventId, nameof(EventId));
        ContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        ContractValidation.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        ContractValidation.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        ContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        ContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        ContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        ContractValidation.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        ContractValidation.RequireExact(Topic, "memory.event/v1", nameof(Topic));
        ContractValidation.RequireSha256(PayloadSha256, nameof(PayloadSha256));
    }
}
