namespace Dps.WindowsEdgeWorker;

internal enum WorkerNativeStopKind
{
    NativeNotStarted,
    NativeTransportAborted,
    NativeWorkerProcessExited
}

internal sealed record WorkerNativeStopRequest(
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
    long WorkerGeneration);

internal sealed record WorkerRuntimeIdentitySnapshot(
    string ActiveReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomTokenSha256,
    string WorkerInstanceId,
    long WorkerGeneration,
    string NativeStopProofKeyId);

internal interface IWorkerRuntimeIdentityProvider
{
    WorkerRuntimeIdentitySnapshot ReadCurrent();
}

internal interface IWorkerNativeNoLaterWriteController
{
    Task<WorkerNativeStopKind> StopAndVerifyNoLaterWriteAsync(
        WorkerNativeStopRequest request,
        CancellationToken cancellationToken = default);
}

internal interface IWorkerNativeStopProofSigningAuthority
{
    string KeyId { get; }

    ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> canonicalSigningBytes,
        CancellationToken cancellationToken = default);

    bool Verify(
        ReadOnlySpan<byte> canonicalSigningBytes,
        ReadOnlySpan<byte> p1363Signature);
}

internal sealed record WorkerNativeStopProofIssuanceResult(
    byte[] ExactWireUtf8,
    string WireSha256);

/// <summary>
/// Compatibility tombstone for the withdrawn Executor-owned
/// native.stop.proof/v1 producer.  It deliberately retains the old source
/// signature so stale callers fail closed at the Worker boundary, but it can
/// never call native stop, read runtime identity, sign, or persist a proof.
/// </summary>
internal sealed class WorkerNativeStopProofIssuer
{
    internal const string QuarantineReason =
        "native.stop.proof/v1 is quarantine-only and cannot authorize UNKNOWN_SUBMISSION; " +
        "a frozen Policy-owned v2 challenge/proof contract, verified route assignment, " +
        "and Release-BOM authority context are required";

    public WorkerNativeStopProofIssuer(
        IWorkerNativeNoLaterWriteController stopController,
        IWorkerRuntimeIdentityProvider runtimeIdentityProvider,
        IWorkerNativeStopProofSigningAuthority signingAuthority,
        DurableNativeStopProofStore proofStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stopController);
        ArgumentNullException.ThrowIfNull(runtimeIdentityProvider);
        ArgumentNullException.ThrowIfNull(signingAuthority);
        ArgumentNullException.ThrowIfNull(proofStore);
        _ = timeProvider;
    }

    public Task<WorkerNativeStopProofIssuanceResult> IssueAsync(
        WorkerNativeStopRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return Task.FromException<WorkerNativeStopProofIssuanceResult>(
            new NotSupportedException(QuarantineReason));
    }
}
