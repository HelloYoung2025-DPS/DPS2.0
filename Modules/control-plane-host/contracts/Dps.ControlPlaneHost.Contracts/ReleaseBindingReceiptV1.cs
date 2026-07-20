using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.ControlPlaneHost.Contracts;

/// <summary>
/// One endpoint of a release binding transition (before or after state).
/// </summary>
public sealed record ReleaseBindingEndpointV1(
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("status")] string Status)
{
    public void Validate()
    {
        ControlContractValidation.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        ReleaseBindingValidation.RequirePositive(Generation, nameof(Generation));
        ReleaseBindingValidation.RequireStatus(Status);
    }
}

/// <summary>
/// Versioned receipt for activation, revocation, and rollback of the active
/// Release BOM binding. Mirrors
/// contracts/provided/release.binding.receipt.v1.schema.json. From is null
/// only for a first activation (explicit nullable shape, never a missing
/// field on the wire). An activation over a revoked binding records that
/// binding as from with status "revoked"; it never becomes "previous" and
/// is never a rollback target.
/// </summary>
public sealed record ReleaseBindingReceiptV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("receipt_kind")] string ReceiptKind,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("from")] ReleaseBindingEndpointV1? From,
    [property: JsonPropertyName("to")] ReleaseBindingEndpointV1 To,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("actor_identity")] string ActorIdentity,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("payload_sha256")] string PayloadSha256,
    [property: JsonPropertyName("receipt_id")] string ReceiptId)
{
    public void Validate()
    {
        ControlContractValidation.RequireMajor(SchemaVersion, 1);
        ControlContractValidation.RequireExact(ContractId, "release.binding.receipt/v1", nameof(ContractId));
        ControlContractValidation.RequireExact(ProducerModule, "control-plane-host", nameof(ProducerModule));
        ControlContractValidation.RequireDeviceBindingId(DeviceBindingId);
        From?.Validate();
        ArgumentNullException.ThrowIfNull(To);
        To.Validate();
        ReleaseBindingValidation.RequirePositive(Sequence, nameof(Sequence));
        ReleaseBindingValidation.RequireIdentity(ActorIdentity, nameof(ActorIdentity));
        ControlContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        ControlContractValidation.RequireSha256(PayloadSha256, nameof(PayloadSha256));
        // The declared digest must BE the digest of the receipt content: a
        // receipt whose fields were mutated without recomputing payload_sha256
        // is rejected here, so no shape-valid-but-inconsistent receipt passes
        // any Validate call site (codec serialize/deserialize, authority
        // issuance, truth-store recovery). Fixed-time over the decoded bytes.
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(PayloadSha256),
                Convert.FromHexString(ComputePayloadSha256())))
        {
            throw new ArgumentException(
                "Release binding receipt payload_sha256 does not match the receipt content.",
                nameof(PayloadSha256));
        }
        ControlContractValidation.RequireHex(ReceiptId, "receipt_", 32, nameof(ReceiptId));
        switch (ReceiptKind)
        {
            case "activation" when From is null or { Status: "previous" } or { Status: "revoked" } && To.Status == "active":
            case "revocation" when From is { Status: "active" } && To.Status == "revoked":
            case "rollback" when From is { Status: "revoked" } && To.Status == "active":
                break;
            default:
                throw new ArgumentException(
                    "Release binding receipt kind does not match its transition endpoints.",
                    nameof(ReceiptKind));
        }
    }

    /// <summary>
    /// Lowercase hex SHA-256 over the canonical JSON of every receipt field
    /// except payload_sha256: python json.dumps(value, sort_keys=True,
    /// separators=(",", ":"), ensure_ascii=False) encoded as UTF-8, matching
    /// the repository anchor_id convention.
    /// </summary>
    public string ComputePayloadSha256()
    {
        var builder = new StringBuilder(512);
        builder.Append("{\"actor_identity\":\"").Append(ActorIdentity)
            .Append("\",\"contract_id\":\"").Append(ContractId)
            .Append("\",\"device_binding_id\":\"").Append(DeviceBindingId)
            .Append("\",\"from\":").Append(FormatEndpoint(From))
            .Append(",\"occurred_at\":\"").Append(FormatUtc(OccurredAt))
            .Append("\",\"producer_module\":\"").Append(ProducerModule)
            .Append("\",\"receipt_id\":\"").Append(ReceiptId)
            .Append("\",\"receipt_kind\":\"").Append(ReceiptKind)
            .Append("\",\"schema_version\":\"").Append(SchemaVersion)
            .Append("\",\"sequence\":").Append(Sequence.ToString(CultureInfo.InvariantCulture))
            .Append(",\"to\":").Append(FormatEndpoint(To))
            .Append('}');
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    private static string FormatEndpoint(ReleaseBindingEndpointV1? endpoint)
        => endpoint is null
            ? "null"
            : "{\"generation\":" + endpoint.Generation.ToString(CultureInfo.InvariantCulture)
              + ",\"release_bom_sha256\":\"" + endpoint.ReleaseBomSha256
              + "\",\"status\":\"" + endpoint.Status + "\"}";

    private static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Strict codec for release.binding.receipt/v1, mirroring
/// ControlPlaneReceiptV1Codec: strict UTF-8, exact snake_case field set
/// (including the nested transition endpoints), explicit null shape for
/// from, canonical wire equality on read, exact Zulu timestamps, and a byte
/// budget. Note the wire is declaration-ordered; the payload_sha256
/// commitment separately uses the repository's sorted-key canonical JSON
/// (see ComputePayloadSha256) — two deliberately distinct profiles.
/// </summary>
public static class ReleaseBindingReceiptV1Codec
{
    public const int MaximumPayloadBytes = 16 * 1024;
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "receipt_kind",
        "device_binding_id", "from", "to", "sequence", "actor_identity",
        "occurred_at", "payload_sha256", "receipt_id"
    };
    private static readonly HashSet<string> EndpointFields = new(StringComparer.Ordinal)
    {
        "release_bom_sha256", "generation", "status"
    };

    public static byte[] Serialize(ReleaseBindingReceiptV1 value)
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
            writer.WriteString("receipt_kind", value.ReceiptKind);
            writer.WriteString("device_binding_id", value.DeviceBindingId);
            WriteEndpoint(writer, "from", value.From);
            WriteEndpoint(writer, "to", value.To);
            writer.WriteNumber("sequence", value.Sequence);
            writer.WriteString("actor_identity", value.ActorIdentity);
            writer.WriteString("occurred_at", ReleaseBindingWire.FormatWireUtc(value.OccurredAt));
            writer.WriteString("payload_sha256", value.PayloadSha256);
            writer.WriteString("receipt_id", value.ReceiptId);
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentException("Release binding receipt exceeds its byte budget.", nameof(value));
        }
        return payload;
    }

    public static ReleaseBindingReceiptV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
    {
        using var document = ReleaseBindingWire.ParseStrict(
            payloadUtf8, MaximumPayloadBytes, "release binding receipt");
        var fields = ReleaseBindingWire.ReadExactFields(
            document.RootElement, ExactFields, "release binding receipt");
        var receipt = new ReleaseBindingReceiptV1(
            ReleaseBindingWire.ReadString(fields, "schema_version"),
            ReleaseBindingWire.ReadString(fields, "contract_id"),
            ReleaseBindingWire.ReadString(fields, "producer_module"),
            ReleaseBindingWire.ReadString(fields, "receipt_kind"),
            ReleaseBindingWire.ReadString(fields, "device_binding_id"),
            ReadNullableEndpoint(fields["from"], "from"),
            ReadNullableEndpoint(fields["to"], "to")
                ?? throw new ArgumentException("Field 'to' must be a transition endpoint."),
            ReleaseBindingWire.ReadInt64(fields["sequence"], "sequence"),
            ReleaseBindingWire.ReadString(fields, "actor_identity"),
            ReleaseBindingWire.ReadWireUtc(fields, "occurred_at"),
            ReleaseBindingWire.ReadString(fields, "payload_sha256"),
            ReleaseBindingWire.ReadString(fields, "receipt_id"));
        receipt.Validate();
        var canonicalPayload = Serialize(receipt);
        try
        {
            if (!payloadUtf8.SequenceEqual(canonicalPayload))
            {
                throw new ArgumentException(
                    "Release binding receipt is not the canonical snake_case wire.",
                    nameof(payloadUtf8));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalPayload);
        }
        return receipt;
    }

    private static void WriteEndpoint(
        Utf8JsonWriter writer,
        string name,
        ReleaseBindingEndpointV1? endpoint)
    {
        if (endpoint is null)
        {
            writer.WriteNull(name);
            return;
        }
        writer.WriteStartObject(name);
        writer.WriteString("release_bom_sha256", endpoint.ReleaseBomSha256);
        writer.WriteNumber("generation", endpoint.Generation);
        writer.WriteString("status", endpoint.Status);
        writer.WriteEndObject();
    }

    private static ReleaseBindingEndpointV1? ReadNullableEndpoint(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Field '{name}' must be null or a transition endpoint.");
        }
        var fields = ReleaseBindingWire.ReadExactFields(
            value, EndpointFields, $"release binding receipt {name} endpoint");
        var endpoint = new ReleaseBindingEndpointV1(
            ReleaseBindingWire.ReadString(fields, "release_bom_sha256"),
            ReleaseBindingWire.ReadInt64(fields["generation"], "generation"),
            ReleaseBindingWire.ReadString(fields, "status"));
        endpoint.Validate();
        return endpoint;
    }
}
