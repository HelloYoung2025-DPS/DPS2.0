using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Dps.ExecutorGateway;
using Xunit;

namespace Dps.ExecutorGateway.Tests;

/// <summary>
/// Consumer-side proof for milestone M1B "policy and gateway read the same
/// generation/token": the gateway adapter consumes the provider-owned shared
/// corpus (embedded in Dps.ControlPlaneHost.Contracts from
/// Modules/control-plane-host/contracts/provided/active.release.binding.v1.corpus.json)
/// through the strict codec + composition-fixed reader path and reproduces
/// the pinned (release_bom_sha256, generation, execution_token) triple
/// byte-for-byte. Policy-approval asserts the identical pinned triple against
/// the identical corpus case in ActiveReleaseBomTruthReaderTests.
/// </summary>
public sealed class ControlPlaneActiveReleaseBomReaderTests
{
    private const string PinnedDevice = "db_11111111111111111111111111111111";
    private const long PinnedGeneration = 1;
    private const string PinnedExecutionTokenBase64 =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly string PinnedReleaseBomSha256 = new('b', 64);

    [Fact, Trait("Category", "Unit")]
    public async Task SharedCorpusValidCaseMapsToThePinnedTripleByteForByte()
    {
        var corpus = LoadSharedCorpus(out var validPayloads, out _);
        Assert.Equal(20, corpus);
        var payload = Assert.Single(validPayloads).Payload;

        var binding = ActiveReleaseBindingV1Codec.Deserialize(payload);
        var reader = new ControlPlaneActiveReleaseBomReader(
            new FixedActiveReleaseBindingReader(binding));
        var mapped = await reader.ReadVerifiedActiveAsync(PinnedDevice, CancellationToken.None);

        Assert.NotNull(mapped);
        Assert.Equal(PinnedReleaseBomSha256, mapped.ReleaseBomSha256);
        Assert.Equal(PinnedGeneration, mapped.Generation);
        Assert.Equal(PinnedExecutionTokenBase64, mapped.ExecutionTokenBase64);
        Assert.Equal(
            Convert.FromBase64String(binding.ExecutionTokenBase64),
            Convert.FromBase64String(mapped.ExecutionTokenBase64));
        Assert.Equal(ActiveReleaseBomBindingV1.CurrentSchemaVersion, mapped.SchemaVersion);
        Assert.Equal(PinnedDevice, mapped.DeviceBindingId);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task SharedCorpusInvalidCasesAreAllRejectedOnTheAdapterPath()
    {
        _ = LoadSharedCorpus(out _, out var invalidPayloads);
        Assert.Equal(19, invalidPayloads.Count);
        foreach (var (id, payload) in invalidPayloads)
        {
            var exception = await Record.ExceptionAsync(async () =>
            {
                var binding = ActiveReleaseBindingV1Codec.Deserialize(payload);
                var reader = new ControlPlaneActiveReleaseBomReader(
                    new FixedActiveReleaseBindingReader(binding));
                _ = await reader.ReadVerifiedActiveAsync(PinnedDevice, CancellationToken.None);
            });
            Assert.True(exception is not null, $"corpus case '{id}' must be rejected");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task MappingCarriesTheRuntimeActivationOrdinalNotTheSignerOrdinal()
    {
        var binding = RuntimeBinding(generation: 3, releaseBomGeneration: 7);
        var reader = new ControlPlaneActiveReleaseBomReader(
            new FixedActiveReleaseBindingReader(binding));
        var mapped = await reader.ReadVerifiedActiveAsync(
            binding.DeviceBindingId, CancellationToken.None);

        Assert.NotNull(mapped);
        Assert.Equal(3, mapped.Generation);
        Assert.Equal(binding.ReleaseBomSha256, mapped.ReleaseBomSha256);
        Assert.Equal(
            Convert.FromBase64String(binding.ExecutionTokenBase64),
            Convert.FromBase64String(mapped.ExecutionTokenBase64));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task AbsentBindingFailsClosedToNull()
    {
        var reader = new ControlPlaneActiveReleaseBomReader(
            new FixedActiveReleaseBindingReader(null));
        Assert.Null(await reader.ReadVerifiedActiveAsync(PinnedDevice, CancellationToken.None));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task NonActiveOrForeignBindingThrowsInsteadOfMapping()
    {
        var previous = RuntimeBinding(generation: 3, releaseBomGeneration: 7)
            with { Status = "previous" };
        var previousReader = new ControlPlaneActiveReleaseBomReader(
            new FixedActiveReleaseBindingReader(previous));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await previousReader.ReadVerifiedActiveAsync(
                previous.DeviceBindingId, CancellationToken.None));

        var active = RuntimeBinding(generation: 3, releaseBomGeneration: 7);
        var foreignReader = new ControlPlaneActiveReleaseBomReader(
            new FixedActiveReleaseBindingReader(active));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await foreignReader.ReadVerifiedActiveAsync(
                "db_ffffffffffffffffffffffffffffffff", CancellationToken.None));
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
