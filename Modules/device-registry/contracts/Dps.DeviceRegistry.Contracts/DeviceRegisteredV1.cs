using System.Text.Json.Serialization;

namespace Dps.DeviceRegistry.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceRegisteredV1(
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
    [property: JsonPropertyName("fingerprint_hmac_sha256"), JsonRequired] string FingerprintHmacSha256,
    [property: JsonPropertyName("fingerprint_key_id"), JsonRequired] string FingerprintKeyId,
    [property: JsonPropertyName("fingerprint_key_epoch"), JsonRequired] long FingerprintKeyEpoch,
    [property: JsonPropertyName("capability_revision"), JsonRequired] long CapabilityRevision,
    [property: JsonPropertyName("capabilities"), JsonRequired] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("status"), JsonRequired] string Status)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "device.registered/v1";
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
        DeviceContractValidation.RequirePrefixedHex(DeviceId, "device_", 32, nameof(DeviceId));
        DeviceContractValidation.RequireSha256(FingerprintHmacSha256, nameof(FingerprintHmacSha256));
        DeviceContractValidation.RequireFingerprintKeyId(FingerprintKeyId);
        DeviceContractValidation.RequireFingerprintKeyEpoch(FingerprintKeyEpoch);
        if (CapabilityRevision < 1) throw new ArgumentOutOfRangeException(nameof(CapabilityRevision));
        DeviceContractValidation.RequireCanonicalCapabilities(Capabilities, nameof(Capabilities));
        if (Status is not ("registered" or "retired")) throw new ArgumentOutOfRangeException(nameof(Status));
    }
}

public static class DeviceContractValidation
{
    public const int MaximumCapabilityCount = 64;
    public const int MaximumCapabilityLength = 64;
    public const int MaximumCapabilityAsciiBytes = MaximumCapabilityCount * MaximumCapabilityLength;

    public static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported {name} '{actual}'.");
    }

    public static void RequireText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException($"{name} must contain between 1 and {maximum} characters.", name);
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name);
    }

    public static void RequireSha256(string value, string name) => RequirePrefixedHex(value, string.Empty, 64, name);

    public static void RequireSoulId(string value) => RequirePrefixedHex(value, "soul_", 64, nameof(value));

    public static void RequireDeviceBindingId(string value) => RequirePrefixedHex(value, "db_", 32, nameof(value));

    public static void RequirePlatformAccountId(string value) => RequirePrefixedHex(value, "pa_", 32, nameof(value));

    public static void RequireTraceId(string value) => RequirePrefixedHex(value, "trace_", 32, nameof(value));

    public static void RequireIdempotencyKey(string value) => RequirePrefixedHex(value, "idem_", 64, nameof(value));

    public static void RequireFingerprintKeyId(string value) => RequirePrefixedHex(value, "fpkey_", 32, nameof(value));

    public static void RequireFingerprintKeyEpoch(long value)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Fingerprint key epoch must be positive.");
    }

    public static void RequirePrefixedHex(string value, string prefix, int hexLength, string name)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + hexLength ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"{name} is not a canonical lowercase hexadecimal identifier.", name);
        }
    }

    public static void RequireCanonicalCapabilities(IReadOnlyList<string> capabilities, string name)
    {
        ArgumentNullException.ThrowIfNull(capabilities, name);
        if (capabilities.Count > MaximumCapabilityCount)
            throw new ArgumentException($"{name} cannot contain more than {MaximumCapabilityCount} items.", name);

        var observedCount = 0;
        var totalAsciiBytes = 0;
        string? previous = null;
        foreach (var capability in capabilities)
        {
            observedCount++;
            if (observedCount > MaximumCapabilityCount)
                throw new ArgumentException($"{name} cannot contain more than {MaximumCapabilityCount} items.", name);
            RequireCapabilityIdentifier(capability, name);
            totalAsciiBytes = checked(totalAsciiBytes + capability.Length);
            if (totalAsciiBytes > MaximumCapabilityAsciiBytes)
                throw new ArgumentException($"{name} exceeds the bounded ASCII payload.", name);
            if (previous is not null && StringComparer.Ordinal.Compare(previous, capability) >= 0)
                throw new ArgumentException($"{name} must be strictly sorted and unique.", name);
            previous = capability;
        }

        if (observedCount != capabilities.Count)
            throw new ArgumentException($"{name} reported an inconsistent item count.", name);
    }

    public static void RequireCapabilityIdentifier(string value, string name)
    {
        if (value is null || value.Length is < 1 or > MaximumCapabilityLength ||
            !IsAsciiLowerAlphaNumeric(value[0]) || !IsAsciiLowerAlphaNumeric(value[^1]))
        {
            throw new ArgumentException($"{name} contains an invalid capability identifier.", name);
        }

        var previousWasSeparator = false;
        foreach (var character in value)
        {
            if (IsAsciiLowerAlphaNumeric(character))
            {
                previousWasSeparator = false;
                continue;
            }

            if (character is not ('.' or '_' or '-') || previousWasSeparator)
                throw new ArgumentException($"{name} contains an invalid capability identifier.", name);
            previousWasSeparator = true;
        }
    }

    private static bool IsAsciiLowerAlphaNumeric(char value)
        => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
