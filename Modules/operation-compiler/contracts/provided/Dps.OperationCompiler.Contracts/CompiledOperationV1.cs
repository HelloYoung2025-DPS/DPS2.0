using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.OperationCompiler.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationStepV1(
    [property: JsonPropertyName("step_id"), JsonRequired] Guid StepId,
    [property: JsonPropertyName("step_kind"), JsonRequired] string StepKind,
    [property: JsonPropertyName("arguments"), JsonRequired] IReadOnlyDictionary<string, string> Arguments,
    [property: JsonPropertyName("retry_safe"), JsonRequired] bool RetrySafe,
    [property: JsonPropertyName("postcondition_kind"), JsonRequired] string PostconditionKind)
{
    private static readonly IReadOnlyDictionary<string, string> AllowedSteps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ui.observe"] = "native-read-complete", ["ui.locate"] = "selector-resolved", ["ui.verify"] = "assertion-satisfied",
        ["control.wait"] = "timer-elapsed", ["fixture.tap"] = "fixture-state-changed", ["fixture.type"] = "fixture-value-matched"
    };
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedArguments = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["ui.observe"] = new HashSet<string>(StringComparer.Ordinal), ["ui.locate"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal),
        ["ui.verify"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal), ["control.wait"] = new HashSet<string>(["duration_ms"], StringComparer.Ordinal),
        ["fixture.tap"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal), ["fixture.type"] = new HashSet<string>(["selector_ref", "value_ref"], StringComparer.Ordinal)
    };
    public void Validate() => _ = SnapshotAndValidate();

    internal OperationStepV1 SnapshotAndValidate()
    {
        OperationContractGuard.RequireGuid(StepId, nameof(StepId));
        if (!AllowedSteps.TryGetValue(StepKind, out var postcondition) || !string.Equals(postcondition, PostconditionKind, StringComparison.Ordinal)) throw new NotSupportedException($"Unknown step or postcondition '{StepKind}/{PostconditionKind}'.");
        ArgumentNullException.ThrowIfNull(Arguments);
        var arguments = OperationContractGuard.SnapshotUniqueArguments(Arguments);
        if (arguments.Any(pair => pair.Key is "x" or "y" or "coordinates" or "coordinate")) throw new NotSupportedException("Coordinate fallback is forbidden.");
        var argumentKeys = arguments.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var allowedArguments = AllowedArguments[StepKind]; if (argumentKeys.Any(key => !allowedArguments.Contains(key)) || allowedArguments.Any(key => !argumentKeys.Contains(key))) throw new NotSupportedException("Step arguments do not match the allowlist.");
        if (arguments.Any(pair => pair.Value.Length is < 1 or > 256)) throw new ArgumentException("Step argument values must be non-empty strings of at most 256 characters.", nameof(Arguments));
        if (StepKind is "fixture.tap" or "fixture.type" && RetrySafe) throw new InvalidOperationException("Side-effect steps are never blindly retry safe.");
        return this with { Arguments = arguments.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompiledOperationV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("operation_id"), JsonRequired] Guid OperationId,
    [property: JsonPropertyName("approval_id"), JsonRequired] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id"), JsonRequired] Guid ProposalId,
    [property: JsonPropertyName("approval_sha256"), JsonRequired] string ApprovalSha256,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("action_kind"), JsonRequired] string ActionKind,
    [property: JsonPropertyName("is_side_effect"), JsonRequired] bool IsSideEffect,
    [property: JsonPropertyName("shadow_only"), JsonRequired] bool ShadowOnly,
    [property: JsonPropertyName("platform_authorization_id")] string? PlatformAuthorizationId,
    [property: JsonPropertyName("steps"), JsonRequired] IReadOnlyList<OperationStepV1> Steps)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "operation.compiled/v1";
    public const string CurrentProducerModule = "operation-compiler";
    private static readonly IReadOnlyDictionary<string, (string StepKind, bool SideEffect, string Postcondition)> AllowedActions = new Dictionary<string, (string, bool, string)>(StringComparer.Ordinal)
    {
        ["observe"] = ("ui.observe", false, "native-read-complete"), ["locate"] = ("ui.locate", false, "selector-resolved"), ["verify"] = ("ui.verify", false, "assertion-satisfied"),
        ["wait"] = ("control.wait", false, "timer-elapsed"), ["fixture.tap"] = ("fixture.tap", true, "fixture-state-changed"), ["fixture.type"] = ("fixture.type", true, "fixture-value-matched")
    };
    public void Validate() => _ = ValidateAndSnapshot();

    public CompiledOperationV1 ValidateAndSnapshot()
    {
        OperationContractGuard.RequireMajor(SchemaVersion, 1); OperationContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId)); OperationContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        OperationContractGuard.RequireGuid(OperationId, nameof(OperationId)); OperationContractGuard.RequireGuid(ApprovalId, nameof(ApprovalId)); OperationContractGuard.RequireGuid(ProposalId, nameof(ProposalId)); OperationContractGuard.RequireSha256(ApprovalSha256, nameof(ApprovalSha256)); OperationContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        OperationContractGuard.RequireTraceId(TraceId); OperationContractGuard.RequireIdempotencyKey(IdempotencyKey); OperationContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt)); OperationContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        if (ShadowOnly) throw new InvalidOperationException("A shadow operation cannot be dispatched.");
        if (IsSideEffect && string.IsNullOrWhiteSpace(PlatformAuthorizationId)) throw new InvalidOperationException("A side-effect operation requires platform authorization.");
        if (PlatformAuthorizationId is { Length: > 256 }) throw new ArgumentException("Platform authorization exceeds 256 characters.", nameof(PlatformAuthorizationId));
        if (!AllowedActions.TryGetValue(ActionKind, out var definition)) throw new NotSupportedException($"Unknown action '{ActionKind}'."); if (definition.SideEffect != IsSideEffect) throw new InvalidOperationException("Action side-effect classification does not match the allowlist.");
        var expectedOperationId = OperationCompiledV1CanonicalIds.ComputeOperationId(
            SchemaVersion, ContractId, ProducerModule, ApprovalId, ProposalId, ApprovalSha256,
            SoulId, DeviceBindingId, PlatformAccountId, TraceId, IdempotencyKey, OccurredAt,
            PrivacyClass, ActionKind, IsSideEffect, ShadowOnly, PlatformAuthorizationId);
        if (OperationId != expectedOperationId) throw new InvalidOperationException("operation_id does not match the operation.compiled/v1 canonical envelope and authoritative approval digest.");
        ArgumentNullException.ThrowIfNull(Steps); if (Steps.Count != 1) throw new InvalidOperationException("The v1 operation requires exactly one step."); var step = Steps[0]; ArgumentNullException.ThrowIfNull(step); step = step.SnapshotAndValidate(); if (!string.Equals(step.StepKind, definition.StepKind, StringComparison.Ordinal) || !string.Equals(step.PostconditionKind, definition.Postcondition, StringComparison.Ordinal)) throw new InvalidOperationException("Action, step, and postcondition do not match the allowlist.");
        var expectedStepId = OperationCompiledV1CanonicalIds.ComputeStepId(OperationId, step.StepKind, step.Arguments, step.RetrySafe, step.PostconditionKind);
        if (step.StepId != expectedStepId) throw new InvalidOperationException("step_id does not match the operation.compiled/v1 canonical input.");
        return this with { Steps = Array.AsReadOnly([step]) };
    }
}

public static class OperationCompiledV1Json
{
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false
    };

    public static string Serialize(CompiledOperationV1 operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return JsonSerializer.Serialize(operation.ValidateAndSnapshot(), StrictOptions);
    }

    public static CompiledOperationV1 Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var operation = JsonSerializer.Deserialize<CompiledOperationV1>(json, StrictOptions)
            ?? throw new JsonException("operation.compiled/v1 cannot be null.");
        return operation.ValidateAndSnapshot();
    }
}

public static class OperationCompiledV1CanonicalIds
{
    public const string OperationIdDomain = "dps.operation-compiler.operation-id/v1";
    public const string StepIdDomain = "dps.operation-compiler.step-id/v1";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static Guid ComputeOperationId(
        string schemaVersion,
        string contractId,
        string producerModule,
        Guid approvalId,
        Guid proposalId,
        string approvalSha256,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string privacyClass,
        string actionKind,
        bool isSideEffect,
        bool shadowOnly,
        string? platformAuthorizationId) => HashGuid(writer =>
        {
            writer.Field(OperationIdDomain);
            writer.Field(schemaVersion);
            writer.Field(contractId);
            writer.Field(producerModule);
            writer.Field(approvalId);
            writer.Field(proposalId);
            writer.Field(approvalSha256);
            writer.Field(soulId);
            writer.Field(deviceBindingId);
            writer.Field(platformAccountId);
            writer.Field(traceId);
            writer.Field(idempotencyKey);
            writer.Field(occurredAt);
            writer.Field(privacyClass);
            writer.Field(actionKind);
            writer.Field(isSideEffect);
            writer.Field(shadowOnly);
            writer.NullableField(platformAuthorizationId);
        });

    public static Guid ComputeStepId(Guid operationId, string stepKind, IReadOnlyDictionary<string, string> arguments, bool retrySafe, string postconditionKind)
    {
        OperationContractGuard.RequireGuid(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(stepKind);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(postconditionKind);
        var orderedArguments = OperationContractGuard.SnapshotUniqueArguments(arguments);
        return HashGuid(writer =>
        {
            writer.Field(StepIdDomain);
            writer.Field(operationId);
            writer.Field(stepKind);
            writer.Field(orderedArguments.Count);
            foreach (var pair in orderedArguments)
            {
                writer.Field(pair.Key);
                writer.Field(pair.Value);
            }
            writer.Field(retrySafe);
            writer.Field(postconditionKind);
        });
    }

    private static Guid HashGuid(Action<CanonicalFieldWriter> write)
    {
        using var writer = new CanonicalFieldWriter();
        write(writer);
        var canonicalBytes = writer.ToArray();
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(canonicalBytes, digest);
            return new Guid(digest[..16]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private sealed class CanonicalFieldWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        internal void Field(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = StrictUtf8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
                _stream.Write(length);
                _stream.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        internal void Field(Guid value) => Field(value.ToString("N"));
        internal void Field(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
        internal void Field(bool value) => Field(value ? "true" : "false");
        internal void Field(DateTimeOffset value) => Field(value.ToString("O", CultureInfo.InvariantCulture));
        internal void NullableField(string? value)
        {
            Field(value is not null);
            if (value is not null) Field(value);
        }
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}

public static class OperationContractGuard
{
    private const int Sha256Length = 64;
    private const int SoulIdLength = 69;
    private const RegexOptions SafeRegexOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan SafeRegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex SchemaVersionPattern = new("^1(?:\\.[0-9]+){0,2}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex SoulIdPattern = new("^soul_[a-f0-9]{64}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex DeviceBindingIdPattern = new("^db_[a-f0-9]{32}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex PlatformAccountIdPattern = new("^pa_[a-f0-9]{32}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex TraceIdPattern = new("^trace_[a-f0-9]{32}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex IdempotencyKeyPattern = new("^idem_[a-f0-9]{64}\\z", SafeRegexOptions, SafeRegexTimeout);
    private static readonly Regex Sha256Pattern = new("^[a-f0-9]{64}\\z", SafeRegexOptions, SafeRegexTimeout);
    public static void RequireMajor(string value, int expected) { RequireText(value, 32, nameof(value)); if (expected != 1 || !SchemaVersionPattern.IsMatch(value)) throw new NotSupportedException($"Unsupported contract major '{value}'."); }
    public static void RequireExact(string actual, string expected, string name) { if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new NotSupportedException($"Unsupported {name} '{actual}'."); }
    public static void RequireGuid(Guid value, string name) { if (value == Guid.Empty) throw new ArgumentException($"{name} cannot be empty.", name); }
    public static void RequireSha256(string value, string name) { if (value is null || value.Length != Sha256Length || !Sha256Pattern.IsMatch(value)) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireScope(string soul, string device, string account) { if (soul is null || soul.Length != SoulIdLength || !SoulIdPattern.IsMatch(soul)) throw new ArgumentException("Invalid opaque soul_id.", nameof(soul)); if (device is null || device.Length != 35 || !DeviceBindingIdPattern.IsMatch(device)) throw new ArgumentException("Invalid opaque device_binding_id.", nameof(device)); if (account is null || account.Length != 35 || !PlatformAccountIdPattern.IsMatch(account)) throw new ArgumentException("Invalid opaque platform_account_id.", nameof(account)); }
    public static void RequireTraceId(string value) { if (value is null || value.Length != 38 || !TraceIdPattern.IsMatch(value)) throw new ArgumentException("Invalid opaque trace_id.", nameof(value)); }
    public static void RequireIdempotencyKey(string value) { if (value is null || value.Length != 69 || !IdempotencyKeyPattern.IsMatch(value)) throw new ArgumentException("Invalid opaque idempotency_key.", nameof(value)); }
    public static void RequireText(string value, int max, string name) { if (value is null || value.Length == 0 || value.Length > max || string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireUtc(DateTimeOffset value, string name) { if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name); }
    internal static IReadOnlyList<KeyValuePair<string, string>> SnapshotUniqueArguments(IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > 16) throw new ArgumentException("operation.compiled/v1 accepts at most 16 argument entries before action-specific validation.", nameof(arguments));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new List<KeyValuePair<string, string>>();
        foreach (var pair in arguments)
        {
            if (snapshot.Count == 16) throw new ArgumentException("operation.compiled/v1 accepts at most 16 argument entries before action-specific validation.", nameof(arguments));
            if (pair.Key is null || !keys.Add(pair.Key)) throw new ArgumentException("Step argument keys must be unique strings.", nameof(arguments));
            if (pair.Key.Length is < 1 or > 64) throw new ArgumentException("Step argument keys must contain 1 to 64 characters.", nameof(arguments));
            if (pair.Value is null || pair.Value.Length is < 1 or > 256) throw new ArgumentException("Step argument values must contain 1 to 256 characters.", nameof(arguments));
            snapshot.Add(pair);
        }
        snapshot.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return snapshot;
    }
}
