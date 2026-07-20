using System.Text.Json.Serialization;

namespace Dps.ControlPlaneHost.Contracts;

/// <summary>
/// Per-device active Release BOM runtime truth owned by control-plane-host.
/// Mirrors contracts/provided/active.release.binding.v1.schema.json.
/// </summary>
public sealed record ActiveReleaseBindingV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("execution_token")] string ExecutionToken,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("signer_identity")] string SignerIdentity,
    [property: JsonPropertyName("signer_key_id")] string SignerKeyId,
    [property: JsonPropertyName("bom_signature_sha256")] string BomSignatureSha256,
    [property: JsonPropertyName("activated_at")] DateTimeOffset ActivatedAt,
    [property: JsonPropertyName("receipt_id")] string ReceiptId)
{
    public void Validate()
    {
        ControlContractValidation.RequireMajor(SchemaVersion, 1);
        ControlContractValidation.RequireExact(ContractId, "active.release.binding/v1", nameof(ContractId));
        ControlContractValidation.RequireExact(ProducerModule, "control-plane-host", nameof(ProducerModule));
        ControlContractValidation.RequireDeviceBindingId(DeviceBindingId);
        ControlContractValidation.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        ReleaseBindingValidation.RequirePositive(Generation, nameof(Generation));
        ControlContractValidation.RequireSha256(ExecutionToken, nameof(ExecutionToken));
        ReleaseBindingValidation.RequireStatus(Status);
        ReleaseBindingValidation.RequireIdentity(SignerIdentity, nameof(SignerIdentity));
        ReleaseBindingValidation.RequireIdentity(SignerKeyId, nameof(SignerKeyId));
        ControlContractValidation.RequireSha256(BomSignatureSha256, nameof(BomSignatureSha256));
        ControlContractValidation.RequireUtc(ActivatedAt, nameof(ActivatedAt));
        ControlContractValidation.RequireHex(ReceiptId, "receipt_", 32, nameof(ReceiptId));
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
