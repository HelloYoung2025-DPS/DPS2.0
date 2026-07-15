using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeWorker;

if (args.Length != 3 || args[1] != "--state-dir")
{
    Console.Error.WriteLine(
        "Usage: process-harness <reconcile|seed-prepared-abort|seed-attempted-abort|dispatch-side-effect-abort|dispatch-acknowledged-abort|inspect|hold-lock> --state-dir <path>");
    return 64;
}

try
{
    switch (args[0])
    {
        case "reconcile":
            return await ReconcileAsync(args[2]).ConfigureAwait(false);
        case "seed-prepared-abort":
            await SeedPreparedAndAbortAsync(args[2]).ConfigureAwait(false);
            return 99;
        case "seed-attempted-abort":
            await SeedAttemptedAndAbortAsync(args[2]).ConfigureAwait(false);
            return 99;
        case "dispatch-side-effect-abort":
            await DispatchSideEffectAndAbortAsync(args[2]).ConfigureAwait(false);
            return 99;
        case "dispatch-acknowledged-abort":
            await DispatchAcknowledgedAndAbortAsync(args[2]).ConfigureAwait(false);
            return 99;
        case "inspect":
            Inspect(args[2]);
            return 0;
        case "hold-lock":
            await HoldLockAsync(args[2]).ConfigureAwait(false);
            return 0;
        default:
            Console.Error.WriteLine("Unknown process-harness mode.");
            return 64;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
    return 70;
}

static async Task<int> ReconcileAsync(string runtimeDirectory)
{
    NarrowJournalClient? client = null;
    try
    {
        var journalPath = Path.Combine(runtimeDirectory, "worker-journal.jsonl");
        var store = await JournalStore.OpenAsync(journalPath).ConfigureAwait(false);
        client = new NarrowJournalClient(store);
        var result = await WorkerProductionHost.ReconcileAsync(
            runtimeDirectory,
            client,
            CancellationToken.None).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        }));
        return result.ReconciliationComplete ? 0 : 78;
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
        PlatformNotSupportedException or JournalConflictException or JournalQuarantinedException or
        JournalAttestationUnavailableException or JournalAttestationStateChangedException)
    {
        Console.Error.WriteLine(
            "Worker startup reconciliation failed closed; action intake remains disabled: " +
            exception.GetType().Name + ": " + exception.Message);
        return 74;
    }
    finally
    {
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
    }
}

static async Task<EdgeLocalJournalAdapter> OpenJournalAdapterAsync(string runtimeDirectory)
{
    var path = Path.Combine(runtimeDirectory, "worker-journal.jsonl");
    var store = await JournalStore.OpenAsync(path).ConfigureAwait(false);
    try
    {
        return EdgeLocalJournalAdapter.Bind(
            new NarrowJournalClient(store),
            runtimeDirectory,
            ownsStore: true);
    }
    catch
    {
        await store.DisposeAsync().ConfigureAwait(false);
        throw;
    }
}

static async Task SeedPreparedAndAbortAsync(string runtimeDirectory)
{
    var store = DurableCommandStateStore.Open(runtimeDirectory);
    var journal = await OpenJournalAdapterAsync(runtimeDirectory).ConfigureAwait(false);
    var processor = new CommandProcessor(
        new NeverDispatchTransport(),
        journal,
        new CrashWindowStateStore(store, ProcessCrashPoint.BeforeFinalize),
        new FixtureTimeProvider(Fixture.OccurredAt),
        WorkerRuntimeMode.Simulation);
    await processor.ProcessAsync(Command(Fixture.OccurredAt, shadow: true), CancellationToken.None)
        .ConfigureAwait(false);
    throw new InvalidOperationException("crash-before-finalize state store returned unexpectedly");
}

static async Task SeedAttemptedAndAbortAsync(string runtimeDirectory)
{
    var store = DurableCommandStateStore.Open(runtimeDirectory);
    var journal = await OpenJournalAdapterAsync(runtimeDirectory).ConfigureAwait(false);
    var processor = new CommandProcessor(
        new CrashBeforeSideEffectTransport(),
        journal,
        store,
        new FixtureTimeProvider(Fixture.OccurredAt),
        WorkerRuntimeMode.Simulation);
    await processor.ProcessAsync(Command(Fixture.OccurredAt), CancellationToken.None).ConfigureAwait(false);
    throw new InvalidOperationException("crash-before-side-effect transport returned unexpectedly");
}

static async Task DispatchSideEffectAndAbortAsync(string runtimeDirectory)
{
    var store = DurableCommandStateStore.Open(runtimeDirectory);
    var journal = await OpenJournalAdapterAsync(runtimeDirectory).ConfigureAwait(false);
    var processor = new CommandProcessor(
        new CrashAfterSideEffectTransport(runtimeDirectory),
        journal,
        store,
        new FixtureTimeProvider(Fixture.OccurredAt),
        WorkerRuntimeMode.Simulation);

    await processor.ProcessAsync(Command(Fixture.OccurredAt), CancellationToken.None).ConfigureAwait(false);
    throw new InvalidOperationException("crash-after-side-effect transport returned unexpectedly");
}

static async Task DispatchAcknowledgedAndAbortAsync(string runtimeDirectory)
{
    var store = DurableCommandStateStore.Open(runtimeDirectory);
    var journal = await OpenJournalAdapterAsync(runtimeDirectory).ConfigureAwait(false);
    var processor = new CommandProcessor(
        new SuccessfulSideEffectTransport(runtimeDirectory),
        journal,
        new CrashWindowStateStore(store, ProcessCrashPoint.AfterDispatchAcknowledged),
        new FixtureTimeProvider(Fixture.OccurredAt),
        WorkerRuntimeMode.Simulation);

    await processor.ProcessAsync(Command(Fixture.OccurredAt), CancellationToken.None).ConfigureAwait(false);
    throw new InvalidOperationException("crash-after-dispatch-acknowledgement store returned unexpectedly");
}

static void Inspect(string runtimeDirectory)
{
    using var store = DurableCommandStateStore.Open(runtimeDirectory);
    var epoch = store.BeginProcessEpoch();
    var same = store.TryBegin(
        Fixture.IdempotencyKey,
        Command(Fixture.OccurredAt, shadow: true).RequestSha256!,
        epoch);
    var conflict = store.TryBegin(
        Fixture.IdempotencyKey,
        Fixture.ConflictingRequestSha256,
        epoch);
    Console.Out.WriteLine(JsonSerializer.Serialize(new
    {
        schema_version = "1.0",
        same_status = same.Status,
        same_phase = same.Phase.ToString(),
        conflict_status = conflict.Status,
        drain = store.GetDrainSnapshot()
    }));
}

static async Task HoldLockAsync(string runtimeDirectory)
{
    using var store = DurableCommandStateStore.Open(runtimeDirectory);
    _ = store.BeginProcessEpoch();
    Console.Out.WriteLine("READY");
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
}

static WorkerCommand Command(DateTimeOffset occurredAt, bool shadow = false)
{
    var command = new WorkerCommand(
        "1.0",
        "edge.worker.exchange/v1",
        "windows-edge-supervisor",
        "soul_" + new string('a', 64),
        "db_" + new string('b', 32),
        "pa_" + new string('c', 32),
        "trace_" + new string('d', 32),
        Fixture.IdempotencyKey,
        occurredAt,
        "internal",
        "COMMAND",
        "command-process-harness",
        "lease-process-harness",
        occurredAt.AddMinutes(5),
        string.Empty,
        "TAP",
        "TAP_SELECTOR",
        "fixture:button",
        null,
        null,
        "fixture state changed",
        shadow,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
    return command with { RequestSha256 = CommandHasher.Compute(command) };
}

namespace Dps.WindowsEdgeWorker.ProcessHarness
{
    public static class ProcessHarnessMarker
    {
    }
}

internal static class Fixture
{
    public static readonly DateTimeOffset OccurredAt =
        DateTimeOffset.Parse("2026-07-15T00:00:00Z");
    public const string IdempotencyKey =
        "idem_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    public const string ConflictingRequestSha256 =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
}

internal sealed class FixtureTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class NarrowJournalClient(JournalStore store) :
    IJournalAppendClient,
    IJournalReadiness,
    IAsyncDisposable
{
    public int Count => store.Count;

    public bool IsQuarantined => store.IsQuarantined;

    public Task<JournalReceipt> AppendAsync(
        JournalAppendRequest request,
        CancellationToken cancellationToken = default) =>
        store.AppendAsync(request, cancellationToken);

    public ValueTask DisposeAsync() => store.DisposeAsync();
}

internal sealed class CrashAfterSideEffectTransport(string runtimeDirectory) : INativeTransport
{
    public Task<NativeDispatchResult> DispatchAsync(
        WorkerCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(runtimeDirectory, "native-side-effect.count");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(path, options))
        {
            stream.Write("1\n"u8);
            stream.Flush(flushToDisk: true);
        }

        Console.Out.WriteLine(
            "{\"schema_version\":\"1.0\",\"state\":\"NATIVE_SIDE_EFFECT_BEFORE_ACK\",\"side_effect_count\":1}");
        Console.Out.Flush();
        Environment.Exit(95);
        throw new InvalidOperationException("process termination returned unexpectedly");
    }
}

internal sealed class CrashBeforeSideEffectTransport : INativeTransport
{
    public Task<NativeDispatchResult> DispatchAsync(
        WorkerCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Out.WriteLine("{\"schema_version\":\"1.0\",\"state\":\"TRANSPORT_ATTEMPTED\"}");
        Console.Out.Flush();
        Environment.Exit(94);
        throw new InvalidOperationException("process termination returned unexpectedly");
    }
}

internal sealed class NeverDispatchTransport : INativeTransport
{
    public Task<NativeDispatchResult> DispatchAsync(
        WorkerCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException<NativeDispatchResult>(new InvalidOperationException(
            "shadow crash fixture must never invoke native transport"));
}

internal sealed class SuccessfulSideEffectTransport(string runtimeDirectory) : INativeTransport
{
    public Task<NativeDispatchResult> DispatchAsync(
        WorkerCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(runtimeDirectory, "native-side-effect.count");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        {
            stream.Write("1\n"u8);
            stream.Flush(flushToDisk: true);
        }
        return Task.FromResult(new NativeDispatchResult(
            true,
            NativeStatus.Success,
            true,
            "local-process native side effect verified"));
    }
}

internal enum ProcessCrashPoint
{
    BeforeFinalize,
    AfterDispatchAcknowledged
}

internal sealed class CrashWindowStateStore(
    IDurableCommandStateStore inner,
    ProcessCrashPoint crashPoint) : IDurableCommandStateStore
{
    public long BeginProcessEpoch() => inner.BeginProcessEpoch();

    public BeginResult TryBegin(string idempotencyKey, string requestSha256, long processEpoch) =>
        inner.TryBegin(idempotencyKey, requestSha256, processEpoch);

    public void MarkAccepted(string idempotencyKey, long processEpoch) =>
        inner.MarkAccepted(idempotencyKey, processEpoch);

    public int MarkTransportAttempted(string idempotencyKey, long processEpoch) =>
        inner.MarkTransportAttempted(idempotencyKey, processEpoch);

    public void MarkPreDispatchRetry(string idempotencyKey, long processEpoch) =>
        inner.MarkPreDispatchRetry(idempotencyKey, processEpoch);

    public void MarkDispatchAcknowledged(string idempotencyKey, long processEpoch)
    {
        inner.MarkDispatchAcknowledged(idempotencyKey, processEpoch);
        if (crashPoint != ProcessCrashPoint.AfterDispatchAcknowledged)
            return;
        Console.Out.WriteLine(
            "{\"schema_version\":\"1.0\",\"state\":\"DISPATCH_ACKNOWLEDGED\",\"side_effect_count\":1}");
        Console.Out.Flush();
        Environment.Exit(96);
    }

    public void PrepareCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalContext journalContext,
        CommandReceipt receipt,
        WorkerJournalWrite terminalWrite) =>
        inner.PrepareCompletion(
            idempotencyKey,
            processEpoch,
            journalContext,
            receipt,
            terminalWrite);

    public void FinalizeCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalAppendReceipt journalReceipt)
    {
        if (crashPoint != ProcessCrashPoint.BeforeFinalize)
        {
            inner.FinalizeCompletion(idempotencyKey, processEpoch, journalReceipt);
            return;
        }
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            schema_version = "1.0",
            state = "COMPLETION_PREPARED_AND_JOURNALED",
            durable_receipt = journalReceipt.Durable,
            entry_id = journalReceipt.EntryId
        }));
        Console.Out.Flush();
        Environment.Exit(93);
    }

    public IReadOnlyList<PreparedCommandCompletion> ClaimPreparedCompletions(long processEpoch) =>
        inner.ClaimPreparedCompletions(processEpoch);

    public CommandDrainSnapshot GetDrainSnapshot() => inner.GetDrainSnapshot();
}
