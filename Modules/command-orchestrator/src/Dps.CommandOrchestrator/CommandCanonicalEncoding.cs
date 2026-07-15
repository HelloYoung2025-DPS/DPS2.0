using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.CommandOrchestrator.Contracts;
using Dps.OperationCompiler.Contracts;

namespace Dps.CommandOrchestrator;

internal static class CommandCanonicalEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string OperationDigestDomain = "dps.command-orchestrator.operation-payload/v1";
    private const string ReceiptDigestDomain = "dps.command-orchestrator.receipt-payload/v1";
    private const string IdempotencyScopeDomain = "dps.command-orchestrator.idempotency-scope/v1";
    private const string CommandIdDomain = "dps.command-orchestrator.command-id/v1";
    private const string LeaseIdDomain = "dps.command-orchestrator.lease-id/v1";

    internal static string OperationDigest(CompiledOperationV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Steps);
        var writer = new CanonicalWriter(OperationDigestDomain);
        writer.Add("schema_version", value.SchemaVersion);
        writer.Add("contract_id", value.ContractId);
        writer.Add("producer_module", value.ProducerModule);
        writer.Add("operation_id", value.OperationId);
        writer.Add("approval_id", value.ApprovalId);
        writer.Add("proposal_id", value.ProposalId);
        writer.Add("approval_sha256", value.ApprovalSha256);
        writer.Add("soul_id", value.SoulId);
        writer.Add("device_binding_id", value.DeviceBindingId);
        writer.Add("platform_account_id", value.PlatformAccountId);
        writer.Add("trace_id", value.TraceId);
        writer.Add("idempotency_key", value.IdempotencyKey);
        writer.Add("occurred_at", value.OccurredAt);
        writer.Add("privacy_class", value.PrivacyClass);
        writer.Add("action_kind", value.ActionKind);
        writer.Add("is_side_effect", value.IsSideEffect);
        writer.Add("shadow_only", value.ShadowOnly);
        writer.AddNullable("platform_authorization_id", value.PlatformAuthorizationId);
        writer.Add("steps.count", value.Steps.Count);
        for (var stepOrdinal = 0; stepOrdinal < value.Steps.Count; stepOrdinal++)
        {
            var step = value.Steps[stepOrdinal];
            ArgumentNullException.ThrowIfNull(step);
            ArgumentNullException.ThrowIfNull(step.Arguments);
            writer.Add("step.ordinal", stepOrdinal);
            writer.Add("step.step_id", step.StepId);
            writer.Add("step.step_kind", step.StepKind);
            writer.Add("step.retry_safe", step.RetrySafe);
            writer.Add("step.postcondition_kind", step.PostconditionKind);
            var arguments = step.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            writer.Add("step.arguments.count", arguments.Length);
            for (var argumentOrdinal = 0; argumentOrdinal < arguments.Length; argumentOrdinal++)
            {
                writer.Add("argument.ordinal", argumentOrdinal);
                writer.Add("argument.key", arguments[argumentOrdinal].Key);
                writer.Add("argument.value", arguments[argumentOrdinal].Value);
            }
        }
        return writer.Sha256Hex();
    }

    internal static string ReceiptDigest(CommandReceiptV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var writer = new CanonicalWriter(ReceiptDigestDomain);
        writer.Add("schema_version", value.SchemaVersion);
        writer.Add("contract_id", value.ContractId);
        writer.Add("producer_module", value.ProducerModule);
        writer.Add("receipt_id", value.ReceiptId);
        writer.Add("command_id", value.CommandId);
        writer.Add("lease_id", value.LeaseId);
        writer.Add("attempt", value.Attempt);
        writer.Add("soul_id", value.SoulId);
        writer.Add("device_binding_id", value.DeviceBindingId);
        writer.Add("platform_account_id", value.PlatformAccountId);
        writer.Add("trace_id", value.TraceId);
        writer.Add("idempotency_key", value.IdempotencyKey);
        writer.Add("occurred_at", value.OccurredAt);
        writer.Add("privacy_class", value.PrivacyClass);
        writer.Add("outcome", value.Outcome);
        writer.AddNullable("native_result_id", value.NativeResultId);
        writer.Add("native_result_verified", value.NativeResultVerified);
        writer.Add("postcondition_verified", value.PostconditionVerified);
        writer.Add("evidence_digest", value.EvidenceDigest);
        writer.Add("retry_allowed", value.RetryAllowed);
        writer.Add("result_code", value.ResultCode);
        return writer.Sha256Hex();
    }

    internal static string SignedReceiptDigest(SignedCommandReceiptV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var canonical = CommandReceiptProtocolV1.CanonicalSignedReceiptBytes(value);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    internal static string IdempotencyScopeKey(string soulId, string deviceBindingId, string platformAccountId, string idempotencyKey)
    {
        CommandContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId);
        CommandContractGuard.RequireIdempotencyKey(idempotencyKey);
        var writer = new CanonicalWriter(IdempotencyScopeDomain);
        writer.Add("soul_id", soulId);
        writer.Add("device_binding_id", deviceBindingId);
        writer.Add("platform_account_id", platformAccountId);
        writer.Add("idempotency_key", idempotencyKey);
        return writer.Sha256Hex();
    }

    internal static Guid CommandId(string idempotencyScopeKey, Guid operationId)
    {
        var writer = new CanonicalWriter(CommandIdDomain);
        writer.Add("idempotency_scope_key", idempotencyScopeKey);
        writer.Add("operation_id", operationId);
        return writer.Sha256Guid();
    }

    internal static Guid LeaseId(Guid commandId, int attempt, string workerId)
    {
        var writer = new CanonicalWriter(LeaseIdDomain);
        writer.Add("command_id", commandId);
        writer.Add("attempt", attempt);
        writer.Add("worker_id", workerId);
        return writer.Sha256Guid();
    }

    private sealed class CanonicalWriter
    {
        private readonly MemoryStream _buffer = new();

        internal CanonicalWriter(string domain)
        {
            Add("domain", domain);
        }

        internal void Add(string name, string value)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(value);
            WriteToken(name);
            WriteToken(value);
        }

        internal void Add(string name, Guid value) => Add(name, value.ToString("N", CultureInfo.InvariantCulture));
        internal void Add(string name, int value) => Add(name, value.ToString(CultureInfo.InvariantCulture));
        internal void Add(string name, bool value) => Add(name, value ? "true" : "false");
        internal void Add(string name, DateTimeOffset value) => Add(name, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        internal void AddNullable(string name, string? value)
        {
            Add($"{name}.present", value is not null);
            if (value is not null) Add($"{name}.value", value);
        }

        internal void AddNullable(string name, Guid? value)
        {
            Add($"{name}.present", value.HasValue);
            if (value.HasValue) Add($"{name}.value", value.Value);
        }

        internal string Sha256Hex() => Convert.ToHexStringLower(Sha256());
        internal Guid Sha256Guid() => new(Sha256().AsSpan(0, 16));

        private byte[] Sha256() => SHA256.HashData(_buffer.ToArray());

        private void WriteToken(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            _buffer.Write(length);
            _buffer.Write(bytes);
        }
    }
}
