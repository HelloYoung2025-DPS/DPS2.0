using System.Collections;
using Dps.OperationCompiler.Contracts;
using Dps.PolicyApproval.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Dps.OperationCompiler.Tests;

public sealed class AllowlistedOperationCompilerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ApprovedFixtureActionCompilesWithoutCoordinateFallback()
    {
        var operation = CompileAuthoritative(Approval("fixture.tap", sideEffect: true, new Dictionary<string, string> { ["selector_ref"] = "fixture.button" }));
        Assert.Equal("fixture.tap", Assert.Single(operation.Steps).StepKind);
        Assert.DoesNotContain(operation.Steps[0].Arguments.Keys, key => key is "x" or "y");
        Assert.False(operation.Steps[0].RetrySafe);
    }

    [Theory]
    [InlineData("coordinate.tap")]
    [InlineData("shell")]
    [InlineData("unknown")]
    [Trait("Category", "Unit")]
    public void UnknownActionFailsClosed(string action)
        => Assert.Throws<NotSupportedException>(() => CompileAuthoritative(Approval(action, false, new Dictionary<string, string>())));

    [Fact]
    [Trait("Category", "Unit")]
    public void CoordinateParametersAreRejectedRatherThanDowngraded()
        => Assert.Throws<NotSupportedException>(() => CompileAuthoritative(Approval("fixture.tap", true, new Dictionary<string, string> { ["x"] = "10", ["y"] = "20" })));

    [Fact]
    [Trait("Category", "Unit")]
    public void DeniedAndShadowApprovalsCannotCompile()
    {
        Assert.Throws<UnauthorizedAccessException>(() => CompileAuthoritative(Approval("observe") with { Decision = ApprovalDecisionV1.Denied, DenialReasons = ["DENIED"] }));
        Assert.Throws<InvalidOperationException>(() => CompileAuthoritative(Approval("observe") with { ShadowOnly = true }));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void UnknownStepAndMajorFailClosed()
    {
        var operation = CompileAuthoritative(Approval("observe"));
        Assert.Throws<NotSupportedException>(() => (operation with { SchemaVersion = "2.0.0" }).Validate());
        var bad = operation with { Steps = [operation.Steps[0] with { StepKind = "coordinate.tap" }] };
        Assert.Throws<NotSupportedException>(() => bad.Validate());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StepIdIsBoundToApprovalAndIdentityScope()
    {
        var baseline = CompileAuthoritative(Approval("observe"));
        var otherSoul = CompileAuthoritative(Approval("observe") with { SoulId = "soul_ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" });
        var otherDevice = CompileAuthoritative(Approval("observe") with { DeviceBindingId = "db_55555555555555555555555555555555" });
        var otherAccount = CompileAuthoritative(Approval("observe") with { PlatformAccountId = "pa_66666666666666666666666666666666" });
        Assert.Equal(4, new[] { baseline.Steps[0].StepId, otherSoul.Steps[0].StepId, otherDevice.Steps[0].StepId, otherAccount.Steps[0].StepId }.Distinct().Count());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DelimiterRichParameterPairsCannotReuseDeterministicIds()
    {
        var first = CompileAuthoritative(Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "alpha",
            ["value_ref"] = "beta,value_ref=gamma"
        }));
        var second = CompileAuthoritative(Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "alpha,value_ref=beta",
            ["value_ref"] = "gamma"
        }));

        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.NotEqual(first.Steps[0].StepId, second.Steps[0].StepId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SameApprovalIsDeterministicAcrossParameterInsertionOrder()
    {
        var first = CompileAuthoritative(Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture:selector=primary",
            ["value_ref"] = "value:primary"
        }));
        var second = CompileAuthoritative(Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["value_ref"] = "value:primary",
            ["selector_ref"] = "fixture:selector=primary"
        }));

        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(first.Steps[0].StepId, second.Steps[0].StepId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApprovalSecurityFieldsAreBoundOrRejected()
    {
        var baselineApproval = Approval("observe");
        var baseline = CompileAuthoritative(baselineApproval);
        var validSecurityChanges = new[]
        {
            baselineApproval with { ProposalId = Guid.Parse("52000000-0000-0000-0000-000000000003") },
            baselineApproval with { TraceId = "trace_77777777777777777777777777777777" },
            baselineApproval with { IdempotencyKey = "idem_8888888888888888888888888888888888888888888888888888888888888888" },
            baselineApproval with { OccurredAt = baselineApproval.OccurredAt.AddSeconds(1) },
            baselineApproval with { PolicyVersion = "1.0.1" },
            baselineApproval with { EvaluatedPolicyIds = ["SOUL-ISO-001", "CMD-IDEMP-001", "RESULT-VERIFY-001", "PLATFORM-AUTH-001"] }
        };

        foreach (var changedApproval in validSecurityChanges)
        {
            var changed = CompileAuthoritative(changedApproval);
            Assert.NotEqual(baseline.OperationId, changed.OperationId);
            Assert.NotEqual(baseline.Steps[0].StepId, changed.Steps[0].StepId);
        }

        Assert.Throws<NotSupportedException>(() => CompileAuthoritative(baselineApproval with { SchemaVersion = "1.1.0" }));
        Assert.Throws<NotSupportedException>(() => CompileAuthoritative(baselineApproval with { Authority = "model" }));
        Assert.Throws<ArgumentException>(() => CompileAuthoritative(baselineApproval with { PlatformAuthorizationId = string.Empty }));
        Assert.Throws<InvalidOperationException>(() => CompileAuthoritative(baselineApproval with { IsSideEffect = true }));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PlatformAuthorizationIsBoundAndMissingOrEmptySideEffectAuthorizationFailsClosed()
    {
        var baselineApproval = Approval("fixture.tap", true, new Dictionary<string, string> { ["selector_ref"] = "fixture.button" });
        var baseline = CompileAuthoritative(baselineApproval);
        var changed = CompileAuthoritative(baselineApproval with { PlatformAuthorizationId = "platform-auth:other" });

        Assert.NotEqual(baseline.OperationId, changed.OperationId);
        Assert.NotEqual(baseline.Steps[0].StepId, changed.Steps[0].StepId);
        Assert.Throws<InvalidOperationException>(() => CompileAuthoritative(baselineApproval with { PlatformAuthorizationId = null }));
        Assert.Throws<ArgumentException>(() => CompileAuthoritative(baselineApproval with { PlatformAuthorizationId = string.Empty }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [Trait("Category", "Contract")]
    public void NullOrEmptyStepArgumentsFailClosed(string? invalidValue)
    {
        var approval = Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = invalidValue!
        });
        Assert.Throws<ArgumentException>(() => CompileAuthoritative(approval));

        var valid = CompileAuthoritative(Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "value"
        }));
        var forgedArguments = new Dictionary<string, string>(valid.Steps[0].Arguments, StringComparer.Ordinal)
        {
            ["value_ref"] = invalidValue!
        };
        var forged = valid with { Steps = [valid.Steps[0] with { Arguments = forgedArguments }] };
        Assert.Throws<ArgumentException>(() => forged.Validate());
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xD801)]
    [Trait("Category", "Unit")]
    public void IllFormedUtf16CannotCollapseIntoReplacementBytes(int invalidCodeUnit)
    {
        var invalidValue = new string((char)invalidCodeUnit, 1);
        var approval = Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = invalidValue
        });

        Assert.Throws<EncoderFallbackException>(() => CompileAuthoritative(approval));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DurationAndReferencesUsePlannerAlignedMachineSyntaxAndRejectPromptOrCoordinates()
    {
        Assert.Equal("1", CompileAuthoritative(Approval(
            "wait",
            parameters: new Dictionary<string, string> { ["duration_ms"] = "1" })).Steps[0].Arguments["duration_ms"]);
        Assert.Equal("600000", CompileAuthoritative(Approval(
            "wait",
            parameters: new Dictionary<string, string> { ["duration_ms"] = "600000" })).Steps[0].Arguments["duration_ms"]);
        Assert.Equal(128, CompileAuthoritative(Approval(
            "fixture.type",
            true,
            new Dictionary<string, string>
            {
                ["selector_ref"] = "s" + new string('a', 127),
                ["value_ref"] = "v" + new string('b', 127)
            })).Steps[0].Arguments["selector_ref"].Length);

        foreach (var invalidDuration in new[] { "0", "01", "600001", "1.0", "+1", "wait now" })
            Assert.Throws<ArgumentException>(() => CompileAuthoritative(Approval(
                "wait",
                parameters: new Dictionary<string, string> { ["duration_ms"] = invalidDuration })));

        foreach (var invalidReference in new[]
                 {
                     "tap the submit button",
                     "selector/submit",
                     "x=10,y=20",
                     "10,20",
                     "coordinate:10,20",
                     "s" + new string('a', 128)
                 })
            Assert.Throws<ArgumentException>(() => CompileAuthoritative(Approval(
                "fixture.tap",
                true,
                new Dictionary<string, string> { ["selector_ref"] = invalidReference })));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void PublicDtoUsesOnlyExplicitSnakeCaseAndStrictlyRoundTrips()
    {
        var operation = CompileAuthoritative(Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "value"
        }));
        var json = OperationCompiledV1Json.Serialize(operation);
        using var document = JsonDocument.Parse(json);
        var rootNames = document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var expectedRootNames = new[]
        {
            "action_kind", "approval_id", "approval_sha256", "contract_id", "device_binding_id", "idempotency_key", "is_side_effect",
            "occurred_at", "operation_id", "platform_account_id", "platform_authorization_id", "privacy_class", "producer_module",
            "proposal_id", "schema_version", "shadow_only", "soul_id", "steps", "trace_id"
        }.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedRootNames, rootNames);

        var stepNames = document.RootElement.GetProperty("steps")[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "arguments", "postcondition_kind", "retry_safe", "step_id", "step_kind" }, stepNames);
        Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);

        var roundTrip = OperationCompiledV1Json.Deserialize(json);
        Assert.Equal(operation.OperationId, roundTrip.OperationId);
        Assert.Equal(operation.Steps[0].StepId, roundTrip.Steps[0].StepId);

        var unknownProperty = JsonNode.Parse(json)!.AsObject();
        unknownProperty["unexpected_property"] = true;
        Assert.Throws<JsonException>(() => OperationCompiledV1Json.Deserialize(unknownProperty.ToJsonString()));

        var missingRequiredBoolean = JsonNode.Parse(json)!.AsObject();
        Assert.True(missingRequiredBoolean.Remove("shadow_only"));
        Assert.Throws<JsonException>(() => OperationCompiledV1Json.Deserialize(missingRequiredBoolean.ToJsonString()));

        var duplicateProperty = json.Insert(1, "\"schema_version\":\"1.0.0\",");
        Assert.Throws<JsonException>(() => OperationCompiledV1Json.Deserialize(duplicateProperty));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SchemaAndDtoAgreeOnPropertiesAndExactlyOneV1Step()
    {
        using var schema = ReadEmbeddedJson("Dps.OperationCompiler.Contracts.operation.compiled.v1.schema.json");
        var rootProperties = schema.RootElement.GetProperty("properties");
        var schemaPropertyNames = rootProperties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var operation = CompileAuthoritative(Approval("observe"));
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(operation));
        var dtoPropertyNames = serialized.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(schemaPropertyNames, dtoPropertyNames);
        var requiredRootNames = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(dtoPropertyNames.Where(name => name != "platform_authorization_id"), requiredRootNames);

        var stepSchema = rootProperties.GetProperty("steps");
        Assert.Equal(1, stepSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(1, stepSchema.GetProperty("maxItems").GetInt32());
        var schemaStepNames = stepSchema.GetProperty("items").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var dtoStepNames = serialized.RootElement.GetProperty("steps")[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(schemaStepNames, dtoStepNames);
        var requiredStepNames = stepSchema.GetProperty("items").GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(dtoStepNames, requiredStepNames);

        var unknownStepProperty = JsonNode.Parse(JsonSerializer.Serialize(operation))!.AsObject();
        unknownStepProperty["steps"]!.AsArray()[0]!.AsObject()["unexpected_step_property"] = true;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CompiledOperationV1>(unknownStepProperty.ToJsonString()));

        var missingStepProperty = JsonNode.Parse(JsonSerializer.Serialize(operation))!.AsObject();
        Assert.True(missingStepProperty["steps"]!.AsArray()[0]!.AsObject().Remove("retry_safe"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CompiledOperationV1>(missingStepProperty.ToJsonString()));

        Assert.Throws<InvalidOperationException>(() => (operation with { Steps = [] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (operation with { Steps = [operation.Steps[0], operation.Steps[0]] }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void CanonicalApprovalOperationAndStepGoldenVectorsAndTamperFailClosed()
    {
        var approval = Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "value"
        });
        var operation = CompileAuthoritative(approval);
        Assert.Equal("f6e98fdde92a0c68efae6f8f17a3ce3dfcc7c6c2b0a3fba5d9ec5b4c50b6106f", operation.ApprovalSha256);
        Assert.Equal(Guid.Parse("557bfb87-3601-9155-8bd1-717f0779d7b8"), operation.OperationId);
        Assert.Equal(Guid.Parse("40a1d3f8-cf08-ffe4-d3c3-aa9655234c79"), operation.Steps[0].StepId);
        Assert.Equal(
            operation.Steps[0].StepId,
            OperationCompiledV1CanonicalIds.ComputeStepId(operation.OperationId, operation.Steps[0].StepKind, operation.Steps[0].Arguments, operation.Steps[0].RetrySafe, operation.Steps[0].PostconditionKind));

        using var canonicalSpec = ReadEmbeddedJson("Dps.OperationCompiler.Contracts.operation.compiled.v1.canonical.json");
        Assert.Equal("strict-utf8", canonicalSpec.RootElement.GetProperty("fieldEncoding").GetProperty("string").GetString());
        Assert.Equal("uint32-big-endian-byte-length-then-bytes", canonicalSpec.RootElement.GetProperty("fieldEncoding").GetProperty("framing").GetString());
        Assert.Equal("SHA-256", canonicalSpec.RootElement.GetProperty("digest").GetString());
        Assert.Equal("first-16-digest-bytes-constructed-as-dotnet-guid-byte-array", canonicalSpec.RootElement.GetProperty("idExtraction").GetString());
        var approvalDigestSpec = canonicalSpec.RootElement.GetProperty("approvalSnapshotDigest");
        Assert.Equal(ApprovalSnapshotV1Canonical.Domain, approvalDigestSpec.GetProperty("domain").GetString());
        Assert.Equal(approvalDigestSpec.GetProperty("goldenSha256").GetString(), ApprovalSnapshotV1Canonical.ComputeSha256(approval));

        var operationSpec = canonicalSpec.RootElement.GetProperty("idKinds").GetProperty("operation_id");
        Assert.Equal(OperationCompiledV1CanonicalIds.OperationIdDomain, operationSpec.GetProperty("domain").GetString());
        var goldenOperation = operationSpec.GetProperty("goldenVector");
        var goldenOperationId = OperationCompiledV1CanonicalIds.ComputeOperationId(
            goldenOperation.GetProperty("schema_version").GetString()!,
            goldenOperation.GetProperty("contract_id").GetString()!,
            goldenOperation.GetProperty("producer_module").GetString()!,
            goldenOperation.GetProperty("approval_id").GetGuid(),
            goldenOperation.GetProperty("proposal_id").GetGuid(),
            goldenOperation.GetProperty("approval_sha256").GetString()!,
            goldenOperation.GetProperty("soul_id").GetString()!,
            goldenOperation.GetProperty("device_binding_id").GetString()!,
            goldenOperation.GetProperty("platform_account_id").GetString()!,
            goldenOperation.GetProperty("trace_id").GetString()!,
            goldenOperation.GetProperty("idempotency_key").GetString()!,
            goldenOperation.GetProperty("occurred_at").GetDateTimeOffset(),
            goldenOperation.GetProperty("privacy_class").GetString()!,
            goldenOperation.GetProperty("action_kind").GetString()!,
            goldenOperation.GetProperty("is_side_effect").GetBoolean(),
            goldenOperation.GetProperty("shadow_only").GetBoolean(),
            goldenOperation.GetProperty("platform_authorization_id").GetString());
        Assert.Equal(goldenOperation.GetProperty("operation_id").GetGuid(), goldenOperationId);
        Assert.Equal(operation.OperationId, goldenOperationId);

        var stepSpec = canonicalSpec.RootElement.GetProperty("idKinds").GetProperty("step_id");
        Assert.Equal(OperationCompiledV1CanonicalIds.StepIdDomain, stepSpec.GetProperty("domain").GetString());
        var golden = stepSpec.GetProperty("goldenVector");
        var goldenArguments = golden.GetProperty("arguments").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        var goldenStepId = OperationCompiledV1CanonicalIds.ComputeStepId(
            golden.GetProperty("operation_id").GetGuid(),
            golden.GetProperty("step_kind").GetString()!,
            goldenArguments,
            golden.GetProperty("retry_safe").GetBoolean(),
            golden.GetProperty("postcondition_kind").GetString()!);
        Assert.Equal(golden.GetProperty("step_id").GetGuid(), goldenStepId);
        Assert.Equal(operation.Steps[0].StepId, goldenStepId);

        using var schema = ReadEmbeddedJson("Dps.OperationCompiler.Contracts.operation.compiled.v1.schema.json");
        var canonicalBinding = schema.RootElement.GetProperty("x-dps-canonical-spec");
        Assert.Equal("operation.compiled.v1.canonical.json", canonicalBinding.GetProperty("resource").GetString());
        var canonicalBytes = ReadEmbeddedBytes("Dps.OperationCompiler.Contracts.operation.compiled.v1.canonical.json");
        try
        {
            Assert.Equal(canonicalBinding.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant());
        }
        finally { CryptographicOperations.ZeroMemory(canonicalBytes); }

        var tamperedStep = operation with { Steps = [operation.Steps[0] with { StepId = Guid.Parse("40a1d3f8-cf08-ffe4-d3c3-aa9655234c78") }] };
        Assert.Throws<InvalidOperationException>(() => tamperedStep.Validate());
        Assert.Throws<InvalidOperationException>(() => (operation with { ApprovalSha256 = new string('0', 64) }).Validate());
        Assert.Throws<ArgumentException>(() => (operation with { TraceId = operation.TraceId + "\n" }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ConsumedApprovalSchemaCollectionsAndLengthsFailClosed()
    {
        var baseline = Approval("observe");
        var invalidApprovals = new[]
        {
            baseline with { SchemaVersion = "1.invalid" },
            baseline with { PolicyVersion = "1.0" },
            baseline with { EvaluatedPolicyIds = ["SOUL-ISO-001", "SOUL-ISO-001"] },
            baseline with { EvaluatedPolicyIds = ["not-a-policy-id"] },
            baseline with { EvaluatedPolicyIds = Enumerable.Range(0, 33).Select(index => $"POLICY-{index:000}").ToArray() },
            baseline with { PlatformAuthorizationId = new string('a', 257) },
            baseline with { Decision = ApprovalDecisionV1.Denied, DenialReasons = ["DENIED", "DENIED"] },
            baseline with { Decision = ApprovalDecisionV1.Denied, DenialReasons = [new string('d', 129)] },
            baseline with { Decision = ApprovalDecisionV1.Denied, DenialReasons = Enumerable.Range(0, 33).Select(index => $"DENIED-{index}").ToArray() },
            baseline with { Parameters = new Dictionary<string, string> { ["unexpected"] = new string('p', 257) } }
        };

        foreach (var invalid in invalidApprovals)
            Assert.NotNull(Record.Exception(() => CompileAuthoritative(invalid)));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void PublicContractStringGuardsBoundInputAndUseNonBacktrackingRegexes()
    {
        var regexes = typeof(OperationContractGuard)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(System.Text.RegularExpressions.Regex))
            .Select(field => Assert.IsType<System.Text.RegularExpressions.Regex>(field.GetValue(null)))
            .ToArray();
        Assert.NotEmpty(regexes);
        Assert.All(regexes, regex => Assert.True(regex.Options.HasFlag(System.Text.RegularExpressions.RegexOptions.NonBacktracking)));
        Assert.All(regexes, regex => Assert.NotEqual(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout, regex.MatchTimeout));

        Assert.Throws<ArgumentException>(() => OperationContractGuard.RequireSha256(new string('a', 65), "digest"));
        Assert.Throws<ArgumentException>(() => OperationContractGuard.RequireScope(
            "soul_" + new string('a', 65),
            "db_" + new string('a', 33),
            "pa_" + new string('a', 33)));
        Assert.Throws<ArgumentException>(() => OperationContractGuard.RequireText(new string(' ', 1_000_000), 128, "trace_id"));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void DuplicateArgumentEnumerationsFailClosedBeforeCanonicalization()
    {
        var duplicateParameters = new DuplicateKeyReadOnlyDictionary(
            new("selector_ref", "fixture.input"),
            new("selector_ref", "fixture.other"),
            new("value_ref", "value"));
        Assert.Throws<ArgumentException>(() => CompileAuthoritative(Approval("fixture.type", true, duplicateParameters)));

        var operation = CompileAuthoritative(Approval("fixture.type", true, new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "value"
        }));
        var forged = operation with { Steps = [operation.Steps[0] with { Arguments = duplicateParameters }] };
        Assert.Throws<ArgumentException>(() => forged.Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void CompilerAndValidateAndSnapshotDoNotExposeCallerMutableCollections()
    {
        var mutableParameters = new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "value"
        };
        var operation = CompileAuthoritative(Approval("fixture.type", true, mutableParameters));
        mutableParameters["value_ref"] = "changed-after-compile";
        Assert.Equal("value", operation.Steps[0].Arguments["value_ref"]);

        var mutableArguments = new Dictionary<string, string>(operation.Steps[0].Arguments, StringComparer.Ordinal);
        var mutableSteps = new List<OperationStepV1> { operation.Steps[0] with { Arguments = mutableArguments } };
        var frozen = (operation with { Steps = mutableSteps }).ValidateAndSnapshot();
        mutableArguments["value_ref"] = "changed-after-validation";
        mutableSteps.Clear();
        Assert.Single(frozen.Steps);
        Assert.Equal("value", frozen.Steps[0].Arguments["value_ref"]);
        frozen.Validate();
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task CompilationRequiresExactActiveAuthoritativeApprovalSnapshot()
    {
        var approval = Approval("observe");
        var digest = ApprovalSnapshotV1Canonical.ComputeSha256(approval);
        var request = Request(approval, digest);
        var reader = new FixedAuthoritativeApprovalReader(new AuthoritativeApprovalSnapshotV1(approval, digest, AuthoritativeApprovalSnapshotV1.Active));
        var compiler = new AllowlistedOperationCompiler(reader);
        var operation = await compiler.CompileAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(request, reader.LastRequest);
        Assert.Equal(digest, operation.ApprovalSha256);

        var inactive = new AllowlistedOperationCompiler(new FixedAuthoritativeApprovalReader(new AuthoritativeApprovalSnapshotV1(approval, digest, "REVOKED")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inactive.CompileAsync(request, TestContext.Current.CancellationToken));

        var mismatchedDigest = new AllowlistedOperationCompiler(new FixedAuthoritativeApprovalReader(new AuthoritativeApprovalSnapshotV1(approval, new string('0', 64), AuthoritativeApprovalSnapshotV1.Active)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mismatchedDigest.CompileAsync(request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => compiler.CompileAsync(request with { DeviceBindingId = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => compiler.CompileAsync(request with { TraceId = request.TraceId + "\n" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => compiler.CompileAsync(request with { IdempotencyKey = request.IdempotencyKey + "\n" }, TestContext.Current.CancellationToken));

        Assert.Null(typeof(AllowlistedOperationCompiler).GetConstructor(Type.EmptyTypes));
        Assert.DoesNotContain(typeof(AllowlistedOperationCompiler).GetMethods(), method =>
            method.Name == nameof(AllowlistedOperationCompiler.CompileAsync) &&
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(ApprovalDecisionV1)));
    }

    private static JsonDocument ReadEmbeddedJson(string resourceName)
    {
        var stream = typeof(CompiledOperationV1).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream);
    }

    private static byte[] ReadEmbeddedBytes(string resourceName)
    {
        using var stream = typeof(CompiledOperationV1).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static ApprovalDecisionV1 Approval(string action, bool sideEffect = false, IReadOnlyDictionary<string, string>? parameters = null) => new(
        ApprovalDecisionV1.CurrentSchemaVersion, ApprovalDecisionV1.CurrentContractId, ApprovalDecisionV1.CurrentProducerModule,
        Guid.Parse("51000000-0000-0000-0000-000000000001"), Guid.Parse("52000000-0000-0000-0000-000000000002"),
        "soul_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "db_11111111111111111111111111111111", "pa_22222222222222222222222222222222",
        "trace_33333333333333333333333333333333", "idem_4444444444444444444444444444444444444444444444444444444444444444", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "internal", action, sideEffect, false,
        parameters ?? new Dictionary<string, string>(), ApprovalDecisionV1.Approved, ApprovalDecisionV1.DeterministicAuthority, "1.0.0",
        ["SOUL-ISO-001", "CMD-IDEMP-001", "RESULT-VERIFY-001"], sideEffect ? "platform-auth:test" : null, []);

    private static CompiledOperationV1 CompileAuthoritative(ApprovalDecisionV1 approval)
    {
        var digest = ApprovalSnapshotV1Canonical.ComputeSha256(approval);
        var snapshot = new AuthoritativeApprovalSnapshotV1(approval, digest, AuthoritativeApprovalSnapshotV1.Active);
        return new AllowlistedOperationCompiler(new FixedAuthoritativeApprovalReader(snapshot))
            .CompileAsync(Request(approval, digest))
            .GetAwaiter()
            .GetResult();
    }

    private static ApprovalCompilationRequestV1 Request(ApprovalDecisionV1 approval, string digest) => new(
        approval.ApprovalId,
        approval.ProposalId,
        approval.SoulId,
        approval.DeviceBindingId,
        approval.PlatformAccountId,
        approval.TraceId,
        approval.IdempotencyKey,
        digest);

    private sealed class FixedAuthoritativeApprovalReader(AuthoritativeApprovalSnapshotV1 snapshot) : IAuthoritativeApprovalReader
    {
        public ApprovalCompilationRequestV1? LastRequest { get; private set; }
        public Task<AuthoritativeApprovalSnapshotV1> ReadAsync(
            ApprovalCompilationRequestV1 request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class DuplicateKeyReadOnlyDictionary(params KeyValuePair<string, string>[] values) : IReadOnlyDictionary<string, string>
    {
        public int Count => values.Length;
        public IEnumerable<string> Keys => values.Select(pair => pair.Key);
        public IEnumerable<string> Values => values.Select(pair => pair.Value);
        public string this[string key] => values.First(pair => string.Equals(pair.Key, key, StringComparison.Ordinal)).Value;
        public bool ContainsKey(string key) => values.Any(pair => string.Equals(pair.Key, key, StringComparison.Ordinal));
        public bool TryGetValue(string key, out string value)
        {
            foreach (var pair in values)
            {
                if (!string.Equals(pair.Key, key, StringComparison.Ordinal)) continue;
                value = pair.Value;
                return true;
            }
            value = null!;
            return false;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string>>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
