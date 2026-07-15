using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.GBrainProjector.Contracts;

public sealed record ProjectionEventV1(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("event_hash")] string EventHash,
    [property: JsonPropertyName("content_digest")] string ContentDigest,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(ProjectionCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt)
{
    public void Validate()
    {
        ProjectionContractValidation.RequireNonEmpty(EventId, nameof(EventId));
        ProjectionContractValidation.RequireSha256(EventHash, nameof(EventHash));
        ProjectionContractValidation.RequireSha256(ContentDigest, nameof(ContentDigest));
        ProjectionContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
    }
}

public sealed record ProjectionInterestEvidenceV1(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("event_hash")] string EventHash,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(ProjectionCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("original_confidence")] decimal OriginalConfidence,
    [property: JsonPropertyName("decayed_confidence")] decimal DecayedConfidence)
{
    public void Validate(DateTimeOffset asOf)
    {
        ProjectionContractValidation.RequireNonEmpty(EventId, nameof(EventId));
        ProjectionContractValidation.RequireSha256(EventHash, nameof(EventHash));
        ProjectionContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        ProjectionContractValidation.RequireConfidence(OriginalConfidence, nameof(OriginalConfidence));
        ProjectionContractValidation.RequireConfidence(DecayedConfidence, nameof(DecayedConfidence));

        if (OccurredAt > asOf)
        {
            throw new ArgumentOutOfRangeException(nameof(OccurredAt), "Interest evidence cannot occur after projection occurred_at.");
        }

        if (DecayedConfidence > OriginalConfidence)
        {
            throw new ArgumentException("Decayed confidence cannot exceed original confidence.", nameof(DecayedConfidence));
        }
    }
}

public sealed record ProjectionInterestV1(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("original_confidence")] decimal OriginalConfidence,
    [property: JsonPropertyName("decayed_confidence")] decimal DecayedConfidence,
    [property: JsonPropertyName("half_life_seconds")] decimal HalfLifeSeconds,
    [property: JsonPropertyName("algorithm_version")] string AlgorithmVersion,
    [property: JsonPropertyName("evidence")] IReadOnlyList<ProjectionInterestEvidenceV1> Evidence)
{
    public void Validate(DateTimeOffset asOf)
    {
        ProjectionContractValidation.RequireText(Topic, 128, nameof(Topic));
        ProjectionContractValidation.RequireConfidence(OriginalConfidence, nameof(OriginalConfidence));
        ProjectionContractValidation.RequireConfidence(DecayedConfidence, nameof(DecayedConfidence));
        ProjectionContractValidation.RequirePositive(HalfLifeSeconds, nameof(HalfLifeSeconds));
        ProjectionContractValidation.RequireText(AlgorithmVersion, 64, nameof(AlgorithmVersion));
        ArgumentNullException.ThrowIfNull(Evidence);

        if (Evidence.Count == 0)
        {
            throw new ArgumentException("A projected interest must retain at least one evidence reference.", nameof(Evidence));
        }

        var eventIds = new HashSet<Guid>();
        foreach (var item in Evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            item.Validate(asOf);
            if (!eventIds.Add(item.EventId))
            {
                throw new ArgumentException("Projected interest evidence event identifiers must be unique.", nameof(Evidence));
            }
        }

        if (DecayedConfidence > OriginalConfidence)
        {
            throw new ArgumentException("Decayed confidence cannot exceed original confidence.", nameof(DecayedConfidence));
        }
    }
}

public sealed record GBrainProjectionV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(ProjectionCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("projection_revision")] string ProjectionRevision,
    [property: JsonPropertyName("projection_checksum")] string ProjectionChecksum,
    [property: JsonPropertyName("render_status")] string RenderStatus,
    [property: JsonPropertyName("source_event_count")] int SourceEventCount,
    [property: JsonPropertyName("events")] IReadOnlyList<ProjectionEventV1> Events,
    [property: JsonPropertyName("interests")] IReadOnlyList<ProjectionInterestV1> Interests)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "gbrain.projection/v1";
    public const string CurrentProducerModule = "gbrain-projector";
    public const string RenderedNotWrittenStatus = "dto-rendered-not-written";

    public void Validate()
    {
        ValidateContent();
        ProjectionContractValidation.RequireSha256(ProjectionChecksum, nameof(ProjectionChecksum));
        var expectedChecksum = GBrainProjectionCanonicalizer.ComputeSha256(this);
        if (!string.Equals(ProjectionChecksum, expectedChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Projection checksum does not match its canonical DTO content.");
        }
    }

    internal void ValidateContent()
    {
        ProjectionContractValidation.RequireMajor(SchemaVersion, 1);
        ProjectionContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ProjectionContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ProjectionContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        ProjectionContractValidation.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        ProjectionContractValidation.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        ProjectionContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        ProjectionContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        ProjectionContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        ProjectionContractValidation.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        ProjectionContractValidation.RequireExact(SourceId, GBrainSourceIds.ForSoul(SoulId), nameof(SourceId));
        ProjectionContractValidation.RequireSha256(ProjectionRevision, nameof(ProjectionRevision));
        ProjectionContractValidation.RequireExact(RenderStatus, RenderedNotWrittenStatus, nameof(RenderStatus));
        ArgumentNullException.ThrowIfNull(Events);
        ArgumentNullException.ThrowIfNull(Interests);

        if (SourceEventCount < 0 || SourceEventCount != Events.Count)
        {
            throw new ArgumentException("Source event count must equal the number of unique projected events.", nameof(SourceEventCount));
        }

        var eventIds = new HashSet<Guid>();
        foreach (var item in Events)
        {
            ArgumentNullException.ThrowIfNull(item);
            item.Validate();
            if (!eventIds.Add(item.EventId))
            {
                throw new ArgumentException("Projected event identifiers must be unique.", nameof(Events));
            }
        }

        var topics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var interest in Interests)
        {
            ArgumentNullException.ThrowIfNull(interest);
            interest.Validate(OccurredAt);
            if (!topics.Add(interest.Topic))
            {
                throw new ArgumentException("Projected interest topics must be unique using ordinal comparison.", nameof(Interests));
            }

            foreach (var evidence in interest.Evidence)
            {
                if (!eventIds.Contains(evidence.EventId))
                {
                    throw new ArgumentException("Every projected interest evidence reference must identify a projected event.", nameof(Interests));
                }
            }
        }
    }
}

public static class GBrainSourceIds
{
    public const int TruncatedSoulHexLength = 28;

    public static string ForSoul(string soulId)
    {
        ProjectionContractValidation.RequireSoulId(soulId, nameof(soulId));
        return "dps-" + soulId.AsSpan(5, TruncatedSoulHexLength).ToString();
    }
}

public static class GBrainProjectionCanonicalizer
{
    public static string Serialize(GBrainProjectionV1 projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();
        return Write(projection, includeChecksum: true);
    }

    public static string ComputeSha256(GBrainProjectionV1 projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.ValidateContent();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(projection, includeChecksum: false))));
    }

    private static string Write(GBrainProjectionV1 projection, bool includeChecksum)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", projection.SchemaVersion);
            writer.WriteString("contract_id", projection.ContractId);
            writer.WriteString("producer_module", projection.ProducerModule);
            writer.WriteString("soul_id", projection.SoulId);
            writer.WriteString("device_binding_id", projection.DeviceBindingId);
            writer.WriteString("platform_account_id", projection.PlatformAccountId);
            writer.WriteString("trace_id", projection.TraceId);
            writer.WriteString("idempotency_key", projection.IdempotencyKey);
            writer.WriteString("occurred_at", FormatUtc(projection.OccurredAt));
            writer.WriteString("privacy_class", projection.PrivacyClass);
            writer.WriteString("source_id", projection.SourceId);
            writer.WriteString("projection_revision", projection.ProjectionRevision.ToLowerInvariant());
            if (includeChecksum)
            {
                writer.WriteString("projection_checksum", projection.ProjectionChecksum.ToLowerInvariant());
            }

            writer.WriteString("render_status", projection.RenderStatus);
            writer.WriteNumber("source_event_count", projection.SourceEventCount);
            writer.WritePropertyName("events");
            writer.WriteStartArray();
            foreach (var item in projection.Events
                         .OrderBy(static value => value.OccurredAt)
                         .ThenBy(static value => value.EventId)
                         .ThenBy(static value => value.EventHash, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("event_id", item.EventId);
                writer.WriteString("event_hash", item.EventHash.ToLowerInvariant());
                writer.WriteString("content_digest", item.ContentDigest.ToLowerInvariant());
                writer.WriteString("occurred_at", FormatUtc(item.OccurredAt));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("interests");
            writer.WriteStartArray();
            foreach (var interest in projection.Interests.OrderBy(static value => value.Topic, StringComparer.Ordinal))
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
                             .OrderBy(static value => value.OccurredAt)
                             .ThenBy(static value => value.EventId)
                             .ThenBy(static value => value.EventHash, StringComparer.Ordinal))
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

    private static string FormatUtc(DateTimeOffset value)
    {
        return ProjectionCanonicalUtcJsonConverter.Format(value);
    }
}

public static class ProjectionContractValidation
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

    public static void RequireSha256(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64 || value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException($"{parameterName} must be a 64-character SHA-256 digest.", parameterName);
        }
    }

    public static void RequireConfidence(decimal value, string parameterName)
    {
        if (value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Confidence must be between zero and one.");
        }
    }

    public static void RequirePositive(decimal value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be greater than zero.");
        }
    }
}

public sealed class ProjectionCanonicalUtcJsonConverter : JsonConverter<DateTimeOffset>
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
        ProjectionContractValidation.RequireUtc(value, nameof(value));
        var utc = value.ToUniversalTime();
        var prefix = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return fraction.Length == 0 ? prefix + "Z" : prefix + "." + fraction + "Z";
    }
}
