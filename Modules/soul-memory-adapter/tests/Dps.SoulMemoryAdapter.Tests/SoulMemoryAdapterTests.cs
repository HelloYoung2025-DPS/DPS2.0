using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.GBrainProjector.Contracts;
using Dps.SoulMemoryAdapter.Contracts;
using Xunit;

namespace Dps.SoulMemoryAdapter.Tests;

public sealed class SoulMemoryAdapterTests
{
    private const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const long FixtureNonce = 0;
    private static readonly DateTimeOffset ProjectionTime =
        new(2026, 7, 14, 1, 2, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset BindingTime = ProjectionTime.AddMinutes(-5);

    [Fact]
    [Trait("Category", "Unit")]
    public void PreparationPreservesTheExactProjectionBindingAndIsIdempotent()
    {
        var projection = CreateProjection(SoulA, "projection-a", 'c');
        var adapter = new DeterministicSoulMemoryAdapter();

        var first = adapter.Prepare(projection);
        var duplicate = adapter.Prepare(projection);

        Assert.Equal(first, duplicate);
        Assert.Equal(projection.SourceId, first.SourceId);
        Assert.Equal(projection.SchemaVersion, first.ProjectionSchemaVersion);
        Assert.Equal(projection.ContractId, first.ProjectionContractId);
        Assert.Equal(projection.ProjectionRevision, first.ProjectionRevision);
        Assert.Equal(projection.ProjectionChecksum, first.ProjectionChecksum);
        Assert.Equal(SoulMemoryReadbackV1.PreparedStatus, first.Status);
        Assert.Equal(SoulMemoryReadbackV1.NoReadbackChecksum, first.ReadbackChecksum);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SourceBindingsRemainIsolatedBetweenSouls()
    {
        var adapter = new DeterministicSoulMemoryAdapter();

        var first = adapter.Prepare(CreateProjection(SoulA, "projection-a", 'c'));
        var second = adapter.Prepare(CreateProjection(SoulB, "projection-b", 'd'));

        Assert.NotEqual(first.SoulId, second.SoulId);
        Assert.NotEqual(first.SourceId, second.SourceId);
        Assert.NotEqual(first.ProjectionRevision, second.ProjectionRevision);
        Assert.NotEqual(first.ProjectionChecksum, second.ProjectionChecksum);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConflictingProjectionReuseOfAnIdempotencyKeyFailsClosed()
    {
        var adapter = new DeterministicSoulMemoryAdapter();
        var first = CreateProjection(SoulA, "shared-idempotency-key", 'c');
        var conflict = CreateProjection(SoulA, "shared-idempotency-key", 'd');

        _ = adapter.Prepare(first);

        Assert.Throws<InvalidOperationException>(() => adapter.Prepare(conflict));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnlyAnExactReadbackCanBecomeVerified()
    {
        var adapter = new DeterministicSoulMemoryAdapter();
        var prepared = adapter.Prepare(CreateProjection(SoulA, "projection-a", 'c'));
        var exact = ExactReadback(prepared);

        var verified = adapter.Verify(new VerifyReadbackCommand(
            prepared,
            exact,
            "trace_55555555555555555555555555555555",
            "idem_6666666666666666666666666666666666666666666666666666666666666666",
            ProjectionTime.AddMinutes(1)));

        Assert.Equal(SoulMemoryReadbackV1.VerifiedStatus, verified.Status);
        Assert.Equal(verified.ProjectionChecksum, verified.ReadbackChecksum);
        Assert.Equal(prepared.SourceId, verified.SourceId);
        Assert.Equal(prepared.ProjectionRevision, verified.ProjectionRevision);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EveryReadbackScopeSchemaRevisionAndChecksumMismatchFailsClosed()
    {
        var adapter = new DeterministicSoulMemoryAdapter();
        var prepared = adapter.Prepare(CreateProjection(SoulA, "projection-a", 'c'));
        var exact = ExactReadback(prepared);

        AssertMismatch(adapter, prepared, exact with
        {
            SoulId = SoulB,
            SourceId = GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce)
        }, "wrong-soul");
        AssertMismatch(adapter, prepared, exact with { DeviceBindingId = "db_other" }, "wrong-binding");
        AssertMismatch(adapter, prepared, exact with { PlatformAccountId = "pa_other" }, "wrong-account");
        AssertMismatch(
            adapter,
            prepared,
            exact with { SourceId = GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce) },
            "wrong-source");
        AssertMismatch(adapter, prepared, exact with { ProjectionSchemaVersion = "2.1.0" }, "wrong-schema");
        AssertMismatch(adapter, prepared, exact with { ProjectionContractId = "gbrain.projection/v1" }, "wrong-contract");
        AssertMismatch(adapter, prepared, exact with { ProjectionRevision = new string('e', 64) }, "wrong-revision");
        AssertMismatch(adapter, prepared, exact with { ReadbackChecksum = new string('f', 64) }, "wrong-checksum");
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SharedPrefixSoulsDeriveDistinctSourcesAndReadbackRequiresTheExactFullSoul()
    {
        var left = "soul_" + new string('a', 28) + new string('b', 36);
        var right = "soul_" + new string('a', 28) + new string('c', 36);
        // The v2 derivation is a domain-separated SHA-256 over the full soul and nonce,
        // so the v1 truncated-prefix source collision no longer exists.
        Assert.NotEqual(
            GBrainSourceIdCandidates.Compute(left, FixtureNonce),
            GBrainSourceIdCandidates.Compute(right, FixtureNonce));

        var adapter = new DeterministicSoulMemoryAdapter();
        var prepared = adapter.Prepare(CreateProjection(left, "collision-left", 'c'));
        var collidingReadback = ExactReadback(prepared) with { SoulId = right };

        AssertMismatch(adapter, prepared, collidingReadback, "collision-readback");
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ReadbackContractUsesTheProjectionWireSemanticsAndFailsClosed()
    {
        var value = new DeterministicSoulMemoryAdapter()
            .Prepare(CreateProjection(SoulA, "projection-a", 'c'));

        value.Validate();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var root = document.RootElement;

        Assert.Equal(GBrainProjectionV2.CurrentContractId,
            root.GetProperty("projection_contract_id").GetString());
        Assert.Equal(value.ProjectionRevision,
            root.GetProperty("projection_revision").GetString());
        Assert.Equal(value.ProjectionChecksum,
            root.GetProperty("projection_checksum").GetString());
        Assert.Equal(SoulMemoryReadbackV1.NoReadbackChecksum,
            root.GetProperty("readback_checksum").GetString());
        Assert.False(root.TryGetProperty("projection_sha256", out _));
        Assert.False(root.TryGetProperty("schema_id", out _));
        Assert.Throws<NotSupportedException>(() =>
            (value with { SchemaVersion = "2.0.0" }).Validate());
        // The v2 source rule is a canonical-format gate (ArgumentException), because the
        // (soul, nonce) derivation cannot be recomputed from this receipt alone.
        Assert.Throws<ArgumentException>(() =>
            (value with { SourceId = "source_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ProviderOwnedReadbackCorpusMatchesTheStrictDtoCodec()
    {
        using var stream = typeof(SoulMemoryReadbackV1).Assembly.GetManifestResourceStream(
            "Dps.SoulMemoryAdapter.Contracts.soul.memory.readback.v1.corpus.json");
        Assert.NotNull(stream);
        using var corpus = JsonDocument.Parse(stream);
        var baseline = JsonNode.Parse(corpus.RootElement.GetProperty("base").GetRawText())!.AsObject();

        foreach (var testCase in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var candidate = JsonNode.Parse(baseline.ToJsonString())!.AsObject();
            foreach (var property in testCase.GetProperty("patch").EnumerateObject())
                candidate[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            foreach (var property in testCase.GetProperty("remove").EnumerateArray())
                Assert.True(candidate.Remove(property.GetString()!));

            var json = candidate.ToJsonString();
            if (testCase.TryGetProperty("rawReplace", out var replacement))
                json = json.Replace(
                    replacement.GetProperty("old").GetString()!,
                    replacement.GetProperty("new").GetString()!,
                    StringComparison.Ordinal);
            var exception = Record.Exception(() => SoulMemoryReadbackV1Json.Deserialize(json));
            Assert.Equal(testCase.GetProperty("codecValid").GetBoolean(), exception is null);
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void TamperedProjectionAndCallerSuppliedOperationsAreRejected()
    {
        var projection = CreateProjection(SoulA, "projection-a", 'c');

        Assert.Throws<NotSupportedException>(() =>
            new DeterministicSoulMemoryAdapter().Prepare(projection with
            {
                SchemaVersion = "1.0.0"
            }));
        Assert.Throws<InvalidOperationException>(() =>
            new DeterministicSoulMemoryAdapter().Prepare(projection with
            {
                ProjectionChecksum = new string('0', 64)
            }));

        Assert.DoesNotContain(
            typeof(GBrainCompanySoulMemoryClient).GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Uri) ||
                string.Equals(parameter.Name, "operation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parameter.Name, "tool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parameter.Name, "url", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ScopeRejectsACanonicalButForeignSourcePairing()
    {
        // A canonical dps-* value that is not the supplied Soul's own nonce-derived
        // candidate must never validate as that Soul's scope: this false
        // (soul, source) pairing is the entry point for a forged binding page via
        // CreateSourceBindingMarkdown/VerifySourceBinding, and the nonce witness
        // closes it locally.
        var scope = new GBrainSoulScope(
            GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce),
            SoulA,
            "db_" + new string('a', 32),
            "pa_" + new string('a', 32),
            FixtureNonce);

        var forged = Assert.Throws<ArgumentException>(() => scope.Validate());
        Assert.Equal("SourceId", forged.ParamName);

        // A different in-window nonce cannot rescue the foreign pairing either.
        Assert.Throws<ArgumentException>(() =>
            (scope with { SourceBindingNonce = 7 }).Validate());

        // The Soul's own witnessed candidate is the only accepted pairing.
        (scope with { SourceId = GBrainSourceIdCandidates.Compute(SoulA, FixtureNonce) })
            .Validate();
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void DeleteIntentRejectsAnInconsistentSoulSourceNonceTriple()
    {
        var projection = CreateProjection(SoulA, "delete-triple", 'c');
        var intent = new GBrainProjectionDeleteIntent(
            GBrainProjectionDeleteIntent.CurrentSchemaVersion,
            GBrainProjectionDeleteIntent.CurrentContractId,
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            "trace_88888888888888888888888888888888",
            CanonicalIdempotency("delete-triple-intent"),
            ProjectionTime.AddMinutes(1),
            projection.ProjectionRevision,
            projection.ProjectionChecksum,
            projection.SourceBindingNonce);
        intent.Validate();

        // Same (soul, source) pair but a different claimed witness: the triple is not
        // self-consistent and must fail before any lease, journal, or page use.
        Assert.Throws<ArgumentException>(() =>
            (intent with { SourceBindingNonce = FixtureNonce + 1 }).Validate());
        // A nonce outside the fixed window fails the witness recomputation itself.
        Assert.ThrowsAny<ArgumentException>(() =>
            (intent with { SourceBindingNonce = GBrainSourceBindingV1.MaximumNonce + 1 }).Validate());
        // A foreign source with the original witness is equally refused.
        Assert.Throws<ArgumentException>(() =>
            (intent with { SourceId = GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce) }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void PinnedGBrainCapabilityFixtureMatchesTheRuntimeFailClosedProfile()
    {
        var resourceName = typeof(SoulMemoryAdapterTests).Assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(
                "gbrain-0.42.42.0.mcp-capabilities.fixture.json",
                StringComparison.Ordinal));
        using var stream = typeof(SoulMemoryAdapterTests).Assembly
            .GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var server = document.RootElement.GetProperty("server");
        Assert.Equal(GBrainCompanyProtocolV1.TestedServerVersion,
            server.GetProperty("version").GetString());
        var protocol = server.GetProperty("mcp_protocol");
        Assert.Equal(GBrainCompanyProtocolV1.PreferredMcpProtocolVersion,
            protocol.GetProperty("client_preferred").GetString());
        Assert.Equal(
            GBrainCompanyProtocolV1.AllowedMcpProtocolVersions.Order(StringComparer.Ordinal),
            protocol.GetProperty("explicit_allowlist").EnumerateArray()
                .Select(value => value.GetString()!)
                .Order(StringComparer.Ordinal));
        Assert.Equal(GBrainCompanyProtocolV1.TestedMcpProtocolVersion,
            protocol.GetProperty("local_gbrain_negotiated").GetString());

        var expected = GBrainCapabilityProfile.Required.ToDictionary(
            profile => profile.Name,
            StringComparer.Ordinal);
        var actual = document.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(expected.Count, actual.Length);
        foreach (var tool in actual)
        {
            var name = tool.GetProperty("name").GetString()!;
            Assert.True(expected.TryGetValue(name, out var profile));
            var properties = tool.GetProperty("properties").EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal(profile.Properties, properties);
            Assert.Equal(
                profile.Required.Order(StringComparer.Ordinal),
                tool.GetProperty("required").EnumerateArray()
                    .Select(value => value.GetString()!)
                    .Order(StringComparer.Ordinal));
        }
    }

    private static void AssertMismatch(
        DeterministicSoulMemoryAdapter adapter,
        SoulMemoryReadbackV1 prepared,
        GBrainReadbackObservation readback,
        string idempotencyKey)
    {
        Assert.Throws<GBrainReadbackMismatchException>(() => adapter.Verify(new VerifyReadbackCommand(
            prepared,
            readback,
            prepared.TraceId,
            CanonicalIdempotency(idempotencyKey),
            ProjectionTime.AddMinutes(1))));
    }

    private static GBrainReadbackObservation ExactReadback(SoulMemoryReadbackV1 prepared)
    {
        return new GBrainReadbackObservation(
            prepared.SourceId,
            prepared.SoulId,
            prepared.DeviceBindingId,
            prepared.PlatformAccountId,
            prepared.ProjectionSchemaVersion,
            prepared.ProjectionContractId,
            prepared.ProjectionRevision,
            prepared.ProjectionChecksum);
    }

    private static GBrainProjectionV2 CreateProjection(
        string soulId,
        string idempotencyKey,
        char revisionHex)
    {
        var binding = GBrainSourceBindingV1.Create(
            soulId,
            GBrainSourceIdCandidates.Compute(soulId, FixtureNonce),
            FixtureNonce,
            BindingTime);
        var withoutChecksum = new GBrainProjectionV2(
            GBrainProjectionV2.CurrentSchemaVersion,
            GBrainProjectionV2.CurrentContractId,
            GBrainProjectionV2.CurrentProducerModule,
            soulId,
            "db_" + new string(soulId[5], 32),
            "pa_" + new string(soulId[5], 32),
            "trace_77777777777777777777777777777777",
            CanonicalIdempotency(idempotencyKey),
            ProjectionTime,
            "personal",
            binding.SourceId,
            binding.Algorithm,
            binding.Nonce,
            binding.SoulHash,
            binding.AllocatedAt,
            binding.BindingRevision,
            binding.BindingChecksum,
            new string(revisionHex, 64),
            new string('0', 64),
            GBrainProjectionV2.RenderedNotWrittenStatus,
            0,
            Array.Empty<ProjectionEventV1>(),
            Array.Empty<ProjectionInterestV1>());

        var projection = withoutChecksum with
        {
            ProjectionChecksum = GBrainProjectionV2Canonicalizer.ComputeSha256(withoutChecksum)
        };
        projection.Validate();
        return projection;
    }

    private static string CanonicalIdempotency(string discriminator) =>
        "idem_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(discriminator)));
}
