using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Xunit;

namespace Dps.PolicyApproval.Tests;

/// <summary>
/// Consumer-side proof for milestone M1B "policy and gateway read the same
/// generation/token": the policy truth reader consumes the provider-owned
/// shared corpus (embedded in Dps.ControlPlaneHost.Contracts from
/// Modules/control-plane-host/contracts/provided/active.release.binding.v1.corpus.json)
/// through the strict codec + composition-fixed reader path and reproduces
/// the pinned (release_bom_sha256, generation, execution_token) triple
/// byte-for-byte. Executor-gateway asserts the identical pinned triple
/// against the identical corpus case in ControlPlaneActiveReleaseBomReaderTests.
/// </summary>
public sealed class ActiveReleaseBomTruthReaderTests
{
    private const string PinnedDevice = "db_11111111111111111111111111111111";
    private const long PinnedGeneration = 1;
    private const string PinnedExecutionTokenBase64 =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly string PinnedReleaseBomSha256 = new('b', 64);

    [Fact, Trait("Category", "Unit")]
    public void SharedCorpusValidCaseReadsThePinnedTripleByteForByte()
    {
        var corpus = LoadSharedCorpus(out var validPayloads, out _);
        Assert.Equal(20, corpus);
        var payload = Assert.Single(validPayloads).Payload;

        var binding = ActiveReleaseBindingV1Codec.Deserialize(payload);
        var reader = new ActiveReleaseBomTruthReader(
            new FixedActiveReleaseBindingReader(binding));

        Assert.True(reader.TryReadActive(
            PinnedDevice, out var releaseBomSha256, out var generation, out var token));
        Assert.Equal(PinnedReleaseBomSha256, releaseBomSha256);
        Assert.Equal(PinnedGeneration, generation);
        Assert.Equal(PinnedExecutionTokenBase64, token);
        Assert.Equal(
            Convert.FromBase64String(binding.ExecutionTokenBase64),
            Convert.FromBase64String(token));
    }

    [Fact, Trait("Category", "Unit")]
    public void SharedCorpusInvalidCasesAreAllRejectedOnTheConsumerPath()
    {
        _ = LoadSharedCorpus(out _, out var invalidPayloads);
        Assert.Equal(19, invalidPayloads.Count);
        foreach (var (id, payload) in invalidPayloads)
        {
            var exception = Record.Exception(() =>
            {
                var binding = ActiveReleaseBindingV1Codec.Deserialize(payload);
                var reader = new ActiveReleaseBomTruthReader(
                    new FixedActiveReleaseBindingReader(binding));
                _ = reader.TryReadActive(PinnedDevice, out _, out _, out _);
            });
            Assert.True(exception is not null, $"corpus case '{id}' must be rejected");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void ReaderCarriesTheRuntimeActivationOrdinalNotTheSignerOrdinal()
    {
        var binding = RuntimeBinding(generation: 3, releaseBomGeneration: 7);
        var reader = new ActiveReleaseBomTruthReader(
            new FixedActiveReleaseBindingReader(binding));

        Assert.True(reader.TryReadActive(
            binding.DeviceBindingId, out var releaseBomSha256, out var generation, out var token));
        Assert.Equal(3, generation);
        Assert.Equal(binding.ReleaseBomSha256, releaseBomSha256);
        Assert.Equal(
            Convert.FromBase64String(binding.ExecutionTokenBase64),
            Convert.FromBase64String(token));
    }

    [Fact, Trait("Category", "Unit")]
    public void AbsentBindingFailsClosedToFalse()
    {
        var reader = new ActiveReleaseBomTruthReader(
            new FixedActiveReleaseBindingReader(null));
        Assert.False(reader.TryReadActive(PinnedDevice, out _, out _, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void NonActiveOrForeignBindingThrowsInsteadOfReading()
    {
        var previous = RuntimeBinding(generation: 3, releaseBomGeneration: 7)
            with { Status = "previous" };
        var previousReader = new ActiveReleaseBomTruthReader(
            new FixedActiveReleaseBindingReader(previous));
        Assert.Throws<InvalidOperationException>(() =>
            previousReader.TryReadActive(previous.DeviceBindingId, out _, out _, out _));

        var active = RuntimeBinding(generation: 3, releaseBomGeneration: 7);
        var foreignReader = new ActiveReleaseBomTruthReader(
            new FixedActiveReleaseBindingReader(active));
        Assert.Throws<InvalidOperationException>(() =>
            foreignReader.TryReadActive(
                "db_ffffffffffffffffffffffffffffffff", out _, out _, out _));
    }

    private static ActiveReleaseBindingV1 RuntimeBinding(long generation, long releaseBomGeneration)
    {
        // The token is generated at runtime and never persisted.
        var token = RandomNumberGenerator.GetBytes(ActiveReleaseBindingV1.ExecutionTokenSizeBytes);
        try
        {
            return new ActiveReleaseBindingV1(
                "1.0.0",
                "active.release.binding/v1",
                "control-plane-host",
                "db_22222222222222222222222222222222",
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                generation,
                releaseBomGeneration,
                Convert.ToBase64String(token),
                Convert.ToHexStringLower(SHA256.HashData(token)),
                "active",
                "deployed-release-controller",
                "deployed-controller-key-v1",
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
                "receipt_99999999999999999999999999999999");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private static int LoadSharedCorpus(
        out IReadOnlyList<(string Id, byte[] Payload)> validPayloads,
        out IReadOnlyList<(string Id, byte[] Payload)> invalidPayloads)
    {
        var assembly = typeof(ActiveReleaseBindingV1).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith(
                "active.release.binding.v1.corpus.json", StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resourceName));
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        var cases = Assert.IsType<JsonArray>(root["cases"]);
        var valid = new List<(string, byte[])>();
        var invalid = new List<(string, byte[])>();
        foreach (var caseNode in cases)
        {
            var contractCase = Assert.IsType<JsonObject>(caseNode);
            var id = Assert.IsAssignableFrom<JsonValue>(contractCase["id"]).GetValue<string>();
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
            (codecValid ? valid : invalid).Add((id, payload));
        }
        validPayloads = valid;
        invalidPayloads = invalid;
        return cases.Count;
    }

    private sealed class FixedActiveReleaseBindingReader : IActiveReleaseBindingReader
    {
        private readonly ActiveReleaseBindingV1? _binding;

        public FixedActiveReleaseBindingReader(ActiveReleaseBindingV1? binding)
        {
            _binding = binding;
        }

        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding)
        {
            binding = _binding;
            return binding is not null;
        }
    }
}
