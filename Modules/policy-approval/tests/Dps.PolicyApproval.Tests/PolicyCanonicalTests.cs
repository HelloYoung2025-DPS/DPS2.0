using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed class PolicyCanonicalTests
{
    private const string Soul = "soul_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Device = "db_cccccccccccccccccccccccccccccccc";
    private const string Account = "pa_cccccccccccccccccccccccccccccccc";
    private const string Trace = "trace_cccccccccccccccccccccccccccccccc";
    private const string Idempotency = "idem_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly IReadOnlySet<string> PromotionCorpusCaseIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "valid", "additional-field", "missing-field", "uppercase-uuid",
            "zero-uuid", "offset-not-zulu", "invalid-calendar-date", "year-zero",
            "fractional-trailing-zero", "trailing-newline-id",
            "trailing-newline-proposal-sha",
            "trailing-carriage-return-release-bom-sha", "digest-space",
            "digest-uppercase", "digest-short", "digest-long", "unknown-contract",
            "fractional-revision", "revision-over-int64",
            "noncanonical-base64-pad-bits", "lifetime-over-five-minutes"
        };
    private static readonly IReadOnlySet<string> DecisionCorpusCaseIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "valid-denied", "valid-approved-side-effect", "unknown-field",
            "missing-platform-authorization-field", "schema-version-major-only",
            "schema-version-leading-zero", "approval-uuid-uppercase",
            "approval-uuid-zero", "proposal-uuid-braced", "device-id-newline",
            "occurred-offset", "occurred-trailing-zero-fraction",
            "occurred-too-precise", "occurred-invalid-calendar",
            "occurred-year-zero", "valid-canonical-fraction",
            "policy-version-leading-zero", "policy-id-newline",
            "observe-unexpected-parameter", "denied-without-reason",
            "approved-shadow", "approved-side-effect-without-platform-authorization"
        };
    private static readonly IReadOnlySet<string> FenceCorpusCaseIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "valid", "valid-canonical-fraction", "valid-int64-maximum",
            "unknown-field", "missing-privacy-class", "legacy-acquired-at-field",
            "schema-version-major-only", "schema-version-leading-zero",
            "fence-uuid-uppercase", "fence-uuid-zero", "proposal-uuid-braced",
            "trace-newline", "status-zero", "runtime-int64-overflow",
            "occurred-offset", "occurred-trailing-zero-fraction",
            "valid-until-invalid-calendar", "occurred-year-zero",
            "valid-until-equals-occurred", "valid-until-before-occurred",
            "lifetime-over-two-seconds", "approval-newline",
            "approval-carriage-return", "approval-space", "approval-uppercase",
            "approval-short", "approval-long", "runtime-newline",
            "runtime-carriage-return", "runtime-space", "runtime-uppercase",
            "runtime-short", "runtime-long", "release-newline",
            "release-carriage-return", "release-space", "release-uppercase",
            "release-short", "release-long"
        };

    [Fact, Trait("Category", "Contract")]
    public void ProposalCanonicalCommitmentSeparatesOldDelimiterCollision()
    {
        var first = Proposal(new Dictionary<string, string>
        {
            ["selector_ref"] = "x,value_ref=y",
            ["value_ref"] = "z"
        });
        var second = first with
        {
            Parameters = new Dictionary<string, string>
            {
                ["selector_ref"] = "x",
                ["value_ref"] = "y,value_ref=z"
            }
        };

        Assert.NotEqual(
            PolicyAuthorizationBinding.ComputeProposalSha256(first),
            PolicyAuthorizationBinding.ComputeProposalSha256(second));
    }

    [Fact, Trait("Category", "Contract")]
    public void StrictUtf8RejectsUnpairedSurrogateAndSnapshotCannotBeCallerConstructed()
    {
        var invalid = Proposal(new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "\ud800"
        });
        Assert.Throws<EncoderFallbackException>(() => PolicyAuthorizationBinding.ComputeProposalSha256(invalid));
        Assert.DoesNotContain(
            typeof(PolicyApprovalAuthoritativeSnapshot).GetConstructors(BindingFlags.Public | BindingFlags.Instance),
            constructor => constructor.GetParameters().Length > 0);
    }

    [Fact, Trait("Category", "Contract")]
    public void ProposalSnapshotReadsCallerCollectionsExactlyOnce()
    {
        var stableParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "fixture.value"
        };
        var flippingParameters = new FlippingReadOnlyDictionary(
            stableParameters,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["selector_ref"] = "fixture.input",
                ["unexpected"] = "second-enumeration-attack"
            });
        var flippingEvidence = new FlippingReadOnlyList(
            ["evidence:canonical"],
            ["not-an-evidence-reference"]);
        var proposal = Proposal(flippingParameters) with { EvidenceRefs = flippingEvidence };

        Assert.Equal(
            PolicyAuthorizationBinding.ComputeProposalSha256(Proposal(stableParameters)),
            PolicyAuthorizationBinding.ComputeProposalSha256(proposal));
        Assert.Equal(1, flippingParameters.EnumerationCount);
        Assert.Equal(1, flippingEvidence.EnumerationCount);
    }

    [Fact, Trait("Category", "Contract")]
    public void OpaqueIdentifiersRejectTrailingNewlineInCsharpAndSchemas()
    {
        var decision = Decision("observe", false, []);
        Assert.Throws<ArgumentException>(() => (decision with { DeviceBindingId = Device + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (decision with { PlatformAccountId = Account + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (decision with { TraceId = Trace + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (decision with { IdempotencyKey = Idempotency + "\n" }).Validate());

        foreach (var resource in new[]
        {
            "Dps.PolicyApproval.Contracts.approval.decision.v1.schema.json",
            "Dps.PolicyApproval.Contracts.approval.execution.fence.v1.schema.json",
            "Dps.PolicyApproval.Contracts.action.execution.promotion.v1.schema.json"
        })
        {
            using var stream = typeof(ApprovalDecisionV1).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded schema '{resource}' is missing.");
            using var document = JsonDocument.Parse(stream);
            var properties = document.RootElement.GetProperty("properties");
            AssertExactSchemaPattern(properties, "device_binding_id", Device);
            AssertExactSchemaPattern(properties, "platform_account_id", Account);
            AssertExactSchemaPattern(properties, "trace_id", Trace);
            AssertExactSchemaPattern(properties, "idempotency_key", Idempotency);
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void ExecutionPromotionContractIsIndependentScopedAndBounded()
    {
        var proposal = Proposal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "fixture.value"
        });
        var promotion = new ActionExecutionPromotionV1(
            ActionExecutionPromotionV1.CurrentSchemaVersion,
            ActionExecutionPromotionV1.CurrentContractId,
            ActionExecutionPromotionV1.CurrentProducerModule,
            ActionExecutionPromotionV1.CurrentAuthScope,
            Guid.Parse("48000000-0000-0000-0000-000000000004"),
            proposal.ProposalId,
            Guid.Parse("48000000-0000-0000-0000-000000000005"),
            proposal.SoulId,
            proposal.DeviceBindingId,
            proposal.PlatformAccountId,
            proposal.TraceId,
            proposal.IdempotencyKey,
            PolicyAuthorizationBinding.ComputeProposalSha256(proposal),
            new string('d', 64),
            7,
            proposal.OccurredAt,
            proposal.OccurredAt.AddMinutes(5),
            "internal",
            Convert.ToBase64String(new byte[64]));
        promotion.Validate();
        var payload = ActionExecutionPromotionV1Codec.Serialize(promotion);
        Assert.Equal(promotion, ActionExecutionPromotionV1Codec.Deserialize(payload));
        Assert.Contains("\"schema_version\"", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\"", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => (promotion with { AuthScope = "policy:evaluate" }).Validate());
        Assert.Throws<ArgumentException>(() => (promotion with { ValidUntil = proposal.OccurredAt.AddMinutes(5).AddTicks(1) }).Validate());
        Assert.Throws<ArgumentException>(() => (promotion with { SignatureBase64 = new string('A', 88) }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void ApprovalSchemaAndCsharpShareActionAndDecisionConditions()
    {
        using var stream = typeof(ApprovalDecisionV1).Assembly.GetManifestResourceStream(
            "Dps.PolicyApproval.Contracts.approval.decision.v1.schema.json")
            ?? throw new InvalidOperationException("Embedded approval schema is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.Equal(32, root.GetProperty("properties").GetProperty("evaluated_policy_ids").GetProperty("maxItems").GetInt32());
        Assert.Equal(32, root.GetProperty("properties").GetProperty("denial_reasons").GetProperty("maxItems").GetInt32());

        var expected = new Dictionary<string, (bool SideEffect, string[] Parameters)>(StringComparer.Ordinal)
        {
            ["observe"] = (false, []),
            ["locate"] = (false, ["selector_ref"]),
            ["verify"] = (false, ["selector_ref"]),
            ["wait"] = (false, ["duration_ms"]),
            ["fixture.tap"] = (true, ["selector_ref"]),
            ["fixture.type"] = (true, ["selector_ref", "value_ref"])
        };
        var actionBranches = root.GetProperty("allOf")[0].GetProperty("oneOf");
        Assert.Equal(expected.Count, actionBranches.GetArrayLength());
        foreach (var branch in actionBranches.EnumerateArray())
        {
            var properties = branch.GetProperty("properties");
            var action = properties.GetProperty("action_kind").GetProperty("const").GetString()!;
            var definition = expected[action];
            Assert.Equal(definition.SideEffect, properties.GetProperty("is_side_effect").GetProperty("const").GetBoolean());
            var parameters = properties.GetProperty("parameters");
            var required = parameters.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal).ToArray()
                : [];
            Assert.Equal(definition.Parameters.Order(StringComparer.Ordinal), required);

            var decision = Decision(action, definition.SideEffect, definition.Parameters);
            decision.Validate();
            Assert.Throws<InvalidOperationException>(() => (decision with { IsSideEffect = !definition.SideEffect }).Validate());
            if (definition.Parameters.Length == 0)
                Assert.Throws<NotSupportedException>(() => (decision with { Parameters = new Dictionary<string, string> { ["unexpected"] = "value" } }).Validate());
            else
                Assert.Throws<NotSupportedException>(() => (decision with { Parameters = new Dictionary<string, string>() }).Validate());
        }

        var approved = Decision("observe", false, []) with
        {
            Decision = ApprovalDecisionV1.Approved,
            ShadowOnly = false,
            DenialReasons = []
        };
        approved.Validate();
        Assert.Throws<InvalidOperationException>(() => (approved with { ShadowOnly = true }).Validate());
        Assert.Throws<InvalidOperationException>(() => (approved with { Decision = ApprovalDecisionV1.Denied }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Decision("fixture.tap", true, ["selector_ref"]) with
        {
            Decision = ApprovalDecisionV1.Approved,
            ShadowOnly = false,
            DenialReasons = [],
            PlatformAuthorizationId = null
        }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void ExecutionFenceContractIsVersionedScopedAndShortLived()
    {
        var request = new ApprovalExecutionFenceRequestV1(
            ApprovalExecutionFenceRequestV1.CurrentSchemaVersion,
            ApprovalExecutionFenceRequestV1.CurrentContractId,
            ApprovalExecutionFenceRequestV1.CurrentConsumerModule,
            Guid.Parse("48000000-0000-0000-0000-000000000001"),
            Guid.Parse("48000000-0000-0000-0000-000000000002"),
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            new string('a', 64),
            1,
            1,
            new string('b', 64),
            new string('c', 64));
        request.Validate();
        Assert.Throws<NotSupportedException>(() => (request with { ContractId = "approval.execution.fence.request/v2" }).Validate());
        Assert.Throws<NotSupportedException>(() => (request with { ConsumerModule = "model" }).Validate());

        var acquiredAt = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        var fence = new ApprovalExecutionFenceV1(
            ApprovalExecutionFenceV1.CurrentSchemaVersion,
            ApprovalExecutionFenceV1.CurrentContractId,
            ApprovalExecutionFenceV1.CurrentProducerModule,
            Guid.Parse("48000000-0000-0000-0000-000000000003"),
            request.ApprovalId,
            request.ProposalId,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.ApprovalSha256,
            request.ExpectedStatusRevision,
            request.ExpectedRuntimeRevision,
            request.ExpectedRuntimeStateSha256,
            request.ExpectedReleaseBomSha256,
            acquiredAt,
            acquiredAt.AddSeconds(2),
            "internal");
        fence.Validate();
        Assert.Throws<ArgumentException>(() => (fence with { ValidUntil = acquiredAt }).Validate());
        Assert.Throws<ArgumentException>(() => (fence with
        {
            ValidUntil = acquiredAt.AddSeconds(2).AddTicks(1)
        }).Validate());
    }

    [Fact, Trait("Category", "Contract")]
    public void OwnedContractCorporaMatchStrictCodecsAndLockedCaseIds()
    {
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.action.execution.promotion.v1.corpus.json",
            PromotionCorpusCaseIds,
            payload => ActionExecutionPromotionV1Codec.Deserialize(payload));
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.approval.decision.v1.corpus.json",
            DecisionCorpusCaseIds,
            payload => ApprovalDecisionV1Codec.Deserialize(payload));
        AssertCodecCorpus(
            "Dps.PolicyApproval.Contracts.approval.execution.fence.v1.corpus.json",
            FenceCorpusCaseIds,
            payload => ApprovalExecutionFenceV1Codec.Deserialize(payload));

        var decisionPayload = ApprovalDecisionV1Codec.Serialize(
            Decision("observe", false, []));
        var decisionJson = Encoding.UTF8.GetString(decisionPayload);
        var semanticJsonb = Encoding.UTF8.GetBytes("{ " + decisionJson[1..]);
        Assert.Throws<ArgumentException>(
            () => ApprovalDecisionV1Codec.Deserialize(semanticJsonb));
        Assert.Equal(
            Guid.Parse("47000000-0000-0000-0000-000000000011"),
            ApprovalDecisionV1Codec.DeserializeSemanticJsonb(semanticJsonb).ApprovalId);

        var duplicateDecision = Encoding.UTF8.GetBytes(decisionJson.Replace(
            "\"schema_version\":\"1.0.0\",",
            "\"schema_version\":\"1.0.0\",\"schema_version\":\"1.0.0\",",
            StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(
            () => ApprovalDecisionV1Codec.Deserialize(duplicateDecision));
        Assert.Throws<ArgumentException>(
            () => ApprovalDecisionV1Codec.Deserialize([0x7b, 0x80, 0x7d]));
        Assert.Throws<ArgumentException>(() => ApprovalDecisionV1Codec.Deserialize(
            new byte[ApprovalDecisionV1Codec.MaximumPayloadBytes + 1]));

        var fencePayload = ApprovalExecutionFenceV1Codec.Serialize(Fence());
        var fenceJson = Encoding.UTF8.GetString(fencePayload);
        var duplicateFence = Encoding.UTF8.GetBytes(fenceJson.Replace(
            "\"schema_version\":\"1.0.0\",",
            "\"schema_version\":\"1.0.0\",\"schema_version\":\"1.0.0\",",
            StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(
            () => ApprovalExecutionFenceV1Codec.Deserialize(duplicateFence));
        Assert.Throws<ArgumentException>(
            () => ApprovalExecutionFenceV1Codec.Deserialize([0x7b, 0x80, 0x7d]));
        Assert.Throws<ArgumentException>(() => ApprovalExecutionFenceV1Codec.Deserialize(
            new byte[ApprovalExecutionFenceV1Codec.MaximumPayloadBytes + 1]));

        var stableDecision = Decision(
            "fixture.type",
            true,
            ["selector_ref", "value_ref"]);
        var flippingParameters = new FlippingReadOnlyDictionary(
            stableDecision.Parameters,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unexpected"] = "second-read-attack"
            });
        var flippingPolicies = new FlippingReadOnlyList(
            stableDecision.EvaluatedPolicyIds,
            ["not-a-policy-id"]);
        var flippingReasons = new FlippingReadOnlyList(
            stableDecision.DenialReasons,
            []);
        Assert.Equal(
            ApprovalDecisionV1Codec.Serialize(stableDecision),
            ApprovalDecisionV1Codec.Serialize(stableDecision with
            {
                Parameters = flippingParameters,
                EvaluatedPolicyIds = flippingPolicies,
                DenialReasons = flippingReasons
            }));
        Assert.Equal(1, flippingParameters.EnumerationCount);
        Assert.Equal(1, flippingPolicies.EnumerationCount);
        Assert.Equal(1, flippingReasons.EnumerationCount);
    }

    private static ApprovalDecisionV1 Decision(
        string action,
        bool sideEffect,
        IReadOnlyList<string> parameterNames)
        => new(
            ApprovalDecisionV1.CurrentSchemaVersion,
            ApprovalDecisionV1.CurrentContractId,
            ApprovalDecisionV1.CurrentProducerModule,
            Guid.Parse("47000000-0000-0000-0000-000000000011"),
            Guid.Parse("47000000-0000-0000-0000-000000000012"),
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            DateTimeOffset.Parse("2026-07-14T00:00:00Z"),
            "internal",
            action,
            sideEffect,
            true,
            parameterNames.ToDictionary(name => name, name => name + "-value", StringComparer.Ordinal),
            ApprovalDecisionV1.Denied,
            ApprovalDecisionV1.DeterministicAuthority,
            "1.0.0",
            ["SOUL-ISO-001"],
            null,
            ["SHADOW_ONLY"]);

    private static ActionProposalV1 Proposal(IReadOnlyDictionary<string, string> parameters)
        => new(
            ActionProposalV1.CurrentSchemaVersion,
            ActionProposalV1.CurrentContractId,
            ActionProposalV1.CurrentProducerModule,
            ActionProposalIdentity.Create(Soul, Device, Account, Idempotency),
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            DateTimeOffset.Parse("2026-07-14T00:00:00Z"),
            "internal",
            "fixture.type",
            true,
            true,
            parameters,
            ["evidence:canonical"]);

    private static ApprovalExecutionFenceV1 Fence()
    {
        var occurredAt = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        return new ApprovalExecutionFenceV1(
            ApprovalExecutionFenceV1.CurrentSchemaVersion,
            ApprovalExecutionFenceV1.CurrentContractId,
            ApprovalExecutionFenceV1.CurrentProducerModule,
            Guid.Parse("48000000-0000-0000-0000-000000000003"),
            Guid.Parse("48000000-0000-0000-0000-000000000001"),
            Guid.Parse("48000000-0000-0000-0000-000000000002"),
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            new string('a', 64),
            1,
            1,
            new string('b', 64),
            new string('c', 64),
            occurredAt,
            occurredAt.AddSeconds(2),
            "internal");
    }

    private static void AssertCodecCorpus(
        string resourceName,
        IReadOnlySet<string> expectedCaseIds,
        Func<byte[], object> deserialize)
    {
        using var stream = typeof(ApprovalDecisionV1).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded contract corpus '{resourceName}' is missing.");
        var corpus = JsonNode.Parse(stream)?.AsObject()
            ?? throw new InvalidOperationException(
                $"Embedded contract corpus '{resourceName}' is invalid.");
        var basePayload = corpus["base"]?.AsObject()
            ?? throw new InvalidOperationException("Contract corpus base is missing.");
        var cases = corpus["cases"]?.AsArray()
            ?? throw new InvalidOperationException("Contract corpus cases are missing.");
        Assert.Equal(expectedCaseIds.Count, cases.Count);
        var observedCaseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caseNode in cases)
        {
            var contractCase = caseNode?.AsObject()
                ?? throw new InvalidOperationException("Contract corpus case is invalid.");
            var caseId = contractCase["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Contract corpus case ID is missing.");
            Assert.True(observedCaseIds.Add(caseId), $"Duplicate corpus case ID '{caseId}'.");
            var instance = basePayload.DeepClone().AsObject();
            foreach (var pair in contractCase["patch"]?.AsObject()
                         ?? throw new InvalidOperationException("Contract corpus patch is missing."))
            {
                instance[pair.Key] = pair.Value?.DeepClone();
            }
            foreach (var field in contractCase["remove"]?.AsArray()
                         ?? throw new InvalidOperationException("Contract corpus remove is missing."))
            {
                instance.Remove(field?.GetValue<string>()
                    ?? throw new InvalidOperationException("Contract corpus remove field is invalid."));
            }
            var payload = Encoding.UTF8.GetBytes(instance.ToJsonString());
            var expectedValid = contractCase["codecValid"]?.GetValue<bool>()
                ?? throw new InvalidOperationException("Contract corpus codecValid is missing.");
            if (expectedValid)
            {
                _ = deserialize(payload);
            }
            else
            {
                Assert.ThrowsAny<Exception>(() => { _ = deserialize(payload); });
            }
        }
        Assert.Equal(
            expectedCaseIds.Order(StringComparer.Ordinal),
            observedCaseIds.Order(StringComparer.Ordinal));
    }

    private static void AssertExactSchemaPattern(
        JsonElement properties,
        string propertyName,
        string validValue)
    {
        var pattern = properties.GetProperty(propertyName).GetProperty("pattern").GetString()
            ?? throw new InvalidOperationException($"Schema property '{propertyName}' has no pattern.");
        Assert.Contains("$(?![\\s\\S])", pattern, StringComparison.Ordinal);
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        Assert.Matches(regex, validValue);
        Assert.DoesNotMatch(regex, validValue + "\n");
    }

    private sealed class FlippingReadOnlyDictionary(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> later) : IReadOnlyDictionary<string, string>
    {
        public int EnumerationCount { get; private set; }
        public int Count => first.Count;
        public IEnumerable<string> Keys => first.Keys;
        public IEnumerable<string> Values => first.Values;
        public string this[string key] => first[key];
        public bool ContainsKey(string key) => first.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => first.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            EnumerationCount++;
            return (EnumerationCount == 1 ? first : later).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FlippingReadOnlyList(
        IReadOnlyList<string> first,
        IReadOnlyList<string> later) : IReadOnlyList<string>
    {
        public int EnumerationCount { get; private set; }
        public int Count => first.Count;
        public string this[int index] => first[index];
        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            return (EnumerationCount == 1 ? first : later).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
