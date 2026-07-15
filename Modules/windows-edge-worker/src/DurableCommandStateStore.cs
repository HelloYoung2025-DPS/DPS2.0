using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.WindowsEdgeWorker;

public sealed class DurableCommandStateStore : IDurableCommandStateStore, IDisposable, IAsyncDisposable
{
    private const long MaximumStateFileBytes = 64L * 1024 * 1024;
    private const int MaximumStateRecordBytes = 1024 * 1024;
    private const int MaximumStatePayloadBytes = 512 * 1024;
    private const int MaximumCommands = 100_000;
    private const int MaximumCrashTailArtifacts = 128;
    private const string GenesisChecksum =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private const string ChecksumEncoding = "dps.length-prefixed-utf8/v1";
    private const string EntryChecksumDomain = "dps.windows-edge-worker.state-entry-sha256/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly FileStream _writerLease;
    private readonly FileStream _stateStream;
    private readonly string _runtimeDirectory;
    private readonly string _leasePath;
    private readonly string _statePath;
    private readonly RuntimeFileIdentity? _writerLeaseIdentity;
    private readonly RuntimeFileIdentity? _stateIdentity;
    private long _processEpoch;
    private long _lastSequence;
    private long _committedLength;
    private string _lastChecksum = GenesisChecksum;
    private bool _faulted;
    private bool _disposed;

    private DurableCommandStateStore(
        string runtimeDirectory,
        string leasePath,
        string statePath,
        FileStream writerLease,
        FileStream stateStream,
        RuntimeFileIdentity? writerLeaseIdentity,
        RuntimeFileIdentity? stateIdentity)
    {
        _runtimeDirectory = runtimeDirectory;
        _leasePath = leasePath;
        _statePath = statePath;
        _writerLease = writerLease;
        _stateStream = stateStream;
        _writerLeaseIdentity = writerLeaseIdentity;
        _stateIdentity = stateIdentity;
    }

    public static DurableCommandStateStore Open(string runtimeDirectory)
    {
        var directory = SecureRuntimeFileSystem.PrepareDirectory(runtimeDirectory);
        var leasePath = Path.Combine(directory, "command-state.writer.lock");
        var statePath = Path.Combine(directory, "command-state.jsonl");
        SecureRuntimeFileSystem.VerifyExistingFile(leasePath);
        SecureRuntimeFileSystem.VerifyExistingFile(statePath);

        FileStream? writerLease = null;
        FileStream? stateStream = null;
        try
        {
            writerLease = SecureRuntimeFileSystem.OpenOrCreatePrivateFile(
                leasePath,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.WriteThrough);
            WriteLeaseIdentity(writerLease);

            stateStream = SecureRuntimeFileSystem.OpenOrCreatePrivateFile(
                statePath,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.WriteThrough);

            var store = new DurableCommandStateStore(
                directory,
                leasePath,
                statePath,
                writerLease,
                stateStream,
                SecureRuntimeFileSystem.CaptureOpenFileIdentity(writerLease, leasePath),
                SecureRuntimeFileSystem.CaptureOpenFileIdentity(stateStream, statePath));
            store.Recover();
            return store;
        }
        catch
        {
            stateStream?.Dispose();
            writerLease?.Dispose();
            throw;
        }
    }

    public long BeginProcessEpoch()
    {
        lock (_sync)
        {
            EnsureUsable();
            var next = checked(_processEpoch + 1);
            PersistEvent(new StateEventPayload("1.0", "PROCESS_EPOCH", next, null, null));
            _processEpoch = next;
            return next;
        }
    }

    public BeginResult TryBegin(string idempotencyKey, string requestSha256, long processEpoch)
    {
        lock (_sync)
        {
            EnsureUsable();
            RequireCurrentProcessEpoch(processEpoch);
            ValidateIdempotencyAndHash(idempotencyKey, requestSha256);
            if (!_states.TryGetValue(idempotencyKey, out var existing))
            {
                if (_states.Count >= MaximumCommands)
                    throw new IOException("durable command-state capacity is exhausted");
                var created = new State(
                    requestSha256,
                    processEpoch,
                    CommandExecutionPhase.Reserved,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null);
                PersistState(idempotencyKey, created, processEpoch);
                _states.Add(idempotencyKey, created);
                return new BeginResult("NEW", created.Phase, false, 0, null);
            }

            if (!string.Equals(existing.RequestSha256, requestSha256, StringComparison.Ordinal))
            {
                return new BeginResult(
                    "CONFLICT",
                    existing.Phase,
                    AcknowledgementFor(existing.Phase),
                    existing.DispatchAttemptCount,
                    null);
            }

            if (existing.Phase == CommandExecutionPhase.Completed)
            {
                return new BeginResult(
                    "DUPLICATE",
                    existing.Phase,
                    existing.Receipt!.DispatchAcknowledged,
                    existing.DispatchAttemptCount,
                    existing.Receipt);
            }

            if (existing.Phase == CommandExecutionPhase.CompletionPrepared)
            {
                var claimed = existing with { ClaimedProcessEpoch = processEpoch };
                PersistState(idempotencyKey, claimed, processEpoch);
                _states[idempotencyKey] = claimed;
                return new BeginResult(
                    "RECONCILE_COMPLETION",
                    claimed.Phase,
                    claimed.Receipt!.DispatchAcknowledged,
                    claimed.DispatchAttemptCount,
                    claimed.Receipt,
                    claimed.PreparedTerminalWrite,
                    claimed.PreparedJournalContext);
            }

            if (existing.ClaimedProcessEpoch == processEpoch)
            {
                return new BeginResult(
                    "IN_PROGRESS",
                    existing.Phase,
                    AcknowledgementFor(existing.Phase),
                    existing.DispatchAttemptCount,
                    null);
            }

            var reclaimed = existing with { ClaimedProcessEpoch = processEpoch };
            PersistState(idempotencyKey, reclaimed, processEpoch);
            _states[idempotencyKey] = reclaimed;
            return reclaimed.Phase is CommandExecutionPhase.Reserved or CommandExecutionPhase.Accepted
                ? new BeginResult(
                    "RESUME_PRE_DISPATCH",
                    reclaimed.Phase,
                    false,
                    reclaimed.DispatchAttemptCount,
                    null)
                : new BeginResult(
                    "RECOVER_UNCERTAIN",
                    reclaimed.Phase,
                    AcknowledgementFor(reclaimed.Phase),
                    reclaimed.DispatchAttemptCount,
                    null);
        }
    }

    public void MarkAccepted(string idempotencyKey, long processEpoch) =>
        Transition(
            idempotencyKey,
            processEpoch,
            CommandExecutionPhase.Reserved,
            CommandExecutionPhase.Accepted);

    public int MarkTransportAttempted(string idempotencyKey, long processEpoch)
    {
        lock (_sync)
        {
            EnsureUsable();
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase != CommandExecutionPhase.Accepted)
                throw new InvalidOperationException(
                    $"command phase {state.Phase} cannot transition to TransportAttempted");
            if (state.DispatchAttemptCount >= CommandDispatchPolicy.MaximumAttempts)
                throw new InvalidOperationException("persistent dispatch-attempt budget is exhausted");
            var next = state with
            {
                Phase = CommandExecutionPhase.TransportAttempted,
                DispatchAttemptCount = checked(state.DispatchAttemptCount + 1)
            };
            PersistState(idempotencyKey, next, processEpoch);
            _states[idempotencyKey] = next;
            return next.DispatchAttemptCount;
        }
    }

    public void MarkPreDispatchRetry(string idempotencyKey, long processEpoch) =>
        Transition(
            idempotencyKey,
            processEpoch,
            CommandExecutionPhase.TransportAttempted,
            CommandExecutionPhase.Accepted);

    public void MarkDispatchAcknowledged(string idempotencyKey, long processEpoch) =>
        Transition(
            idempotencyKey,
            processEpoch,
            CommandExecutionPhase.TransportAttempted,
            CommandExecutionPhase.DispatchAcknowledged);

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
            EnsureUsable();
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase == CommandExecutionPhase.Reserved)
                throw new InvalidOperationException("reserved command cannot complete before acceptance");
            if (state.Phase is CommandExecutionPhase.CompletionPrepared or CommandExecutionPhase.Completed)
                throw new InvalidOperationException(
                    $"command phase {state.Phase} cannot prepare completion");
            if (!string.Equals(journalContext.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                throw new InvalidDataException("prepared Journal context does not match command idempotency");
            var next = state with
            {
                Phase = CommandExecutionPhase.CompletionPrepared,
                CompletionSourcePhase = state.Phase,
                Receipt = receipt,
                PreparedTerminalWrite = terminalWrite,
                PreparedJournalContext = journalContext,
                TerminalJournalReceipt = null
            };
            ValidateState(idempotencyKey, next, processEpoch);
            PersistState(idempotencyKey, next, processEpoch);
            _states[idempotencyKey] = next;
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
            EnsureUsable();
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase != CommandExecutionPhase.CompletionPrepared ||
                state.PreparedTerminalWrite is not { } prepared ||
                state.PreparedJournalContext is not { } context)
                throw new InvalidOperationException("command completion was not durably prepared");
            var request = WorkerJournalAppendRequest.Create(context, prepared);
            WorkerJournalReceiptValidator.Validate(request, journalReceipt);
            var completed = state with
            {
                Phase = CommandExecutionPhase.Completed,
                TerminalJournalReceipt = journalReceipt
            };
            PersistState(idempotencyKey, completed, processEpoch);
            _states[idempotencyKey] = completed;
        }
    }

    public IReadOnlyList<PreparedCommandCompletion> ClaimPreparedCompletions(long processEpoch)
    {
        lock (_sync)
        {
            EnsureUsable();
            RequireCurrentProcessEpoch(processEpoch);
            var prepared = new List<PreparedCommandCompletion>();
            foreach (var pair in _states.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray())
            {
                var state = pair.Value;
                if (state.Phase != CommandExecutionPhase.CompletionPrepared)
                    continue;
                if (state.PreparedJournalContext is not { } context ||
                    state.Receipt is not { } receipt ||
                    state.PreparedTerminalWrite is not { } terminalWrite)
                    throw new InvalidDataException("prepared completion is incomplete");
                var claimed = state with { ClaimedProcessEpoch = processEpoch };
                PersistState(pair.Key, claimed, processEpoch);
                _states[pair.Key] = claimed;
                prepared.Add(new PreparedCommandCompletion(pair.Key, context, receipt, terminalWrite));
            }
            return prepared;
        }
    }

    public CommandDrainSnapshot GetDrainSnapshot()
    {
        lock (_sync)
        {
            EnsureUsable();
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

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _stateStream.Dispose();
            }
            finally
            {
                _writerLease.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void WriteLeaseIdentity(FileStream lease)
    {
        var payload = StrictUtf8.GetBytes(JsonSerializer.Serialize(new
        {
            schema_version = "1.0",
            process_id = Environment.ProcessId,
            opened_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }) + "\n");
        if (payload.Length > 4096)
            throw new IOException("worker writer lease identity exceeds the hard limit");
        lease.SetLength(0);
        lease.Position = 0;
        lease.Write(payload);
        lease.Flush(flushToDisk: true);
    }

    private void Transition(
        string idempotencyKey,
        long processEpoch,
        CommandExecutionPhase expected,
        CommandExecutionPhase nextPhase)
    {
        lock (_sync)
        {
            EnsureUsable();
            var state = RequireClaim(idempotencyKey, processEpoch);
            if (state.Phase != expected)
                throw new InvalidOperationException(
                    $"command phase {state.Phase} cannot transition to {nextPhase}");
            var next = state with { Phase = nextPhase };
            ValidateState(idempotencyKey, next, processEpoch);
            PersistState(idempotencyKey, next, processEpoch);
            _states[idempotencyKey] = next;
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

    private void PersistState(string idempotencyKey, State state, long processEpoch)
    {
        ValidateState(idempotencyKey, state, processEpoch);
        PersistEvent(new StateEventPayload(
            "1.0",
            "COMMAND_STATE",
            processEpoch,
            idempotencyKey,
            DurableStatePayload.FromState(state)));
    }

    private void PersistEvent(StateEventPayload payload)
    {
        EnsureUsable();
        VerifyActivePaths(_committedLength);
        var payloadJson = CanonicalStateJson.Canonicalize(
            JsonSerializer.Serialize(payload, SerializerOptions));
        var payloadBytes = StrictUtf8.GetBytes(payloadJson);
        if (payloadBytes.Length > MaximumStatePayloadBytes)
            throw new IOException("durable command-state payload exceeds the hard byte limit");
        var payloadSha256 = Sha256(payloadBytes);
        var sequence = checked(_lastSequence + 1);
        var entryChecksum = StateChecksum.Compute(
            EntryChecksumDomain,
            sequence.ToString(CultureInfo.InvariantCulture),
            _lastChecksum,
            payloadSha256,
            ChecksumEncoding);
        var line = new StateLogLine(
            "1.0",
            sequence,
            _lastChecksum,
            payloadJson,
            payloadSha256,
            ChecksumEncoding,
            entryChecksum);
        var serialized = CanonicalStateJson.Canonicalize(
            JsonSerializer.Serialize(line, SerializerOptions));
        var bytes = StrictUtf8.GetBytes(serialized + "\n");
        if (bytes.Length > MaximumStateRecordBytes)
            throw new IOException("durable command-state record exceeds the hard byte limit");
        if (_stateStream.Length != _committedLength)
        {
            _faulted = true;
            throw new IOException("durable command-state file changed outside the active writer lease");
        }
        if (_committedLength > MaximumStateFileBytes - bytes.Length)
            throw new IOException("durable command-state file reached its hard capacity limit");

        try
        {
            _stateStream.Position = _committedLength;
            _stateStream.Write(bytes);
            _stateStream.Flush(flushToDisk: true);
            _committedLength = checked(_committedLength + bytes.Length);
            _lastSequence = sequence;
            _lastChecksum = entryChecksum;
            VerifyActivePaths(_committedLength);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void Recover()
    {
        if (_stateStream.Length > MaximumStateFileBytes)
            throw new InvalidDataException("durable command-state file exceeds the hard recovery limit");
        if (_stateStream.Length == 0)
        {
            _committedLength = 0;
            return;
        }

        var length = checked((int)_stateStream.Length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        _stateStream.Position = 0;
        _stateStream.ReadExactly(bytes);
        var committedLength = bytes.Length;
        if (bytes[^1] != (byte)'\n')
        {
            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
            committedLength = lastNewline + 1;
            var tailLength = bytes.Length - committedLength;
            if (tailLength > MaximumStateRecordBytes)
                throw new InvalidDataException("durable command-state crash tail exceeds the hard limit");
            var tail = bytes.AsSpan(committedLength, tailLength);
            var tailHash = Sha256(tail);
            var crashTailPath = _statePath + "." + tailHash[..16] + ".crash-tail";
            VerifyCrashTailCapacity(crashTailPath);
            SecureRuntimeFileSystem.WritePrivateFileCreateNewOrVerify(
                crashTailPath,
                tail,
                MaximumStateRecordBytes);
            _stateStream.SetLength(committedLength);
            _stateStream.Flush(flushToDisk: true);
        }

        var start = 0;
        for (var index = 0; index < committedLength; index++)
        {
            if (bytes[index] != (byte)'\n')
                continue;
            var lineLength = index - start;
            if (lineLength == 0 || lineLength > MaximumStateRecordBytes)
                throw new InvalidDataException("durable command-state contains an invalid record length");
            var serialized = StrictUtf8.GetString(bytes, start, lineLength);
            RecoverLine(serialized);
            start = index + 1;
        }
        if (start != committedLength)
            throw new InvalidDataException("durable command-state recovery did not consume the committed bytes");
        _committedLength = committedLength;
        _stateStream.Position = committedLength;
        VerifyActivePaths(committedLength);
    }

    private void RecoverLine(string serialized)
    {
        StateLogLine line;
        try
        {
            if (!string.Equals(
                    serialized,
                    CanonicalStateJson.Canonicalize(serialized),
                    StringComparison.Ordinal))
                throw new JsonException("state record JSON is not canonical");
            line = JsonSerializer.Deserialize<StateLogLine>(serialized, SerializerOptions) ??
                throw new JsonException("state record deserialized to null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("durable command-state record is malformed", exception);
        }

        if (line.SchemaVersion != "1.0" || line.ChecksumEncoding != ChecksumEncoding)
            throw new InvalidDataException("durable command-state record uses an unknown format");
        if (line.Sequence != checked(_lastSequence + 1) ||
            !string.Equals(line.PreviousChecksum, _lastChecksum, StringComparison.Ordinal))
            throw new InvalidDataException("durable command-state sequence or checksum chain is broken");
        if (!IsLowerSha256(line.PayloadSha256) || !IsLowerSha256(line.EntryChecksum))
            throw new InvalidDataException("durable command-state record contains an invalid digest");
        if (!string.Equals(
                line.PayloadJson,
                CanonicalStateJson.Canonicalize(line.PayloadJson),
                StringComparison.Ordinal))
            throw new InvalidDataException("durable command-state payload is not canonical JSON");
        var payloadBytes = StrictUtf8.GetBytes(line.PayloadJson);
        if (payloadBytes.Length > MaximumStatePayloadBytes ||
            !string.Equals(Sha256(payloadBytes), line.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("durable command-state payload digest is invalid");
        var expectedChecksum = StateChecksum.Compute(
            EntryChecksumDomain,
            line.Sequence.ToString(CultureInfo.InvariantCulture),
            line.PreviousChecksum,
            line.PayloadSha256,
            line.ChecksumEncoding);
        if (!string.Equals(expectedChecksum, line.EntryChecksum, StringComparison.Ordinal))
            throw new InvalidDataException("durable command-state entry checksum is invalid");

        StateEventPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<StateEventPayload>(line.PayloadJson, SerializerOptions) ??
                throw new JsonException("state payload deserialized to null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("durable command-state payload is malformed", exception);
        }
        ApplyRecoveredEvent(payload);
        _lastSequence = line.Sequence;
        _lastChecksum = line.EntryChecksum;
    }

    private void ApplyRecoveredEvent(StateEventPayload payload)
    {
        if (payload.SchemaVersion != "1.0" || payload.ProcessEpoch < 1)
            throw new InvalidDataException("durable command-state event identity is invalid");
        if (payload.EventKind == "PROCESS_EPOCH")
        {
            if (payload.IdempotencyKey is not null || payload.State is not null ||
                payload.ProcessEpoch != checked(_processEpoch + 1))
                throw new InvalidDataException("durable process-epoch event is inconsistent");
            _processEpoch = payload.ProcessEpoch;
            return;
        }
        if (payload.EventKind != "COMMAND_STATE" || payload.IdempotencyKey is null ||
            payload.State is null || payload.ProcessEpoch != _processEpoch)
            throw new InvalidDataException("durable command-state event is inconsistent");
        if (!_states.ContainsKey(payload.IdempotencyKey) && _states.Count >= MaximumCommands)
            throw new InvalidDataException("durable command-state command capacity is exceeded");
        var state = payload.State.ToState();
        ValidateState(payload.IdempotencyKey, state, payload.ProcessEpoch);
        _states[payload.IdempotencyKey] = state;
    }

    private static void ValidateState(string idempotencyKey, State state, long processEpoch)
    {
        ValidateIdempotencyAndHash(idempotencyKey, state.RequestSha256);
        if (state.ClaimedProcessEpoch < 1 || state.ClaimedProcessEpoch > processEpoch)
            throw new InvalidDataException("durable command-state process claim is invalid");
        if (state.DispatchAttemptCount is < 0 or > CommandDispatchPolicy.MaximumAttempts)
            throw new InvalidDataException("durable command-state dispatch count is invalid");
        if (!Enum.IsDefined(state.Phase))
            throw new InvalidDataException("durable command-state phase is unknown");
        if (state.Phase == CommandExecutionPhase.Reserved && state.DispatchAttemptCount != 0)
            throw new InvalidDataException("reserved durable state cannot contain dispatch attempts");
        if (state.Phase is CommandExecutionPhase.TransportAttempted or CommandExecutionPhase.DispatchAcknowledged &&
            state.DispatchAttemptCount == 0)
            throw new InvalidDataException("attempted durable state must contain a dispatch attempt");

        var requiresCompletion = state.Phase is
            CommandExecutionPhase.CompletionPrepared or CommandExecutionPhase.Completed;
        if (requiresCompletion != (state.Receipt is not null &&
                                   state.PreparedTerminalWrite is not null &&
                                   state.PreparedJournalContext is not null &&
                                   state.CompletionSourcePhase is not null))
            throw new InvalidDataException("durable completion fields do not match the command phase");
        if ((state.Phase == CommandExecutionPhase.Completed) !=
            (state.TerminalJournalReceipt is not null))
            throw new InvalidDataException(
                "durable terminal Journal proof does not match the command phase");
        if (!requiresCompletion)
        {
            if (state.TerminalJournalReceipt is not null || state.CompletionSourcePhase is not null)
                throw new InvalidDataException(
                    "nonterminal durable state cannot contain completion proof");
            return;
        }

        var receipt = state.Receipt!;
        var context = state.PreparedJournalContext!;
        var write = state.PreparedTerminalWrite!;
        var completionSource = state.CompletionSourcePhase!.Value;
        if (!Enum.IsDefined(completionSource) || completionSource is
            CommandExecutionPhase.CompletionPrepared or CommandExecutionPhase.Completed)
            throw new InvalidDataException("durable completion source phase is invalid");
        if (!string.Equals(receipt.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(context.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) ||
            !CanonicalIds.IsSoul(context.SoulId) ||
            !CanonicalIds.IsDeviceBinding(context.DeviceBindingId) ||
            !CanonicalIds.IsPlatformAccount(context.PlatformAccountId) ||
            !CanonicalIds.IsTrace(context.TraceId) ||
            context.OccurredAt.Offset != TimeSpan.Zero ||
            context.PrivacyClass is not ("internal" or "personal" or "sensitive") ||
            !JournalIdentifiers.IsAsciiToken(context.CommandId, 128) ||
            !string.Equals(receipt.CommandId, context.CommandId, StringComparison.Ordinal))
            throw new InvalidDataException("durable completion scope is inconsistent");
        if (receipt.CommandId is null || receipt.IdempotencyKey is null ||
            receipt.ResultStatus is null || receipt.Detail is null ||
            receipt.Detail.Length > 4096 || !IsValidUtf8(receipt.Detail) ||
            write.EntryId is null || write.EntryType is null || write.Detail is null ||
            write.PayloadJson is null || write.PayloadSha256 is null ||
            context.SoulId is null || context.DeviceBindingId is null ||
            context.PlatformAccountId is null || context.TraceId is null ||
            context.IdempotencyKey is null || context.PrivacyClass is null ||
            context.CommandId is null || !ReceiptTruthIsValid(receipt))
            throw new InvalidDataException("durable completion receipt truth is invalid");
        ValidateCompletionTransition(completionSource, state.DispatchAttemptCount, receipt);
        var expectedWrite = WorkerJournalWrite.Create(
            context,
            "TERMINAL",
            receipt.ResultStatus,
            receipt.Detail);
        if (write != expectedWrite)
            throw new InvalidDataException("durable terminal Journal write is not deterministic");
        if (state.TerminalJournalReceipt is { } terminalReceipt)
        {
            var request = WorkerJournalAppendRequest.Create(context, write);
            WorkerJournalReceiptValidator.Validate(request, terminalReceipt);
        }
    }

    private static void ValidateCompletionTransition(
        CommandExecutionPhase source,
        int dispatchAttemptCount,
        CommandReceipt receipt)
    {
        var valid = receipt.ResultStatus switch
        {
            "SHADOWED" =>
                source == CommandExecutionPhase.Accepted && dispatchAttemptCount == 0,
            "VERIFIED_SUCCESS" =>
                source == CommandExecutionPhase.DispatchAcknowledged && dispatchAttemptCount > 0,
            "UNKNOWN_OUTCOME" when receipt.DispatchAcknowledged is null =>
                source == CommandExecutionPhase.TransportAttempted && dispatchAttemptCount > 0,
            "UNKNOWN_OUTCOME" when receipt.DispatchAcknowledged == true =>
                source == CommandExecutionPhase.DispatchAcknowledged && dispatchAttemptCount > 0,
            "FAILED" when receipt.DispatchAcknowledged == false =>
                source == CommandExecutionPhase.Accepted &&
                dispatchAttemptCount == CommandDispatchPolicy.MaximumAttempts,
            "FAILED" when receipt.DispatchAcknowledged == true =>
                source == CommandExecutionPhase.DispatchAcknowledged && dispatchAttemptCount > 0,
            _ => false
        };
        if (!valid)
            throw new InvalidDataException(
                "durable completion receipt is inconsistent with its source phase");
    }

    private static bool ReceiptTruthIsValid(CommandReceipt receipt) => receipt.ResultStatus switch
    {
        "VERIFIED_SUCCESS" =>
            receipt.DispatchAcknowledged == true && receipt.NativeStatus == NativeStatus.Success &&
            receipt.PostconditionVerified == true && !receipt.RetryAllowed,
        "UNKNOWN_OUTCOME" =>
            receipt.DispatchAcknowledged != false && receipt.NativeStatus == NativeStatus.UnknownOutcome &&
            receipt.PostconditionVerified is null && !receipt.RetryAllowed,
        "FAILED" =>
            receipt.DispatchAcknowledged is not null && !receipt.RetryAllowed &&
            (receipt.DispatchAcknowledged == false
                ? receipt.NativeStatus == NativeStatus.Failed && receipt.PostconditionVerified == false
                : receipt.NativeStatus == NativeStatus.Success
                    ? receipt.PostconditionVerified == false
                    : receipt.NativeStatus == NativeStatus.Failed && receipt.PostconditionVerified is not null),
        "SHADOWED" =>
            receipt.DispatchAcknowledged == false && receipt.NativeStatus is null &&
            receipt.PostconditionVerified is null && !receipt.RetryAllowed,
        _ => false
    };

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new IOException("durable command-state store is faulted and requires process restart");
    }

    private void VerifyActivePaths(long expectedStateLength)
    {
        if (!string.Equals(
                SecureRuntimeFileSystem.PrepareDirectory(_runtimeDirectory),
                _runtimeDirectory,
                StringComparison.Ordinal))
            throw new IOException("worker runtime directory identity changed");
        SecureRuntimeFileSystem.VerifyExistingFile(_leasePath);
        SecureRuntimeFileSystem.VerifyExistingFile(_statePath);
        SecureRuntimeFileSystem.VerifyOpenFileIdentity(
            _writerLease,
            _leasePath,
            _writerLeaseIdentity);
        SecureRuntimeFileSystem.VerifyOpenFileIdentity(
            _stateStream,
            _statePath,
            _stateIdentity);
        var pathLength = new FileInfo(_statePath).Length;
        if (_stateStream.Length != expectedStateLength || pathLength != expectedStateLength)
            throw new IOException(
                "durable command-state path changed outside the active writer lease");
    }

    private void VerifyCrashTailCapacity(string targetPath)
    {
        var artifacts = Directory.EnumerateFileSystemEntries(
                _runtimeDirectory,
                "command-state.jsonl.*.crash-tail",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumCrashTailArtifacts + 1)
            .ToArray();
        if (artifacts.Length > MaximumCrashTailArtifacts ||
            artifacts.Length == MaximumCrashTailArtifacts &&
            !artifacts.Contains(targetPath, StringComparer.Ordinal))
        {
            throw new IOException("worker command-state crash-tail artifact capacity is exhausted");
        }
    }

    private static void ValidateIdempotencyAndHash(string idempotencyKey, string requestSha256)
    {
        if (!CanonicalIds.IsIdempotency(idempotencyKey))
            throw new InvalidDataException("durable state idempotency key is not canonical");
        if (!IsLowerSha256(requestSha256))
            throw new InvalidDataException("durable state request hash is not lowercase SHA-256");
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidUtf8(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool? AcknowledgementFor(CommandExecutionPhase phase) => phase switch
    {
        CommandExecutionPhase.Reserved or CommandExecutionPhase.Accepted => false,
        CommandExecutionPhase.TransportAttempted => null,
        CommandExecutionPhase.DispatchAcknowledged => true,
        CommandExecutionPhase.CompletionPrepared => null,
        _ => null
    };

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record State(
        string RequestSha256,
        long ClaimedProcessEpoch,
        CommandExecutionPhase Phase,
        int DispatchAttemptCount,
        CommandReceipt? Receipt,
        WorkerJournalWrite? PreparedTerminalWrite,
        WorkerJournalContext? PreparedJournalContext,
        CommandExecutionPhase? CompletionSourcePhase,
        WorkerJournalAppendReceipt? TerminalJournalReceipt);

    private sealed record StateEventPayload(
        [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
        [property: JsonPropertyName("event_kind"), JsonRequired] string EventKind,
        [property: JsonPropertyName("process_epoch"), JsonRequired] long ProcessEpoch,
        [property: JsonPropertyName("idempotency_key"), JsonRequired] string? IdempotencyKey,
        [property: JsonPropertyName("state"), JsonRequired] DurableStatePayload? State);

    private sealed record DurableStatePayload(
        [property: JsonPropertyName("request_sha256"), JsonRequired] string RequestSha256,
        [property: JsonPropertyName("claimed_process_epoch"), JsonRequired] long ClaimedProcessEpoch,
        [property: JsonPropertyName("phase"), JsonRequired] CommandExecutionPhase Phase,
        [property: JsonPropertyName("dispatch_attempt_count"), JsonRequired] int DispatchAttemptCount,
        [property: JsonPropertyName("receipt"), JsonRequired] CommandReceipt? Receipt,
        [property: JsonPropertyName("prepared_terminal_write"), JsonRequired] WorkerJournalWrite? PreparedTerminalWrite,
        [property: JsonPropertyName("prepared_journal_context"), JsonRequired] WorkerJournalContext? PreparedJournalContext,
        [property: JsonPropertyName("completion_source_phase"), JsonRequired] CommandExecutionPhase? CompletionSourcePhase,
        [property: JsonPropertyName("terminal_journal_receipt"), JsonRequired] WorkerJournalAppendReceipt? TerminalJournalReceipt)
    {
        public static DurableStatePayload FromState(State state) => new(
            state.RequestSha256,
            state.ClaimedProcessEpoch,
            state.Phase,
            state.DispatchAttemptCount,
            state.Receipt,
            state.PreparedTerminalWrite,
            state.PreparedJournalContext,
            state.CompletionSourcePhase,
            state.TerminalJournalReceipt);

        public State ToState() => new(
            RequestSha256,
            ClaimedProcessEpoch,
            Phase,
            DispatchAttemptCount,
            Receipt,
            PreparedTerminalWrite,
            PreparedJournalContext,
            CompletionSourcePhase,
            TerminalJournalReceipt);
    }

    private sealed record StateLogLine(
        [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
        [property: JsonPropertyName("sequence"), JsonRequired] long Sequence,
        [property: JsonPropertyName("previous_checksum"), JsonRequired] string PreviousChecksum,
        [property: JsonPropertyName("payload_json"), JsonRequired] string PayloadJson,
        [property: JsonPropertyName("payload_sha256"), JsonRequired] string PayloadSha256,
        [property: JsonPropertyName("checksum_encoding"), JsonRequired] string ChecksumEncoding,
        [property: JsonPropertyName("entry_checksum"), JsonRequired] string EntryChecksum);
}

internal static class CanonicalStateJson
{
    public static string Canonicalize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, document.RootElement);
        }
        return new UTF8Encoding(false, true).GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties)
                    if (!names.Add(property.Name))
                        throw new JsonException("duplicate JSON property: " + property.Name);
                foreach (var property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

internal static class StateChecksum
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Compute(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        WriteField(stream, domain);
        WriteLength(stream, fields.Length);
        foreach (var field in fields)
            WriteField(stream, field);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteField(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteLength(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteLength(Stream stream, int value)
    {
        if (value < 0)
            throw new InvalidDataException("state checksum field length cannot be negative");
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value));
        stream.Write(length);
    }
}
