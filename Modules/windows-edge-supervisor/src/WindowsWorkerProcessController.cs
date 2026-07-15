using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Dps.WindowsEdgeSupervisor;

public sealed record WorkerProcessIdentity(
    WorkerSlot Slot,
    int ProcessId,
    DateTimeOffset StartedAt,
    string Version,
    string ArtifactSha256,
    string PathIdentitySha256);

public interface IWorkerProcessController : IDisposable
{
    WorkerProcessIdentity StartCandidate(CandidateLaunchAuthorization authorization);
    WorkerProcessIdentity GetRequired(WorkerSlot slot);
    void Revalidate(WorkerSlot slot);
    void Terminate(WorkerSlot slot);
}

public interface IWorkerRuntimeChannel
{
    Task VerifyHealthAndShadowAsync(
        WorkerProcessIdentity process,
        WorkerArtifact artifact,
        CancellationToken cancellationToken);

    Task<byte[]> StopIntakeAndDrainAsync(
        WorkerProcessIdentity process,
        ReadOnlyMemory<byte> signedDrainDirective,
        CancellationToken cancellationToken);
}

/// <summary>
/// Explicit production default while the exact Worker IPC has not yet been
/// promoted to a stable runtime endpoint. It prevents a prebuilt file or mock
/// result from being mistaken for live Worker health or drain truth.
/// </summary>
public sealed class UnavailableWorkerRuntimeChannel : IWorkerRuntimeChannel
{
    public Task VerifyHealthAndShadowAsync(
        WorkerProcessIdentity process,
        WorkerArtifact artifact,
        CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException(
            "live Worker health/shadow IPC is not composed; F6 remains WAITING_EXTERNAL"));

    public Task<byte[]> StopIntakeAndDrainAsync(
        WorkerProcessIdentity process,
        ReadOnlyMemory<byte> signedDrainDirective,
        CancellationToken cancellationToken) =>
        Task.FromException<byte[]>(new InvalidOperationException(
            "live Worker drain IPC is not composed; cutover fails closed"));
}

[SupportedOSPlatform("windows")]
public sealed class FixedWindowsWorkerProcessController : IWorkerProcessController
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly object _sync = new();
    private static readonly Regex VersionPattern = new(
        "^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly string _approvedWorkerRoot;
    private readonly Dictionary<WorkerSlot, RunningWorker> _workers = new();
    private bool _disposed;

    public FixedWindowsWorkerProcessController(string approvedWorkerRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the production Worker process controller is Windows-only");
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedWorkerRoot);
        _approvedWorkerRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedWorkerRoot));
        _ = SecurePathProof.CaptureDirectory(_approvedWorkerRoot, _approvedWorkerRoot);
    }

    public WorkerProcessIdentity StartCandidate(CandidateLaunchAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var artifact = authorization.Artifact;
        ArgumentNullException.ThrowIfNull(artifact);
        if (!VersionPattern.IsMatch(artifact.Version) ||
            !string.Equals(Path.GetExtension(artifact.BinaryPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Worker version or executable type is not accepted");
        RequireFrozenWorkerLaunchAbi();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_workers.ContainsKey(artifact.Slot))
                throw new InvalidOperationException("the Worker slot already has a process");
            if (!Path.GetFullPath(artifact.VersionDirectory).StartsWith(
                    _approvedWorkerRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Worker version directory is outside the approved root");
            LockedWorkerRuntimeClosure? runtimeClosure = authorization.ConsumeForLaunch();
            try
            {
                var launchPolicy = CreateFixedLaunchPolicy(artifact);
                if (launchPolicy.UseShellExecute || launchPolicy.ArgumentList.Count != 0 ||
                    !string.IsNullOrEmpty(launchPolicy.Arguments))
                    throw new InvalidOperationException(
                        "Worker launch policy attempted shell or argument injection");
                var binary = Path.GetFullPath(launchPolicy.FileName);
                var workingDirectory = Path.GetFullPath(launchPolicy.WorkingDirectory);
                if (binary.Contains('"'))
                    throw new InvalidOperationException(
                        "Worker executable path contains a forbidden quote");
                var commandLine = new StringBuilder("\"" + binary + "\"");
                var environment = CreateMinimalEnvironment(artifact);
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                ProcessInformation processInformation;
                try
                {
                    if (!CreateProcessW(
                            binary,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow,
                            environment,
                            workingDirectory,
                            ref startup,
                            out processInformation))
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "unable to start the fixed Worker executable");
                }
                finally
                {
                    Marshal.FreeHGlobal(environment);
                }

                using var threadHandle = new SafeKernelHandle(processInformation.Thread, ownsHandle: true);
                var processHandle = new SafeKernelHandle(processInformation.Process, ownsHandle: true);
                SafeKernelHandle? job = null;
                try
                {
                    job = CreateKillOnCloseJob();
                    if (!AssignProcessToJobObject(job, processHandle))
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "unable to assign Worker to its fail-stop Job Object");
                    runtimeClosure.Revalidate();
                    var suspendedBinary = QueryProcessImagePath(processHandle);
                    if (!string.Equals(
                            suspendedBinary,
                            binary,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "suspended Worker executable path does not match the locked signed artifact");
                    if (ResumeThread(threadHandle) == uint.MaxValue)
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "unable to resume the Job-confined Worker process");
                    var process = System.Diagnostics.Process.GetProcessById(
                        checked((int)processInformation.ProcessId));
                    process.EnableRaisingEvents = true;
                    var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
                    var observedBinary = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                    if (!string.Equals(observedBinary, binary, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "started Worker executable path does not match the signed artifact");
                    runtimeClosure.Revalidate();
                    var identity = new WorkerProcessIdentity(
                        artifact.Slot,
                        process.Id,
                        startedAt,
                        artifact.Version,
                        artifact.Sha256,
                        runtimeClosure.IdentitySha256);
                    _workers.Add(
                        artifact.Slot,
                        new RunningWorker(process, processHandle, job, runtimeClosure, identity));
                    processHandle = null!;
                    job = null;
                    runtimeClosure = null;
                    return identity;
                }
                catch
                {
                    _ = TerminateProcess(processHandle, 1);
                    job?.Dispose();
                    processHandle.Dispose();
                    throw;
                }
            }
            finally
            {
                runtimeClosure?.Dispose();
            }
        }
    }

    private static void RequireFrozenWorkerLaunchAbi() =>
        throw new InvalidOperationException(
            "edge Worker launch/runtime ABI is not frozen; F6 remains WAITING_EXTERNAL and process launch fails closed");

    public WorkerProcessIdentity GetRequired(WorkerSlot slot)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var running = GetRunning(slot);
            RevalidateRunning(running);
            return running.Identity;
        }
    }

    public void Revalidate(WorkerSlot slot)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            RevalidateRunning(GetRunning(slot));
        }
    }

    public void Terminate(WorkerSlot slot)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_workers.TryGetValue(slot, out var running)) return;
            running.Dispose();
            _workers.Remove(slot);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var running in _workers.Values) running.Dispose();
            _workers.Clear();
            _disposed = true;
        }
    }

    private static IntPtr CreateMinimalEnvironment(WorkerArtifact artifact)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.Combine(systemRoot, "Temp");
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_EnableDiagnostics"] = "0",
            ["DPS_EDGE_WORKER_SLOT"] = artifact.Slot.ToString(),
            ["DPS_EDGE_WORKER_VERSION"] = artifact.Version,
            ["SystemRoot"] = systemRoot,
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["WINDIR"] = systemRoot
        };
        var block = string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static System.Diagnostics.ProcessStartInfo CreateFixedLaunchPolicy(WorkerArtifact artifact) => new()
    {
        FileName = Path.GetFullPath(artifact.BinaryPath),
        WorkingDirectory = Path.GetFullPath(artifact.VersionDirectory),
        UseShellExecute = false,
        Arguments = string.Empty,
        CreateNoWindow = true,
        ErrorDialog = false,
        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
    };

    private static SafeKernelHandle CreateKillOnCloseJob()
    {
        var handle = new SafeKernelHandle(CreateJobObjectW(IntPtr.Zero, null), ownsHandle: true);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "unable to create the Worker Job Object");
        }
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, pointer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "unable to set KILL_ON_JOB_CLOSE");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return handle;
    }

    private static string QueryProcessImagePath(SafeKernelHandle processHandle)
    {
        var capacity = 32768u;
        var buffer = new StringBuilder(checked((int)capacity));
        if (!QueryFullProcessImageNameW(processHandle, 0, buffer, ref capacity))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "unable to resolve the suspended Worker executable path");
        return Path.GetFullPath(buffer.ToString());
    }

    private static void RevalidateRunning(RunningWorker running)
    {
        if (running.Process.HasExited)
            throw new InvalidOperationException("the selected Worker process has exited");
        if (running.Process.Id != running.Identity.ProcessId ||
            new DateTimeOffset(running.Process.StartTime.ToUniversalTime()) != running.Identity.StartedAt)
            throw new InvalidOperationException("the selected Worker process identity changed");
        running.RuntimeClosure.Revalidate();
        if (running.RuntimeClosure.IdentitySha256 != running.Identity.PathIdentitySha256)
            throw new InvalidOperationException("the selected Worker path identity changed");
    }

    private RunningWorker GetRunning(WorkerSlot slot) =>
        _workers.TryGetValue(slot, out var running)
            ? running
            : throw new InvalidOperationException("the selected Worker slot has no running process");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeKernelHandle job,
        uint informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeKernelHandle job,
        SafeKernelHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeKernelHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeKernelHandle process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle() : base(ownsHandle: true) { }
        public SafeKernelHandle(IntPtr preexistingHandle, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(preexistingHandle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class RunningWorker : IDisposable
    {
        private readonly SafeKernelHandle _processHandle;
        private readonly SafeKernelHandle _jobHandle;

        public RunningWorker(
            System.Diagnostics.Process process,
            SafeKernelHandle processHandle,
            SafeKernelHandle jobHandle,
            LockedWorkerRuntimeClosure runtimeClosure,
            WorkerProcessIdentity identity)
        {
            Process = process;
            _processHandle = processHandle;
            _jobHandle = jobHandle;
            RuntimeClosure = runtimeClosure;
            Identity = identity;
        }

        public System.Diagnostics.Process Process { get; }
        public LockedWorkerRuntimeClosure RuntimeClosure { get; }
        public WorkerProcessIdentity Identity { get; }

        public void Dispose()
        {
            _jobHandle.Dispose();
            if (!Process.HasExited)
            {
                if (!Process.WaitForExit(5000))
                {
                    if (!TerminateProcess(_processHandle, 1))
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "unable to terminate Worker before releasing its runtime-closure locks");
                    if (!Process.WaitForExit(5000))
                        throw new InvalidOperationException(
                            "Worker did not exit; runtime-closure locks remain held");
                }
            }
            if (!Process.HasExited)
                throw new InvalidOperationException(
                    "Worker is still alive; runtime-closure locks cannot be released");
            Process.Dispose();
            _processHandle.Dispose();
            RuntimeClosure.Dispose();
        }
    }
}

public sealed class AbWorkerProcessCoordinator
{
    private static readonly TimeSpan WorkerRuntimeOperationTimeout = TimeSpan.FromSeconds(30);
    private readonly AbWorkerSupervisor _supervisor;
    private readonly IWorkerProcessController _processes;
    private readonly IWorkerRuntimeChannel _runtime;
    private readonly AppendOnlyEvidenceLog _evidence;

    public AbWorkerProcessCoordinator(
        AbWorkerSupervisor supervisor,
        IWorkerProcessController processes,
        IWorkerRuntimeChannel runtime,
        AppendOnlyEvidenceLog evidence)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public async Task StageCandidateAsync(
        WorkerArtifact artifact,
        CapabilityEvidenceVerification capability,
        CancellationToken cancellationToken)
    {
        var authorization = _supervisor.PrepareCandidateLaunch(artifact, capability);
        WorkerProcessIdentity? identity = null;
        try
        {
            identity = _processes.StartCandidate(authorization);
            await AwaitWorkerRuntimeAsync(
                token => _runtime.VerifyHealthAndShadowAsync(identity, artifact, token),
                "Worker health/shadow verification",
                cancellationToken).ConfigureAwait(false);
            _processes.Revalidate(artifact.Slot);
            _supervisor.CommitCandidateLaunch(authorization);
        }
        catch
        {
            _supervisor.AbortCandidateLaunch(authorization);
            if (identity is not null) _processes.Terminate(artifact.Slot);
            throw;
        }
        _evidence.Append("worker.candidate.staged", JsonSerializer.SerializeToUtf8Bytes(identity));
    }

    public ProcessRouteLease AcquireRoute(string deviceBindingId)
    {
        var lease = _supervisor.AcquireRoute(deviceBindingId);
        try
        {
            _processes.Revalidate(lease.Snapshot.Slot);
            return new ProcessRouteLease(lease, _processes.GetRequired(lease.Snapshot.Slot));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<bool> CutoverAsync(DrainScope scope, CancellationToken cancellationToken)
    {
        var drainingSlot = _supervisor.ActiveSlot;
        var candidateSlot = drainingSlot == WorkerSlot.A ? WorkerSlot.B : WorkerSlot.A;
        _processes.Revalidate(candidateSlot);
        var process = _processes.GetRequired(drainingSlot);
        _ = _supervisor.BeginDrain(scope);
        var directive = await _supervisor.PrepareDrainDirectiveAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await AwaitWorkerRuntimeAsync(
            token => _runtime.StopIntakeAndDrainAsync(process, directive, token),
            "Worker stop-intake/drain",
            cancellationToken).ConfigureAwait(false);
        _processes.Revalidate(candidateSlot);
        if (!await _supervisor.TryCutoverAsync(receipt, cancellationToken).ConfigureAwait(false)) return false;
        _processes.Revalidate(_supervisor.ActiveSlot);
        _evidence.Append("worker.cutover.committed", JsonSerializer.SerializeToUtf8Bytes(new
        {
            from = drainingSlot.ToString(),
            to = _supervisor.ActiveSlot.ToString(),
            _supervisor.RoutingEpoch
        }));
        return true;
    }

    public async Task<bool> RollbackAsync(DrainScope scope, CancellationToken cancellationToken)
    {
        var drainingSlot = _supervisor.ActiveSlot;
        var rollbackSlot = drainingSlot == WorkerSlot.A ? WorkerSlot.B : WorkerSlot.A;
        _processes.Revalidate(rollbackSlot);
        var process = _processes.GetRequired(drainingSlot);
        _ = _supervisor.BeginDrain(scope);
        var directive = await _supervisor.PrepareDrainDirectiveAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await AwaitWorkerRuntimeAsync(
            token => _runtime.StopIntakeAndDrainAsync(process, directive, token),
            "Worker stop-intake/drain",
            cancellationToken).ConfigureAwait(false);
        _processes.Revalidate(rollbackSlot);
        if (!await _supervisor.TryRollbackAsync(receipt, cancellationToken).ConfigureAwait(false)) return false;
        _processes.Revalidate(_supervisor.ActiveSlot);
        _evidence.Append("worker.rollback.committed", JsonSerializer.SerializeToUtf8Bytes(new
        {
            from = drainingSlot.ToString(),
            to = _supervisor.ActiveSlot.ToString(),
            _supervisor.RoutingEpoch
        }));
        return true;
    }

    private static async Task AwaitWorkerRuntimeAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(WorkerRuntimeOperationTimeout);
        try
        {
            await operation(bounded.Token)
                .WaitAsync(WorkerRuntimeOperationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            bounded.Cancel();
            throw new TimeoutException(
                operationName + " exceeded the protected 30-second deadline and failed closed",
                exception);
        }
    }

    private static async Task<T> AwaitWorkerRuntimeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(WorkerRuntimeOperationTimeout);
        try
        {
            return await operation(bounded.Token)
                .WaitAsync(WorkerRuntimeOperationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            bounded.Cancel();
            throw new TimeoutException(
                operationName + " exceeded the protected 30-second deadline and failed closed",
                exception);
        }
    }
}

public sealed class ProcessRouteLease : IDisposable
{
    private readonly RouteLease _route;

    internal ProcessRouteLease(RouteLease route, WorkerProcessIdentity process)
    {
        _route = route;
        Process = process;
    }

    public RouteSnapshot Snapshot => _route.Snapshot;
    public WorkerProcessIdentity Process { get; }
    public void Dispose() => _route.Dispose();
}
