using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.CommandOrchestrator.Contracts;

public sealed record ExecutionAuthorizationV1(
    string SchemaVersion, string ContractId, string ProducerModule,
    string SignatureDomain, string CanonicalEncoding, string CommandDigestAlgorithm,
    string SignatureAlgorithm, string SignatureFormat, string SignatureEncoding,
    string CallerModule, string AuthScope, Guid CommandId, Guid LeaseId, int Attempt,
    string SoulId, string DeviceBindingId, string PlatformAccountId, string TraceId, string IdempotencyKey,
    DateTimeOffset OccurredAt, string PrivacyClass, string CommandSha256, string ReleaseBomSha256,
    long ActiveReleaseBomGeneration, string ActiveReleaseBomTokenSha256,
    DateTimeOffset ValidUntil, bool ShadowMode, string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "execution.authorization/v1";
    public const string CurrentProducerModule = "command-orchestrator";
    public const string CurrentSignatureDomain = "dps.command-orchestrator.execution-authorization/v1";
    public const string CurrentCanonicalEncoding = "dps.canonical.uint32be-length-prefixed-utf8/v1";
    public const string CurrentCommandDigestAlgorithm = "sha256:dps.command-orchestrator.command-dispatch/v1";
    public const string CurrentSignatureAlgorithm = "ecdsa-p256-sha256";
    public const string CurrentSignatureFormat = "ieee-p1363-fixed-field-concatenation";
    public const string CurrentSignatureEncoding = "base64";
    public const string CurrentCallerModule = "command-orchestrator";
    public const string CurrentAuthScope = "executor:dispatch";

    public void ValidatePayload()
    {
        CommandContractGuard.RequireMajor(SchemaVersion, 1);
        CommandContractGuard.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        CommandContractGuard.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        CommandContractGuard.RequireExact(SignatureDomain, CurrentSignatureDomain, nameof(SignatureDomain));
        CommandContractGuard.RequireExact(CanonicalEncoding, CurrentCanonicalEncoding, nameof(CanonicalEncoding));
        CommandContractGuard.RequireExact(CommandDigestAlgorithm, CurrentCommandDigestAlgorithm, nameof(CommandDigestAlgorithm));
        CommandContractGuard.RequireExact(SignatureAlgorithm, CurrentSignatureAlgorithm, nameof(SignatureAlgorithm));
        CommandContractGuard.RequireExact(SignatureFormat, CurrentSignatureFormat, nameof(SignatureFormat));
        CommandContractGuard.RequireExact(SignatureEncoding, CurrentSignatureEncoding, nameof(SignatureEncoding));
        CommandContractGuard.RequireExact(CallerModule, CurrentCallerModule, nameof(CallerModule));
        CommandContractGuard.RequireExact(AuthScope, CurrentAuthScope, nameof(AuthScope));
        CommandContractGuard.RequireGuid(CommandId, nameof(CommandId));
        CommandContractGuard.RequireGuid(LeaseId, nameof(LeaseId));
        if (Attempt is < 1 or > 3) throw new InvalidOperationException("Authorization attempt must be between one and three.");
        CommandContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        CommandContractGuard.RequireTraceId(TraceId);
        CommandContractGuard.RequireIdempotencyKey(IdempotencyKey);
        CommandContractGuard.RequireUtc(OccurredAt, nameof(OccurredAt));
        CommandContractGuard.RequireExact(PrivacyClass, "internal", nameof(PrivacyClass));
        CommandContractGuard.RequireSha256(CommandSha256, nameof(CommandSha256));
        CommandContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (ActiveReleaseBomGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ActiveReleaseBomGeneration));
        CommandContractGuard.RequireSha256(ActiveReleaseBomTokenSha256, nameof(ActiveReleaseBomTokenSha256));
        CommandContractGuard.RequireUtc(ValidUntil, nameof(ValidUntil));
        if (ValidUntil <= OccurredAt) throw new InvalidOperationException("Authorization must expire after it is issued.");
    }

    public void Validate()
    {
        ValidatePayload();
        byte[] signature;
        try { signature = Convert.FromBase64String(SignatureBase64); }
        catch (FormatException exception) { throw new ArgumentException("Signature must use Base64 encoding.", nameof(SignatureBase64), exception); }
        try
        {
            if (signature.Length != ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes)
                throw new ArgumentException("Signature must be a 64-byte P-256 IEEE P1363 value.", nameof(SignatureBase64));
            if (!string.Equals(Convert.ToBase64String(signature), SignatureBase64, StringComparison.Ordinal))
                throw new ArgumentException("Signature must use canonical Base64 without ignored whitespace or pad bits.", nameof(SignatureBase64));
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }
}

public static class ExecutionAuthorizationProtocolV1
{
    public const int P1363SignatureSizeBytes = 64;
    private const string CommandDomain = "dps.command-orchestrator.command-dispatch/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ComputeCommandSha256(CommandDispatchV1 command)
    {
        var canonical = CanonicalCommandBytes(command);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    public static string ComputeAuthorizationSha256(ExecutionAuthorizationV1 authorization)
    {
        var canonical = CanonicalAuthorizationBytes(authorization);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    public static byte[] CanonicalCommandBytes(CommandDispatchV1 command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return Encode(writer =>
        {
            writer.Token(CommandDomain);
            writer.Field("schema_version", command.SchemaVersion);
            writer.Field("contract_id", command.ContractId);
            writer.Field("producer_module", command.ProducerModule);
            writer.Field("command_id", command.CommandId);
            writer.Field("operation_id", command.OperationId);
            writer.Field("approval_id", command.ApprovalId);
            writer.Field("approval_sha256", command.ApprovalSha256);
            writer.Field("soul_id", command.SoulId);
            writer.Field("device_binding_id", command.DeviceBindingId);
            writer.Field("platform_account_id", command.PlatformAccountId);
            writer.Field("trace_id", command.TraceId);
            writer.Field("idempotency_key", command.IdempotencyKey);
            writer.Field("occurred_at", command.OccurredAt);
            writer.Field("privacy_class", command.PrivacyClass);
            writer.Field("action_kind", command.ActionKind);
            writer.Field("is_side_effect", command.IsSideEffect);
            writer.NullableField("platform_authorization_id", command.PlatformAuthorizationId);
            writer.Field("lease_id", command.LeaseId);
            writer.Field("lease_owner", command.LeaseOwner);
            writer.Field("lease_expires_at", command.LeaseExpiresAt);
            writer.Field("attempt", command.Attempt);
            writer.Field("steps.count", command.Steps.Count);
            for (var stepOrdinal = 0; stepOrdinal < command.Steps.Count; stepOrdinal++)
            {
                var step = command.Steps[stepOrdinal];
                writer.Field("steps.ordinal", stepOrdinal);
                writer.Field("steps.step_id", step.StepId);
                writer.Field("steps.step_kind", step.StepKind);
                writer.Field("steps.retry_safe", step.RetrySafe);
                writer.Field("steps.postcondition_kind", step.PostconditionKind);
                writer.Field("steps.arguments.count", step.Arguments.Count);
                var argumentOrdinal = 0;
                foreach (var pair in step.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.Field("steps.arguments.ordinal", argumentOrdinal++);
                    writer.Field("steps.arguments.key", pair.Key);
                    writer.Field("steps.arguments.value", pair.Value);
                }
            }
        });
    }

    public static byte[] CanonicalAuthorizationBytes(ExecutionAuthorizationV1 authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.ValidatePayload();
        return Encode(writer =>
        {
            writer.Token(authorization.SignatureDomain);
            writer.Field("schema_version", authorization.SchemaVersion);
            writer.Field("contract_id", authorization.ContractId);
            writer.Field("producer_module", authorization.ProducerModule);
            writer.Field("signature_domain", authorization.SignatureDomain);
            writer.Field("canonical_encoding", authorization.CanonicalEncoding);
            writer.Field("command_digest_algorithm", authorization.CommandDigestAlgorithm);
            writer.Field("signature_algorithm", authorization.SignatureAlgorithm);
            writer.Field("signature_format", authorization.SignatureFormat);
            writer.Field("signature_encoding", authorization.SignatureEncoding);
            writer.Field("caller_module", authorization.CallerModule);
            writer.Field("auth_scope", authorization.AuthScope);
            writer.Field("command_id", authorization.CommandId);
            writer.Field("lease_id", authorization.LeaseId);
            writer.Field("attempt", authorization.Attempt);
            writer.Field("soul_id", authorization.SoulId);
            writer.Field("device_binding_id", authorization.DeviceBindingId);
            writer.Field("platform_account_id", authorization.PlatformAccountId);
            writer.Field("trace_id", authorization.TraceId);
            writer.Field("idempotency_key", authorization.IdempotencyKey);
            writer.Field("occurred_at", authorization.OccurredAt);
            writer.Field("privacy_class", authorization.PrivacyClass);
            writer.Field("command_sha256", authorization.CommandSha256);
            writer.Field("release_bom_sha256", authorization.ReleaseBomSha256);
            writer.Field("active_release_bom_generation", authorization.ActiveReleaseBomGeneration);
            writer.Field("active_release_bom_token_sha256", authorization.ActiveReleaseBomTokenSha256);
            writer.Field("valid_until", authorization.ValidUntil);
            writer.Field("shadow_mode", authorization.ShadowMode);
        });
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
