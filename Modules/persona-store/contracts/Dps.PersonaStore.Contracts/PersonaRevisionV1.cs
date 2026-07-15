using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.PersonaStore.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonaRevisionV1(
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
    [property: JsonPropertyName("persona_revision"), JsonRequired] long PersonaRevision,
    [property: JsonPropertyName("traits_sha256"), JsonRequired] string TraitsSha256,
    [property: JsonPropertyName("trait_keys"), JsonRequired] IReadOnlyList<string> TraitKeys,
    [property: JsonPropertyName("evidence_sha256"), JsonRequired] IReadOnlyList<string> EvidenceSha256,
    [property: JsonPropertyName("status"), JsonRequired] string Status)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "persona.revision/v1";
    public const string CurrentProducerModule = "persona-store";
    private static readonly string[] AllowedKeys = ["curiosity", "humor", "pace", "sociality", "tone"];

    public bool Equals(PersonaRevisionV1? other) =>
        other is not null &&
        SchemaVersion == other.SchemaVersion && ContractId == other.ContractId && ProducerModule == other.ProducerModule &&
        SoulId == other.SoulId && DeviceBindingId == other.DeviceBindingId && PlatformAccountId == other.PlatformAccountId &&
        TraceId == other.TraceId && IdempotencyKey == other.IdempotencyKey && OccurredAt == other.OccurredAt &&
        PrivacyClass == other.PrivacyClass && PersonaRevision == other.PersonaRevision && TraitsSha256 == other.TraitsSha256 &&
        Status == other.Status && TraitKeys.SequenceEqual(other.TraitKeys, StringComparer.Ordinal) &&
        EvidenceSha256.SequenceEqual(other.EvidenceSha256, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(ContractId, StringComparer.Ordinal);
        hash.Add(ProducerModule, StringComparer.Ordinal);
        hash.Add(SoulId, StringComparer.Ordinal);
        hash.Add(DeviceBindingId, StringComparer.Ordinal);
        hash.Add(PlatformAccountId, StringComparer.Ordinal);
        hash.Add(TraceId, StringComparer.Ordinal);
        hash.Add(IdempotencyKey, StringComparer.Ordinal);
        hash.Add(OccurredAt);
        hash.Add(PrivacyClass, StringComparer.Ordinal);
        hash.Add(PersonaRevision);
        hash.Add(TraitsSha256, StringComparer.Ordinal);
        foreach (var value in TraitKeys) hash.Add(value, StringComparer.Ordinal);
        foreach (var value in EvidenceSha256) hash.Add(value, StringComparer.Ordinal);
        hash.Add(Status, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

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
        PersonaContractValidation.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        if (PersonaRevision < 1) throw new ArgumentOutOfRangeException(nameof(PersonaRevision));
        PersonaContractValidation.RequireSha256(TraitsSha256, nameof(TraitsSha256));
        if (TraitKeys is null || TraitKeys.Except(AllowedKeys, StringComparer.Ordinal).Any())
            throw new ArgumentException("Trait keys must be known.", nameof(TraitKeys));
        PersonaContractValidation.RequireStrictOrdinalAscending(TraitKeys, nameof(TraitKeys));
        if ((Status == "active" && TraitKeys.Count == 0) || (Status == "deleted" && TraitKeys.Count != 0))
            throw new ArgumentException("Active revisions require trait keys and deleted revisions cannot expose them.", nameof(TraitKeys));
        if (EvidenceSha256 is null || EvidenceSha256.Count is < 1 or > 64)
            throw new ArgumentException("Between one and 64 evidence hashes are required.", nameof(EvidenceSha256));
        PersonaContractValidation.RequireStrictOrdinalAscending(EvidenceSha256, nameof(EvidenceSha256));
        foreach (var digest in EvidenceSha256) PersonaContractValidation.RequireSha256(digest, nameof(EvidenceSha256));
        if (Status is not ("active" or "deleted")) throw new ArgumentOutOfRangeException(nameof(Status));
    }

    public PersonaRevisionV1 ImmutableCopy()
    {
        Validate();
        var copy = this with
        {
            TraitKeys = Array.AsReadOnly(TraitKeys.ToArray()),
            EvidenceSha256 = Array.AsReadOnly(EvidenceSha256.ToArray())
        };
        copy.Validate();
        return copy;
    }
}

public static class PersonaContractValidation
{
    public static void RequireMajor(string value, int expected)
    {
        RequireText(value, 32, nameof(value));
        var segments = value.Split('.');
        if (segments.Length is < 1 or > 3 ||
            !string.Equals(segments[0], expected.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            segments.Any(static segment => segment.Length == 0 || segment.AsSpan().ContainsAnyExcept("0123456789")))
            throw new NotSupportedException($"Unsupported schema version '{value}'.");
    }
    public static void RequireExact(string actual, string expected, string name) { if (actual != expected) throw new NotSupportedException($"Unsupported {name} '{actual}'."); }
    public static void RequireText(string value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireOccurredAt(DateTimeOffset value, string name) { if (value.Offset != TimeSpan.Zero || value.Year is < 2020 or > 2199) throw new ArgumentException($"{name} must be a non-default UTC timestamp in the supported range.", name); }
    public static void RequireOccurredAtText(string value, string name)
    {
        if (value is null || value.Length is < 20 or > 33 || !OccurredAtPattern.IsMatch(value) ||
            !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            throw new JsonException($"Invalid {name} wire representation.");
        RequireOccurredAt(parsed, name);
    }
    public static void RequireSoulId(string value) => RequireHex(value, "soul_", 64, nameof(value));
    public static void RequireDeviceBindingId(string value) => RequireHex(value, "db_", 32, nameof(value));
    public static void RequirePlatformAccountId(string value) => RequireHex(value, "pa_", 32, nameof(value));
    public static void RequireTraceId(string value) => RequireHex(value, "trace_", 32, nameof(value));
    public static void RequireIdempotencyKey(string value) => RequireHex(value, "idem_", 64, nameof(value));
    public static void RequireExportReceiptId(string value) => RequireHex(value, "pexport_", 64, nameof(value));
    public static void RequireSha256(string value, string name) => RequireHex(value, string.Empty, 64, name);
    public static void RequireStrictOrdinalAscending(IReadOnlyList<string> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is null || (index > 0 && StringComparer.Ordinal.Compare(values[index - 1], values[index]) >= 0))
                throw new ArgumentException($"{name} must be strictly ordinal ascending and unique.", name);
        }
    }
    private static void RequireHex(string value, string prefix, int length, string name) { if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + length || value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef")) throw new ArgumentException($"Invalid {name}.", name); }

    private static readonly Regex OccurredAtPattern = new(
        @"\A(?:20[2-9][0-9]|21[0-9]{2})-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,7})?(?:Z|\+00:00)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}

public static class PersonaContractJson
{
    private const int MaximumUtf8Bytes = 64 * 1024;
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T DeserializeStrict<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var maximumUtf8Bytes = typeof(T) == typeof(PersonaHistoryExportV1) ? 16 * 1024 * 1024 : MaximumUtf8Bytes;
        if (Encoding.UTF8.GetByteCount(json) > maximumUtf8Bytes)
            throw new JsonException($"The contract exceeds the {maximumUtf8Bytes}-byte limit.");

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        RejectDuplicateProperties(document.RootElement);
        ValidateOccurredAtWireRepresentations<T>(document.RootElement);
        var value = JsonSerializer.Deserialize<T>(json, StrictOptions)
            ?? throw new JsonException("The contract payload was null.");
        return value switch
        {
            PersonaRevisionV1 revision => (T)(object)revision.ImmutableCopy(),
            PersonaHistoryExportV1 export => (T)(object)export.ImmutableCopy(),
            _ => throw new NotSupportedException($"No strict Persona validator is registered for {typeof(T).FullName}.")
        };
    }

    private static void ValidateOccurredAtWireRepresentations<T>(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("occurred_at", out var occurredAt) || occurredAt.ValueKind != JsonValueKind.String)
            throw new JsonException("The contract requires one occurred_at string in its envelope.");
        PersonaContractValidation.RequireOccurredAtText(occurredAt.GetString()!, "occurred_at");
        if (typeof(T) != typeof(PersonaHistoryExportV1)) return;
        if (!root.TryGetProperty("revisions", out var revisions) || revisions.ValueKind != JsonValueKind.Array)
            throw new JsonException("The history export requires a revisions array.");
        foreach (var item in revisions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("revision", out var revision) ||
                revision.ValueKind != JsonValueKind.Object || !revision.TryGetProperty("occurred_at", out var revisionOccurredAt) ||
                revisionOccurredAt.ValueKind != JsonValueKind.String)
                throw new JsonException("Every history export item requires one revision occurred_at string.");
            PersonaContractValidation.RequireOccurredAtText(revisionOccurredAt.GetString()!, "revision.occurred_at");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException($"Duplicate JSON property '{property.Name}' is forbidden.");
                RejectDuplicateProperties(property.Value);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Array) return;
        foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
