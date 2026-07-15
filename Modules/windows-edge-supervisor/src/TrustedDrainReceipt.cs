using System.Security.Cryptography;
using System.Text;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeSupervisor.Contracts;

namespace Dps.WindowsEdgeSupervisor;

internal sealed record DrainReceiptExpectation(
    string DrainId,
    WorkerSlot Slot,
    string WorkerVersion,
    string WorkerArtifactSha256,
    string JournalArtifactSha256,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    long RoutingEpoch,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string OccurredAt);

internal sealed class VerifiedDurableDrainEvidence
{
    internal VerifiedDurableDrainEvidence(
        VerifiedWorkerDrainReceiptV1 workerReceipt,
        JournalDrainAttestation journalAttestation,
        string journalWireSha256)
    {
        WorkerReceipt = workerReceipt;
        JournalAttestation = journalAttestation;
        JournalWireSha256 = journalWireSha256;
    }

    public VerifiedWorkerDrainReceiptV1 WorkerReceipt { get; }
    public JournalDrainAttestation JournalAttestation { get; }
    public string WorkerWireSha256 => WorkerReceipt.WireSha256;
    public string JournalWireSha256 { get; }
}

internal static class DurableDrainEvidenceVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static VerifiedWorkerDrainReceiptV1 DecodeAndVerifyWorker(
        ReadOnlySpan<byte> workerReceiptWire,
        DrainReceiptExpectation expectation,
        PinnedRsaTrustStore workerTrustStore)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(workerTrustStore);
        var parsed = WorkerDrainReceiptContractCodec.Deserialize(workerReceiptWire);
        using var workerKey = workerTrustStore.CloneRequiredPublicKey(parsed.WorkerKeyId);
        return WorkerDrainReceiptContractCodec.DecodeAndVerifyDurableContinuation(
            workerReceiptWire,
            new WorkerDrainReceiptExpectationV1(
                expectation.DrainId,
                expectation.Slot.ToString(),
                expectation.WorkerVersion,
                expectation.WorkerArtifactSha256,
                expectation.JournalArtifactSha256,
                expectation.ReleaseBomSha256,
                expectation.ProtectedPolicySha256,
                expectation.RoutingEpoch,
                expectation.SoulId,
                expectation.DeviceBindingId,
                expectation.PlatformAccountId,
                expectation.TraceId,
                expectation.IdempotencyKey,
                expectation.OccurredAt),
            workerKey);
    }

    internal static VerifiedDurableDrainEvidence DecodeAndVerifyPair(
        VerifiedWorkerDrainReceiptV1 workerReceipt,
        ReadOnlySpan<byte> journalAttestationWire,
        DrainReceiptExpectation expectation,
        string expectedJournalRequestId,
        PinnedRsaTrustStore journalTrustStore,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workerReceipt);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedJournalRequestId);
        ArgumentNullException.ThrowIfNull(journalTrustStore);
        string journalJson;
        try
        {
            journalJson = StrictUtf8.GetString(journalAttestationWire);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Journal drain attestation contains invalid UTF-8", exception);
        }
        var attestation = JournalDrainAttestationCodec.Deserialize(journalJson);
        var canonicalJournalWire = StrictUtf8.GetBytes(JournalDrainAttestationCodec.Serialize(attestation));
        if (!journalAttestationWire.SequenceEqual(canonicalJournalWire))
            throw new InvalidDataException(
                "Journal drain attestation wire is not the exact canonical owner serialization");
        if (attestation.SignatureKeyId == workerReceipt.Envelope.WorkerKeyId)
            throw new InvalidDataException(
                "Worker and Journal drain proofs require distinct signing identities");
        using var journalKey = journalTrustStore.CloneRequiredPublicKey(attestation.SignatureKeyId);
        JournalDrainAttestationCodec.Verify(attestation, journalKey, now);
        ValidatePairBinding(
            workerReceipt,
            attestation,
            expectation,
            expectedJournalRequestId);
        return new VerifiedDurableDrainEvidence(
            workerReceipt,
            attestation,
            WorkerDrainReceiptContractCodec.ComputeSha256(canonicalJournalWire));
    }

    private static void ValidatePairBinding(
        VerifiedWorkerDrainReceiptV1 worker,
        JournalDrainAttestation journal,
        DrainReceiptExpectation expectation,
        string expectedJournalRequestId)
    {
        var receipt = worker.Envelope;
        var expectedEntryId = "worker-drain-" + expectation.DrainId["drain-".Length..];
        var expectedPayloadSha256 = WorkerDrainReceiptContractCodec.ComputeSha256(
            WorkerDrainReceiptContractCodec.CreateJournalPayload(receipt, worker.WireSha256));
        if (journal.RequestProducerModule != "windows-edge-supervisor" ||
            journal.RequestId != expectedJournalRequestId ||
            journal.SoulId != receipt.SoulId ||
            journal.DeviceBindingId != receipt.DeviceBindingId ||
            journal.PlatformAccountId != receipt.PlatformAccountId ||
            journal.TraceId != receipt.TraceId ||
            journal.IdempotencyKey != receipt.IdempotencyKey ||
            journal.OccurredAt != receipt.OccurredAt ||
            journal.PrivacyClass != receipt.PrivacyClass ||
            journal.DrainId != receipt.DrainId ||
            journal.CommandId != receipt.DrainId ||
            journal.EntryId != expectedEntryId ||
            journal.EntryType != "WORKER_DRAINED" ||
            journal.WorkerSlot != receipt.Slot ||
            journal.WorkerVersion != receipt.WorkerVersion ||
            journal.WorkerArtifactSha256 != receipt.WorkerArtifactSha256 ||
            journal.JournalArtifactSha256 != receipt.JournalArtifactSha256 ||
            journal.ReleaseBomSha256 != receipt.ReleaseBomSha256 ||
            journal.ProtectedPolicySha256 != receipt.ProtectedPolicySha256 ||
            journal.RoutingEpoch != receipt.RoutingEpoch ||
            journal.IntakeStopped != receipt.IntakeStopped ||
            journal.WorkerDrained != receipt.WorkerDrained ||
            journal.RemainingInFlight != receipt.RemainingInFlight ||
            journal.WorkerReceiptWireSha256 != worker.WireSha256 ||
            journal.EntryPayloadSha256 != expectedPayloadSha256 ||
            journal.JournalReceipt.PayloadSha256 != expectedPayloadSha256 ||
            journal.JournalReceipt.RequestProducerModule != "windows-edge-worker" ||
            journal.JournalReceipt.CommandId != receipt.DrainId ||
            journal.JournalReceipt.EntryId != expectedEntryId ||
            !journal.JournalReceipt.Durable)
            throw new InvalidDataException(
                "Worker receipt and rich Journal attestation are not the exact same durable drain proof");

        if (journal.DrainId != expectation.DrainId ||
            journal.WorkerSlot != expectation.Slot.ToString() ||
            journal.WorkerVersion != expectation.WorkerVersion ||
            journal.WorkerArtifactSha256 != expectation.WorkerArtifactSha256 ||
            journal.JournalArtifactSha256 != expectation.JournalArtifactSha256 ||
            journal.ReleaseBomSha256 != expectation.ReleaseBomSha256 ||
            journal.ProtectedPolicySha256 != expectation.ProtectedPolicySha256 ||
            journal.RoutingEpoch != expectation.RoutingEpoch ||
            journal.SoulId != expectation.SoulId ||
            journal.DeviceBindingId != expectation.DeviceBindingId ||
            journal.PlatformAccountId != expectation.PlatformAccountId ||
            journal.TraceId != expectation.TraceId ||
            journal.IdempotencyKey != expectation.IdempotencyKey ||
            journal.OccurredAt != expectation.OccurredAt)
            throw new InvalidDataException(
                "rich Journal attestation does not match the active drain expectation");
    }
}
