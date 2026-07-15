using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.MemoryEventLedger.Contracts;

public sealed record InterestSignalV1(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("confidence")] decimal Confidence)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Topic) || Topic.Length > 128)
        {
            throw new ArgumentException("An interest topic must contain between 1 and 128 characters.", nameof(Topic));
        }

        if (Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Confidence), "Confidence must be finite and between zero and one.");
        }
    }
}

public sealed record MemoryObservationV1(
    [property: JsonPropertyName("content_digest")] string ContentDigest,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("interest_signals")] IReadOnlyList<InterestSignalV1> InterestSignals)
{
    public void Validate()
    {
        if (!Verified)
        {
            throw new InvalidOperationException("Unverified observations cannot enter the memory ledger.");
        }

        ContractValidation.RequireSha256(ContentDigest, nameof(ContentDigest));
        ArgumentNullException.ThrowIfNull(InterestSignals);

        foreach (var signal in InterestSignals)
        {
            ArgumentNullException.ThrowIfNull(signal);
            signal.Validate();
        }
    }
}

public sealed record MemoryEventV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(MemoryCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("observation")] MemoryObservationV1 Observation)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "memory.event/v1";
    public const string CurrentProducerModule = "memory-event-ledger";
    public const string ObservedContentEventType = "content.observed";

    public void Validate()
    {
        ContractValidation.RequireMajor(SchemaVersion, 1);
        ContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ContractValidation.RequireNonEmpty(EventId, nameof(EventId));
        ContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        ContractValidation.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        ContractValidation.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        ContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        ContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        ContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        ContractValidation.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        ContractValidation.RequireExact(EventType, ObservedContentEventType, nameof(EventType));
        ArgumentNullException.ThrowIfNull(Observation);
        Observation.Validate();
    }
}

public static class MemoryEventCanonicalizer
{
    public static string Serialize(MemoryEventV1 memoryEvent)
    {
        ArgumentNullException.ThrowIfNull(memoryEvent);
        memoryEvent.Validate();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", memoryEvent.SchemaVersion);
            writer.WriteString("contract_id", memoryEvent.ContractId);
            writer.WriteString("producer_module", memoryEvent.ProducerModule);
            writer.WriteString("event_id", memoryEvent.EventId);
            writer.WriteString("soul_id", memoryEvent.SoulId);
            writer.WriteString("device_binding_id", memoryEvent.DeviceBindingId);
            writer.WriteString("platform_account_id", memoryEvent.PlatformAccountId);
            writer.WriteString("trace_id", memoryEvent.TraceId);
            writer.WriteString("idempotency_key", memoryEvent.IdempotencyKey);
            writer.WriteString("occurred_at", MemoryCanonicalUtcJsonConverter.Format(memoryEvent.OccurredAt));
            writer.WriteString("privacy_class", memoryEvent.PrivacyClass);
            writer.WriteString("event_type", memoryEvent.EventType);
            writer.WritePropertyName("observation");
            writer.WriteStartObject();
            writer.WriteString("content_digest", memoryEvent.Observation.ContentDigest.ToLowerInvariant());
            writer.WriteBoolean("verified", memoryEvent.Observation.Verified);
            writer.WritePropertyName("interest_signals");
            writer.WriteStartArray();

            foreach (var signal in memoryEvent.Observation.InterestSignals
                         .OrderBy(static item => item.Topic, StringComparer.Ordinal)
                         .ThenBy(static item => item.Confidence))
            {
                writer.WriteStartObject();
                writer.WriteString("topic", signal.Topic);
                writer.WriteNumber("confidence", signal.Confidence);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ComputeSha256(MemoryEventV1 memoryEvent)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(memoryEvent))));
    }
}

public static class ContractValidation
{
    public static void RequireMajor(string schemaVersion, int expectedMajor)
    {
        RequireText(schemaVersion, 32, nameof(schemaVersion));
        var parts = schemaVersion.Split('.');
        if (parts.Length is < 1 or > 3 ||
            !string.Equals(parts[0], expectedMajor.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            parts.Any(static part => part.Length == 0 || part.Any(static character => character is < '0' or > '9')))
        {
            throw new NotSupportedException($"Unsupported schema major '{schemaVersion}'. Expected major {expectedMajor}.");
        }
    }

    public static void RequireExact(string actual, string expected, string parameterName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported {parameterName} '{actual}'. Expected '{expected}'.");
        }
    }

    public static void RequireText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"{parameterName} must contain between 1 and {maximumLength} characters.", parameterName);
        }
    }

    public static void RequireNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException($"{parameterName} cannot be empty.", parameterName);
        }
    }

    public static void RequireSoulId(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 69 || !value.StartsWith("soul_", StringComparison.Ordinal) ||
            value.AsSpan(5).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"{parameterName} must be an opaque soul_ identifier.", parameterName);
        }
    }

    public static void RequireOpaqueId(string value, string prefix, string parameterName)
    {
        RequirePrefixedLowerHex(value, prefix, 32, parameterName);
    }

    public static void RequireTraceId(string value, string parameterName) =>
        RequirePrefixedLowerHex(value, "trace_", 32, parameterName);

    public static void RequireIdempotencyKey(string value, string parameterName) =>
        RequirePrefixedLowerHex(value, "idem_", 64, parameterName);

    private static void RequirePrefixedLowerHex(string value, string prefix, int bodyLength, string parameterName)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + bodyLength ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException($"{parameterName} must be a canonical opaque identifier.", parameterName);
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{parameterName} must be UTC.", parameterName);
        }
    }

    public static void RequireSha256(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64 || value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException($"{parameterName} must be a 64-character SHA-256 digest.", parameterName);
        }
    }
}

public sealed class MemoryCanonicalUtcJsonConverter : JsonConverter<DateTimeOffset>
{
    private static readonly Regex CanonicalUtc = new(
        "\\A[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\\.[0-9]{0,6}[1-9])?Z\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is null || text.Length > 32 || !CanonicalUtc.IsMatch(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) || value.Offset != TimeSpan.Zero)
            throw new JsonException("occurred_at must be canonical UTC with at most seven fractional digits.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));

    internal static string Format(DateTimeOffset value)
    {
        ContractValidation.RequireUtc(value, nameof(value));
        var utc = value.ToUniversalTime();
        var prefix = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return fraction.Length == 0 ? prefix + "Z" : prefix + "." + fraction + "Z";
    }
}
