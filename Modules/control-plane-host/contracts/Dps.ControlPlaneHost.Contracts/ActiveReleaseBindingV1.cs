using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.ControlPlaneHost.Contracts;

/// <summary>
/// Per-device active Release BOM runtime truth owned by control-plane-host.
/// Mirrors contracts/provided/active.release.binding.v1.schema.json.
/// Generation is the runtime activation ordinal (strictly monotonic per
/// device, including rollback); ReleaseBomGeneration is the out-of-repo
/// signer's ordinal carried inside the signed BOM (rollback may revert it).
/// The execution token is signer-committed: it is canonical Base64 for
/// exactly 32 opaque bytes and its SHA-256 equals ActivationTokenSha256,
/// the digest the signer pre-committed inside the signed BOM.
/// </summary>
public sealed record ActiveReleaseBindingV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("release_bom_generation")] long ReleaseBomGeneration,
    [property: JsonPropertyName("execution_token")] string ExecutionTokenBase64,
    [property: JsonPropertyName("activation_token_sha256")] string ActivationTokenSha256,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("signer_identity")] string SignerIdentity,
    [property: JsonPropertyName("signer_key_id")] string SignerKeyId,
    [property: JsonPropertyName("bom_signature_sha256")] string BomSignatureSha256,
    [property: JsonPropertyName("activated_at")] DateTimeOffset ActivatedAt,
    [property: JsonPropertyName("receipt_id")] string ReceiptId)
{
    public const int ExecutionTokenSizeBytes = 32;

    public void Validate()
    {
        ControlContractValidation.RequireMajor(SchemaVersion, 1);
        ControlContractValidation.RequireExact(ContractId, "active.release.binding/v1", nameof(ContractId));
        ControlContractValidation.RequireExact(ProducerModule, "control-plane-host", nameof(ProducerModule));
        ControlContractValidation.RequireDeviceBindingId(DeviceBindingId);
        ControlContractValidation.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        ReleaseBindingValidation.RequirePositive(Generation, nameof(Generation));
        ReleaseBindingValidation.RequirePositive(ReleaseBomGeneration, nameof(ReleaseBomGeneration));
        ControlContractValidation.RequireSha256(ActivationTokenSha256, nameof(ActivationTokenSha256));
        RequireSignerCommittedToken();
        ReleaseBindingValidation.RequireStatus(Status);
        ReleaseBindingValidation.RequireIdentity(SignerIdentity, nameof(SignerIdentity));
        ReleaseBindingValidation.RequireIdentity(SignerKeyId, nameof(SignerKeyId));
        ControlContractValidation.RequireSha256(BomSignatureSha256, nameof(BomSignatureSha256));
        ControlContractValidation.RequireUtc(ActivatedAt, nameof(ActivatedAt));
        ControlContractValidation.RequireHex(ReceiptId, "receipt_", 32, nameof(ReceiptId));
    }

    private void RequireSignerCommittedToken()
    {
        byte[] token;
        try
        {
            token = Convert.FromBase64String(ExecutionTokenBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "Execution token must use Base64 encoding.", nameof(ExecutionTokenBase64), exception);
        }
        try
        {
            if (token.Length != ExecutionTokenSizeBytes
                || !string.Equals(Convert.ToBase64String(token), ExecutionTokenBase64, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Execution token must be canonical Base64 for exactly 256 opaque bits.",
                    nameof(ExecutionTokenBase64));
            }
            if (!string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(token)),
                    ActivationTokenSha256,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Execution token does not match the signer-committed activation_token_sha256.",
                    nameof(ExecutionTokenBase64));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }
}

/// <summary>
/// Strict codec for active.release.binding/v1, mirroring
/// ControlPlaneReceiptV1Codec: strict UTF-8, exact snake_case field set,
/// canonical wire equality on read, exact Zulu timestamps, byte budget.
/// </summary>
public static class ActiveReleaseBindingV1Codec
{
    public const int MaximumPayloadBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "device_binding_id",
        "release_bom_sha256", "generation", "release_bom_generation",
        "execution_token", "activation_token_sha256", "status",
        "signer_identity", "signer_key_id", "bom_signature_sha256",
        "activated_at", "receipt_id"
    };

    public static byte[] Serialize(ActiveReleaseBindingV1 value)
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
            writer.WriteString("device_binding_id", value.DeviceBindingId);
            writer.WriteString("release_bom_sha256", value.ReleaseBomSha256);
            writer.WriteNumber("generation", value.Generation);
            writer.WriteNumber("release_bom_generation", value.ReleaseBomGeneration);
            writer.WriteString("execution_token", value.ExecutionTokenBase64);
            writer.WriteString("activation_token_sha256", value.ActivationTokenSha256);
            writer.WriteString("status", value.Status);
            writer.WriteString("signer_identity", value.SignerIdentity);
            writer.WriteString("signer_key_id", value.SignerKeyId);
            writer.WriteString("bom_signature_sha256", value.BomSignatureSha256);
            writer.WriteString("activated_at", ReleaseBindingWire.FormatWireUtc(value.ActivatedAt));
            writer.WriteString("receipt_id", value.ReceiptId);
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentException("Active release binding exceeds its byte budget.", nameof(value));
        }
        return payload;
    }

    public static ActiveReleaseBindingV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
    {
        using var document = ReleaseBindingWire.ParseStrict(
            payloadUtf8, MaximumPayloadBytes, "active release binding");
        var fields = ReleaseBindingWire.ReadExactFields(
            document.RootElement, ExactFields, "active release binding");
        var binding = new ActiveReleaseBindingV1(
            ReleaseBindingWire.ReadString(fields, "schema_version"),
            ReleaseBindingWire.ReadString(fields, "contract_id"),
            ReleaseBindingWire.ReadString(fields, "producer_module"),
            ReleaseBindingWire.ReadString(fields, "device_binding_id"),
            ReleaseBindingWire.ReadString(fields, "release_bom_sha256"),
            ReleaseBindingWire.ReadInt64(fields["generation"], "generation"),
            ReleaseBindingWire.ReadInt64(fields["release_bom_generation"], "release_bom_generation"),
            ReleaseBindingWire.ReadString(fields, "execution_token"),
            ReleaseBindingWire.ReadString(fields, "activation_token_sha256"),
            ReleaseBindingWire.ReadString(fields, "status"),
            ReleaseBindingWire.ReadString(fields, "signer_identity"),
            ReleaseBindingWire.ReadString(fields, "signer_key_id"),
            ReleaseBindingWire.ReadString(fields, "bom_signature_sha256"),
            ReleaseBindingWire.ReadWireUtc(fields, "activated_at"),
            ReleaseBindingWire.ReadString(fields, "receipt_id"));
        binding.Validate();
        var canonicalPayload = Serialize(binding);
        try
        {
            if (!payloadUtf8.SequenceEqual(canonicalPayload))
            {
                throw new ArgumentException(
                    "Active release binding is not the canonical snake_case wire.",
                    nameof(payloadUtf8));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalPayload);
        }
        return binding;
    }
}

/// <summary>
/// Shared strict wire helpers for the release binding contract pack; the
/// same fail-closed profile as ControlPlaneReceiptV1Codec's private helpers.
/// </summary>
public static class ReleaseBindingWire
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static JsonDocument ParseStrict(
        ReadOnlySpan<byte> payloadUtf8,
        int maximumPayloadBytes,
        string label)
    {
        if (payloadUtf8.Length is < 2 || payloadUtf8.Length > maximumPayloadBytes)
        {
            throw new ArgumentException($"The {label} is outside its byte budget.", nameof(payloadUtf8));
        }
        try
        {
            _ = StrictUtf8.GetCharCount(payloadUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                $"The {label} is not strict UTF-8.", nameof(payloadUtf8), exception);
        }
        var document = JsonDocument.Parse(
            payloadUtf8.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new ArgumentException($"The {label} must be one JSON object.");
        }
        return document;
    }

    public static IReadOnlyDictionary<string, JsonElement> ReadExactFields(
        JsonElement root,
        IReadOnlySet<string> exactFields,
        string label)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!exactFields.Contains(property.Name))
            {
                throw new ArgumentException($"Unknown {label} field '{property.Name}'.");
            }
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new ArgumentException($"Duplicate {label} field '{property.Name}'.");
            }
        }
        if (!exactFields.SetEquals(fields.Keys))
        {
            throw new ArgumentException($"The {label} has missing fields.");
        }
        return fields;
    }

    public static string ReadString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Field '{name}' must be a string.");
        }
        return value.GetString()
            ?? throw new ArgumentException($"Field '{name}' is null.");
    }

    public static long ReadInt64(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || value.GetRawText().AsSpan().ContainsAny(".eE"))
        {
            throw new ArgumentException($"Field '{name}' must be a signed 64-bit integer.");
        }
        return parsed;
    }

    public static DateTimeOffset ReadWireUtc(
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
            throw new ArgumentException($"Field '{name}' must be exact Zulu UTC.");
        }
        return parsed;
    }

    public static string FormatWireUtc(DateTimeOffset value)
    {
        ControlContractValidation.RequireUtc(value, nameof(value));
        return value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }
}

public static class ReleaseBindingValidation
{
    private static readonly string[] Statuses = ["active", "previous", "revoked"];

    public static void RequirePositive(long value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentException($"Invalid {name}.", name);
        }
    }

    public static void RequireStatus(string value)
    {
        if (!Statuses.Contains(value, StringComparer.Ordinal))
        {
            throw new ArgumentException("Invalid release binding status.", nameof(value));
        }
    }

    public static void RequireIdentity(string value, string name)
    {
        if (value is null
            || value.Length is < 2 or > 64
            || value[0] is < 'a' or > 'z'
            || value.AsSpan(1).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-"))
        {
            throw new ArgumentException($"Invalid {name}.", name);
        }
    }
}
