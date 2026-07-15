using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dps.MemoryEventLedger;

internal sealed record ConsumedCommandReceiptV1(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    Guid ReceiptId,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string PrivacyClass,
    string Outcome,
    Guid? NativeResultId,
    bool NativeResultVerified,
    bool PostconditionVerified,
    string EvidenceDigest,
    bool RetryAllowed,
    string ResultCode);

internal sealed record ConsumedSignedCommandReceiptV1(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    string SignatureDomain,
    string CanonicalEncoding,
    string ReceiptDigestAlgorithm,
    string CommandDigestAlgorithm,
    string EvidenceDigestAlgorithm,
    string SignatureAlgorithm,
    string SignatureFormat,
    string SignatureEncoding,
    string SignerModule,
    string AuthScope,
    Guid ReceiptId,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string PrivacyClass,
    string ReceiptSha256,
    string CommandSha256,
    string AuthorizationSha256,
    string ReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomTokenSha256,
    string? NativeEvidenceSha256,
    string? PostconditionEvidenceSha256,
    ConsumedCommandReceiptV1 Receipt,
    string SignatureBase64)
{
    private const int MaximumRawBytes = 32_768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] EnvelopeProperties =
    [
        "schema_version", "contract_id", "producer_module", "signature_domain", "canonical_encoding",
        "receipt_digest_algorithm", "command_digest_algorithm", "evidence_digest_algorithm", "signature_algorithm",
        "signature_format", "signature_encoding", "signer_module", "auth_scope", "receipt_id", "command_id",
        "lease_id", "attempt", "soul_id", "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
        "occurred_at", "privacy_class", "receipt_sha256", "command_sha256", "authorization_sha256",
        "release_bom_sha256", "active_release_bom_generation", "active_release_bom_token_sha256",
        "native_evidence_sha256", "postcondition_evidence_sha256", "receipt", "signature_base64"
    ];
    private static readonly string[] ReceiptProperties =
    [
        "schema_version", "contract_id", "producer_module", "receipt_id", "command_id", "lease_id", "attempt",
        "soul_id", "device_binding_id", "platform_account_id", "trace_id", "idempotency_key", "occurred_at",
        "privacy_class", "outcome", "native_result_id", "native_result_verified", "postcondition_verified",
        "evidence_digest", "retry_allowed", "result_code"
    ];

    internal static ConsumedSignedCommandReceiptV1 ParseExact(ReadOnlySpan<byte> raw)
    {
        if (raw.Length is 0 or > MaximumRawBytes) throw new JsonException("Signed receipt bytes are empty or exceed 32768 bytes.");
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.ValidateJsonShape(raw, 6, 96);
        using var document = JsonDocument.Parse(raw.ToArray(), new JsonDocumentOptions { MaxDepth = 6 });
        var root = document.RootElement;
        RequireExactProperties(root, EnvelopeProperties, "signed receipt");
        var receiptElement = root.GetProperty("receipt");
        RequireExactProperties(receiptElement, ReceiptProperties, "receipt payload");

        var receipt = new ConsumedCommandReceiptV1(
            Text(receiptElement, "schema_version", 32), Text(receiptElement, "contract_id", 64),
            Text(receiptElement, "producer_module", 64), GuidValue(receiptElement, "receipt_id"),
            GuidValue(receiptElement, "command_id"), GuidValue(receiptElement, "lease_id"),
            IntValue(receiptElement, "attempt", 1, 3), Text(receiptElement, "soul_id", 69),
            Text(receiptElement, "device_binding_id", 35), Text(receiptElement, "platform_account_id", 35),
            Text(receiptElement, "trace_id", 38), Text(receiptElement, "idempotency_key", 69),
            Utc(receiptElement, "occurred_at"), Text(receiptElement, "privacy_class", 16),
            Text(receiptElement, "outcome", 32), NullableGuid(receiptElement, "native_result_id"),
            Bool(receiptElement, "native_result_verified"), Bool(receiptElement, "postcondition_verified"),
            Text(receiptElement, "evidence_digest", 64), Bool(receiptElement, "retry_allowed"),
            Text(receiptElement, "result_code", 128));

        var value = new ConsumedSignedCommandReceiptV1(
            Text(root, "schema_version", 32), Text(root, "contract_id", 64), Text(root, "producer_module", 64),
            Text(root, "signature_domain", 128), Text(root, "canonical_encoding", 128),
            Text(root, "receipt_digest_algorithm", 128), Text(root, "command_digest_algorithm", 128),
            Text(root, "evidence_digest_algorithm", 128), Text(root, "signature_algorithm", 64),
            Text(root, "signature_format", 128), Text(root, "signature_encoding", 32), Text(root, "signer_module", 64),
            Text(root, "auth_scope", 128), GuidValue(root, "receipt_id"), GuidValue(root, "command_id"),
            GuidValue(root, "lease_id"), IntValue(root, "attempt", 1, 3), Text(root, "soul_id", 69),
            Text(root, "device_binding_id", 35), Text(root, "platform_account_id", 35), Text(root, "trace_id", 38),
            Text(root, "idempotency_key", 69), Utc(root, "occurred_at"), Text(root, "privacy_class", 16),
            Text(root, "receipt_sha256", 64), Text(root, "command_sha256", 64), Text(root, "authorization_sha256", 64),
            Text(root, "release_bom_sha256", 64), LongValue(root, "active_release_bom_generation", 1),
            Text(root, "active_release_bom_token_sha256", 64), NullableText(root, "native_evidence_sha256", 64),
            NullableText(root, "postcondition_evidence_sha256", 64), receipt, Text(root, "signature_base64", 88));
        value.Validate();
        var canonicalRaw = value.CanonicalJson();
        try
        {
            if (!raw.SequenceEqual(canonicalRaw)) throw new JsonException("Signed receipt bytes are not the exact bounded canonical JSON encoding.");
        }
        finally { CryptographicOperations.ZeroMemory(canonicalRaw); }
        return value;
    }

    internal byte[] CanonicalSignaturePayload()
    {
        Validate();
        return Encode(writer =>
        {
            writer.Token(SignatureDomain);
            writer.Field("schema_version", SchemaVersion); writer.Field("contract_id", ContractId); writer.Field("producer_module", ProducerModule);
            writer.Field("signature_domain", SignatureDomain); writer.Field("canonical_encoding", CanonicalEncoding);
            writer.Field("receipt_digest_algorithm", ReceiptDigestAlgorithm); writer.Field("command_digest_algorithm", CommandDigestAlgorithm);
            writer.Field("evidence_digest_algorithm", EvidenceDigestAlgorithm); writer.Field("signature_algorithm", SignatureAlgorithm);
            writer.Field("signature_format", SignatureFormat); writer.Field("signature_encoding", SignatureEncoding);
            writer.Field("signer_module", SignerModule); writer.Field("auth_scope", AuthScope); writer.Field("receipt_id", ReceiptId);
            writer.Field("command_id", CommandId); writer.Field("lease_id", LeaseId); writer.Field("attempt", Attempt);
            writer.Field("soul_id", SoulId); writer.Field("device_binding_id", DeviceBindingId); writer.Field("platform_account_id", PlatformAccountId);
            writer.Field("trace_id", TraceId); writer.Field("idempotency_key", IdempotencyKey); writer.Field("occurred_at", OccurredAt);
            writer.Field("privacy_class", PrivacyClass); writer.Field("receipt_sha256", ReceiptSha256); writer.Field("command_sha256", CommandSha256);
            writer.Field("authorization_sha256", AuthorizationSha256); writer.Field("release_bom_sha256", ReleaseBomSha256);
            writer.Field("active_release_bom_generation", ActiveReleaseBomGeneration); writer.Field("active_release_bom_token_sha256", ActiveReleaseBomTokenSha256);
            writer.NullableField("native_evidence_sha256", NativeEvidenceSha256); writer.NullableField("postcondition_evidence_sha256", PostconditionEvidenceSha256);
        });
    }

    internal byte[] CanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            Write(writer, "schema_version", SchemaVersion); Write(writer, "contract_id", ContractId); Write(writer, "producer_module", ProducerModule);
            Write(writer, "signature_domain", SignatureDomain); Write(writer, "canonical_encoding", CanonicalEncoding);
            Write(writer, "receipt_digest_algorithm", ReceiptDigestAlgorithm); Write(writer, "command_digest_algorithm", CommandDigestAlgorithm);
            Write(writer, "evidence_digest_algorithm", EvidenceDigestAlgorithm); Write(writer, "signature_algorithm", SignatureAlgorithm);
            Write(writer, "signature_format", SignatureFormat); Write(writer, "signature_encoding", SignatureEncoding); Write(writer, "signer_module", SignerModule);
            Write(writer, "auth_scope", AuthScope); writer.WriteString("receipt_id", ReceiptId); writer.WriteString("command_id", CommandId); writer.WriteString("lease_id", LeaseId);
            writer.WriteNumber("attempt", Attempt); Write(writer, "soul_id", SoulId); Write(writer, "device_binding_id", DeviceBindingId); Write(writer, "platform_account_id", PlatformAccountId);
            Write(writer, "trace_id", TraceId); Write(writer, "idempotency_key", IdempotencyKey); WriteUtc(writer, "occurred_at", OccurredAt); Write(writer, "privacy_class", PrivacyClass);
            Write(writer, "receipt_sha256", ReceiptSha256); Write(writer, "command_sha256", CommandSha256); Write(writer, "authorization_sha256", AuthorizationSha256);
            Write(writer, "release_bom_sha256", ReleaseBomSha256); writer.WriteNumber("active_release_bom_generation", ActiveReleaseBomGeneration);
            Write(writer, "active_release_bom_token_sha256", ActiveReleaseBomTokenSha256); WriteNullable(writer, "native_evidence_sha256", NativeEvidenceSha256);
            WriteNullable(writer, "postcondition_evidence_sha256", PostconditionEvidenceSha256);
            writer.WritePropertyName("receipt"); WriteReceipt(writer, Receipt); Write(writer, "signature_base64", SignatureBase64); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private void Validate()
    {
        Exact(SchemaVersion, "1.0.0"); Exact(ContractId, "command.receipt.signed/v1"); Exact(ProducerModule, "executor-gateway");
        Exact(SignatureDomain, "dps.executor-gateway.command-receipt/v1"); Exact(CanonicalEncoding, "dps.canonical.uint32be-length-prefixed-utf8/v1");
        Exact(ReceiptDigestAlgorithm, "sha256:dps.command-orchestrator.command-receipt/v1");
        Exact(CommandDigestAlgorithm, "sha256:dps.command-orchestrator.command-dispatch/v1");
        Exact(EvidenceDigestAlgorithm, "sha256:dps.command-orchestrator.command-receipt-evidence/v1");
        Exact(SignatureAlgorithm, "ecdsa-p256-sha256"); Exact(SignatureFormat, "ieee-p1363-fixed-field-concatenation");
        Exact(SignatureEncoding, "base64"); Exact(SignerModule, "executor-gateway"); Exact(AuthScope, "command-orchestrator:receipt");
        Exact(PrivacyClass, "internal"); Exact(Receipt.SchemaVersion, "1.0.0"); Exact(Receipt.ContractId, "command.receipt/v1");
        Exact(Receipt.ProducerModule, "executor-gateway"); Exact(Receipt.PrivacyClass, "internal");
        RequireScope();
        foreach (var digest in new[] { ReceiptSha256, CommandSha256, AuthorizationSha256, ReleaseBomSha256, ActiveReleaseBomTokenSha256, Receipt.EvidenceDigest }) Digest(digest);
        if (NativeEvidenceSha256 is not null) Digest(NativeEvidenceSha256); if (PostconditionEvidenceSha256 is not null) Digest(PostconditionEvidenceSha256);
        if (ReceiptId != Receipt.ReceiptId || CommandId != Receipt.CommandId || LeaseId != Receipt.LeaseId || Attempt != Receipt.Attempt ||
            SoulId != Receipt.SoulId || DeviceBindingId != Receipt.DeviceBindingId || PlatformAccountId != Receipt.PlatformAccountId ||
            TraceId != Receipt.TraceId || IdempotencyKey != Receipt.IdempotencyKey || OccurredAt != Receipt.OccurredAt || PrivacyClass != Receipt.PrivacyClass)
            throw new UnauthorizedAccessException("Signed receipt envelope does not exactly match its payload.");
        var expectedReceipt = Convert.ToHexStringLower(SHA256.HashData(CanonicalReceiptBytes(Receipt)));
        if (!FixedEqual(ReceiptSha256, expectedReceipt)) throw new UnauthorizedAccessException("receipt_sha256 mismatch.");
        var expectedEvidence = EvidenceDigest(NativeEvidenceSha256, PostconditionEvidenceSha256);
        if (!FixedEqual(Receipt.EvidenceDigest, expectedEvidence)) throw new UnauthorizedAccessException("Receipt evidence digest mismatch.");
        byte[] signature;
        try { signature = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new ArgumentException("Signature is not Base64.", nameof(SignatureBase64), exception); }
        try
        {
            if (signature.Length != 64 || Convert.ToBase64String(signature) != SignatureBase64)
                throw new ArgumentException("Signature must be canonical 64-byte P-256 P1363 Base64.", nameof(SignatureBase64));
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private void RequireScope()
    {
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireNonEmpty(ReceiptId, nameof(ReceiptId));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireNonEmpty(CommandId, nameof(CommandId));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireNonEmpty(LeaseId, nameof(LeaseId));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireSoulId(SoulId, nameof(SoulId));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireOpaqueId(DeviceBindingId, "db_", nameof(DeviceBindingId));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireOpaqueId(PlatformAccountId, "pa_", nameof(PlatformAccountId));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireTraceId(TraceId, nameof(TraceId));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireUtc(OccurredAt, nameof(OccurredAt));
    }

    private static byte[] CanonicalReceiptBytes(ConsumedCommandReceiptV1 receipt) => Encode(writer =>
    {
        writer.Token("dps.command-orchestrator.command-receipt/v1"); writer.Field("schema_version", receipt.SchemaVersion);
        writer.Field("contract_id", receipt.ContractId); writer.Field("producer_module", receipt.ProducerModule); writer.Field("receipt_id", receipt.ReceiptId);
        writer.Field("command_id", receipt.CommandId); writer.Field("lease_id", receipt.LeaseId); writer.Field("attempt", receipt.Attempt);
        writer.Field("soul_id", receipt.SoulId); writer.Field("device_binding_id", receipt.DeviceBindingId); writer.Field("platform_account_id", receipt.PlatformAccountId);
        writer.Field("trace_id", receipt.TraceId); writer.Field("idempotency_key", receipt.IdempotencyKey); writer.Field("occurred_at", receipt.OccurredAt);
        writer.Field("privacy_class", receipt.PrivacyClass); writer.Field("outcome", receipt.Outcome); writer.NullableField("native_result_id", receipt.NativeResultId);
        writer.Field("native_result_verified", receipt.NativeResultVerified); writer.Field("postcondition_verified", receipt.PostconditionVerified);
        writer.Field("evidence_digest", receipt.EvidenceDigest); writer.Field("retry_allowed", receipt.RetryAllowed); writer.Field("result_code", receipt.ResultCode);
    });

    private static string EvidenceDigest(string? native, string? postcondition)
    {
        var bytes = Encode(writer =>
        {
            writer.Token("dps.command-orchestrator.command-receipt-evidence/v1");
            writer.NullableField("native_evidence_sha256", native); writer.NullableField("postcondition_evidence_sha256", postcondition);
        });
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Encode(Action<FieldWriter> write) { using var writer = new FieldWriter(); write(writer); return writer.ToArray(); }
    private static void Exact(string actual, string expected) { if (actual != expected) throw new NotSupportedException($"Unsupported receipt value '{actual}'."); }
    private static void Digest(string value) => Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.RequireSha256(value, nameof(value));
    private static bool FixedEqual(string left, string right) => Dps.MemoryEventLedger.Contracts.MemoryContractValidationV2.FixedTimeEquals(left, right);

    private static void RequireExactProperties(JsonElement element, IReadOnlyCollection<string> expected, string description)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException($"{description} must be an object.");
        var names = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (names.Length != expected.Count || names.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
            throw new JsonException($"{description} has missing or unknown properties.");
    }
    private static string Text(JsonElement root, string name, int max) { var value = root.GetProperty(name); if (value.ValueKind != JsonValueKind.String) throw new JsonException($"{name} must be string."); var text = value.GetString()!; if (text.Length is 0 || text.Length > max || text.Any(char.IsControl)) throw new JsonException($"{name} is invalid."); return text; }
    private static string? NullableText(JsonElement root, string name, int max) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(root, name, max);
    private static Guid GuidValue(JsonElement root, string name) { var text = Text(root, name, 36); return Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty ? value : throw new JsonException($"{name} is not canonical UUID."); }
    private static Guid? NullableGuid(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : GuidValue(root, name);
    private static bool Bool(JsonElement root, string name) { var value = root.GetProperty(name); return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new JsonException($"{name} must be boolean.") }; }
    private static int IntValue(JsonElement root, string name, int min, int max) { if (!root.GetProperty(name).TryGetInt32(out var value) || value < min || value > max) throw new JsonException($"{name} integer is invalid."); return value; }
    private static long LongValue(JsonElement root, string name, long min) { if (!root.GetProperty(name).TryGetInt64(out var value) || value < min) throw new JsonException($"{name} integer is invalid."); return value; }
    private static DateTimeOffset Utc(JsonElement root, string name) { var text = Text(root, name, 32); if (!text.EndsWith('Z') || !DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)) throw new JsonException($"{name} must be exact seven-digit canonical UTC."); return value.ToUniversalTime(); }
    private static void Write(Utf8JsonWriter writer, string name, string value) => writer.WriteString(name, value);
    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value) { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value); }
    private static void WriteUtc(Utf8JsonWriter writer, string name, DateTimeOffset value) => writer.WriteString(name, value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
    private static void WriteReceipt(Utf8JsonWriter writer, ConsumedCommandReceiptV1 value)
    {
        writer.WriteStartObject(); Write(writer, "schema_version", value.SchemaVersion); Write(writer, "contract_id", value.ContractId); Write(writer, "producer_module", value.ProducerModule);
        writer.WriteString("receipt_id", value.ReceiptId); writer.WriteString("command_id", value.CommandId); writer.WriteString("lease_id", value.LeaseId); writer.WriteNumber("attempt", value.Attempt);
        Write(writer, "soul_id", value.SoulId); Write(writer, "device_binding_id", value.DeviceBindingId); Write(writer, "platform_account_id", value.PlatformAccountId);
        Write(writer, "trace_id", value.TraceId); Write(writer, "idempotency_key", value.IdempotencyKey); WriteUtc(writer, "occurred_at", value.OccurredAt); Write(writer, "privacy_class", value.PrivacyClass);
        Write(writer, "outcome", value.Outcome); if (value.NativeResultId.HasValue) writer.WriteString("native_result_id", value.NativeResultId.Value); else writer.WriteNull("native_result_id");
        writer.WriteBoolean("native_result_verified", value.NativeResultVerified); writer.WriteBoolean("postcondition_verified", value.PostconditionVerified);
        Write(writer, "evidence_digest", value.EvidenceDigest); writer.WriteBoolean("retry_allowed", value.RetryAllowed); Write(writer, "result_code", value.ResultCode); writer.WriteEndObject();
    }

    private sealed class FieldWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        internal void Field(string name, string value) { Token(name); Token(value); }
        internal void Field(string name, Guid value) => Field(name, value.ToString("N"));
        internal void Field(string name, int value) => Field(name, value.ToString(CultureInfo.InvariantCulture));
        internal void Field(string name, long value) => Field(name, value.ToString(CultureInfo.InvariantCulture));
        internal void Field(string name, bool value) => Field(name, value ? "true" : "false");
        internal void Field(string name, DateTimeOffset value) => Field(name, value.ToString("O", CultureInfo.InvariantCulture));
        internal void NullableField(string name, string? value) { Field($"{name}.present", value is not null); if (value is not null) Field($"{name}.value", value); }
        internal void NullableField(string name, Guid? value) { Field($"{name}.present", value.HasValue); if (value.HasValue) Field($"{name}.value", value.Value); }
        internal void Token(string value) { var bytes = StrictUtf8.GetBytes(value); try { Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length)); _stream.Write(length); _stream.Write(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); } }
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}
