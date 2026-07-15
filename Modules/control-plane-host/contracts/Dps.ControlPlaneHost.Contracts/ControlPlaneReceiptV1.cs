using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.ControlPlaneHost.Contracts;

public sealed record ControlPlaneReceiptV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("receipt_id")] string ReceiptId,
    [property: JsonPropertyName("source_contract_id")] string SourceContractId,
    [property: JsonPropertyName("source_producer_module")] string SourceProducerModule,
    [property: JsonPropertyName("source_payload_sha256")] string SourcePayloadSha256,
    [property: JsonPropertyName("decision")] string Decision)
{
    public void Validate()
    {
        ControlContractValidation.RequireMajor(SchemaVersion, 1); ControlContractValidation.RequireExact(ContractId, "control.plane.receipt/v1", nameof(ContractId)); ControlContractValidation.RequireExact(ProducerModule, "control-plane-host", nameof(ProducerModule)); ControlContractValidation.RequireSoulId(SoulId); ControlContractValidation.RequireDeviceBindingId(DeviceBindingId); ControlContractValidation.RequirePlatformAccountId(PlatformAccountId); ControlContractValidation.RequireTraceId(TraceId); ControlContractValidation.RequireIdempotencyKey(IdempotencyKey); ControlContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt)); ControlContractValidation.RequireExact(PrivacyClass, "sensitive", nameof(PrivacyClass)); ControlContractValidation.RequireHex(ReceiptId, "receipt_", 32, nameof(ReceiptId)); ControlContractValidation.RequireSourceOwnerPair(SourceContractId, SourceProducerModule); ControlContractValidation.RequireSha256(SourcePayloadSha256, nameof(SourcePayloadSha256)); ControlContractValidation.RequireExact(Decision, "accepted", nameof(Decision));
    }
}

public static class ControlPlaneReceiptV1Codec
{
    public const int MaximumPayloadBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "soul_id",
        "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
        "occurred_at", "privacy_class", "receipt_id", "source_contract_id",
        "source_producer_module", "source_payload_sha256", "decision"
    };

    public static byte[] Serialize(ControlPlaneReceiptV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", value.SchemaVersion);
            writer.WriteString("contract_id", value.ContractId);
            writer.WriteString("producer_module", value.ProducerModule);
            writer.WriteString("soul_id", value.SoulId);
            writer.WriteString("device_binding_id", value.DeviceBindingId);
            writer.WriteString("platform_account_id", value.PlatformAccountId);
            writer.WriteString("trace_id", value.TraceId);
            writer.WriteString("idempotency_key", value.IdempotencyKey);
            writer.WriteString("occurred_at", FormatWireUtc(value.OccurredAt));
            writer.WriteString("privacy_class", value.PrivacyClass);
            writer.WriteString("receipt_id", value.ReceiptId);
            writer.WriteString("source_contract_id", value.SourceContractId);
            writer.WriteString("source_producer_module", value.SourceProducerModule);
            writer.WriteString("source_payload_sha256", value.SourcePayloadSha256);
            writer.WriteString("decision", value.Decision);
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentException("Control Plane receipt exceeds its byte budget.", nameof(value));
        }
        return payload;
    }

    public static ControlPlaneReceiptV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: true);

    /// <summary>
    /// Reads PostgreSQL jsonb text after jsonb has intentionally discarded key
    /// order and whitespace. Exact fields, types, values and UTC semantics stay
    /// mandatory; only byte-for-byte wire canonicality is not asserted here.
    /// </summary>
    public static ControlPlaneReceiptV1 DeserializeSemanticJsonb(ReadOnlySpan<byte> payloadUtf8)
        => DeserializeCore(payloadUtf8, requireCanonicalWire: false);

    private static ControlPlaneReceiptV1 DeserializeCore(
        ReadOnlySpan<byte> payloadUtf8,
        bool requireCanonicalWire)
    {
        if (payloadUtf8.Length is < 2 or > MaximumPayloadBytes)
        {
            throw new ArgumentException(
                "Control Plane receipt is outside its byte budget.",
                nameof(payloadUtf8));
        }
        try
        {
            _ = StrictUtf8.GetCharCount(payloadUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                "Control Plane receipt is not strict UTF-8.",
                nameof(payloadUtf8),
                exception);
        }

        using var document = JsonDocument.Parse(
            payloadUtf8.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Control Plane receipt must be one JSON object.");
        }

        var fields = ReadExactFields(document.RootElement);
        var receipt = new ControlPlaneReceiptV1(
            ReadString(fields, "schema_version"),
            ReadString(fields, "contract_id"),
            ReadString(fields, "producer_module"),
            ReadString(fields, "soul_id"),
            ReadString(fields, "device_binding_id"),
            ReadString(fields, "platform_account_id"),
            ReadString(fields, "trace_id"),
            ReadString(fields, "idempotency_key"),
            ReadWireUtc(fields, "occurred_at"),
            ReadString(fields, "privacy_class"),
            ReadString(fields, "receipt_id"),
            ReadString(fields, "source_contract_id"),
            ReadString(fields, "source_producer_module"),
            ReadString(fields, "source_payload_sha256"),
            ReadString(fields, "decision"));
        receipt.Validate();
        if (requireCanonicalWire)
        {
            var canonicalPayload = Serialize(receipt);
            try
            {
                if (!payloadUtf8.SequenceEqual(canonicalPayload))
                {
                    throw new ArgumentException(
                        "Control Plane receipt is not the canonical snake_case wire.",
                        nameof(payloadUtf8));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalPayload);
            }
        }
        return receipt;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadExactFields(JsonElement root)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExactFields.Contains(property.Name))
            {
                throw new ArgumentException($"Unknown Control Plane receipt field '{property.Name}'.");
            }
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new ArgumentException($"Duplicate Control Plane receipt field '{property.Name}'.");
            }
        }
        if (!ExactFields.SetEquals(fields.Keys))
        {
            throw new ArgumentException("Control Plane receipt has missing fields.");
        }
        return fields;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Control Plane receipt field '{name}' must be a string.");
        }
        return value.GetString()
            ?? throw new ArgumentException($"Control Plane receipt field '{name}' is null.");
    }

    private static DateTimeOffset ReadWireUtc(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = ReadString(fields, name);
        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
        ];
        if (!value.EndsWith('Z')
            || !DateTimeOffset.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Control Plane receipt field '{name}' must be exact Zulu UTC.");
        }
        return parsed;
    }

    private static string FormatWireUtc(DateTimeOffset value)
    {
        ControlContractValidation.RequireUtc(value, nameof(value));
        return value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }
}

public static class ControlContractValidation
{
    public static void RequireMajor(string value, int expected)
    {
        RequireText(value, 32, nameof(value));
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3
            || parts.Any(static part => part.Length == 0 || part.Any(static value => value is < '0' or > '9'))
            || !string.Equals(
                parts[0],
                expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported schema version '{value}'.");
        }
    }
    public static void RequireExact(string actual, string expected, string name) { if (actual != expected) throw new NotSupportedException($"Unsupported {name} '{actual}'."); }
    public static void RequireText(string value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"Invalid {name}.", name); }
    public static void RequireUtc(DateTimeOffset value, string name) { if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} must be UTC.", name); }
    public static void RequireSoulId(string value) => RequireHex(value, "soul_", 64, nameof(value));
    public static void RequireDeviceBindingId(string value) => RequireHex(value, "db_", 32, nameof(value));
    public static void RequirePlatformAccountId(string value) => RequireHex(value, "pa_", 32, nameof(value));
    public static void RequireTraceId(string value) => RequireHex(value, "trace_", 32, nameof(value));
    public static void RequireIdempotencyKey(string value) => RequireHex(value, "idem_", 64, nameof(value));
    public static void RequireSha256(string value, string name) => RequireHex(value, string.Empty, 64, name);
    public static void RequireSourceOwnerPair(string contractId, string producerModule)
    {
        var expectedProducer = contractId switch
        {
            "device.registered/v1" => "device-registry",
            "platform.account.authorized/v1" => "platform-account-registry",
            "identity.binding/v1" => "binding",
            "persona.revision/v1" => "persona-store",
            "soul.memory.readback/v1" => "soul-memory-adapter",
            _ => throw new NotSupportedException($"Unsupported source contract '{contractId}'.")
        };
        RequireExact(producerModule, expectedProducer, nameof(producerModule));
    }
    public static void RequireHex(string value, string prefix, int length, string name) { if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + length || value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef")) throw new ArgumentException($"Invalid {name}.", name); }
}
