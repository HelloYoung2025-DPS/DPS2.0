using System.Text.Json;
using Dps.EvidenceService.Contracts;

namespace Dps.EvidenceService.Tests;

public sealed class TestEvidenceContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void Schema_requires_complete_common_identity_and_trace_envelope()
    {
        using var schema = LoadSchema();
        var required = schema.RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);

        var expected = new[]
        {
            "schema_version",
            "contract_id",
            "producer_module",
            "soul_id",
            "device_binding_id",
            "platform_account_id",
            "trace_id",
            "idempotency_key",
            "occurred_at",
            "privacy_class"
        };

        Assert.All(expected, field => Assert.Contains(field, required));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Equal(
            "^db_[a-f0-9]{32}$(?![\\s\\S])",
            properties.GetProperty("device_binding_id").GetProperty("pattern").GetString());
        Assert.Equal(
            "^pa_[a-f0-9]{32}$(?![\\s\\S])",
            properties.GetProperty("platform_account_id").GetProperty("pattern").GetString());
        Assert.Equal(
            "^trace_[a-f0-9]{32}$(?![\\s\\S])",
            properties.GetProperty("trace_id").GetProperty("pattern").GetString());
        Assert.Equal(
            "^idem_[a-f0-9]{64}$(?![\\s\\S])",
            properties.GetProperty("idempotency_key").GetProperty("pattern").GetString());

        var receipt = EvidenceTestData.Valid().Receipt;
        Assert.Throws<ArgumentException>(() => (receipt with { TraceId = receipt.TraceId + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (receipt with { IdempotencyKey = receipt.IdempotencyKey + "\n" }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Schema_exposes_exact_statuses_and_records_failure_without_calling_it_pass()
    {
        using var schema = LoadSchema();
        var statuses = schema.RootElement.GetProperty("properties").GetProperty("status").GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();

        Assert.Equal(
            ["PASS", "FAIL", "SKIP", "PARTIAL", "NOT_RUN", "INFRA_ERROR", "NOT_APPLICABLE"],
            statuses);

        var passRule = schema.RootElement.GetProperty("allOf")[0];
        Assert.Equal(
            "PASS",
            passRule.GetProperty("if").GetProperty("properties").GetProperty("status").GetProperty("const").GetString());
        var thenProperties = passRule.GetProperty("then").GetProperty("properties");
        Assert.Equal(0, thenProperties.GetProperty("exit_code").GetProperty("const").GetInt32());
        Assert.Equal(1, thenProperties.GetProperty("artifacts").GetProperty("minItems").GetInt32());
        Assert.Equal(
            "string",
            passRule.GetProperty("else").GetProperty("properties").GetProperty("reason_code").GetProperty("type").GetString());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Canonical_json_uses_snake_case_and_round_trips_strictly()
    {
        var valid = EvidenceTestData.Valid();
        var json = TestEvidenceCanonicalizer.Serialize(valid.Receipt);
        var roundTrip = TestEvidenceCanonicalizer.Deserialize(json);

        Assert.Contains("\"schema_version\":\"1.0.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"instruction_receipt_sha256\"", json, StringComparison.Ordinal);
        Assert.Equal(valid.Receipt.EvidenceId, roundTrip.EvidenceId);
        Assert.Equal(TestEvidenceCanonicalizer.ComputeSha256(valid.Receipt),
            TestEvidenceCanonicalizer.ComputeSha256(roundTrip));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Default_json_serializer_uses_the_same_snake_case_wire_names()
    {
        var valid = EvidenceTestData.Valid();
        var json = JsonSerializer.Serialize(valid.Receipt);
        var roundTrip = JsonSerializer.Deserialize<TestEvidenceV1>(json);

        Assert.Contains("\"schema_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"execution_environment\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source_receipts\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceReceipts", json, StringComparison.Ordinal);
        Assert.NotNull(roundTrip);
        roundTrip.Validate();
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Unknown_json_member_is_rejected_instead_of_ignored()
    {
        var valid = EvidenceTestData.Valid();
        var json = TestEvidenceCanonicalizer.Serialize(valid.Receipt);
        var tampered = json[..^1] + ",\"unknown_major_bypass\":true}";

        Assert.Throws<JsonException>(() => TestEvidenceCanonicalizer.Deserialize(tampered));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Null_digest_boundaries_fail_closed_with_validation_error()
    {
        var valid = EvidenceTestData.Valid();
        var nullInstruction = valid.Receipt with { InstructionReceiptSha256 = null! };
        var nullArtifact = valid.Receipt with
        {
            Artifacts = [valid.Receipt.Artifacts[0] with { Sha256 = null! }]
        };

        Assert.Throws<ArgumentException>(nullInstruction.Validate);
        Assert.Throws<ArgumentException>(nullArtifact.Validate);
    }

    private static JsonDocument LoadSchema()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contracts", "test.evidence.v1.schema.json");
        Assert.True(File.Exists(path), $"Contract schema missing at {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
