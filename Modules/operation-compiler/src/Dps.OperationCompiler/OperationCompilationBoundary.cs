using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dps.OperationCompiler.Contracts;

namespace Dps.OperationCompiler;

/// <summary>
/// Asynchronous command handoff owned by the operation compiler. Implementations
/// must finish validation and take ownership of any required copy before the task
/// completes. The producer buffer remains valid until that task reaches a terminal
/// state and is then zeroed.
/// </summary>
public interface ICompiledOperationCommandConsumer
{
    Task AcceptAsync(
        ReadOnlyMemory<byte> canonicalOperationWire,
        CancellationToken cancellationToken);
}

/// <summary>
/// Durable isolation sink for a boundary call whose port task did not reach a
/// terminal result before the production deadline or caller cancellation.
/// </summary>
public interface IOperationBoundaryQuarantine
{
    Task QuarantineAsync(
        LateOperationBoundaryOutcome outcome,
        CancellationToken cancellationToken);
}

public sealed record LateOperationBoundaryOutcome(
    string Phase,
    string Trigger,
    string TerminalState,
    string? TerminalErrorType,
    Guid ApprovalId,
    Guid ProposalId,
    Guid? OperationId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string ApprovalSha256,
    string? OperationWireSha256);

/// <summary>
/// Preserves the authoritative approval lookup and compiler decision path before
/// crossing the command boundary as strict operation.compiled/v1 wire bytes.
/// The complete read/compile/handoff path has one fixed two-second production
/// deadline. A result that terminates after the deadline is never returned as an
/// accepted operation and is sent to the required quarantine sink.
/// </summary>
public sealed class OperationCompilationBoundary
{
    public static readonly TimeSpan ProductionDeadline = TimeSpan.FromMilliseconds(2000);

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly AllowlistedOperationCompiler _compiler;
    private readonly ICompiledOperationCommandConsumer _consumer;
    private readonly IOperationBoundaryQuarantine _quarantine;
    private readonly TimeSpan _deadline;
    private readonly ConcurrentDictionary<long, Task> _lateObservers = new();
    private readonly ConcurrentQueue<Exception> _quarantineFailures = new();
    private long _lateObserverSequence;

    public OperationCompilationBoundary(
        AllowlistedOperationCompiler compiler,
        ICompiledOperationCommandConsumer consumer,
        IOperationBoundaryQuarantine quarantine)
        : this(compiler, consumer, quarantine, ProductionDeadline)
    {
    }

    internal OperationCompilationBoundary(
        AllowlistedOperationCompiler compiler,
        ICompiledOperationCommandConsumer consumer,
        IOperationBoundaryQuarantine quarantine,
        TimeSpan deadline)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        if (deadline <= TimeSpan.Zero || deadline > ProductionDeadline)
            throw new ArgumentOutOfRangeException(nameof(deadline), "The boundary deadline must be positive and cannot exceed the manifest 2000ms limit.");
        _deadline = deadline;
    }

    public async Task<CompiledOperationV1> CompileAndAcceptAsync(
        ApprovalCompilationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var boundaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new BoundaryExecutionState();
        var execution = ExecuteCoreAsync(request, state, boundaryCancellation.Token);
        try
        {
            return await execution.WaitAsync(_deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            boundaryCancellation.Cancel();
            TrackLateOutcome(execution, request, state, "DEADLINE_EXCEEDED");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            boundaryCancellation.Cancel();
            TrackLateOutcome(execution, request, state, "CALLER_CANCELLED");
            throw;
        }
    }

    /// <summary>
    /// Allows a host to drain all tracked late terminal outcomes before shutdown.
    /// Quarantine write failures are surfaced here and can never be reported as a
    /// successful handoff.
    /// </summary>
    public async Task DrainLateQuarantinesAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observers = _lateObservers.Values.ToArray();
            if (observers.Length == 0) break;
            await Task.WhenAll(observers).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var failures = new List<Exception>();
        while (_quarantineFailures.TryDequeue(out var failure)) failures.Add(failure);
        if (failures.Count > 0)
            throw new AggregateException("One or more late operation outcomes could not be quarantined.", failures);
    }

    private async Task<CompiledOperationV1> ExecuteCoreAsync(
        ApprovalCompilationRequestV1 request,
        BoundaryExecutionState state,
        CancellationToken cancellationToken)
    {
        var operation = await _compiler.CompileAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var json = OperationCompiledV1Json.Serialize(operation);
        var wire = StrictUtf8.GetBytes(json);
        try
        {
            // Re-enter through the public strict codec at the producer boundary.
            // This prevents a serializer-only path from becoming dispatch truth.
            var verified = OperationCompiledV1Json.Deserialize(StrictUtf8.GetString(wire));
            var wireSha256 = Convert.ToHexString(SHA256.HashData(wire)).ToLowerInvariant();
            state.MarkCommandAccept(verified.OperationId, wireSha256);
            var acceptTask = _consumer.AcceptAsync(wire, cancellationToken)
                ?? throw new InvalidOperationException("The command consumer returned no task.");
            await acceptTask.ConfigureAwait(false);
            // Do not turn a result that completed after WaitAsync timed out into a
            // caller-visible success. The detached observer quarantines it instead.
            return verified;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wire);
        }
    }

    private void TrackLateOutcome(
        Task<CompiledOperationV1> execution,
        ApprovalCompilationRequestV1 request,
        BoundaryExecutionState state,
        string trigger)
    {
        var sequence = Interlocked.Increment(ref _lateObserverSequence);
        var observer = ObserveAndQuarantineLateOutcomeAsync(execution, request, state, trigger);
        if (!_lateObservers.TryAdd(sequence, observer))
            throw new InvalidOperationException("Could not register the late-operation observer.");
        _ = observer.ContinueWith(
            static (completed, boxed) =>
            {
                _ = completed.Exception;
                var registration = ((OperationCompilationBoundary Boundary, long Sequence))boxed!;
                registration.Boundary._lateObservers.TryRemove(registration.Sequence, out _);
            },
            (this, sequence),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveAndQuarantineLateOutcomeAsync(
        Task<CompiledOperationV1> execution,
        ApprovalCompilationRequestV1 request,
        BoundaryExecutionState state,
        string trigger)
    {
        var terminalState = "COMPLETED";
        string? terminalErrorType = null;
        try
        {
            _ = await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            terminalState = "CANCELLED";
            terminalErrorType = exception.GetType().FullName;
        }
        catch (Exception exception)
        {
            terminalState = "FAULTED";
            terminalErrorType = exception.GetType().FullName;
        }

        var snapshot = state.Snapshot();
        var outcome = new LateOperationBoundaryOutcome(
            snapshot.Phase,
            trigger,
            terminalState,
            terminalErrorType,
            request.ApprovalId,
            request.ProposalId,
            snapshot.OperationId,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.ApprovalSha256,
            snapshot.OperationWireSha256);
        try
        {
            // The request token and boundary deadline are already terminal. A
            // durable quarantine adapter owns its own bounded write policy.
            await _quarantine.QuarantineAsync(outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _quarantineFailures.Enqueue(exception);
        }
    }

    private sealed class BoundaryExecutionState
    {
        private readonly object _gate = new();
        private string _phase = "authoritative-approval-read";
        private Guid? _operationId;
        private string? _operationWireSha256;

        internal void MarkCommandAccept(Guid operationId, string operationWireSha256)
        {
            lock (_gate)
            {
                _phase = "command-accept";
                _operationId = operationId;
                _operationWireSha256 = operationWireSha256;
            }
        }

        internal (string Phase, Guid? OperationId, string? OperationWireSha256) Snapshot()
        {
            lock (_gate) return (_phase, _operationId, _operationWireSha256);
        }
    }
}
