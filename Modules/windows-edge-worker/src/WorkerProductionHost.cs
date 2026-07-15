using Dps.EdgeLocalJournal;

namespace Dps.WindowsEdgeWorker;

public sealed record ProductionReconcileResult(
    string SchemaVersion,
    string HostMode,
    string ActionIntake,
    int ReconciledCompletions,
    int UnfinishedCount,
    int UncertainCount,
    int CompletionPendingCount,
    bool ReconciliationComplete);

public static class WorkerProductionHost
{
    public static async Task<ProductionReconcileResult> ReconcileAsync(
        string runtimeDirectory,
        IJournalAppendClient journalProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journalProvider);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "production Windows runtime ACL and native ABI evidence have not been bound; intake and reconciliation remain disabled");
        await using var stateStore = DurableCommandStateStore.Open(runtimeDirectory);
        await using var journal = EdgeLocalJournalAdapter.Bind(journalProvider, runtimeDirectory);
        var processor = new CommandProcessor(
            new DisabledNativeTransport(),
            journal,
            stateStore,
            TimeProvider.System,
            WorkerRuntimeMode.Production);

        var reconciled = await processor.ReconcilePreparedCompletionsAsync(cancellationToken)
            .ConfigureAwait(false);
        var snapshot = processor.GetDrainSnapshot();
        return new ProductionReconcileResult(
            "1.0",
            "PRODUCTION_RECONCILE_ONLY",
            "DISABLED_PENDING_WINDOWS_ABI",
            reconciled,
            snapshot.UnfinishedCount,
            snapshot.UncertainCount,
            snapshot.CompletionPendingCount,
            snapshot.IsDrained);
    }

    public static Task<ProductionReconcileResult> ReconcileAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken = default) =>
        throw new JournalAttestationUnavailableException(
            "production reconciliation requires an external append-only Journal IPC client; the Worker executable never loads the Journal implementation or attestation capability directly");

    private sealed class DisabledNativeTransport : INativeTransport
    {
        public Task<NativeDispatchResult> DispatchAsync(
            WorkerCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<NativeDispatchResult>(new InvalidOperationException(
                "production native action intake is disabled pending the separately reviewed Windows ABI"));
    }
}
