using System.Globalization;
using System.Text.RegularExpressions;

namespace Dps.AuditMetrics.Contracts;

public sealed record AuditEventV1(
    string SchemaVersion, string ContractId, string ProducerModule, Guid AuditEventId, Guid SubjectId,
    string SoulId, string DeviceBindingId, string PlatformAccountId, string TraceId, string IdempotencyKey, DateTimeOffset OccurredAt,
    string PrivacyClass, string EventType, string Outcome, string SourceContractId, string EvidenceDigest, IReadOnlyDictionary<string, string> Labels)
{
    public const string CurrentSchemaVersion = "1.0.0"; public const string CurrentContractId = "audit.event/v1"; public const string CurrentProducerModule = "audit-metrics";
    public void Validate()
    {
        AuditContractGuard.RequireMajor(SchemaVersion, 1); AuditContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId)); AuditContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule)); AuditContractGuard.RequireGuid(AuditEventId, nameof(AuditEventId)); AuditContractGuard.RequireGuid(SubjectId, nameof(SubjectId)); AuditContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId); AuditContractGuard.RequireTraceId(TraceId); AuditContractGuard.RequireIdempotencyKey(IdempotencyKey); AuditContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt)); AuditContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass)); AuditContractGuard.RequireExact(EventType, "command.completed", nameof(EventType)); AuditContractGuard.RequireExact(SourceContractId, "command.receipt/v1", nameof(SourceContractId)); AuditContractGuard.RequireSha256(EvidenceDigest, nameof(EvidenceDigest));
        if (Outcome is not ("SUCCESS" or "FAILED" or "UNKNOWN_OUTCOME")) throw new NotSupportedException($"Unknown audit outcome '{Outcome}'."); ArgumentNullException.ThrowIfNull(Labels);
        var allowed = new HashSet<string>(["result_code", "verification_class"], StringComparer.Ordinal); if (Labels.Count != 2 || Labels.Keys.Any(key => !allowed.Contains(key))) throw new NotSupportedException("Only low-cardinality audit labels are allowed.");
        foreach (var pair in Labels) AuditContractGuard.RequireSafeLabel(pair.Key, pair.Value);
        var expected = Outcome switch { "SUCCESS" => "verified", "FAILED" => "failed", "UNKNOWN_OUTCOME" => "unknown", _ => throw new NotSupportedException() };
        if (!Labels.TryGetValue("verification_class", out var actual) || !string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException("Audit verification class cannot upgrade or change the receipt outcome.");
    }
}

public static class AuditContractGuard
{
    private static readonly Regex Soul = new("^soul_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant); private static readonly Regex Device = new("^db_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Account = new("^pa_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Trace = new("^trace_[a-f0-9]{32}\\z", RegexOptions.CultureInvariant); private static readonly Regex Idempotency = new("^idem_[a-f0-9]{64}\\z", RegexOptions.CultureInvariant); private static readonly Regex PhoneLike = new("\\+?[0-9][0-9 -]{7,}[0-9]", RegexOptions.CultureInvariant); private static readonly Regex OpaqueMetadata = new("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant);
    public static void RequireMajor(string value, int expected) { RequireText(value, 32, nameof(value)); if (!int.TryParse(value.Split('.', 2)[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major != expected) throw new NotSupportedException($"Unsupported contract major '{value}'."); }
    public static void RequireExact(string actual, string expected, string name) { if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new NotSupportedException($"Unsupported {name} '{actual}'."); }
    public static void RequireGuid(Guid value, string name) { if (value == Guid.Empty) throw new ArgumentException($"{name} cannot be empty.", name); }
    public static void RequireScope(string soul, string device, string account) { if (!Soul.IsMatch(soul) || !Device.IsMatch(device) || !Account.IsMatch(account)) throw new ArgumentException("Invalid opaque identity scope."); }
    public static void RequireTraceId(string value) { if (!Trace.IsMatch(value)) throw new ArgumentException("Invalid opaque trace_id.", nameof(value)); }
    public static void RequireIdempotencyKey(string value) { if (!Idempotency.IsMatch(value)) throw new ArgumentException("Invalid opaque idempotency_key.", nameof(value)); }
    public static void RequireText(string value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireOpaqueMetadata(string value, int max, string name) { RequireText(value, max, name); var lower = value.ToLowerInvariant(); if (!OpaqueMetadata.IsMatch(value) || lower.Contains('@') || PhoneLike.IsMatch(value) || new[] { "secret=", "secret:", "token=", "token:", "password=", "password:", "email=", "email:", "phone=", "phone:", "cookie=", "cookie:", "authorization:", "bearer " }.Any(lower.Contains)) throw new ArgumentException($"{name} may contain raw PII or secret material.", name); }
    public static void RequireUtc(DateTimeOffset value, string name) { if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name); }
    public static void RequireSha256(string value, string name) { if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)) || value.Any(char.IsUpper)) throw new ArgumentException($"{name} must be lowercase SHA-256.", name); }
    public static void RequireSafeLabel(string key, string value) { RequireOpaqueMetadata(value, 128, key); var lower = $"{key}:{value}".ToLowerInvariant(); if (lower.Contains('@') || PhoneLike.IsMatch(value) || new[] { "secret", "token", "password", "email", "phone", "prompt", "content", "cookie", "authorization" }.Any(lower.Contains)) throw new ArgumentException("Audit label may contain raw PII or secret material.", key); }
}
