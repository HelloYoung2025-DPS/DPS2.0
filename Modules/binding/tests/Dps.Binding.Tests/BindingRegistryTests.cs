using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Dps.Binding.Contracts;
using Dps.DeviceRegistry;
using Dps.DeviceRegistry.Contracts;
using Dps.PlatformAccountRegistry;
using Dps.PlatformAccountRegistry.Contracts;
using Xunit;

namespace Dps.Binding.Tests;

public sealed class BindingRegistryTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSoul = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BindingId = "db_11111111111111111111111111111111";
    private const string AccountId = "pa_22222222222222222222222222222222";
    private const string DeviceId = "device_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset OccurredAt = new(2026, 7, 14, 1, 2, 3, TimeSpan.Zero);
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Unit")]
    public async Task ExactCurrentProviderTruthCreatesOneIdempotentBinding()
    {
        var readers = Readers();
        var registry = new InMemoryBindingRegistry(readers.Device, readers.Account);
        var command = Command("idem-binding-1");

        var first = await registry.BindAsync(command, TestCancellation);
        var duplicate = await registry.BindAsync(command, TestCancellation);

        Assert.Equal(first, duplicate);
        Assert.Equal("active", first.Status);
        Assert.Equal(7, first.DeviceRegistrationRevision);
        Assert.Equal(11, first.AccountAuthorizationRevision);
        Assert.Equal(first, await registry.GetAsync(Soul, BindingId, AccountId, TestCancellation));
        Assert.Equal(1, readers.Device.CallCount);
        Assert.Equal(1, readers.Account.CallCount);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task CallerCannotSubmitProofAndReaderScopeMismatchFailsClosed()
    {
        var properties = typeof(CreateBindingCommand).GetProperties().Select(static property => property.PropertyType).ToArray();
        Assert.DoesNotContain(typeof(DeviceRegisteredV1), properties);
        Assert.DoesNotContain(typeof(PlatformAccountAuthorizedV1), properties);
        Assert.DoesNotContain(typeof(InMemoryBindingRegistry).Assembly.GetExportedTypes(), static type => type.Name.EndsWith("Proof", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(InMemoryBindingRegistry).Assembly.GetExportedTypes(), static type => type.Name.EndsWith("Reader", StringComparison.Ordinal));
        Assert.Empty(typeof(PostgresBindingRegistry).GetConstructors());
        var compositionFactory = Assert.Single(typeof(PostgresBindingRegistry).GetMethods(), static method => method.Name == "CreateForCompositionAsync");
        Assert.Equal(5, compositionFactory.GetParameters().Length);
        Assert.Equal(typeof(IDeviceBindingReservationClient), compositionFactory.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(IPlatformAccountBindingReservationClient), compositionFactory.GetParameters()[2].ParameterType);
        Assert.Equal(typeof(SignedBindingCompositionAttestationV1), compositionFactory.GetParameters()[3].ParameterType);
        Assert.Equal(typeof(CancellationToken), compositionFactory.GetParameters()[4].ParameterType);
        Assert.DoesNotContain(typeof(PostgresDeviceRegistry).GetMethods(), static method =>
            method.Name is "ReserveBindingAsync" or "ConfirmBindingAsync" or "ReleaseBindingAsync");
        Assert.DoesNotContain(typeof(PostgresPlatformAccountRegistry).GetMethods(), static method =>
            method.Name is "ReserveBindingAsync" or "ConfirmBindingAsync" or "ReleaseBindingAsync");

        var readers = Readers(deviceMutation: value => value with { SoulId = OtherSoul });
        var registry = new InMemoryBindingRegistry(readers.Device, readers.Account);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.BindAsync(Command("idem-binding-2"), TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public void ModuleAssembliesExposeNoCrossModuleInternalsVisibleTo()
    {
        Assert.Equal(
            ["Dps.Binding.Tests"],
            FriendAssemblies(typeof(PostgresBindingRegistry).Assembly));
        Assert.Equal(
            ["Dps.DeviceRegistry.Tests"],
            FriendAssemblies(typeof(PostgresDeviceRegistry).Assembly));
        Assert.Equal(
            ["Dps.PlatformAccountRegistry.Tests"],
            FriendAssemblies(typeof(PostgresPlatformAccountRegistry).Assembly));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ProviderCommandDeadlineStopsAnImplementationThatIgnoresCancellation()
    {
        CancellationToken observed = default;
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() => BindingProviderCommandDeadline.ExecuteAsync(
            token =>
            {
                observed = token;
                return never.Task;
            },
            TimeSpan.FromMilliseconds(50),
            TestCancellation));

        Assert.True(observed.IsCancellationRequested);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ProductionCompositionRejectsCallerSignedProviderImplementations()
    {
        using var callerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceClient = new CallerDeviceClient();
        var accountClient = new CallerAccountClient();
        var now = OccurredAt;
        var options = new PostgresBindingRegistryOptions("Host=127.0.0.1;Database=not_opened", "binding_test");
        var unsigned = UnsignedCompositionAttestation(deviceClient, accountClient, options, now, 7);
        var signed = unsigned with
        {
            SignatureBase64 = Convert.ToBase64String(callerKey.SignData(
                BindingCompositionAttestationVerifier.Canonicalize(unsigned),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };

        BindingCompositionAttestationVerifier.Verify(
            signed,
            deviceClient,
            accountClient,
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(options),
            options.TrustEpoch,
            callerKey.ExportSubjectPublicKeyInfo(),
            now);
        Assert.Throws<UnauthorizedAccessException>(() => BindingCompositionAttestationVerifier.Verify(
            signed with { DeviceRegistryInstanceConfigurationSha256 = new string('f', 64) },
            deviceClient,
            accountClient,
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(options),
            options.TrustEpoch,
            callerKey.ExportSubjectPublicKeyInfo(),
            now));
        Assert.Throws<UnauthorizedAccessException>(() => BindingCompositionAttestationVerifier.Verify(
            signed,
            deviceClient,
            accountClient,
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(options),
            options.TrustEpoch,
            callerKey.ExportSubjectPublicKeyInfo(),
            signed.ExpiresAt));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PostgresBindingRegistry.CreateForCompositionAsync(
            options,
            deviceClient,
            accountClient,
            signed,
            TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public void CompositionGenerationFenceRejectsRollbackAndSameGenerationEquivocation()
    {
        var bom = new string('a', 64);
        var descriptor = new string('b', 64);
        PostgresBindingRegistry.EnsureCompositionGenerationTransition(null, null, null, 7, bom, descriptor);
        PostgresBindingRegistry.EnsureCompositionGenerationTransition(7, bom, descriptor, 7, bom, descriptor);
        PostgresBindingRegistry.EnsureCompositionGenerationTransition(7, bom, descriptor, 8, new string('c', 64), new string('d', 64));

        Assert.Throws<UnauthorizedAccessException>(() =>
            PostgresBindingRegistry.EnsureCompositionGenerationTransition(7, bom, descriptor, 6, bom, descriptor));
        Assert.Throws<UnauthorizedAccessException>(() =>
            PostgresBindingRegistry.EnsureCompositionGenerationTransition(7, bom, descriptor, 7, new string('c', 64), descriptor));
        Assert.Throws<UnauthorizedAccessException>(() =>
            PostgresBindingRegistry.EnsureCompositionGenerationTransition(7, bom, descriptor, 7, bom, new string('c', 64)));
    }

    [Fact, Trait("Category", "Unit")]
    public void BindingInstanceIdentityAndTestSignerFailuresDoNotLeakSecretsOrPaths()
    {
        var first = new PostgresBindingRegistryOptions(
            "Host=db.internal;Port=5432;Database=dps;Username=binding;Password=first-secret",
            "binding",
            7);
        var rotatedPassword = first with
        {
            ConnectionString = "Host=db.internal;Port=5432;Database=dps;Username=binding;Password=second-secret"
        };
        var wrongDatabase = first with
        {
            ConnectionString = "Host=db.internal;Port=5432;Database=attacker;Username=binding;Password=first-secret"
        };

        Assert.Equal(
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(first),
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(rotatedPassword));
        Assert.NotEqual(
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(first),
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(wrongDatabase));
        Assert.DoesNotContain("secret", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TrustEpoch = 7", first.ToString(), StringComparison.Ordinal);

        var externalKeyPath = Path.Combine(
            Path.GetTempPath(),
            "dps-binding-missing-external-key-" + Guid.NewGuid().ToString("N") + ".pk8");
        var loadFailure = Assert.Throws<InvalidOperationException>(() =>
            BindingPlatformAuthorizationEvidenceFactory.ReadExternalPrivateKeyFile(externalKeyPath));
        Assert.Null(loadFailure.InnerException);
        Assert.DoesNotContain(externalKeyPath, loadFailure.ToString(), StringComparison.Ordinal);
        Assert.Equal("approval_mixed-case", BindingPlatformAuthorizationEvidenceFactory.EvidenceId("MiXeD-case"));
    }

    [Fact, Trait("Category", "Unit")]
    public void PinnedProductionRootAcceptsGoldenSignatureAndRejectsEveryCriticalTamper()
    {
        var golden = GoldenCompositionAttestation();

        BindingCompositionAttestationVerifier.VerifyPinnedRootSignature(golden);
        Assert.Throws<UnauthorizedAccessException>(() =>
            BindingCompositionAttestationVerifier.VerifyPinnedRootSignature(golden with { ReleaseBomSha256 = new string('6', 64) }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            BindingCompositionAttestationVerifier.VerifyPinnedRootSignature(golden with { Generation = 2 }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            BindingCompositionAttestationVerifier.VerifyPinnedRootSignature(golden with { DeviceRegistryInstanceConfigurationSha256 = new string('6', 64) }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            BindingCompositionAttestationVerifier.VerifyPinnedRootSignature(golden with { PlatformAccountRegistryContractsArtifactSha256 = new string('6', 64) }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            BindingCompositionAttestationVerifier.VerifyPinnedRootSignature(golden with { ExpiresAt = golden.ExpiresAt.AddMinutes(1) }));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task MutationFenceLeaseDisposeIsIdempotentAndNeverMasksCommittedWork()
    {
        var releaseCalls = 0;
        var receipt = new BindingMutationFenceV1(
            BindingMutationFenceV1.CurrentSchemaVersion,
            BindingMutationFenceV1.CurrentContractId,
            BindingMutationFenceV1.CurrentProducerModule,
            Soul,
            BindingId,
            AccountId,
            Trace("fence-release"),
            Idempotency("persona-mutation-release"),
            OccurredAt,
            "sensitive",
            1,
            "bfence_" + new string('1', 64),
            1,
            "held");
        var lease = new PostgresBindingMutationFenceLease(
            receipt,
            () =>
            {
                releaseCalls++;
                return ValueTask.FromException(new InvalidOperationException("simulated release failure after consumer commit"));
            });

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, releaseCalls);
    }

    [Fact, Trait("Category", "Unit")]
    public void ProviderReservationReceiptsMustProveExactScopeRevisionStateAndLiveLease()
    {
        var reservationId = "bres_" + new string('a', 64);
        var binding = BindingValidation.CreateResult(
            Soul,
            BindingId,
            AccountId,
            DeviceId,
            1,
            "active",
            7,
            11,
            Trace("provider-receipt"),
            Idempotency("provider-receipt"),
            OccurredAt);
        var device = new DeviceBindingReservationV1(
            DeviceBindingReservationV1.CurrentSchemaVersion,
            DeviceBindingReservationV1.CurrentContractId,
            DeviceBindingReservationV1.CurrentProducerModule,
            Soul,
            BindingId,
            AccountId,
            binding.TraceId,
            BindingProviderReservationReceiptValidation.CreateDeviceReceiptIdempotencyKey(reservationId, "held"),
            OccurredAt,
            "sensitive",
            reservationId,
            DeviceId,
            7,
            "held",
            OccurredAt.AddMinutes(5));
        var account = new PlatformAccountBindingReservationV1(
            PlatformAccountBindingReservationV1.CurrentSchemaVersion,
            PlatformAccountBindingReservationV1.CurrentContractId,
            PlatformAccountBindingReservationV1.CurrentProducerModule,
            Soul,
            BindingId,
            AccountId,
            binding.TraceId,
            BindingProviderReservationReceiptValidation.CreatePlatformAccountReceiptIdempotencyKey(reservationId, "active"),
            OccurredAt,
            "sensitive",
            reservationId,
            11,
            "active",
            null);

        BindingProviderReservationReceiptValidation.EnsureDevice(
            device, binding, reservationId, binding.TraceId, OccurredAt, "held", OccurredAt);
        BindingProviderReservationReceiptValidation.EnsureAccount(
            account, binding, reservationId, binding.TraceId, OccurredAt, "active", OccurredAt);
        Assert.Throws<InvalidOperationException>(() => BindingProviderReservationReceiptValidation.EnsureDevice(
            device with { DeviceRegistrationRevision = 8 },
            binding, reservationId, binding.TraceId, OccurredAt, "held", OccurredAt));
        Assert.Throws<InvalidOperationException>(() => BindingProviderReservationReceiptValidation.EnsureDevice(
            device with { SoulId = OtherSoul },
            binding, reservationId, binding.TraceId, OccurredAt, "held", OccurredAt));
        Assert.Throws<InvalidOperationException>(() => BindingProviderReservationReceiptValidation.EnsureDevice(
            device with { LeaseExpiresAt = OccurredAt },
            binding, reservationId, binding.TraceId, OccurredAt, "held", OccurredAt));
        Assert.Throws<InvalidOperationException>(() => BindingProviderReservationReceiptValidation.EnsureDevice(
            device with { LeaseExpiresAt = OccurredAt.AddYears(1) },
            binding, reservationId, binding.TraceId, OccurredAt, "held", OccurredAt));
        Assert.Throws<InvalidOperationException>(() => BindingProviderReservationReceiptValidation.EnsureAccount(
            account with
            {
                State = "released",
                IdempotencyKey = BindingProviderReservationReceiptValidation.CreatePlatformAccountReceiptIdempotencyKey(
                    reservationId,
                    "released")
            },
            binding, reservationId, binding.TraceId, OccurredAt, "active", OccurredAt));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("retired", "authorized")]
    [InlineData("registered", "suspended")]
    [InlineData("registered", "revoked")]
    public async Task InactiveProviderTruthCannotCreateBinding(string deviceStatus, string accountStatus)
    {
        var readers = Readers(
            deviceMutation: value => value with { Status = deviceStatus },
            accountMutation: value => value with { Status = accountStatus });
        var registry = new InMemoryBindingRegistry(readers.Device, readers.Account);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.BindAsync(Command("idem-inactive"), TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ConflictingIdempotencyScopeAndCrossSoulReadFailClosed()
    {
        var readers = Readers();
        var registry = new InMemoryBindingRegistry(readers.Device, readers.Account);
        var command = Command("idem-binding-3");
        await registry.BindAsync(command, TestCancellation);

        await Assert.ThrowsAsync<BindingIdempotencyConflictException>(() =>
            registry.BindAsync(command with { DeviceId = "device_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }, TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.GetAsync(OtherSoul, BindingId, AccountId, TestCancellation));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task RevocationIsIdempotentAndRevokedBindingCannotBeResurrected()
    {
        var readers = Readers();
        var registry = new InMemoryBindingRegistry(readers.Device, readers.Account);
        var active = await registry.BindAsync(Command("idem-binding-4"), TestCancellation);
        var revoke = new RevokeBindingCommand(
            Soul,
            BindingId,
            AccountId,
            active.BindingRevision,
            Trace("binding-revoke"),
            Idempotency("binding-5"),
            OccurredAt.AddMinutes(1));

        var revoked = await registry.RevokeAsync(revoke, TestCancellation);
        Assert.Equal(revoked, await registry.RevokeAsync(revoke, TestCancellation));
        Assert.Equal(2, revoked.BindingRevision);
        Assert.Equal("revoked", revoked.Status);
        await Assert.ThrowsAsync<BindingHistoricalReceiptException>(() =>
            registry.BindAsync(Command("idem-binding-4"), TestCancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.BindAsync(Command("idem-binding-6") with { OccurredAt = OccurredAt.AddMinutes(2) }, TestCancellation));
    }

    [Fact, Trait("Category", "Contract")]
    public async Task ContractRejectsUnknownMajorAndNoncanonicalIdentifiers()
    {
        var readers = Readers();
        var value = await new InMemoryBindingRegistry(readers.Device, readers.Account)
            .BindAsync(Command("idem-binding-contract"), TestCancellation);
        value.Validate();
        var json = JsonSerializer.Serialize(value);

        Assert.Contains("\"device_binding_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"binding_revision\"", json, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "2.0.0" }).Validate());
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "1.1.0" }).Validate());
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "1.evil" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { PlatformAccountId = Guid.NewGuid().ToString() }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void MutationFenceContractIsVersionedAndCannotCarryCallerProof()
    {
        Assert.DoesNotContain(
            typeof(AcquireBindingMutationFenceCommand).GetProperties(),
            static property => property.Name.Contains("Revision", StringComparison.Ordinal) ||
                               property.PropertyType == typeof(IdentityBindingV1));
        var receipt = new BindingMutationFenceV1(
            BindingMutationFenceV1.CurrentSchemaVersion,
            BindingMutationFenceV1.CurrentContractId,
            BindingMutationFenceV1.CurrentProducerModule,
            Soul,
            BindingId,
            AccountId,
            Trace("fence"),
            Idempotency("persona-mutation"),
            OccurredAt,
            "sensitive",
            1,
            "bfence_" + new string('1', 64),
            1,
            "held");

        receipt.Validate();
        Assert.Throws<NotSupportedException>(() => (receipt with { SchemaVersion = "2.0.0" }).Validate());
        Assert.Throws<NotSupportedException>(() => (receipt with { SchemaVersion = "1.1.0" }).Validate());
        Assert.Throws<ArgumentException>(() => (receipt with { FenceId = "caller-proof" }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void CompatibilityManifestRejectsProvidersWithoutReservationClients()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(FindBindingManifest()));
        var dependencies = manifest.RootElement.GetProperty("dependencies")
            .EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("moduleId").GetString()!,
                static item => item.GetProperty("versionRange").GetString()!,
                StringComparer.Ordinal);

        Assert.Equal(">=0.4.0 <1.0.0", dependencies["device-registry"]);
        Assert.Equal(">=0.4.0 <1.0.0", dependencies["platform-account-registry"]);
        Assert.DoesNotContain(">=0.1.0", dependencies.Values);
    }

    [Fact, Trait("Category", "Contract")]
    public void CompositionAttestationContractRejectsMalformedVersionsAndSignatures()
    {
        var value = UnsignedCompositionAttestation(
            new CallerDeviceClient(),
            new CallerAccountClient(),
            new PostgresBindingRegistryOptions("Host=127.0.0.1;Database=contract", "binding_contract"),
            OccurredAt,
            1);

        value.ValidateShape();
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "1.evil" }).ValidateShape());
        Assert.Throws<NotSupportedException>(() => (value with { SchemaVersion = "1.1.0" }).ValidateShape());
        Assert.Throws<ArgumentException>(() => (value with { SignatureBase64 = "not-base64" }).ValidateShape());
    }

    [Fact, Trait("Category", "Contract")]
    public void CompositionAttestationJsonBoundaryIsStrictAndPinnedGoldenRemainsVerifiable()
    {
        var golden = GoldenCompositionAttestation();
        var json = JsonSerializer.Serialize(golden);
        var missingHost = JsonNode.Parse(json)!.AsObject();
        Assert.True(missingHost.Remove("composition_host_artifact_sha256"));

        var parsed = BindingContractJson.DeserializeStrict<SignedBindingCompositionAttestationV1>(json);
        Assert.Equal(golden, parsed);
        BindingCompositionAttestationVerifier.VerifyPinnedRootSignature(parsed);
        Assert.Throws<JsonException>(() => BindingContractJson.DeserializeStrict<SignedBindingCompositionAttestationV1>(
            missingHost.ToJsonString()));
        Assert.Throws<JsonException>(() => BindingContractJson.DeserializeStrict<SignedBindingCompositionAttestationV1>(
            json.Replace("{", "{\"unknown_field\":true,", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => BindingContractJson.DeserializeStrict<SignedBindingCompositionAttestationV1>(
            json.Replace("{", $"{{\"trace_id\":\"{golden.TraceId}\",", StringComparison.Ordinal)));
    }

    [Fact, Trait("Category", "Contract")]
    public async Task StrictContractJsonRejectsMissingUnknownDuplicateAndSecretShapedEnvelopeFields()
    {
        var readers = Readers();
        var value = await new InMemoryBindingRegistry(readers.Device, readers.Account)
            .BindAsync(Command("strict-contract-json"), TestCancellation);
        var json = JsonSerializer.Serialize(value);
        var missingOccurredAt = JsonNode.Parse(json)!.AsObject();
        Assert.True(missingOccurredAt.Remove("occurred_at"));

        Assert.Equal(value, BindingContractJson.DeserializeStrict<IdentityBindingV1>(json));
        Assert.Throws<JsonException>(() => BindingContractJson.DeserializeStrict<IdentityBindingV1>(
            json.Replace("{", "{\"unknown_field\":true,", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => BindingContractJson.DeserializeStrict<IdentityBindingV1>(
            missingOccurredAt.ToJsonString()));
        Assert.Throws<JsonException>(() => BindingContractJson.DeserializeStrict<IdentityBindingV1>(
            json.Replace("{", $"{{\"trace_id\":\"{value.TraceId}\",", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => (value with { DeviceBindingId = "db_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { PlatformAccountId = "pa_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { TraceId = "trace_user@example.com" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { IdempotencyKey = "idem_Bearer-secret-token" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { DeviceBindingId = value.DeviceBindingId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { PlatformAccountId = value.PlatformAccountId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { TraceId = value.TraceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { IdempotencyKey = value.IdempotencyKey + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => BindingContractValidation.RequireText("\ud800", 16, "isolatedSurrogate"));
    }

    [Fact, Trait("Category", "Contract")]
    public void SchemasUseAbsoluteEndAnchorsForEveryCanonicalEnvelopeIdentifier()
    {
        var moduleRoot = Path.GetDirectoryName(FindBindingManifest())!;
        const string exactUtcPattern = "^(?!0000-)\\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\\d|3[01])T(?:[01]\\d|2[0-3]):[0-5]\\d:[0-5]\\d(?:\\.\\d{1,7})?(?:Z|\\+00:00)$(?![\\s\\S])";
        foreach (var file in new[]
                 {
                     "identity.binding.v1.schema.json",
                     "identity.binding.mutation-fence.v1.schema.json",
                     "binding.composition.attestation.v1.schema.json"
                 })
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(moduleRoot, "contracts", "provided", file)));
            var properties = schema.RootElement.GetProperty("properties");
            Assert.Equal("^db_[a-f0-9]{32}$(?![\\s\\S])", properties.GetProperty("device_binding_id").GetProperty("pattern").GetString());
            Assert.Equal("^pa_[a-f0-9]{32}$(?![\\s\\S])", properties.GetProperty("platform_account_id").GetProperty("pattern").GetString());
            Assert.Equal("^trace_[a-f0-9]{32}$(?![\\s\\S])", properties.GetProperty("trace_id").GetProperty("pattern").GetString());
            Assert.Equal("^idem_[a-f0-9]{64}$(?![\\s\\S])", properties.GetProperty("idempotency_key").GetProperty("pattern").GetString());
            Assert.Equal(exactUtcPattern, properties.GetProperty("occurred_at").GetProperty("pattern").GetString());
        }

        using var identitySchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            moduleRoot, "contracts", "provided", "identity.binding.v1.schema.json")));
        var identity = identitySchema.RootElement.GetProperty("properties");
        foreach (var field in new[] { "binding_revision", "device_registration_revision", "account_authorization_revision" })
            Assert.Equal(long.MaxValue, identity.GetProperty(field).GetProperty("maximum").GetInt64());

        using var fenceSchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            moduleRoot, "contracts", "provided", "identity.binding.mutation-fence.v1.schema.json")));
        var fence = fenceSchema.RootElement.GetProperty("properties");
        Assert.Equal(long.MaxValue, fence.GetProperty("binding_revision").GetProperty("maximum").GetInt64());
        Assert.Equal(long.MaxValue, fence.GetProperty("fence_sequence").GetProperty("maximum").GetInt64());

        using var attestationSchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            moduleRoot, "contracts", "provided", "binding.composition.attestation.v1.schema.json")));
        var attestation = attestationSchema.RootElement.GetProperty("properties");
        Assert.Equal(exactUtcPattern, attestation.GetProperty("issued_at").GetProperty("pattern").GetString());
        Assert.Equal(exactUtcPattern, attestation.GetProperty("expires_at").GetProperty("pattern").GetString());
        foreach (var field in new[] { "generation", "binding_instance_trust_epoch", "device_registry_instance_trust_epoch", "platform_account_registry_instance_trust_epoch" })
            Assert.Equal(long.MaxValue, attestation.GetProperty(field).GetProperty("maximum").GetInt64());
    }

    [Fact, Trait("Category", "Contract")]
    public void SharedIdentityBindingBoundaryCorpusMatchesStrictConsumerRules()
    {
        var path = Path.Combine(
            Path.GetDirectoryName(FindBindingManifest())!,
            "contracts",
            "corpus",
            "identity.binding.v1.corpus.json");
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
            var parsed = BindingContractJson.DeserializeStrict<IdentityBindingV1>(
                entry.GetProperty("payload").GetRawText());
            Assert.Equal(IdentityBindingV1.CurrentContractId, parsed.ContractId);
        }
        foreach (var entry in invalid)
        {
            Assert.ThrowsAny<Exception>(() =>
                BindingContractJson.DeserializeStrict<IdentityBindingV1>(
                    entry.GetProperty("payload").GetRawText()));
        }
    }

    private static CreateBindingCommand Command(string idempotencyLabel) => new(
        Soul,
        BindingId,
        AccountId,
        DeviceId,
        Trace("binding"),
        Idempotency(idempotencyLabel),
        OccurredAt);

    private static string Trace(string label) => "trace_" + Digest("trace", label)[..32];

    private static string Idempotency(string label) => "idem_" + Digest("idempotency", label);

    private static string Digest(string domain, string label)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(domain + ":" + label)));

    private static SignedBindingCompositionAttestationV1 UnsignedCompositionAttestation(
        IDeviceBindingReservationClient deviceClient,
        IPlatformAccountBindingReservationClient accountClient,
        PostgresBindingRegistryOptions options,
        DateTimeOffset now,
        long generation)
        => new(
            SignedBindingCompositionAttestationV1.CurrentSchemaVersion,
            SignedBindingCompositionAttestationV1.CurrentContractId,
            SignedBindingCompositionAttestationV1.CurrentProducerModule,
            null,
            null,
            null,
            Trace("binding-composition"),
            Idempotency("binding-composition-generation-" + generation),
            now,
            "internal",
            SignedBindingCompositionAttestationV1.CurrentRootKeyId,
            new string('a', 64),
            generation,
            now,
            now.AddMinutes(5),
            BindingCompositionAttestationVerifier.ComputeBindingInstanceConfigurationSha256(options),
            options.TrustEpoch,
            BindingCompositionAttestationVerifier.ComputeImplementationArtifactSha256(typeof(PostgresBindingRegistry)),
            BindingCompositionAttestationVerifier.ComputeImplementationArtifactSha256(typeof(SignedBindingCompositionAttestationV1)),
            BindingCompositionAttestationVerifier.ComputeAssemblyArtifactSha256(System.Reflection.Assembly.GetEntryAssembly()!),
            deviceClient.InstanceConfigurationSha256,
            deviceClient.InstanceTrustEpoch,
            BindingCompositionAttestationVerifier.ComputeImplementationArtifactSha256(deviceClient.GetType()),
            BindingCompositionAttestationVerifier.ComputeImplementationArtifactSha256(typeof(IDeviceBindingReservationClient)),
            accountClient.InstanceConfigurationSha256,
            accountClient.InstanceTrustEpoch,
            BindingCompositionAttestationVerifier.ComputeImplementationArtifactSha256(accountClient.GetType()),
            BindingCompositionAttestationVerifier.ComputeImplementationArtifactSha256(typeof(IPlatformAccountBindingReservationClient)),
            Convert.ToBase64String(new byte[64]));

    private static SignedBindingCompositionAttestationV1 GoldenCompositionAttestation()
        => new(
            "1.0.0",
            "binding.composition.attestation/v1",
            "binding",
            null,
            null,
            null,
            "trace_11111111111111111111111111111111",
            "idem_2222222222222222222222222222222222222222222222222222222222222222",
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            "internal",
            "dps-binding-composition-root-2026-07",
            new string('a', 64),
            1,
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 0, 5, 0, TimeSpan.Zero),
            new string('b', 64),
            1,
            new string('c', 64),
            new string('d', 64),
            new string('e', 64),
            new string('f', 64),
            2,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            3,
            new string('4', 64),
            new string('5', 64),
            "WXdgYveURCxtxpi8MmMF2Lxsl4b9dtlRwXYvBYR4wGl/2XztFHFTxPNt3T/VFBSl9OZnrIUJuSH/M+KKu8DL5g==");

    private static string[] FriendAssemblies(System.Reflection.Assembly assembly)
        => assembly.GetCustomAttributes(typeof(InternalsVisibleToAttribute), inherit: false)
            .Cast<InternalsVisibleToAttribute>()
            .Select(static attribute => attribute.AssemblyName.Split(',', 2)[0])
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindBindingManifest()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Modules", "binding", "module.yaml");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Modules/binding/module.yaml was not found from the test output path.");
    }

    private static ReaderFixture Readers(
        Func<DeviceRegisteredV1, DeviceRegisteredV1>? deviceMutation = null,
        Func<PlatformAccountAuthorizedV1, PlatformAccountAuthorizedV1>? accountMutation = null)
    {
        var device = new DeviceRegisteredV1(
            DeviceRegisteredV1.CurrentSchemaVersion,
            DeviceRegisteredV1.CurrentContractId,
            DeviceRegisteredV1.CurrentProducerModule,
            Soul,
            BindingId,
            AccountId,
            Trace("device"),
            Idempotency("device"),
            OccurredAt.AddMinutes(-2),
            "sensitive",
            DeviceId,
            new string('1', 64),
            "fpkey_33333333333333333333333333333333",
            7,
            7,
            ["observe"],
            "registered");
        var account = new PlatformAccountAuthorizedV1(
            PlatformAccountAuthorizedV1.CurrentSchemaVersion,
            PlatformAccountAuthorizedV1.CurrentContractId,
            PlatformAccountAuthorizedV1.CurrentProducerModule,
            Soul,
            BindingId,
            AccountId,
            Trace("account"),
            Idempotency("account"),
            OccurredAt.AddMinutes(-1),
            "sensitive",
            "fixture",
            new string('2', 64),
            "binding-test-key",
            "approval_binding_fixture",
            11,
            "authorized",
            7);
        return new ReaderFixture(
            new StubDeviceReader(deviceMutation?.Invoke(device) ?? device),
            new StubAccountReader(accountMutation?.Invoke(account) ?? account));
    }

    private sealed record ReaderFixture(StubDeviceReader Device, StubAccountReader Account);

    private sealed class StubDeviceReader(DeviceRegisteredV1 value) : IDeviceRegistrationReader
    {
        public int CallCount { get; private set; }

        public Task<DeviceRegisteredV1> ReadCurrentAsync(
            string deviceId,
            string soulId,
            string deviceBindingId,
            string platformAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(value);
        }
    }

    private sealed class StubAccountReader(PlatformAccountAuthorizedV1 value) : IPlatformAccountAuthorizationReader
    {
        public int CallCount { get; private set; }

        public Task<PlatformAccountAuthorizedV1> ReadCurrentAsync(
            string platformAccountId,
            string soulId,
            string deviceBindingId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(value);
        }
    }

    private sealed class CallerDeviceClient : IDeviceBindingReservationClient
    {
        public string InstanceConfigurationSha256 => new('d', 64);
        public long InstanceTrustEpoch => 1;

        public Task<DeviceRegisteredV1> ReadCurrentAsync(string deviceId, string soulId, string deviceBindingId, string platformAccountId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DeviceBindingReservationV1> ReserveAsync(ReserveDeviceBindingCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DeviceBindingReservationV1> ConfirmAsync(DeviceBindingReservationCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DeviceBindingReservationV1> ReleaseAsync(DeviceBindingReservationCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CallerAccountClient : IPlatformAccountBindingReservationClient
    {
        public string InstanceConfigurationSha256 => new('e', 64);
        public long InstanceTrustEpoch => 1;

        public Task<PlatformAccountAuthorizedV1> ReadCurrentAsync(string platformAccountId, string soulId, string deviceBindingId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlatformAccountBindingReservationV1> ReserveAsync(ReservePlatformAccountBindingCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlatformAccountBindingReservationV1> ConfirmAsync(PlatformAccountBindingReservationCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlatformAccountBindingReservationV1> ReleaseAsync(PlatformAccountBindingReservationCommand command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
