using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.GBrainProjector.Contracts;

public sealed record GBrainProjectionV2(
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
    [property: JsonPropertyName("source_binding_algorithm")] string SourceBindingAlgorithm,
    [property: JsonPropertyName("source_binding_nonce")] long SourceBindingNonce,
    [property: JsonPropertyName("source_binding_soul_hash")] string SourceBindingSoulHash,
    [property: JsonPropertyName("source_binding_allocated_at"), JsonConverter(typeof(ProjectionCanonicalUtcJsonConverter))] DateTimeOffset SourceBindingAllocatedAt,
    [property: JsonPropertyName("source_binding_revision")] string SourceBindingRevision,
    [property: JsonPropertyName("source_binding_checksum")] string SourceBindingChecksum,
    [property: JsonPropertyName("projection_revision")] string ProjectionRevision,
    [property: JsonPropertyName("projection_checksum")] string ProjectionChecksum,
    [property: JsonPropertyName("render_status")] string RenderStatus,
    [property: JsonPropertyName("source_event_count")] int SourceEventCount,
    [property: JsonPropertyName("events")] IReadOnlyList<ProjectionEventV1> Events,
    [property: JsonPropertyName("interests")] IReadOnlyList<ProjectionInterestV1> Interests)
{
    public const string CurrentSchemaVersion = "2.0.0";
    public const string CurrentContractId = "gbrain.projection/v2";
    public const string CurrentProducerModule = "gbrain-projector";
    public const string RenderedNotWrittenStatus = "dto-rendered-not-written";

    [JsonIgnore]
    public GBrainSourceBindingV1 SourceBinding => new(
        GBrainSourceBindingV1.CurrentSchemaVersion,
        GBrainSourceBindingV1.CurrentContractId,
        GBrainSourceBindingV1.CurrentProducerModule,
        SoulId,
        null,
        null,
        null,
        null,
        SourceBindingAllocatedAt,
        GBrainSourceBindingV1.CurrentPrivacyClass,
        SourceId,
        SourceBindingAlgorithm,
        SourceBindingNonce,
        SourceBindingSoulHash,
        SourceBindingAllocatedAt,
        SourceBindingRevision,
        SourceBindingChecksum);

    public void Validate()
    {
        ValidateContent();
        ProjectionContractValidation.RequireSha256(ProjectionChecksum, nameof(ProjectionChecksum));
        var expected = GBrainProjectionV2Canonicalizer.ComputeSha256(this);
        if (!SourceBindingContractValidation.FixedTimeEquals(ProjectionChecksum, expected))
        {
            throw new InvalidOperationException("Projection checksum does not match canonical v2 DTO content.");
        }
    }

    internal void ValidateContent()
    {
        ProjectionContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        ProjectionContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ProjectionContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ProjectionContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        ProjectionContractValidation.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        ProjectionContractValidation.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        ProjectionContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        ProjectionContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        ProjectionContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        ProjectionContractValidation.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        SourceBinding.Validate();
        ProjectionContractValidation.RequireSha256(ProjectionRevision, nameof(ProjectionRevision));
        ProjectionContractValidation.RequireExact(RenderStatus, RenderedNotWrittenStatus, nameof(RenderStatus));
        ArgumentNullException.ThrowIfNull(Events);
        ArgumentNullException.ThrowIfNull(Interests);
        if (SourceEventCount < 0 || SourceEventCount != Events.Count)
        {
            throw new ArgumentException("Source event count must equal the unique projected event count.", nameof(SourceEventCount));
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
                throw new ArgumentException("Projected interest topics must be ordinal-unique.", nameof(Interests));
            }

            foreach (var evidence in interest.Evidence)
            {
                if (!eventIds.Contains(evidence.EventId))
                {
                    throw new ArgumentException("Projected interest evidence must identify a projected event.", nameof(Interests));
                }
            }
        }
    }
}

public static class GBrainProjectionV2Canonicalizer
{
    public static string Serialize(GBrainProjectionV2 projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();
        return Write(projection, includeChecksum: true);
    }

    public static string ComputeSha256(GBrainProjectionV2 projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.ValidateContent();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(projection, includeChecksum: false))));
    }

    private static string Write(GBrainProjectionV2 projection, bool includeChecksum)
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
            writer.WriteString("occurred_at", ProjectionCanonicalUtcJsonConverter.Format(projection.OccurredAt));
            writer.WriteString("privacy_class", projection.PrivacyClass);
            writer.WriteString("source_id", projection.SourceId);
            writer.WriteString("source_binding_algorithm", projection.SourceBindingAlgorithm);
            writer.WriteNumber("source_binding_nonce", projection.SourceBindingNonce);
            writer.WriteString("source_binding_soul_hash", projection.SourceBindingSoulHash);
            writer.WriteString("source_binding_allocated_at", ProjectionCanonicalUtcJsonConverter.Format(projection.SourceBindingAllocatedAt));
            writer.WriteString("source_binding_revision", projection.SourceBindingRevision);
            writer.WriteString("source_binding_checksum", projection.SourceBindingChecksum);
            writer.WriteString("projection_revision", projection.ProjectionRevision);
            if (includeChecksum)
            {
                writer.WriteString("projection_checksum", projection.ProjectionChecksum);
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
                writer.WriteString("occurred_at", ProjectionCanonicalUtcJsonConverter.Format(item.OccurredAt));
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
                    writer.WriteString("occurred_at", ProjectionCanonicalUtcJsonConverter.Format(evidence.OccurredAt));
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
}
