using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.PolicyApproval.Contracts;

public sealed record ApprovalDecisionV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("action_kind")] string ActionKind,
    [property: JsonPropertyName("is_side_effect")] bool IsSideEffect,
    [property: JsonPropertyName("shadow_only")] bool ShadowOnly,
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, string> Parameters,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("authority")] string Authority,
    [property: JsonPropertyName("policy_version")] string PolicyVersion,
    [property: JsonPropertyName("evaluated_policy_ids")] IReadOnlyList<string> EvaluatedPolicyIds,
    [property: JsonPropertyName("platform_authorization_id")] string? PlatformAuthorizationId,
    [property: JsonPropertyName("denial_reasons")] IReadOnlyList<string> DenialReasons)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.decision/v1";
    public const string CurrentProducerModule = "policy-approval";
    public const string DeterministicAuthority = "deterministic-policy-engine";
    public const string Approved = "APPROVED";
    public const string Denied = "DENIED";
    private static readonly Regex PolicyIdPattern = new(
        "\\A[A-Z]+(?:-[A-Z]+)*-[0-9]{3}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly IReadOnlyDictionary<string, (bool SideEffect, IReadOnlySet<string> Parameters)> AllowedActions =
        new Dictionary<string, (bool, IReadOnlySet<string>)>(StringComparer.Ordinal)
        {
            ["observe"] = (false, new HashSet<string>(StringComparer.Ordinal)),
            ["locate"] = (false, new HashSet<string>(["selector_ref"], StringComparer.Ordinal)),
            ["verify"] = (false, new HashSet<string>(["selector_ref"], StringComparer.Ordinal)),
            ["wait"] = (false, new HashSet<string>(["duration_ms"], StringComparer.Ordinal)),
            ["fixture.tap"] = (true, new HashSet<string>(["selector_ref"], StringComparer.Ordinal)),
            ["fixture.type"] = (true, new HashSet<string>(["selector_ref", "value_ref"], StringComparer.Ordinal))
        };

    public void Validate()
    {
        ApprovalContractGuard.RequireExact(
            SchemaVersion,
            CurrentSchemaVersion,
            nameof(SchemaVersion));
        ApprovalContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ApprovalContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ApprovalContractGuard.RequireGuid(ApprovalId, nameof(ApprovalId));
        ApprovalContractGuard.RequireGuid(ProposalId, nameof(ProposalId));
        ApprovalContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        ApprovalContractGuard.RequireTraceId(TraceId);
        ApprovalContractGuard.RequireIdempotencyKey(IdempotencyKey);
        ApprovalContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        ApprovalContractGuard.RequireExact(Authority, DeterministicAuthority, nameof(Authority));
        ApprovalContractGuard.RequireSemVer(PolicyVersion);
        if (Decision is not (Approved or Denied)) throw new NotSupportedException($"Unknown decision '{Decision}'.");
        ArgumentNullException.ThrowIfNull(Parameters);
        if (!AllowedActions.TryGetValue(ActionKind, out var definition)) throw new NotSupportedException($"Unknown action '{ActionKind}'.");
        if (definition.SideEffect != IsSideEffect) throw new InvalidOperationException("Action side-effect classification does not match the allowlist.");
        if (Parameters.Count > 16) throw new InvalidOperationException("At most 16 action parameters are accepted.");
        foreach (var pair in Parameters)
        {
            ApprovalContractGuard.RequireText(pair.Key, 64, nameof(Parameters));
            ApprovalContractGuard.RequireText(pair.Value, 256, nameof(Parameters));
        }
        if (Parameters.Keys.Any(key => !definition.Parameters.Contains(key)) || definition.Parameters.Any(key => !Parameters.ContainsKey(key))) throw new NotSupportedException("Action parameters do not match the allowlist.");
        ArgumentNullException.ThrowIfNull(EvaluatedPolicyIds);
        ArgumentNullException.ThrowIfNull(DenialReasons);
        if (EvaluatedPolicyIds.Count is < 1 or > 32
            || EvaluatedPolicyIds.Distinct(StringComparer.Ordinal).Count() != EvaluatedPolicyIds.Count
            || EvaluatedPolicyIds.Any(policy => string.IsNullOrWhiteSpace(policy) || policy.Length > 64 || !PolicyIdPattern.IsMatch(policy)))
            throw new InvalidOperationException("One to 32 unique bounded policy IDs are required.");
        if (DenialReasons.Count > 32
            || DenialReasons.Distinct(StringComparer.Ordinal).Count() != DenialReasons.Count)
            throw new InvalidOperationException("At most 32 unique denial reasons are accepted.");
        foreach (var reason in DenialReasons)
            ApprovalContractGuard.RequireText(reason, 128, nameof(DenialReasons));
        if (PlatformAuthorizationId is not null)
            ApprovalContractGuard.RequireText(PlatformAuthorizationId, 256, nameof(PlatformAuthorizationId));
        if (Decision == Approved && (ShadowOnly || DenialReasons.Count != 0)) throw new InvalidOperationException("Approved decisions cannot be shadow-only or contain denial reasons.");
        if (Decision == Approved && IsSideEffect && string.IsNullOrWhiteSpace(PlatformAuthorizationId)) throw new InvalidOperationException("Side effects require platform authorization.");
        if (Decision == Denied && DenialReasons.Count == 0) throw new InvalidOperationException("Denied decisions require at least one reason.");
    }
}

public static class ApprovalDecisionV1Codec
{
    public const int MaximumPayloadBytes = 64 * 1024;
    private const string ContractName = "approval.decision/v1";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "approval_id",
        "proposal_id", "soul_id", "device_binding_id", "platform_account_id",
        "trace_id", "idempotency_key", "occurred_at", "privacy_class",
        "action_kind", "is_side_effect", "shadow_only", "parameters",
        "decision", "authority", "policy_version", "evaluated_policy_ids",
        "platform_authorization_id", "denial_reasons"
    };

    public static byte[] Serialize(ApprovalDecisionV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = SnapshotCollections(value);
        value.Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", value.SchemaVersion);
            writer.WriteString("contract_id", value.ContractId);
            writer.WriteString("producer_module", value.ProducerModule);
            writer.WriteString("approval_id", value.ApprovalId.ToString("D"));
            writer.WriteString("proposal_id", value.ProposalId.ToString("D"));
            writer.WriteString("soul_id", value.SoulId);
            writer.WriteString("device_binding_id", value.DeviceBindingId);
            writer.WriteString("platform_account_id", value.PlatformAccountId);
            writer.WriteString("trace_id", value.TraceId);
            writer.WriteString("idempotency_key", value.IdempotencyKey);
            writer.WriteString(
                "occurred_at",
                PolicyApprovalContractJson.FormatCanonicalUtc(value.OccurredAt));
            writer.WriteString("privacy_class", value.PrivacyClass);
            writer.WriteString("action_kind", value.ActionKind);
            writer.WriteBoolean("is_side_effect", value.IsSideEffect);
            writer.WriteBoolean("shadow_only", value.ShadowOnly);
            writer.WriteStartObject("parameters");
            foreach (var pair in value.Parameters.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteString("decision", value.Decision);
            writer.WriteString("authority", value.Authority);
            writer.WriteString("policy_version", value.PolicyVersion);
            writer.WriteStartArray("evaluated_policy_ids");
            foreach (var policy in value.EvaluatedPolicyIds) writer.WriteStringValue(policy);
            writer.WriteEndArray();
            if (value.PlatformAuthorizationId is null)
                writer.WriteNull("platform_authorization_id");
            else
                writer.WriteString("platform_authorization_id", value.PlatformAuthorizationId);
            writer.WriteStartArray("denial_reasons");
            foreach (var reason in value.DenialReasons) writer.WriteStringValue(reason);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentException(
                "Approval decision payload exceeds its byte budget.",
                nameof(value));
        }
        return payload;
    }

    public static ApprovalDecisionV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: true);

    public static ApprovalDecisionV1 DeserializeSemanticJsonb(
        ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: false);

    private static ApprovalDecisionV1 DeserializeCore(
        ReadOnlySpan<byte> payloadUtf8,
        bool requireCanonicalWire)
    {
        PolicyApprovalContractJson.RequirePayload(
            payloadUtf8,
            MaximumPayloadBytes,
            ContractName);
        using var document = JsonDocument.Parse(
            payloadUtf8.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        var fields = PolicyApprovalContractJson.ReadExactFields(
            document.RootElement,
            ExactFields,
            ExactFields,
            ContractName);
        var value = new ApprovalDecisionV1(
            PolicyApprovalContractJson.ReadString(fields, "schema_version", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "contract_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "producer_module", ContractName),
            PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "approval_id", ContractName),
            PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "proposal_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "soul_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "device_binding_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "platform_account_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "trace_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "idempotency_key", ContractName),
            PolicyApprovalContractJson.ReadCanonicalUtc(fields, "occurred_at", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "privacy_class", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "action_kind", ContractName),
            PolicyApprovalContractJson.ReadBoolean(fields, "is_side_effect", ContractName),
            PolicyApprovalContractJson.ReadBoolean(fields, "shadow_only", ContractName),
            PolicyApprovalContractJson.ReadStringMap(fields, "parameters", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "decision", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "authority", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "policy_version", ContractName),
            PolicyApprovalContractJson.ReadStringList(
                fields,
                "evaluated_policy_ids",
                ContractName),
            PolicyApprovalContractJson.ReadNullableString(
                fields,
                "platform_authorization_id",
                ContractName),
            PolicyApprovalContractJson.ReadStringList(
                fields,
                "denial_reasons",
                ContractName));
        value.Validate();
        if (requireCanonicalWire)
        {
            PolicyApprovalContractJson.RequireCanonicalWire(
                payloadUtf8,
                Serialize(value),
                ContractName);
        }
        return value;
    }

    private static ApprovalDecisionV1 SnapshotCollections(ApprovalDecisionV1 value)
    {
        ArgumentNullException.ThrowIfNull(value.Parameters);
        ArgumentNullException.ThrowIfNull(value.EvaluatedPolicyIds);
        ArgumentNullException.ThrowIfNull(value.DenialReasons);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value.Parameters)
        {
            if (!parameters.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException(
                    $"Duplicate approval decision parameter '{pair.Key}'.",
                    nameof(value));
            if (parameters.Count > 16)
                throw new ArgumentException(
                    "Approval decision parameters exceed their item budget.",
                    nameof(value));
        }
        var policyIds = value.EvaluatedPolicyIds.ToArray();
        var denialReasons = value.DenialReasons.ToArray();
        return value with
        {
            Parameters = parameters,
            EvaluatedPolicyIds = policyIds,
            DenialReasons = denialReasons
        };
    }
}

public static class ApprovalContractGuard
{
    private static readonly Regex SoulIdPattern = new("\\Asoul_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex DeviceBindingIdPattern = new("\\Adb_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PlatformAccountIdPattern = new("\\Apa_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TraceIdPattern = new("\\Atrace_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex IdempotencyKeyPattern = new("\\Aidem_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SemVerPattern = new(
        "\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    public static void RequireMajor(string value, int expected)
    {
        RequireText(value, 32, nameof(value));
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3
            || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || major != expected)
            throw new NotSupportedException($"Unsupported contract major '{value}'.");
    }
    public static void RequireExact(string actual, string expected, string name) { if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new NotSupportedException($"Unsupported {name} '{actual}'."); }
    public static void RequireGuid(Guid value, string name) { if (value == Guid.Empty) throw new ArgumentException($"{name} cannot be empty.", name); }
    public static void RequireScope(string soulId, string deviceBindingId, string platformAccountId) { if (!SoulIdPattern.IsMatch(soulId)) throw new ArgumentException("Invalid opaque soul_id.", nameof(soulId)); if (!DeviceBindingIdPattern.IsMatch(deviceBindingId)) throw new ArgumentException("Invalid opaque device_binding_id.", nameof(deviceBindingId)); if (!PlatformAccountIdPattern.IsMatch(platformAccountId)) throw new ArgumentException("Invalid opaque platform_account_id.", nameof(platformAccountId)); }
    public static void RequireTraceId(string value) { if (!TraceIdPattern.IsMatch(value)) throw new ArgumentException("Invalid opaque trace_id.", nameof(value)); }
    public static void RequireIdempotencyKey(string value) { if (!IdempotencyKeyPattern.IsMatch(value)) throw new ArgumentException("Invalid opaque idempotency_key.", nameof(value)); }
    public static void RequireText(string value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireUtc(DateTimeOffset value, string name) { if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name); }
    public static void RequireSemVer(string value)
    {
        if (value is null || value.Length > 32 || !SemVerPattern.IsMatch(value))
            throw new ArgumentException("PolicyVersion must be canonical numeric SemVer.", nameof(value));
    }
    public static void RequireSha256(string value, string name) { if (value is null || value.Length != 64 || value.Any(character => !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))) throw new ArgumentException($"{name} must be a lowercase SHA-256 digest.", name); }
    public static void RequireP256P1363Signature(string value, string name)
    {
        if (value is null || value.Length != 88)
            throw new ArgumentException($"{name} must be canonical Base64 for a 64-byte P-256 P1363 signature.", name);
        byte[] signature;
        try { signature = Convert.FromBase64String(value); }
        catch (FormatException exception)
        {
            throw new ArgumentException($"{name} must be valid canonical Base64.", name, exception);
        }
        try
        {
            if (signature.Length != 64 || !string.Equals(Convert.ToBase64String(signature), value, StringComparison.Ordinal))
                throw new ArgumentException($"{name} must be canonical Base64 for a 64-byte P-256 P1363 signature.", name);
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }
}
