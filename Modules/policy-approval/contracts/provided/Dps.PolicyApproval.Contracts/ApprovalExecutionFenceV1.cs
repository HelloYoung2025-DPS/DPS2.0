using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.PolicyApproval.Contracts;

public sealed record ApprovalExecutionFenceRequestV1(
    string SchemaVersion,
    string ContractId,
    string ConsumerModule,
    Guid ApprovalId,
    Guid ProposalId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string ApprovalSha256,
    long ExpectedStatusRevision,
    long ExpectedRuntimeRevision,
    string ExpectedRuntimeStateSha256,
    string ExpectedReleaseBomSha256)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.execution.fence.request/v1";
    public const string CurrentConsumerModule = "executor-gateway";

    public void Validate()
    {
        ApprovalContractGuard.RequireMajor(SchemaVersion, 1);
        ApprovalContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ApprovalContractGuard.RequireExact(ConsumerModule, CurrentConsumerModule, nameof(ConsumerModule));
        ApprovalContractGuard.RequireGuid(ApprovalId, nameof(ApprovalId));
        ApprovalContractGuard.RequireGuid(ProposalId, nameof(ProposalId));
        ApprovalContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        ApprovalContractGuard.RequireTraceId(TraceId);
        ApprovalContractGuard.RequireIdempotencyKey(IdempotencyKey);
        ApprovalContractGuard.RequireSha256(ApprovalSha256, nameof(ApprovalSha256));
        if (ExpectedStatusRevision <= 0) throw new ArgumentOutOfRangeException(nameof(ExpectedStatusRevision));
        if (ExpectedRuntimeRevision <= 0) throw new ArgumentOutOfRangeException(nameof(ExpectedRuntimeRevision));
        ApprovalContractGuard.RequireSha256(ExpectedRuntimeStateSha256, nameof(ExpectedRuntimeStateSha256));
        ApprovalContractGuard.RequireSha256(ExpectedReleaseBomSha256, nameof(ExpectedReleaseBomSha256));
    }
}

public sealed record ApprovalExecutionFenceAuthorizationV1(
    string CallerModule,
    string AuthScope,
    string FenceRequestSha256,
    string ReleaseBomSha256,
    DateTimeOffset ValidUntil,
    string SignatureBase64)
{
    public const string CurrentCallerModule = "control-plane-host";
    public const string CurrentAuthScope = "approval:fence";

    public void Validate()
    {
        ApprovalContractGuard.RequireExact(CallerModule, CurrentCallerModule, nameof(CallerModule));
        ApprovalContractGuard.RequireExact(AuthScope, CurrentAuthScope, nameof(AuthScope));
        ApprovalContractGuard.RequireSha256(FenceRequestSha256, nameof(FenceRequestSha256));
        ApprovalContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        ApprovalContractGuard.RequireUtc(ValidUntil, nameof(ValidUntil));
        ApprovalContractGuard.RequireP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }
}

public sealed record ApprovalExecutionFenceV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("fence_id")] Guid FenceId,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("approval_sha256")] string ApprovalSha256,
    [property: JsonPropertyName("status_revision")] long StatusRevision,
    [property: JsonPropertyName("runtime_revision")] long RuntimeRevision,
    [property: JsonPropertyName("runtime_state_sha256")] string RuntimeStateSha256,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset AcquiredAt,
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "approval.execution.fence/v1";
    public const string CurrentProducerModule = "policy-approval";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        ApprovalContractGuard.RequireExact(
            SchemaVersion,
            CurrentSchemaVersion,
            nameof(SchemaVersion));
        ApprovalContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ApprovalContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ApprovalContractGuard.RequireGuid(FenceId, nameof(FenceId));
        ApprovalContractGuard.RequireGuid(ApprovalId, nameof(ApprovalId));
        ApprovalContractGuard.RequireGuid(ProposalId, nameof(ProposalId));
        ApprovalContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        ApprovalContractGuard.RequireTraceId(TraceId);
        ApprovalContractGuard.RequireIdempotencyKey(IdempotencyKey);
        ApprovalContractGuard.RequireSha256(ApprovalSha256, nameof(ApprovalSha256));
        if (StatusRevision <= 0) throw new ArgumentOutOfRangeException(nameof(StatusRevision));
        if (RuntimeRevision <= 0) throw new ArgumentOutOfRangeException(nameof(RuntimeRevision));
        ApprovalContractGuard.RequireSha256(RuntimeStateSha256, nameof(RuntimeStateSha256));
        ApprovalContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        ApprovalContractGuard.RequireUtc(AcquiredAt, nameof(AcquiredAt));
        ApprovalContractGuard.RequireUtc(ValidUntil, nameof(ValidUntil));
        if (ValidUntil <= AcquiredAt || ValidUntil - AcquiredAt > MaximumLifetime)
            throw new ArgumentException(
                "Fence validity must be positive and no longer than two seconds.",
                nameof(ValidUntil));
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
    }
}

public static class ApprovalExecutionFenceV1Codec
{
    public const int MaximumPayloadBytes = 16 * 1024;
    private const string ContractName = "approval.execution.fence/v1";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "fence_id",
        "approval_id", "proposal_id", "soul_id", "device_binding_id",
        "platform_account_id", "trace_id", "idempotency_key", "approval_sha256",
        "status_revision", "runtime_revision", "runtime_state_sha256",
        "release_bom_sha256", "occurred_at", "valid_until", "privacy_class"
    };

    public static byte[] Serialize(ApprovalExecutionFenceV1 value)
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
            writer.WriteString("fence_id", value.FenceId.ToString("D"));
            writer.WriteString("approval_id", value.ApprovalId.ToString("D"));
            writer.WriteString("proposal_id", value.ProposalId.ToString("D"));
            writer.WriteString("soul_id", value.SoulId);
            writer.WriteString("device_binding_id", value.DeviceBindingId);
            writer.WriteString("platform_account_id", value.PlatformAccountId);
            writer.WriteString("trace_id", value.TraceId);
            writer.WriteString("idempotency_key", value.IdempotencyKey);
            writer.WriteString("approval_sha256", value.ApprovalSha256);
            writer.WriteNumber("status_revision", value.StatusRevision);
            writer.WriteNumber("runtime_revision", value.RuntimeRevision);
            writer.WriteString("runtime_state_sha256", value.RuntimeStateSha256);
            writer.WriteString("release_bom_sha256", value.ReleaseBomSha256);
            writer.WriteString(
                "occurred_at",
                PolicyApprovalContractJson.FormatCanonicalUtc(value.AcquiredAt));
            writer.WriteString(
                "valid_until",
                PolicyApprovalContractJson.FormatCanonicalUtc(value.ValidUntil));
            writer.WriteString("privacy_class", value.PrivacyClass);
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentException(
                "Approval execution fence payload exceeds its byte budget.",
                nameof(value));
        }
        return payload;
    }

    public static ApprovalExecutionFenceV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
    {
        PolicyApprovalContractJson.RequirePayload(
            payloadUtf8,
            MaximumPayloadBytes,
            ContractName);
        using var document = JsonDocument.Parse(
            payloadUtf8.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
        var fields = PolicyApprovalContractJson.ReadExactFields(
            document.RootElement,
            ExactFields,
            ExactFields,
            ContractName);
        var value = new ApprovalExecutionFenceV1(
            PolicyApprovalContractJson.ReadString(fields, "schema_version", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "contract_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "producer_module", ContractName),
            PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "fence_id", ContractName),
            PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "approval_id", ContractName),
            PolicyApprovalContractJson.ReadAbsoluteGuid(fields, "proposal_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "soul_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "device_binding_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "platform_account_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "trace_id", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "idempotency_key", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "approval_sha256", ContractName),
            PolicyApprovalContractJson.ReadPositiveInt64(fields, "status_revision", ContractName),
            PolicyApprovalContractJson.ReadPositiveInt64(fields, "runtime_revision", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "runtime_state_sha256", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "release_bom_sha256", ContractName),
            PolicyApprovalContractJson.ReadCanonicalUtc(fields, "occurred_at", ContractName),
            PolicyApprovalContractJson.ReadCanonicalUtc(fields, "valid_until", ContractName),
            PolicyApprovalContractJson.ReadString(fields, "privacy_class", ContractName));
        value.Validate();
        PolicyApprovalContractJson.RequireCanonicalWire(
            payloadUtf8,
            Serialize(value),
            ContractName);
        return value;
    }
}
