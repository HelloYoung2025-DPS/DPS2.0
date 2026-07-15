using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.CommandOrchestrator.Contracts;

public sealed record SignedCommandReceiptV1(
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
    CommandReceiptV1 Receipt,
    string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "command.receipt.signed/v1";
    public const string CurrentProducerModule = "executor-gateway";
    public const string CurrentSignatureDomain = "dps.executor-gateway.command-receipt/v1";
    public const string CurrentCanonicalEncoding = "dps.canonical.uint32be-length-prefixed-utf8/v1";
    public const string CurrentReceiptDigestAlgorithm = "sha256:dps.command-orchestrator.command-receipt/v1";
    public const string CurrentCommandDigestAlgorithm = ExecutionAuthorizationV1.CurrentCommandDigestAlgorithm;
    public const string CurrentEvidenceDigestAlgorithm = "sha256:dps.command-orchestrator.command-receipt-evidence/v1";
    public const string CurrentSignatureAlgorithm = "ecdsa-p256-sha256";
    public const string CurrentSignatureFormat = "ieee-p1363-fixed-field-concatenation";
    public const string CurrentSignatureEncoding = "base64";
    public const string CurrentSignerModule = "executor-gateway";
    public const string CurrentAuthScope = "command-orchestrator:receipt";

    public string Outcome => Receipt.Outcome;
    public bool RetryAllowed => Receipt.RetryAllowed;
    public string ResultCode => Receipt.ResultCode;
    public bool NativeResultVerified => Receipt.NativeResultVerified;
    public bool PostconditionVerified => Receipt.PostconditionVerified;
    public Guid? NativeResultId => Receipt.NativeResultId;

    public void ValidatePayload()
    {
        CommandContractGuard.RequireMajor(SchemaVersion, 1);
        CommandContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        CommandContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        CommandContractGuard.RequireExact(SignatureDomain, CurrentSignatureDomain, nameof(SignatureDomain));
        CommandContractGuard.RequireExact(CanonicalEncoding, CurrentCanonicalEncoding, nameof(CanonicalEncoding));
        CommandContractGuard.RequireExact(ReceiptDigestAlgorithm, CurrentReceiptDigestAlgorithm, nameof(ReceiptDigestAlgorithm));
        CommandContractGuard.RequireExact(CommandDigestAlgorithm, CurrentCommandDigestAlgorithm, nameof(CommandDigestAlgorithm));
        CommandContractGuard.RequireExact(EvidenceDigestAlgorithm, CurrentEvidenceDigestAlgorithm, nameof(EvidenceDigestAlgorithm));
        CommandContractGuard.RequireExact(SignatureAlgorithm, CurrentSignatureAlgorithm, nameof(SignatureAlgorithm));
        CommandContractGuard.RequireExact(SignatureFormat, CurrentSignatureFormat, nameof(SignatureFormat));
        CommandContractGuard.RequireExact(SignatureEncoding, CurrentSignatureEncoding, nameof(SignatureEncoding));
        CommandContractGuard.RequireExact(SignerModule, CurrentSignerModule, nameof(SignerModule));
        CommandContractGuard.RequireExact(AuthScope, CurrentAuthScope, nameof(AuthScope));
        CommandContractGuard.RequireGuid(ReceiptId, nameof(ReceiptId));
        CommandContractGuard.RequireGuid(CommandId, nameof(CommandId));
        CommandContractGuard.RequireGuid(LeaseId, nameof(LeaseId));
        if (Attempt is < 1 or > 3) throw new InvalidOperationException("Signed receipt attempt must be between one and three.");
        CommandContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        CommandContractGuard.RequireTraceId(TraceId);
        CommandContractGuard.RequireIdempotencyKey(IdempotencyKey);
        CommandContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        CommandContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        CommandContractGuard.RequireSha256(ReceiptSha256, nameof(ReceiptSha256));
        CommandContractGuard.RequireSha256(CommandSha256, nameof(CommandSha256));
        CommandContractGuard.RequireSha256(AuthorizationSha256, nameof(AuthorizationSha256));
        CommandContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (ActiveReleaseBomGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ActiveReleaseBomGeneration));
        CommandContractGuard.RequireSha256(ActiveReleaseBomTokenSha256, nameof(ActiveReleaseBomTokenSha256));
        if (NativeEvidenceSha256 is not null) CommandContractGuard.RequireSha256(NativeEvidenceSha256, nameof(NativeEvidenceSha256));
        if (PostconditionEvidenceSha256 is not null) CommandContractGuard.RequireSha256(PostconditionEvidenceSha256, nameof(PostconditionEvidenceSha256));
        ArgumentNullException.ThrowIfNull(Receipt);
        Receipt.Validate();
        if (ReceiptId != Receipt.ReceiptId || CommandId != Receipt.CommandId || LeaseId != Receipt.LeaseId || Attempt != Receipt.Attempt ||
            !string.Equals(SoulId, Receipt.SoulId, StringComparison.Ordinal) ||
            !string.Equals(DeviceBindingId, Receipt.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(PlatformAccountId, Receipt.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(TraceId, Receipt.TraceId, StringComparison.Ordinal) ||
            !string.Equals(IdempotencyKey, Receipt.IdempotencyKey, StringComparison.Ordinal) ||
            OccurredAt != Receipt.OccurredAt || !string.Equals(PrivacyClass, Receipt.PrivacyClass, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Signed receipt envelope scope does not match its exact receipt payload.");
        if (!FixedDigestEquals(ReceiptSha256, CommandReceiptProtocolV1.ComputeReceiptSha256(Receipt)))
            throw new UnauthorizedAccessException("Signed receipt digest does not match its exact receipt payload.");
        if (Receipt.NativeResultVerified && NativeEvidenceSha256 is null)
            throw new InvalidOperationException("Verified native result requires a bound native evidence digest.");
        if (Receipt.PostconditionVerified && PostconditionEvidenceSha256 is null)
            throw new InvalidOperationException("Verified postcondition requires a bound postcondition evidence digest.");
        if (Receipt.Outcome == CommandReceiptV1.Success && (NativeEvidenceSha256 is null || PostconditionEvidenceSha256 is null))
            throw new InvalidOperationException("SUCCESS requires bound native and postcondition evidence digests.");
        if (!FixedDigestEquals(Receipt.EvidenceDigest, CommandReceiptProtocolV1.ComputeEvidenceDigest(NativeEvidenceSha256, PostconditionEvidenceSha256)))
            throw new UnauthorizedAccessException("Receipt evidence digest does not match the bound native and postcondition evidence summaries.");
    }

    public void Validate()
    {
        ValidatePayload();
        byte[] signature;
        try { signature = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new ArgumentException("Signed receipt signature must use Base64.", nameof(SignatureBase64), exception); }
        try
        {
            if (signature.Length != CommandReceiptProtocolV1.P1363SignatureSizeBytes)
                throw new ArgumentException("Signed receipt signature must be a 64-byte P-256 IEEE P1363 value.", nameof(SignatureBase64));
            if (!string.Equals(Convert.ToBase64String(signature), SignatureBase64, StringComparison.Ordinal))
                throw new ArgumentException("Signed receipt signature must use canonical Base64.", nameof(SignatureBase64));
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private static bool FixedDigestEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}

public static class CommandReceiptProtocolV1
{
    public const int P1363SignatureSizeBytes = 64;
    private const string ReceiptDomain = "dps.command-orchestrator.command-receipt/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ComputeReceiptSha256(CommandReceiptV1 receipt)
    {
        var canonical = CanonicalReceiptBytes(receipt);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    public static byte[] CanonicalReceiptBytes(CommandReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        return Encode(writer =>
        {
            writer.Token(ReceiptDomain);
            writer.Field("schema_version", receipt.SchemaVersion);
            writer.Field("contract_id", receipt.ContractId);
            writer.Field("producer_module", receipt.ProducerModule);
            writer.Field("receipt_id", receipt.ReceiptId);
            writer.Field("command_id", receipt.CommandId);
            writer.Field("lease_id", receipt.LeaseId);
            writer.Field("attempt", receipt.Attempt);
            writer.Field("soul_id", receipt.SoulId);
            writer.Field("device_binding_id", receipt.DeviceBindingId);
            writer.Field("platform_account_id", receipt.PlatformAccountId);
            writer.Field("trace_id", receipt.TraceId);
            writer.Field("idempotency_key", receipt.IdempotencyKey);
            writer.Field("occurred_at", receipt.OccurredAt);
            writer.Field("privacy_class", receipt.PrivacyClass);
            writer.Field("outcome", receipt.Outcome);
            writer.NullableField("native_result_id", receipt.NativeResultId);
            writer.Field("native_result_verified", receipt.NativeResultVerified);
            writer.Field("postcondition_verified", receipt.PostconditionVerified);
            writer.Field("evidence_digest", receipt.EvidenceDigest);
            writer.Field("retry_allowed", receipt.RetryAllowed);
            writer.Field("result_code", receipt.ResultCode);
        });
    }

    public static byte[] CanonicalSignedReceiptBytes(SignedCommandReceiptV1 signed)
    {
        ArgumentNullException.ThrowIfNull(signed);
        signed.ValidatePayload();
        return Encode(writer =>
        {
            writer.Token(signed.SignatureDomain);
            writer.Field("schema_version", signed.SchemaVersion);
            writer.Field("contract_id", signed.ContractId);
            writer.Field("producer_module", signed.ProducerModule);
            writer.Field("signature_domain", signed.SignatureDomain);
            writer.Field("canonical_encoding", signed.CanonicalEncoding);
            writer.Field("receipt_digest_algorithm", signed.ReceiptDigestAlgorithm);
            writer.Field("command_digest_algorithm", signed.CommandDigestAlgorithm);
            writer.Field("evidence_digest_algorithm", signed.EvidenceDigestAlgorithm);
            writer.Field("signature_algorithm", signed.SignatureAlgorithm);
            writer.Field("signature_format", signed.SignatureFormat);
            writer.Field("signature_encoding", signed.SignatureEncoding);
            writer.Field("signer_module", signed.SignerModule);
            writer.Field("auth_scope", signed.AuthScope);
            writer.Field("receipt_id", signed.ReceiptId);
            writer.Field("command_id", signed.CommandId);
            writer.Field("lease_id", signed.LeaseId);
            writer.Field("attempt", signed.Attempt);
            writer.Field("soul_id", signed.SoulId);
            writer.Field("device_binding_id", signed.DeviceBindingId);
            writer.Field("platform_account_id", signed.PlatformAccountId);
            writer.Field("trace_id", signed.TraceId);
            writer.Field("idempotency_key", signed.IdempotencyKey);
            writer.Field("occurred_at", signed.OccurredAt);
            writer.Field("privacy_class", signed.PrivacyClass);
            writer.Field("receipt_sha256", signed.ReceiptSha256);
            writer.Field("command_sha256", signed.CommandSha256);
            writer.Field("authorization_sha256", signed.AuthorizationSha256);
            writer.Field("release_bom_sha256", signed.ReleaseBomSha256);
            writer.Field("active_release_bom_generation", signed.ActiveReleaseBomGeneration);
            writer.Field("active_release_bom_token_sha256", signed.ActiveReleaseBomTokenSha256);
            writer.NullableField("native_evidence_sha256", signed.NativeEvidenceSha256);
            writer.NullableField("postcondition_evidence_sha256", signed.PostconditionEvidenceSha256);
        });
    }

    public static string ComputeEvidenceDigest(string? nativeEvidenceSha256, string? postconditionEvidenceSha256)
    {
        if (nativeEvidenceSha256 is not null) CommandContractGuard.RequireSha256(nativeEvidenceSha256, nameof(nativeEvidenceSha256));
        if (postconditionEvidenceSha256 is not null) CommandContractGuard.RequireSha256(postconditionEvidenceSha256, nameof(postconditionEvidenceSha256));
        var canonical = Encode(writer =>
        {
            writer.Token("dps.command-orchestrator.command-receipt-evidence/v1");
            writer.NullableField("native_evidence_sha256", nativeEvidenceSha256);
            writer.NullableField("postcondition_evidence_sha256", postconditionEvidenceSha256);
        });
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static byte[] Encode(Action<CanonicalFieldWriter> write)
    {
        using var writer = new CanonicalFieldWriter();
        write(writer);
        return writer.ToArray();
    }

    private sealed class CanonicalFieldWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        public void Field(string name, string value) { Token(name); Token(value); }
        public void Field(string name, Guid value) => Field(name, value.ToString("N"));
        public void Field(string name, int value) => Field(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, long value) => Field(name, value.ToString(CultureInfo.InvariantCulture));
        public void Field(string name, bool value) => Field(name, value ? "true" : "false");
        public void Field(string name, DateTimeOffset value) => Field(name, value.ToString("O", CultureInfo.InvariantCulture));
        public void NullableField(string name, string? value)
        {
            Field($"{name}.present", value is not null);
            if (value is not null) Field($"{name}.value", value);
        }
        public void NullableField(string name, Guid? value)
        {
            Field($"{name}.present", value.HasValue);
            if (value.HasValue) Field($"{name}.value", value.Value);
        }
        public void Token(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = StrictUtf8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
                _stream.Write(length);
                _stream.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}
