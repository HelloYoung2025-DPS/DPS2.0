using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Dps.EvidenceService.Contracts;

public sealed record EvidenceArtifactV1(
    [property: JsonPropertyName("artifact_id"), JsonPropertyOrder(0)] string ArtifactId,
    [property: JsonPropertyName("sha256"), JsonPropertyOrder(1)] string Sha256,
    [property: JsonPropertyName("size_bytes"), JsonPropertyOrder(2)] long SizeBytes,
    [property: JsonPropertyName("media_type"), JsonPropertyOrder(3)] string MediaType);

public sealed record SourceReceiptDigestV1(
    [property: JsonPropertyName("contract_id"), JsonPropertyOrder(0)] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonPropertyOrder(1)] string ProducerModule,
    [property: JsonPropertyName("sha256"), JsonPropertyOrder(2)] string Sha256);

public sealed record TestEvidenceV1(
    [property: JsonPropertyName("schema_version"), JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonPropertyOrder(1)] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonPropertyOrder(2)] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonPropertyOrder(3)] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonPropertyOrder(4)] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonPropertyOrder(5)] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonPropertyOrder(6)] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonPropertyOrder(7)] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonPropertyOrder(8)] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonPropertyOrder(9)] string PrivacyClass,
    [property: JsonPropertyName("evidence_id"), JsonPropertyOrder(10)] Guid EvidenceId,
    [property: JsonPropertyName("test_id"), JsonPropertyOrder(11)] string TestId,
    [property: JsonPropertyName("module_id"), JsonPropertyOrder(12)] string ModuleId,
    [property: JsonPropertyName("test_type"), JsonPropertyOrder(13)] string TestType,
    [property: JsonPropertyName("execution_environment"), JsonPropertyOrder(14)] string ExecutionEnvironment,
    [property: JsonPropertyName("required"), JsonPropertyOrder(15)] bool Required,
    [property: JsonPropertyName("status"), JsonPropertyOrder(16)] string Status,
    [property: JsonPropertyName("verification_level"), JsonPropertyOrder(17)] string VerificationLevel,
    [property: JsonPropertyName("baseline_commit"), JsonPropertyOrder(18)] string BaselineCommit,
    [property: JsonPropertyName("instruction_receipt_id"), JsonPropertyOrder(19)] string InstructionReceiptId,
    [property: JsonPropertyName("instruction_receipt_sha256"), JsonPropertyOrder(20)] string InstructionReceiptSha256,
    [property: JsonPropertyName("implementer_identity"), JsonPropertyOrder(21)] string ImplementerIdentity,
    [property: JsonPropertyName("evidence_issuer_identity"), JsonPropertyOrder(22)] string EvidenceIssuerIdentity,
    [property: JsonPropertyName("release_approver_identity"), JsonPropertyOrder(23)] string? ReleaseApproverIdentity,
    [property: JsonPropertyName("command_sha256"), JsonPropertyOrder(24)] string CommandSha256,
    [property: JsonPropertyName("started_at"), JsonPropertyOrder(25)] DateTimeOffset StartedAt,
    [property: JsonPropertyName("finished_at"), JsonPropertyOrder(26)] DateTimeOffset FinishedAt,
    [property: JsonPropertyName("exit_code"), JsonPropertyOrder(27)] int? ExitCode,
    [property: JsonPropertyName("artifacts"), JsonPropertyOrder(28)] IReadOnlyList<EvidenceArtifactV1> Artifacts,
    [property: JsonPropertyName("source_receipts"), JsonPropertyOrder(29)] IReadOnlyList<SourceReceiptDigestV1> SourceReceipts,
    [property: JsonPropertyName("reason_code"), JsonPropertyOrder(30)] string? ReasonCode)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "test.evidence/v1";
    public const string CurrentProducerModule = "evidence-service";

    public void Validate() => TestEvidenceContractValidation.Validate(this);
}

public static class TestEvidenceCanonicalizer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string Serialize(TestEvidenceV1 evidence)
    {
        var normalized = Normalize(evidence);
        normalized.Validate();
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static string ComputeSha256(TestEvidenceV1 evidence)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(evidence))));
    }

    public static TestEvidenceV1 Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Evidence JSON is required.", nameof(json));
        }

        var result = JsonSerializer.Deserialize<TestEvidenceV1>(json, SerializerOptions)
            ?? throw new JsonException("Evidence JSON deserialized to null.");
        result.Validate();
        return Normalize(result);
    }

    private static TestEvidenceV1 Normalize(TestEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Artifacts);
        ArgumentNullException.ThrowIfNull(evidence.SourceReceipts);

        return evidence with
        {
            Artifacts = evidence.Artifacts
                .OrderBy(static item => item.ArtifactId, StringComparer.Ordinal)
                .ThenBy(static item => item.Sha256, StringComparer.Ordinal)
                .ToArray(),
            SourceReceipts = evidence.SourceReceipts
                .OrderBy(static item => item.ContractId, StringComparer.Ordinal)
                .ThenBy(static item => item.ProducerModule, StringComparer.Ordinal)
                .ThenBy(static item => item.Sha256, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.MakeReadOnly();
        return options;
    }
}

public static class TestEvidenceContractValidation
{
    private static readonly string[] AllowedStatuses =
    [
        "PASS", "FAIL", "SKIP", "PARTIAL", "NOT_RUN", "INFRA_ERROR", "NOT_APPLICABLE"
    ];

    private static readonly string[] AllowedTestTypes =
    [
        "static", "unit", "contract", "integration", "simulated", "windows", "device", "canary", "scale"
    ];

    private static readonly string[] AllowedEnvironments =
    [
        "local", "hosted", "mock", "simulated", "windows", "device", "canary", "scale"
    ];

    private static readonly string[] VerificationLevels =
    [
        "REPOSITORY_STATIC_VERIFIED",
        "CONTRACT_VERIFIED",
        "INTEGRATION_VERIFIED",
        "WINDOWS_VERIFIED",
        "DEVICE_VERIFIED",
        "CANARY_VERIFIED",
        "SCALE_VERIFIED"
    ];

    public static void Validate(TestEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        RequireExact(evidence.SchemaVersion, TestEvidenceV1.CurrentSchemaVersion, nameof(evidence.SchemaVersion));
        RequireExact(evidence.ContractId, TestEvidenceV1.CurrentContractId, nameof(evidence.ContractId));
        RequireExact(evidence.ProducerModule, TestEvidenceV1.CurrentProducerModule, nameof(evidence.ProducerModule));
        RequireSoulId(evidence.SoulId, nameof(evidence.SoulId));
        RequireOpaqueId(evidence.DeviceBindingId, "db_", nameof(evidence.DeviceBindingId));
        RequireOpaqueId(evidence.PlatformAccountId, "pa_", nameof(evidence.PlatformAccountId));
        RequirePrefixedLowerHex(evidence.TraceId, "trace_", 32, nameof(evidence.TraceId));
        RequirePrefixedLowerHex(evidence.IdempotencyKey, "idem_", 64, nameof(evidence.IdempotencyKey));
        RequireUtc(evidence.OccurredAt, nameof(evidence.OccurredAt));
        RequireOneOf(evidence.PrivacyClass, ["internal", "personal", "pseudonymous"], nameof(evidence.PrivacyClass));

        if (evidence.EvidenceId == Guid.Empty)
        {
            throw new ArgumentException("EvidenceId cannot be empty.", nameof(evidence.EvidenceId));
        }

        RequireIdentifier(evidence.TestId, nameof(evidence.TestId));
        RequireModuleId(evidence.ModuleId, nameof(evidence.ModuleId));
        RequireOneOf(evidence.TestType, AllowedTestTypes, nameof(evidence.TestType));
        RequireOneOf(evidence.ExecutionEnvironment, AllowedEnvironments, nameof(evidence.ExecutionEnvironment));
        RequireOneOf(evidence.Status, AllowedStatuses, nameof(evidence.Status));
        RequireOneOf(evidence.VerificationLevel, VerificationLevels, nameof(evidence.VerificationLevel));
        RequireLowerHex(evidence.BaselineCommit, 40, nameof(evidence.BaselineCommit));
        RequireIdentifier(evidence.InstructionReceiptId, nameof(evidence.InstructionReceiptId));
        RequireSha256(evidence.InstructionReceiptSha256, nameof(evidence.InstructionReceiptSha256));
        RequireIdentity(evidence.ImplementerIdentity, nameof(evidence.ImplementerIdentity));
        RequireIdentity(evidence.EvidenceIssuerIdentity, nameof(evidence.EvidenceIssuerIdentity));
        if (evidence.ReleaseApproverIdentity is not null)
        {
            RequireIdentity(evidence.ReleaseApproverIdentity, nameof(evidence.ReleaseApproverIdentity));
        }

        if (string.Equals(evidence.ImplementerIdentity, evidence.EvidenceIssuerIdentity, StringComparison.Ordinal) ||
            string.Equals(evidence.ImplementerIdentity, evidence.ReleaseApproverIdentity, StringComparison.Ordinal) ||
            string.Equals(evidence.EvidenceIssuerIdentity, evidence.ReleaseApproverIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Implementation, evidence issuance, and release approval identities must be separated.");
        }

        RequireSha256(evidence.CommandSha256, nameof(evidence.CommandSha256));
        RequireUtc(evidence.StartedAt, nameof(evidence.StartedAt));
        RequireUtc(evidence.FinishedAt, nameof(evidence.FinishedAt));
        if (evidence.FinishedAt < evidence.StartedAt || evidence.OccurredAt != evidence.FinishedAt)
        {
            throw new ArgumentException("Evidence time must be UTC, ordered, and occurred_at must equal finished_at.", nameof(evidence.FinishedAt));
        }

        ArgumentNullException.ThrowIfNull(evidence.Artifacts);
        ArgumentNullException.ThrowIfNull(evidence.SourceReceipts);
        ValidateArtifacts(evidence.Artifacts);
        ValidateSourceReceipts(evidence.SourceReceipts);

        if (string.Equals(evidence.Status, "PASS", StringComparison.Ordinal) &&
            (evidence.ExitCode != 0 || evidence.Artifacts.Count == 0))
        {
            throw new InvalidOperationException("PASS evidence requires exit code zero and at least one raw artifact digest.");
        }

        if (!string.Equals(evidence.Status, "PASS", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(evidence.ReasonCode))
        {
            throw new InvalidOperationException("Non-PASS evidence requires a machine-readable reason code.");
        }

        if (evidence.ReasonCode is not null)
        {
            RequireReasonCode(evidence.ReasonCode);
        }

        ValidateVerificationCeiling(evidence);
    }

    public static void RequireSha256(string value, string parameterName) => RequireLowerHex(value, 64, parameterName);

    private static void ValidateArtifacts(IReadOnlyList<EvidenceArtifactV1> artifacts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            RequireIdentifier(artifact.ArtifactId, nameof(artifact.ArtifactId));
            RequireSha256(artifact.Sha256, nameof(artifact.Sha256));
            if (artifact.SizeBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artifact.SizeBytes));
            }

            RequireText(artifact.MediaType, 128, nameof(artifact.MediaType));
            if (!ids.Add(artifact.ArtifactId))
            {
                throw new ArgumentException("Artifact identifiers must be unique.", nameof(artifacts));
            }
        }
    }

    private static void ValidateSourceReceipts(IReadOnlyList<SourceReceiptDigestV1> sourceReceipts)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in sourceReceipts)
        {
            ArgumentNullException.ThrowIfNull(receipt);
            RequireContractId(receipt.ContractId, nameof(receipt.ContractId));
            RequireModuleId(receipt.ProducerModule, nameof(receipt.ProducerModule));
            RequireSha256(receipt.Sha256, nameof(receipt.Sha256));
            if (!keys.Add(receipt.ContractId + "\n" + receipt.ProducerModule))
            {
                throw new ArgumentException("Source receipt owner pairs must be unique.", nameof(sourceReceipts));
            }
        }
    }

    private static void ValidateVerificationCeiling(TestEvidenceV1 evidence)
    {
        var requested = Array.IndexOf(VerificationLevels, evidence.VerificationLevel);
        var testCeiling = evidence.TestType switch
        {
            "static" or "unit" => 0,
            "contract" => 1,
            "integration" or "simulated" => 2,
            "windows" => 3,
            "device" => 4,
            "canary" => 5,
            "scale" => 6,
            _ => -1
        };
        var environmentCeiling = evidence.ExecutionEnvironment switch
        {
            "mock" => 0,
            "hosted" => 1,
            "local" or "simulated" => 2,
            "windows" => 3,
            "device" => 4,
            "canary" => 5,
            "scale" => 6,
            _ => -1
        };

        if (requested < 0 || requested > Math.Min(testCeiling, environmentCeiling))
        {
            throw new InvalidOperationException("Evidence type or execution environment cannot claim the requested verification level.");
        }
    }

    private static void RequireExact(string actual, string expected, string parameterName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported {parameterName} '{actual}'. Expected '{expected}'.");
        }
    }

    private static void RequireText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"{parameterName} must contain between 1 and {maximumLength} characters.", parameterName);
        }
    }

    private static void RequireOneOf(string value, IReadOnlyCollection<string> allowed, string parameterName)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new NotSupportedException($"Unsupported {parameterName} '{value}'.");
        }
    }

    private static void RequireSoulId(string value, string parameterName)
    {
        if (value is null || value.Length != 69 || !value.StartsWith("soul_", StringComparison.Ordinal) ||
            value.AsSpan(5).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"{parameterName} must be an opaque soul_ identifier.", parameterName);
        }
    }

    private static void RequireOpaqueId(string value, string prefix, string parameterName)
    {
        RequirePrefixedLowerHex(value, prefix, 32, parameterName);
    }

    private static void RequirePrefixedLowerHex(string value, string prefix, int bodyLength, string parameterName)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + bodyLength ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException($"{parameterName} must be a canonical opaque identifier.", parameterName);
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{parameterName} must be UTC.", parameterName);
        }
    }

    private static void RequireLowerHex(string value, int length, string parameterName)
    {
        if (value is null || value.Length != length || value.AsSpan().ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"{parameterName} must be {length} lowercase hexadecimal characters.", parameterName);
        }
    }

    private static void RequireIdentifier(string value, string parameterName)
    {
        RequireText(value, 128, parameterName);
        if (!char.IsLetterOrDigit(value[0]) || value.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not ':' and not '/' and not '-'))
        {
            throw new ArgumentException($"{parameterName} is not a safe identifier.", parameterName);
        }
    }

    private static void RequireIdentity(string value, string parameterName)
    {
        RequireText(value, 128, parameterName);
        if (!char.IsLetterOrDigit(value[0]) || value.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not ':' and not '@' and not '/' and not '-'))
        {
            throw new ArgumentException($"{parameterName} is not a safe actor identity.", parameterName);
        }
    }

    private static void RequireModuleId(string value, string parameterName)
    {
        RequireText(value, 128, parameterName);
        var parts = value.Split('-', StringSplitOptions.None);
        if (parts.Any(static part => part.Length == 0 || part.Any(static character => !char.IsAsciiLetterOrDigit(character) || char.IsUpper(character))))
        {
            throw new ArgumentException($"{parameterName} must be lowercase kebab-case.", parameterName);
        }
    }

    private static void RequireContractId(string value, string parameterName)
    {
        RequireText(value, 128, parameterName);
        var separator = value.LastIndexOf("/v", StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= value.Length ||
            !int.TryParse(value[(separator + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major <= 0)
        {
            throw new ArgumentException($"{parameterName} must contain an explicit positive major version.", parameterName);
        }
    }

    private static void RequireReasonCode(string value)
    {
        RequireText(value, 128, nameof(value));
        if (!char.IsAsciiLetterOrDigit(value[0]) || char.IsUpper(value[0]) || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-') ||
            value.Any(char.IsUpper))
        {
            throw new ArgumentException("ReasonCode must be lowercase and machine-readable.", nameof(value));
        }
    }
}

public static class TestEvidenceReleaseEvaluator
{
    public static bool SatisfiesRequiredGate(TestEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        return evidence.Required &&
               string.Equals(evidence.Status, "PASS", StringComparison.Ordinal) &&
               evidence.ExitCode == 0 &&
               evidence.Artifacts.Count > 0;
    }
}
