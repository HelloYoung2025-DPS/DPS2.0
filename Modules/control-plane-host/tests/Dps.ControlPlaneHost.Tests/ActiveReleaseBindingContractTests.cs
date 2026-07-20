using System.Security.Cryptography;
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
            [key], new InMemoryReleaseBindingTruthStore(), () => Now);

        var (firstBom, firstToken) = SignBom(rsa, key.KeyId, "bom-1", 1, null);
        authority.Activate(Device, firstBom, firstToken);
        var (secondBom, secondToken) = SignBom(
            rsa, key.KeyId, "bom-2", 2, Convert.ToHexStringLower(SHA256.HashData(firstBom)));
        authority.Activate(Device, secondBom, secondToken);
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
        string? previousSha)
    {
        var token = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("token:" + bomId)));
        var payload = new JsonObject
        {
            ["schema_version"] = "dps.release-bom/v1",
            ["bom_id"] = bomId,
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
            ["previous_stable_bom"] = previousSha is null ? null : (JsonNode)("bom-previous-" + bomId),
            ["previous_stable_bom_sha256"] = previousSha,
            ["native_stop_authorities"] = new JsonArray(),
            ["device_route_assignment_authorities"] = new JsonArray(),
            ["native_stop_challenge_authorities"] = new JsonArray()
        };
        using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
        var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
        var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n").Concat(canonical).ToArray();
        var signature = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        payload["signature"] = new JsonObject
        {
            ["algorithm"] = "rsa-pss-sha256",
            ["key_id"] = keyId,
            ["value"] = Convert.ToBase64String(signature)
        };
        return (Encoding.UTF8.GetBytes(payload.ToJsonString()), token);
    }
}
