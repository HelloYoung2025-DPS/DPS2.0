using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.PersonaStore.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonaHistoryExportItemV1(
    [property: JsonPropertyName("revision"), JsonRequired] PersonaRevisionV1 Revision,
    [property: JsonPropertyName("live_primary_payload_state"), JsonRequired] string LivePrimaryPayloadState,
    [property: JsonPropertyName("traits"), JsonRequired] IReadOnlyDictionary<string, string>? Traits)
{
    public const string Retained = "retained";
    public const string LivePrimaryLogicallyDeleted = "live-primary-logically-deleted";

    public PersonaHistoryExportItemV1 ImmutableCopy() => new(
        Revision.ImmutableCopy(),
        LivePrimaryPayloadState,
        Traits is null ? null : PersonaTraitVocabularyV1.ValidateAndFreeze(Traits));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonaHistoryExportV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("live_primary_payload_state"), JsonRequired] string LivePrimaryPayloadState,
    [property: JsonPropertyName("snapshot_persona_revision"), JsonRequired] long SnapshotPersonaRevision,
    [property: JsonPropertyName("snapshot_cursor_hmac_sha256"), JsonRequired] string SnapshotCursorHmacSha256,
    [property: JsonPropertyName("export_request_hmac_sha256"), JsonRequired] string ExportRequestHmacSha256,
    [property: JsonPropertyName("export_payload_sha256"), JsonRequired] string ExportPayloadSha256,
    [property: JsonPropertyName("export_receipt_hmac_sha256"), JsonRequired] string ExportReceiptHmacSha256,
    [property: JsonPropertyName("export_receipt_id"), JsonRequired] string ExportReceiptId,
    [property: JsonPropertyName("revisions"), JsonRequired] IReadOnlyList<PersonaHistoryExportItemV1> Revisions)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "persona.history.export/v1";
    public const string CurrentProducerModule = "persona-store";

    public void Validate()
    {
        PersonaContractValidation.RequireMajor(SchemaVersion, 1);
        PersonaContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        PersonaContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        PersonaContractValidation.RequireSoulId(SoulId);
        PersonaContractValidation.RequireDeviceBindingId(DeviceBindingId);
        PersonaContractValidation.RequirePlatformAccountId(PlatformAccountId);
        PersonaContractValidation.RequireTraceId(TraceId);
        PersonaContractValidation.RequireIdempotencyKey(IdempotencyKey);
        PersonaContractValidation.RequireOccurredAt(OccurredAt, nameof(OccurredAt));
        PersonaContractValidation.RequireExact(PrivacyClass, "sensitive", nameof(PrivacyClass));
        if (LivePrimaryPayloadState is not (PersonaHistoryExportItemV1.Retained or PersonaHistoryExportItemV1.LivePrimaryLogicallyDeleted))
            throw new ArgumentOutOfRangeException(nameof(LivePrimaryPayloadState));
        if (SnapshotPersonaRevision < 1) throw new ArgumentOutOfRangeException(nameof(SnapshotPersonaRevision));
        PersonaContractValidation.RequireSha256(SnapshotCursorHmacSha256, nameof(SnapshotCursorHmacSha256));
        PersonaContractValidation.RequireSha256(ExportRequestHmacSha256, nameof(ExportRequestHmacSha256));
        PersonaContractValidation.RequireSha256(ExportPayloadSha256, nameof(ExportPayloadSha256));
        PersonaContractValidation.RequireSha256(ExportReceiptHmacSha256, nameof(ExportReceiptHmacSha256));
        PersonaContractValidation.RequireExportReceiptId(ExportReceiptId);
        if (!string.Equals(ExportReceiptId, "pexport_" + ExportReceiptHmacSha256, StringComparison.Ordinal))
            throw new ArgumentException("The export receipt ID must bind the exact receipt HMAC.", nameof(ExportReceiptId));
        if (Revisions is null || Revisions.Count is < 1 or > 10_000)
            throw new ArgumentException("Between one and 10,000 persona history revisions are required.", nameof(Revisions));

        for (var index = 0; index < Revisions.Count; index++)
        {
            var item = Revisions[index] ?? throw new ArgumentException("Persona history items cannot be null.", nameof(Revisions));
            item.Revision.Validate();
            if (item.Revision.SoulId != SoulId || item.Revision.DeviceBindingId != DeviceBindingId ||
                item.Revision.PlatformAccountId != PlatformAccountId)
                throw new UnauthorizedAccessException("Persona history item scope does not match the export envelope.");
            if (item.Revision.PersonaRevision != index + 1L)
                throw new ArgumentException("Persona history revisions must be contiguous and begin at one.", nameof(Revisions));
            if (item.LivePrimaryPayloadState != LivePrimaryPayloadState)
                throw new ArgumentException("Persona history payload states must match the export envelope.", nameof(Revisions));

            if (LivePrimaryPayloadState == PersonaHistoryExportItemV1.Retained)
            {
                if (item.Revision.Status != "active" || item.Traits is null)
                    throw new ArgumentException("Retained history requires an active revision and raw traits.", nameof(Revisions));
                var traits = PersonaTraitVocabularyV1.ValidateAndFreeze(item.Traits);
                if (!traits.Keys.SequenceEqual(item.Revision.TraitKeys, StringComparer.Ordinal))
                    throw new ArgumentException("Retained history trait keys do not match the revision.", nameof(Revisions));
            }
            else if (item.Traits is not null)
            {
                throw new ArgumentException("A live-primary logically deleted export cannot contain raw traits.", nameof(Revisions));
            }

            if (LivePrimaryPayloadState == PersonaHistoryExportItemV1.LivePrimaryLogicallyDeleted &&
                item.Revision.Status != (index == Revisions.Count - 1 ? "deleted" : "active"))
                throw new ArgumentException("A logically deleted history requires active predecessors and exactly one final deleted revision.", nameof(Revisions));
        }

        var current = Revisions[^1].Revision;
        if (SnapshotPersonaRevision != current.PersonaRevision)
            throw new ArgumentException("The export snapshot tail must equal the final persona revision.", nameof(SnapshotPersonaRevision));
        if ((LivePrimaryPayloadState == PersonaHistoryExportItemV1.Retained && current.Status != "active") ||
            (LivePrimaryPayloadState == PersonaHistoryExportItemV1.LivePrimaryLogicallyDeleted && current.Status != "deleted"))
            throw new ArgumentException("The export payload state must match the current persona revision.", nameof(Revisions));

        var computedPayloadSha256 = PersonaHistoryExportIntegrity.ComputePayloadSha256(this);
        if (!PersonaHistoryExportIntegrity.FixedTimeSha256Equals(computedPayloadSha256, ExportPayloadSha256))
            throw new InvalidDataException("The export payload checksum does not match its canonical snapshot.");
    }

    public PersonaHistoryExportV1 ImmutableCopy()
    {
        Validate();
        var copy = this with
        {
            Revisions = Array.AsReadOnly(Revisions.Select(static item => item.ImmutableCopy()).ToArray())
        };
        copy.Validate();
        ValidateWireByteCount(JsonSerializer.SerializeToUtf8Bytes(copy).Length);
        return copy;
    }

    public static void ValidateWireByteCount(int byteCount)
    {
        if (byteCount < 0 || byteCount > 16 * 1024 * 1024)
            throw new ArgumentException("Persona history export exceeds the v1 16-MiB wire ceiling.", nameof(byteCount));
    }
}

public static class PersonaHistoryExportIntegrity
{
    private const string PayloadDomain = "dps.persona-store.history-export-payload-sha256/v1";

    public static string ComputePayloadSha256(PersonaHistoryExportV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("domain", PayloadDomain);
            writer.WriteString("schema_version", value.SchemaVersion);
            writer.WriteString("contract_id", value.ContractId);
            writer.WriteString("producer_module", value.ProducerModule);
            writer.WriteString("soul_id", value.SoulId);
            writer.WriteString("device_binding_id", value.DeviceBindingId);
            writer.WriteString("platform_account_id", value.PlatformAccountId);
            writer.WriteString("trace_id", value.TraceId);
            writer.WriteString("idempotency_key", value.IdempotencyKey);
            writer.WriteString("occurred_at", value.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("privacy_class", value.PrivacyClass);
            writer.WriteString("live_primary_payload_state", value.LivePrimaryPayloadState);
            writer.WriteNumber("snapshot_persona_revision", value.SnapshotPersonaRevision);
            writer.WriteStartArray("revisions");
            foreach (var item in value.Revisions)
            {
                writer.WriteStartObject();
                WriteRevision(writer, item.Revision);
                writer.WriteString("live_primary_payload_state", item.LivePrimaryPayloadState);
                writer.WritePropertyName("traits");
                if (item.Traits is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartObject();
                    foreach (var trait in item.Traits.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                        writer.WriteString(trait.Key, trait.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    public static bool FixedTimeSha256Equals(string left, string right)
    {
        if (!TryDecodeSha256(left, out var leftBytes) || !TryDecodeSha256(right, out var rightBytes))
            return false;
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool TryDecodeSha256(string value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != 64 || value.AsSpan().ContainsAnyExcept("0123456789abcdef")) return false;
        bytes = Convert.FromHexString(value);
        return true;
    }

    private static void WriteRevision(Utf8JsonWriter writer, PersonaRevisionV1 revision)
    {
        writer.WritePropertyName("revision");
        writer.WriteStartObject();
        writer.WriteString("schema_version", revision.SchemaVersion);
        writer.WriteString("contract_id", revision.ContractId);
        writer.WriteString("producer_module", revision.ProducerModule);
        writer.WriteString("soul_id", revision.SoulId);
        writer.WriteString("device_binding_id", revision.DeviceBindingId);
        writer.WriteString("platform_account_id", revision.PlatformAccountId);
        writer.WriteString("trace_id", revision.TraceId);
        writer.WriteString("idempotency_key", revision.IdempotencyKey);
        writer.WriteString("occurred_at", revision.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("privacy_class", revision.PrivacyClass);
        writer.WriteNumber("persona_revision", revision.PersonaRevision);
        writer.WriteString("traits_sha256", revision.TraitsSha256);
        writer.WriteStartArray("trait_keys");
        foreach (var key in revision.TraitKeys) writer.WriteStringValue(key);
        writer.WriteEndArray();
        writer.WriteStartArray("evidence_sha256");
        foreach (var digest in revision.EvidenceSha256) writer.WriteStringValue(digest);
        writer.WriteEndArray();
        writer.WriteString("status", revision.Status);
        writer.WriteEndObject();
    }
}

public static class PersonaTraitVocabularyV1
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedValues =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["curiosity"] = new(["low", "medium", "high"], StringComparer.Ordinal),
            ["humor"] = new(["low", "subtle", "playful"], StringComparer.Ordinal),
            ["pace"] = new(["slow", "balanced", "fast"], StringComparer.Ordinal),
            ["sociality"] = new(["reserved", "balanced", "outgoing"], StringComparer.Ordinal),
            ["tone"] = new(["calm", "warm", "direct", "playful", "formal"], StringComparer.Ordinal)
        };

    public static IReadOnlyDictionary<string, string> ValidateAndFreeze(IReadOnlyDictionary<string, string> traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        var validated = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in traits)
        {
            if (!AllowedValues.TryGetValue(pair.Key, out var allowed) || pair.Value is null || !allowed.Contains(pair.Value) ||
                !validated.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Persona traits violate the closed v1 vocabulary.", nameof(traits));
        }
        if (validated.Count is < 1 or > 5)
            throw new ArgumentException("Between one and five closed-vocabulary persona traits are required.", nameof(traits));
        return new ReadOnlyDictionary<string, string>(validated);
    }
}
