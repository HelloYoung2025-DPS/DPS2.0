using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeWorker;
using Xunit;

namespace Dps.WindowsEdgeWorker.Tests;

public sealed class DurableHostTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Production_restart_reconciles_durable_append_once_and_keeps_intake_closed()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var token = TestContext.Current.CancellationToken;

        var seeded = await RunHarnessAsync("seed-prepared-abort", runtime.Path, token);
        Assert.Equal(93, seeded.ExitCode);
        using (var seedJson = ParseLastJsonLine(seeded.StandardOutput))
        {
            Assert.Equal(
                "COMPLETION_PREPARED_AND_JOURNALED",
                seedJson.RootElement.GetProperty("state").GetString());
            Assert.True(seedJson.RootElement.GetProperty("durable_receipt").GetBoolean());
        }

        var journalPath = System.IO.Path.Combine(runtime.Path, "worker-journal.jsonl");
        Assert.Equal(2, File.ReadLines(journalPath).Count());
        var committedJournalLength = new FileInfo(journalPath).Length;

        var firstRestart = await RunWorkerAsync(runtime.Path, token);
        Assert.Equal(0, firstRestart.ExitCode);
        using (var status = ParseLastJsonLine(firstRestart.StandardOutput))
        {
            Assert.Equal(
                "DISABLED_PENDING_WINDOWS_ABI",
                status.RootElement.GetProperty("action_intake").GetString());
            Assert.Equal(1, status.RootElement.GetProperty("reconciled_completions").GetInt32());
            Assert.True(status.RootElement.GetProperty("reconciliation_complete").GetBoolean());
        }
        Assert.Equal(committedJournalLength, new FileInfo(journalPath).Length);
        Assert.Equal(2, File.ReadLines(journalPath).Count());

        var secondRestart = await RunWorkerAsync(runtime.Path, token);
        Assert.Equal(0, secondRestart.ExitCode);
        using (var status = ParseLastJsonLine(secondRestart.StandardOutput))
        {
            Assert.Equal(0, status.RootElement.GetProperty("reconciled_completions").GetInt32());
            Assert.True(status.RootElement.GetProperty("reconciliation_complete").GetBoolean());
        }
        Assert.Equal(committedJournalLength, new FileInfo(journalPath).Length);

        var inspection = await RunHarnessAsync("inspect", runtime.Path, token);
        Assert.Equal(0, inspection.ExitCode);
        using (var status = ParseLastJsonLine(inspection.StandardOutput))
        {
            Assert.Equal("DUPLICATE", status.RootElement.GetProperty("same_status").GetString());
            Assert.Equal("Completed", status.RootElement.GetProperty("same_phase").GetString());
            Assert.Equal("CONFLICT", status.RootElement.GetProperty("conflict_status").GetString());
        }

        AssertPrivateRuntimeModes(runtime.Path);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Production_restart_after_transport_attempt_preserves_uncertainty_and_fails_closed()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var token = TestContext.Current.CancellationToken;

        var seeded = await RunHarnessAsync("seed-attempted-abort", runtime.Path, token);
        Assert.Equal(94, seeded.ExitCode);

        var firstRestart = await RunWorkerAsync(runtime.Path, token);
        Assert.Equal(78, firstRestart.ExitCode);
        using (var status = ParseLastJsonLine(firstRestart.StandardOutput))
        {
            Assert.Equal(
                "DISABLED_PENDING_WINDOWS_ABI",
                status.RootElement.GetProperty("action_intake").GetString());
            Assert.Equal(0, status.RootElement.GetProperty("reconciled_completions").GetInt32());
            Assert.Equal(1, status.RootElement.GetProperty("uncertain_count").GetInt32());
            Assert.False(status.RootElement.GetProperty("reconciliation_complete").GetBoolean());
        }

        var journalPath = System.IO.Path.Combine(runtime.Path, "worker-journal.jsonl");
        Assert.True(File.Exists(journalPath));
        Assert.Single(File.ReadLines(journalPath));
        var acceptedJournalLength = new FileInfo(journalPath).Length;

        var secondRestart = await RunWorkerAsync(runtime.Path, token);
        Assert.Equal(78, secondRestart.ExitCode);
        using var repeatedStatus = ParseLastJsonLine(secondRestart.StandardOutput);
        Assert.Equal(1, repeatedStatus.RootElement.GetProperty("uncertain_count").GetInt32());
        Assert.Equal(acceptedJournalLength, new FileInfo(journalPath).Length);
        Assert.Single(File.ReadLines(journalPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Production_restart_after_native_side_effect_before_ack_never_redispatches_or_fakes_completion()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var token = TestContext.Current.CancellationToken;

        var seeded = await RunHarnessAsync("dispatch-side-effect-abort", runtime.Path, token);
        Assert.Equal(95, seeded.ExitCode);
        using (var seedJson = ParseLastJsonLine(seeded.StandardOutput))
        {
            Assert.Equal(
                "NATIVE_SIDE_EFFECT_BEFORE_ACK",
                seedJson.RootElement.GetProperty("state").GetString());
            Assert.Equal(1, seedJson.RootElement.GetProperty("side_effect_count").GetInt32());
        }

        var sideEffectPath = System.IO.Path.Combine(runtime.Path, "native-side-effect.count");
        Assert.Equal("1\n", File.ReadAllText(sideEffectPath));

        for (var restart = 0; restart < 2; restart++)
        {
            var result = await RunWorkerAsync(runtime.Path, token);
            Assert.Equal(78, result.ExitCode);
            using var status = ParseLastJsonLine(result.StandardOutput);
            Assert.Equal(1, status.RootElement.GetProperty("uncertain_count").GetInt32());
            Assert.Equal(0, status.RootElement.GetProperty("reconciled_completions").GetInt32());
            Assert.False(status.RootElement.GetProperty("reconciliation_complete").GetBoolean());
            Assert.Equal("1\n", File.ReadAllText(sideEffectPath));
        }

        var journalPath = System.IO.Path.Combine(runtime.Path, "worker-journal.jsonl");
        Assert.True(File.Exists(journalPath));
        Assert.Single(File.ReadLines(journalPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Production_restart_after_durable_dispatch_acknowledgement_preserves_uncertainty_without_redispatch()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var token = TestContext.Current.CancellationToken;

        var seeded = await RunHarnessAsync("dispatch-acknowledged-abort", runtime.Path, token);
        Assert.Equal(96, seeded.ExitCode);
        using (var seedJson = ParseLastJsonLine(seeded.StandardOutput))
        {
            Assert.Equal(
                "DISPATCH_ACKNOWLEDGED",
                seedJson.RootElement.GetProperty("state").GetString());
            Assert.Equal(1, seedJson.RootElement.GetProperty("side_effect_count").GetInt32());
        }

        var sideEffectPath = System.IO.Path.Combine(runtime.Path, "native-side-effect.count");
        var journalPath = System.IO.Path.Combine(runtime.Path, "worker-journal.jsonl");
        Assert.Equal("1\n", File.ReadAllText(sideEffectPath));
        Assert.Single(File.ReadLines(journalPath));

        var restart = await RunWorkerAsync(runtime.Path, token);
        Assert.Equal(78, restart.ExitCode);
        using var status = ParseLastJsonLine(restart.StandardOutput);
        Assert.Equal(1, status.RootElement.GetProperty("uncertain_count").GetInt32());
        Assert.Equal(0, status.RootElement.GetProperty("reconciled_completions").GetInt32());
        Assert.False(status.RootElement.GetProperty("reconciliation_complete").GetBoolean());
        Assert.Equal("1\n", File.ReadAllText(sideEffectPath));
        Assert.Single(File.ReadLines(journalPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Exclusive_writer_fence_blocks_a_second_process_and_releases_after_forced_stop()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var token = TestContext.Current.CancellationToken;
        using var holder = StartManagedAssembly(
            ProcessHarnessAssemblyPath(),
            ["hold-lock", "--state-dir", runtime.Path]);
        try
        {
            var ready = await holder.StandardOutput.ReadLineAsync(token)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.Equal("READY", ready);

            var blocked = await RunWorkerAsync(runtime.Path, token);
            Assert.Equal(74, blocked.ExitCode);
            Assert.Contains("failed closed", blocked.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync(CancellationToken.None);
        }

        var released = await RunWorkerAsync(runtime.Path, token);
        Assert.Equal(0, released.ExitCode);
        using var status = ParseLastJsonLine(released.StandardOutput);
        Assert.True(status.RootElement.GetProperty("reconciliation_complete").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Durable_store_rejects_relative_insecure_and_symbolic_link_directories()
    {
        Assert.Throws<InvalidDataException>(() => DurableCommandStateStore.Open("relative-worker-state"));

        using var insecure = new TemporaryRuntimeDirectory(create: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                insecure.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Assert.Throws<UnauthorizedAccessException>(() => DurableCommandStateStore.Open(insecure.Path));
            File.SetUnixFileMode(
                insecure.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var target = new TemporaryRuntimeDirectory(create: true);
            var link = insecure.Path + "-link";
            Directory.CreateSymbolicLink(link, target.Path);
            try
            {
                Assert.Throws<IOException>(() => DurableCommandStateStore.Open(link));
            }
            finally
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Durable_store_detects_committed_tamper_and_live_path_replacement()
    {
        using var tamperedRuntime = new TemporaryRuntimeDirectory();
        var statePath = System.IO.Path.Combine(tamperedRuntime.Path, "command-state.jsonl");
        using (var store = DurableCommandStateStore.Open(tamperedRuntime.Path))
        {
            var epoch = store.BeginProcessEpoch();
            _ = store.TryBegin(IdempotencyKey, RequestSha256, epoch);
        }

        var tampered = File.ReadAllBytes(statePath);
        tampered[tampered.Length / 2] ^= 1;
        File.WriteAllBytes(statePath, tampered);
        Assert.Throws<InvalidDataException>(() => DurableCommandStateStore.Open(tamperedRuntime.Path));

        using var replacementRuntime = new TemporaryRuntimeDirectory();
        using var active = DurableCommandStateStore.Open(replacementRuntime.Path);
        var activeEpoch = active.BeginProcessEpoch();
        _ = active.TryBegin(IdempotencyKey, RequestSha256, activeEpoch);
        var activePath = System.IO.Path.Combine(replacementRuntime.Path, "command-state.jsonl");
        var archivedPath = activePath + ".replaced";
        var exactCopy = File.ReadAllBytes(activePath);
        try
        {
            File.Move(activePath, archivedPath);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        File.WriteAllBytes(activePath, exactCopy);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(activePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<IOException>(() => active.MarkAccepted(IdempotencyKey, activeEpoch));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Durable_store_rejects_hard_links_and_live_writer_lock_replacement()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var hardLinkRuntime = new TemporaryRuntimeDirectory(create: true);
        var hardLinkedState = System.IO.Path.Combine(hardLinkRuntime.Path, "command-state.jsonl");
        var alias = System.IO.Path.Combine(hardLinkRuntime.Path, "command-state.alias");
        File.WriteAllBytes(hardLinkedState, []);
        File.SetUnixFileMode(hardLinkedState, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(0, CreateHardLink(hardLinkedState, alias));
        Assert.Throws<IOException>(() => DurableCommandStateStore.Open(hardLinkRuntime.Path));

        using var replacementRuntime = new TemporaryRuntimeDirectory();
        using var store = DurableCommandStateStore.Open(replacementRuntime.Path);
        var epoch = store.BeginProcessEpoch();
        _ = store.TryBegin(IdempotencyKey, RequestSha256, epoch);
        var leasePath = System.IO.Path.Combine(replacementRuntime.Path, "command-state.writer.lock");
        var archivedLeasePath = leasePath + ".replaced";
        File.Move(leasePath, archivedLeasePath);
        File.WriteAllBytes(
            leasePath,
            new byte[checked((int)new FileInfo(archivedLeasePath).Length)]);
        File.SetUnixFileMode(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<IOException>(() => store.MarkAccepted(IdempotencyKey, epoch));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Production_start_rejects_a_fifo_before_opening_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runtime = new TemporaryRuntimeDirectory(create: true);
        var statePath = System.IO.Path.Combine(runtime.Path, "command-state.jsonl");
        Assert.Equal(0, CreateFifo(statePath, 0x0180));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = await RunWorkerAsync(runtime.Path, timeout.Token);

        Assert.Equal(74, result.ExitCode);
        Assert.Contains("regular file", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Standalone_worker_executable_without_external_Journal_provider_fails_closed()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var result = await RunManagedAssemblyAsync(
            typeof(CommandProcessor).Assembly.Location,
            ["--production-reconcile", "--state-dir", runtime.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(74, result.ExitCode);
        Assert.Contains("external append-only Journal IPC client", result.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(runtime.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    [Trait("ExecutionBoundary", "LOCAL_PROCESS")]
    public async Task Supervisor_zero_argument_launch_ABI_cannot_start_or_transition_the_worker()
    {
        using var runtime = new TemporaryRuntimeDirectory(create: true);
        var result = await RunManagedAssemblyAsync(
            typeof(CommandProcessor).Assembly.Location,
            [],
            TestContext.Current.CancellationToken,
            runtime.Path);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains(
            "--production-reconcile --state-dir <absolute-private-directory>",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Empty(Directory.EnumerateFileSystemEntries(runtime.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Recovery_isolates_an_idempotent_crash_tail_and_rejects_oversized_state()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var statePath = System.IO.Path.Combine(runtime.Path, "command-state.jsonl");
        using (var store = DurableCommandStateStore.Open(runtime.Path))
            _ = store.BeginProcessEpoch();
        var committedLength = new FileInfo(statePath).Length;
        var tail = Encoding.UTF8.GetBytes("{\"interrupted\":true");
        using (var stream = new FileStream(statePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.Write(tail);
        var tailHash = Convert.ToHexStringLower(SHA256.HashData(tail));
        var isolationPath = statePath + "." + tailHash[..16] + ".crash-tail";
        File.WriteAllBytes(isolationPath, tail);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(isolationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using (var recovered = DurableCommandStateStore.Open(runtime.Path))
        {
            Assert.Equal(committedLength, new FileInfo(statePath).Length);
            Assert.Equal(tail, File.ReadAllBytes(isolationPath));
            Assert.True(recovered.BeginProcessEpoch() >= 2);
        }

        using var oversizedRuntime = new TemporaryRuntimeDirectory(create: true);
        var oversizedPath = System.IO.Path.Combine(oversizedRuntime.Path, "command-state.jsonl");
        using (var stream = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            stream.SetLength(64L * 1024 * 1024 + 1);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(oversizedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<InvalidDataException>(() => DurableCommandStateStore.Open(oversizedRuntime.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Recovery_rejects_unbounded_crash_tail_artifacts()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var statePath = System.IO.Path.Combine(runtime.Path, "command-state.jsonl");
        using (var store = DurableCommandStateStore.Open(runtime.Path))
            _ = store.BeginProcessEpoch();
        using (var stream = new FileStream(statePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.Write("{\"interrupted\":true"u8);

        for (var index = 0; index < 128; index++)
        {
            var path = System.IO.Path.Combine(
                runtime.Path,
                "command-state.jsonl." + index.ToString("x16") + ".crash-tail");
            File.WriteAllBytes(path, []);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Throws<IOException>(() => DurableCommandStateStore.Open(runtime.Path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Journal_adapter_uses_narrow_IPC_readiness_without_inspecting_foreign_store_files()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var runtime = new TemporaryRuntimeDirectory(create: true);
        var token = TestContext.Current.CancellationToken;
        var journalPath = System.IO.Path.Combine(runtime.Path, "worker-journal.jsonl");
        File.WriteAllBytes(journalPath, []);
        File.SetUnixFileMode(
            journalPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await using var adapter = await OpenRealJournalAdapterAsync(runtime.Path, token);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(journalPath));

        using var excessive = new TemporaryRuntimeDirectory(create: true);
        for (var index = 0; index < 129; index++)
        {
            var path = System.IO.Path.Combine(
                excessive.Path,
                "worker-journal.jsonl.extra-" + index.ToString("D3"));
            File.WriteAllBytes(path, []);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await using var excessiveAdapter = await OpenRealJournalAdapterAsync(excessive.Path, token);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Journal_adapter_refuses_to_report_a_quarantined_owner_store_as_ready()
    {
        using var runtime = new TemporaryRuntimeDirectory(create: true);
        var quarantinePath = System.IO.Path.Combine(
            runtime.Path,
            "worker-journal.jsonl.quarantine.json");
        File.WriteAllBytes(quarantinePath, []);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(quarantinePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Assert.ThrowsAsync<JournalQuarantinedException>(() =>
            OpenRealJournalAdapterAsync(runtime.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public void Durable_store_rejects_terminal_truth_without_its_required_source_phase()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        using var store = DurableCommandStateStore.Open(runtime.Path);
        var epoch = store.BeginProcessEpoch();
        _ = store.TryBegin(IdempotencyKey, RequestSha256, epoch);
        store.MarkAccepted(IdempotencyKey, epoch);
        var context = JournalContext();
        var falseSuccess = new CommandReceipt(
            context.CommandId,
            context.IdempotencyKey,
            "VERIFIED_SUCCESS",
            true,
            NativeStatus.Success,
            true,
            Duplicate: false,
            RetryAllowed: false,
            "forged success without dispatch acknowledgement state");
        var write = WorkerJournalWrite.Create(
            context,
            "TERMINAL",
            falseSuccess.ResultStatus,
            falseSuccess.Detail);

        Assert.Throws<InvalidDataException>(() => store.PrepareCompletion(
            IdempotencyKey,
            epoch,
            context,
            falseSuccess,
            write));
        Assert.Equal(1, store.GetDrainSnapshot().UnfinishedCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Durable_store_requires_full_Journal_scope_and_persists_terminal_proof()
    {
        using var runtime = new TemporaryRuntimeDirectory();
        var token = TestContext.Current.CancellationToken;
        using var store = DurableCommandStateStore.Open(runtime.Path);
        var epoch = store.BeginProcessEpoch();
        _ = store.TryBegin(IdempotencyKey, RequestSha256, epoch);
        store.MarkAccepted(IdempotencyKey, epoch);
        var context = JournalContext();
        var shadowed = new CommandReceipt(
            context.CommandId,
            context.IdempotencyKey,
            "SHADOWED",
            false,
            null,
            null,
            Duplicate: false,
            RetryAllowed: false,
            "durable proof fixture");
        var write = WorkerJournalWrite.Create(
            context,
            "TERMINAL",
            shadowed.ResultStatus,
            shadowed.Detail);
        store.PrepareCompletion(IdempotencyKey, epoch, context, shadowed, write);

        await using var journal = await OpenRealJournalAdapterAsync(runtime.Path, token);
        var ownerReceipt = await journal.AppendAsync(
            WorkerJournalAppendRequest.Create(context, write),
            token);
        Assert.Throws<InvalidDataException>(() => store.FinalizeCompletion(
            IdempotencyKey,
            epoch,
            ownerReceipt with { SoulId = "soul_" + new string('f', 64) }));
        Assert.Equal(1, store.GetDrainSnapshot().CompletionPendingCount);

        store.FinalizeCompletion(IdempotencyKey, epoch, ownerReceipt);
        Assert.True(store.GetDrainSnapshot().IsDrained);
        var lastStateLine = File.ReadLines(
            System.IO.Path.Combine(runtime.Path, "command-state.jsonl")).Last();
        using var stateEnvelope = JsonDocument.Parse(lastStateLine);
        using var statePayload = JsonDocument.Parse(
            stateEnvelope.RootElement.GetProperty("payload_json").GetString() ??
            throw new InvalidDataException("durable state payload_json is null"));
        var terminalProof = statePayload.RootElement.GetProperty("state")
            .GetProperty("terminal_journal_receipt");
        Assert.Equal(
            ownerReceipt.EntryChecksum,
            terminalProof.GetProperty("entry_checksum").GetString());
    }

    private const string IdempotencyKey =
        "idem_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string RequestSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLink(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int CreateFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    private static WorkerJournalContext JournalContext() => new(
        "soul_" + new string('a', 64),
        "db_" + new string('b', 32),
        "pa_" + new string('c', 32),
        "trace_" + new string('d', 32),
        IdempotencyKey,
        DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
        "internal",
        "command-durable-host-test");

    private static async Task<ProcessResult> RunHarnessAsync(
        string mode,
        string runtimeDirectory,
        CancellationToken cancellationToken) =>
        await RunManagedAssemblyAsync(
            ProcessHarnessAssemblyPath(),
            [mode, "--state-dir", runtimeDirectory],
            cancellationToken);

    private static async Task<EdgeLocalJournalAdapter> OpenRealJournalAdapterAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(runtimeDirectory))
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(runtimeDirectory);
            else
                Directory.CreateDirectory(
                    runtimeDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var path = System.IO.Path.Combine(runtimeDirectory, "worker-journal.jsonl");
        var store = await JournalStore.OpenAsync(path, cancellationToken).ConfigureAwait(false);
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

    private static async Task<ProcessResult> RunWorkerAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken) =>
        await RunManagedAssemblyAsync(
            ProcessHarnessAssemblyPath(),
            ["reconcile", "--state-dir", runtimeDirectory],
            cancellationToken);

    private static async Task<ProcessResult> RunManagedAssemblyAsync(
        string assemblyPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        using var process = StartManagedAssembly(assemblyPath, arguments, workingDirectory);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
    }

    private static Process StartManagedAssembly(
        string assemblyPath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var runtimeConfig = System.IO.Path.Combine(
            baseDirectory,
            "Dps.WindowsEdgeWorker.Tests.runtimeconfig.json");
        var depsFile = System.IO.Path.Combine(
            baseDirectory,
            "Dps.WindowsEdgeWorker.Tests.deps.json");
        if (!File.Exists(runtimeConfig) || !File.Exists(depsFile))
            throw new InvalidOperationException("test process runtime metadata is missing");

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DPS_DOTNET") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (workingDirectory is not null)
            start.WorkingDirectory = workingDirectory;
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(runtimeConfig);
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(depsFile);
        start.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("test process did not start");
    }

    private static string ProcessHarnessAssemblyPath()
    {
        var path = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "Dps.WindowsEdgeWorker.ProcessHarness.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException("test process harness assembly is missing", path);
        return path;
    }

    private static JsonDocument ParseLastJsonLine(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? throw new InvalidDataException("process did not emit JSON status");
        return JsonDocument.Parse(line);
    }

    private static void AssertPrivateRuntimeModes(string runtimeDirectory)
    {
        if (OperatingSystem.IsWindows())
            return;
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(runtimeDirectory));
        foreach (var path in Directory.EnumerateFiles(runtimeDirectory).Where(path =>
                     System.IO.Path.GetFileName(path).StartsWith("command-state", StringComparison.Ordinal) ||
                     System.IO.Path.GetFileName(path).StartsWith("drain-receipts", StringComparison.Ordinal)))
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryRuntimeDirectory : IDisposable
    {
        public TemporaryRuntimeDirectory(bool create = false)
        {
            var baseDirectory = OperatingSystem.IsMacOS() && Directory.Exists("/private/tmp")
                ? "/private/tmp"
                : System.IO.Path.GetTempPath();
            Path = System.IO.Path.Combine(
                baseDirectory,
                "dps-worker-tests-" + Guid.NewGuid().ToString("N"));
            if (create)
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(Path);
                else
                    Directory.CreateDirectory(
                        Path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(
                            Path,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
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
