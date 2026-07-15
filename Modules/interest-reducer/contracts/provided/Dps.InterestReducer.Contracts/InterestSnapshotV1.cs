using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.InterestReducer.Contracts;

public sealed record InterestEvidenceV1(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("event_hash")] string EventHash,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(InterestCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("original_confidence")] decimal OriginalConfidence,
    [property: JsonPropertyName("decayed_confidence")] decimal DecayedConfidence)
{
    public void Validate(DateTimeOffset asOf)
    {
        InterestContractValidation.RequireNonEmpty(EventId, nameof(EventId));
        InterestContractValidation.RequireSha256(EventHash, nameof(EventHash));
        InterestContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        InterestContractValidation.RequireConfidence(OriginalConfidence, nameof(OriginalConfidence));
        InterestContractValidation.RequireConfidence(DecayedConfidence, nameof(DecayedConfidence));

        if (OccurredAt > asOf)
        {
            throw new ArgumentOutOfRangeException(nameof(OccurredAt), "Evidence cannot occur after the snapshot as_of time.");
        }

        if (DecayedConfidence > OriginalConfidence)
        {
            throw new ArgumentException("Decayed confidence cannot exceed original confidence.", nameof(DecayedConfidence));
        }
    }
}

public sealed record InterestValueV1(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("original_confidence")] decimal OriginalConfidence,
    [property: JsonPropertyName("decayed_confidence")] decimal DecayedConfidence,
    [property: JsonPropertyName("half_life_seconds")] decimal HalfLifeSeconds,
    [property: JsonPropertyName("algorithm_version")] string AlgorithmVersion,
    [property: JsonPropertyName("evidence")] IReadOnlyList<InterestEvidenceV1> Evidence)
{
    public void Validate(DateTimeOffset asOf)
    {
        InterestContractValidation.RequireText(Topic, 128, nameof(Topic));
        InterestContractValidation.RequireConfidence(OriginalConfidence, nameof(OriginalConfidence));
        InterestContractValidation.RequireConfidence(DecayedConfidence, nameof(DecayedConfidence));
        InterestContractValidation.RequirePositive(HalfLifeSeconds, nameof(HalfLifeSeconds));
        InterestContractValidation.RequireExact(
            AlgorithmVersion,
            InterestSnapshotV1.CurrentAlgorithmVersion,
            nameof(AlgorithmVersion));
        ArgumentNullException.ThrowIfNull(Evidence);

        if (Evidence.Count == 0)
        {
            throw new ArgumentException("An interest value must cite at least one event.", nameof(Evidence));
        }

        var eventIds = new HashSet<Guid>();
        foreach (var item in Evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            item.Validate(asOf);

            if (!eventIds.Add(item.EventId))
            {
                throw new ArgumentException("Evidence event identifiers must be unique within an interest value.", nameof(Evidence));
            }
        }

        if (DecayedConfidence > OriginalConfidence)
        {
            throw new ArgumentException("Decayed confidence cannot exceed original confidence.", nameof(DecayedConfidence));
        }
    }
}

public sealed record InterestSnapshotV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(InterestCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("as_of"), JsonConverter(typeof(InterestCanonicalUtcJsonConverter))] DateTimeOffset AsOf,
    [property: JsonPropertyName("algorithm_version")] string AlgorithmVersion,
    [property: JsonPropertyName("half_life_seconds")] decimal HalfLifeSeconds,
    [property: JsonPropertyName("source_event_count")] int SourceEventCount,
    [property: JsonPropertyName("interests")] IReadOnlyList<InterestValueV1> Interests)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "interest.snapshot/v1";
    public const string CurrentProducerModule = "interest-reducer";
    public const string CurrentAlgorithmVersion = "exponential-half-life/v1";

    public void Validate()
    {
        InterestContractValidation.RequireMajor(SchemaVersion, 1);
        InterestContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        InterestContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        InterestContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        InterestContractValidation.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        InterestContractValidation.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        InterestContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        InterestContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        InterestContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        InterestContractValidation.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        InterestContractValidation.RequireUtc(AsOf, nameof(AsOf));
        InterestContractValidation.RequireExact(AlgorithmVersion, CurrentAlgorithmVersion, nameof(AlgorithmVersion));
        InterestContractValidation.RequirePositive(HalfLifeSeconds, nameof(HalfLifeSeconds));
        ArgumentNullException.ThrowIfNull(Interests);

        if (OccurredAt != AsOf)
        {
            throw new ArgumentException("Snapshot occurred_at must equal the explicit as_of time.", nameof(OccurredAt));
        }

        if (SourceEventCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceEventCount), "Source event count cannot be negative.");
        }

        var topics = new HashSet<string>(StringComparer.Ordinal);
        var eventEvidence = new Dictionary<Guid, (string Hash, DateTimeOffset OccurredAt)>();
        foreach (var interest in Interests)
        {
            ArgumentNullException.ThrowIfNull(interest);
            interest.Validate(AsOf);

            if (!topics.Add(interest.Topic))
            {
                throw new ArgumentException("Interest topics must be unique using ordinal comparison.", nameof(Interests));
            }

            if (interest.AlgorithmVersion != AlgorithmVersion || interest.HalfLifeSeconds != HalfLifeSeconds)
            {
                throw new ArgumentException("Every interest value must use the snapshot decay basis.", nameof(Interests));
            }

            foreach (var evidence in interest.Evidence)
            {
                if (eventEvidence.TryGetValue(evidence.EventId, out var existing) &&
                    (!string.Equals(existing.Hash, evidence.EventHash, StringComparison.OrdinalIgnoreCase) ||
                     existing.OccurredAt != evidence.OccurredAt))
                {
                    throw new ArgumentException(
                        "The same event_id cannot cite conflicting hash or occurrence data across interests.",
                        nameof(Interests));
                }

                eventEvidence[evidence.EventId] = (evidence.EventHash, evidence.OccurredAt);
            }
        }

        if (SourceEventCount < eventEvidence.Count)
        {
            throw new ArgumentException(
                "Source event count cannot be smaller than the number of cited events.",
                nameof(SourceEventCount));
        }
    }
}

public static class InterestSnapshotCanonicalizer
{
    public static string Serialize(InterestSnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", snapshot.SchemaVersion);
            writer.WriteString("contract_id", snapshot.ContractId);
            writer.WriteString("producer_module", snapshot.ProducerModule);
            writer.WriteString("soul_id", snapshot.SoulId);
            writer.WriteString("device_binding_id", snapshot.DeviceBindingId);
            writer.WriteString("platform_account_id", snapshot.PlatformAccountId);
            writer.WriteString("trace_id", snapshot.TraceId);
            writer.WriteString("idempotency_key", snapshot.IdempotencyKey);
            writer.WriteString("occurred_at", FormatUtc(snapshot.OccurredAt));
            writer.WriteString("privacy_class", snapshot.PrivacyClass);
            writer.WriteString("as_of", FormatUtc(snapshot.AsOf));
            writer.WriteString("algorithm_version", snapshot.AlgorithmVersion);
            writer.WriteNumber("half_life_seconds", snapshot.HalfLifeSeconds);
            writer.WriteNumber("source_event_count", snapshot.SourceEventCount);
            writer.WritePropertyName("interests");
            writer.WriteStartArray();

            foreach (var interest in snapshot.Interests.OrderBy(static item => item.Topic, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("topic", interest.Topic);
                writer.WriteNumber("original_confidence", interest.OriginalConfidence);
                writer.WriteNumber("decayed_confidence", interest.DecayedConfidence);
                writer.WriteNumber("half_life_seconds", interest.HalfLifeSeconds);
                writer.WriteString("algorithm_version", interest.AlgorithmVersion);
                writer.WritePropertyName("evidence");
                writer.WriteStartArray();

                foreach (var evidence in interest.Evidence
                             .OrderBy(static item => item.OccurredAt)
                             .ThenBy(static item => item.EventId)
                             .ThenBy(static item => item.EventHash, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("event_id", evidence.EventId);
                    writer.WriteString("event_hash", evidence.EventHash.ToLowerInvariant());
                    writer.WriteString("occurred_at", FormatUtc(evidence.OccurredAt));
                    writer.WriteNumber("original_confidence", evidence.OriginalConfidence);
                    writer.WriteNumber("decayed_confidence", evidence.DecayedConfidence);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ComputeSha256(InterestSnapshotV1 snapshot)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(snapshot))));
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return InterestCanonicalUtcJsonConverter.Format(value);
    }
}

public static class InterestContractValidation
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
        if (value is null || value.Length != 69 || !value.StartsWith("soul_", StringComparison.Ordinal) ||
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

    public static void RequireConfidence(decimal value, string parameterName)
    {
        if (value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Confidence must be finite and between zero and one.");
        }
    }

    public static void RequirePositive(decimal value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and greater than zero.");
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

public sealed class InterestCanonicalUtcJsonConverter : JsonConverter<DateTimeOffset>
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
            throw new JsonException("Timestamp must be canonical UTC with at most seven fractional digits.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(Format(value));

    internal static string Format(DateTimeOffset value)
    {
        InterestContractValidation.RequireUtc(value, nameof(value));
        var utc = value.ToUniversalTime();
        var prefix = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return fraction.Length == 0 ? prefix + "Z" : prefix + "." + fraction + "Z";
    }
}
