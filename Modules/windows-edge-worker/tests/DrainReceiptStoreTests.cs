using System.Text;
using Dps.WindowsEdgeWorker;
using Xunit;

namespace Dps.WindowsEdgeWorker.Tests;

public sealed class DrainReceiptStoreTests
{
    private const string DrainId =
        "drain-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fingerprint =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ConflictingFingerprint =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string JournalEntryId =
        "worker-drain-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string JournalEntryChecksum =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private static readonly byte[] Wire = Encoding.UTF8.GetBytes(
        "{\"schema_version\":\"1.0\",\"contract_id\":\"edge.worker.drain.receipt/v1\"}");

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Prepared_and_committed_receipts_survive_restart_with_exact_wire_idempotency()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        string wireSha256;
        using (var store = DurableWorkerDrainReceiptStore.Open(runtime.Path))
        {
            var prepared = store.Prepare(DrainId, Fingerprint, Wire);
            Assert.Equal(WorkerDrainReceiptPersistenceState.Prepared, prepared.State);
            Assert.Equal(Wire, prepared.ExactWireUtf8);
            wireSha256 = prepared.WireSha256;

            var retry = store.Prepare(
                DrainId,
                Fingerprint,
                "a newly randomized PSS receipt that must be ignored"u8);
            Assert.Equal(Wire, retry.ExactWireUtf8);
            Assert.Equal(wireSha256, retry.WireSha256);

            retry.ExactWireUtf8[0] ^= 0xff;
            Assert.Equal(Wire, store.Read(DrainId, Fingerprint)!.ExactWireUtf8);

            var committed = store.Commit(
                DrainId,
                Fingerprint,
                wireSha256,
                JournalEntryId,
                JournalEntryChecksum,
                journalSequence: 7);
            Assert.Equal(WorkerDrainReceiptPersistenceState.Committed, committed.State);
            Assert.Equal(JournalEntryId, committed.JournalEntryId);
        }

        using (var recovered = DurableWorkerDrainReceiptStore.Open(runtime.Path))
        {
            var committed = recovered.Read(DrainId, Fingerprint);
            Assert.NotNull(committed);
            Assert.Equal(WorkerDrainReceiptPersistenceState.Committed, committed.State);
            Assert.Equal(Wire, committed.ExactWireUtf8);
            Assert.Equal(wireSha256, committed.WireSha256);
            Assert.Equal(JournalEntryChecksum, committed.JournalEntryChecksum);
            Assert.Equal(7, committed.JournalSequence);

            Assert.Throws<WorkerDrainReceiptConflictException>(() =>
                recovered.Read(DrainId, ConflictingFingerprint));
        }

        using var quarantined = DurableWorkerDrainReceiptStore.Open(runtime.Path);
        Assert.Throws<WorkerDrainReceiptConflictException>(() =>
            quarantined.Read(DrainId, Fingerprint));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Partial_tail_is_isolated_without_losing_the_last_durable_prepared_wire()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        string wireSha256;
        using (var store = DurableWorkerDrainReceiptStore.Open(runtime.Path))
            wireSha256 = store.Prepare(DrainId, Fingerprint, Wire).WireSha256;

        var statePath = System.IO.Path.Combine(runtime.Path, "drain-receipts.jsonl");
        using (var stream = new FileStream(statePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write("{\"interrupted\":true"u8);
            stream.Flush(flushToDisk: true);
        }

        using var recovered = DurableWorkerDrainReceiptStore.Open(runtime.Path);
        var prepared = recovered.Read(DrainId, Fingerprint);
        Assert.NotNull(prepared);
        Assert.Equal(WorkerDrainReceiptPersistenceState.Prepared, prepared.State);
        Assert.Equal(Wire, prepared.ExactWireUtf8);
        Assert.Equal(wireSha256, prepared.WireSha256);
        Assert.Single(File.ReadLines(statePath));
        Assert.Single(Directory.EnumerateFiles(
            runtime.Path,
            "drain-receipts.jsonl.*.crash-tail",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Writer_lease_blocks_a_second_drain_receipt_store()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var first = DurableWorkerDrainReceiptStore.Open(runtime.Path);

        Assert.Throws<IOException>(() => DurableWorkerDrainReceiptStore.Open(runtime.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Committed_journal_locator_is_immutable_across_retries_and_restart()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        string wireSha256;
        using (var store = DurableWorkerDrainReceiptStore.Open(runtime.Path))
        {
            wireSha256 = store.Prepare(DrainId, Fingerprint, Wire).WireSha256;
            var committed = store.Commit(
                DrainId,
                Fingerprint,
                wireSha256,
                JournalEntryId,
                JournalEntryChecksum,
                journalSequence: 7);

            var retry = store.Commit(
                DrainId,
                Fingerprint,
                wireSha256,
                JournalEntryId,
                JournalEntryChecksum,
                journalSequence: 7);
            Assert.Equal(committed.DrainId, retry.DrainId);
            Assert.Equal(committed.InputFingerprintSha256, retry.InputFingerprintSha256);
            Assert.Equal(committed.ExactWireUtf8, retry.ExactWireUtf8);
            Assert.Equal(committed.WireSha256, retry.WireSha256);
            Assert.Equal(committed.State, retry.State);
            Assert.Equal(committed.JournalEntryId, retry.JournalEntryId);
            Assert.Equal(committed.JournalEntryChecksum, retry.JournalEntryChecksum);
            Assert.Equal(committed.JournalSequence, retry.JournalSequence);

            Assert.Throws<InvalidDataException>(() => store.Commit(
                DrainId,
                Fingerprint,
                wireSha256,
                JournalEntryId,
                new string('e', 64),
                journalSequence: 7));
            Assert.Throws<InvalidDataException>(() => store.Commit(
                DrainId,
                Fingerprint,
                wireSha256,
                JournalEntryId,
                JournalEntryChecksum,
                journalSequence: 8));
        }

        using var recovered = DurableWorkerDrainReceiptStore.Open(runtime.Path);
        Assert.Throws<InvalidDataException>(() => recovered.Commit(
            DrainId,
            Fingerprint,
            wireSha256,
            JournalEntryId,
            JournalEntryChecksum,
            journalSequence: 8));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Invalid_drain_scope_fingerprint_and_wire_fail_before_persistence()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var store = DurableWorkerDrainReceiptStore.Open(runtime.Path);

        Assert.Throws<ArgumentException>(() =>
            store.Prepare("drain-not-hex", Fingerprint, Wire));
        Assert.Throws<ArgumentException>(() =>
            store.Prepare(DrainId, "not-a-sha256", Wire));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Prepare(DrainId, Fingerprint, ReadOnlySpan<byte>.Empty));
        Assert.Empty(File.ReadLines(System.IO.Path.Combine(runtime.Path, "drain-receipts.jsonl")));
    }
}

internal sealed class DrainReceiptTemporaryRuntimeDirectory : IDisposable
{
    public DrainReceiptTemporaryRuntimeDirectory()
    {
        var baseDirectory = OperatingSystem.IsMacOS() && Directory.Exists("/private/tmp")
            ? "/private/tmp"
            : System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(
            baseDirectory,
            "dps-worker-drain-tests-" + Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
