using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Dps.DeviceRegistry.Contracts;

namespace Dps.DeviceRegistry;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterDeviceCommand(
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("fingerprint_hmac_sha256"), JsonRequired] string FingerprintHmacSha256,
    [property: JsonPropertyName("fingerprint_key_id"), JsonRequired] string FingerprintKeyId,
    [property: JsonPropertyName("fingerprint_key_epoch"), JsonRequired] long FingerprintKeyEpoch,
    [property: JsonPropertyName("capabilities"), JsonRequired] IReadOnlyCollection<string> Capabilities,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDeviceCapabilitiesCommand(
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("device_id"), JsonRequired] string DeviceId,
    [property: JsonPropertyName("expected_revision"), JsonRequired] long ExpectedRevision,
    [property: JsonPropertyName("capabilities"), JsonRequired] IReadOnlyCollection<string> Capabilities,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetireDeviceCommand(
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("device_id"), JsonRequired] string DeviceId,
    [property: JsonPropertyName("expected_revision"), JsonRequired] long ExpectedRevision,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt);

public interface IDeviceRegistry
{
    DeviceRegisteredV1 Register(RegisterDeviceCommand command);
    DeviceRegisteredV1 UpdateCapabilities(UpdateDeviceCapabilitiesCommand command);
    DeviceRegisteredV1 Retire(RetireDeviceCommand command);
    DeviceRegisteredV1 Get(string deviceId, string soulId, string deviceBindingId, string platformAccountId);
    bool IsRegistered(string deviceId, string soulId, string deviceBindingId, string platformAccountId);
}

public sealed partial class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceRegisteredV1> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PayloadKey, DeviceRegisteredV1 Result)> _idempotency = new(StringComparer.Ordinal);
    private readonly string _fingerprintKeyId;
    private readonly long _fingerprintKeyEpoch;

    public InMemoryDeviceRegistry(string fingerprintKeyId, long fingerprintKeyEpoch)
    {
        DeviceContractValidation.RequireFingerprintKeyId(fingerprintKeyId);
        DeviceContractValidation.RequireFingerprintKeyEpoch(fingerprintKeyEpoch);
        _fingerprintKeyId = fingerprintKeyId;
        _fingerprintKeyEpoch = fingerprintKeyEpoch;
    }

    public DeviceRegisteredV1 Register(RegisterDeviceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        DeviceContractValidation.RequireSha256(command.FingerprintHmacSha256, nameof(command.FingerprintHmacSha256));
        EnsureConfiguredFingerprintKey(command.FingerprintKeyId, command.FingerprintKeyEpoch);
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
        var capabilities = DeviceCapabilityNormalizer.Normalize(command.Capabilities);
        var fingerprint = command.FingerprintHmacSha256;
        var fingerprintIdentity = CreateFingerprintIdentity(command.FingerprintKeyId, command.FingerprintKeyEpoch, fingerprint);
        var payloadKey = string.Join(':', command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.FingerprintKeyId,
            command.FingerprintKeyEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fingerprint,
            string.Join(',', capabilities));

        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                EnsureSamePayload(prior.PayloadKey, payloadKey);
                return prior.Result;
            }

            if (_byFingerprint.TryGetValue(fingerprintIdentity, out var existingId))
            {
                var existing = _byId[existingId];
                if (!SameScope(existing, command.SoulId, command.DeviceBindingId, command.PlatformAccountId) ||
                    !existing.Capabilities.SequenceEqual(capabilities, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("The fingerprint is already registered under a different scope or capability set.");
                }
                _idempotency.Add(command.IdempotencyKey, (payloadKey, existing));
                return existing;
            }

            var registered = Create(CreateDeviceId(), command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
                fingerprint, command.FingerprintKeyId, command.FingerprintKeyEpoch, 1, capabilities,
                command.TraceId, command.IdempotencyKey, command.OccurredAt, "registered");
            _byId.Add(registered.DeviceId, registered);
            _byFingerprint.Add(fingerprintIdentity, registered.DeviceId);
            _idempotency.Add(command.IdempotencyKey, (payloadKey, registered));
            return registered;
        }
    }

    public DeviceRegisteredV1 UpdateCapabilities(UpdateDeviceCapabilitiesCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        DeviceContractValidation.RequirePrefixedHex(command.DeviceId, "device_", 32, nameof(command.DeviceId));
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
        var normalized = DeviceCapabilityNormalizer.Normalize(command.Capabilities);
        var payloadKey = string.Join(':', "update", command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.DeviceId, command.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Join(',', normalized));

        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                EnsureSamePayload(prior.PayloadKey, payloadKey);
                return prior.Result;
            }
            var current = GetUnderLock(command.DeviceId, command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
            EnsureNoEffectiveBindingReservationUnderLock(current.DeviceId);
            if (current.Status != "registered") throw new InvalidOperationException("A retired device cannot be updated.");
            if (current.CapabilityRevision != command.ExpectedRevision) throw new InvalidOperationException("Stale capability revision.");
            var updated = Create(current.DeviceId, current.SoulId, current.DeviceBindingId, current.PlatformAccountId,
                current.FingerprintHmacSha256, current.FingerprintKeyId, current.FingerprintKeyEpoch,
                current.CapabilityRevision + 1, normalized, command.TraceId, command.IdempotencyKey,
                command.OccurredAt, current.Status);
            _byId[current.DeviceId] = updated;
            _idempotency.Add(command.IdempotencyKey, (payloadKey, updated));
            return updated;
        }
    }

    public DeviceRegisteredV1 Retire(RetireDeviceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        DeviceContractValidation.RequirePrefixedHex(command.DeviceId, "device_", 32, nameof(command.DeviceId));
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
        var payloadKey = string.Join(':', "retire", command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.DeviceId, command.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                EnsureSamePayload(prior.PayloadKey, payloadKey);
                return prior.Result;
            }
            var current = GetUnderLock(command.DeviceId, command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
            EnsureNoEffectiveBindingReservationUnderLock(current.DeviceId);
            if (current.Status != "registered") throw new InvalidOperationException("Device is already retired.");
            if (current.CapabilityRevision != command.ExpectedRevision) throw new InvalidOperationException("Stale capability revision.");
            var retired = Create(current.DeviceId, current.SoulId, current.DeviceBindingId, current.PlatformAccountId,
                current.FingerprintHmacSha256, current.FingerprintKeyId, current.FingerprintKeyEpoch,
                current.CapabilityRevision + 1, current.Capabilities, command.TraceId, command.IdempotencyKey,
                command.OccurredAt, "retired");
            _byId[current.DeviceId] = retired;
            _idempotency.Add(command.IdempotencyKey, (payloadKey, retired));
            return retired;
        }
    }

    public DeviceRegisteredV1 Get(string deviceId, string soulId, string deviceBindingId, string platformAccountId)
    {
        ValidateScope(soulId, deviceBindingId, platformAccountId);
        DeviceContractValidation.RequirePrefixedHex(deviceId, "device_", 32, nameof(deviceId));
        lock (_gate) return GetUnderLock(deviceId, soulId, deviceBindingId, platformAccountId);
    }

    public bool IsRegistered(string deviceId, string soulId, string deviceBindingId, string platformAccountId) =>
        Get(deviceId, soulId, deviceBindingId, platformAccountId).Status == "registered";

    private DeviceRegisteredV1 GetUnderLock(string deviceId, string soulId, string bindingId, string accountId)
    {
        if (!_byId.TryGetValue(deviceId, out var value)) throw new KeyNotFoundException("Unknown device.");
        if (!SameScope(value, soulId, bindingId, accountId)) throw new UnauthorizedAccessException("Device scope mismatch.");
        return value;
    }

    private static DeviceRegisteredV1 Create(string deviceId, string soulId, string bindingId, string accountId,
        string fingerprintHmacSha256, string fingerprintKeyId, long fingerprintKeyEpoch, long revision,
        IReadOnlyList<string> capabilities, string traceId, string idempotencyKey,
        DateTimeOffset occurredAt, string status)
    {
        var value = new DeviceRegisteredV1(DeviceRegisteredV1.CurrentSchemaVersion, DeviceRegisteredV1.CurrentContractId,
            DeviceRegisteredV1.CurrentProducerModule, soulId, bindingId, accountId, traceId, idempotencyKey, occurredAt,
            "sensitive", deviceId, fingerprintHmacSha256, fingerprintKeyId, fingerprintKeyEpoch,
            revision, capabilities, status);
        value.Validate();
        return value;
    }

    private static bool SameScope(DeviceRegisteredV1 value, string soulId, string bindingId, string accountId) =>
        value.SoulId == soulId && value.DeviceBindingId == bindingId && value.PlatformAccountId == accountId;

    private static void ValidateScope(string soulId, string bindingId, string accountId)
    {
        DeviceContractValidation.RequireSoulId(soulId);
        DeviceContractValidation.RequireDeviceBindingId(bindingId);
        DeviceContractValidation.RequirePlatformAccountId(accountId);
    }

    private static void ValidateEnvelope(string traceId, string idempotencyKey, DateTimeOffset occurredAt)
    {
        DeviceContractValidation.RequireTraceId(traceId);
        DeviceContractValidation.RequireIdempotencyKey(idempotencyKey);
        DeviceContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }

    private void EnsureConfiguredFingerprintKey(string fingerprintKeyId, long fingerprintKeyEpoch)
    {
        DeviceContractValidation.RequireFingerprintKeyId(fingerprintKeyId);
        DeviceContractValidation.RequireFingerprintKeyEpoch(fingerprintKeyEpoch);
        if (!string.Equals(fingerprintKeyId, _fingerprintKeyId, StringComparison.Ordinal) ||
            fingerprintKeyEpoch != _fingerprintKeyEpoch)
            throw new InvalidOperationException("The fingerprint HMAC key version is not active for registration.");
    }

    private static string CreateFingerprintIdentity(string keyId, long keyEpoch, string hmacSha256)
        => string.Concat(keyId, ":", keyEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", hmacSha256);

    private static string CreateDeviceId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        try { return "device_" + Convert.ToHexStringLower(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void EnsureSamePayload(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The idempotency key is bound to a different device mutation.");
    }
}

internal static class DeviceCapabilityNormalizer
{
    internal static IReadOnlyList<string> Normalize(IReadOnlyCollection<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count > DeviceContractValidation.MaximumCapabilityCount)
            throw new ArgumentException(
                $"Capabilities cannot contain more than {DeviceContractValidation.MaximumCapabilityCount} items.",
                nameof(capabilities));

        var observedCount = 0;
        var totalAsciiBytes = 0;
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            observedCount++;
            if (observedCount > DeviceContractValidation.MaximumCapabilityCount)
                throw new ArgumentException(
                    $"Capabilities cannot contain more than {DeviceContractValidation.MaximumCapabilityCount} items.",
                    nameof(capabilities));
            DeviceContractValidation.RequireCapabilityIdentifier(capability, nameof(capabilities));
            totalAsciiBytes = checked(totalAsciiBytes + capability.Length);
            if (totalAsciiBytes > DeviceContractValidation.MaximumCapabilityAsciiBytes)
                throw new ArgumentException("Capabilities exceed the bounded ASCII payload.", nameof(capabilities));
            normalized.Add(capability);
        }

        if (observedCount != capabilities.Count)
            throw new ArgumentException("Capabilities reported an inconsistent item count.", nameof(capabilities));
        return new ReadOnlyCollection<string>(normalized.ToArray());
    }
}
