using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Xunit;

namespace Dps.ControlPlaneHost.Tests;

/// <summary>
/// Contract enforcement for the release binding pack: every corpus case is
/// executed against its strict C# codec (the codecValid column has a real
/// executor), and authority-produced DTOs round-trip the canonical wire.
/// </summary>
public sealed class ActiveReleaseBindingContractTests
{
    private const string Device = "db_11111111111111111111111111111111";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact, Trait("Category", "Contract")]
    public void RecoveryCapabilityIsSealedNominalAndHasOneModuleIssuer()
    {
        var contractAssembly = typeof(ActiveReleaseBindingRecoveryCapability).Assembly;
        Assert.Null(contractAssembly.GetType(
            "Dps.ControlPlaneHost.Contracts.IActiveReleaseBindingReader"));
        Assert.Null(contractAssembly.GetType(
            "Dps.ControlPlaneHost.Contracts.IActiveReleaseBindingRecoveryCoordinator"));
        Assert.Null(contractAssembly.GetType(
            "Dps.ControlPlaneHost.Contracts.IActiveReleaseBindingRecoveryScope"));

        AssertNominalPublicSurface(typeof(ActiveReleaseBindingRecoveryCapability));
        AssertNominalPublicSurface(typeof(ActiveReleaseBindingRecoveryLease));
        var factsSourceConstructor = Assert.Single(
            typeof(PolicyBoundReleaseBomFactsSource).GetConstructors());
        Assert.Equal(
            [typeof(ActiveReleaseBindingRecoveryCapability)],
            factsSourceConstructor.GetParameters()
                .Select(static parameter => parameter.ParameterType));

        var friends = contractAssembly
            .GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(static attribute => attribute.AssemblyName)
            .ToArray();
        Assert.Equal(["Dps.ControlPlaneHost"], friends);

        var issuer = Assert.IsAssignableFrom<Type>(contractAssembly.GetType(
            "Dps.ControlPlaneHost.Contracts.IActiveReleaseBindingRecoveryCapabilityIssuer"));
        Assert.False(issuer.IsPublic);
        var issuerTypes = typeof(ActiveReleaseBindingAuthority).Assembly
            .GetTypes()
            .Where(issuer.IsAssignableFrom)
            .ToArray();
        Assert.Equal([typeof(ActiveReleaseBindingAuthority)], issuerTypes);

        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var authority = new ActiveReleaseBindingAuthority(
            [new ReleaseBomTrustKey(
                "test-bom-key-v1",
                "test-release-controller",
                Convert.ToHexStringLower(parameters.Modulus!),
                65537)],
            InMemoryReleaseBindingTruthStore.CreateTestOnly(),
            () => Now);
        Assert.Same(authority.RecoveryCapability, authority.RecoveryCapability);
        Assert.False(authority.RecoveryCapability.IsDurable);
        Assert.Throws<InvalidOperationException>(
            () => authority.RecoveryCapability.RequireDurable());
        Assert.Throws<InvalidOperationException>(
            () => new PolicyBoundReleaseBomFactsSource(authority.RecoveryCapability));
        Assert.False(authority.RecoveryCapability.TryReadActive(Device, out _));
    }

    private static void AssertNominalPublicSurface(Type type)
    {
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        var constructors = type.GetConstructors(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(
            constructors,
            static constructor => constructor.IsPublic
                || constructor.IsFamily
                || constructor.IsFamilyOrAssembly);
        var declaredPublicMethods = type.GetMethods(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            declaredPublicMethods,
            static method => method.IsVirtual && !method.IsFinal);
        Assert.DoesNotContain(
            declaredPublicMethods.SelectMany(static method => method.GetParameters()),
            static parameter => parameter.ParameterType == typeof(object)
                || typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
    }

    [Fact, Trait("Category", "Contract")]
    public void ActiveReleaseBindingCorpusBindsTheCodec()
    {
        AssertCorpusCodec(
            "active.release.binding.v1.schema.json",
            "active.release.binding.v1.corpus.json",
            static payload => ActiveReleaseBindingV1Codec.Deserialize(payload));
    }

    [Fact, Trait("Category", "Contract")]
    public void ReleaseBindingReceiptCorpusBindsTheCodec()
    {
        AssertCorpusCodec(
            "release.binding.receipt.v1.schema.json",
            "release.binding.receipt.v1.corpus.json",
            static payload => ReleaseBindingReceiptV1Codec.Deserialize(payload));
    }

    [Fact, Trait("Category", "Contract")]
    public void AuthorityOutputsRoundTripTheStrictCodecs()
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var key = new ReleaseBomTrustKey(
            "test-bom-key-v1",
            "test-release-controller",
            Convert.ToHexStringLower(parameters.Modulus!),
            65537);
        var authority = new ActiveReleaseBindingAuthority(
            [key], InMemoryReleaseBindingTruthStore.CreateTestOnly(), () => Now);

        var (firstBom, firstToken) = SignBom(rsa, key.KeyId, "bom-1", 1, null);
        authority.Activate(Device, firstBom, firstToken);
        var firstStableBom = SignStableTwin(rsa, key.KeyId, firstBom);
        var (secondBom, secondToken) = SignBom(
            rsa, key.KeyId, "bom-2", 2, firstStableBom);
        authority.Activate(Device, secondBom, firstStableBom, secondToken);
        authority.Rollback(Device, firstToken);
        Assert.True(authority.TryReadActive(Device, out var binding));
        authority.Revoke(Device, binding!.Generation);

        // Every receipt kind and the binding itself survive a strict
        // Serialize -> Deserialize round trip byte-for-byte.
        var roundTrippedBinding = ActiveReleaseBindingV1Codec.Deserialize(
            ActiveReleaseBindingV1Codec.Serialize(binding));
        Assert.Equal(binding, roundTrippedBinding);
        var receipts = authority.ReadReceipts(Device);
        Assert.Equal(
            new[] { "activation", "activation", "rollback", "revocation" },
            receipts.Select(static receipt => receipt.ReceiptKind));
        foreach (var receipt in receipts)
        {
            var roundTripped = ReleaseBindingReceiptV1Codec.Deserialize(
                ReleaseBindingReceiptV1Codec.Serialize(receipt));
            Assert.Equal(receipt, roundTripped);
        }

        // A self-consistent payload digest cannot make an impossible
        // transition into a valid public receipt. Activation/rollback advance
        // the runtime generation exactly once; revocation preserves the exact
        // digest and generation while changing only active -> revoked.
        AssertSemanticallyInvalid(receipts[0] with
        {
            To = receipts[0].To with { Generation = receipts[0].To.Generation + 1 }
        });
        AssertSemanticallyInvalid(receipts[2] with
        {
            To = receipts[2].To with { Generation = receipts[2].To.Generation + 1 }
        });
        AssertSemanticallyInvalid(receipts[3] with
        {
            To = receipts[3].To with { Generation = receipts[3].To.Generation + 1 }
        });
        AssertSemanticallyInvalid(receipts[3] with
        {
            To = receipts[3].To with { ReleaseBomSha256 = new string('f', 64) }
        });
    }

    private static void AssertSemanticallyInvalid(ReleaseBindingReceiptV1 receipt)
    {
        var rebound = receipt with { PayloadSha256 = receipt.ComputePayloadSha256() };
        Assert.ThrowsAny<Exception>(() => ReleaseBindingReceiptV1Codec.Serialize(rebound));
    }

    private sealed class PinnedBindingReader(ActiveReleaseBindingV1 binding) : IActiveReleaseBindingReader
    {
        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? read)
        {
            read = binding;
            return true;
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void PolicyFactsSourceServesThePinnedSharedCorpusTriple()
    {
        // The same shared corpus valid case both consumers pin: the policy
        // lifecycle wire (ApprovalSubmissionLifecycleV1.ReleaseBomSha256 /
        // ReleaseBomGeneration) must carry exactly what the executor-gateway
        // wire (ActiveReleaseBomBindingV1) carries.
        var assembly = typeof(ActiveReleaseBindingV1).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith(
                "active.release.binding.v1.corpus.json", StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resourceName));
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        var binding = ActiveReleaseBindingV1Codec.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(baseline));

        var source = new PolicyBoundReleaseBomFactsSource(new PinnedBindingReader(binding));
        Assert.True(source.TryReadActiveFacts(
            binding.DeviceBindingId, out var sha, out var generation));

        // Pinned values shared with the executor-gateway adapter tests.
        Assert.Equal(new string('b', 64), sha);
        // release_bom_generation on the policy lifecycle wire is the
        // ANTI-ROLLBACK runtime activation ordinal — the same value the
        // gateway's ActiveReleaseBomBindingV1.Generation carries (its
        // contract mandates "monotonic generation anti-rollback"), which
        // the signer ordinal cannot satisfy because rollback legitimately
        // reverts it. The corpus valid case separates the two: runtime
        // generation 1 versus signer release_bom_generation 7.
        Assert.Equal(1, generation);
        Assert.Equal(7, binding.ReleaseBomGeneration);
        Assert.NotEqual(binding.ReleaseBomGeneration, generation);
    }

    private static void AssertCorpusCodec(
        string schemaSuffix,
        string corpusSuffix,
        Action<byte[]> deserialize)
    {
        var assembly = typeof(ActiveReleaseBindingV1).Assembly;
        Assert.Contains(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(schemaSuffix, StringComparison.Ordinal));
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(corpusSuffix, StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resourceName));
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        var cases = Assert.IsType<JsonArray>(root["cases"]);
        Assert.NotEmpty(cases);
        foreach (var caseNode in cases)
        {
            var contractCase = Assert.IsType<JsonObject>(caseNode);
            var instance = Assert.IsType<JsonObject>(baseline.DeepClone());
            var patch = Assert.IsType<JsonObject>(contractCase["patch"]);
            foreach (var pair in patch)
            {
                instance[pair.Key] = pair.Value?.DeepClone();
            }
            var remove = Assert.IsType<JsonArray>(contractCase["remove"]);
            foreach (var field in remove)
            {
                Assert.True(instance.Remove(
                    Assert.IsAssignableFrom<JsonValue>(field).GetValue<string>()));
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(instance);
            var codecValid = Assert.IsAssignableFrom<JsonValue>(contractCase["codecValid"])
                .GetValue<bool>();
            if (codecValid)
            {
                deserialize(payload);
            }
            else
            {
                Assert.ThrowsAny<Exception>(() => deserialize(payload));
            }
        }
    }

    private static (byte[] Bom, string Token) SignBom(
        RSA rsa,
        string keyId,
        string bomId,
        long signerGeneration,
        byte[]? previousStableBom)
    {
        var token = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("token:" + bomId)));
        var wireBomId = bomId.Length >= 8 ? bomId : "test-" + bomId;
        string? previousStableBomId = null;
        if (previousStableBom is not null)
        {
            using var previousDocument = JsonDocument.Parse(previousStableBom);
            previousStableBomId = previousDocument.RootElement
                .GetProperty("bom_id")
                .GetString();
        }
        var payload = new JsonObject
        {
            ["schema_version"] = "dps.release-bom/v1",
            ["bom_id"] = wireBomId,
            ["status"] = "SIGNED",
            ["integration_commit"] = new string('a', 40),
            ["created_at"] = "2026-07-14T00:00:00.0000001Z",
            ["release_bom_generation"] = signerGeneration,
            ["activation_token_sha256"] = Convert.ToHexStringLower(
                SHA256.HashData(Convert.FromBase64String(token))),
            ["modules"] = new JsonArray(),
            ["instruction_hashes"] = new JsonObject(),
            ["contracts"] = new JsonArray(),
            ["database_versions"] = new JsonObject(),
            ["dependency_dag_sha256"] = new string('b', 64),
            ["compatibility_matrix_sha256"] = new string('c', 64),
            ["feature_flags"] = new JsonObject(),
            ["kill_switches"] = new JsonArray(),
            ["ai_toolchain"] = new JsonObject(),
            ["evidence"] = new JsonArray(),
            ["risk"] = new JsonObject(),
            ["release_approval"] = new JsonObject(),
            ["rollout"] = new JsonObject(),
            ["rollback"] = new JsonObject(),
            ["previous_stable_bom"] = previousStableBomId,
            ["previous_stable_bom_sha256"] = previousStableBom is null
                ? null
                : Convert.ToHexStringLower(SHA256.HashData(previousStableBom)),
            ["native_stop_authorities"] = new JsonArray(),
            ["device_route_assignment_authorities"] = new JsonArray(),
            ["native_stop_challenge_authorities"] = new JsonArray()
        };
        return (SignPayload(rsa, keyId, payload), token);
    }

    private static byte[] SignStableTwin(RSA rsa, string keyId, byte[] signedBom)
    {
        var payload = JsonNode.Parse(signedBom)!.AsObject();
        payload["status"] = "STABLE";
        payload.Remove("signature");
        return SignPayload(rsa, keyId, payload);
    }

    private static byte[] SignPayload(RSA rsa, string keyId, JsonObject payload)
    {
        payload.Remove("signature");
        using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
        var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
        var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
            .Concat(canonical)
            .ToArray();
        var signature = rsa.SignData(
            message,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        payload["signature"] = new JsonObject
        {
            ["algorithm"] = "rsa-pss-sha256",
            ["key_id"] = keyId,
            ["value"] = Convert.ToBase64String(signature)
        };
        using var fullDocument = JsonDocument.Parse(payload.ToJsonString());
        return ReleaseBomCanonicalJson.Serialize(fullDocument.RootElement);
    }
}
