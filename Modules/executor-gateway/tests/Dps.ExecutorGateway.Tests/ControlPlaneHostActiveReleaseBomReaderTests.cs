using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Dps.ExecutorGateway;
using Xunit;

namespace Dps.ExecutorGateway.Tests;

/// <summary>
/// Unit coverage for ControlPlaneHostActiveReleaseBomReader, the
/// composition-fixed unique adapter (RebuildPlan §4.3 / finding F4) that
/// backs the gateway's IVerifiedActiveReleaseBomReader port with the
/// control-plane-host authoritative active.release.binding/v1 reader.
/// Mapping, fail-closed null cases, and cancellation are covered against
/// labelled fakes; the pinned shared corpus triple case keeps the adapter
/// same-source with the provider corpus
/// (Modules/control-plane-host/contracts/provided/active.release.binding.v1.corpus.json).
/// </summary>
public sealed class ControlPlaneHostActiveReleaseBomReaderTests
{
    private const string SharedCorpusRepositoryRelativePath =
        "Modules/control-plane-host/contracts/provided/active.release.binding.v1.corpus.json";

    private const string Device = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDevice = "db_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string PinnedDevice = "db_11111111111111111111111111111111";
    private const long PinnedGeneration = 1;
    private const string PinnedExecutionTokenBase64 =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly string PinnedReleaseBomSha256 = new('b', 64);

    [Fact, Trait("Category", "Unit")]
    public async Task AdapterMapsTheActiveBindingFieldByField()
    {
        var token = Convert.ToBase64String(
            Enumerable.Repeat((byte)0x42, ActiveReleaseBindingV1.ExecutionTokenSizeBytes).ToArray());
        var tokenSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Convert.FromBase64String(token)));
        var binding = LoadSharedCorpusBinding() with
        {
            DeviceBindingId = Device,
            ReleaseBomSha256 = new string('c', 64),
            Generation = 9,
            ReleaseBomGeneration = 3,
            ExecutionTokenBase64 = token,
            ActivationTokenSha256 = tokenSha256,
        };
        var reader = new FakeActiveReleaseBindingReader(binding);
        var adapter = new ControlPlaneHostActiveReleaseBomReader(reader);

        var mapped = await adapter.ReadVerifiedActiveAsync(
            Device, TestContext.Current.CancellationToken);

        Assert.NotNull(mapped);
        Assert.Equal(1, reader.CallCount);
        Assert.Equal(ActiveReleaseBomBindingV1.CurrentSchemaVersion, mapped.SchemaVersion);
        Assert.Equal(Device, mapped.DeviceBindingId);
        Assert.Equal(new string('c', 64), mapped.ReleaseBomSha256);
        // The gateway wire carries the runtime activation ordinal, never the
        // signer's release_bom_generation (rollback may legitimately revert
        // the signer ordinal, so it cannot be the anti-rollback fence).
        Assert.Equal(9, mapped.Generation);
        Assert.NotEqual(binding.ReleaseBomGeneration, mapped.Generation);
        Assert.Equal(token, mapped.ExecutionTokenBase64);
        Assert.Equal(tokenSha256, mapped.ComputeExecutionTokenSha256());
        Assert.Equal("active", mapped.Status);
        mapped.Validate();
        // The token stays redacted from string rendering on the mapped DTO.
        Assert.DoesNotContain(token, mapped.ToString(), StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task AdapterReturnsNullWhenThePackReaderFindsNoActiveBinding()
    {
        var adapter = new ControlPlaneHostActiveReleaseBomReader(
            new FakeActiveReleaseBindingReader(null, found: false));

        var mapped = await adapter.ReadVerifiedActiveAsync(
            Device, TestContext.Current.CancellationToken);

        Assert.Null(mapped);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task AdapterReturnsNullForANullBinding()
    {
        var adapter = new ControlPlaneHostActiveReleaseBomReader(
            new FakeActiveReleaseBindingReader(null));

        var mapped = await adapter.ReadVerifiedActiveAsync(
            Device, TestContext.Current.CancellationToken);

        Assert.Null(mapped);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task AdapterReturnsNullForANonActiveBinding()
    {
        foreach (var status in new[] { "previous", "revoked" })
        {
            var binding = LoadSharedCorpusBinding() with { Status = status };
            var adapter = new ControlPlaneHostActiveReleaseBomReader(
                new FakeActiveReleaseBindingReader(binding));

            var mapped = await adapter.ReadVerifiedActiveAsync(
                binding.DeviceBindingId, TestContext.Current.CancellationToken);

            Assert.Null(mapped);
        }
    }

    [Fact, Trait("Category", "Unit")]
    public async Task AdapterReturnsNullForAForeignDeviceBinding()
    {
        var binding = LoadSharedCorpusBinding() with { DeviceBindingId = OtherDevice };
        var adapter = new ControlPlaneHostActiveReleaseBomReader(
            new FakeActiveReleaseBindingReader(binding));

        var mapped = await adapter.ReadVerifiedActiveAsync(
            Device, TestContext.Current.CancellationToken);

        Assert.Null(mapped);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task AdapterObservesTheCancellationTokenBeforeReading()
    {
        var reader = new FakeActiveReleaseBindingReader(LoadSharedCorpusBinding());
        var adapter = new ControlPlaneHostActiveReleaseBomReader(reader);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await adapter.ReadVerifiedActiveAsync(Device, cancelled.Token));

        Assert.Equal(0, reader.CallCount);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task AdapterServesThePinnedSharedCorpusTriple()
    {
        // The provider-owned shared corpus valid case, deserialized through
        // the pack codec exactly like control-plane-host's pinned-triple
        // test, must flow through the adapter byte-for-byte: the gateway
        // wire then carries the identical (release_bom_sha256, generation,
        // execution_token) triple both consumption paths pin.
        var binding = LoadSharedCorpusBinding();
        var adapter = new ControlPlaneHostActiveReleaseBomReader(
            new FakeActiveReleaseBindingReader(binding));

        var mapped = await adapter.ReadVerifiedActiveAsync(
            PinnedDevice, TestContext.Current.CancellationToken);

        Assert.NotNull(mapped);
        Assert.Equal(PinnedReleaseBomSha256, mapped.ReleaseBomSha256);
        Assert.Equal(PinnedGeneration, mapped.Generation);
        Assert.Equal(PinnedExecutionTokenBase64, mapped.ExecutionTokenBase64);
        Assert.Equal(PinnedDevice, mapped.DeviceBindingId);
        Assert.Equal("active", mapped.Status);
        Assert.Equal(
            Convert.FromBase64String(PinnedExecutionTokenBase64),
            Convert.FromBase64String(mapped.ExecutionTokenBase64));
        Assert.Equal(binding.ActivationTokenSha256, mapped.ComputeExecutionTokenSha256());
        // The corpus valid case separates the runtime ordinal from the
        // signer ordinal; the adapter must carry the runtime one.
        Assert.Equal(7, binding.ReleaseBomGeneration);
        Assert.NotEqual(binding.ReleaseBomGeneration, mapped.Generation);
        mapped.Validate();
    }

    private static ActiveReleaseBindingV1 LoadSharedCorpusBinding()
    {
        var corpusPath = Path.Combine(FindRepositoryRoot(), SharedCorpusRepositoryRelativePath);
        using var stream = File.OpenRead(corpusPath);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        var baseline = Assert.IsType<JsonObject>(root["base"]);
        return ActiveReleaseBindingV1Codec.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(baseline));
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

    private sealed class FakeActiveReleaseBindingReader(
        ActiveReleaseBindingV1? binding,
        bool found = true) : IActiveReleaseBindingTestReader
    {
        public int CallCount { get; private set; }

        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? read)
        {
            CallCount++;
            read = binding;
            return found;
        }
    }
}
