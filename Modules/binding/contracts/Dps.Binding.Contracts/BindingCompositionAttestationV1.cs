using System.Text.Json.Serialization;

namespace Dps.Binding.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignedBindingCompositionAttestationV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string? SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string? DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string? PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("root_key_id"), JsonRequired] string RootKeyId,
    [property: JsonPropertyName("release_bom_sha256"), JsonRequired] string ReleaseBomSha256,
    [property: JsonPropertyName("generation"), JsonRequired] long Generation,
    [property: JsonPropertyName("issued_at"), JsonRequired] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at"), JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("binding_instance_configuration_sha256"), JsonRequired] string BindingInstanceConfigurationSha256,
    [property: JsonPropertyName("binding_instance_trust_epoch"), JsonRequired] long BindingInstanceTrustEpoch,
    [property: JsonPropertyName("binding_artifact_sha256"), JsonRequired] string BindingArtifactSha256,
    [property: JsonPropertyName("binding_contracts_artifact_sha256"), JsonRequired] string BindingContractsArtifactSha256,
    [property: JsonPropertyName("composition_host_artifact_sha256"), JsonRequired] string CompositionHostArtifactSha256,
    [property: JsonPropertyName("device_registry_instance_configuration_sha256"), JsonRequired] string DeviceRegistryInstanceConfigurationSha256,
    [property: JsonPropertyName("device_registry_instance_trust_epoch"), JsonRequired] long DeviceRegistryInstanceTrustEpoch,
    [property: JsonPropertyName("device_registry_artifact_sha256"), JsonRequired] string DeviceRegistryArtifactSha256,
    [property: JsonPropertyName("device_registry_contracts_artifact_sha256"), JsonRequired] string DeviceRegistryContractsArtifactSha256,
    [property: JsonPropertyName("platform_account_registry_instance_configuration_sha256"), JsonRequired] string PlatformAccountRegistryInstanceConfigurationSha256,
    [property: JsonPropertyName("platform_account_registry_instance_trust_epoch"), JsonRequired] long PlatformAccountRegistryInstanceTrustEpoch,
    [property: JsonPropertyName("platform_account_registry_artifact_sha256"), JsonRequired] string PlatformAccountRegistryArtifactSha256,
    [property: JsonPropertyName("platform_account_registry_contracts_artifact_sha256"), JsonRequired] string PlatformAccountRegistryContractsArtifactSha256,
    [property: JsonPropertyName("signature_base64"), JsonRequired] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "binding.composition.attestation/v1";
    public const string CurrentProducerModule = "binding";
    public const string CurrentRootKeyId = "dps-binding-composition-root-2026-07";

    public void ValidateShape()
    {
        BindingContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        BindingContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        BindingContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        if (SoulId is not null || DeviceBindingId is not null || PlatformAccountId is not null)
            throw new ArgumentException("A Release BOM composition attestation is global and cannot carry Soul, device, or account scope.");
        BindingContractValidation.RequireTraceId(TraceId);
        BindingContractValidation.RequireIdempotencyKey(IdempotencyKey);
        BindingContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        BindingContractValidation.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        BindingContractValidation.RequireExact(RootKeyId, CurrentRootKeyId, nameof(RootKeyId));
        BindingContractValidation.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        BindingContractValidation.RequireUtc(IssuedAt, nameof(IssuedAt));
        BindingContractValidation.RequireUtc(ExpiresAt, nameof(ExpiresAt));
        if (OccurredAt != IssuedAt) throw new ArgumentException("OccurredAt must equal the signed issuance time.", nameof(OccurredAt));
        if (ExpiresAt <= IssuedAt) throw new ArgumentException("The composition attestation must expire after issuance.", nameof(ExpiresAt));
        BindingContractValidation.RequireSha256(BindingInstanceConfigurationSha256, nameof(BindingInstanceConfigurationSha256));
        if (BindingInstanceTrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(BindingInstanceTrustEpoch));
        BindingContractValidation.RequireSha256(BindingArtifactSha256, nameof(BindingArtifactSha256));
        BindingContractValidation.RequireSha256(BindingContractsArtifactSha256, nameof(BindingContractsArtifactSha256));
        BindingContractValidation.RequireSha256(CompositionHostArtifactSha256, nameof(CompositionHostArtifactSha256));
        BindingContractValidation.RequireSha256(DeviceRegistryInstanceConfigurationSha256, nameof(DeviceRegistryInstanceConfigurationSha256));
        if (DeviceRegistryInstanceTrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(DeviceRegistryInstanceTrustEpoch));
        BindingContractValidation.RequireSha256(DeviceRegistryArtifactSha256, nameof(DeviceRegistryArtifactSha256));
        BindingContractValidation.RequireSha256(DeviceRegistryContractsArtifactSha256, nameof(DeviceRegistryContractsArtifactSha256));
        BindingContractValidation.RequireSha256(PlatformAccountRegistryInstanceConfigurationSha256, nameof(PlatformAccountRegistryInstanceConfigurationSha256));
        if (PlatformAccountRegistryInstanceTrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(PlatformAccountRegistryInstanceTrustEpoch));
        BindingContractValidation.RequireSha256(PlatformAccountRegistryArtifactSha256, nameof(PlatformAccountRegistryArtifactSha256));
        BindingContractValidation.RequireSha256(PlatformAccountRegistryContractsArtifactSha256, nameof(PlatformAccountRegistryContractsArtifactSha256));
        BindingContractValidation.RequireText(SignatureBase64, 128, nameof(SignatureBase64));
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(SignatureBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("SignatureBase64 must be canonical Base64.", nameof(SignatureBase64), exception);
        }
        if (signature.Length != 64 || !string.Equals(Convert.ToBase64String(signature), SignatureBase64, StringComparison.Ordinal))
            throw new ArgumentException("SignatureBase64 must be one canonical P-256 P1363 signature.", nameof(SignatureBase64));
    }
}
