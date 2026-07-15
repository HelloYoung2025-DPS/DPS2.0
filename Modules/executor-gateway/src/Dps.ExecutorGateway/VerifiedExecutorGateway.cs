using System.Collections.Frozen;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.CommandOrchestrator.Contracts;
using Dps.ExecutorGateway.Contracts;
using Dps.PolicyApproval.Contracts;

namespace Dps.ExecutorGateway;

public sealed record VerifiedExecutionAuthorization(ExecutionAuthorizationV1 Authorization);
public sealed record ActiveReleaseBomBindingV1(
    string SchemaVersion,
    string DeviceBindingId,
    string ReleaseBomSha256,
    long Generation,
    string ExecutionTokenBase64)
{
    public const string CurrentSchemaVersion = "dps.active-release-bom-binding/v1";
    public const int ExecutionTokenSizeBytes = 32;

    public void Validate()
    {
        CommandContractGuard.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        CommandContractGuard.RequireDeviceBindingId(DeviceBindingId);
        CommandContractGuard.RequireSha256(ReleaseBomSha256, nameof(ReleaseBomSha256));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        var token = DecodeExecutionToken();
        CryptographicOperations.ZeroMemory(token);
    }

    public string ComputeExecutionTokenSha256()
    {
        var token = DecodeExecutionToken();
        try { return Convert.ToHexStringLower(SHA256.HashData(token)); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    public override string ToString() => $"{nameof(ActiveReleaseBomBindingV1)} {{ SchemaVersion = {SchemaVersion}, DeviceBindingId = {DeviceBindingId}, ReleaseBomSha256 = {ReleaseBomSha256}, Generation = {Generation}, ExecutionTokenBase64 = [REDACTED] }}";

    private byte[] DecodeExecutionToken()
    {
        byte[] token;
        try { token = Convert.FromBase64String(ExecutionTokenBase64); }
        catch (FormatException exception) { throw new ArgumentException("Active BOM execution token must use Base64 encoding.", nameof(ExecutionTokenBase64), exception); }
        if (token.Length != ExecutionTokenSizeBytes || !string.Equals(Convert.ToBase64String(token), ExecutionTokenBase64, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(token);
            throw new ArgumentException("Active BOM execution token must be canonical Base64 for exactly 256 opaque bits.", nameof(ExecutionTokenBase64));
        }
        return token;
    }
}
public sealed record PostconditionVerification(bool Verified, string EvidenceDigest, string ResultCode);
public sealed record NativeExecutionRequestV1(
    CommandDispatchV1 Command,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    Guid StepId,
    string StepKind,
    string CommandSha256,
    string AuthorizationSha256,
    Guid SubmissionAttemptId,
    string SubmissionIntentSha256,
    string PendingStateSha256,
    string ActiveReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomExecutionTokenBase64,
    string ActiveReleaseBomTokenSha256)
{
    public override string ToString() => $"{nameof(NativeExecutionRequestV1)} {{ CommandId = {CommandId}, LeaseId = {LeaseId}, Attempt = {Attempt}, DeviceBindingId = {DeviceBindingId}, CommandSha256 = {CommandSha256}, AuthorizationSha256 = {AuthorizationSha256}, SubmissionAttemptId = {SubmissionAttemptId}, SubmissionIntentSha256 = {SubmissionIntentSha256}, PendingStateSha256 = {PendingStateSha256}, ActiveReleaseBomSha256 = {ActiveReleaseBomSha256}, ActiveReleaseBomGeneration = {ActiveReleaseBomGeneration}, ActiveReleaseBomExecutionTokenBase64 = [REDACTED] }}";
}
public sealed record NativeSubmission(NativeSubmissionAck Acknowledgement, INativeSubmissionCompletion Completion);
public sealed record VerifiedSubmissionPendingAuthorization(
    ApprovalSubmissionIntentV1 Intent,
    ApprovalSubmissionStateV1 PendingState);
public sealed record VerifiedSubmissionAcknowledgedAuthorization(
    ApprovalSubmissionAcknowledgementV1 Acknowledgement,
    ApprovalSubmissionStateV1 AcknowledgedState);
public sealed record VerifiedSubmissionUnknownAuthorization(
    ApprovalSubmissionStateV1 UnknownState);
public sealed record NativeStopRequest(
    Guid SubmissionAttemptId,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string NativeRequestBindingSha256,
    string SubmittedRequestSha256,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string ActiveReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomTokenSha256,
    string WorkerInstanceId,
    long WorkerGeneration)
{
    public void Validate()
    {
        NativeContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        NativeContractGuard.RequireGuid(CommandId, nameof(CommandId));
        NativeContractGuard.RequireGuid(LeaseId, nameof(LeaseId));
        if (Attempt is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(Attempt));
        NativeContractGuard.RequireSha256(NativeRequestBindingSha256, nameof(NativeRequestBindingSha256));
        NativeContractGuard.RequireSha256(SubmittedRequestSha256, nameof(SubmittedRequestSha256));
        CommandContractGuard.RequireScope(SoulId, DeviceBindingId, PlatformAccountId);
        CommandContractGuard.RequireTraceId(TraceId);
        CommandContractGuard.RequireIdempotencyKey(IdempotencyKey);
        NativeContractGuard.RequireSha256(ActiveReleaseBomSha256, nameof(ActiveReleaseBomSha256));
        if (ActiveReleaseBomGeneration < 1) throw new ArgumentOutOfRangeException(nameof(ActiveReleaseBomGeneration));
        NativeContractGuard.RequireSha256(ActiveReleaseBomTokenSha256, nameof(ActiveReleaseBomTokenSha256));
        RequireWorkerInstanceId(WorkerInstanceId);
        if (WorkerGeneration < 1) throw new ArgumentOutOfRangeException(nameof(WorkerGeneration));
    }

    internal static void RequireWorkerInstanceId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 35 || !value.StartsWith("wi_", StringComparison.Ordinal) ||
            value.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException(
                "Worker instance id must be 'wi_' followed by exactly 32 lowercase hexadecimal characters.",
                nameof(value));
    }
}
public sealed record NativeSubmissionGuardRetention(
    Guid RetentionId,
    Guid SubmissionAttemptId,
    string NativeRequestBindingSha256,
    string SubmittedRequestSha256,
    string WorkerInstanceId,
    long WorkerGeneration,
    string GuardianInstanceId,
    bool ProcessRooted)
{
    public void Validate()
    {
        NativeContractGuard.RequireGuid(RetentionId, nameof(RetentionId));
        NativeContractGuard.RequireGuid(SubmissionAttemptId, nameof(SubmissionAttemptId));
        NativeContractGuard.RequireSha256(NativeRequestBindingSha256, nameof(NativeRequestBindingSha256));
        NativeContractGuard.RequireSha256(SubmittedRequestSha256, nameof(SubmittedRequestSha256));
        NativeStopRequest.RequireWorkerInstanceId(WorkerInstanceId);
        if (WorkerGeneration < 1) throw new ArgumentOutOfRangeException(nameof(WorkerGeneration));
        NativeContractGuard.RequireText(GuardianInstanceId, 128, nameof(GuardianInstanceId));
        if (!ProcessRooted)
            throw new UnauthorizedAccessException("Uncertain native submission was not transferred to a process-rooted guardian.");
    }

    public void Validate(NativeStopRequest expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        Validate();
        if (SubmissionAttemptId != expected.SubmissionAttemptId ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(NativeRequestBindingSha256), Convert.FromHexString(expected.NativeRequestBindingSha256)) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(SubmittedRequestSha256), Convert.FromHexString(expected.SubmittedRequestSha256)) ||
            !string.Equals(WorkerInstanceId, expected.WorkerInstanceId, StringComparison.Ordinal) ||
            WorkerGeneration != expected.WorkerGeneration)
            throw new UnauthorizedAccessException("Uncertain native submission was not transferred to the exact process-rooted guardian.");
    }
}
public sealed record NativeSubmissionCallbackResult(
    string Disposition,
    NativeSubmission? Submission,
    NativeSubmissionGuardRetention? GuardRetention,
    string ResultCode)
{
    public const string Submitted = "SUBMITTED";
    public const string PendingRetained = "PENDING_RETAINED";
    public const string ExistingUncertain = "EXISTING_UNCERTAIN";
    public const string WaitingExternal = "WAITING_EXTERNAL";

    public bool IsSubmitted => string.Equals(Disposition, Submitted, StringComparison.Ordinal);
    public bool IsPendingRetained => string.Equals(Disposition, PendingRetained, StringComparison.Ordinal);
    public bool IsExistingUncertain => string.Equals(Disposition, ExistingUncertain, StringComparison.Ordinal);

    public void Validate()
    {
        NativeContractGuard.RequireText(Disposition, 64, nameof(Disposition));
        NativeContractGuard.RequireText(ResultCode, 128, nameof(ResultCode));
        if (IsSubmitted)
        {
            ArgumentNullException.ThrowIfNull(Submission);
            if (GuardRetention is not null || !string.Equals(ResultCode, Submitted, StringComparison.Ordinal))
                throw new InvalidDataException("A submitted callback cannot carry retention evidence or a non-SUBMITTED result code.");
            return;
        }
        if (Submission is not null)
            throw new InvalidDataException("An uncertain native callback result cannot expose a submission as reusable authority.");
        if (IsPendingRetained)
        {
            if (GuardRetention is null)
                throw new InvalidDataException("A PENDING-retained callback requires exactly one process-rooted guardian receipt.");
            GuardRetention.Validate();
            NativeContractGuard.RequireExact(ResultCode, WaitingExternal, nameof(ResultCode));
            return;
        }
        if (IsExistingUncertain)
        {
            if (GuardRetention is not null)
                throw new InvalidDataException("An existing uncertain state cannot claim a newly retained submission guard.");
            NativeContractGuard.RequireExact(ResultCode, WaitingExternal, nameof(ResultCode));
            return;
        }
        throw new NotSupportedException($"Unsupported native callback disposition '{Disposition}'.");
    }

    public static NativeSubmissionCallbackResult Success(NativeSubmission submission) =>
        new(Submitted, submission ?? throw new ArgumentNullException(nameof(submission)), null, Submitted);

    public static NativeSubmissionCallbackResult RetainPending(NativeSubmissionGuardRetention guardRetention) =>
        new(PendingRetained, null, guardRetention ?? throw new ArgumentNullException(nameof(guardRetention)), WaitingExternal);

    public static NativeSubmissionCallbackResult WaitForExternalReconciliation() =>
        new(ExistingUncertain, null, null, WaitingExternal);
}
public sealed record GuardedNativeSubmissionResult(
    VerifiedSubmissionPendingAuthorization Pending,
    NativeSubmissionCallbackResult CallbackResult,
    VerifiedSubmissionAcknowledgedAuthorization? Acknowledged,
    VerifiedSubmissionUnknownAuthorization? Unknown,
    bool GuardRetainedUntilProcessExit)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Pending);
        ArgumentNullException.ThrowIfNull(CallbackResult);
        CallbackResult.Validate();
        if (CallbackResult.IsSubmitted)
        {
            if (Acknowledged is null || Unknown is not null || GuardRetainedUntilProcessExit)
                throw new InvalidDataException("A submitted callback requires only a durable owner ACKNOWLEDGED state and a released guard.");
            return;
        }
        if (CallbackResult.IsExistingUncertain)
        {
            if (Acknowledged is not null || GuardRetainedUntilProcessExit)
                throw new InvalidDataException("An existing PENDING or UNKNOWN_SUBMISSION state must remain non-runnable and wait for external reconciliation.");
            return;
        }
        if (Acknowledged is not null || Unknown is not null || !GuardRetainedUntilProcessExit)
            throw new InvalidDataException("An uncertain callback must leave durable PENDING as the only state and retain the session guard until process exit.");
    }
}
public interface INativeSubmissionCompletion : IAsyncDisposable
{
    Guid CompletionHandleId { get; }
    Task<NativeExecutionResponse> WaitForResultAsync(CancellationToken cancellationToken);
}

public static class NativeSubmissionProtocolV1
{
    private const string NativeRequestDomain = "dps.executor-gateway.native-request-binding/v1";
    private const string SubmittedRequestDomain = "dps.executor-gateway.submitted-request/v1";
    private const string AcknowledgementDomain = "dps.executor-gateway.native-submission-ack/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ComputeNativeRequestBindingSha256(NativeExecutionRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Hash(writer => WriteNativeRequestFields(writer, NativeRequestDomain, request));
    }

    public static string ComputeSubmittedRequestSha256(NativeExecutionRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        NativeContractGuard.RequireGuid(request.SubmissionAttemptId, nameof(request.SubmissionAttemptId));
        NativeContractGuard.RequireSha256(request.SubmissionIntentSha256, nameof(request.SubmissionIntentSha256));
        NativeContractGuard.RequireSha256(request.PendingStateSha256, nameof(request.PendingStateSha256));
        return Hash(writer =>
        {
            WriteNativeRequestFields(writer, SubmittedRequestDomain, request);
            writer.Field(request.SubmissionAttemptId);
            writer.Field(request.SubmissionIntentSha256);
            writer.Field(request.PendingStateSha256);
        });
    }

    public static string ComputeAcknowledgementSha256(NativeSubmissionAck acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        acknowledgement.ValidatePayload();
        return Hash(writer =>
        {
            writer.Field(AcknowledgementDomain);
            writer.Field(acknowledgement.SchemaVersion);
            writer.Field(acknowledgement.ContractId);
            writer.Field(acknowledgement.ProducerModule);
            writer.Field(acknowledgement.SubmissionId);
            writer.Field(acknowledgement.CompletionHandleId);
            writer.Field(acknowledgement.CommandId);
            writer.Field(acknowledgement.LeaseId);
            writer.Field(acknowledgement.Attempt);
            writer.Field(acknowledgement.SoulId);
            writer.Field(acknowledgement.DeviceBindingId);
            writer.Field(acknowledgement.PlatformAccountId);
            writer.Field(acknowledgement.TraceId);
            writer.Field(acknowledgement.IdempotencyKey);
            writer.Field(acknowledgement.OccurredAt);
            writer.Field(acknowledgement.PrivacyClass);
            writer.Field(acknowledgement.Durability);
            writer.Field(acknowledgement.CommandSha256);
            writer.Field(acknowledgement.AuthorizationSha256);
            writer.Field(acknowledgement.SubmissionAttemptId);
            writer.Field(acknowledgement.SubmissionIntentSha256);
            writer.Field(acknowledgement.PendingStateSha256);
            writer.Field(acknowledgement.ActiveReleaseBomSha256);
            writer.Field(acknowledgement.ActiveReleaseBomGeneration);
            writer.Field(acknowledgement.ActiveReleaseBomTokenSha256);
            writer.Field(acknowledgement.SubmittedRequestSha256);
        });
    }

    private static void WriteNativeRequestFields(CanonicalWriter writer, string domain, NativeExecutionRequestV1 request)
    {
        writer.Field(domain);
        writer.Field(request.CommandId);
        writer.Field(request.LeaseId);
        writer.Field(request.Attempt);
        writer.Field(request.SoulId);
        writer.Field(request.DeviceBindingId);
        writer.Field(request.PlatformAccountId);
        writer.Field(request.TraceId);
        writer.Field(request.IdempotencyKey);
        writer.Field(request.StepId);
        writer.Field(request.StepKind);
        writer.Field(request.CommandSha256);
        writer.Field(request.AuthorizationSha256);
        writer.Field(request.ActiveReleaseBomSha256);
        writer.Field(request.ActiveReleaseBomGeneration);
        writer.Field(request.ActiveReleaseBomTokenSha256);
    }

    private static string Hash(Action<CanonicalWriter> write)
    {
        using var writer = new CanonicalWriter();
        write(writer);
        var bytes = writer.ToArray();
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
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
public sealed record NativeExecutionResponse(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    Guid NativeResultId,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string ActiveReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomTokenSha256,
    IReadOnlyList<NativeStepResultV1> StepResults);
public interface ITrustedClock { DateTimeOffset GetUtcNow(); }
public sealed class SystemTrustedClock : ITrustedClock { public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow; }
public interface IExecutionAuthorizationVerifier { ValueTask<VerifiedExecutionAuthorization> VerifyAsync(CommandDispatchV1 command, ExecutionAuthorizationV1 authorization, CancellationToken cancellationToken); }
public interface IVerifiedActiveReleaseBomReader
{
    // Production implementations must cryptographically authenticate the supervisor-issued 256-bit token,
    // enforce monotonic generation anti-rollback, and perform a current authoritative read. Cached or caller-supplied values are invalid.
    ValueTask<ActiveReleaseBomBindingV1?> ReadVerifiedActiveAsync(string deviceBindingId, CancellationToken cancellationToken);
}
public interface IApprovalExecutionFenceLease : IAsyncDisposable
{
    ApprovalExecutionFenceRequestV1 Request { get; }
    ApprovalExecutionFenceV1 Fence { get; }
    string FenceRequestSha256 { get; }
    string NativeRequestBindingSha256 { get; }
    // This authoritative revalidation is valid only while the initial policy transaction is open.
    // ExecuteFirstNativeSubmissionAsync later commits PENDING and retains a separate session guard;
    // its callback rechecks the immutable signed fence and current authorization/BOM before dispatch.
    Task<ApprovalExecutionFenceV1> RevalidateForNativeDispatchAsync(CancellationToken cancellationToken = default);
    // This is the only native-submission entrypoint. The production adapter must acquire an
    // owner-coordinated cross-commit session guard, commit the exact PENDING state, and invoke the
    // callback exactly once only when owner Begin returned Disposition=Inserted and MaySubmit=true.
    // Existing PENDING is durable evidence but never permission, so its callback count is zero.
    // No rollbackable business transaction may be active while the callback can cross the native
    // boundary. The session guard must remain held until ACKNOWLEDGED is durably committed for the
    // validated acknowledgement. Every uncertain callback remains PENDING and transfers the guard
    // to the process-rooted guardian. Existing PENDING or UNKNOWN_SUBMISSION state is returned only
    // as WAITING_EXTERNAL and can never reopen submission. Terminal
    // transitions must use the guard session (not a new connection that would deadlock on the same
    // advisory keys). The method returns only after terminal owner state or guardian transfer is
    // durable; process death before then leaves PENDING blocking retries.
    // Production adapters cryptographically verify every owner signature and canonical digest,
    // plus authoritative proposal/request read-back, before returning a VerifiedSubmission wrapper.
    ValueTask<GuardedNativeSubmissionResult> ExecuteFirstNativeSubmissionAsync(
        Func<VerifiedSubmissionPendingAuthorization, CancellationToken, Task<NativeSubmissionCallbackResult>> callback,
        CancellationToken cancellationToken = default);

    // Submission uncertainty is not a terminal state. Before returning PENDING_RETAINED, the adapter
    // atomically transfers the exact policy session/lease, inert native attempt, and any outstanding
    // first-byte task into a process-rooted guardian that has no ordinary release API. A local
    // reference, finalizer, failed DisposeAsync, or fire-and-forget observer is not retention.
    NativeSubmissionGuardRetention RetainGuardUntilProcessExit(
        INativeSubmissionAttempt nativeAttempt,
        Task<NativeSubmission>? outstandingSubmission,
        NativeStopRequest expectedStop);
}
public interface IApprovalExecutionFenceProvider
{
    // The production adapter owns policy-approval credentials and constructs the signed fence authorization.
    // Per-call callers cannot supply or override a fence request, status revision, runtime revision, or signature.
    // Under the same authoritative lock used to issue the lease, the provider must prevent an unreconciled
    // PENDING, historical UNKNOWN, or ACKNOWLEDGED state for the exact nativeRequestBindingSha256 from
    // reaching the native callback; existing uncertain state is returned only as WAITING_EXTERNAL.
    Task<IApprovalExecutionFenceLease> AcquireAsync(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        string nativeRequestBindingSha256,
        CancellationToken cancellationToken = default);
}
public interface INativeCommandExecutor
{
    // The inert capability is created before durable PENDING and receives no command/request. It
    // may start an isolated worker or allocate a handle, but MUST NOT write, flush, enqueue, or
    // otherwise cross a device boundary. The only operation allowed to write the first native byte
    // is SubmitFirstByteAsync after policy has committed and validated the exact PENDING state.
    INativeSubmissionAttempt CreateInertSubmissionAttempt();
}
public interface INativeSubmissionAttempt : IAsyncDisposable
{
    string WorkerInstanceId { get; }
    long WorkerGeneration { get; }
    // This method may be called exactly once and returns only after a durable native submission
    // acknowledgement exists. Completion of the phone action remains on NativeSubmission.Completion.
    Task<NativeSubmission> SubmitFirstByteAsync(
        NativeExecutionRequestV1 request,
        CancellationToken cancellationToken);
}
public interface IBusinessPostconditionVerifier { Task<PostconditionVerification> VerifyAsync(CommandDispatchV1 command, NativeResultV1 nativeResult, CancellationToken cancellationToken); }
public interface IExecutorProcessFailStop
{
    // Production implementations must synchronously terminate the Executor host and every
    // non-orphanable native worker. Returning is a contract violation and is converted to a throw.
    void TerminateProcess(string reasonCode, Exception cause);
}
public interface ICommandExecutionGateway
{
    Task<SignedCommandReceiptV1> ExecuteAsync(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorizationEnvelope,
        CancellationToken cancellationToken = default);
}

public sealed class VerifiedExecutorGateway : ICommandExecutionGateway
{
    private readonly ITrustedClock _trustedClock;
    private readonly EcdsaCommandReceiptSigner _receiptSigner;
    private readonly IExecutionAuthorizationVerifier _authorizationVerifier;
    private readonly IVerifiedActiveReleaseBomReader _activeReleaseBomReader;
    private readonly IApprovalExecutionFenceProvider _approvalExecutionFenceProvider;
    private readonly INativeCommandExecutor _nativeExecutor;
    private readonly IExecutorProcessFailStop _processFailStop;
    private readonly IBusinessPostconditionVerifier _postconditionVerifier;
    private readonly TimeSpan _timeout;

    public VerifiedExecutorGateway(
        ITrustedClock trustedClock,
        EcdsaCommandReceiptSigner receiptSigner,
        IExecutionAuthorizationVerifier authorizationVerifier,
        IVerifiedActiveReleaseBomReader activeReleaseBomReader,
        IApprovalExecutionFenceProvider approvalExecutionFenceProvider,
        INativeCommandExecutor nativeExecutor,
        IExecutorProcessFailStop processFailStop,
        IBusinessPostconditionVerifier postconditionVerifier,
        TimeSpan timeout)
    {
        _trustedClock = trustedClock ?? throw new ArgumentNullException(nameof(trustedClock));
        _receiptSigner = receiptSigner ?? throw new ArgumentNullException(nameof(receiptSigner));
        _authorizationVerifier = authorizationVerifier ?? throw new ArgumentNullException(nameof(authorizationVerifier));
        _activeReleaseBomReader = activeReleaseBomReader ?? throw new ArgumentNullException(nameof(activeReleaseBomReader));
        _approvalExecutionFenceProvider = approvalExecutionFenceProvider ?? throw new ArgumentNullException(nameof(approvalExecutionFenceProvider));
        _nativeExecutor = nativeExecutor ?? throw new ArgumentNullException(nameof(nativeExecutor));
        _processFailStop = processFailStop ?? throw new ArgumentNullException(nameof(processFailStop));
        _postconditionVerifier = postconditionVerifier ?? throw new ArgumentNullException(nameof(postconditionVerifier));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    public async Task<SignedCommandReceiptV1> ExecuteAsync(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorizationEnvelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authorizationEnvelope);
        command = SnapshotCommand(command);
        command.Validate();
        authorizationEnvelope.Validate();

        var authorization = await _authorizationVerifier.VerifyAsync(command, authorizationEnvelope, cancellationToken);
        var afterAuthorization = ReadTrustedNow();
        ValidateAuthorization(command, authorizationEnvelope, authorization, afterAuthorization);
        var activeBinding = await ReadInitialActiveReleaseBomAsync(command, authorization.Authorization, _activeReleaseBomReader, cancellationToken);
        var nativeRequest = Snapshot(command, authorization.Authorization, activeBinding);
        var nativeRequestBindingSha256 = NativeSubmissionProtocolV1.ComputeNativeRequestBindingSha256(nativeRequest);

        using var cancellationScope = new BoundaryCancellationScope(cancellationToken);
        var timeoutToken = cancellationScope.Token;
        var fenceLease = await _approvalExecutionFenceProvider.AcquireAsync(
            command, authorization.Authorization, nativeRequestBindingSha256, timeoutToken);
        ArgumentNullException.ThrowIfNull(fenceLease);
        DateTimeOffset beforeNative = afterAuthorization;
        ApprovalExecutionFenceRequestV1 fenceRequest;
        ApprovalExecutionFenceV1 originalFence;
        try
        {
            var beforeFenceRevalidation = ReadTrustedNow(afterAuthorization);
            fenceRequest = SnapshotFenceRequest(fenceLease.Request);
            originalFence = SnapshotFence(fenceLease.Fence);
            NativeContractGuard.RequireSha256(fenceLease.NativeRequestBindingSha256, nameof(fenceLease.NativeRequestBindingSha256));
            if (!FixedDigestEquals(fenceLease.NativeRequestBindingSha256, nativeRequestBindingSha256))
                throw new UnauthorizedAccessException("Approval execution fence lease is bound to another native submission request.");
            ValidateApprovalFenceBinding(command, authorization.Authorization, fenceRequest, originalFence, beforeFenceRevalidation);
            _ = await ReadInitialActiveReleaseBomAsync(command, authorization.Authorization, _activeReleaseBomReader, timeoutToken);
            var revalidatedFence = SnapshotFence(await fenceLease.RevalidateForNativeDispatchAsync(timeoutToken));
            beforeNative = ReadTrustedNow(beforeFenceRevalidation);
            ValidateAuthorization(command, authorizationEnvelope, authorization, beforeNative);
            ValidateApprovalFenceBinding(command, authorization.Authorization, fenceRequest, revalidatedFence, beforeNative);
            if (!Equals(originalFence, revalidatedFence))
                throw new UnauthorizedAccessException("Approval execution fence changed while its native-dispatch lease was held.");
        }
        catch
        {
            _ = await TryDisposeAsync(fenceLease, _timeout);
            throw;
        }

        INativeSubmissionAttempt nativeAttempt;
        try
        {
            nativeAttempt = _nativeExecutor.CreateInertSubmissionAttempt() ??
                throw new InvalidDataException("Native executor returned no inert single-use submission attempt.");
        }
        catch
        {
            _ = await TryDisposeAsync(fenceLease, _timeout);
            throw;
        }

        // Policy owns the only PENDING -> first native callback -> ACKNOWLEDGED sequence.
        // Its cross-commit session guard spans the whole callback, but the callback itself runs with
        // no rollbackable policy transaction. Existing PENDING never enters the callback.
        var nativeCallbackInvocationCount = 0;
        GuardedNativeSubmissionResult guardedSubmission;
        try
        {
            guardedSubmission = await fenceLease.ExecuteFirstNativeSubmissionAsync(
                async (pendingAuthorization, callbackToken) =>
                {
                    if (Interlocked.Increment(ref nativeCallbackInvocationCount) != 1)
                        throw new InvalidDataException(
                            "Policy Approval invoked the single-use native callback more than once.");
                    ArgumentNullException.ThrowIfNull(pendingAuthorization);
                    nativeRequest = BindPending(nativeRequest, pendingAuthorization);
                    var stopRequest = CreateNativeStopRequest(nativeRequest, nativeRequestBindingSha256, nativeAttempt);
                    try
                    {
                        var afterPending = ReadTrustedNow(beforeNative);
                        ValidateSubmissionPending(
                            command,
                            authorization.Authorization,
                            pendingAuthorization,
                            originalFence,
                            fenceRequest,
                            fenceLease.FenceRequestSha256,
                            nativeRequestBindingSha256,
                            afterPending);
                        _ = NativeSubmissionProtocolV1.ComputeSubmittedRequestSha256(nativeRequest);
                        ValidateAuthorization(command, authorizationEnvelope, authorization, afterPending);
                        _ = await ReadInitialActiveReleaseBomAsync(
                            command,
                            authorization.Authorization,
                            _activeReleaseBomReader,
                            callbackToken);
                        beforeNative = ReadTrustedNow(afterPending);
                        ValidateAuthorization(command, authorizationEnvelope, authorization, beforeNative);
                        ValidateApprovalFenceBinding(
                            command,
                            authorization.Authorization,
                            fenceRequest,
                            originalFence,
                            beforeNative);
                    }
                    catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
                    {
                        return await AbortOrRetainPendingAsync(
                            fenceLease,
                            nativeAttempt,
                            null,
                            null,
                            stopRequest,
                            "AUTHORITY_TRANSITION_UNCERTAIN",
                            cancellationScope).ConfigureAwait(false);
                    }

                    Task<NativeSubmission>? submissionTask;
                    try { submissionTask = nativeAttempt.SubmitFirstByteAsync(nativeRequest, callbackToken); }
                    catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
                    {
                        return await AbortOrRetainPendingAsync(
                            fenceLease,
                            nativeAttempt,
                            null,
                            null,
                            stopRequest,
                            "NATIVE_SUBMISSION_UNCERTAIN",
                            cancellationScope).ConfigureAwait(false);
                    }
                    if (submissionTask is null)
                    {
                        return await AbortOrRetainPendingAsync(
                            fenceLease,
                            nativeAttempt,
                            null,
                            null,
                            stopRequest,
                            "NATIVE_SUBMISSION_NULL",
                            cancellationScope).ConfigureAwait(false);
                    }

                    NativeSubmission submission;
                    try { submission = await submissionTask.WaitAsync(_timeout, CancellationToken.None); }
                    catch (TimeoutException)
                    {
                        return await AbortOrRetainPendingAsync(
                            fenceLease,
                            nativeAttempt,
                            submissionTask,
                            null,
                            stopRequest,
                            "NATIVE_SUBMISSION_TIMEOUT",
                            cancellationScope).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (callbackToken.IsCancellationRequested)
                    {
                        return await AbortOrRetainPendingAsync(
                            fenceLease,
                            nativeAttempt,
                            submissionTask,
                            null,
                            stopRequest,
                            cancellationToken.IsCancellationRequested
                                ? "NATIVE_SUBMISSION_CANCELLED"
                                : "NATIVE_SUBMISSION_TIMEOUT",
                            cancellationScope).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
                    {
                        return await AbortOrRetainPendingAsync(
                            fenceLease,
                            nativeAttempt,
                            submissionTask,
                            null,
                            stopRequest,
                            "NATIVE_SUBMISSION_UNCERTAIN",
                            cancellationScope).ConfigureAwait(false);
                    }

                    try
                    {
                        ArgumentNullException.ThrowIfNull(submission);
                        var acknowledgement = SnapshotSubmissionAck(submission.Acknowledgement);
                        var completion = submission.Completion ??
                            throw new InvalidDataException("Native submission completion handle is missing.");
                        var afterAcknowledgement = ReadTrustedNow(beforeNative);
                        ValidateSubmissionAcknowledgement(
                            nativeRequest,
                            authorization.Authorization,
                            acknowledgement,
                            completion.CompletionHandleId,
                            afterAcknowledgement);
                        beforeNative = afterAcknowledgement;
                        _ = await TryDisposeAsync(nativeAttempt, _timeout).ConfigureAwait(false);
                        return NativeSubmissionCallbackResult.Success(
                            new NativeSubmission(acknowledgement, completion));
                    }
                    catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
                    {
                        return await AbortOrRetainPendingAsync(
                            fenceLease,
                            nativeAttempt,
                            submissionTask,
                            submission,
                            stopRequest,
                            "NATIVE_SUBMISSION_ACK_INVALID",
                            cancellationScope).ConfigureAwait(false);
                    }
                },
                timeoutToken);
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            cancellationScope.RequestBestEffortCancellation();
            throw FailStop(
                "APPROVAL_GUARDED_SUBMISSION_UNCERTAIN",
                exception);
        }

        NativeSubmission submission;
        NativeSubmissionAck acknowledgement;
        INativeSubmissionCompletion completion;
        VerifiedSubmissionPendingAuthorization pending;
        try
        {
            ArgumentNullException.ThrowIfNull(guardedSubmission);
            guardedSubmission.Validate();
            var observedCallbackInvocations = Volatile.Read(ref nativeCallbackInvocationCount);
            if ((guardedSubmission.CallbackResult.IsExistingUncertain && observedCallbackInvocations != 0) ||
                (!guardedSubmission.CallbackResult.IsExistingUncertain && observedCallbackInvocations != 1))
                throw new InvalidDataException(
                    "Policy Approval returned a guarded-submission disposition that does not match the native callback invocation count.");
            pending = guardedSubmission.Pending;
            var afterTerminal = ReadTrustedNow(beforeNative);
            ValidateSubmissionPending(
                command,
                authorization.Authorization,
                pending,
                originalFence,
                fenceRequest,
                fenceLease.FenceRequestSha256,
                nativeRequestBindingSha256,
                afterTerminal);
            nativeRequest = BindPending(nativeRequest, pending);

            if (guardedSubmission.CallbackResult.IsExistingUncertain)
            {
                if (guardedSubmission.Unknown is not null)
                    ValidateExistingSubmissionUnknown(
                        command,
                        authorization.Authorization,
                        pending,
                        guardedSubmission.Unknown,
                        nativeRequestBindingSha256,
                        afterTerminal);
                _ = await TryDisposeAsync(nativeAttempt, _timeout).ConfigureAwait(false);
                _ = await TryDisposeAsync(fenceLease, _timeout).ConfigureAwait(false);
                return Receipt(command, authorization.Authorization, nativeRequest, afterTerminal,
                    CommandReceiptV1.UnknownOutcome, null, false, false, false,
                    NativeSubmissionCallbackResult.WaitingExternal);
            }

            if (guardedSubmission.CallbackResult.IsPendingRetained)
            {
                // Do not dispose this lease. Its provider must keep the exact guard connection
                // strongly rooted until executor process death; durable PENDING blocks retry.
                guardedSubmission.CallbackResult.GuardRetention!.Validate(
                    CreateNativeStopRequest(nativeRequest, nativeRequestBindingSha256, nativeAttempt));
                return Receipt(command, authorization.Authorization, nativeRequest, afterTerminal,
                    CommandReceiptV1.UnknownOutcome, null, false, false, false,
                    NativeSubmissionCallbackResult.WaitingExternal);
            }

            if (guardedSubmission.Unknown is not null || guardedSubmission.Acknowledged is null)
                throw new InvalidDataException("A submitted native callback requires only a durable owner ACKNOWLEDGED state.");
            submission = guardedSubmission.CallbackResult.Submission!;
            acknowledgement = SnapshotSubmissionAck(submission.Acknowledgement);
            completion = submission.Completion ??
                throw new InvalidDataException("Native submission completion handle is missing.");
            ValidateSubmissionAcknowledgement(
                nativeRequest,
                authorization.Authorization,
                acknowledgement,
                completion.CompletionHandleId,
                afterTerminal);
            ValidateSubmissionAcknowledged(
                nativeRequest,
                acknowledgement,
                pending,
                guardedSubmission.Acknowledged,
                nativeRequestBindingSha256,
                afterTerminal);
            beforeNative = afterTerminal;
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            cancellationScope.RequestBestEffortCancellation();
            if (guardedSubmission?.CallbackResult?.Submission?.Completion is not null)
                _ = ObserveCompletionDisposalAsync(
                    guardedSubmission.CallbackResult.Submission.Completion,
                    _timeout);
            throw FailStop(
                "APPROVAL_SUBMISSION_TERMINAL_INVALID",
                exception);
        }

        // Only an authority-committed ACKNOWLEDGED transition for a validated durable
        // acknowledgement allows the approval fence to be released.
        if (!await TryDisposeAsync(fenceLease, _timeout))
        {
            cancellationScope.RequestBestEffortCancellation();
            _ = ObserveCompletionDisposalAsync(completion, _timeout);
            var receiptTime = TryReadTrustedNow(beforeNative, out var afterFenceReleaseFailure) ? afterFenceReleaseFailure : beforeNative;
            return Receipt(command, authorization.Authorization, nativeRequest, receiptTime, CommandReceiptV1.UnknownOutcome, null, false, false, false, "APPROVAL_FENCE_RELEASE_UNCERTAIN");
        }

        NativeExecutionResponse nativeResponse;
        try
        {
            var completionTask = completion.WaitForResultAsync(timeoutToken);
            ArgumentNullException.ThrowIfNull(completionTask);
            nativeResponse = await completionTask.WaitAsync(_timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            cancellationScope.RequestBestEffortCancellation();
            _ = ObserveCompletionDisposalAsync(completion, _timeout);
            var receiptTime = TryReadTrustedNow(beforeNative, out var afterTimeout) ? afterTimeout : beforeNative;
            return Receipt(command, authorization.Authorization, nativeRequest, receiptTime, CommandReceiptV1.UnknownOutcome, null, false, false, false, "NATIVE_TIMEOUT");
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            cancellationScope.RequestBestEffortCancellation();
            _ = ObserveCompletionDisposalAsync(completion, _timeout);
            var receiptTime = TryReadTrustedNow(beforeNative, out var afterTimeout) ? afterTimeout : beforeNative;
            return Receipt(command, authorization.Authorization, nativeRequest, receiptTime, CommandReceiptV1.UnknownOutcome, null, false, false, false, "NATIVE_TIMEOUT");
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            _ = ObserveCompletionDisposalAsync(completion, _timeout);
            var receiptTime = TryReadTrustedNow(beforeNative, out var afterFailure) ? afterFailure : beforeNative;
            return Receipt(command, authorization.Authorization, nativeRequest, receiptTime, CommandReceiptV1.UnknownOutcome, null, false, false, false, "NATIVE_BOUNDARY_FAILURE");
        }

        if (!await TryDisposeAsync(completion, _timeout))
        {
            var receiptTime = TryReadTrustedNow(beforeNative, out var afterCompletionReleaseFailure) ? afterCompletionReleaseFailure : beforeNative;
            return Receipt(command, authorization.Authorization, nativeRequest, receiptTime, CommandReceiptV1.UnknownOutcome,
                nativeResponse is null || nativeResponse.NativeResultId == Guid.Empty ? null : nativeResponse.NativeResultId,
                false, false, false, "NATIVE_COMPLETION_RELEASE_UNCERTAIN");
        }

        if (!TryReadTrustedNow(beforeNative, out var afterNative))
            return Receipt(command, authorization.Authorization, nativeRequest, beforeNative, CommandReceiptV1.UnknownOutcome,
                nativeResponse is null || nativeResponse.NativeResultId == Guid.Empty ? null : nativeResponse.NativeResultId,
                false, false, false, "TRUSTED_CLOCK_UNAVAILABLE_AFTER_NATIVE");
        NativeResultV1 nativeResult;
        try
        {
            ArgumentNullException.ThrowIfNull(nativeResponse);
            CommandContractGuard.RequireGuid(nativeResponse.NativeResultId, nameof(nativeResponse.NativeResultId));
            CommandContractGuard.RequireUtc(nativeResponse.OccurredAt, nameof(nativeResponse.OccurredAt));
            var nativeStepSnapshot = SnapshotNativeStepResults(nativeResponse.StepResults);
            nativeResult = new NativeResultV1(
                nativeResponse.SchemaVersion, nativeResponse.ContractId, nativeResponse.ProducerModule,
                nativeResponse.NativeResultId, nativeResponse.CommandId, nativeResponse.LeaseId, nativeResponse.Attempt,
                nativeResponse.SoulId, nativeResponse.DeviceBindingId, nativeResponse.PlatformAccountId,
                nativeResponse.TraceId, nativeResponse.IdempotencyKey, nativeResponse.OccurredAt, "internal",
                nativeResponse.ActiveReleaseBomSha256, nativeResponse.ActiveReleaseBomGeneration,
                nativeResponse.ActiveReleaseBomTokenSha256, nativeStepSnapshot);
            nativeResult.Validate();
            ValidateNativeScope(nativeRequest, nativeResult, afterNative);
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            return Receipt(command, authorization.Authorization, nativeRequest, afterNative, CommandReceiptV1.UnknownOutcome,
                nativeResponse is null || nativeResponse.NativeResultId == Guid.Empty ? null : nativeResponse.NativeResultId,
                false, false, false, "NATIVE_CONTRACT_OR_SCOPE_INVALID");
        }
        var nativeEvidence = NativeEvidenceDigest(nativeResult);

        if (!IsTemporallyValid(command, authorization.Authorization, afterNative))
            return Receipt(command, authorization.Authorization, nativeRequest, afterNative, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "AUTH_EXPIRED_AFTER_NATIVE", nativeEvidence);

        var afterNativeBinding = await ReadActiveReleaseBomAfterNativeAsync(nativeRequest, _activeReleaseBomReader, cancellationToken);
        if (!TryReadTrustedNow(afterNative, out var afterNativeBindingTime))
            return Receipt(command, authorization.Authorization, nativeRequest, afterNative, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "TRUSTED_CLOCK_UNAVAILABLE_AFTER_NATIVE", nativeEvidence);
        if (afterNativeBinding is null || !IsTemporallyValid(command, authorization.Authorization, afterNativeBindingTime))
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false,
                afterNativeBinding is null ? "ACTIVE_BOM_CHANGED_AFTER_NATIVE" : "AUTH_EXPIRED_AFTER_NATIVE", nativeEvidence);

        var step = command.Steps[0];
        var nativeStep = nativeResult.StepResults[0];
        if (step.StepId != nativeStep.StepId || !string.Equals(step.StepKind, nativeStep.StepKind, StringComparison.Ordinal))
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "NATIVE_CONTRACT_OR_SCOPE_INVALID", nativeEvidence);
        if (nativeStep.Status == NativeStepResultV1.Unknown)
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "NATIVE_UNKNOWN", nativeEvidence);
        if (nativeStep.Status == NativeStepResultV1.Failed)
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.Failed, nativeResult.NativeResultId, true, false, step.RetrySafe, "NATIVE_FAILED", nativeEvidence);

        PostconditionVerification postcondition;
        try { postcondition = await _postconditionVerifier.VerifyAsync(command, nativeResult, timeoutToken).WaitAsync(_timeout, cancellationToken); }
        catch (TimeoutException)
        {
            cancellationScope.RequestBestEffortCancellation();
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "POSTCONDITION_TIMEOUT", nativeEvidence);
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "POSTCONDITION_UNAVAILABLE", nativeEvidence);
        }
        try
        {
            ArgumentNullException.ThrowIfNull(postcondition);
            NativeContractGuard.RequireSha256(postcondition.EvidenceDigest, nameof(postcondition.EvidenceDigest));
            NativeContractGuard.RequireText(postcondition.ResultCode, 128, nameof(postcondition.ResultCode));
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "POSTCONDITION_CONTRACT_INVALID", nativeEvidence);
        }
        if (!TryReadTrustedNow(afterNativeBindingTime, out var afterPostcondition))
            return Receipt(command, authorization.Authorization, nativeRequest, afterNativeBindingTime, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "TRUSTED_CLOCK_UNAVAILABLE_BEFORE_RECEIPT", nativeEvidence, postcondition.EvidenceDigest);
        var finalBinding = await ReadActiveReleaseBomAfterNativeAsync(nativeRequest, _activeReleaseBomReader, cancellationToken);
        if (!TryReadTrustedNow(afterPostcondition, out var beforeReceipt))
            return Receipt(command, authorization.Authorization, nativeRequest, afterPostcondition, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false, "TRUSTED_CLOCK_UNAVAILABLE_BEFORE_RECEIPT", nativeEvidence, postcondition.EvidenceDigest);
        if (finalBinding is null || !IsTemporallyValid(command, authorization.Authorization, beforeReceipt))
            return Receipt(command, authorization.Authorization, nativeRequest, beforeReceipt, CommandReceiptV1.UnknownOutcome, nativeResult.NativeResultId, false, false, false,
                finalBinding is null ? "ACTIVE_BOM_CHANGED_BEFORE_RECEIPT" : "AUTH_EXPIRED_BEFORE_RECEIPT", nativeEvidence, postcondition.EvidenceDigest);
        return postcondition.Verified
            ? Receipt(command, authorization.Authorization, nativeRequest, beforeReceipt, CommandReceiptV1.Success, nativeResult.NativeResultId, true, true, false, "VERIFIED", nativeEvidence, postcondition.EvidenceDigest)
            : Receipt(command, authorization.Authorization, nativeRequest, beforeReceipt, CommandReceiptV1.Failed, nativeResult.NativeResultId, true, false, false, "POSTCONDITION_FAILED", nativeEvidence, postcondition.EvidenceDigest);
    }

    private static void ValidateAuthorization(CommandDispatchV1 command, ExecutionAuthorizationV1 envelope, VerifiedExecutionAuthorization verified, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(verified.Authorization);
        CommandContractGuard.RequireUtc(now, nameof(now));
        verified.Authorization.Validate();
        if (!FixedCanonicalEquals(envelope, verified.Authorization))
            throw new UnauthorizedAccessException("Verifier result does not match the signed execution-authorization payload.");

        var authorization = verified.Authorization;
        if (authorization.CommandId != command.CommandId || authorization.LeaseId != command.LeaseId || authorization.Attempt != command.Attempt ||
            !string.Equals(authorization.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(authorization.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(authorization.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(authorization.TraceId, command.TraceId, StringComparison.Ordinal) ||
            !string.Equals(authorization.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Authorization scope does not match the exact command and lease.");
        if (!FixedDigestEquals(authorization.CommandSha256, ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command)))
            throw new UnauthorizedAccessException("Authorization is not bound to the exact command payload.");
        if (authorization.ShadowMode) throw new UnauthorizedAccessException("Shadow mode may not execute a real command.");
        if (!IsTemporallyValid(command, authorization, now))
            throw new UnauthorizedAccessException("Authorization or lease is not currently valid.");
    }

    private static bool IsTemporallyValid(CommandDispatchV1 command, ExecutionAuthorizationV1 authorization, DateTimeOffset now) =>
        authorization.OccurredAt >= command.OccurredAt && authorization.OccurredAt <= now &&
        authorization.ValidUntil > now && authorization.ValidUntil <= command.LeaseExpiresAt && command.LeaseExpiresAt > now;

    private static async ValueTask<ActiveReleaseBomBindingV1> ReadInitialActiveReleaseBomAsync(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        IVerifiedActiveReleaseBomReader reader,
        CancellationToken cancellationToken)
    {
        var active = await reader.ReadVerifiedActiveAsync(command.DeviceBindingId, cancellationToken);
        if (active is null) throw new UnauthorizedAccessException("Current active Release BOM is unavailable.");
        active.Validate();
        var tokenSha256 = active.ComputeExecutionTokenSha256();
        if (!string.Equals(active.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !FixedDigestEquals(active.ReleaseBomSha256, authorization.ReleaseBomSha256) ||
            active.Generation != authorization.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(tokenSha256, authorization.ActiveReleaseBomTokenSha256))
            throw new UnauthorizedAccessException("Signed authorization does not match the exact active Release BOM generation and execution token.");
        return active;
    }

    private static void ValidateApprovalFenceBinding(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        ApprovalExecutionFenceRequestV1 request,
        ApprovalExecutionFenceV1 fence,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fence);
        request.Validate();
        fence.Validate();
        CommandContractGuard.RequireUtc(observedAt, nameof(observedAt));
        if (request.ApprovalId != command.ApprovalId ||
            !string.Equals(request.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(request.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(request.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(request.TraceId, command.TraceId, StringComparison.Ordinal) ||
            !string.Equals(request.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(request.ApprovalSha256, command.ApprovalSha256) ||
            !FixedDigestEquals(request.ExpectedReleaseBomSha256, authorization.ReleaseBomSha256))
            throw new UnauthorizedAccessException("Approval execution fence request is outside the exact command, approval, identity, or Release BOM scope.");
        if (fence.ApprovalId != request.ApprovalId || fence.ProposalId != request.ProposalId ||
            !string.Equals(fence.SoulId, request.SoulId, StringComparison.Ordinal) ||
            !string.Equals(fence.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(fence.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(fence.TraceId, request.TraceId, StringComparison.Ordinal) ||
            !string.Equals(fence.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(fence.ApprovalSha256, request.ApprovalSha256) ||
            fence.StatusRevision != request.ExpectedStatusRevision ||
            fence.RuntimeRevision != request.ExpectedRuntimeRevision ||
            !FixedDigestEquals(fence.RuntimeStateSha256, request.ExpectedRuntimeStateSha256) ||
            !FixedDigestEquals(fence.ReleaseBomSha256, request.ExpectedReleaseBomSha256) ||
            fence.AcquiredAt > observedAt || fence.ValidUntil <= observedAt)
            throw new UnauthorizedAccessException("Approval execution fence does not match its exact request or is not currently valid.");
    }

    private static async ValueTask<ActiveReleaseBomBindingV1?> ReadActiveReleaseBomAfterNativeAsync(
        NativeExecutionRequestV1 request,
        IVerifiedActiveReleaseBomReader reader,
        CancellationToken cancellationToken)
    {
        ActiveReleaseBomBindingV1? active;
        try { active = await reader.ReadVerifiedActiveAsync(request.DeviceBindingId, cancellationToken); }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception)) { return null; }
        if (active is null) return null;
        try { active.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
        return string.Equals(active.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) &&
               FixedDigestEquals(active.ReleaseBomSha256, request.ActiveReleaseBomSha256) &&
               active.Generation == request.ActiveReleaseBomGeneration &&
               FixedDigestEquals(active.ComputeExecutionTokenSha256(), request.ActiveReleaseBomTokenSha256)
            ? active
            : null;
    }

    private static CommandDispatchV1 SnapshotCommand(CommandDispatchV1 command)
    {
        ArgumentNullException.ThrowIfNull(command.Steps);
        if (command.Steps.Count != 1) throw new InvalidOperationException("The v1 command protocol requires exactly one compiled step.");
        var step = command.Steps[0] ?? throw new ArgumentException("Command step cannot be null.", nameof(command));
        ArgumentNullException.ThrowIfNull(step.Arguments);
        var arguments = step.Arguments.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var stepSnapshot = step with { Arguments = arguments };
        return command with { Steps = Array.AsReadOnly([stepSnapshot]) };
    }

    private static IReadOnlyList<NativeStepResultV1> SnapshotNativeStepResults(IReadOnlyList<NativeStepResultV1>? stepResults)
    {
        ArgumentNullException.ThrowIfNull(stepResults);
        if (stepResults.Count != 1) throw new InvalidOperationException("The v1 native result requires exactly one ordered step result.");
        var step = stepResults[0] ?? throw new ArgumentException("Native step result cannot be null.", nameof(stepResults));
        return Array.AsReadOnly([step with { }]);
    }

    private static NativeExecutionRequestV1 Snapshot(CommandDispatchV1 command, ExecutionAuthorizationV1 authorization, ActiveReleaseBomBindingV1 active)
    {
        var step = command.Steps[0];
        return new NativeExecutionRequestV1(
            command, command.CommandId, command.LeaseId, command.Attempt,
            command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.TraceId, command.IdempotencyKey,
            step.StepId, step.StepKind,
            ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command),
            ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization),
            Guid.Empty, string.Empty, string.Empty,
            active.ReleaseBomSha256, active.Generation, active.ExecutionTokenBase64,
            active.ComputeExecutionTokenSha256());
    }

    private static NativeExecutionRequestV1 BindPending(
        NativeExecutionRequestV1 request,
        VerifiedSubmissionPendingAuthorization pending) => request with
    {
        SubmissionAttemptId = pending.Intent.SubmissionAttemptId,
        SubmissionIntentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(pending.Intent),
        PendingStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(pending.PendingState)
    };

    private static ApprovalExecutionFenceRequestV1 SnapshotFenceRequest(ApprovalExecutionFenceRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request with { };
    }

    private static ApprovalExecutionFenceV1 SnapshotFence(ApprovalExecutionFenceV1 fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        return fence with { };
    }

    private static NativeSubmissionAck SnapshotSubmissionAck(NativeSubmissionAck acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        return acknowledgement with { };
    }

    private static void ValidateSubmissionPending(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        VerifiedSubmissionPendingAuthorization pending,
        ApprovalExecutionFenceV1 fence,
        ApprovalExecutionFenceRequestV1 fenceRequest,
        string fenceRequestSha256,
        string nativeRequestBindingSha256,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(pending.Intent);
        ArgumentNullException.ThrowIfNull(pending.PendingState);
        pending.Intent.Validate();
        pending.PendingState.Validate();
        NativeContractGuard.RequireSha256(fenceRequestSha256, nameof(fenceRequestSha256));
        NativeContractGuard.RequireUtc(observedAt, nameof(observedAt));
        var intent = pending.Intent;
        var state = pending.PendingState;
        var expectedAuthorizationSha256 = ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization);
        var expectedIntentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent);
        var expectedStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(state);

        if (intent.ApprovalId != command.ApprovalId ||
            intent.ProposalId != fence.ProposalId ||
            intent.ProposalId != fenceRequest.ProposalId ||
            intent.CommandId != command.CommandId ||
            intent.LeaseId != command.LeaseId ||
            intent.Attempt != command.Attempt ||
            !string.Equals(intent.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(intent.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(intent.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(intent.TraceId, command.TraceId, StringComparison.Ordinal) ||
            !string.Equals(intent.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(intent.FenceRequestSha256, fenceRequestSha256) ||
            !FixedDigestEquals(intent.ApprovalSha256, command.ApprovalSha256) ||
            intent.StatusRevision != fence.StatusRevision ||
            intent.RuntimeRevision != fence.RuntimeRevision ||
            !FixedDigestEquals(intent.RuntimeStateSha256, fence.RuntimeStateSha256) ||
            !FixedDigestEquals(intent.ReleaseBomSha256, authorization.ReleaseBomSha256) ||
            intent.ReleaseBomGeneration != authorization.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(intent.ExecutionAuthorizationSha256, expectedAuthorizationSha256) ||
            !FixedDigestEquals(intent.NativeRequestBindingSha256, nativeRequestBindingSha256) ||
            intent.OccurredAt < fence.AcquiredAt || intent.OccurredAt > observedAt ||
            intent.ValidUntil <= observedAt ||
            state.SubmissionAttemptId != intent.SubmissionAttemptId ||
            state.ApprovalId != intent.ApprovalId ||
            state.ProposalId != intent.ProposalId ||
            state.CommandId != intent.CommandId ||
            state.LeaseId != intent.LeaseId ||
            state.Attempt != intent.Attempt ||
            !string.Equals(state.SoulId, intent.SoulId, StringComparison.Ordinal) ||
            !string.Equals(state.DeviceBindingId, intent.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(state.PlatformAccountId, intent.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(state.TraceId, intent.TraceId, StringComparison.Ordinal) ||
            !string.Equals(state.IdempotencyKey, intent.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(state.ReleaseBomSha256, intent.ReleaseBomSha256) ||
            state.ReleaseBomGeneration != intent.ReleaseBomGeneration ||
            !FixedDigestEquals(state.NativeRequestBindingSha256, nativeRequestBindingSha256) ||
            !FixedDigestEquals(state.SubmissionIntentSha256, expectedIntentSha256) ||
            !string.Equals(state.State, ApprovalSubmissionStateV1.SubmissionPending, StringComparison.Ordinal) ||
            state.PredecessorStateSha256 is not null ||
            !FixedDigestEquals(state.EvidenceSha256, expectedIntentSha256) ||
            !FixedDigestEquals(state.StateSha256, expectedStateSha256) ||
            state.OccurredAt < intent.OccurredAt || state.OccurredAt > observedAt)
            throw new UnauthorizedAccessException("Policy Approval did not return the exact durable SUBMISSION_PENDING state for this command, fence, authorization, BOM, and native request binding.");
    }

    private static void ValidateSubmissionAcknowledgement(
        NativeExecutionRequestV1 request,
        ExecutionAuthorizationV1 authorization,
        NativeSubmissionAck acknowledgement,
        Guid completionHandleId,
        DateTimeOffset observedAt)
    {
        acknowledgement.Validate();
        NativeContractGuard.RequireGuid(completionHandleId, nameof(completionHandleId));
        NativeContractGuard.RequireUtc(observedAt, nameof(observedAt));
        var expectedCommandSha256 = ExecutionAuthorizationProtocolV1.ComputeCommandSha256(request.Command);
        var expectedAuthorizationSha256 = ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization);
        var expectedSubmittedRequestSha256 = NativeSubmissionProtocolV1.ComputeSubmittedRequestSha256(request);
        var expectedAcknowledgementSha256 = NativeSubmissionProtocolV1.ComputeAcknowledgementSha256(acknowledgement);
        if (acknowledgement.CompletionHandleId != completionHandleId ||
            acknowledgement.CommandId != request.CommandId ||
            acknowledgement.LeaseId != request.LeaseId ||
            acknowledgement.Attempt != request.Attempt ||
            !string.Equals(acknowledgement.SoulId, request.SoulId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.TraceId, request.TraceId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(request.CommandSha256, expectedCommandSha256) ||
            !FixedDigestEquals(request.AuthorizationSha256, expectedAuthorizationSha256) ||
            !FixedDigestEquals(acknowledgement.CommandSha256, expectedCommandSha256) ||
            !FixedDigestEquals(acknowledgement.AuthorizationSha256, expectedAuthorizationSha256) ||
            acknowledgement.SubmissionAttemptId != request.SubmissionAttemptId ||
            !FixedDigestEquals(acknowledgement.SubmissionIntentSha256, request.SubmissionIntentSha256) ||
            !FixedDigestEquals(acknowledgement.PendingStateSha256, request.PendingStateSha256) ||
            !FixedDigestEquals(acknowledgement.ActiveReleaseBomSha256, request.ActiveReleaseBomSha256) ||
            acknowledgement.ActiveReleaseBomGeneration != request.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(acknowledgement.ActiveReleaseBomTokenSha256, request.ActiveReleaseBomTokenSha256) ||
            !FixedDigestEquals(acknowledgement.SubmittedRequestSha256, expectedSubmittedRequestSha256) ||
            !FixedDigestEquals(acknowledgement.AcknowledgementSha256, expectedAcknowledgementSha256) ||
            acknowledgement.OccurredAt < request.Command.OccurredAt || acknowledgement.OccurredAt > observedAt)
            throw new UnauthorizedAccessException("Native submission acknowledgement is not a durable exact binding to the command, authorization, BOM, scope, and completion handle.");
    }

    private static void ValidateSubmissionAcknowledged(
        NativeExecutionRequestV1 request,
        NativeSubmissionAck nativeAcknowledgement,
        VerifiedSubmissionPendingAuthorization pending,
        VerifiedSubmissionAcknowledgedAuthorization acknowledged,
        string nativeRequestBindingSha256,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(acknowledged);
        ArgumentNullException.ThrowIfNull(acknowledged.Acknowledgement);
        ArgumentNullException.ThrowIfNull(acknowledged.AcknowledgedState);
        acknowledged.Acknowledgement.Validate();
        acknowledged.AcknowledgedState.Validate();
        NativeContractGuard.RequireUtc(observedAt, nameof(observedAt));

        var ownerAcknowledgement = acknowledged.Acknowledgement;
        var state = acknowledged.AcknowledgedState;
        var intentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(pending.Intent);
        var pendingStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(pending.PendingState);
        var ownerAcknowledgementSha256 = ApprovalSubmissionLifecycleBinding.ComputeAcknowledgementSha256(ownerAcknowledgement);
        var acknowledgedStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(state);
        var submittedRequestSha256 = NativeSubmissionProtocolV1.ComputeSubmittedRequestSha256(request);

        if (ownerAcknowledgement.SubmissionAttemptId != request.SubmissionAttemptId ||
            ownerAcknowledgement.ApprovalId != pending.Intent.ApprovalId ||
            ownerAcknowledgement.ProposalId != pending.Intent.ProposalId ||
            ownerAcknowledgement.CommandId != request.CommandId ||
            ownerAcknowledgement.LeaseId != request.LeaseId ||
            ownerAcknowledgement.Attempt != request.Attempt ||
            !string.Equals(ownerAcknowledgement.SoulId, request.SoulId, StringComparison.Ordinal) ||
            !string.Equals(ownerAcknowledgement.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(ownerAcknowledgement.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(ownerAcknowledgement.TraceId, request.TraceId, StringComparison.Ordinal) ||
            !string.Equals(ownerAcknowledgement.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(ownerAcknowledgement.ReleaseBomSha256, request.ActiveReleaseBomSha256) ||
            ownerAcknowledgement.ReleaseBomGeneration != request.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(ownerAcknowledgement.NativeRequestBindingSha256, nativeRequestBindingSha256) ||
            !FixedDigestEquals(ownerAcknowledgement.SubmissionIntentSha256, intentSha256) ||
            !FixedDigestEquals(ownerAcknowledgement.PendingStateSha256, pendingStateSha256) ||
            !FixedDigestEquals(ownerAcknowledgement.SubmittedRequestSha256, submittedRequestSha256) ||
            ownerAcknowledgement.NativeSubmissionId != nativeAcknowledgement.SubmissionId ||
            ownerAcknowledgement.CompletionHandleId != nativeAcknowledgement.CompletionHandleId ||
            !FixedDigestEquals(ownerAcknowledgement.NativeAcknowledgementSha256, nativeAcknowledgement.AcknowledgementSha256) ||
            ownerAcknowledgement.OccurredAt < nativeAcknowledgement.OccurredAt ||
            ownerAcknowledgement.OccurredAt > observedAt ||
            ownerAcknowledgement.ValidUntil <= observedAt ||
            state.SubmissionAttemptId != request.SubmissionAttemptId ||
            state.ApprovalId != ownerAcknowledgement.ApprovalId ||
            state.ProposalId != ownerAcknowledgement.ProposalId ||
            state.CommandId != request.CommandId ||
            state.LeaseId != request.LeaseId ||
            state.Attempt != request.Attempt ||
            !string.Equals(state.SoulId, request.SoulId, StringComparison.Ordinal) ||
            !string.Equals(state.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(state.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(state.TraceId, request.TraceId, StringComparison.Ordinal) ||
            !string.Equals(state.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(state.ReleaseBomSha256, request.ActiveReleaseBomSha256) ||
            state.ReleaseBomGeneration != request.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(state.NativeRequestBindingSha256, nativeRequestBindingSha256) ||
            !FixedDigestEquals(state.SubmissionIntentSha256, intentSha256) ||
            !string.Equals(state.State, ApprovalSubmissionStateV1.SubmissionAcknowledged, StringComparison.Ordinal) ||
            !FixedDigestEquals(state.PredecessorStateSha256!, pendingStateSha256) ||
            !FixedDigestEquals(state.EvidenceSha256, ownerAcknowledgementSha256) ||
            !FixedDigestEquals(state.StateSha256, acknowledgedStateSha256) ||
            state.OccurredAt < ownerAcknowledgement.OccurredAt || state.OccurredAt > observedAt)
            throw new UnauthorizedAccessException("Policy Approval did not append the exact SUBMISSION_ACKNOWLEDGED state for the validated native acknowledgement.");
    }

    private static void ValidateExistingSubmissionUnknown(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        VerifiedSubmissionPendingAuthorization pending,
        VerifiedSubmissionUnknownAuthorization unknown,
        string nativeRequestBindingSha256,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(unknown);
        ArgumentNullException.ThrowIfNull(unknown.UnknownState);
        unknown.UnknownState.Validate();
        NativeContractGuard.RequireUtc(observedAt, nameof(observedAt));

        var state = unknown.UnknownState;
        var intent = pending.Intent;
        var intentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent);
        var pendingStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(pending.PendingState);
        var stateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(state);
        if (state.SubmissionAttemptId != intent.SubmissionAttemptId ||
            state.ApprovalId != command.ApprovalId ||
            state.ProposalId != intent.ProposalId ||
            state.CommandId != command.CommandId ||
            state.LeaseId != command.LeaseId ||
            state.Attempt != command.Attempt ||
            !string.Equals(state.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(state.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(state.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(state.TraceId, command.TraceId, StringComparison.Ordinal) ||
            !string.Equals(state.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(state.ReleaseBomSha256, authorization.ReleaseBomSha256) ||
            state.ReleaseBomGeneration != authorization.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(state.NativeRequestBindingSha256, nativeRequestBindingSha256) ||
            !FixedDigestEquals(state.SubmissionIntentSha256, intentSha256) ||
            !string.Equals(state.State, ApprovalSubmissionStateV1.UnknownSubmission, StringComparison.Ordinal) ||
            !FixedDigestEquals(state.PredecessorStateSha256!, pendingStateSha256) ||
            !FixedDigestEquals(state.StateSha256, stateSha256) ||
            state.OccurredAt < pending.PendingState.OccurredAt || state.OccurredAt > observedAt)
            throw new UnauthorizedAccessException("Policy Approval did not return the exact existing UNKNOWN_SUBMISSION state for external reconciliation.");
    }

    private static void ValidateNativeScope(NativeExecutionRequestV1 request, NativeResultV1 result, DateTimeOffset observedAt)
    {
        if (result.CommandId != request.CommandId || result.LeaseId != request.LeaseId || result.Attempt != request.Attempt ||
            !string.Equals(result.SoulId, request.SoulId, StringComparison.Ordinal) ||
            !string.Equals(result.DeviceBindingId, request.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(result.PlatformAccountId, request.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(result.TraceId, request.TraceId, StringComparison.Ordinal) ||
            !string.Equals(result.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !FixedDigestEquals(result.ActiveReleaseBomSha256, request.ActiveReleaseBomSha256) ||
            result.ActiveReleaseBomGeneration != request.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(result.ActiveReleaseBomTokenSha256, request.ActiveReleaseBomTokenSha256) ||
            result.OccurredAt < request.Command.OccurredAt || result.OccurredAt > observedAt)
            throw new UnauthorizedAccessException("SOUL-ISO-001: native result does not match the exact sent command, lease, attempt, active BOM generation, or execution token.");

        var nativeStep = result.StepResults[0];
        if (nativeStep.StepId != request.StepId || !string.Equals(nativeStep.StepKind, request.StepKind, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Native result does not cover the exact step in the sent execution snapshot.");
    }

    private DateTimeOffset ReadTrustedNow(DateTimeOffset? notBefore = null)
    {
        var now = _trustedClock.GetUtcNow();
        CommandContractGuard.RequireUtc(now, nameof(ITrustedClock));
        if (notBefore is not null && now < notBefore.Value)
            throw new InvalidOperationException("Trusted clock moved backwards during execution.");
        return now;
    }

    private bool TryReadTrustedNow(DateTimeOffset notBefore, out DateTimeOffset now)
    {
        try { now = ReadTrustedNow(notBefore); return true; }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception)) { now = notBefore; return false; }
    }

    private static bool IsRecoverableBoundaryFailure(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private Task<NativeSubmissionCallbackResult> AbortOrRetainPendingAsync(
        IApprovalExecutionFenceLease fenceLease,
        INativeSubmissionAttempt nativeAttempt,
        Task<NativeSubmission>? submissionTask,
        NativeSubmission? observedSubmission,
        NativeStopRequest expectedStop,
        string uncertainResultCode,
        BoundaryCancellationScope cancellationScope)
    {
        cancellationScope.RequestBestEffortCancellation();
        NativeContractGuard.RequireText(uncertainResultCode, 128, nameof(uncertainResultCode));
        _ = observedSubmission;
        if (submissionTask is not null && !submissionTask.IsCompleted)
            _ = ObserveLateSubmissionAsync(submissionTask, _timeout);

        // native.stop.proof/v1 is byte-frozen quarantine input only. It is not requested,
        // verified, or converted into UNKNOWN_SUBMISSION by the runtime. Without the future
        // Policy-owned authority protocol, every uncertain attempt remains durable PENDING,
        // the exact guard/attempt/task is process-rooted, and the only domain result is the
        // non-retryable WAITING_EXTERNAL outcome.
        var retention = fenceLease.RetainGuardUntilProcessExit(nativeAttempt, submissionTask, expectedStop);
        retention.Validate(expectedStop);
        return Task.FromResult(NativeSubmissionCallbackResult.RetainPending(retention));
    }

    private static NativeStopRequest CreateNativeStopRequest(
        NativeExecutionRequestV1 request,
        string nativeRequestBindingSha256,
        INativeSubmissionAttempt nativeAttempt)
    {
        ArgumentNullException.ThrowIfNull(request);
        NativeContractGuard.RequireSha256(nativeRequestBindingSha256, nameof(nativeRequestBindingSha256));
        var expected = new NativeStopRequest(
            request.SubmissionAttemptId,
            request.CommandId,
            request.LeaseId,
            request.Attempt,
            nativeRequestBindingSha256,
            NativeSubmissionProtocolV1.ComputeSubmittedRequestSha256(request),
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.ActiveReleaseBomSha256,
            request.ActiveReleaseBomGeneration,
            request.ActiveReleaseBomTokenSha256,
            nativeAttempt.WorkerInstanceId,
            nativeAttempt.WorkerGeneration);
        expected.Validate();
        return expected;
    }

    private Exception FailStop(string reasonCode, Exception cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(cause);
        _processFailStop.TerminateProcess(reasonCode, cause);
        return new InvalidOperationException(
            $"Executor process fail-stop authority returned after '{reasonCode}'.",
            cause);
    }

    private static async Task<bool> TryDisposeAsync(IAsyncDisposable value, TimeSpan timeout)
    {
        Task disposal;
        try { disposal = value.DisposeAsync().AsTask(); }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception)) { return false; }
        try
        {
            await disposal.WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            _ = ObserveTaskFailureAsync(disposal);
            return false;
        }
    }

    private static async Task ObserveLateSubmissionAsync(Task<NativeSubmission> submissionTask, TimeSpan cleanupTimeout)
    {
        try
        {
            var submission = await submissionTask.ConfigureAwait(false);
            if (submission?.Completion is not null)
                _ = await TryDisposeAsync(submission.Completion, cleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception)) { }
    }

    private static async Task ObserveCompletionDisposalAsync(INativeSubmissionCompletion completion, TimeSpan cleanupTimeout)
    {
        _ = await TryDisposeAsync(completion, cleanupTimeout).ConfigureAwait(false);
    }

    private static async Task ObserveTaskFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception)) { }
    }

    private SignedCommandReceiptV1 Receipt(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        NativeExecutionRequestV1 nativeRequest,
        DateTimeOffset now,
        string outcome,
        Guid? nativeId,
        bool nativeVerified,
        bool postVerified,
        bool retry,
        string code,
        string? nativeEvidence = null,
        string? postconditionEvidence = null)
    {
        var digest = CommandReceiptProtocolV1.ComputeEvidenceDigest(nativeEvidence, postconditionEvidence);
        var receipt = new CommandReceiptV1(
            CommandReceiptV1.CurrentSchemaVersion, CommandReceiptV1.CurrentContractId, CommandReceiptV1.CurrentProducerModule,
            DeterministicGuid($"{command.CommandId:N}:{command.LeaseId:N}:{command.Attempt}:{outcome}:{digest}"), command.CommandId,
            command.LeaseId, command.Attempt, command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.TraceId, command.IdempotencyKey, now, "internal", outcome, nativeId, nativeVerified, postVerified, digest, retry, code);
        receipt.Validate();
        return _receiptSigner.Sign(
            receipt,
            command,
            authorization,
            nativeRequest.ActiveReleaseBomSha256,
            nativeRequest.ActiveReleaseBomGeneration,
            nativeRequest.ActiveReleaseBomTokenSha256,
            nativeEvidence,
            postconditionEvidence);
    }

    private static string NativeEvidenceDigest(NativeResultV1 native) => Sha256($"{native.NativeResultId:N}:{string.Join('|', native.StepResults.Select(step => step.EvidenceDigest))}");
    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static bool FixedDigestEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    private static bool FixedCanonicalEquals(ExecutionAuthorizationV1 left, ExecutionAuthorizationV1 right)
    {
        var leftBytes = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(left);
        var rightBytes = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(right);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally { CryptographicOperations.ZeroMemory(leftBytes); CryptographicOperations.ZeroMemory(rightBytes); }
    }

    private sealed class BoundaryCancellationScope : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private Task? _cancellation;

        public BoundaryCancellationScope(CancellationToken callerToken) => _source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        public CancellationToken Token => _source.Token;

        public void RequestBestEffortCancellation()
        {
            if (_cancellation is not null) return;
            try
            {
                _cancellation = _source.CancelAsync();
                _ = ObserveCancellationFailureAsync(_cancellation);
            }
            catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
            {
                // The UNKNOWN receipt is authoritative even when a hostile boundary callback rejects cancellation.
            }
        }

        public void Dispose()
        {
            var cancellation = _cancellation;
            if (cancellation is null || cancellation.IsCompleted)
            {
                _source.Dispose();
                return;
            }

            _ = cancellation.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _source,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task ObserveCancellationFailureAsync(Task cancellation)
        {
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception exception) when (IsRecoverableBoundaryFailure(exception)) { }
        }
    }
}

public static class ExecutionAuthorizationBinding
{
    public static string ComputeCommandSha256(CommandDispatchV1 command) => ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command);
}

public sealed class EcdsaExecutionAuthorizationVerifier : IExecutionAuthorizationVerifier, IDisposable
{
    private readonly object _sync = new();
    private readonly ECDsa _publicKey;

    public EcdsaExecutionAuthorizationVerifier(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        _publicKey = ECDsa.Create();
        try
        {
            _publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length) throw new ArgumentException("Public key contains trailing bytes.", nameof(subjectPublicKeyInfo));
            var parameters = _publicKey.ExportParameters(false);
            if (_publicKey.KeySize != 256 || !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
                throw new ArgumentException("Execution authorization requires a NIST P-256 public key.", nameof(subjectPublicKeyInfo));
        }
        catch { _publicKey.Dispose(); throw; }
    }

    public ValueTask<VerifiedExecutionAuthorization> VerifyAsync(CommandDispatchV1 command, ExecutionAuthorizationV1 envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(envelope);
        command.Validate();
        envelope.Validate();
        if (envelope.CommandId != command.CommandId || envelope.LeaseId != command.LeaseId || envelope.Attempt != command.Attempt ||
            !string.Equals(envelope.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(envelope.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(envelope.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(envelope.TraceId, command.TraceId, StringComparison.Ordinal) ||
            !string.Equals(envelope.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal) ||
            envelope.OccurredAt < command.OccurredAt || envelope.ValidUntil > command.LeaseExpiresAt)
            throw new UnauthorizedAccessException("Execution authorization is bound to another command, lease, or identity scope.");
        if (!FixedDigestEquals(envelope.CommandSha256, ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command)))
            throw new UnauthorizedAccessException("Execution authorization command digest mismatch.");

        var signature = Convert.FromBase64String(envelope.SignatureBase64);
        var payload = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(envelope);
        bool verified;
        try
        {
            lock (_sync)
                verified = _publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally { CryptographicOperations.ZeroMemory(signature); CryptographicOperations.ZeroMemory(payload); }
        if (!verified) throw new UnauthorizedAccessException("Execution authorization signature verification failed.");
        return ValueTask.FromResult(new VerifiedExecutionAuthorization(envelope));
    }

    public static byte[] CanonicalBytes(ExecutionAuthorizationV1 envelope) => ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(envelope);
    public void Dispose() => _publicKey.Dispose();

    private static bool FixedDigestEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}
