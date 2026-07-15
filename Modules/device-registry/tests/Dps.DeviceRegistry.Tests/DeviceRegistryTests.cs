using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dps.DeviceRegistry.Contracts;
using Xunit;

namespace Dps.DeviceRegistry.Tests;

public sealed class DeviceRegistryTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Binding = "db_11111111111111111111111111111111";
    private const string Account = "pa_22222222222222222222222222222222";
    private const string FingerprintKeyId = "fpkey_33333333333333333333333333333333";
    private const long FingerprintKeyEpoch = 7;
    private const long TrustEpoch = 17;

    [Fact, Trait("Category", "Unit")]
    public void DuplicateRegistrationKeepsStableIdentityAndRejectsConflicts()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = Registry();
        var first = registry.Register(Register('a', ["adb", "ocr"], "device-1", now));
        var duplicate = registry.Register(Register('a', ["ocr", "adb"], "device-1", now));

        Assert.Equal(first.DeviceId, duplicate.DeviceId);
        Assert.StartsWith("device_", first.DeviceId, StringComparison.Ordinal);
        Assert.Equal(["adb", "ocr"], first.Capabilities);
        Assert.Throws<InvalidOperationException>(() => registry.Register(Register('b', ["adb"], "device-1", now)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Register('a', ["vision"], "device-2", now)));
    }

    [Fact, Trait("Category", "Unit")]
    public void CapabilityAndRetirementMutationsAreScopedVersionedAndIdempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = Registry();
        var first = registry.Register(Register('a', ["adb"], "device-1", now));
        var update = new UpdateDeviceCapabilitiesCommand(
            Soul, Binding, Account, first.DeviceId, 1, ["vision", "adb"],
            Trace("device-2"), Idempotency("device-2"), now);
        var updated = registry.UpdateCapabilities(update);

        Assert.Equal(2, updated.CapabilityRevision);
        Assert.Equal(updated, registry.UpdateCapabilities(update));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Get(
            first.DeviceId,
            Soul.Replace('a', 'b'),
            Binding,
            Account));
        Assert.Throws<InvalidOperationException>(() => registry.UpdateCapabilities(
            update with { ExpectedRevision = 1, IdempotencyKey = Idempotency("device-3") }));

        var retired = registry.Retire(new RetireDeviceCommand(
            Soul, Binding, Account, first.DeviceId, 2,
            Trace("device-3"), Idempotency("device-4"), now));
        Assert.Equal("retired", retired.Status);
        Assert.False(registry.IsRegistered(first.DeviceId, Soul, Binding, Account));
    }

    [Fact, Trait("Category", "Unit")]
    public void EffectiveBindingReservationFreezesExactDeviceRevisionUntilRelease()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = Registry();
        var device = registry.Register(Register('a', ["adb"], "device-1", now));
        var reservationId = "bres_" + new string('1', 64);
        var held = registry.ReserveBinding(new ReserveDeviceBindingCommand(
            Soul, Binding, Account, device.DeviceId, device.CapabilityRevision, reservationId,
            Trace("reserve"), now));
        Assert.Equal("held", held.State);
        var reservation = new DeviceBindingReservationCommand(
            Soul, Binding, Account, device.DeviceId, device.CapabilityRevision, reservationId,
            Trace("confirm"), now);
        Assert.Equal("active", registry.ConfirmBinding(reservation).State);
        Assert.Throws<DeviceBindingReservationConflictException>(() => registry.UpdateCapabilities(
            new UpdateDeviceCapabilitiesCommand(
                Soul, Binding, Account, device.DeviceId, device.CapabilityRevision,
                ["adb", "verify"], Trace("update"), Idempotency("update"), now)));
        Assert.Throws<DeviceBindingReservationConflictException>(() => registry.Retire(
            new RetireDeviceCommand(
                Soul, Binding, Account, device.DeviceId, device.CapabilityRevision,
                Trace("retire"), Idempotency("retire"), now)));
        Assert.Equal("released", registry.ReleaseBinding(reservation with { TraceId = Trace("release") }).State);
        Assert.Equal(2, registry.UpdateCapabilities(
            new UpdateDeviceCapabilitiesCommand(
                Soul, Binding, Account, device.DeviceId, device.CapabilityRevision,
                ["adb", "verify"], Trace("update"), Idempotency("update"), now)).CapabilityRevision);
    }

    [Fact, Trait("Category", "Unit")]
    public void CapabilitiesAreCanonicalAsciiAndBoundedBeforeUntrustedEnumeration()
    {
        var registry = Registry();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => registry.Register(Register(
            'a', new DeclaredOversizeCapabilities(), "oversize-declared", now)));
        Assert.Throws<ArgumentException>(() => registry.Register(Register(
            'a', new LyingOverflowCapabilities(), "oversize-observed", now)));
        Assert.Throws<ArgumentException>(() => registry.Register(Register(
            'a', ["ADB"], "uppercase", now)));
        Assert.Throws<ArgumentException>(() => registry.Register(Register(
            'a', ["adb..shell"], "separator", now)));
        Assert.Throws<ArgumentException>(() => registry.Register(Register(
            'a', ["设备"], "unicode", now)));
        Assert.Throws<ArgumentException>(() => registry.Register(Register(
            'a', ["adb\n"], "trailing-newline", now)));
    }

    [Fact, Trait("Category", "Unit")]
    public void RegistrationRejectsUnconfiguredFingerprintKeyVersion()
    {
        var registry = Registry();
        var command = Register('a', ["adb"], "wrong-key", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => registry.Register(
            command with { FingerprintKeyId = "fpkey_44444444444444444444444444444444" }));
        Assert.Throws<InvalidOperationException>(() => registry.Register(
            command with { FingerprintKeyEpoch = FingerprintKeyEpoch + 1 }));
    }

    [Fact, Trait("Category", "Unit")]
    public void BindingProviderIdentityBindsNonSecretConfigurationAndTrustEpoch()
    {
        var options = new PostgresDeviceRegistryOptions(
            "Host=localhost;Port=5432;Database=dps;Username=young;Password=secret-one",
            "device_registry",
            FingerprintKeyId,
            FingerprintKeyEpoch,
            TrustEpoch);
        var configurationSha256 = DeviceRegistryProviderInstanceIdentity.Compute(options);

        Assert.Matches("^[a-f0-9]{64}$", configurationSha256);
        Assert.Equal(configurationSha256, DeviceRegistryProviderInstanceIdentity.Compute(
            options with
            {
                ConnectionString = "Host=localhost;Port=5432;Database=dps;Username=young;Password=secret-two"
            }));
        Assert.NotEqual(configurationSha256, DeviceRegistryProviderInstanceIdentity.Compute(
            options with { ConnectionString = "Host=127.0.0.1;Port=5432;Database=dps;Username=young;Password=secret-one" }));
        Assert.NotEqual(configurationSha256, DeviceRegistryProviderInstanceIdentity.Compute(
            options with { SchemaName = "device_registry_next" }));
        Assert.NotEqual(configurationSha256, DeviceRegistryProviderInstanceIdentity.Compute(
            options with { FingerprintKeyId = "fpkey_44444444444444444444444444444444" }));
        Assert.NotEqual(configurationSha256, DeviceRegistryProviderInstanceIdentity.Compute(
            options with { FingerprintKeyEpoch = FingerprintKeyEpoch + 1 }));
        Assert.NotEqual(configurationSha256, DeviceRegistryProviderInstanceIdentity.Compute(
            options with { TrustEpoch = TrustEpoch + 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceRegistryProviderInstanceIdentity.Compute(
            options with { TrustEpoch = 0 }));

        var client = new PostgresDeviceRegistry(options).CreateBindingReservationClient();
        Assert.Equal(configurationSha256, client.InstanceConfigurationSha256);
        Assert.Equal(TrustEpoch, client.InstanceTrustEpoch);
        Assert.DoesNotContain("secret-one", options.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", options.ToString(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => DeviceRegistryTestDatabase.ValidateTestDatabaseTarget(
            "Host=127.0.0.1;Port=55434;Database=device_test;Username=test;Password=test"));
        Assert.Throws<InvalidOperationException>(() => DeviceRegistryTestDatabase.ValidateTestDatabaseTarget(
            "Host=127.0.0.1;Port=5432;Database=dps_gbrain_company;Username=test;Password=test"));
    }

    [Fact, Trait("Category", "Contract")]
    public void RegisteredContractIsExactStrictAndKeyed()
    {
        var value = Registry().Register(Register('a', ["adb"], "device-1", DateTimeOffset.UtcNow));
        value.Validate();
        var json = JsonSerializer.Serialize(value);

        Assert.Contains("\"soul_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"device_binding_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fingerprint_hmac_sha256\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fingerprint_key_id\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint_sha256", json, StringComparison.Ordinal);
        Assert.DoesNotContain("not_applicable", json, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "1.0.1" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { SoulId = Guid.NewGuid().ToString() }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { SoulId = value.SoulId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { TraceId = "trace-not-hex" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { DeviceBindingId = value.DeviceBindingId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { PlatformAccountId = value.PlatformAccountId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { TraceId = value.TraceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { IdempotencyKey = value.IdempotencyKey + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { DeviceId = value.DeviceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { FingerprintHmacSha256 = value.FingerprintHmacSha256 + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { FingerprintKeyId = value.FingerprintKeyId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with
        {
            OccurredAt = value.OccurredAt.ToOffset(TimeSpan.FromHours(1))
        }).Validate());

        var withUnknown = JsonNode.Parse(json)!.AsObject();
        withUnknown["unknown"] = true;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceRegisteredV1>(withUnknown.ToJsonString()));

        var withMissing = JsonNode.Parse(json)!.AsObject();
        Assert.True(withMissing.Remove("fingerprint_key_id"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceRegisteredV1>(withMissing.ToJsonString()));
    }

    [Fact, Trait("Category", "Contract")]
    public void MutationCommandsUseStrictCanonicalJson()
    {
        var command = Register('a', ["adb"], "strict-command", DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(command);
        var roundTrip = JsonSerializer.Deserialize<RegisterDeviceCommand>(json)
            ?? throw new InvalidOperationException("The registration command did not deserialize.");
        Assert.Equal(command.FingerprintKeyId, roundTrip.FingerprintKeyId);
        Assert.Equal(command.FingerprintKeyEpoch, roundTrip.FingerprintKeyEpoch);
        Assert.Equal(command.IdempotencyKey, roundTrip.IdempotencyKey);
        Assert.True(command.Capabilities.SequenceEqual(roundTrip.Capabilities, StringComparer.Ordinal));

        var withUnknown = JsonNode.Parse(json)!.AsObject();
        withUnknown["extra"] = "rejected";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegisterDeviceCommand>(withUnknown.ToJsonString()));

        var withMissing = JsonNode.Parse(json)!.AsObject();
        Assert.True(withMissing.Remove("idempotency_key"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegisterDeviceCommand>(withMissing.ToJsonString()));

        Assert.Equal(typeof(string), typeof(IDeviceBindingReservationClient)
            .GetProperty(nameof(IDeviceBindingReservationClient.InstanceConfigurationSha256))?.PropertyType);
        Assert.Equal(typeof(long), typeof(IDeviceBindingReservationClient)
            .GetProperty(nameof(IDeviceBindingReservationClient.InstanceTrustEpoch))?.PropertyType);

        AssertSchemaRejectsTrailingNewline("device.registered.v1.schema.json");
        AssertSchemaRejectsTrailingNewline("device.binding.reservation.v1.schema.json");
    }

    [Fact, Trait("Category", "Contract")]
    public void BindingReservationContractIsVersionedStrictAndStateExpiryIsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = Registry();
        var device = registry.Register(Register('a', ["adb"], "device-1", now));
        var value = registry.ReserveBinding(new ReserveDeviceBindingCommand(
            Soul, Binding, Account, device.DeviceId, 1, "bres_" + new string('2', 64),
            Trace("reserve"), now));
        value.Validate();
        var json = JsonSerializer.Serialize(value);
        Assert.Contains("\"reservation_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lease_expires_at\"", json, StringComparison.Ordinal);
        Assert.Matches("^idem_[a-f0-9]{64}$", value.IdempotencyKey);
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "1.1.0" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { ReservationId = "caller-proof" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { ReservationId = value.ReservationId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { DeviceId = value.DeviceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { State = "active" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { IdempotencyKey = "idem_" + new string('0', 64) }).Validate());

        var withMissingNullable = JsonNode.Parse(json)!.AsObject();
        Assert.True(withMissingNullable.Remove("lease_expires_at"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceBindingReservationV1>(
            withMissingNullable.ToJsonString()));
    }

    [Fact, Trait("Category", "Contract")]
    public void SharedRegisteredDeviceBoundaryCorpusMatchesStrictConsumerRules()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Corpus",
            "device.registered.v1.corpus.json");
        var rawCorpus = File.ReadAllText(path);
        Assert.Contains("9223372036854775807", rawCorpus, StringComparison.Ordinal);
        Assert.Contains("9223372036854775808", rawCorpus, StringComparison.Ordinal);
        Assert.DoesNotContain("9223372036854776000", rawCorpus, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(rawCorpus);
        var valid = document.RootElement.GetProperty("valid").EnumerateArray().ToArray();
        var invalid = document.RootElement.GetProperty("invalid").EnumerateArray().ToArray();

        Assert.Equal(2, valid.Length);
        Assert.Equal(8, invalid.Length);
        foreach (var entry in valid)
        {
            var parsed = DeviceContractJson.DeserializeStrict<DeviceRegisteredV1>(
                entry.GetProperty("payload").GetRawText());
            Assert.Equal(DeviceRegisteredV1.CurrentContractId, parsed.ContractId);
        }
        foreach (var entry in invalid)
        {
            Assert.ThrowsAny<Exception>(() =>
                DeviceContractJson.DeserializeStrict<DeviceRegisteredV1>(
                    entry.GetProperty("payload").GetRawText()));
        }
    }

    private static InMemoryDeviceRegistry Registry()
        => new(FingerprintKeyId, FingerprintKeyEpoch);

    private static RegisterDeviceCommand Register(
        char digest,
        IReadOnlyCollection<string> capabilities,
        string idempotencyLabel,
        DateTimeOffset occurredAt)
        => new(
            Soul,
            Binding,
            Account,
            new string(digest, 64),
            FingerprintKeyId,
            FingerprintKeyEpoch,
            capabilities,
            Trace("register-" + idempotencyLabel),
            Idempotency(idempotencyLabel),
            occurredAt);

    private static string Trace(string value) => CanonicalToken("trace_", value, 16);

    private static string Idempotency(string value) => CanonicalToken("idem_", value, 32);

    private static void AssertSchemaRejectsTrailingNewline(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Contracts", fileName);
        var schema = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Schema '{fileName}' was not an object.");
        var properties = schema["properties"]?.AsObject()
            ?? throw new InvalidOperationException($"Schema '{fileName}' had no properties.");
        var identifiers = new List<(string Name, string Pattern, string Value)>
        {
            (Name: "soul_id", Pattern: "^soul_[a-f0-9]{64}$(?![\\s\\S])", Value: Soul),
            (Name: "device_binding_id", Pattern: "^db_[a-f0-9]{32}$(?![\\s\\S])", Value: Binding),
            (Name: "platform_account_id", Pattern: "^pa_[a-f0-9]{32}$(?![\\s\\S])", Value: Account),
            (Name: "trace_id", Pattern: "^trace_[a-f0-9]{32}$(?![\\s\\S])", Value: Trace("schema")),
            (Name: "idempotency_key", Pattern: "^idem_[a-f0-9]{64}$(?![\\s\\S])", Value: Idempotency("schema")),
            (Name: "device_id", Pattern: "^device_[a-f0-9]{32}$(?![\\s\\S])", Value: "device_" + new string('a', 32))
        };
        if (properties.ContainsKey("fingerprint_hmac_sha256"))
        {
            identifiers.Add(("fingerprint_hmac_sha256", "^[a-f0-9]{64}$(?![\\s\\S])", new string('b', 64)));
            identifiers.Add(("fingerprint_key_id", "^fpkey_[a-f0-9]{32}$(?![\\s\\S])", FingerprintKeyId));
        }
        if (properties.ContainsKey("reservation_id"))
            identifiers.Add(("reservation_id", "^bres_[a-f0-9]{64}$(?![\\s\\S])", "bres_" + new string('c', 64)));

        foreach (var identifier in identifiers)
        {
            var pattern = properties[identifier.Name]?["pattern"]?.GetValue<string>();
            Assert.Equal(identifier.Pattern, pattern);
            var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
            Assert.Matches(regex, identifier.Value);
            Assert.DoesNotMatch(regex, identifier.Value + "\n");
        }

        if (properties["capabilities"]?["items"]?["pattern"]?.GetValue<string>() is { } capabilityPattern)
        {
            Assert.Equal("^[a-z0-9]+(?:[._-][a-z0-9]+)*$(?![\\s\\S])", capabilityPattern);
            var capabilityRegex = new Regex(capabilityPattern, RegexOptions.CultureInvariant);
            Assert.Matches(capabilityRegex, "adb.shell");
            Assert.DoesNotMatch(capabilityRegex, "adb.shell\n");
        }
    }

    private static string CanonicalToken(string prefix, string value, int digestBytes)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        try
        {
            return prefix + Convert.ToHexStringLower(digest.AsSpan(0, digestBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private sealed class DeclaredOversizeCapabilities : IReadOnlyCollection<string>
    {
        public int Count => DeviceContractValidation.MaximumCapabilityCount + 1;
        public IEnumerator<string> GetEnumerator()
            => throw new InvalidOperationException("The declared bound should reject before enumeration.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LyingOverflowCapabilities : IReadOnlyCollection<string>
    {
        public int Count => 1;

        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; index <= DeviceContractValidation.MaximumCapabilityCount; index++)
                yield return "cap" + index.ToString("d2", System.Globalization.CultureInfo.InvariantCulture);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
