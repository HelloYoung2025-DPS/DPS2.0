using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// field on the wire).
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
        ControlContractValidation.RequireHex(ReceiptId, "receipt_", 32, nameof(ReceiptId));
        switch (ReceiptKind)
        {
            case "activation" when From is null or { Status: "previous" } && To.Status == "active":
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
