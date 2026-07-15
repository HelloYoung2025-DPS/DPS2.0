using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.Planner.Contracts;
using Xunit;

namespace Dps.Planner.Tests;

public sealed class ActionProposalContractTests
{
    private const string SelectorA = "selector_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SelectorB = "selector_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ValueC = "value_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string EvidenceD = "evidence_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string EvidenceE = "evidence_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Fact]
    [Trait("Category", "Contract")]
    public void SharedAdversarialCorpusMatchesStrictCsharpCodec()
    {
        var corpus = JsonNode.Parse(File.ReadAllText(CorpusPath()))!.AsObject();
        var baseline = corpus["base"]!.AsObject();
        foreach (var caseNode in corpus["cases"]!.AsArray())
        {
            var testCase = caseNode!.AsObject();
            var instance = baseline.DeepClone().AsObject();
            if (testCase["overrides"] is JsonObject overrides)
            {
                foreach (var pair in overrides)
                {
                    instance[pair.Key] = pair.Value?.DeepClone();
                }
            }
            if (testCase["remove"] is JsonArray removals)
            {
                foreach (var propertyName in removals)
                {
                    instance.Remove(propertyName!.GetValue<string>());
                }
            }

            var expected = testCase["valid"]!.GetValue<bool>();
            var accepted = TryDeserialize(Encoding.UTF8.GetBytes(instance.ToJsonString()));
            Assert.True(
                expected == accepted,
                $"Contract case '{testCase["name"]!.GetValue<string>()}' expected valid={expected}, actual={accepted}.");
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void StrictCodecRejectsDuplicateRootAndParameterMembers()
    {
        var observe = Encoding.UTF8.GetString(ActionProposalV2Json.Serialize(Proposal("observe")));
        var duplicateRoot = observe[..^1] + ",\"trace_id\":\"trace_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}";
        Assert.Throws<JsonException>(() => ActionProposalV2Json.Deserialize(Encoding.UTF8.GetBytes(duplicateRoot)));

        var locate = Encoding.UTF8.GetString(ActionProposalV2Json.Serialize(Proposal("locate")));
        var duplicateParameter = locate.Replace(
            $"\"parameters\":{{\"selector_ref\":\"{SelectorA}\"}}",
            $"\"parameters\":{{\"selector_ref\":\"{SelectorA}\",\"selector_ref\":\"{SelectorB}\"}}",
            StringComparison.Ordinal);
        Assert.NotEqual(locate, duplicateParameter);
        Assert.Throws<JsonException>(() => ActionProposalV2Json.Deserialize(Encoding.UTF8.GetBytes(duplicateParameter)));

        var oversized = Encoding.UTF8.GetBytes(observe + new string(' ', ActionProposalV2Json.MaximumWireBytes));
        Assert.Throws<JsonException>(() => ActionProposalV2Json.Deserialize(oversized));
        Assert.Throws<JsonException>(() => ActionProposalV2Json.Deserialize([]));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SerializationRoundTripIsCanonicalAndImmutable()
    {
        var original = Proposal("fixture.type") with
        {
            Parameters = new Dictionary<string, string>
            {
                ["value_ref"] = ValueC,
                ["selector_ref"] = SelectorA
            },
            EvidenceRefs = [EvidenceE, EvidenceD]
        };
        var firstWire = ActionProposalV2Json.Serialize(original);
        var snapshot = ActionProposalV2Json.Deserialize(firstWire);
        var secondWire = ActionProposalV2Json.Serialize(snapshot);

        Assert.Equal(firstWire, secondWire);
        Assert.Equal(new[] { "selector_ref", "value_ref" }, snapshot.Parameters.Keys);
        Assert.Equal(new[] { EvidenceD, EvidenceE }, snapshot.EvidenceRefs);
        Assert.Throws<NotSupportedException>((Action)(() =>
            ((IDictionary<string, string>)snapshot.Parameters).Add("x", "y")));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void MissingExtraCaseVariantAndNullMembersFailClosed()
    {
        var root = JsonNode.Parse(ActionProposalV2Json.Serialize(Proposal("observe")))!.AsObject();

        var missing = root.DeepClone().AsObject();
        missing.Remove("trace_id");
        Assert.False(TryDeserialize(Encoding.UTF8.GetBytes(missing.ToJsonString())));

        var extra = root.DeepClone().AsObject();
        extra["unexpected"] = "value";
        Assert.False(TryDeserialize(Encoding.UTF8.GetBytes(extra.ToJsonString())));

        var caseVariant = root.DeepClone().AsObject();
        caseVariant["TraceId"] = caseVariant["trace_id"]!.DeepClone();
        Assert.False(TryDeserialize(Encoding.UTF8.GetBytes(caseVariant.ToJsonString())));

        var nullParameters = root.DeepClone().AsObject();
        nullParameters["parameters"] = null;
        Assert.False(TryDeserialize(Encoding.UTF8.GetBytes(nullParameters.ToJsonString())));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void UnpairedSurrogateAndInvalidUtf8FailBeforeProposalCreation()
    {
        var wire = Encoding.UTF8.GetString(ActionProposalV2Json.Serialize(Proposal("observe")));
        var surrogate = wire.Replace(
            "trace_33333333333333333333333333333333",
            "trace_\\ud800",
            StringComparison.Ordinal);
        Assert.False(TryDeserialize(Encoding.UTF8.GetBytes(surrogate)));

        var invalidUtf8 = Encoding.UTF8.GetBytes(wire);
        var traceIndex = Encoding.UTF8.GetString(invalidUtf8).IndexOf("trace_3333", StringComparison.Ordinal);
        Assert.True(traceIndex >= 0);
        invalidUtf8[traceIndex + "trace_".Length] = 0xff;
        Assert.False(TryDeserialize(invalidUtf8));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SchemaArtifactDeclaresExactDraftAndActionConditions()
    {
        using var stream = typeof(ActionProposalV2).Assembly.GetManifestResourceStream(
            "Dps.Planner.Contracts.action.proposal.v2.schema.json")
            ?? throw new InvalidOperationException("Embedded action.proposal/v2 schema is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal(ActionProposalV2Json.MaximumWireBytes, root.GetProperty("x-dps-max-wire-bytes").GetInt32());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(CorpusPath()))).ToLowerInvariant(),
            root.GetProperty("x-dps-adversarial-corpus-sha256").GetString());
        var proposalIdentity = root.GetProperty("x-dps-proposal-id");
        Assert.Equal(ActionProposalIdentity.Domain, proposalIdentity.GetProperty("domain").GetString());
        Assert.Equal("SHA-256", proposalIdentity.GetProperty("digest").GetString());
        Assert.Equal("uint32-big-endian-length-prefixed-strict-utf8", proposalIdentity.GetProperty("encoding").GetString());
        Assert.Equal(8, proposalIdentity.GetProperty("uuidVersion").GetInt32());
        Assert.Equal(
            new[] { "domain", "soul_id", "device_binding_id", "platform_account_id", "idempotency_key" },
            proposalIdentity.GetProperty("fields").EnumerateArray().Select(field => field.GetString()));
        Assert.Equal(ActionProposalV2Canonical.Domain, root.GetProperty("x-dps-canonical-sha256").GetProperty("domain").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(ActionProposalV2.MaximumEvidenceReferences, root.GetProperty("properties").GetProperty("evidence_refs").GetProperty("maxItems").GetInt32());
        Assert.Equal("^selector_[a-f0-9]{64}$(?![\\s\\S])", root.GetProperty("$defs").GetProperty("selector_ref").GetProperty("pattern").GetString());
        Assert.Equal("^value_[a-f0-9]{64}$(?![\\s\\S])", root.GetProperty("$defs").GetProperty("value_ref").GetProperty("pattern").GetString());
        Assert.Equal("^evidence_[a-f0-9]{64}$(?![\\s\\S])", root.GetProperty("$defs").GetProperty("evidence_ref").GetProperty("pattern").GetString());

        var expected = new Dictionary<string, (bool SideEffect, string[] Parameters)>(StringComparer.Ordinal)
        {
            ["observe"] = (false, []),
            ["locate"] = (false, ["selector_ref"]),
            ["verify"] = (false, ["selector_ref"]),
            ["wait"] = (false, ["duration_ms"]),
            ["fixture.tap"] = (true, ["selector_ref"]),
            ["fixture.type"] = (true, ["selector_ref", "value_ref"])
        };
        var branches = root.GetProperty("allOf")[0].GetProperty("oneOf");
        Assert.Equal(expected.Count, branches.GetArrayLength());
        foreach (var branch in branches.EnumerateArray())
        {
            var properties = branch.GetProperty("properties");
            var action = properties.GetProperty("action_kind").GetProperty("const").GetString()!;
            var parameters = properties.GetProperty("parameters");
            var required = parameters.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal).ToArray()
                : [];
            Assert.Equal(expected[action].SideEffect, properties.GetProperty("is_side_effect").GetProperty("const").GetBoolean());
            Assert.Equal(expected[action].Parameters.Order(StringComparer.Ordinal), required);
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void DeprecatedV1RemainsSeparateShadowReadCompatibilityButCurrentPlannerProducesOnlyV2()
    {
        using var modes = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "action-proposal-major-modes.v1.json")));
        Assert.Equal("dps.contract-major-modes/v1", modes.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("reject", modes.RootElement.GetProperty("unknownMajorMode").GetString());
        var majorModes = modes.RootElement.GetProperty("majors").EnumerateArray().ToArray();
        Assert.Collection(
            majorModes,
            major =>
            {
                Assert.Equal(1, major.GetProperty("major").GetInt32());
                Assert.Equal("quarantine-only", major.GetProperty("mode").GetString());
                Assert.False(major.GetProperty("plannerMayProduce").GetBoolean());
                Assert.False(major.GetProperty("proposalCarriesExecutionAuthority").GetBoolean());
                Assert.False(major.GetProperty("downstreamPromotionEligible").GetBoolean());
            },
            major =>
            {
                Assert.Equal(2, major.GetProperty("major").GetInt32());
                Assert.Equal("active", major.GetProperty("mode").GetString());
                Assert.True(major.GetProperty("plannerMayProduce").GetBoolean());
                Assert.False(major.GetProperty("proposalCarriesExecutionAuthority").GetBoolean());
                Assert.True(major.GetProperty("downstreamPromotionEligible").GetBoolean());
            });

        var legacy = LegacyV1Proposal();
        var legacyWire = ActionProposalV1Json.Serialize(legacy);
        var readBack = ActionProposalV1Json.Deserialize(legacyWire);

        Assert.Equal(ActionProposalV1.CurrentContractId, readBack.ContractId);
        Assert.True(readBack.ShadowOnly);
        Assert.ThrowsAny<Exception>(() => ActionProposalV2Json.Deserialize(legacyWire));

        var current = new ShadowActionPlanner().Propose(new PlanningRequest(
            legacy.SoulId,
            legacy.DeviceBindingId,
            legacy.PlatformAccountId,
            legacy.TraceId,
            legacy.IdempotencyKey,
            legacy.OccurredAt,
            "model-proposer",
            "observe",
            new Dictionary<string, string>(),
            [EvidenceD]));
        Assert.IsType<ActionProposalV2>(current);
        Assert.Equal(ActionProposalV2.CurrentContractId, current.ContractId);
        Assert.DoesNotContain("approval", Encoding.UTF8.GetString(ActionProposalV2Json.Serialize(current)), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDeserialize(ReadOnlySpan<byte> wire)
    {
        try
        {
            _ = ActionProposalV2Json.Deserialize(wire);
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static ActionProposalV2 Proposal(string action)
    {
        var parameters = action switch
        {
            "locate" or "verify" or "fixture.tap" => new Dictionary<string, string> { ["selector_ref"] = SelectorA },
            "wait" => new Dictionary<string, string> { ["duration_ms"] = "1000" },
            "fixture.type" => new Dictionary<string, string>
            {
                ["selector_ref"] = SelectorA,
                ["value_ref"] = ValueC
            },
            _ => new Dictionary<string, string>()
        };
        return new ActionProposalV2(
            ActionProposalV2.CurrentSchemaVersion,
            ActionProposalV2.CurrentContractId,
            ActionProposalV2.CurrentProducerModule,
            ActionProposalIdentity.Create(
                "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "db_11111111111111111111111111111111",
                "pa_22222222222222222222222222222222",
                "idem_4444444444444444444444444444444444444444444444444444444444444444"),
            "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            "trace_33333333333333333333333333333333",
            "idem_4444444444444444444444444444444444444444444444444444444444444444",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            "internal",
            action,
            action is "fixture.tap" or "fixture.type",
            true,
            parameters,
            [EvidenceD]);
    }

    private static ActionProposalV1 LegacyV1Proposal() => new(
        ActionProposalV1.CurrentSchemaVersion,
        ActionProposalV1.CurrentContractId,
        ActionProposalV1.CurrentProducerModule,
        ActionProposalIdentity.Create(
            "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            "idem_4444444444444444444444444444444444444444444444444444444444444444"),
        "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "db_11111111111111111111111111111111",
        "pa_22222222222222222222222222222222",
        "trace_33333333333333333333333333333333",
        "idem_4444444444444444444444444444444444444444444444444444444444444444",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        "internal",
        "observe",
        false,
        true,
        new Dictionary<string, string>(),
        ["evidence:legacy-shadow"]);

    private static string CorpusPath() => Path.Combine(AppContext.BaseDirectory, "action-proposal-contract-cases.v2.json");
}
