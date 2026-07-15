using Dps.EdgeLocalJournal;

namespace Dps.WindowsEdgeWorker;

public sealed class EdgeLocalJournalAdapter : IDurableWorkerJournal, IAsyncDisposable
{
    private readonly IJournalAppendClient _appendClient;
    private readonly IJournalReadiness _readiness;
    private readonly IAsyncDisposable? _ownedResource;
    private bool _disposed;

    private EdgeLocalJournalAdapter(
        IJournalAppendClient appendClient,
        IJournalReadiness readiness,
        IAsyncDisposable? ownedResource)
    {
        _appendClient = appendClient;
        _readiness = readiness;
        _ownedResource = ownedResource;
    }

    public static EdgeLocalJournalAdapter Bind(
        IJournalAppendClient appendClient,
        string runtimeDirectory,
        bool ownsStore = false)
    {
        ArgumentNullException.ThrowIfNull(appendClient);
        if (appendClient is IJournalDrainAttestationProvider or IJournalQuarantineAdministration)
            throw new InvalidOperationException(
                "Worker production composition rejects a Journal client with attestation or quarantine-administration capability");
        if (appendClient is not IJournalReadiness readiness)
            throw new InvalidOperationException(
                "Worker Journal append client must expose the separate read-only readiness capability");
        var ownedResource = ownsStore
            ? appendClient as IAsyncDisposable ?? throw new InvalidOperationException(
                "an owned Worker Journal append client must support async disposal")
            : null;
        _ = SecureRuntimeFileSystem.PrepareDirectory(runtimeDirectory);
        if (readiness.IsQuarantined)
            throw new JournalQuarantinedException(
                "worker Journal is quarantined; startup reconciliation cannot report drain completion");
        return new EdgeLocalJournalAdapter(
            appendClient,
            readiness,
            ownedResource);
    }

    public async Task<WorkerJournalAppendReceipt> AppendAsync(
        WorkerJournalAppendRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (_readiness.IsQuarantined)
            throw new JournalQuarantinedException(
                "worker Journal is quarantined; append cannot advance durable Worker state");
        JournalReceipt receipt;
        try
        {
            receipt = await _appendClient.AppendAsync(
                new JournalAppendRequest(
                    request.SchemaVersion,
                    request.ContractId,
                    request.ProducerModule,
                    request.CommandId,
                    request.EntryId,
                    request.EntryType,
                    request.TraceId,
                    request.IdempotencyKey,
                    request.PrivacyClass,
                    request.SoulId,
                    request.DeviceBindingId,
                    request.PlatformAccountId,
                    request.PayloadJson,
                    request.PayloadSha256,
                    request.OccurredAt),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_readiness.IsQuarantined)
                throw new JournalQuarantinedException(
                    "worker Journal entered quarantine during append; durable Worker state cannot advance");
        }
        return new WorkerJournalAppendReceipt(
            receipt.SchemaVersion,
            receipt.ContractId,
            receipt.ProducerModule,
            receipt.RequestProducerModule,
            receipt.CommandId,
            receipt.EntryId,
            receipt.EntryType,
            receipt.TraceId,
            receipt.IdempotencyKey,
            receipt.PrivacyClass,
            receipt.SoulId,
            receipt.DeviceBindingId,
            receipt.PlatformAccountId,
            receipt.OccurredAt,
            receipt.Sequence,
            receipt.PayloadSha256,
            receipt.PreviousChecksum,
            receipt.EntryChecksum,
            receipt.Durable,
            receipt.Duplicate);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownedResource is not null)
            await _ownedResource.DisposeAsync().ConfigureAwait(false);
    }

}
