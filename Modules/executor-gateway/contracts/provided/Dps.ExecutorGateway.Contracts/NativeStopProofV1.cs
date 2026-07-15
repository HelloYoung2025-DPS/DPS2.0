using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Dps.ExecutorGateway.Contracts;

public sealed record NativeAbortConfirmation(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("stopped")] bool Stopped,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("native_request_binding_sha256")] string NativeRequestBindingSha256,
    [property: JsonPropertyName("submitted_request_sha256")] string SubmittedRequestSha256,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("active_release_bom_sha256")] string ActiveReleaseBomSha256,
    [property: JsonPropertyName("active_release_bom_generation")] long ActiveReleaseBomGeneration,
    [property: JsonPropertyName("active_release_bom_token_sha256")] string ActiveReleaseBomTokenSha256,
    [property: JsonPropertyName("worker_instance_id")] string WorkerInstanceId,
    [property: JsonPropertyName("worker_generation")] long WorkerGeneration,
    [property: JsonPropertyName("stop_kind")] string ResultCode,
    [property: JsonPropertyName("evidence_sha256")] string EvidenceSha256,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("auth_scope")] string AuthScope,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("signature_base64")] string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "native.stop.proof/v1";
    public const string CurrentProducerModule = "windows-edge-worker";
    public const string CurrentPrivacyClass = "internal";
    public const string CurrentAuthScope = "executor-gateway.native-stop-proof";
    public const int P1363SignatureSizeBytes = 64;
    public const string NotStarted = "NATIVE_NOT_STARTED";
    public const string TransportAborted = "NATIVE_TRANSPORT_ABORTED";
    public const string WorkerProcessExited = "NATIVE_WORKER_PROCESS_EXITED";
    private static readonly IReadOnlySet<string> AllowedResultCodes = new HashSet<string>(
        [NotStarted, TransportAborted, WorkerProcessExited],
        StringComparer.Ordinal);

    public void Validate()
    {
        NativeContractGuard.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        NativeContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        NativeContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        if (!Stopped)
            throw new InvalidDataException("Native abort confirmation must prove that no later native write remains possible.");
        NativeContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        NativeContractGuard.RequireGuid(CommandId, nameof(CommandId));
        NativeContractGuard.RequireGuid(LeaseId, nameof(LeaseId));
        if (Attempt is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(Attempt));
        NativeContractGuard.RequireSha256(NativeRequestBindingSha256, nameof(NativeRequestBindingSha256));
        NativeContractGuard.RequireSha256(SubmittedRequestSha256, nameof(SubmittedRequestSha256));
        NativeContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        NativeContractGuard.RequireTraceId(TraceId);
        NativeContractGuard.RequireIdempotencyKey(IdempotencyKey);
        NativeContractGuard.RequireSha256(ActiveReleaseBomSha256, nameof(ActiveReleaseBomSha256));
        if (ActiveReleaseBomGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ActiveReleaseBomGeneration));
        NativeContractGuard.RequireSha256(ActiveReleaseBomTokenSha256, nameof(ActiveReleaseBomTokenSha256));
        NativeStopProofProtocolV1.RequireWorkerInstanceId(WorkerInstanceId);
        if (WorkerGeneration < 1) throw new ArgumentOutOfRangeException(nameof(WorkerGeneration));
        NativeContractGuard.RequireText(ResultCode, 128, nameof(ResultCode));
        if (!AllowedResultCodes.Contains(ResultCode))
            throw new NotSupportedException($"Unsupported native abort confirmation '{ResultCode}'.");
        NativeContractGuard.RequireSha256(EvidenceSha256, nameof(EvidenceSha256));
        NativeContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        NativeContractGuard.RequireExact(PrivacyClass, CurrentPrivacyClass, nameof(PrivacyClass));
        NativeContractGuard.RequireExact(AuthScope, CurrentAuthScope, nameof(AuthScope));
        NativeStopProofProtocolV1.RequireKeyId(KeyId);
        byte[] signature;
        try { signature = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception)
        {
            throw new ArgumentException("Native stop proof signature must use Base64 encoding.", nameof(SignatureBase64), exception);
        }
        try
        {
            if (signature.Length != P1363SignatureSizeBytes ||
                !string.Equals(Convert.ToBase64String(signature), SignatureBase64, StringComparison.Ordinal))
                throw new ArgumentException(
                    "Native stop proof signature must be canonical Base64 for a 64-byte P-256 P1363 signature.",
                    nameof(SignatureBase64));
        }
        finally { CryptographicOperations.ZeroMemory(signature); }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(EvidenceSha256),
                Convert.FromHexString(NativeStopProofProtocolV1.ComputeEvidenceSha256(this))))
            throw new InvalidDataException(
                "Native abort confirmation evidence digest is not canonical for its exact attempt and worker incarnation.");
    }
}

public static class NativeStopProofProtocolV1
{
    public const string EvidenceDomain = "dps.native-stop-evidence-sha256/v1";
    public const string SigningDomain = "dps.native-stop-proof/signing/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ComputeEvidenceSha256(NativeAbortConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        using var writer = new CanonicalWriter();
        writer.Field(EvidenceDomain);
        WriteBoundFields(writer, confirmation);
        writer.Field(confirmation.OccurredAt);
        writer.Field(confirmation.PrivacyClass);
        var bytes = writer.ToArray();
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static byte[] CanonicalSigningBytes(NativeAbortConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        using var writer = new CanonicalWriter();
        writer.Field(SigningDomain);
        WriteBoundFields(writer, confirmation);
        writer.Field(confirmation.EvidenceSha256);
        writer.Field(confirmation.OccurredAt);
        writer.Field(confirmation.PrivacyClass);
        writer.Field(confirmation.AuthScope);
        writer.Field(confirmation.KeyId);
        return writer.ToArray();
    }

    public static void RequireWorkerInstanceId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 35 || !value.StartsWith("wi_", StringComparison.Ordinal) ||
            value.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException(
                "Worker instance id must be 'wi_' followed by exactly 32 lowercase hexadecimal characters.",
                nameof(value));
    }

    public static void RequireKeyId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 128 ||
            !(value[0] is >= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789._-") >= 0)
            throw new ArgumentException(
                "Native stop proof key id must be lowercase opaque text beginning with an alphanumeric character.",
                nameof(value));
    }

    private static void WriteBoundFields(CanonicalWriter writer, NativeAbortConfirmation confirmation)
    {
        writer.Field(confirmation.SchemaVersion);
        writer.Field(confirmation.ContractId);
        writer.Field(confirmation.ProducerModule);
        writer.Field(confirmation.Stopped ? "true" : "false");
        writer.Field(confirmation.SubmissionAttemptId);
        writer.Field(confirmation.CommandId);
        writer.Field(confirmation.LeaseId);
        writer.Field(confirmation.Attempt);
        writer.Field(confirmation.NativeRequestBindingSha256);
        writer.Field(confirmation.SubmittedRequestSha256);
        writer.Field(confirmation.SoulId);
        writer.Field(confirmation.DeviceBindingId);
        writer.Field(confirmation.PlatformAccountId);
        writer.Field(confirmation.TraceId);
        writer.Field(confirmation.IdempotencyKey);
        writer.Field(confirmation.ActiveReleaseBomSha256);
        writer.Field(confirmation.ActiveReleaseBomGeneration);
        writer.Field(confirmation.ActiveReleaseBomTokenSha256);
        writer.Field(confirmation.WorkerInstanceId);
        writer.Field(confirmation.WorkerGeneration);
        writer.Field(confirmation.ResultCode);
    }

    private sealed class CanonicalWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public void Field(string value)
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

        public void Field(Guid value) => Field(value.ToString("N"));
        public void Field(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
        public void Field(long value) => Field(value.ToString(CultureInfo.InvariantCulture));
        public void Field(DateTimeOffset value) => Field(value.ToString("O", CultureInfo.InvariantCulture));
        public byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}
