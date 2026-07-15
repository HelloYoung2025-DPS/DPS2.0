using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.PolicyApproval.Contracts;

public static class ApprovalSubmissionLifecycleBinding
{
    public static string ComputeIntentSha256(ApprovalSubmissionIntentV1 value)
        => Compute(CanonicalIntentBytes(value));

    public static byte[] CanonicalIntentBytes(ApprovalSubmissionIntentV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return Write(writer =>
        {
            writer.Field("dps.policy-approval.submission-intent/v1");
            writer.Field(value.SchemaVersion); writer.Field(value.ContractId); writer.Field(value.ProducerModule); writer.Field(value.AuthScope);
            writer.Field(value.SubmissionAttemptId); writer.Field(value.FenceRequestSha256);
            WriteAttempt(writer, value.ApprovalId, value.ProposalId, value.CommandId, value.LeaseId, value.Attempt, value.SoulId, value.DeviceBindingId, value.PlatformAccountId, value.TraceId, value.IdempotencyKey);
            writer.Field(value.ApprovalSha256); writer.Field(value.ProposalSha256); writer.Field(value.StatusRevision); writer.Field(value.RuntimeRevision);
            writer.Field(value.RuntimeStateSha256); writer.Field(value.ReleaseBomSha256); writer.Field(value.ReleaseBomGeneration);
            writer.Field(value.ExecutionAuthorizationSha256); writer.Field(value.NativeRequestBindingSha256);
            writer.Field(value.OccurredAt); writer.Field(value.ValidUntil); writer.Field(value.PrivacyClass);
        });
    }

    public static string ComputeAcknowledgementSha256(ApprovalSubmissionAcknowledgementV1 value)
        => Compute(CanonicalAcknowledgementBytes(value));

    public static byte[] CanonicalAcknowledgementBytes(ApprovalSubmissionAcknowledgementV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return Write(writer =>
        {
            writer.Field("dps.policy-approval.submission-acknowledgement/v1");
            writer.Field(value.SchemaVersion); writer.Field(value.ContractId); writer.Field(value.ProducerModule); writer.Field(value.AuthScope);
            writer.Field(value.AcknowledgementId); writer.Field(value.SubmissionAttemptId);
            WriteAttempt(writer, value.ApprovalId, value.ProposalId, value.CommandId, value.LeaseId, value.Attempt, value.SoulId, value.DeviceBindingId, value.PlatformAccountId, value.TraceId, value.IdempotencyKey);
            writer.Field(value.ReleaseBomSha256); writer.Field(value.ReleaseBomGeneration); writer.Field(value.NativeRequestBindingSha256);
            writer.Field(value.SubmissionIntentSha256); writer.Field(value.PendingStateSha256); writer.Field(value.SubmittedRequestSha256);
            writer.Field(value.NativeSubmissionId); writer.Field(value.CompletionHandleId); writer.Field(value.NativeAcknowledgementSha256);
            writer.Field(value.OccurredAt); writer.Field(value.ValidUntil); writer.Field(value.PrivacyClass);
        });
    }

    public static string ComputeReconciliationSha256(ApprovalSubmissionReconciliationV1 value)
        => Compute(CanonicalReconciliationBytes(value));

    public static byte[] CanonicalReconciliationBytes(ApprovalSubmissionReconciliationV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return Write(writer =>
        {
            writer.Field("dps.policy-approval.submission-reconciliation/v1");
            writer.Field(value.SchemaVersion); writer.Field(value.ContractId); writer.Field(value.ProducerModule); writer.Field(value.AuthScope); writer.Field(value.AuthorityRole);
            writer.Field(value.ReconciliationId); writer.Field(value.SubmissionAttemptId);
            WriteAttempt(writer, value.ApprovalId, value.ProposalId, value.CommandId, value.LeaseId, value.Attempt, value.SoulId, value.DeviceBindingId, value.PlatformAccountId, value.TraceId, value.IdempotencyKey);
            writer.Field(value.SubmissionIntentSha256); writer.Field(value.PendingStateSha256); writer.Field(value.Finding); writer.Field(value.EvidenceSha256);
            writer.Field(value.OccurredAt); writer.Field(value.ValidUntil); writer.Field(value.PrivacyClass);
        });
    }

    public static string ComputeRecoverySha256(ApprovalSubmissionRecoveryV1 value)
        => Compute(CanonicalRecoveryBytes(value));

    public static byte[] CanonicalRecoveryBytes(ApprovalSubmissionRecoveryV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return Write(writer =>
        {
            writer.Field("dps.policy-approval.submission-recovery/v1");
            writer.Field(value.SchemaVersion); writer.Field(value.ContractId); writer.Field(value.ProducerModule); writer.Field(value.AuthScope); writer.Field(value.AuthorityRole);
            writer.Field(value.RecoveryId); writer.Field(value.SubmissionAttemptId); writer.Field(value.ReconciliationId); writer.Field(value.ReconciliationSha256);
            WriteAttempt(writer, value.ApprovalId, value.ProposalId, value.CommandId, value.PreviousLeaseId, value.PreviousAttempt, value.SoulId, value.DeviceBindingId, value.PlatformAccountId, value.TraceId, value.IdempotencyKey);
            writer.Field(value.NextSubmissionAttemptId); writer.Field(value.NextLeaseId); writer.Field(value.NextAttempt);
            writer.Field(value.NextReleaseBomSha256); writer.Field(value.NextReleaseBomGeneration);
            writer.Field(value.NextExecutionAuthorizationSha256); writer.Field(value.NextNativeRequestBindingSha256); writer.Field(value.HumanApprovalId);
            writer.Field(value.OccurredAt); writer.Field(value.ValidUntil); writer.Field(value.PrivacyClass);
        });
    }

    public static string ComputeStateSha256(ApprovalSubmissionStateV1 value)
        => Compute(CanonicalStateBytes(value));

    public static byte[] CanonicalStateBytes(ApprovalSubmissionStateV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return Write(writer =>
        {
            writer.Field("dps.policy-approval.submission-state/v1");
            writer.Field(value.SchemaVersion); writer.Field(value.ContractId); writer.Field(value.ProducerModule);
            writer.Field(value.StateEventId); writer.Field(value.SubmissionAttemptId);
            WriteAttempt(writer, value.ApprovalId, value.ProposalId, value.CommandId, value.LeaseId, value.Attempt, value.SoulId, value.DeviceBindingId, value.PlatformAccountId, value.TraceId, value.IdempotencyKey);
            writer.Field(value.ReleaseBomSha256); writer.Field(value.ReleaseBomGeneration); writer.Field(value.NativeRequestBindingSha256); writer.Field(value.SubmissionIntentSha256);
            writer.Field(value.State); writer.NullableField(value.PredecessorStateSha256); writer.Field(value.EvidenceSha256); writer.Field(value.OccurredAt); writer.Field(value.PrivacyClass);
        });
    }

    private static void WriteAttempt(CanonicalWriter writer, Guid approvalId, Guid proposalId, Guid commandId, Guid leaseId, int attempt, string soulId, string deviceBindingId, string platformAccountId, string traceId, string idempotencyKey)
    {
        writer.Field(approvalId); writer.Field(proposalId); writer.Field(commandId); writer.Field(leaseId); writer.Field(attempt);
        writer.Field(soulId); writer.Field(deviceBindingId); writer.Field(platformAccountId); writer.Field(traceId); writer.Field(idempotencyKey);
    }

    private static byte[] Write(Action<CanonicalWriter> write)
    {
        using var writer = new CanonicalWriter();
        write(writer);
        return writer.ToArray();
    }

    private static string Compute(byte[] canonical)
    {
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(canonical, digest);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private sealed class CanonicalWriter : IDisposable
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly MemoryStream _stream = new();
        internal void Field(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = StrictUtf8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
                _stream.Write(length); _stream.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        internal void NullableField(string? value) { Field(value is null ? "false" : "true"); if (value is not null) Field(value); }
        internal void Field(Guid value) => Field(value.ToString("N"));
        internal void Field(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
        internal void Field(long value) => Field(value.ToString(CultureInfo.InvariantCulture));
        internal void Field(DateTimeOffset value) => Field(value.ToString("O", CultureInfo.InvariantCulture));
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}
