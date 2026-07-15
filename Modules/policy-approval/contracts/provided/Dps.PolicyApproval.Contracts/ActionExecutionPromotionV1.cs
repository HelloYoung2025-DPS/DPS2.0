using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.PolicyApproval.Contracts;

/// <summary>
/// Independent signed authority that may promote one exact shadow-only
/// action proposal into executable policy evaluation. The proposal alone is
/// never execution authority. Policy Approval owns this input contract while
/// Control Plane Host remains its only allowed wire producer.
/// </summary>
public sealed record ActionExecutionPromotionV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("promotion_id")] Guid PromotionId,
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("release_approval_id")] Guid ReleaseApprovalId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("proposal_sha256")] string ProposalSha256,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("expected_runtime_revision")] long ExpectedRuntimeRevision,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("signature_base64")] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "action.execution.promotion/v1";
    public const string CurrentProducerModule = "control-plane-host";
    public const string CurrentAuthScope = "policy:promote";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        ApprovalContractGuard.RequireExact(
            SchemaVersion,
            CurrentSchemaVersion,
            nameof(SchemaVersion));
        ApprovalContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ApprovalContractGuard.RequireExact(
            ProducerModule,
            CurrentProducerModule,
            nameof(ProducerModule));
        ApprovalContractGuard.RequireExact(AuthScope, CurrentAuthScope, nameof(AuthScope));
        ApprovalContractGuard.RequireGuid(PromotionId, nameof(PromotionId));
        ApprovalContractGuard.RequireGuid(ProposalId, nameof(ProposalId));
        ApprovalContractGuard.RequireGuid(ReleaseApprovalId, nameof(ReleaseApprovalId));
        ApprovalContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        ApprovalContractGuard.RequireTraceId(TraceId);
        ApprovalContractGuard.RequireIdempotencyKey(IdempotencyKey);
        ApprovalContractGuard.RequireSha256(ProposalSha256, nameof(ProposalSha256));
        ApprovalContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (ExpectedRuntimeRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedRuntimeRevision));
        }
        ApprovalContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        ApprovalContractGuard.RequireUtc(ValidUntil, nameof(ValidUntil));
        if (ValidUntil <= OccurredAt || ValidUntil - OccurredAt > MaximumLifetime)
        {
            throw new ArgumentException(
                "Execution promotion validity must be positive and no longer than five minutes.",
                nameof(ValidUntil));
        }
        ApprovalContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        ApprovalContractGuard.RequireP256P1363Signature(SignatureBase64, nameof(SignatureBase64));
    }
}

public static class ActionExecutionPromotionV1Codec
{
    public const int MaximumPayloadBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "contract_id", "producer_module", "auth_scope",
        "promotion_id", "proposal_id", "release_approval_id", "soul_id",
        "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
        "proposal_sha256", "release_bom_sha256", "expected_runtime_revision",
        "occurred_at", "valid_until", "privacy_class", "signature_base64"
    };

    public static byte[] Serialize(ActionExecutionPromotionV1 value)
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
            writer.WriteString("auth_scope", value.AuthScope);
            writer.WriteString("promotion_id", value.PromotionId.ToString("D"));
            writer.WriteString("proposal_id", value.ProposalId.ToString("D"));
            writer.WriteString("release_approval_id", value.ReleaseApprovalId.ToString("D"));
            writer.WriteString("soul_id", value.SoulId);
            writer.WriteString("device_binding_id", value.DeviceBindingId);
            writer.WriteString("platform_account_id", value.PlatformAccountId);
            writer.WriteString("trace_id", value.TraceId);
            writer.WriteString("idempotency_key", value.IdempotencyKey);
            writer.WriteString("proposal_sha256", value.ProposalSha256);
            writer.WriteString("release_bom_sha256", value.ReleaseBomSha256);
            writer.WriteNumber("expected_runtime_revision", value.ExpectedRuntimeRevision);
            writer.WriteString("occurred_at", FormatWireUtc(value.OccurredAt));
            writer.WriteString("valid_until", FormatWireUtc(value.ValidUntil));
            writer.WriteString("privacy_class", value.PrivacyClass);
            writer.WriteString("signature_base64", value.SignatureBase64);
            writer.WriteEndObject();
        }
        var payload = stream.ToArray();
        if (payload.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new ArgumentException(
                "Execution promotion payload exceeds its byte budget.",
                nameof(value));
        }
        return payload;
    }

    public static ActionExecutionPromotionV1 Deserialize(ReadOnlySpan<byte> payloadUtf8)
    {
        if (payloadUtf8.Length is < 2 or > MaximumPayloadBytes)
        {
            throw new ArgumentException(
                "Execution promotion payload is outside its byte budget.",
                nameof(payloadUtf8));
        }
        try
        {
            _ = StrictUtf8.GetCharCount(payloadUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                "Execution promotion payload is not strict UTF-8.",
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
            throw new ArgumentException("Execution promotion payload must be one JSON object.");
        }
        var fields = ReadExactFields(document.RootElement);
        var promotion = new ActionExecutionPromotionV1(
            ReadString(fields, "schema_version"),
            ReadString(fields, "contract_id"),
            ReadString(fields, "producer_module"),
            ReadString(fields, "auth_scope"),
            ReadAbsoluteGuid(fields, "promotion_id"),
            ReadAbsoluteGuid(fields, "proposal_id"),
            ReadAbsoluteGuid(fields, "release_approval_id"),
            ReadString(fields, "soul_id"),
            ReadString(fields, "device_binding_id"),
            ReadString(fields, "platform_account_id"),
            ReadString(fields, "trace_id"),
            ReadString(fields, "idempotency_key"),
            ReadString(fields, "proposal_sha256"),
            ReadString(fields, "release_bom_sha256"),
            ReadPositiveInt64(fields, "expected_runtime_revision"),
            ReadWireUtc(fields, "occurred_at"),
            ReadWireUtc(fields, "valid_until"),
            ReadString(fields, "privacy_class"),
            ReadString(fields, "signature_base64"));
        promotion.Validate();
        var canonicalPayload = Serialize(promotion);
        try
        {
            if (!payloadUtf8.SequenceEqual(canonicalPayload))
            {
                throw new ArgumentException(
                    "Execution promotion payload is not the canonical snake_case wire.",
                    nameof(payloadUtf8));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalPayload);
        }
        return promotion;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadExactFields(JsonElement root)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExactFields.Contains(property.Name))
            {
                throw new ArgumentException(
                    $"Unknown execution promotion field '{property.Name}'.");
            }
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new ArgumentException(
                    $"Duplicate execution promotion field '{property.Name}'.");
            }
        }
        if (!ExactFields.SetEquals(fields.Keys))
        {
            throw new ArgumentException("Execution promotion payload has missing fields.");
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
            throw new ArgumentException(
                $"Execution promotion field '{name}' must be a string.");
        }
        return value.GetString()
            ?? throw new ArgumentException($"Execution promotion field '{name}' is null.");
    }

    private static Guid ReadAbsoluteGuid(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = ReadString(fields, name);
        if (value.Length != 36
            || !Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Execution promotion field '{name}' is not an absolute UUID.");
        }
        return parsed;
    }

    private static long ReadPositiveInt64(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = fields[name];
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed < 1)
        {
            throw new ArgumentException(
                $"Execution promotion field '{name}' must be a positive integer.");
        }
        return parsed;
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
                $"Execution promotion field '{name}' must be exact Zulu UTC.");
        }
        return parsed;
    }

    private static string FormatWireUtc(DateTimeOffset value)
    {
        ApprovalContractGuard.RequireUtc(value, nameof(value));
        return value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }
}

public static class ActionExecutionPromotionV1Canonical
{
    public const string PayloadDomain = "dps.policy-approval.action-execution-promotion/v1";
    public const string SignedDomain =
        "dps.policy-approval.action-execution-promotion-signed-sha256/v1";

    public static byte[] CanonicalBytes(ActionExecutionPromotionV1 promotion)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        promotion.Validate();
        using var writer = new CanonicalWriter();
        writer.Field(PayloadDomain);
        writer.Field(promotion.SchemaVersion);
        writer.Field(promotion.ContractId);
        writer.Field(promotion.ProducerModule);
        writer.Field(promotion.AuthScope);
        writer.Field(promotion.PromotionId);
        writer.Field(promotion.ProposalId);
        writer.Field(promotion.ReleaseApprovalId);
        writer.Field(promotion.SoulId);
        writer.Field(promotion.DeviceBindingId);
        writer.Field(promotion.PlatformAccountId);
        writer.Field(promotion.TraceId);
        writer.Field(promotion.IdempotencyKey);
        writer.Field(promotion.ProposalSha256);
        writer.Field(promotion.ReleaseBomSha256);
        writer.Field(promotion.ExpectedRuntimeRevision);
        writer.Field(promotion.OccurredAt);
        writer.Field(promotion.ValidUntil);
        writer.Field(promotion.PrivacyClass);
        return writer.ToArray();
    }

    public static string ComputeSignedSha256(ActionExecutionPromotionV1 promotion)
    {
        var canonical = CanonicalBytes(promotion);
        try
        {
            using var writer = new CanonicalWriter();
            writer.Field(SignedDomain);
            writer.Field(canonical);
            writer.Field(promotion.SignatureBase64);
            var signed = writer.ToArray();
            try
            {
                return Convert.ToHexStringLower(SHA256.HashData(signed));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private sealed class CanonicalWriter : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly MemoryStream _stream = new();

        internal void Field(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = StrictUtf8.GetBytes(value);
            try
            {
                Field(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        internal void Field(Guid value) => Field(value.ToString("N"));

        internal void Field(long value) =>
            Field(value.ToString(CultureInfo.InvariantCulture));

        internal void Field(DateTimeOffset value) =>
            Field(value.ToString("O", CultureInfo.InvariantCulture));

        internal void Field(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
            _stream.Write(length);
            _stream.Write(value);
        }

        internal byte[] ToArray() => _stream.ToArray();

        public void Dispose() => _stream.Dispose();
    }
}
