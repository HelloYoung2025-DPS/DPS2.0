using System.Text.RegularExpressions;

namespace Dps.CommandOrchestrator.Contracts;

public sealed record CommandStepV1(Guid StepId, string StepKind, IReadOnlyDictionary<string, string> Arguments, bool RetrySafe, string PostconditionKind)
{
    public static readonly IReadOnlySet<string> AllowedKinds = new HashSet<string>(["ui.observe", "ui.locate", "ui.verify", "control.wait", "fixture.tap", "fixture.type"], StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedArguments = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["ui.observe"] = new HashSet<string>(StringComparer.Ordinal), ["ui.locate"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal), ["ui.verify"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal),
        ["control.wait"] = new HashSet<string>(["duration_ms"], StringComparer.Ordinal), ["fixture.tap"] = new HashSet<string>(["selector_ref"], StringComparer.Ordinal), ["fixture.type"] = new HashSet<string>(["selector_ref", "value_ref"], StringComparer.Ordinal)
    };
    public void Validate()
    {
        CommandContractGuard.RequireGuid(StepId, nameof(StepId)); CommandContractGuard.RequireText(PostconditionKind, 128, nameof(PostconditionKind)); ArgumentNullException.ThrowIfNull(Arguments);
        if (!AllowedKinds.Contains(StepKind)) throw new NotSupportedException($"Unknown step '{StepKind}'.");
        if (Arguments.Keys.Any(key => key is "x" or "y" or "coordinates" or "coordinate")) throw new NotSupportedException("Coordinate fallback is forbidden.");
        var allowedArguments = AllowedArguments[StepKind]; if (Arguments.Keys.Any(key => !allowedArguments.Contains(key)) || allowedArguments.Any(key => !Arguments.ContainsKey(key))) throw new NotSupportedException("Step arguments do not match the allowlist.");
        foreach (var pair in Arguments) CommandContractGuard.RequireText(pair.Value, 256, $"Arguments[{pair.Key}]");
        if (StepKind is "fixture.tap" or "fixture.type" && RetrySafe) throw new InvalidOperationException("Side effects cannot be blindly retried.");
    }
}

public sealed record CommandDispatchV1(
    string SchemaVersion, string ContractId, string ProducerModule, Guid CommandId, Guid OperationId, Guid ApprovalId, string ApprovalSha256,
    string SoulId, string DeviceBindingId, string PlatformAccountId, string TraceId, string IdempotencyKey, DateTimeOffset OccurredAt,
    string PrivacyClass, string ActionKind, bool IsSideEffect, string? PlatformAuthorizationId, Guid LeaseId, string LeaseOwner,
    DateTimeOffset LeaseExpiresAt, int Attempt, IReadOnlyList<CommandStepV1> Steps)
{
    public const string CurrentSchemaVersion = "1.0.0"; public const string CurrentContractId = "command.dispatch/v1"; public const string CurrentProducerModule = "command-orchestrator";
    private static readonly IReadOnlyDictionary<string, (string StepKind, bool SideEffect, string Postcondition)> AllowedActions =
        new Dictionary<string, (string, bool, string)>(StringComparer.Ordinal)
        {
            ["observe"] = ("ui.observe", false, "native-read-complete"),
            ["locate"] = ("ui.locate", false, "selector-resolved"),
            ["verify"] = ("ui.verify", false, "assertion-satisfied"),
            ["wait"] = ("control.wait", false, "timer-elapsed"),
            ["fixture.tap"] = ("fixture.tap", true, "fixture-state-changed"),
            ["fixture.type"] = ("fixture.type", true, "fixture-value-matched")
        };
    public void Validate()
    {
        CommandContractGuard.RequireMajor(SchemaVersion, 1); CommandContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId)); CommandContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        CommandContractGuard.RequireGuid(CommandId, nameof(CommandId)); CommandContractGuard.RequireGuid(OperationId, nameof(OperationId)); CommandContractGuard.RequireGuid(ApprovalId, nameof(ApprovalId)); CommandContractGuard.RequireSha256(ApprovalSha256, nameof(ApprovalSha256)); CommandContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        CommandContractGuard.RequireTraceId(TraceId); CommandContractGuard.RequireIdempotencyKey(IdempotencyKey); CommandContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt)); CommandContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        CommandContractGuard.RequireGuid(LeaseId, nameof(LeaseId)); CommandContractGuard.RequireText(LeaseOwner, 128, nameof(LeaseOwner)); CommandContractGuard.RequireUtc(LeaseExpiresAt, nameof(LeaseExpiresAt));
        if (LeaseExpiresAt <= OccurredAt) throw new InvalidOperationException("Lease must expire after dispatch creation."); if (Attempt is < 1 or > 3) throw new InvalidOperationException("Attempt must be between one and three.");
        if (!AllowedActions.TryGetValue(ActionKind, out var definition)) throw new NotSupportedException($"Unknown action '{ActionKind}'.");
        if (IsSideEffect != definition.SideEffect) throw new InvalidOperationException("Action side-effect classification does not match the signed allowlist.");
        if (PlatformAuthorizationId is not null) CommandContractGuard.RequireText(PlatformAuthorizationId, 256, nameof(PlatformAuthorizationId));
        if (IsSideEffect && string.IsNullOrWhiteSpace(PlatformAuthorizationId)) throw new InvalidOperationException("Side effect requires platform authorization.");
        ArgumentNullException.ThrowIfNull(Steps); if (Steps.Count != 1) throw new InvalidOperationException("The v1 command protocol requires exactly one compiled step.");
        var step = Steps[0]; step.Validate();
        if (!string.Equals(step.StepKind, definition.StepKind, StringComparison.Ordinal) || !string.Equals(step.PostconditionKind, definition.Postcondition, StringComparison.Ordinal)) throw new InvalidOperationException("Action, step, and postcondition do not match the signed allowlist.");
    }
}

public sealed record CommandReceiptV1(
    string SchemaVersion, string ContractId, string ProducerModule, Guid ReceiptId, Guid CommandId, Guid LeaseId, int Attempt,
    string SoulId, string DeviceBindingId, string PlatformAccountId, string TraceId, string IdempotencyKey, DateTimeOffset OccurredAt,
    string PrivacyClass, string Outcome, Guid? NativeResultId, bool NativeResultVerified, bool PostconditionVerified,
    string EvidenceDigest, bool RetryAllowed, string ResultCode)
{
    public const string CurrentSchemaVersion = "1.0.0"; public const string CurrentContractId = "command.receipt/v1"; public const string CurrentProducerModule = "executor-gateway";
    public const string Success = "SUCCESS"; public const string Failed = "FAILED"; public const string UnknownOutcome = "UNKNOWN_OUTCOME";
    public void Validate()
    {
        CommandContractGuard.RequireMajor(SchemaVersion, 1); CommandContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId)); CommandContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule)); CommandContractGuard.RequireGuid(ReceiptId, nameof(ReceiptId)); CommandContractGuard.RequireGuid(CommandId, nameof(CommandId)); CommandContractGuard.RequireGuid(LeaseId, nameof(LeaseId)); if (Attempt is < 1 or > 3) throw new InvalidOperationException("Receipt attempt must be between one and three."); CommandContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        CommandContractGuard.RequireTraceId(TraceId); CommandContractGuard.RequireIdempotencyKey(IdempotencyKey); CommandContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt)); CommandContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass)); CommandContractGuard.RequireSha256(EvidenceDigest, nameof(EvidenceDigest)); CommandContractGuard.RequireText(ResultCode, 128, nameof(ResultCode));
        if (Outcome is not (Success or Failed or UnknownOutcome)) throw new NotSupportedException($"Unknown command outcome '{Outcome}'.");
        if (Outcome == Success && (!NativeResultVerified || !PostconditionVerified || NativeResultId is null)) throw new InvalidOperationException("SUCCESS requires native and business-postcondition verification.");
        if (Outcome == Failed && RetryAllowed && (!NativeResultVerified || NativeResultId is null)) throw new InvalidOperationException("Retryable FAILED requires a verified native result.");
        if (Outcome == UnknownOutcome && RetryAllowed) throw new InvalidOperationException("UNKNOWN_OUTCOME cannot be blindly retried.");
    }
}

public static class CommandContractGuard
{
    private static readonly Regex Soul = new("^soul_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant); private static readonly Regex Device = new("^db_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Account = new("^pa_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Trace = new("^trace_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Idempotency = new("^idem_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    public static void RequireMajor(string value, int expected) { RequireText(value, 32, nameof(value)); if (!Regex.IsMatch(value, $"\\A{expected}(?:\\.[0-9]+){{0,2}}\\z", RegexOptions.CultureInvariant)) throw new NotSupportedException($"Unsupported contract major '{value}'."); }
    public static void RequireExact(string actual, string expected, string name) { if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new NotSupportedException($"Unsupported {name} '{actual}'."); }
    public static void RequireGuid(Guid value, string name) { if (value == Guid.Empty) throw new ArgumentException($"{name} cannot be empty.", name); }
    public static void RequireScope(string soul, string device, string account) { if (!Soul.IsMatch(soul) || !Device.IsMatch(device) || !Account.IsMatch(account)) throw new ArgumentException("Invalid opaque identity scope."); }
    public static void RequireDeviceBindingId(string value) { if (!Device.IsMatch(value)) throw new ArgumentException("Invalid opaque device_binding_id.", nameof(value)); }
    public static void RequireTraceId(string value) { if (!Trace.IsMatch(value)) throw new ArgumentException("Invalid opaque trace_id.", nameof(value)); }
    public static void RequireIdempotencyKey(string value) { if (!Idempotency.IsMatch(value)) throw new ArgumentException("Invalid opaque idempotency_key.", nameof(value)); }
    public static void RequireText(string value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireUtc(DateTimeOffset value, string name) { if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name); }
    public static void RequireSha256(string value, string name) { if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)) || value.Any(char.IsUpper)) throw new ArgumentException($"{name} must be lowercase SHA-256.", name); }
}
