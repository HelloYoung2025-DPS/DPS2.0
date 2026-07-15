using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dps.WindowsEdgeWorker;

public enum NativeStatus
{
    Success,
    Failed,
    UnknownOutcome
}

public sealed record NativeDispatchResult(
    bool DispatchAcknowledged,
    NativeStatus Status,
    bool PostconditionVerified,
    string Detail);

public sealed record CommandReceipt(
    string? CommandId,
    string? IdempotencyKey,
    string ResultStatus,
    bool? DispatchAcknowledged,
    NativeStatus? NativeStatus,
    bool? PostconditionVerified,
    bool Duplicate,
    bool RetryAllowed,
    string Detail);

public sealed class TransportDispatchException(string message, bool? dispatchAcknowledged) : Exception(message)
{
    public bool? DispatchAcknowledged { get; } = dispatchAcknowledged;
}

public interface INativeTransport
{
    Task<NativeDispatchResult> DispatchAsync(WorkerCommand command, CancellationToken cancellationToken);
}

public interface IWorkerJournal
{
    Task<WorkerJournalAppendReceipt> AppendAsync(
        WorkerJournalAppendRequest request,
        CancellationToken cancellationToken);
}

public interface ICommandStateStore
{
    long BeginProcessEpoch();
    BeginResult TryBegin(string idempotencyKey, string requestSha256, long processEpoch);
    void MarkAccepted(string idempotencyKey, long processEpoch);
    int MarkTransportAttempted(string idempotencyKey, long processEpoch);
    void MarkPreDispatchRetry(string idempotencyKey, long processEpoch);
    void MarkDispatchAcknowledged(string idempotencyKey, long processEpoch);
    void PrepareCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalContext journalContext,
        CommandReceipt receipt,
        WorkerJournalWrite terminalWrite);
    void FinalizeCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalAppendReceipt journalReceipt);
    IReadOnlyList<PreparedCommandCompletion> ClaimPreparedCompletions(long processEpoch);
    CommandDrainSnapshot GetDrainSnapshot();
}

public interface IDurableWorkerJournal : IWorkerJournal
{
}

public interface IDurableCommandStateStore : ICommandStateStore
{
}

public enum WorkerRuntimeMode
{
    Simulation,
    Production
}

public sealed record WorkerJournalContext(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string PrivacyClass,
    string CommandId)
{
    public static WorkerJournalContext FromCommand(WorkerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new WorkerJournalContext(
            command.SoulId ?? throw new InvalidDataException("soul_id is required for journaling"),
            command.DeviceBindingId ?? throw new InvalidDataException("device_binding_id is required for journaling"),
            command.PlatformAccountId ?? throw new InvalidDataException("platform_account_id is required for journaling"),
            command.TraceId ?? throw new InvalidDataException("trace_id is required for journaling"),
            command.IdempotencyKey ?? throw new InvalidDataException("idempotency_key is required for journaling"),
            command.OccurredAt ?? throw new InvalidDataException("occurred_at is required for journaling"),
            command.PrivacyClass ?? throw new InvalidDataException("privacy_class is required for journaling"),
            command.CommandId ?? throw new InvalidDataException("command_id is required for journaling"));
    }
}

public sealed record WorkerJournalWrite(
    string EntryId,
    string EntryType,
    string Detail,
    string PayloadJson,
    string PayloadSha256)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static WorkerJournalWrite Create(
        WorkerCommand command,
        string eventKey,
        string entryType,
        string detail)
        => Create(WorkerJournalContext.FromCommand(command), eventKey, entryType, detail);

    public static WorkerJournalWrite Create(
        WorkerJournalContext context,
        string eventKey,
        string entryType,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(eventKey);
        ArgumentNullException.ThrowIfNull(entryType);
        ArgumentNullException.ThrowIfNull(detail);
        if (!IsUpperToken(eventKey, 1, 64))
            throw new InvalidDataException("journal event key is invalid");
        if (!IsUpperToken(entryType, 3, 64))
            throw new InvalidDataException("journal entry type is invalid");
        if (detail.Length > 4096)
            throw new InvalidDataException("journal detail exceeds the worker contract limit");

        var entryId = $"worker:{context.IdempotencyKey}:{eventKey}";
        if (entryId.Length > 160)
            throw new InvalidDataException("journal entry identity exceeds the provider contract limit");
        var payload = CreateCanonicalPayload(entryType, detail);
        return new WorkerJournalWrite(entryId, entryType, detail, payload.Json, payload.Sha256);
    }

    internal static (string Json, string Sha256) CreateCanonicalPayload(string entryType, string detail)
    {
        _ = StrictUtf8.GetByteCount(detail);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("detail", detail);
            writer.WriteString("entry_type", entryType);
            writer.WriteString("schema_version", "1.0");
            writer.WriteEndObject();
        }

        var payload = stream.ToArray();
        try
        {
            return (
                StrictUtf8.GetString(payload),
                Convert.ToHexStringLower(SHA256.HashData(payload)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static bool IsUpperToken(string value, int minimum, int maximum) =>
        value.Length >= minimum && value.Length <= maximum && value[0] is >= 'A' and <= 'Z' &&
        value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}

public sealed record WorkerJournalAppendRequest(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    string CommandId,
    string EntryId,
    string EntryType,
    string TraceId,
    string IdempotencyKey,
    string PrivacyClass,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string PayloadJson,
    string PayloadSha256,
    DateTimeOffset OccurredAt)
{
    public static WorkerJournalAppendRequest Create(
        WorkerJournalContext context,
        WorkerJournalWrite write)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);
        if (!JournalIdentifiers.IsAsciiToken(context.CommandId, 128))
            throw new InvalidDataException("Journal command_id is not a canonical ASCII token");
        if (!JournalIdentifiers.IsAsciiToken(write.EntryId, 160))
            throw new InvalidDataException("Journal entry_id is not a canonical ASCII token");
        if (context.OccurredAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Journal occurred_at must use an explicit zero UTC offset");
        if (write.PayloadJson.Length > 262144)
            throw new InvalidDataException("Journal payload_json exceeds the provider contract limit");
        var expectedPayload = WorkerJournalWrite.CreateCanonicalPayload(write.EntryType, write.Detail);
        if (!string.Equals(write.PayloadJson, expectedPayload.Json, StringComparison.Ordinal) ||
            !string.Equals(write.PayloadSha256, expectedPayload.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Journal payload_json and payload_sha256 must match the canonical worker payload");
        return new WorkerJournalAppendRequest(
            "1.0",
            "edge.journal.append/v1",
            "windows-edge-worker",
            context.CommandId,
            write.EntryId,
            write.EntryType,
            context.TraceId,
            context.IdempotencyKey,
            context.PrivacyClass,
            context.SoulId,
            context.DeviceBindingId,
            context.PlatformAccountId,
            write.PayloadJson,
            write.PayloadSha256,
            context.OccurredAt);
    }
}

internal static class JournalIdentifiers
{
    public static bool IsAsciiToken(string? value, int maximum) =>
        !string.IsNullOrEmpty(value) && value.Length <= maximum &&
        IsAsciiAlphaNumeric(value[0]) &&
        value.All(character =>
            IsAsciiAlphaNumeric(character) || character is '.' or '_' or ':' or '-');

    private static bool IsAsciiAlphaNumeric(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

public sealed record WorkerJournalAppendReceipt(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    string RequestProducerModule,
    string CommandId,
    string EntryId,
    string EntryType,
    string TraceId,
    string IdempotencyKey,
    string PrivacyClass,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string OccurredAt,
    long Sequence,
    string PayloadSha256,
    string PreviousChecksum,
    string EntryChecksum,
    bool Durable,
    bool Duplicate);

internal static class WorkerJournalReceiptValidator
{
    public static void Validate(
        WorkerJournalAppendRequest request,
        WorkerJournalAppendReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(receipt);
        var expectedOccurredAt = request.OccurredAt.ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
        if (receipt.SchemaVersion != "1.0" ||
            receipt.ContractId != "edge.journal.receipt/v1" ||
            receipt.ProducerModule != "edge-local-journal" ||
            receipt.RequestProducerModule != request.ProducerModule ||
            receipt.CommandId != request.CommandId ||
            receipt.EntryId != request.EntryId ||
            receipt.EntryType != request.EntryType ||
            receipt.TraceId != request.TraceId ||
            receipt.IdempotencyKey != request.IdempotencyKey ||
            receipt.PrivacyClass != request.PrivacyClass ||
            receipt.SoulId != request.SoulId ||
            receipt.DeviceBindingId != request.DeviceBindingId ||
            receipt.PlatformAccountId != request.PlatformAccountId ||
            receipt.OccurredAt != expectedOccurredAt ||
            receipt.Sequence < 1 ||
            !IsLowerSha256(receipt.PreviousChecksum) ||
            !IsLowerSha256(receipt.EntryChecksum) ||
            !receipt.Durable ||
            !string.Equals(receipt.PayloadSha256, request.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Journal did not return a matching durable append receipt");
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record PreparedCommandCompletion(
    string IdempotencyKey,
    WorkerJournalContext JournalContext,
    CommandReceipt Receipt,
    WorkerJournalWrite TerminalWrite);

public sealed record CommandDrainSnapshot(
    int UnfinishedCount,
    int UncertainCount,
    int CompletionPendingCount)
{
    public bool IsDrained =>
        UnfinishedCount == 0 && UncertainCount == 0 && CompletionPendingCount == 0;
}

public static class CommandDispatchPolicy
{
    public const int MaximumAttempts = 2;
}

public enum CommandExecutionPhase
{
    Reserved,
    Accepted,
    TransportAttempted,
    DispatchAcknowledged,
    CompletionPrepared,
    Completed
}

public sealed record BeginResult(
    string Status,
    CommandExecutionPhase Phase,
    bool? DispatchAcknowledged,
    int DispatchAttemptCount,
    CommandReceipt? ExistingReceipt,
    WorkerJournalWrite? PreparedTerminalWrite = null,
    WorkerJournalContext? PreparedJournalContext = null);

public sealed class InMemoryCommandStateStore : ICommandStateStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private long _processEpoch;

    public long BeginProcessEpoch()
    {
        lock (_sync) return checked(++_processEpoch);
    }

    public BeginResult TryBegin(string idempotencyKey, string requestSha256, long processEpoch)
    {
        lock (_sync)
        {
            RequireCurrentProcessEpoch(processEpoch);
            if (!_states.TryGetValue(idempotencyKey, out var existing))
            {
                _states.Add(idempotencyKey, new State(requestSha256, processEpoch));
                return new BeginResult("NEW", CommandExecutionPhase.Reserved, false, 0, null);
            }

            if (!string.Equals(existing.RequestSha256, requestSha256, StringComparison.Ordinal))
            {
                return new BeginResult(
                    "CONFLICT", existing.Phase, AcknowledgementFor(existing.Phase), existing.DispatchAttemptCount, null);
            }

            if (existing.Phase == CommandExecutionPhase.Completed)
                return new BeginResult(
                    "DUPLICATE", existing.Phase, existing.Receipt!.DispatchAcknowledged,
                    existing.DispatchAttemptCount, existing.Receipt);

            if (existing.Phase == CommandExecutionPhase.CompletionPrepared)
            {
                existing.ClaimedProcessEpoch = processEpoch;
                return new BeginResult(
                    "RECONCILE_COMPLETION",
                    existing.Phase,
                    existing.Receipt!.DispatchAcknowledged,
                    existing.DispatchAttemptCount,
                    existing.Receipt,
                    existing.PreparedTerminalWrite,
                    existing.PreparedJournalContext);
            }

            if (existing.ClaimedProcessEpoch == processEpoch)
                return new BeginResult(
                    "IN_PROGRESS", existing.Phase, AcknowledgementFor(existing.Phase),
                    existing.DispatchAttemptCount, null);

            existing.ClaimedProcessEpoch = processEpoch;
            return existing.Phase is CommandExecutionPhase.Reserved or CommandExecutionPhase.Accepted
                ? new BeginResult(
                    "RESUME_PRE_DISPATCH", existing.Phase, false, existing.DispatchAttemptCount, null)
                : new BeginResult(
                    "RECOVER_UNCERTAIN", existing.Phase, AcknowledgementFor(existing.Phase),
                    existing.DispatchAttemptCount, null);
        }
    }

    public void MarkAccepted(string idempotencyKey, long processEpoch) =>
        Transition(idempotencyKey, processEpoch, CommandExecutionPhase.Reserved, CommandExecutionPhase.Accepted);

    public int MarkTransportAttempted(string idempotencyKey, long processEpoch)
    {
        lock (_sync)
        {
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase != CommandExecutionPhase.Accepted)
                throw new InvalidOperationException($"command phase {state.Phase} cannot transition to TransportAttempted");
            if (state.DispatchAttemptCount >= CommandDispatchPolicy.MaximumAttempts)
                throw new InvalidOperationException("persistent dispatch-attempt budget is exhausted");
            state.DispatchAttemptCount++;
            state.Phase = CommandExecutionPhase.TransportAttempted;
            return state.DispatchAttemptCount;
        }
    }

    public void MarkPreDispatchRetry(string idempotencyKey, long processEpoch) =>
        Transition(idempotencyKey, processEpoch, CommandExecutionPhase.TransportAttempted, CommandExecutionPhase.Accepted);

    public void MarkDispatchAcknowledged(string idempotencyKey, long processEpoch) =>
        Transition(idempotencyKey, processEpoch, CommandExecutionPhase.TransportAttempted, CommandExecutionPhase.DispatchAcknowledged);

    public void PrepareCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalContext journalContext,
        CommandReceipt receipt,
        WorkerJournalWrite terminalWrite)
    {
        ArgumentNullException.ThrowIfNull(journalContext);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(terminalWrite);
        lock (_sync)
        {
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase == CommandExecutionPhase.Reserved)
                throw new InvalidOperationException("reserved command cannot complete before acceptance");
            if (state.Phase is CommandExecutionPhase.CompletionPrepared or CommandExecutionPhase.Completed)
                throw new InvalidOperationException($"command phase {state.Phase} cannot prepare completion");
            if (!string.Equals(journalContext.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                throw new InvalidDataException("prepared Journal context does not match command idempotency");
            state.PreparedJournalContext = journalContext;
            state.Receipt = receipt;
            state.PreparedTerminalWrite = terminalWrite;
            state.Phase = CommandExecutionPhase.CompletionPrepared;
        }
    }

    public void FinalizeCompletion(
        string idempotencyKey,
        long processEpoch,
        WorkerJournalAppendReceipt journalReceipt)
    {
        ArgumentNullException.ThrowIfNull(journalReceipt);
        lock (_sync)
        {
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase != CommandExecutionPhase.CompletionPrepared ||
                state.PreparedTerminalWrite is not { } prepared)
                throw new InvalidOperationException("command completion was not durably prepared");
            if (!journalReceipt.Durable ||
                !string.Equals(journalReceipt.EntryId, prepared.EntryId, StringComparison.Ordinal) ||
                !string.Equals(journalReceipt.EntryType, prepared.EntryType, StringComparison.Ordinal) ||
                !string.Equals(journalReceipt.PayloadSha256, prepared.PayloadSha256, StringComparison.Ordinal))
                throw new InvalidDataException("terminal journal receipt does not prove the prepared audit entry");
            state.Phase = CommandExecutionPhase.Completed;
        }
    }

    public IReadOnlyList<PreparedCommandCompletion> ClaimPreparedCompletions(long processEpoch)
    {
        lock (_sync)
        {
            RequireCurrentProcessEpoch(processEpoch);
            var prepared = new List<PreparedCommandCompletion>();
            foreach (var pair in _states)
            {
                var state = pair.Value;
                if (state.Phase != CommandExecutionPhase.CompletionPrepared) continue;
                if (state.PreparedJournalContext is not { } context || state.Receipt is not { } receipt ||
                    state.PreparedTerminalWrite is not { } terminalWrite)
                    throw new InvalidDataException("prepared completion is incomplete");
                state.ClaimedProcessEpoch = processEpoch;
                prepared.Add(new PreparedCommandCompletion(pair.Key, context, receipt, terminalWrite));
            }
            return prepared;
        }
    }

    public CommandDrainSnapshot GetDrainSnapshot()
    {
        lock (_sync)
        {
            var unfinished = 0;
            var uncertain = 0;
            var completionPending = 0;
            foreach (var state in _states.Values)
            {
                switch (state.Phase)
                {
                    case CommandExecutionPhase.Reserved:
                    case CommandExecutionPhase.Accepted:
                        unfinished++;
                        break;
                    case CommandExecutionPhase.TransportAttempted:
                    case CommandExecutionPhase.DispatchAcknowledged:
                        uncertain++;
                        break;
                    case CommandExecutionPhase.CompletionPrepared:
                        completionPending++;
                        break;
                }
            }
            return new CommandDrainSnapshot(unfinished, uncertain, completionPending);
        }
    }

    private void Transition(
        string idempotencyKey,
        long processEpoch,
        CommandExecutionPhase expected,
        CommandExecutionPhase next)
    {
        lock (_sync)
        {
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase != expected)
                throw new InvalidOperationException($"command phase {state.Phase} cannot transition to {next}");
            state.Phase = next;
        }
    }

    private State RequireClaim(string idempotencyKey, long processEpoch)
    {
        RequireCurrentProcessEpoch(processEpoch);
        if (!_states.TryGetValue(idempotencyKey, out var state))
            throw new InvalidOperationException("command state was not begun");
        if (state.ClaimedProcessEpoch != processEpoch)
            throw new InvalidOperationException("command state is fenced by another process epoch");
        return state;
    }

    private void RequireCurrentProcessEpoch(long processEpoch)
    {
        if (processEpoch != _processEpoch)
            throw new InvalidOperationException("command state is fenced by a newer process epoch");
    }

    private static bool? AcknowledgementFor(CommandExecutionPhase phase) => phase switch
    {
        CommandExecutionPhase.Reserved or CommandExecutionPhase.Accepted => false,
        CommandExecutionPhase.TransportAttempted => null,
        CommandExecutionPhase.DispatchAcknowledged => true,
        CommandExecutionPhase.CompletionPrepared => null,
        _ => null
    };

    private sealed class State(string requestSha256, long claimedProcessEpoch)
    {
        public string RequestSha256 { get; } = requestSha256;
        public long ClaimedProcessEpoch { get; set; } = claimedProcessEpoch;
        public CommandExecutionPhase Phase { get; set; } = CommandExecutionPhase.Reserved;
        public int DispatchAttemptCount { get; set; }
        public CommandReceipt? Receipt { get; set; }
        public WorkerJournalWrite? PreparedTerminalWrite { get; set; }
        public WorkerJournalContext? PreparedJournalContext { get; set; }
    }
}

public sealed class CommandProcessor
{
    private readonly INativeTransport _transport;
    private readonly IWorkerJournal _journal;
    private readonly ICommandStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly long _processEpoch;
    private readonly object _intakeSync = new();
    private bool _accepting;
    private int _inFlight;

    public WorkerRuntimeMode RuntimeMode { get; }

    public CommandProcessor(
        INativeTransport transport,
        IWorkerJournal journal,
        ICommandStateStore stateStore,
        TimeProvider? timeProvider = null,
        WorkerRuntimeMode runtimeMode = WorkerRuntimeMode.Simulation)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        if (runtimeMode == WorkerRuntimeMode.Production &&
            (_journal is not IDurableWorkerJournal || _stateStore is not IDurableCommandStateStore))
            throw new InvalidOperationException(
                "production mode requires injected durable Journal and command-state adapters");
        _timeProvider = timeProvider ?? TimeProvider.System;
        _processEpoch = _stateStore.BeginProcessEpoch();
        RuntimeMode = runtimeMode;
        _accepting = runtimeMode == WorkerRuntimeMode.Simulation;
    }

    public bool IsDrained
    {
        get
        {
            lock (_intakeSync)
            {
                if (_accepting || Volatile.Read(ref _inFlight) != 0) return false;
            }
            return _stateStore.GetDrainSnapshot().IsDrained;
        }
    }

    public CommandDrainSnapshot GetDrainSnapshot() => _stateStore.GetDrainSnapshot();

    public void StopIntake()
    {
        lock (_intakeSync) _accepting = false;
    }

    public async Task<CommandReceipt> ProcessAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_intakeSync)
        {
            if (!_accepting)
            {
                return Rejected(command, "worker is draining");
            }

            _inFlight++;
        }

        try
        {
            var validation = Validate(command);
            if (validation is not null) return Rejected(command, validation);

            var actualHash = CommandHasher.Compute(command);
            if (!string.Equals(actualHash, command.RequestSha256, StringComparison.Ordinal))
            {
                return Rejected(command, "request_sha256 mismatch");
            }

            var idempotencyKey = command.IdempotencyKey!;
            var begin = _stateStore.TryBegin(idempotencyKey, actualHash, _processEpoch);
            if (begin.Status == "CONFLICT")
            {
                return new CommandReceipt(
                    command.CommandId, command.IdempotencyKey, "QUARANTINED", false, null, null,
                    Duplicate: false, RetryAllowed: false, "idempotency key reused with different scoped request");
            }

            if (begin.Status == "IN_PROGRESS")
            {
                if (begin.Phase is CommandExecutionPhase.TransportAttempted or CommandExecutionPhase.DispatchAcknowledged)
                {
                    return Unknown(
                        command,
                        begin.DispatchAcknowledged,
                        $"active delivery reached {begin.Phase}; reconcile instead of redispatch") with
                    {
                        Duplicate = true
                    };
                }

                return new CommandReceipt(
                    command.CommandId, command.IdempotencyKey, "IN_PROGRESS", false, null, null,
                    Duplicate: true, RetryAllowed: true, $"prior delivery is active in phase {begin.Phase}");
            }

            if (begin.Status == "RECOVER_UNCERTAIN")
            {
                var recoveredUnknown = Unknown(
                    command,
                    begin.DispatchAcknowledged,
                    $"process restarted after phase {begin.Phase}; native outcome requires reconciliation") with
                {
                    Duplicate = true
                };
                return await CompleteDurablyAsync(command, recoveredUnknown).ConfigureAwait(false);
            }

            if (begin.Status == "RECONCILE_COMPLETION")
            {
                var preparedReceipt = begin.ExistingReceipt ??
                    throw new InvalidDataException("prepared completion is missing its receipt");
                var preparedWrite = begin.PreparedTerminalWrite ??
                    throw new InvalidDataException("prepared completion is missing its terminal journal write");
                var preparedContext = begin.PreparedJournalContext ??
                    throw new InvalidDataException("prepared completion is missing its Journal context");
                await FinalizePreparedCompletionAsync(preparedContext, preparedWrite).ConfigureAwait(false);
                return preparedReceipt with { Duplicate = true, RetryAllowed = false };
            }

            if (begin.Status == "DUPLICATE")
            {
                return begin.ExistingReceipt! with { Duplicate = true, RetryAllowed = false };
            }

            if (begin.Phase == CommandExecutionPhase.Reserved)
            {
                await AppendJournalAsync(
                    command,
                    WorkerJournalWrite.Create(command, "ACCEPTED", "ACCEPTED", "lease and request validated"),
                    cancellationToken).ConfigureAwait(false);
                _stateStore.MarkAccepted(idempotencyKey, _processEpoch);
            }

            if (command.Shadow == true)
            {
                var shadowed = new CommandReceipt(
                    command.CommandId, command.IdempotencyKey, "SHADOWED", false, null, null,
                    Duplicate: false, RetryAllowed: false, "shadow mode suppressed native dispatch");
                return await CompleteDurablyAsync(command, shadowed).ConfigureAwait(false);
            }

            if (begin.DispatchAttemptCount >= CommandDispatchPolicy.MaximumAttempts)
            {
                var exhausted = Failed(
                    command,
                    false,
                    retryAllowed: false,
                    "persistent pre-dispatch retry budget was exhausted before restart");
                return await CompleteDurablyAsync(command, exhausted).ConfigureAwait(false);
            }

            NativeDispatchResult native;
            for (;;)
            {
                var attempt = _stateStore.MarkTransportAttempted(idempotencyKey, _processEpoch);
                try
                {
                    native = await _transport.DispatchAsync(command, cancellationToken).ConfigureAwait(false);
                    if (!native.DispatchAcknowledged && attempt < CommandDispatchPolicy.MaximumAttempts)
                    {
                        _stateStore.MarkPreDispatchRetry(idempotencyKey, _processEpoch);
                        await AppendJournalAsync(
                            command,
                            WorkerJournalWrite.Create(
                                command,
                                $"PRE_DISPATCH_RETRY_{attempt}",
                                "PRE_DISPATCH_RETRY",
                                "native transport confirmed no dispatch: " + native.Detail),
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    break;
                }
                catch (TransportDispatchException exception) when (
                    exception.DispatchAcknowledged == false && attempt < CommandDispatchPolicy.MaximumAttempts)
                {
                    _stateStore.MarkPreDispatchRetry(idempotencyKey, _processEpoch);
                    await AppendJournalAsync(
                        command,
                        WorkerJournalWrite.Create(
                            command,
                            $"PRE_DISPATCH_RETRY_{attempt}",
                            "PRE_DISPATCH_RETRY",
                            exception.Message),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TransportDispatchException exception)
                {
                    CommandReceipt transportFailure;
                    if (exception.DispatchAcknowledged == true)
                    {
                        _stateStore.MarkDispatchAcknowledged(idempotencyKey, _processEpoch);
                        transportFailure = Unknown(command, true, "transport failed after dispatch acknowledgement: " + exception.Message);
                    }
                    else if (exception.DispatchAcknowledged == false)
                    {
                        _stateStore.MarkPreDispatchRetry(idempotencyKey, _processEpoch);
                        transportFailure = Failed(command, false, retryAllowed: false, "bounded pre-dispatch retry exhausted: " + exception.Message);
                    }
                    else
                    {
                        transportFailure = Unknown(command, null, "transport attempt outcome is unknown: " + exception.Message);
                    }
                    return await CompleteDurablyAsync(command, transportFailure).ConfigureAwait(false);
                }
            }

            if (native.DispatchAcknowledged)
                _stateStore.MarkDispatchAcknowledged(idempotencyKey, _processEpoch);
            else
                _stateStore.MarkPreDispatchRetry(idempotencyKey, _processEpoch);

            var dispatchState = native.DispatchAcknowledged
                ? "DISPATCH_ACKNOWLEDGED"
                : "DISPATCH_NOT_ACKNOWLEDGED";
            await AppendJournalAsync(
                command,
                WorkerJournalWrite.Create(command, dispatchState, dispatchState, native.Detail),
                CancellationToken.None).ConfigureAwait(false);

            CommandReceipt receipt;
            if (!native.DispatchAcknowledged)
            {
                receipt = Failed(command, false, retryAllowed: false, "bounded pre-dispatch retry exhausted: " + native.Detail);
            }
            else if (native.Status == NativeStatus.UnknownOutcome)
            {
                receipt = Unknown(command, true, native.Detail);
            }
            else if (native.Status == NativeStatus.Success && native.PostconditionVerified)
            {
                receipt = new CommandReceipt(
                    command.CommandId, command.IdempotencyKey, "VERIFIED_SUCCESS", true, NativeStatus.Success, true,
                    Duplicate: false, RetryAllowed: false, native.Detail);
            }
            else
            {
                receipt = new CommandReceipt(
                    command.CommandId, command.IdempotencyKey, "FAILED", true, native.Status, native.PostconditionVerified,
                    Duplicate: false, RetryAllowed: false,
                    native.Status == NativeStatus.Success ? "native success lacked verified postcondition" : native.Detail);
            }

            return await CompleteDurablyAsync(command, receipt).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public async Task<int> ReconcilePreparedCompletionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_intakeSync)
        {
            _inFlight++;
        }

        try
        {
            var prepared = _stateStore.ClaimPreparedCompletions(_processEpoch);
            var completed = 0;
            foreach (var completion in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await FinalizePreparedCompletionAsync(
                    completion.JournalContext,
                    completion.TerminalWrite).ConfigureAwait(false);
                completed++;
            }
            return completed;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<CommandReceipt> CompleteDurablyAsync(
        WorkerCommand command,
        CommandReceipt receipt)
    {
        var idempotencyKey = command.IdempotencyKey ??
            throw new InvalidDataException("idempotency_key is required for completion");
        var journalContext = WorkerJournalContext.FromCommand(command);
        var terminalWrite = WorkerJournalWrite.Create(
            journalContext,
            "TERMINAL",
            receipt.ResultStatus,
            receipt.Detail);
        _stateStore.PrepareCompletion(
            idempotencyKey,
            _processEpoch,
            journalContext,
            receipt,
            terminalWrite);
        await FinalizePreparedCompletionAsync(journalContext, terminalWrite).ConfigureAwait(false);
        return receipt;
    }

    private async Task FinalizePreparedCompletionAsync(
        WorkerJournalContext context,
        WorkerJournalWrite terminalWrite)
    {
        var journalReceipt = await AppendJournalAsync(
            context,
            terminalWrite,
            CancellationToken.None).ConfigureAwait(false);
        _stateStore.FinalizeCompletion(context.IdempotencyKey, _processEpoch, journalReceipt);
    }

    private async Task<WorkerJournalAppendReceipt> AppendJournalAsync(
        WorkerCommand command,
        WorkerJournalWrite write,
        CancellationToken cancellationToken) =>
        await AppendJournalAsync(
            WorkerJournalContext.FromCommand(command),
            write,
            cancellationToken).ConfigureAwait(false);

    private async Task<WorkerJournalAppendReceipt> AppendJournalAsync(
        WorkerJournalContext context,
        WorkerJournalWrite write,
        CancellationToken cancellationToken)
    {
        var request = WorkerJournalAppendRequest.Create(context, write);
        var receipt = await _journal.AppendAsync(request, cancellationToken).ConfigureAwait(false);
        WorkerJournalReceiptValidator.Validate(request, receipt);
        return receipt;
    }

    private string? Validate(WorkerCommand command)
    {
        return WorkerCommandValidator.GetCommandError(command, _timeProvider.GetUtcNow());
    }

    private static CommandReceipt Rejected(WorkerCommand command, string detail) =>
        new(command.CommandId, command.IdempotencyKey, "REJECTED", false, null, null, false, false, detail);

    private static CommandReceipt Unknown(WorkerCommand command, bool? dispatchAcknowledged, string detail) =>
        new(command.CommandId, command.IdempotencyKey, "UNKNOWN_OUTCOME", dispatchAcknowledged, NativeStatus.UnknownOutcome, null, false, false, detail);

    private static CommandReceipt Failed(WorkerCommand command, bool? dispatchAcknowledged, bool retryAllowed, string detail) =>
        new(command.CommandId, command.IdempotencyKey, "FAILED", dispatchAcknowledged, NativeStatus.Failed, false, false, retryAllowed, detail);
}

public static class CommandHasher
{
    private const string Domain = "dps.windows-edge-worker.command-request-sha256/v1";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Compute(WorkerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contractId = Require(command.ContractId, "contract_id");
        var producerModule = Require(command.ProducerModule, "producer_module");
        var soulId = Require(command.SoulId, "soul_id");
        var deviceBindingId = Require(command.DeviceBindingId, "device_binding_id");
        var platformAccountId = Require(command.PlatformAccountId, "platform_account_id");
        var commandId = Require(command.CommandId, "command_id");
        var traceId = Require(command.TraceId, "trace_id");
        var idempotencyKey = Require(command.IdempotencyKey, "idempotency_key");
        var occurredAt = command.OccurredAt ?? throw new InvalidDataException("occurred_at is required");
        var privacyClass = Require(command.PrivacyClass, "privacy_class");
        var leaseId = Require(command.LeaseId, "lease_id");
        var leaseExpiresAt = command.LeaseExpiresAt ?? throw new InvalidDataException("lease_expires_at is required");
        var actionKind = Require(command.ActionKind, "action_kind");
        var stepKind = Require(command.StepKind, "step_kind");
        var shadow = command.Shadow ?? throw new InvalidDataException("shadow is required");
        if (occurredAt.Offset != TimeSpan.Zero || leaseExpiresAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException(
                "occurred_at and lease_expires_at must use an explicit zero UTC offset for request hashing");

        string?[] fields =
        [
            contractId,
            producerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            commandId,
            traceId,
            idempotencyKey,
            occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            privacyClass,
            leaseId,
            leaseExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            actionKind,
            stepKind,
            command.Selector,
            command.Text,
            command.WaitMs?.ToString(CultureInfo.InvariantCulture),
            command.ExpectedPostcondition,
            shadow ? "1" : "0"
        ];

        var canonical = Encode(fields);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(canonical));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static byte[] Encode(IReadOnlyList<string?> fields)
    {
        var domainBytes = StrictUtf8.GetBytes(Domain);
        var encodedFields = new byte[]?[fields.Count];
        try
        {
            var outputLength = checked(sizeof(uint) + domainBytes.Length + sizeof(uint));
            for (var index = 0; index < fields.Count; index++)
            {
                outputLength = checked(outputLength + sizeof(byte));
                if (fields[index] is not { } field) continue;

                encodedFields[index] = StrictUtf8.GetBytes(field);
                outputLength = checked(outputLength + sizeof(uint) + encodedFields[index]!.Length);
            }

            var output = GC.AllocateUninitializedArray<byte>(outputLength);
            var offset = 0;
            WriteLengthPrefixed(output, ref offset, domainBytes);
            BinaryPrimitives.WriteUInt32BigEndian(
                output.AsSpan(offset, sizeof(uint)),
                checked((uint)fields.Count));
            offset += sizeof(uint);

            foreach (var encodedField in encodedFields)
            {
                if (encodedField is null)
                {
                    output[offset++] = 0;
                    continue;
                }

                output[offset++] = 1;
                WriteLengthPrefixed(output, ref offset, encodedField);
            }

            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domainBytes);
            foreach (var encodedField in encodedFields)
            {
                if (encodedField is not null) CryptographicOperations.ZeroMemory(encodedField);
            }
        }
    }

    private static void WriteLengthPrefixed(byte[] destination, ref int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(
            destination.AsSpan(offset, sizeof(uint)),
            checked((uint)value.Length));
        offset += sizeof(uint);
        value.AsSpan().CopyTo(destination.AsSpan(offset, value.Length));
        offset += value.Length;
    }

    private static string Require(string? value, string field) =>
        value ?? throw new InvalidDataException($"{field} is required for request hashing");
}

public static class CanonicalIds
{
    public static bool IsSoul(string? value) => value is { Length: 69 } && value.StartsWith("soul_", StringComparison.Ordinal) &&
        value.AsSpan(5).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsDeviceBinding(string? value) => IsOpaque(value, "db_");

    public static bool IsPlatformAccount(string? value) => IsOpaque(value, "pa_");

    public static bool IsTrace(string? value) => IsPrefixedLowerHex(value, "trace_", 32);

    public static bool IsIdempotency(string? value) => IsPrefixedLowerHex(value, "idem_", 64);

    private static bool IsOpaque(string? value, string prefix) =>
        IsPrefixedLowerHex(value, prefix, 32);

    private static bool IsPrefixedLowerHex(string? value, string prefix, int bodyLength) =>
        value is not null && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length == prefix.Length + bodyLength &&
        value.AsSpan(prefix.Length).ToString().All(
            character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
