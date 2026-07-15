using System.Text.Json.Serialization;

namespace Dps.Binding.Contracts;

public sealed record AcquireBindingMutationFenceCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public interface IBindingMutationFenceLease : IAsyncDisposable
{
    BindingMutationFenceV1 Receipt { get; }
}

public interface IBindingMutationFenceClient
{
    Task<IBindingMutationFenceLease> AcquireAsync(
        AcquireBindingMutationFenceCommand command,
        CancellationToken cancellationToken = default);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingMutationFenceV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("binding_revision"), JsonRequired] long BindingRevision,
    [property: JsonPropertyName("fence_id"), JsonRequired] string FenceId,
    [property: JsonPropertyName("fence_sequence"), JsonRequired] long FenceSequence,
    [property: JsonPropertyName("state"), JsonRequired] string State)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "identity.binding.mutation.fence/v1";
    public const string CurrentProducerModule = "binding";

    public void Validate()
    {
        BindingContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        BindingContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        BindingContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        BindingContractValidation.RequireSoulId(SoulId);
        BindingContractValidation.RequireDeviceBindingId(DeviceBindingId);
        BindingContractValidation.RequirePlatformAccountId(PlatformAccountId);
        BindingContractValidation.RequireTraceId(TraceId);
        BindingContractValidation.RequireIdempotencyKey(IdempotencyKey);
        BindingContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        BindingContractValidation.RequireExact(PrivacyClass, "sensitive", nameof(PrivacyClass));
        if (BindingRevision < 1) throw new ArgumentOutOfRangeException(nameof(BindingRevision));
        BindingContractValidation.RequirePrefixedHex(FenceId, "bfence_", 64, nameof(FenceId));
        if (FenceSequence < 1) throw new ArgumentOutOfRangeException(nameof(FenceSequence));
        BindingContractValidation.RequireExact(State, "held", nameof(State));
    }
}
