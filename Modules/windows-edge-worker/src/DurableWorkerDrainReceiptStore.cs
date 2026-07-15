using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.WindowsEdgeWorker;

public enum WorkerDrainReceiptPersistenceState
{
    Prepared,
    Committed
}

public sealed record PersistedWorkerDrainReceipt(
    string DrainId,
    string InputFingerprintSha256,
    byte[] ExactWireUtf8,
    string WireSha256,
    WorkerDrainReceiptPersistenceState State,
    string? JournalEntryId,
    string? JournalEntryChecksum,
    long? JournalSequence);

public sealed class WorkerDrainReceiptConflictException(string message) : InvalidOperationException(message);

public sealed class DurableWorkerDrainReceiptStore : IDisposable, IAsyncDisposable
{
    private const long MaximumStateFileBytes = 128L * 1024 * 1024;
    private const int MaximumRecordBytes = 128 * 1024;
    private const int MaximumWireBytes = 32 * 1024;
    private const int MaximumRecords = 4096;
    private const int MaximumCrashTailBytes = MaximumRecordBytes;
    private const int MaximumCrashTailArtifacts = 64;
    private const string GenesisChecksum =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private const string RecordChecksumDomain =
        "dps.windows-edge-worker.drain-receipt-state/v1";
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
    private readonly Dictionary<string, StoredState> _states = new(StringComparer.Ordinal);
    private readonly FileStream _writerLease;
    private readonly FileStream _stateStream;
    private readonly string _writerLeasePath;
    private readonly string _statePath;
    private readonly RuntimeFileIdentity? _writerLeaseIdentity;
    private readonly RuntimeFileIdentity? _stateIdentity;
    private long _lastSequence;
    private string _lastChecksum = GenesisChecksum;
    private bool _faulted;
    private bool _disposed;

    private DurableWorkerDrainReceiptStore(
        string writerLeasePath,
        string statePath,
        FileStream writerLease,
        FileStream stateStream,
        RuntimeFileIdentity? writerLeaseIdentity,
        RuntimeFileIdentity? stateIdentity)
    {
        _writerLeasePath = writerLeasePath;
        _statePath = statePath;
        _writerLease = writerLease;
        _stateStream = stateStream;
        _writerLeaseIdentity = writerLeaseIdentity;
        _stateIdentity = stateIdentity;
    }

    public static DurableWorkerDrainReceiptStore Open(string runtimeDirectory)
    {
        var directory = SecureRuntimeFileSystem.PrepareDirectory(runtimeDirectory);
        var writerLeasePath = Path.Combine(directory, "drain-receipts.writer.lock");
        var statePath = Path.Combine(directory, "drain-receipts.jsonl");
        SecureRuntimeFileSystem.VerifyExistingFile(writerLeasePath);
        SecureRuntimeFileSystem.VerifyExistingFile(statePath);

        FileStream? writerLease = null;
        FileStream? stateStream = null;
        try
        {
            writerLease = SecureRuntimeFileSystem.OpenOrCreatePrivateFile(
                writerLeasePath,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.WriteThrough);
            stateStream = SecureRuntimeFileSystem.OpenOrCreatePrivateFile(
                statePath,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.WriteThrough);
            var store = new DurableWorkerDrainReceiptStore(
                writerLeasePath,
                statePath,
                writerLease,
                stateStream,
                SecureRuntimeFileSystem.CaptureOpenFileIdentity(writerLease, writerLeasePath),
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

    public PersistedWorkerDrainReceipt? Read(
        string drainId,
        string inputFingerprintSha256)
    {
        lock (_sync)
        {
            EnsureUsable();
            ValidateDrainAndFingerprint(drainId, inputFingerprintSha256);
            if (!_states.TryGetValue(drainId, out var state))
                return null;
            if (state.Quarantined || state.InputFingerprintSha256 != inputFingerprintSha256)
            {
                if (!state.Quarantined)
                    QuarantineConflict(state, inputFingerprintSha256);
                throw new WorkerDrainReceiptConflictException(
                    "drain_id was reused with a different signed-input fingerprint");
            }
            return ToSnapshot(state);
        }
    }

    public PersistedWorkerDrainReceipt? ReadExisting(string drainId)
    {
        lock (_sync)
        {
            EnsureUsable();
            if (!IsPrefixedLowerHex(drainId, "drain-", 64))
                throw new ArgumentException("drain_id is not canonical", nameof(drainId));
            if (!_states.TryGetValue(drainId, out var state))
                return null;
            if (state.Quarantined)
                throw new WorkerDrainReceiptConflictException(
                    "drain_id is durably quarantined after conflicting signed input");
            return ToSnapshot(state);
        }
    }

    public PersistedWorkerDrainReceipt Prepare(
        string drainId,
        string inputFingerprintSha256,
        ReadOnlySpan<byte> exactWireUtf8)
    {
        lock (_sync)
        {
            EnsureUsable();
            ValidateDrainAndFingerprint(drainId, inputFingerprintSha256);
            ValidateWire(exactWireUtf8);
            if (_states.TryGetValue(drainId, out var existing))
            {
                if (existing.Quarantined || existing.InputFingerprintSha256 != inputFingerprintSha256)
                {
                    if (!existing.Quarantined)
                        QuarantineConflict(existing, inputFingerprintSha256);
                    throw new WorkerDrainReceiptConflictException(
                        "drain_id was reused with a different signed-input fingerprint");
                }
                return ToSnapshot(existing);
            }

            var wire = exactWireUtf8.ToArray();
            var wireSha256 = Sha256(wire);
            var created = new StoredState(
                drainId,
                inputFingerprintSha256,
                wire,
                wireSha256,
                WorkerDrainReceiptPersistenceState.Prepared,
                null,
                null,
                null,
                Quarantined: false);
            Persist(created, EventKind.Prepared, conflictingFingerprintSha256: null);
            _states.Add(drainId, created);
            return ToSnapshot(created);
        }
    }

    public PersistedWorkerDrainReceipt Commit(
        string drainId,
        string inputFingerprintSha256,
        string workerWireSha256,
        string journalEntryId,
        string journalEntryChecksum,
        long journalSequence)
    {
        lock (_sync)
        {
            EnsureUsable();
            ValidateDrainAndFingerprint(drainId, inputFingerprintSha256);
            RequireLowerSha256(workerWireSha256, nameof(workerWireSha256));
            RequireJournalEntryId(journalEntryId);
            RequireLowerSha256(journalEntryChecksum, nameof(journalEntryChecksum));
            if (journalSequence < 1)
                throw new ArgumentOutOfRangeException(nameof(journalSequence));
            if (!_states.TryGetValue(drainId, out var existing))
                throw new InvalidOperationException("worker drain receipt must be PREPARED before Journal commit");
            if (existing.Quarantined || existing.InputFingerprintSha256 != inputFingerprintSha256)
            {
                if (!existing.Quarantined)
                    QuarantineConflict(existing, inputFingerprintSha256);
                throw new WorkerDrainReceiptConflictException(
                    "drain_id was reused with a different signed-input fingerprint");
            }
            if (existing.WireSha256 != workerWireSha256)
                throw new InvalidDataException("worker receipt wire digest changed after PREPARED");
            if (existing.PersistenceState == WorkerDrainReceiptPersistenceState.Committed)
            {
                if (existing.JournalEntryId != journalEntryId ||
                    existing.JournalEntryChecksum != journalEntryChecksum ||
                    existing.JournalSequence != journalSequence)
                    throw new InvalidDataException(
                        "Journal locator changed after the worker drain receipt was COMMITTED");
                return ToSnapshot(existing);
            }

            var committed = existing with
            {
                PersistenceState = WorkerDrainReceiptPersistenceState.Committed,
                JournalEntryId = journalEntryId,
                JournalEntryChecksum = journalEntryChecksum,
                JournalSequence = journalSequence
            };
            Persist(committed, EventKind.Committed, conflictingFingerprintSha256: null);
            _states[drainId] = committed;
            return ToSnapshot(committed);
        }
    }

    private void Recover()
    {
        lock (_sync)
        {
            VerifyBoundFiles();
            if (_stateStream.Length > MaximumStateFileBytes)
                throw new IOException("worker drain-receipt state exceeds its hard byte limit");
            if (_stateStream.Length > int.MaxValue)
                throw new IOException("worker drain-receipt state is too large to recover safely");
            _stateStream.Position = 0;
            var bytes = GC.AllocateUninitializedArray<byte>((int)_stateStream.Length);
            _stateStream.ReadExactly(bytes);
            var committedLength = FindCommittedLength(bytes);
            if (committedLength != bytes.Length)
            {
                IsolateCrashTail(bytes.AsSpan(committedLength));
                _stateStream.SetLength(committedLength);
                _stateStream.Flush(flushToDisk: true);
                bytes = bytes[..committedLength];
            }

            var offset = 0;
            var recordCount = 0;
            while (offset < bytes.Length)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', offset);
                if (newline < 0)
                    throw new InvalidDataException("worker drain-receipt state has an incomplete record");
                var length = newline - offset;
                if (length is < 1 or > MaximumRecordBytes)
                    throw new InvalidDataException("worker drain-receipt state record size is invalid");
                if (bytes[newline - 1] == (byte)'\r')
                    throw new InvalidDataException("worker drain-receipt state uses noncanonical line endings");
                var record = DeserializeRecord(bytes.AsSpan(offset, length));
                ApplyRecovered(record);
                offset = newline + 1;
                recordCount++;
                if (recordCount > MaximumRecords)
                    throw new IOException("worker drain-receipt state record count exceeds its hard limit");
            }
            _stateStream.Position = _stateStream.Length;
            VerifyBoundFiles();
        }
    }

    private void ApplyRecovered(StateRecord record)
    {
        if (record.SchemaVersion != "1.0" ||
            record.Sequence != checked(_lastSequence + 1) ||
            record.PreviousChecksum != _lastChecksum)
            throw new InvalidDataException("worker drain-receipt state chain is invalid");
        ValidateDrainAndFingerprint(record.DrainId, record.InputFingerprintSha256);
        RequireLowerSha256(record.WorkerWireSha256, nameof(record.WorkerWireSha256));
        var wire = DecodeCanonicalBase64(record.WorkerWireBase64);
        ValidateWire(wire);
        if (Sha256(wire) != record.WorkerWireSha256)
            throw new InvalidDataException("persisted worker receipt wire digest is invalid");
        var expectedChecksum = ComputeRecordChecksum(record with { RecordChecksum = string.Empty });
        if (record.RecordChecksum != expectedChecksum)
            throw new InvalidDataException("worker drain-receipt state checksum is invalid");

        switch (record.EventKind)
        {
            case EventKind.Prepared:
                if (_states.ContainsKey(record.DrainId) || record.JournalEntryId is not null ||
                    record.JournalEntryChecksum is not null || record.JournalSequence is not null ||
                    record.ConflictingInputFingerprintSha256 is not null)
                    throw new InvalidDataException("worker drain PREPARED transition is invalid");
                _states.Add(record.DrainId, new StoredState(
                    record.DrainId,
                    record.InputFingerprintSha256,
                    wire,
                    record.WorkerWireSha256,
                    WorkerDrainReceiptPersistenceState.Prepared,
                    null,
                    null,
                    null,
                    Quarantined: false));
                break;
            case EventKind.Committed:
                var prepared = RequirePreparedState(record);
                RequireJournalEntryId(record.JournalEntryId);
                RequireLowerSha256(record.JournalEntryChecksum, nameof(record.JournalEntryChecksum));
                if (record.JournalSequence is null or < 1 ||
                    record.ConflictingInputFingerprintSha256 is not null)
                    throw new InvalidDataException("worker drain COMMITTED transition is invalid");
                _states[record.DrainId] = prepared with
                {
                    PersistenceState = WorkerDrainReceiptPersistenceState.Committed,
                    JournalEntryId = record.JournalEntryId,
                    JournalEntryChecksum = record.JournalEntryChecksum,
                    JournalSequence = record.JournalSequence
                };
                break;
            case EventKind.Quarantined:
                var prior = RequireExistingSameWire(record);
                RequireLowerSha256(
                    record.ConflictingInputFingerprintSha256,
                    nameof(record.ConflictingInputFingerprintSha256));
                if (prior.Quarantined ||
                    record.ConflictingInputFingerprintSha256 == prior.InputFingerprintSha256 ||
                    record.JournalEntryId != prior.JournalEntryId ||
                    record.JournalEntryChecksum != prior.JournalEntryChecksum ||
                    record.JournalSequence != prior.JournalSequence)
                    throw new InvalidDataException(
                        "worker drain quarantine transition changed prior truth or is not conflicting");
                _states[record.DrainId] = prior with { Quarantined = true };
                break;
            default:
                throw new InvalidDataException("unknown worker drain-receipt state event");
        }

        _lastSequence = record.Sequence;
        _lastChecksum = record.RecordChecksum;
    }

    private StoredState RequirePreparedState(StateRecord record)
    {
        var state = RequireExistingSameWire(record);
        if (state.Quarantined || state.PersistenceState != WorkerDrainReceiptPersistenceState.Prepared)
            throw new InvalidDataException("worker drain COMMITTED does not follow PREPARED");
        return state;
    }

    private StoredState RequireExistingSameWire(StateRecord record)
    {
        if (!_states.TryGetValue(record.DrainId, out var state) ||
            state.InputFingerprintSha256 != record.InputFingerprintSha256 ||
            state.WireSha256 != record.WorkerWireSha256 ||
            !state.ExactWireUtf8.AsSpan().SequenceEqual(DecodeCanonicalBase64(record.WorkerWireBase64)))
            throw new InvalidDataException("worker drain state transition changed the PREPARED proof");
        return state;
    }

    private void Persist(
        StoredState state,
        EventKind eventKind,
        string? conflictingFingerprintSha256)
    {
        VerifyBoundFiles();
        if (_lastSequence >= MaximumRecords)
            throw new IOException("worker drain-receipt state record count exceeds its hard limit");
        var record = new StateRecord(
            "1.0",
            checked(_lastSequence + 1),
            _lastChecksum,
            eventKind,
            state.DrainId,
            state.InputFingerprintSha256,
            Convert.ToBase64String(state.ExactWireUtf8),
            state.WireSha256,
            state.JournalEntryId,
            state.JournalEntryChecksum,
            state.JournalSequence,
            conflictingFingerprintSha256,
            string.Empty);
        record = record with { RecordChecksum = ComputeRecordChecksum(record) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
        if (bytes.Length is < 1 or > MaximumRecordBytes)
            throw new IOException("worker drain-receipt state record size exceeds its hard limit");
        if (_stateStream.Length + bytes.Length + 1 > MaximumStateFileBytes)
            throw new IOException("worker drain-receipt state exceeds its hard byte limit");
        try
        {
            _stateStream.Position = _stateStream.Length;
            _stateStream.Write(bytes);
            _stateStream.WriteByte((byte)'\n');
            _stateStream.Flush(flushToDisk: true);
            VerifyBoundFiles();
            _lastSequence = record.Sequence;
            _lastChecksum = record.RecordChecksum;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void QuarantineConflict(StoredState state, string conflictingFingerprintSha256)
    {
        Persist(state, EventKind.Quarantined, conflictingFingerprintSha256);
        _states[state.DrainId] = state with { Quarantined = true };
    }

    private void IsolateCrashTail(ReadOnlySpan<byte> tail)
    {
        if (tail.IsEmpty)
            return;
        if (tail.Length > MaximumCrashTailBytes)
            throw new IOException("worker drain-receipt crash tail exceeds its hard limit");
        var directory = Path.GetDirectoryName(_statePath)!;
        var artifacts = Directory.EnumerateFileSystemEntries(
                directory,
                "drain-receipts.jsonl.*.crash-tail",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumCrashTailArtifacts + 1)
            .ToArray();
        if (artifacts.Length > MaximumCrashTailArtifacts)
            throw new IOException("worker drain-receipt crash-tail count exceeds its hard limit");
        var path = _statePath + "." + Sha256(tail) + ".crash-tail";
        SecureRuntimeFileSystem.WritePrivateFileCreateNewOrVerify(
            path,
            tail,
            MaximumCrashTailBytes);
    }

    private void VerifyBoundFiles()
    {
        SecureRuntimeFileSystem.VerifyOpenFileIdentity(
            _writerLease,
            _writerLeasePath,
            _writerLeaseIdentity);
        SecureRuntimeFileSystem.VerifyOpenFileIdentity(
            _stateStream,
            _statePath,
            _stateIdentity);
    }

    private static int FindCommittedLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes[^1] == (byte)'\n')
            return bytes.Length;
        return bytes.LastIndexOf((byte)'\n') + 1;
    }

    private static StateRecord DeserializeRecord(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<StateRecord>(utf8, SerializerOptions) ??
                throw new InvalidDataException("worker drain-receipt state record is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("worker drain-receipt state JSON is invalid", exception);
        }
    }

    private static string ComputeRecordChecksum(StateRecord record)
    {
        using var stream = new MemoryStream();
        WriteComponent(stream, RecordChecksumDomain);
        WriteComponent(stream, record.SchemaVersion);
        WriteComponent(stream, record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteComponent(stream, record.PreviousChecksum);
        WriteComponent(stream, record.EventKind.ToString());
        WriteComponent(stream, record.DrainId);
        WriteComponent(stream, record.InputFingerprintSha256);
        WriteComponent(stream, record.WorkerWireBase64);
        WriteComponent(stream, record.WorkerWireSha256);
        WriteNullableComponent(stream, record.JournalEntryId);
        WriteNullableComponent(stream, record.JournalEntryChecksum);
        WriteNullableComponent(
            stream,
            record.JournalSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteNullableComponent(stream, record.ConflictingInputFingerprintSha256);
        return Sha256(stream.ToArray());
    }

    private static void WriteComponent(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteNullableComponent(Stream stream, string? value)
    {
        if (value is null)
        {
            Span<byte> marker = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(marker, -1);
            stream.Write(marker);
            return;
        }
        WriteComponent(stream, value);
    }

    private static byte[] DecodeCanonicalBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (Convert.ToBase64String(bytes) != value)
                throw new InvalidDataException("worker receipt wire is not canonical Base64");
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("worker receipt wire is not canonical Base64", exception);
        }
    }

    private static void ValidateWire(ReadOnlySpan<byte> exactWireUtf8)
    {
        if (exactWireUtf8.IsEmpty || exactWireUtf8.Length > MaximumWireBytes)
            throw new ArgumentOutOfRangeException(nameof(exactWireUtf8));
        try
        {
            _ = StrictUtf8.GetString(exactWireUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("worker receipt wire is not strict UTF-8", exception);
        }
    }

    private static void ValidateDrainAndFingerprint(
        string drainId,
        string inputFingerprintSha256)
    {
        if (!IsPrefixedLowerHex(drainId, "drain-", 64))
            throw new ArgumentException("drain_id is not canonical", nameof(drainId));
        RequireLowerSha256(inputFingerprintSha256, nameof(inputFingerprintSha256));
    }

    private static void RequireJournalEntryId(string? value)
    {
        if (value is null || !value.StartsWith("worker-drain-", StringComparison.Ordinal) ||
            !IsPrefixedLowerHex(value, "worker-drain-", 64))
            throw new ArgumentException("Journal entry_id is not a canonical worker drain locator");
    }

    private static void RequireLowerSha256(string? value, string parameter)
    {
        if (!IsLowerHex(value, 64))
            throw new ArgumentException(parameter + " must be 64 lowercase hexadecimal characters");
    }

    private static bool IsPrefixedLowerHex(string? value, string prefix, int bodyLength) =>
        value is not null && value.Length == prefix.Length + bodyLength &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        IsLowerHex(value[prefix.Length..], bodyLength);

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static PersistedWorkerDrainReceipt ToSnapshot(StoredState state) => new(
        state.DrainId,
        state.InputFingerprintSha256,
        state.ExactWireUtf8.ToArray(),
        state.WireSha256,
        state.PersistenceState,
        state.JournalEntryId,
        state.JournalEntryChecksum,
        state.JournalSequence);

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new IOException("worker drain-receipt store is faulted and requires restart recovery");
        VerifyBoundFiles();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stateStream.Dispose();
            _writerLease.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private enum EventKind
    {
        Prepared,
        Committed,
        Quarantined
    }

    private sealed record StoredState(
        string DrainId,
        string InputFingerprintSha256,
        byte[] ExactWireUtf8,
        string WireSha256,
        WorkerDrainReceiptPersistenceState PersistenceState,
        string? JournalEntryId,
        string? JournalEntryChecksum,
        long? JournalSequence,
        bool Quarantined);

    private sealed record StateRecord(
        string SchemaVersion,
        long Sequence,
        string PreviousChecksum,
        EventKind EventKind,
        string DrainId,
        string InputFingerprintSha256,
        string WorkerWireBase64,
        string WorkerWireSha256,
        string? JournalEntryId,
        string? JournalEntryChecksum,
        long? JournalSequence,
        string? ConflictingInputFingerprintSha256,
        string RecordChecksum);
}
