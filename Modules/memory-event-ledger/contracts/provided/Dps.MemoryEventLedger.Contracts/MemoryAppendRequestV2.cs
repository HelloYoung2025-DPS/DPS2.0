using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Dps.MemoryEventLedger.Contracts;

public sealed record MemoryAppendRequestV2(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(MemoryCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("signed_receipt_canonical_base64")] string SignedReceiptCanonicalBase64,
    [property: JsonPropertyName("interest_signals")] IReadOnlyList<InterestSignalV2> InterestSignals)
{
    public const string CurrentSchemaVersion = "2.0.0";
    public const string CurrentContractId = "memory.append.request/v2";
    public const string CurrentProducerModule = "control-plane-host";
    public const int MaximumSignedReceiptBytes = 32_768;

    public void Validate()
    {
        MemoryContractValidationV2.RequireMajor(SchemaVersion, 2);
        MemoryContractValidationV2.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        MemoryContractValidationV2.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        MemoryContractValidationV2.RequireNonEmpty(EventId, nameof(EventId));
        MemoryContractValidationV2.RequireSoulId(SoulId, nameof(SoulId));
        MemoryContractValidationV2.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        MemoryContractValidationV2.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        MemoryContractValidationV2.RequireTraceId(TraceId, nameof(TraceId));
        MemoryContractValidationV2.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        MemoryContractValidationV2.RequireUtc(OccurredAt, nameof(OccurredAt));
        MemoryContractValidationV2.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        ArgumentNullException.ThrowIfNull(InterestSignals);
        if (InterestSignals.Count > MemoryObservationV2.MaximumInterestSignals)
            throw new ArgumentOutOfRangeException(nameof(InterestSignals));
        foreach (var signal in InterestSignals) { ArgumentNullException.ThrowIfNull(signal); signal.Validate(); }
        var topics = new HashSet<string>(StringComparer.Ordinal);
        if (InterestSignals.Any(signal => !topics.Add(signal.Topic))) throw new InvalidOperationException("Interest signal topics must be unique.");

        byte[] raw;
        try { raw = Convert.FromBase64String(SignedReceiptCanonicalBase64); }
        catch (FormatException exception) { throw new ArgumentException("Signed receipt must be canonical Base64.", nameof(SignedReceiptCanonicalBase64), exception); }
        try
        {
            if (raw.Length is 0 or > MaximumSignedReceiptBytes ||
                !string.Equals(Convert.ToBase64String(raw), SignedReceiptCanonicalBase64, StringComparison.Ordinal))
                throw new ArgumentException("Signed receipt Base64 is empty, non-canonical, or exceeds its byte bound.", nameof(SignedReceiptCanonicalBase64));
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
}
