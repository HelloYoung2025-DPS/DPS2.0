using System.Text.Json.Serialization;

namespace Dps.Binding.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityBindingV1(
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
    [property: JsonPropertyName("device_id"), JsonRequired] string DeviceId,
    [property: JsonPropertyName("binding_revision"), JsonRequired] long BindingRevision,
    [property: JsonPropertyName("status"), JsonRequired] string Status,
    [property: JsonPropertyName("device_registration_revision"), JsonRequired] long DeviceRegistrationRevision,
    [property: JsonPropertyName("account_authorization_revision"), JsonRequired] long AccountAuthorizationRevision)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "identity.binding/v1";
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
        BindingContractValidation.RequirePrefixedHex(DeviceId, "device_", 32, nameof(DeviceId));
        if (BindingRevision < 1) throw new ArgumentOutOfRangeException(nameof(BindingRevision));
        if (DeviceRegistrationRevision < 1) throw new ArgumentOutOfRangeException(nameof(DeviceRegistrationRevision));
        if (AccountAuthorizationRevision < 1) throw new ArgumentOutOfRangeException(nameof(AccountAuthorizationRevision));
        if (Status is not ("active" or "revoked")) throw new ArgumentOutOfRangeException(nameof(Status));
    }
}

public static class BindingContractValidation
{
    public static void RequireMajor(string version, int expected)
    {
        RequireText(version, 32, nameof(version));
        var segments = version.Split('.');
        if (segments.Length is < 1 or > 3 ||
            segments.Any(static segment => segment.Length == 0 || segment.Any(static character => character is < '0' or > '9')) ||
            segments.Any(static segment => segment.Length > 1 && segment[0] == '0') ||
            !int.TryParse(segments[0], out var actual) ||
            actual != expected ||
            !string.Equals(segments[0], expected.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported schema major '{version}'.");
    }

    public static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported {name} '{actual}'.");
    }

    public static void RequireText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException($"{name} must contain between 1 and {maximum} characters.", name);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) ||
                (char.IsLowSurrogate(character) && (index == 0 || !char.IsHighSurrogate(value[index - 1]))) ||
                (char.IsHighSurrogate(character) && (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))))
            {
                throw new ArgumentException($"{name} must contain canonical Unicode scalar text without controls.", name);
            }
            if (char.IsHighSurrogate(character)) index++;
        }
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be a non-default UTC instant.", name);
    }

    public static void RequireSoulId(string value) => RequirePrefixedHex(value, "soul_", 64, nameof(value));
    public static void RequireDeviceBindingId(string value) => RequirePrefixedHex(value, "db_", 32, nameof(value));
    public static void RequirePlatformAccountId(string value) => RequirePrefixedHex(value, "pa_", 32, nameof(value));
    public static void RequireTraceId(string value) => RequirePrefixedHex(value, "trace_", 32, nameof(value));
    public static void RequireIdempotencyKey(string value) => RequirePrefixedHex(value, "idem_", 64, nameof(value));
    public static void RequireSha256(string value, string name) => RequirePrefixedHex(value, string.Empty, 64, name);

    public static void RequirePrefixedHex(string value, string prefix, int hexLength, string name)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + hexLength ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"{name} is not a canonical lowercase hexadecimal identifier.", name);
        }
    }

}
