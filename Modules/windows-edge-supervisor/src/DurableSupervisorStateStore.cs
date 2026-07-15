using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.WindowsEdgeSupervisor;

/// <summary>
/// Checksum-chained local state for Supervisor routing truth, coordinated with
/// an externally protected anchor through a recoverable prepare/commit protocol.
/// Production startup must use Resume; Bootstrap is a separate provisioning
/// operation and refuses to replace any existing state.
/// </summary>
public sealed class DurableSupervisorStateStore : IDisposable
{
    private const int MaximumStateBytes = 1048576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };
    private readonly ISupervisorStateAnchor _anchor;
    private readonly object _operationSync = new();
    private FileStream? _writerLease;

    public DurableSupervisorStateStore(
        string absoluteStatePath,
        ISupervisorStateAnchor externallyProtectedAnchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteStatePath);
        ArgumentNullException.ThrowIfNull(externallyProtectedAnchor);
        if (!Path.IsPathFullyQualified(absoluteStatePath))
            throw new ArgumentException("Supervisor state path must be absolute", nameof(absoluteStatePath));
        _anchor = externallyProtectedAnchor;
        StatePath = Path.GetFullPath(absoluteStatePath);
        var directory = Path.GetDirectoryName(StatePath) ??
            throw new InvalidOperationException("Supervisor state path has no parent directory");
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        WriterLeasePath = StatePath + ".writer.lock";
        if (File.Exists(WriterLeasePath)) RejectLink(WriterLeasePath);
        var lease = new FileStream(
            WriterLeasePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        try
        {
            RejectLink(WriterLeasePath);
            _writerLease = lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public string StatePath { get; }
    public string WriterLeasePath { get; }
    public bool Exists => File.Exists(StatePath);

    internal StoredSupervisorState LoadRequired()
    {
        lock (_operationSync) return LoadRequiredCore();
    }

    private StoredSupervisorState LoadRequiredCore()
    {
        EnsureWriterLease();
        var snapshot = ReadValidatedAnchorSnapshot();
        if (!File.Exists(StatePath))
        {
            if (snapshot.Committed is null && snapshot.Prepared is not null)
            {
                AbortPreparedOrThrow(snapshot.Prepared);
                EnsureExactAnchorSnapshot(committed: null, prepared: null);
            }
            throw new InvalidOperationException(
                "durable Supervisor state is missing; restart fails closed and cannot bootstrap implicitly");
        }
        RejectLink(StatePath);
        var bytes = File.ReadAllBytes(StatePath);
        var stored = Decode(bytes);
        var local = new SupervisorStateAnchor(stored.Generation, stored.StateSha256);

        if (snapshot.Prepared is null)
        {
            if (snapshot.Committed != local)
                throw new InvalidDataException(
                    "durable Supervisor state does not match the externally protected committed anchor");
            return stored;
        }

        if (snapshot.Prepared.Next == local)
        {
            var requiredPrevious = snapshot.Committed?.StateSha256 ?? new string('0', 64);
            if (stored.PreviousStateSha256 != requiredPrevious)
                throw new InvalidDataException(
                    "prepared durable Supervisor state does not extend the committed external anchor");
            CommitPreparedOrThrow(snapshot.Prepared);
            EnsureExactAnchorSnapshot(snapshot.Prepared.Next, prepared: null);
            return stored;
        }

        if (snapshot.Committed is not null && snapshot.Committed == local)
        {
            AbortPreparedOrThrow(snapshot.Prepared);
            EnsureExactAnchorSnapshot(snapshot.Committed, prepared: null);
            return stored;
        }

        throw new InvalidDataException(
            "durable Supervisor state matches neither the committed nor the prepared external anchor");
    }

    internal StoredSupervisorState Initialize(SupervisorStatePayload payload)
    {
        lock (_operationSync) return InitializeCore(payload);
    }

    private StoredSupervisorState InitializeCore(SupervisorStatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsureWriterLease();
        var snapshot = ReadValidatedAnchorSnapshot();
        if (!File.Exists(StatePath) && snapshot.Committed is null && snapshot.Prepared is not null)
        {
            AbortPreparedOrThrow(snapshot.Prepared);
            EnsureExactAnchorSnapshot(committed: null, prepared: null);
            snapshot = ReadValidatedAnchorSnapshot();
        }
        if (File.Exists(StatePath))
            throw new InvalidOperationException("durable Supervisor state already exists; bootstrap cannot overwrite it");
        if (snapshot.Committed is not null || snapshot.Prepared is not null)
            throw new InvalidOperationException("external Supervisor state anchor already exists; bootstrap cannot replace it");
        var write = PrepareWrite(payload, generation: 1, previousStateSha256: new string('0', 64));
        var next = new SupervisorStateAnchor(write.Stored.Generation, write.Stored.StateSha256);
        var preparation = _anchor.TryPrepare(expectedCommitted: null, next) ??
            throw new InvalidOperationException("external Supervisor state anchor rejected bootstrap preparation");
        ValidateReturnedPreparation(preparation, next);
        EnsureExactAnchorSnapshot(committed: null, preparation);
        try
        {
            WritePrepared(write.Bytes, replace: false);
        }
        catch
        {
            TryAbortBestEffort(preparation);
            throw;
        }
        CommitPreparedOrThrow(preparation);
        EnsureExactAnchorSnapshot(next, prepared: null);
        return write.Stored;
    }

    internal StoredSupervisorState Advance(
        SupervisorStatePayload payload,
        long expectedGeneration,
        string expectedStateSha256)
    {
        lock (_operationSync)
            return AdvanceCore(payload, expectedGeneration, expectedStateSha256);
    }

    private StoredSupervisorState AdvanceCore(
        SupervisorStatePayload payload,
        long expectedGeneration,
        string expectedStateSha256)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsureWriterLease();
        var current = LoadRequiredCore();
        if (current.Generation != expectedGeneration || current.StateSha256 != expectedStateSha256)
            throw new InvalidOperationException("durable Supervisor state changed concurrently or was rolled back");
        var write = PrepareWrite(payload, checked(expectedGeneration + 1), expectedStateSha256);
        var expected = new SupervisorStateAnchor(expectedGeneration, expectedStateSha256);
        var next = new SupervisorStateAnchor(write.Stored.Generation, write.Stored.StateSha256);
        var preparation = _anchor.TryPrepare(expected, next) ??
            throw new InvalidOperationException("external Supervisor state anchor rejected transition preparation");
        ValidateReturnedPreparation(preparation, next);
        EnsureExactAnchorSnapshot(expected, preparation);
        try
        {
            WritePrepared(write.Bytes, replace: true);
        }
        catch
        {
            TryAbortBestEffort(preparation);
            throw;
        }
        CommitPreparedOrThrow(preparation);
        EnsureExactAnchorSnapshot(next, prepared: null);
        return write.Stored;
    }

    public void Dispose()
    {
        lock (_operationSync)
            Interlocked.Exchange(ref _writerLease, null)?.Dispose();
    }

    private PreparedStateWrite PrepareWrite(
        SupervisorStatePayload payload,
        long generation,
        string previousStateSha256)
    {
        ValidateSha256(previousStateSha256, "previous_state_sha256");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, StrictJson);
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
        var stateSha256 = ComputeStateSha256(generation, previousStateSha256, payloadSha256);
        var envelope = new SupervisorStateEnvelope
        {
            SchemaVersion = "dps.windows-edge-supervisor-state/v1",
            Generation = generation,
            PreviousStateSha256 = previousStateSha256,
            PayloadSha256 = payloadSha256,
            StateSha256 = stateSha256,
            Payload = payload
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, StrictJson);
        if (bytes.Length > MaximumStateBytes)
            throw new InvalidOperationException("durable Supervisor state exceeds the maximum size");
        return new PreparedStateWrite(
            new StoredSupervisorState(generation, previousStateSha256, stateSha256, payload),
            bytes);
    }

    private void WritePrepared(byte[] bytes, bool replace)
    {
        var directory = Path.GetDirectoryName(StatePath) ??
            throw new InvalidOperationException("Supervisor state path has no parent directory");
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        if (File.Exists(StatePath)) RejectLink(StatePath);
        var temporary = StatePath + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!replace && File.Exists(StatePath))
                throw new InvalidOperationException("durable Supervisor state appeared during bootstrap");
            File.Move(temporary, StatePath, overwrite: replace);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private SupervisorStateAnchorSnapshot ReadValidatedAnchorSnapshot()
    {
        var snapshot = _anchor.ReadSnapshot() ??
            throw new InvalidDataException("external Supervisor state anchor returned no snapshot");
        if (snapshot.Committed is not null)
            ValidateAnchor(snapshot.Committed, "committed");
        if (snapshot.Prepared is not null)
        {
            ValidatePreparation(snapshot.Prepared);
            var expectedGeneration = snapshot.Committed is null
                ? 1
                : checked(snapshot.Committed.Generation + 1);
            if (snapshot.Prepared.Next.Generation != expectedGeneration)
                throw new InvalidDataException(
                    "prepared external Supervisor state anchor is not the next monotonic generation");
        }
        return snapshot;
    }

    private static void ValidateAnchor(SupervisorStateAnchor anchor, string name)
    {
        if (anchor.Generation < 1)
            throw new InvalidDataException(name + " external Supervisor state anchor generation is invalid");
        ValidateSha256(anchor.StateSha256, name + "_state_sha256");
    }

    private static void ValidatePreparation(SupervisorStatePreparation preparation)
    {
        if (preparation.Token is null || preparation.Next is null ||
            preparation.Token.Length != 69 ||
            !preparation.Token.StartsWith("prep_", StringComparison.Ordinal) ||
            preparation.Token.Skip(5).Any(
                character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new InvalidDataException(
                "external Supervisor state preparation token is not canonical");
        ValidateAnchor(preparation.Next, "prepared");
    }

    private static void ValidateReturnedPreparation(
        SupervisorStatePreparation preparation,
        SupervisorStateAnchor requestedNext)
    {
        ValidatePreparation(preparation);
        if (preparation.Next != requestedNext)
            throw new InvalidDataException(
                "external Supervisor state anchor prepared a head other than the exact requested next state");
    }

    private void CommitPreparedOrThrow(SupervisorStatePreparation prepared)
    {
        if (!_anchor.TryCommit(prepared))
            throw new InvalidOperationException("external Supervisor state anchor rejected prepared-state commit");
    }

    private void AbortPreparedOrThrow(SupervisorStatePreparation prepared)
    {
        if (!_anchor.TryAbort(prepared))
            throw new InvalidOperationException("external Supervisor state anchor rejected prepared-state abort");
    }

    private void TryAbortBestEffort(SupervisorStatePreparation prepared)
    {
        try
        {
            _ = _anchor.TryAbort(prepared);
        }
        catch
        {
            // Recovery will reconcile the retained prepared head on next Resume.
        }
    }

    private void EnsureExactAnchorSnapshot(
        SupervisorStateAnchor? committed,
        SupervisorStatePreparation? prepared)
    {
        var actual = ReadValidatedAnchorSnapshot();
        if (actual.Committed != committed || actual.Prepared != prepared)
            throw new InvalidDataException(
                "external Supervisor state anchor did not reach the required committed/prepared state");
    }

    private static StoredSupervisorState Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumStateBytes)
            throw new InvalidDataException("durable Supervisor state size is outside the contract range");
        SupervisorStateEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SupervisorStateEnvelope>(bytes, StrictJson) ??
                throw new InvalidDataException("durable Supervisor state is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("durable Supervisor state JSON is invalid", exception);
        }
        if (envelope.SchemaVersion != "dps.windows-edge-supervisor-state/v1" || envelope.Generation < 1)
            throw new InvalidDataException("unknown durable Supervisor state version or generation");
        if (envelope.Payload is null)
            throw new InvalidDataException("durable Supervisor state payload is missing");
        ValidateSha256(envelope.PreviousStateSha256, "previous_state_sha256");
        ValidateSha256(envelope.PayloadSha256, "payload_sha256");
        ValidateSha256(envelope.StateSha256, "state_sha256");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, StrictJson);
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
        var stateSha256 = ComputeStateSha256(
            envelope.Generation,
            envelope.PreviousStateSha256,
            envelope.PayloadSha256);
        if (payloadSha256 != envelope.PayloadSha256 || stateSha256 != envelope.StateSha256)
            throw new InvalidDataException("durable Supervisor state checksum is invalid");
        return new StoredSupervisorState(
            envelope.Generation,
            envelope.PreviousStateSha256,
            envelope.StateSha256,
            envelope.Payload);
    }

    private static string ComputeStateSha256(long generation, string previous, string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(string.Join(
            "\n",
            "dps.windows-edge-supervisor-state-checksum/v1",
            generation.ToString(CultureInfo.InvariantCulture),
            previous,
            payload))));

    private static void RejectLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("durable Supervisor state path cannot be a link or reparse point");
    }

    private static void ValidateSha256(string? value, string field)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new InvalidDataException(field + " is not canonical SHA-256");
    }

    private void EnsureWriterLease()
    {
        if (_writerLease is null)
            throw new ObjectDisposedException(nameof(DurableSupervisorStateStore));
    }

    private sealed class SupervisorStateEnvelope
    {
        [JsonPropertyName("schema_version"), JsonRequired] public required string SchemaVersion { get; init; }
        [JsonPropertyName("generation"), JsonRequired] public long Generation { get; init; }
        [JsonPropertyName("previous_state_sha256"), JsonRequired] public required string PreviousStateSha256 { get; init; }
        [JsonPropertyName("payload_sha256"), JsonRequired] public required string PayloadSha256 { get; init; }
        [JsonPropertyName("state_sha256"), JsonRequired] public required string StateSha256 { get; init; }
        [JsonPropertyName("payload"), JsonRequired] public required SupervisorStatePayload Payload { get; init; }
    }

    private sealed record PreparedStateWrite(StoredSupervisorState Stored, byte[] Bytes);
}

public sealed record SupervisorStateAnchor(long Generation, string StateSha256);

public sealed record SupervisorStatePreparation(
    string Token,
    SupervisorStateAnchor Next);

public sealed record SupervisorStateAnchorSnapshot(
    SupervisorStateAnchor? Committed,
    SupervisorStatePreparation? Prepared);

/// <summary>
/// Externally protected monotonic routing-state head. Production composition
/// must back this interface with a Windows service/ACL, TPM, or equivalent
/// authority outside the mutable state file. Every method must be linearizable
/// and crash-durable. TryPrepare must atomically compare the committed head,
/// allocate an unpredictable one-use token, and durably publish the prepared
/// head before returning it. TryCommit/TryAbort must compare that exact token
/// and head, durably apply one terminal transition, and reject stale ABA calls.
/// One authority instance is bound to one host and one state-path identity. The
/// Supervisor provides no insecure file-based fallback.
/// </summary>
public interface ISupervisorStateAnchor
{
    SupervisorStateAnchorSnapshot ReadSnapshot();

    SupervisorStatePreparation? TryPrepare(
        SupervisorStateAnchor? expectedCommitted,
        SupervisorStateAnchor next);

    bool TryCommit(SupervisorStatePreparation prepared);

    bool TryAbort(SupervisorStatePreparation prepared);
}

internal sealed record StoredSupervisorState(
    long Generation,
    string PreviousStateSha256,
    string StateSha256,
    SupervisorStatePayload Payload);

internal sealed record SupervisorStatePayload(
    [property: JsonPropertyName("host_id")] string HostId,
    [property: JsonPropertyName("release_bom_sha256")] string ReleaseBomSha256,
    [property: JsonPropertyName("protected_policy_sha256")] string ProtectedPolicySha256,
    [property: JsonPropertyName("bridge_server_key_id")] string BridgeServerKeyId,
    [property: JsonPropertyName("journal_artifact_sha256")] string JournalArtifactSha256,
    [property: JsonPropertyName("artifact_trust_fingerprint")] string ArtifactTrustFingerprint,
    [property: JsonPropertyName("capability_trust_fingerprint")] string CapabilityTrustFingerprint,
    [property: JsonPropertyName("drain_directive_trust_fingerprint")] string DrainDirectiveTrustFingerprint,
    [property: JsonPropertyName("drain_directive_signing_key_id")] string DrainDirectiveSigningKeyId,
    [property: JsonPropertyName("worker_drain_trust_fingerprint")] string WorkerDrainTrustFingerprint,
    [property: JsonPropertyName("journal_drain_trust_fingerprint")] string JournalDrainTrustFingerprint,
    [property: JsonPropertyName("active_slot")] WorkerSlot ActiveSlot,
    [property: JsonPropertyName("previous_slot")] WorkerSlot? PreviousSlot,
    [property: JsonPropertyName("routing_epoch")] long RoutingEpoch,
    [property: JsonPropertyName("active_drain")] DrainExpectation? ActiveDrain,
    [property: JsonPropertyName("prepared_drain_directive")] PersistedPreparedDrainDirective? PreparedDrainDirective,
    [property: JsonPropertyName("last_worker_drain_receipt_wire_sha256")] string? LastWorkerDrainReceiptWireSha256,
    [property: JsonPropertyName("last_journal_drain_attestation_wire_sha256")] string? LastJournalDrainAttestationWireSha256,
    [property: JsonPropertyName("slots")] PersistedSlotState[] Slots,
    [property: JsonPropertyName("binding_routes")] PersistedBindingRoute[] BindingRoutes);

internal sealed record PersistedPreparedDrainDirective(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("drain_id")] string DrainId,
    [property: JsonPropertyName("wire_base64")] string WireBase64,
    [property: JsonPropertyName("wire_sha256")] string WireSha256);

internal sealed record PersistedSlotState(
    [property: JsonPropertyName("artifact")] WorkerArtifact Artifact,
    [property: JsonPropertyName("accepting")] bool Accepting,
    [property: JsonPropertyName("validated")] bool Validated,
    [property: JsonPropertyName("previously_stable")] bool PreviouslyStable,
    [property: JsonPropertyName("in_flight")] int InFlight,
    [property: JsonPropertyName("capability_evidence_sha256")] string? CapabilityEvidenceSha256);

internal sealed record PersistedBindingRoute(
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("slot")] WorkerSlot Slot);
