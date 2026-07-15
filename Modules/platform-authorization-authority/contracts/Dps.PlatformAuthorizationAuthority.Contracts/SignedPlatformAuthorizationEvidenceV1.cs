using System.Text.Json.Serialization;

namespace Dps.PlatformAuthorizationAuthority.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignedPlatformAuthorizationEvidenceV1(
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
    [property: JsonPropertyName("authorization_evidence_id"), JsonRequired] string AuthorizationEvidenceId,
    [property: JsonPropertyName("platform"), JsonRequired] string Platform,
    [property: JsonPropertyName("alias_digest"), JsonRequired] string AliasDigest,
    [property: JsonPropertyName("alias_key_id"), JsonRequired] string AliasKeyId,
    [property: JsonPropertyName("alias_key_epoch"), JsonRequired] long AliasKeyEpoch,
    [property: JsonPropertyName("target_status"), JsonRequired] string TargetStatus,
    [property: JsonPropertyName("authorization_revision"), JsonRequired] long AuthorizationRevision,
    [property: JsonPropertyName("issuer_id"), JsonRequired] string IssuerId,
    [property: JsonPropertyName("issuer_key_id"), JsonRequired] string IssuerKeyId,
    [property: JsonPropertyName("release_bom_sha256"), JsonRequired] string ReleaseBomSha256,
    [property: JsonPropertyName("release_generation"), JsonRequired] long ReleaseGeneration,
    [property: JsonPropertyName("issued_at"), JsonRequired] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at"), JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("signature_base64"), JsonRequired] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "platform.account.authorization.evidence/v1";
    public const string CurrentProducerModule = "platform-authorization-authority";
    public const string CurrentIssuerId = PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerId;
    public const string CurrentIssuerKeyId = PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId;

    public void Validate()
    {
        ValidateUnsignedFields();
        PlatformAuthorizationContractValidation.RequireCanonicalP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }

    public void ValidateUnsignedFields()
    {
        PlatformAuthorizationContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        PlatformAuthorizationContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        PlatformAuthorizationContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        PlatformAuthorizationContractValidation.RequireSoulId(SoulId);
        PlatformAuthorizationContractValidation.RequireDeviceBindingId(DeviceBindingId);
        PlatformAuthorizationContractValidation.RequirePlatformAccountId(PlatformAccountId);
        PlatformAuthorizationContractValidation.RequireTraceId(TraceId);
        PlatformAuthorizationContractValidation.RequireIdempotencyKey(IdempotencyKey);
        PlatformAuthorizationContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        PlatformAuthorizationContractValidation.RequireExact(PrivacyClass, "sensitive", nameof(PrivacyClass));
        PlatformAuthorizationContractValidation.RequireAuthorizationEvidenceId(AuthorizationEvidenceId);
        PlatformAuthorizationContractValidation.RequireIdentifier(Platform, nameof(Platform));
        PlatformAuthorizationContractValidation.RequireSha256(AliasDigest, nameof(AliasDigest));
        PlatformAuthorizationContractValidation.RequireKeyId(AliasKeyId, nameof(AliasKeyId));
        if (AliasKeyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(AliasKeyEpoch));
        PlatformAuthorizationContractValidation.RequireStatus(TargetStatus, nameof(TargetStatus));
        if (AuthorizationRevision < 1) throw new ArgumentOutOfRangeException(nameof(AuthorizationRevision));
        PlatformAuthorizationContractValidation.RequireExact(IssuerId, CurrentIssuerId, nameof(IssuerId));
        PlatformAuthorizationContractValidation.RequireExact(IssuerKeyId, CurrentIssuerKeyId, nameof(IssuerKeyId));
        PlatformAuthorizationContractValidation.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (ReleaseGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ReleaseGeneration));
        PlatformAuthorizationContractValidation.RequireUtc(IssuedAt, nameof(IssuedAt));
        PlatformAuthorizationContractValidation.RequireUtc(ExpiresAt, nameof(ExpiresAt));
        if (IssuedAt > OccurredAt || OccurredAt > ExpiresAt)
            throw new ArgumentException("occurred_at must be inside the signed evidence validity window.");
        if (ExpiresAt <= IssuedAt || ExpiresAt - IssuedAt > TimeSpan.FromMinutes(15))
            throw new ArgumentException("Signed authorization evidence must expire within 15 minutes.", nameof(ExpiresAt));
    }
}
