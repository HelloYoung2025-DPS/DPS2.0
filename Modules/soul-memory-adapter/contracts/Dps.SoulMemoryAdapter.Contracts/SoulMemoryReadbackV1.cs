using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dps.GBrainProjector.Contracts;

namespace Dps.SoulMemoryAdapter.Contracts;

public sealed record SoulMemoryReadbackV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(CanonicalUtcDateTimeOffsetJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("projection_schema_version")] string ProjectionSchemaVersion,
    [property: JsonPropertyName("projection_contract_id")] string ProjectionContractId,
    [property: JsonPropertyName("projection_revision")] string ProjectionRevision,
    [property: JsonPropertyName("projection_checksum")] string ProjectionChecksum,
    [property: JsonPropertyName("readback_checksum")] string ReadbackChecksum,
    [property: JsonPropertyName("status")] string Status)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "soul.memory.readback/v1";
    public const string CurrentProducerModule = "soul-memory-adapter";
    public const string PreparedStatus = "prepared";
    public const string VerifiedStatus = "verified";
    public static readonly string NoReadbackChecksum = new('0', 64);

    public void Validate()
    {
        SoulMemoryContractValidation.RequireMajor(SchemaVersion, 1);
        SoulMemoryContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        SoulMemoryContractValidation.RequireExact(
            ProducerModule,
            CurrentProducerModule,
            nameof(ProducerModule));
        SoulMemoryContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        SoulMemoryContractValidation.RequireDeviceBindingId(DeviceBindingId, nameof(DeviceBindingId));
        SoulMemoryContractValidation.RequirePlatformAccountId(
            PlatformAccountId,
            nameof(PlatformAccountId));
        SoulMemoryContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        SoulMemoryContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        SoulMemoryContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        SoulMemoryContractValidation.RequireExact(PrivacyClass, "personal", nameof(PrivacyClass));
        // gbrain.projection/v2 derives source_id from (soul, nonce) via domain-separated
        // SHA-256; the nonce never travels on this receipt, so no consumer can re-derive
        // the binding locally. This layer therefore enforces canonical format only; the
        // authoritative SourceId<->(SoulHash,Nonce) binding proof is GBrainProjectionV2
        // .Validate() on the projection itself, and prepared/readback consistency is the
        // adapter's exact field-by-field comparison.
        SoulMemoryContractValidation.RequireSourceId(SourceId, nameof(SourceId));
        // soul.memory.readback/v1 deliberately keeps its own major and exact 17-field
        // wire shape across the gbrain.projection v1->v2 migration; only the expected
        // values of the embedded projection metadata below moved to the v2 contract.
        SoulMemoryContractValidation.RequireMajor(ProjectionSchemaVersion, 2);
        SoulMemoryContractValidation.RequireExact(
            ProjectionContractId,
            GBrainProjectionV2.CurrentContractId,
            nameof(ProjectionContractId));
        SoulMemoryContractValidation.RequireSha256(ProjectionRevision, nameof(ProjectionRevision));
        SoulMemoryContractValidation.RequireSha256(ProjectionChecksum, nameof(ProjectionChecksum));
        SoulMemoryContractValidation.RequireSha256(ReadbackChecksum, nameof(ReadbackChecksum));

        if (Status is not (PreparedStatus or VerifiedStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(Status), "Unknown read-back status.");
        }

        if (Status == PreparedStatus &&
            !string.Equals(ReadbackChecksum, NoReadbackChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A prepared projection cannot claim an external read-back checksum.");
        }

        if (Status == VerifiedStatus &&
            !string.Equals(ReadbackChecksum, ProjectionChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Verified read-back checksum must equal the GBrain projection checksum.");
        }
    }
}

public static class SoulMemoryReadbackV1Json
{
    private const int MaximumUtf8Bytes = 65_536;
    private static readonly HashSet<string> ExactProperties = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "soul_id", "device_binding_id",
        "platform_account_id", "trace_id", "idempotency_key", "occurred_at", "privacy_class",
        "source_id", "projection_schema_version", "projection_contract_id", "projection_revision",
        "projection_checksum", "readback_checksum", "status"
    };
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    public static SoulMemoryReadbackV1 Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes)
            throw new JsonException("soul.memory.readback/v1 exceeds the bounded wire size.");

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("soul.memory.readback/v1 must be a JSON object.");

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!observed.Add(property.Name))
                throw new JsonException($"Duplicate property '{property.Name}' is forbidden.");
            if (!ExactProperties.Contains(property.Name))
                throw new JsonException($"Unknown property '{property.Name}' is forbidden.");
        }
        if (!observed.SetEquals(ExactProperties))
            throw new JsonException("The exact soul.memory.readback/v1 property set is required.");

        var value = JsonSerializer.Deserialize<SoulMemoryReadbackV1>(json, StrictOptions)
            ?? throw new JsonException("soul.memory.readback/v1 cannot be null.");
        value.Validate();
        return value;
    }

    public static string Serialize(SoulMemoryReadbackV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return JsonSerializer.Serialize(value, StrictOptions);
    }
}

public static class SoulMemoryContractValidation
{
    public static void RequireMajor(string value, int expected)
    {
        RequireText(value, 32, nameof(value));
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3 ||
            !string.Equals(parts[0], expected.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            parts.Any(static part => part.Length == 0 || part.Any(static character => character is < '0' or > '9')))
        {
            throw new NotSupportedException(
                $"Unsupported schema major '{value}'. Expected major {expected}.");
        }
    }

    public static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unsupported {name} '{actual}'. Expected '{expected}'.");
        }
    }

    public static void RequireText(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"{name} must contain between 1 and {maximumLength} characters.",
                name);
        }
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be UTC.", name);
        }
    }

    public static void RequireSoulId(string value, string name)
    {
        RequireHex(value, "soul_", 64, name);
    }

    public static void RequireDeviceBindingId(string value, string name)
    {
        RequireHex(value, "db_", 32, name);
    }

    public static void RequirePlatformAccountId(string value, string name)
    {
        RequireHex(value, "pa_", 32, name);
    }

    public static void RequireTraceId(string value, string name) =>
        RequireHex(value, "trace_", 32, name);

    public static void RequireIdempotencyKey(string value, string name) =>
        RequireHex(value, "idem_", 64, name);

    public static void RequireSha256(string value, string name)
    {
        RequireHex(value, string.Empty, 64, name);
    }

    /// <summary>
    /// Canonical GBrain Source identifier format: "dps-" + 28 lowercase hex characters
    /// (32 total, matching GBrainSourceBindingV1.SourceIdLength). Format-only by design:
    /// the v2 derivation needs the binding nonce, so the cryptographic binding proof
    /// lives in GBrainProjectionV2.Validate(), not here.
    /// </summary>
    public static void RequireSourceId(string value, string name)
    {
        RequireHex(value, "dps-", 28, name);
    }

    private static void RequireHex(string value, string prefix, int length, string name)
    {
        if (value is null ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + length ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"Invalid {name}.", name);
        }
    }

}

public sealed class CanonicalUtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private static readonly Regex CanonicalUtc = new(
        "\\A[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\\.[0-9]{0,6}[1-9])?Z\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (value is null || value.Length > 32 || !CanonicalUtc.IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new JsonException("occurred_at must be canonical UTC with at most seven fractional digits.");
        return parsed;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        SoulMemoryContractValidation.RequireUtc(value, nameof(value));
        var utc = value.ToUniversalTime();
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        writer.WriteStringValue(fraction.Length == 0 ? text + "Z" : text + "." + fraction + "Z");
    }
}
