using System.Text.RegularExpressions;

namespace Dps.ExecutorGateway.Contracts;

public sealed record NativeStepResultV1(Guid StepId, string StepKind, string Status, string NativeCode, string EvidenceDigest)
{
    public const string Success = "SUCCESS"; public const string Failed = "FAILED"; public const string Unknown = "UNKNOWN";
    private static readonly IReadOnlySet<string> AllowedStepKinds = new HashSet<string>(["ui.observe", "ui.locate", "ui.verify", "control.wait", "fixture.tap", "fixture.type"], StringComparer.Ordinal);
    public void Validate()
    {
        NativeContractGuard.RequireGuid(StepId, nameof(StepId)); NativeContractGuard.RequireText(StepKind, 128, nameof(StepKind)); if (!AllowedStepKinds.Contains(StepKind)) throw new NotSupportedException($"Unknown native step '{StepKind}'."); NativeContractGuard.RequireText(NativeCode, 128, nameof(NativeCode)); NativeContractGuard.RequireSha256(EvidenceDigest, nameof(EvidenceDigest));
        if (Status is not (Success or Failed or Unknown)) throw new NotSupportedException($"Unknown native status '{Status}'.");
    }
}

public sealed record NativeResultV1(
    string SchemaVersion, string ContractId, string ProducerModule, Guid NativeResultId, Guid CommandId, Guid LeaseId, int Attempt,
    string SoulId, string DeviceBindingId, string PlatformAccountId, string TraceId, string IdempotencyKey, DateTimeOffset OccurredAt,
    string PrivacyClass, string ActiveReleaseBomSha256, long ActiveReleaseBomGeneration, string ActiveReleaseBomTokenSha256,
    IReadOnlyList<NativeStepResultV1> StepResults)
{
    public const string CurrentSchemaVersion = "1.0.0"; public const string CurrentContractId = "native.result/v1"; public const string CurrentProducerModule = "executor-gateway";
    public void Validate()
    {
        NativeContractGuard.RequireMajor(SchemaVersion, 1); NativeContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId)); NativeContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule)); NativeContractGuard.RequireGuid(NativeResultId, nameof(NativeResultId)); NativeContractGuard.RequireGuid(CommandId, nameof(CommandId)); NativeContractGuard.RequireGuid(LeaseId, nameof(LeaseId)); if (Attempt is < 1 or > 3) throw new InvalidOperationException("Native result attempt must be between one and three."); NativeContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId); NativeContractGuard.RequireTraceId(TraceId); NativeContractGuard.RequireIdempotencyKey(IdempotencyKey); NativeContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt)); NativeContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass)); NativeContractGuard.RequireSha256(ActiveReleaseBomSha256, nameof(ActiveReleaseBomSha256)); if (ActiveReleaseBomGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ActiveReleaseBomGeneration)); NativeContractGuard.RequireSha256(ActiveReleaseBomTokenSha256, nameof(ActiveReleaseBomTokenSha256)); ArgumentNullException.ThrowIfNull(StepResults); if (StepResults.Count != 1) throw new InvalidOperationException("The v1 native result requires exactly one ordered step result."); StepResults[0].Validate();
    }
}

public static class NativeContractGuard
{
    private static readonly Regex Soul = new("^soul_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant); private static readonly Regex Device = new("^db_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Account = new("^pa_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Trace = new("^trace_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Idempotency = new("^idem_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    public static void RequireMajor(string value, int expected) { RequireText(value, 32, nameof(value)); if (!Regex.IsMatch(value, $"^{expected}(?:\\.[0-9]+){{0,2}}$", RegexOptions.CultureInvariant)) throw new NotSupportedException($"Unsupported contract major '{value}'."); }
    public static void RequireExact(string actual, string expected, string name) { if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new NotSupportedException($"Unsupported {name} '{actual}'."); }
    public static void RequireGuid(Guid value, string name) { if (value == Guid.Empty) throw new ArgumentException($"{name} cannot be empty.", name); }
    public static void RequireScope(string soul, string device, string account) { if (!Soul.IsMatch(soul) || !Device.IsMatch(device) || !Account.IsMatch(account)) throw new ArgumentException("Invalid opaque identity scope."); }
    public static void RequireTraceId(string value) { if (!Trace.IsMatch(value)) throw new ArgumentException("Invalid opaque trace_id.", nameof(value)); }
    public static void RequireIdempotencyKey(string value) { if (!Idempotency.IsMatch(value)) throw new ArgumentException("Invalid opaque idempotency_key.", nameof(value)); }
    public static void RequireText(string value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireUtc(DateTimeOffset value, string name) { if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name); }
    public static void RequireSha256(string value, string name) { if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)) || value.Any(char.IsUpper)) throw new ArgumentException($"{name} must be lowercase SHA-256.", name); }
}
