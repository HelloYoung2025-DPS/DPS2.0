using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Dps.PlatformAccountRegistry.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReservePlatformAccountBindingCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    string ReservationId,
    string TraceId,
    DateTimeOffset OccurredAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformAccountBindingReservationCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    string ReservationId,
    string TraceId,
    DateTimeOffset OccurredAt);

public interface IPlatformAccountBindingReservationClient
{
    string InstanceConfigurationSha256 { get; }
    long InstanceTrustEpoch { get; }

    Task<PlatformAccountAuthorizedV1> ReadCurrentAsync(
        string platformAccountId,
        string soulId,
        string deviceBindingId,
        CancellationToken cancellationToken = default);

    Task<PlatformAccountBindingReservationV1> ReserveAsync(
        ReservePlatformAccountBindingCommand command,
        CancellationToken cancellationToken = default);

    Task<PlatformAccountBindingReservationV1> ConfirmAsync(
        PlatformAccountBindingReservationCommand command,
        CancellationToken cancellationToken = default);

    Task<PlatformAccountBindingReservationV1> ReleaseAsync(
        PlatformAccountBindingReservationCommand command,
        CancellationToken cancellationToken = default);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformAccountBindingReservationV1(
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
    [property: JsonPropertyName("account_authorization_revision"), JsonRequired] long AccountAuthorizationRevision,
    [property: JsonPropertyName("state"), JsonRequired] string State,
    [property: JsonPropertyName("lease_expires_at"), JsonRequired] DateTimeOffset? LeaseExpiresAt)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "platform.account.binding.reservation/v1";
    public const string CurrentProducerModule = "platform-account-registry";

    public void Validate()
    {
        AccountContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        AccountContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        AccountContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        AccountContractValidation.RequireSoulId(SoulId);
        AccountContractValidation.RequireDeviceBindingId(DeviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(PlatformAccountId);
        AccountContractValidation.RequireTraceId(TraceId);
        AccountContractValidation.RequireIdempotencyKey(IdempotencyKey);
        AccountContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        AccountContractValidation.RequireExact(PrivacyClass, "sensitive", nameof(PrivacyClass));
        AccountContractValidation.RequirePrefixedHex(ReservationId, "bres_", 64, nameof(ReservationId));
        if (AccountAuthorizationRevision < 1) throw new ArgumentOutOfRangeException(nameof(AccountAuthorizationRevision));
        if (State is not ("held" or "active" or "released")) throw new ArgumentOutOfRangeException(nameof(State));
        if ((State == "held") != LeaseExpiresAt.HasValue)
            throw new ArgumentException("Only a held reservation has a lease expiry.", nameof(LeaseExpiresAt));
        if (LeaseExpiresAt is { } expiry)
            AccountContractValidation.RequireUtc(expiry, nameof(LeaseExpiresAt));
        var expectedIdempotencyKey = CreateReceiptIdempotencyKey(ReservationId, State);
        if (!string.Equals(IdempotencyKey, expectedIdempotencyKey, StringComparison.Ordinal))
            throw new ArgumentException("The reservation receipt idempotency key is not canonical.", nameof(IdempotencyKey));
    }

    public static string CreateReceiptIdempotencyKey(string reservationId, string state)
    {
        AccountContractValidation.RequirePrefixedHex(reservationId, "bres_", 64, nameof(reservationId));
        if (state is not ("held" or "active" or "released")) throw new ArgumentOutOfRangeException(nameof(state));
        var bytes = Encoding.ASCII.GetBytes(
            "dps.platform-account-binding-reservation.receipt/v1:" + reservationId + ":" + state);
        try { return "idem_" + Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
