using System.Text.Json.Nodes;
using Dps.ExecutorGateway;
using Xunit;

namespace Dps.ExecutorGateway.Tests;

/// <summary>
/// Contract-layer same-source proof for milestone M1B "policy and gateway read
/// the same generation/token", without any cross-module reference: this test
/// reads the provider-owned shared corpus file
/// Modules/control-plane-host/contracts/provided/active.release.binding.v1.corpus.json
/// directly from the repository root, extracts the valid base case's
/// (release_bom_sha256, generation, execution_token) triple, and proves that
/// the gateway's own ActiveReleaseBomBindingV1 accepts exactly that triple
/// byte-for-byte against the pinned constants that control-plane-host's
/// PolicyBoundReleaseBomFactsSource contract test pins against the identical
/// corpus case. The production wiring (a control-plane-host-backed
/// IVerifiedActiveReleaseBomReader implementation) belongs to the future M4
/// composition root (RebuildPlan section 10); a module-level ProjectReference
/// from executor-gateway to control-plane-host is forbidden because it closes
/// a dependency cycle back into control-plane-host. What this test pins is the
/// contract-layer same-source invariant only.
/// </summary>
public sealed class ActiveReleaseBomBindingSharedCorpusTests
{
    private const string SharedCorpusRepositoryRelativePath =
        "Modules/control-plane-host/contracts/provided/active.release.binding.v1.corpus.json";

    private const string PinnedDevice = "db_11111111111111111111111111111111";
    private const long PinnedGeneration = 1;
    private const string PinnedExecutionTokenBase64 =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly string PinnedReleaseBomSha256 = new('b', 64);

    [Fact, Trait("Category", "Unit")]
    public void SharedCorpusValidCaseTripleValidatesAsTheGatewayBindingByteForByte()
    {
        var corpusCase = LoadSingleValidCorpusCase();

        var releaseBomSha256 = RequireString(corpusCase, "release_bom_sha256");
        var generation = RequireInt64(corpusCase, "generation");
        var executionToken = RequireString(corpusCase, "execution_token");
        var deviceBindingId = RequireString(corpusCase, "device_binding_id");

        var binding = new ActiveReleaseBomBindingV1(
            ActiveReleaseBomBindingV1.CurrentSchemaVersion,
            deviceBindingId,
            releaseBomSha256,
            generation,
            executionToken);
        binding.Validate();

        Assert.Equal(PinnedReleaseBomSha256, binding.ReleaseBomSha256);
        Assert.Equal(PinnedGeneration, binding.Generation);
        Assert.Equal(PinnedExecutionTokenBase64, binding.ExecutionTokenBase64);
        Assert.Equal(PinnedDevice, binding.DeviceBindingId);
        Assert.Equal(
            Convert.FromBase64String(PinnedExecutionTokenBase64),
            Convert.FromBase64String(binding.ExecutionTokenBase64));

        // Gateway Generation carries the runtime activation ordinal (the
        // provider's anti-rollback fence), never the signer's
        // release_bom_generation. control-plane-host's
        // PolicyBoundReleaseBomFactsSource makes the identical choice against
        // the identical corpus case, so both consumption paths are same-source
        // and same-value at the contract layer.
        var signerOrdinal = RequireInt64(corpusCase, "release_bom_generation");
        Assert.Equal(7, signerOrdinal);
        Assert.NotEqual(signerOrdinal, binding.Generation);
    }

    private static JsonObject LoadSingleValidCorpusCase()
    {
        var corpusPath = Path.Combine(FindRepositoryRoot(), SharedCorpusRepositoryRelativePath);
        using var stream = File.OpenRead(corpusPath);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        var cases = Assert.IsType<JsonArray>(root["cases"]);

        JsonObject? validInstance = null;
        foreach (var caseNode in cases)
        {
            var contractCase = Assert.IsType<JsonObject>(caseNode);
            if (!Assert.IsAssignableFrom<JsonValue>(contractCase["codecValid"]).GetValue<bool>())
            {
                continue;
            }

            Assert.Null(validInstance);
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

            validInstance = instance;
        }

        Assert.NotNull(validInstance);
        return validInstance;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SharedCorpusRepositoryRelativePath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"repository root containing '{SharedCorpusRepositoryRelativePath}' was not found above '{AppContext.BaseDirectory}'");
    }

    private static string RequireString(JsonObject instance, string field)
        => Assert.IsAssignableFrom<JsonValue>(instance[field]).GetValue<string>();

    private static long RequireInt64(JsonObject instance, string field)
        => Assert.IsAssignableFrom<JsonValue>(instance[field]).GetValue<long>();
}
