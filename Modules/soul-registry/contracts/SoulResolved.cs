using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.SoulRegistry.Contracts;

/// <summary>
/// The pseudonymous, immutable identity context produced after a verified alias
/// has been resolved. Raw aliases are deliberately absent from this contract.
/// </summary>
public sealed record SoulResolved
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "soul.resolved/v1";
    public const string CurrentProducerModule = "soul-registry";
    public const string CurrentPrivacyClass = "pseudonymous";

    public SoulResolved(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string aliasKind,
        string aliasDigest,
        string aliasKeyId)
    {
        SoulId = soulId;
        DeviceBindingId = deviceBindingId;
        PlatformAccountId = platformAccountId;
        TraceId = traceId;
        IdempotencyKey = idempotencyKey;
        OccurredAt = occurredAt;
        AliasKind = aliasKind;
        AliasDigest = aliasDigest;
        AliasKeyId = aliasKeyId;
    }

    [JsonPropertyName("schema_version")]
    [JsonPropertyOrder(0)]
    public string SchemaVersion => CurrentSchemaVersion;

    [JsonPropertyName("contract_id")]
    [JsonPropertyOrder(1)]
    public string ContractId => CurrentContractId;

    [JsonPropertyName("producer_module")]
    [JsonPropertyOrder(2)]
    public string ProducerModule => CurrentProducerModule;

    [JsonPropertyName("soul_id")]
    [JsonPropertyOrder(3)]
    public string SoulId { get; }

    [JsonPropertyName("device_binding_id")]
    [JsonPropertyOrder(4)]
    public string DeviceBindingId { get; }

    [JsonPropertyName("platform_account_id")]
    [JsonPropertyOrder(5)]
    public string PlatformAccountId { get; }

    [JsonPropertyName("trace_id")]
    [JsonPropertyOrder(6)]
    public string TraceId { get; }

    [JsonPropertyName("idempotency_key")]
    [JsonPropertyOrder(7)]
    public string IdempotencyKey { get; }

    [JsonPropertyName("occurred_at")]
    [JsonPropertyOrder(8)]
    public DateTimeOffset OccurredAt { get; }

    [JsonPropertyName("privacy_class")]
    [JsonPropertyOrder(9)]
    public string PrivacyClass => CurrentPrivacyClass;

    [JsonPropertyName("alias_kind")]
    [JsonPropertyOrder(10)]
    public string AliasKind { get; }

    [JsonPropertyName("alias_digest")]
    [JsonPropertyOrder(11)]
    public string AliasDigest { get; }

    [JsonPropertyName("alias_key_id")]
    [JsonPropertyOrder(12)]
    public string AliasKeyId { get; }

    public void Validate()
    {
        SoulResolvedValidation.RequireSupportedMajor(SchemaVersion, 1);
        SoulResolvedValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        SoulResolvedValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        SoulResolvedValidation.RequireExact(PrivacyClass, CurrentPrivacyClass, nameof(PrivacyClass));
        SoulResolvedValidation.RequirePattern(SoulId, "^soul_[a-f0-9]{64}\\z", nameof(SoulId));
        SoulResolvedValidation.RequirePattern(DeviceBindingId, "^db_[a-f0-9]{32}\\z", nameof(DeviceBindingId));
        SoulResolvedValidation.RequirePattern(PlatformAccountId, "^pa_[a-f0-9]{32}\\z", nameof(PlatformAccountId));
        SoulResolvedValidation.RequirePattern(TraceId, "^trace_[a-f0-9]{32}\\z", nameof(TraceId));
        SoulResolvedValidation.RequirePattern(IdempotencyKey, "^idem_[a-f0-9]{64}\\z", nameof(IdempotencyKey));

        if (OccurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("OccurredAt must use UTC.", nameof(OccurredAt));
        }

        if (AliasKind is not ("email" or "phone" or "platform_id"))
        {
            throw new ArgumentException("AliasKind is not supported.", nameof(AliasKind));
        }

        SoulResolvedValidation.RequirePattern(AliasDigest, "^[a-f0-9]{64}\\z", nameof(AliasDigest));
        SoulResolvedValidation.RequirePattern(AliasKeyId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", nameof(AliasKeyId));
    }
}

public static class SoulResolvedValidation
{
    public static void RequireSupportedMajor(string schemaVersion, int supportedMajor)
    {
        if (!Version.TryParse(schemaVersion, out var parsed) || parsed.Major != supportedMajor)
        {
            throw new NotSupportedException("Unknown soul.resolved contract major was rejected.");
        }
    }

    internal static void RequireExact(string? value, string expected, string parameterName)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException("Contract metadata is invalid.", parameterName);
        }
    }

    internal static void RequireText(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException("A required contract field is invalid.", parameterName);
        }
    }

    internal static void RequirePattern(string? value, string pattern, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("A required contract identifier is invalid.", parameterName);
        }
    }
}
