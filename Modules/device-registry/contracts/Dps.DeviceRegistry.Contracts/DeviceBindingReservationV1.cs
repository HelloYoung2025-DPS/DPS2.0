using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Dps.DeviceRegistry.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReserveDeviceBindingCommand(
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("device_id"), JsonRequired] string DeviceId,
    [property: JsonPropertyName("expected_revision"), JsonRequired] long ExpectedRevision,
    [property: JsonPropertyName("reservation_id"), JsonRequired] string ReservationId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceBindingReservationCommand(
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("device_id"), JsonRequired] string DeviceId,
    [property: JsonPropertyName("expected_revision"), JsonRequired] long ExpectedRevision,
    [property: JsonPropertyName("reservation_id"), JsonRequired] string ReservationId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt);

public interface IDeviceBindingReservationClient
{
    string InstanceConfigurationSha256 { get; }
    long InstanceTrustEpoch { get; }

    Task<DeviceRegisteredV1> ReadCurrentAsync(
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default);

    Task<DeviceBindingReservationV1> ReserveAsync(
        ReserveDeviceBindingCommand command,
        CancellationToken cancellationToken = default);

    Task<DeviceBindingReservationV1> ConfirmAsync(
        DeviceBindingReservationCommand command,
        CancellationToken cancellationToken = default);

    Task<DeviceBindingReservationV1> ReleaseAsync(
        DeviceBindingReservationCommand command,
        CancellationToken cancellationToken = default);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceBindingReservationV1(
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
    [property: JsonPropertyName("reservation_id"), JsonRequired] string ReservationId,
    [property: JsonPropertyName("device_id"), JsonRequired] string DeviceId,
    [property: JsonPropertyName("device_registration_revision"), JsonRequired] long DeviceRegistrationRevision,
    [property: JsonPropertyName("state"), JsonRequired] string State,
    [property: JsonPropertyName("lease_expires_at"), JsonRequired] DateTimeOffset? LeaseExpiresAt)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "device.binding.reservation/v1";
    public const string CurrentProducerModule = "device-registry";

    public void Validate()
    {
        DeviceContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        DeviceContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        DeviceContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        DeviceContractValidation.RequireSoulId(SoulId);
        DeviceContractValidation.RequireDeviceBindingId(DeviceBindingId);
        DeviceContractValidation.RequirePlatformAccountId(PlatformAccountId);
        DeviceContractValidation.RequireTraceId(TraceId);
        DeviceContractValidation.RequireIdempotencyKey(IdempotencyKey);
        DeviceContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        DeviceContractValidation.RequireExact(PrivacyClass, "sensitive", nameof(PrivacyClass));
        DeviceContractValidation.RequirePrefixedHex(ReservationId, "bres_", 64, nameof(ReservationId));
        DeviceContractValidation.RequirePrefixedHex(DeviceId, "device_", 32, nameof(DeviceId));
        if (DeviceRegistrationRevision < 1) throw new ArgumentOutOfRangeException(nameof(DeviceRegistrationRevision));
        if (State is not ("held" or "active" or "released")) throw new ArgumentOutOfRangeException(nameof(State));
        if ((State == "held") != LeaseExpiresAt.HasValue)
            throw new ArgumentException("Only a held reservation has a lease expiry.", nameof(LeaseExpiresAt));
        if (LeaseExpiresAt is { } expiry) DeviceContractValidation.RequireUtc(expiry, nameof(LeaseExpiresAt));
        if (!string.Equals(IdempotencyKey, CreateReceiptIdempotencyKey(ReservationId, State), StringComparison.Ordinal))
            throw new ArgumentException("The reservation receipt idempotency key is not canonical.", nameof(IdempotencyKey));
    }

    public static string CreateReceiptIdempotencyKey(string reservationId, string state)
    {
        DeviceContractValidation.RequirePrefixedHex(reservationId, "bres_", 64, nameof(reservationId));
        if (state is not ("held" or "active" or "released")) throw new ArgumentOutOfRangeException(nameof(state));
        var bytes = Encoding.ASCII.GetBytes(
            "dps.device-binding-reservation.receipt/v1:" + reservationId + ":" + state);
        try { return "idem_" + Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
