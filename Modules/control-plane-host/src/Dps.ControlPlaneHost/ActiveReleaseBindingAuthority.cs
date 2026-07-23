using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.ControlPlaneHost.Contracts;

namespace Dps.ControlPlaneHost;

/// <summary>
/// Raised whenever active release binding material fails a fail-closed check.
/// No state mutation ever precedes this exception. Derivable only inside
/// this module for the typed conflict cases
/// (<see cref="ReleaseBindingTruthConflictException"/>,
/// <see cref="ReleaseBindingRecoveryFenceConflictException"/>).
/// </summary>
public class ActiveReleaseBindingException : Exception
{
    public ActiveReleaseBindingException(string message) : base(message) { }
    public ActiveReleaseBindingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Wire facts about the signed Release BOM this authority activates.
/// RequiredTopLevelFields is dual-pinned with
/// Tools/ci/candidate_bom_validator.py::_BOM_FIELDS (a python test in
/// Tests/ci extracts both literals and asserts set equality); change either
/// side only together with the other.
/// </summary>
public static class ReleaseBomWireContract
{
    public const string SchemaVersion = "dps.release-bom/v1";
    // Matches candidate_bom_validator._validate_exact_shape(expected_status="SIGNED"):
    // the runtime activates exactly the signed candidate wire status.
    public const string ExpectedStatus = "SIGNED";
    public const string PreviousStableStatus = "STABLE";
    public const int ExecutionTokenSizeBytes = 32;

    public static readonly IReadOnlySet<string> RequiredTopLevelFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "schema_version",
        "bom_id",
        "status",
        "integration_commit",
        "created_at",
        "release_bom_generation",
        "activation_token_sha256",
        "modules",
        "instruction_hashes",
        "contracts",
        "database_versions",
        "dependency_dag_sha256",
        "compatibility_matrix_sha256",
        "feature_flags",
        "kill_switches",
        "ai_toolchain",
        "evidence",
        "risk",
        "release_approval",
        "rollout",
        "rollback",
        "previous_stable_bom",
        "previous_stable_bom_sha256",
        "native_stop_authorities",
        "device_route_assignment_authorities",
        "native_stop_challenge_authorities",
        "signature"
    };
}

/// <summary>
/// One trusted Release BOM signing key parsed from the deployed release trust
/// policy: purpose "bom", algorithm "rsa-pss-sha256" only.
/// </summary>
public sealed record ReleaseBomTrustKey(
    string KeyId,
    string Identity,
    string ModulusHex,
    int Exponent)
{
    public const string RequiredAlgorithm = "rsa-pss-sha256";
    private static readonly IReadOnlyList<string> BomOnlyPurposes =
        Array.AsReadOnly(new[] { "bom" });

    /// <summary>
    /// The four-argument constructor is the safe BOM-only convenience shape.
    /// Callers that materialize the complete policy tuple use the overload
    /// below; both shapes are revalidated by the authority before trust.
    /// </summary>
    public string Algorithm { get; } = RequiredAlgorithm;
    public IReadOnlyList<string> Purposes { get; } = BomOnlyPurposes;

    public ReleaseBomTrustKey(
        string keyId,
        string identity,
        string modulusHex,
        int exponent,
        string algorithm,
        IReadOnlyList<string> purposes)
        : this(keyId, identity, modulusHex, exponent)
    {
        Algorithm = algorithm;
        Purposes = purposes;
    }

    /// <summary>
    /// Enforces the deployed BOM verifier key profile and snapshots the
    /// caller-owned purposes collection. This is deliberately shared by the
    /// JSON policy parser and the authority's public constructor, so directly
    /// constructing this record cannot bypass release-policy restrictions.
    /// </summary>
    internal static ReleaseBomTrustKey ValidateAndSnapshot(
        ReleaseBomTrustKey? key)
    {
        if (key is null)
        {
            throw new ActiveReleaseBindingException("bom trust key is null");
        }
        if (string.IsNullOrEmpty(key.KeyId))
        {
            throw new ActiveReleaseBindingException("bom key_id is missing");
        }
        if (string.IsNullOrEmpty(key.Identity))
        {
            throw new ActiveReleaseBindingException("bom key identity is missing");
        }
        if (key.Purposes is null
            || key.Purposes.Count != 1
            || !string.Equals(key.Purposes[0], "bom", StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "bom key purposes must be the exact singleton array ['bom']");
        }
        if (!string.Equals(
                key.Algorithm,
                RequiredAlgorithm,
                StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "bom key algorithm must be rsa-pss-sha256");
        }
        if (string.IsNullOrEmpty(key.ModulusHex)
            || key.ModulusHex[0] == '0'
            || key.ModulusHex.Any(static character =>
                character is not (>= '0' and <= '9')
                && character is not (>= 'a' and <= 'f')))
        {
            throw new ActiveReleaseBindingException(
                "bom key modulus must be canonical lowercase unsigned hexadecimal");
        }
        var unsignedModulusOctets = checked((key.ModulusHex.Length + 1) / 2);
        if (unsignedModulusOctets < 256)
        {
            throw new ActiveReleaseBindingException(
                "bom key modulus must occupy at least 256 unsigned octets");
        }
        if (key.Exponent != 65537)
        {
            throw new ActiveReleaseBindingException(
                "bom key exponent must be 65537");
        }
        return new ReleaseBomTrustKey(
            key.KeyId,
            key.Identity,
            key.ModulusHex,
            key.Exponent,
            key.Algorithm,
            BomOnlyPurposes);
    }

    /// <summary>
    /// Parses the deployed release trust policy document (the JSON shape of
    /// governance/policies/deployed-release-trust-policy.v1.json) and keeps
    /// only keys whose purposes include "bom" with algorithm
    /// "rsa-pss-sha256". Fails closed when no such key exists.
    /// </summary>
    public static IReadOnlyList<ReleaseBomTrustKey> FromTrustPolicy(JsonElement policy)
    {
        if (policy.ValueKind != JsonValueKind.Object
            || !policy.TryGetProperty("keys", out var keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            throw new ActiveReleaseBindingException("release trust policy has no keys array");
        }
        var parsed = new List<ReleaseBomTrustKey>();
        foreach (var key in keys.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object
                || !key.TryGetProperty("purposes", out var purposes)
                || purposes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var purposeValues = purposes.EnumerateArray().ToArray();
            if (!purposeValues.Any(static value =>
                    value.ValueKind == JsonValueKind.String
                    && value.GetString() == "bom"))
            {
                continue;
            }
            if (purposeValues.Length != 1
                || purposeValues[0].ValueKind != JsonValueKind.String
                || purposeValues[0].GetString() != "bom")
            {
                throw new ActiveReleaseBindingException(
                    "bom key purposes must be the exact singleton array ['bom']");
            }
            var modulusHex = key.GetProperty("modulus_hex").GetString()
                ?? throw new ActiveReleaseBindingException("bom key modulus is missing");
            var exponent = key.GetProperty("exponent").GetInt32();
            parsed.Add(ValidateAndSnapshot(new ReleaseBomTrustKey(
                key.GetProperty("key_id").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key_id is missing"),
                key.GetProperty("identity").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key identity is missing"),
                modulusHex,
                exponent,
                key.GetProperty("algorithm").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key algorithm is missing"),
                purposeValues.Select(static value =>
                        value.ValueKind == JsonValueKind.String
                            ? value.GetString()!
                            : string.Empty)
                    .ToArray())));
        }
        if (parsed.Count == 0)
        {
            throw new ActiveReleaseBindingException("release trust policy pins no bom signing key");
        }
        return parsed;
    }
}

/// <summary>
/// One appended truth record: the receipt of a transition plus the full
/// post-transition device snapshot needed to recover the authority.
/// SignedBomBytes carries the exact canonical signed Release BOM the
/// transition activated (activation and rollback); it is null only for
/// revocation. Recovery re-runs the full signature and token binding over
/// these bytes, so a journal cannot smuggle a binding that no signed BOM
/// backs. PreviousStableBomBytes carries the exact externally signed STABLE
/// lifecycle twin referenced by a non-bootstrap activation. It is null for
/// bootstrap activation, revocation, and rollback.
/// </summary>
public sealed record ReleaseBindingTruthRecord(
    string DeviceBindingId,
    ReleaseBindingReceiptV1 Receipt,
    ActiveReleaseBindingV1 CurrentBinding,
    ActiveReleaseBindingV1? PreviousBinding,
    long LastActivationSignerGeneration,
    string RequestSha256,
    byte[]? SignedBomBytes,
    byte[]? PreviousStableBomBytes);

/// <summary>
/// One atomic per-device journal snapshot. HeadSequence and Records are read
/// from the same store snapshot, so an authority never combines a head from
/// one instant with a delta from another.
/// </summary>
public sealed record ReleaseBindingJournalSnapshot(
    long HeadSequence,
    IReadOnlyList<ReleaseBindingTruthRecord> Records);

/// <summary>
/// One per-device transition linearization scope: the durable serialization
/// primitive for a single activation, revocation, or rollback, held across
/// the authority's in-process gate so the durable lock is always acquired
/// BEFORE the gate (the global lock order documented on
/// <see cref="IReleaseBindingTruthStore.BeginTransition"/>). The authority
/// disposes the scope after the transition publishes. An unused scope persists
/// nothing. An exceptional append is an ambiguous acknowledgement outcome: it
/// may have committed before the caller observed the exception, so the
/// authority publishes nothing locally and an exact retry must first
/// synchronize the durable head.
/// </summary>
public interface IReleaseBindingTransitionScope : IDisposable
{
    /// <summary>
    /// Appends the transition's journal record inside the scope's
    /// linearization window. On a successful return the record is durable
    /// (for the durable store, committed) and the per-device serialization
    /// may be released. An exception does not prove absence: the durable store
    /// may have committed before its acknowledgement was lost. Callers must
    /// leave local state unpublished and recover the exact durable result.
    /// </summary>
    void Append(ReleaseBindingTruthRecord record);
}

/// <summary>
/// Default <see cref="IReleaseBindingTransitionScope"/> for stores whose
/// <see cref="IReleaseBindingTruthStore.Append"/> already IS the full
/// linearization point (the single-process in-memory compare-and-set store
/// and test doubles): no durable lock exists to hoist ahead of the
/// authority's gate, so the scope simply forwards the append.
/// </summary>
internal sealed class PassThroughReleaseBindingTransitionScope : IReleaseBindingTransitionScope
{
    private readonly IReleaseBindingTruthStore _store;

    internal PassThroughReleaseBindingTransitionScope(IReleaseBindingTruthStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public void Append(ReleaseBindingTruthRecord record) => _store.Append(record);

    public void Dispose()
    {
    }
}

/// <summary>
/// Module-private read seam used by control-plane-host's own lifecycle
/// tests. Cross-module production composition cannot name or implement it;
/// public consumers receive only the sealed authority-issued capability.
/// </summary>
internal interface IActiveReleaseBindingReader
{
    bool TryReadActive(
        string deviceBindingId,
        out ActiveReleaseBindingV1? binding);
}

/// <summary>
/// Module-private store coordination port. Implementations hold the exact
/// per-device transition primitive used by activation, revocation, and
/// rollback; it is intentionally absent from the public contract surface.
/// </summary>
internal interface IActiveReleaseBindingRecoveryCoordinator
{
    ValueTask<IActiveReleaseBindingRecoveryScope> AcquireAsync(
        string deviceBindingId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Module-private held store scope. Only
/// <see cref="ActiveReleaseBindingAuthority"/> may wrap one in the public
/// nominal recovery lease issued by its capability.
/// </summary>
internal interface IActiveReleaseBindingRecoveryScope : IAsyncDisposable
{
    ActiveReleaseBindingV1 ActiveBinding { get; }
}

/// <summary>
/// Append-only truth store for release binding transitions. The authority
/// appends one record per successful transition and loads the full journal
/// at construction to recover state. The durable implementation is
/// <see cref="PostgresReleaseBindingTruthStore"/> (per-device compare-and-set
/// journal); <see cref="InMemoryReleaseBindingTruthStore"/> is test-only and
/// cannot be constructed without the explicit test-only factory.
///
/// The two per-device freshness reads exist so an authority instance can
/// prove its in-memory view of one device is not behind the durable journal
/// head before serving an authoritative read (multi-instance resync): they
/// are read-only SELECTs and never weaken the append-only compare-and-set
/// guarantees of <see cref="Append"/>.
/// </summary>
public interface IReleaseBindingTruthStore
{
    void Append(ReleaseBindingTruthRecord record);
    IReadOnlyList<ReleaseBindingTruthRecord> LoadAll();

    /// <summary>
    /// The durable journal head sequence for one device: the highest
    /// appended per-device sequence, or 0 when the device has no journal
    /// records. Because the journal is append-only, a head lower than a
    /// previously observed sequence means the store is forked.
    /// </summary>
    long LoadDeviceHeadSequence(string deviceBindingId);

    /// <summary>
    /// The device's journal records strictly after
    /// <paramref name="afterSequence"/> in ascending sequence order (empty
    /// when the device is at or behind that sequence). Each record is the
    /// exact stored row, re-validated by the caller through the same
    /// pipeline as a full recovery replay.
    /// </summary>
    IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(string deviceBindingId, long afterSequence);

    /// <summary>
    /// Atomically reads the device head and all records after the caller's
    /// cached sequence. Durable implementations must use one database
    /// snapshot; composing separate head and delta reads is forbidden because
    /// a concurrent append could otherwise create a stale-serving TOCTOU.
    /// </summary>
    ReleaseBindingJournalSnapshot LoadSnapshotAfter(
        string deviceBindingId,
        long afterSequence);

    /// <summary>
    /// Opens the per-device transition linearization scope for one
    /// activation, revocation, or rollback. GLOBAL LOCK ORDER (deadlock
    /// freedom): every path that touches both the durable per-device lock
    /// and the authority's in-process gate acquires the durable lock FIRST,
    /// so the authority opens this scope before entering its gate and the
    /// scoped <see cref="IReleaseBindingTransitionScope.Append"/> is the only
    /// append a transition performs. A policy-approval recovery obtains the
    /// store-issued coordination scope backed by this same per-device
    /// primitive and holds it through the policy commit; with this order the
    /// two paths can never close a hold-and-wait cycle. The default implementation is
    /// a pass-through: correct only for stores whose <see cref="Append"/>
    /// already is the full linearization point (the single-process in-memory
    /// compare-and-set store, test doubles). A durable multi-process store
    /// MUST override with a scope that holds the real per-device
    /// serialization primitive from before the gate until the append commits
    /// (<see cref="PostgresReleaseBindingTruthStore"/> holds the per-device
    /// pg_advisory_xact_lock on the scope's own transaction, which the SQL
    /// append function then takes re-entrantly on the same session).
    /// </summary>
    IReleaseBindingTransitionScope BeginTransition(string deviceBindingId)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        return new PassThroughReleaseBindingTransitionScope(this);
    }
}

/// <summary>
/// Deterministic in-memory truth store for tests and restart-recovery tests.
/// Not durable: process death loses the journal. TEST-ONLY: the constructor
/// is private and the single factory is named CreateTestOnly so a production
/// composition can never silently take the in-memory implementation — any
/// call site names its test-only nature explicitly.
/// </summary>
public sealed class InMemoryReleaseBindingTruthStore
    : IReleaseBindingTruthStore,
      IReleaseBindingRecoveryFenceAuthority,
      IActiveReleaseBindingRecoveryCoordinator
{
    private readonly Lock _gate = new();
    private readonly List<ReleaseBindingTruthRecord> _records = [];
    private readonly Dictionary<string, SemaphoreSlim> _transitionLocks = [];
    private readonly Dictionary<Guid, (ReleaseBindingRecoveryFence Fence, string ContentSha256)>
        _fences = [];

    private InMemoryReleaseBindingTruthStore()
    {
    }

    /// <summary>
    /// The only way to obtain the non-durable in-memory store. Production
    /// composition must use <see cref="PostgresReleaseBindingTruthStore"/>.
    /// M4 boundary (RebuildPlan §4.3, PR#6 v2): this store is test-only and
    /// the production composition root is deferred to milestone M4; until
    /// then non-test environments have no composition entrypoint at all —
    /// HostStartup accepts only --self-check and fails closed with exit 64
    /// for every other invocation (pinned by HostStartupEntrypointTests).
    /// </summary>
    public static InMemoryReleaseBindingTruthStore CreateTestOnly() => new();

    public void Append(ReleaseBindingTruthRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var receipt = record.Receipt
            ?? throw new ActiveReleaseBindingException("truth store append requires a receipt");
        lock (_gate)
        {
            // Compare-and-swap guard mirroring the durable PostgreSQL CAS
            // journal: the journal accepts only the exactly-next sequence
            // per device, so two authority instances sharing one store
            // cannot both land the same generation — the loser faults
            // instead of silently forking the journal.
            if (receipt.Sequence != LastSequenceLocked(record.DeviceBindingId) + 1)
            {
                throw new ReleaseBindingTruthConflictException(
                    "truth store append sequence conflict: expected the exactly-next per-device sequence");
            }
            _records.Add(record);
        }
    }

    public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll()
    {
        lock (_gate)
        {
            return [.. _records];
        }
    }

    public long LoadDeviceHeadSequence(string deviceBindingId)
    {
        lock (_gate)
        {
            return LastSequenceLocked(deviceBindingId);
        }
    }

    public IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(
        string deviceBindingId,
        long afterSequence)
    {
        lock (_gate)
        {
            // Appends land under the compare-and-set guard above, so the
            // per-device subsequence is already ascending and contiguous.
            var delta = new List<ReleaseBindingTruthRecord>();
            foreach (var existing in _records)
            {
                if (string.Equals(existing.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
                    && existing.Receipt.Sequence > afterSequence)
                {
                    delta.Add(existing);
                }
            }
            return delta;
        }
    }

    public ReleaseBindingJournalSnapshot LoadSnapshotAfter(
        string deviceBindingId,
        long afterSequence)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        lock (_gate)
        {
            var head = LastSequenceLocked(deviceBindingId);
            var delta = _records
                .Where(record => string.Equals(
                        record.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
                    && record.Receipt.Sequence > afterSequence)
                .OrderBy(record => record.Receipt.Sequence)
                .ToArray();
            return new ReleaseBindingJournalSnapshot(head, delta);
        }
    }

    public IReleaseBindingTransitionScope BeginTransition(string deviceBindingId)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        var transitionLock = GetTransitionLock(deviceBindingId);
        transitionLock.Wait();
        return new InMemoryTransitionScope(this, transitionLock);
    }

    async ValueTask<IActiveReleaseBindingRecoveryScope>
        IActiveReleaseBindingRecoveryCoordinator.AcquireAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        var transitionLock = GetTransitionLock(deviceBindingId);
        await transitionLock.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                var head = HeadLocked(deviceBindingId);
                if (head?.CurrentBinding is not { Status: "active" } active)
                {
                    throw new ActiveReleaseBindingException(
                        "recovery coordination requires an active release binding");
                }
                return new InMemoryRecoveryScope(active, transitionLock);
            }
        }
        catch
        {
            transitionLock.Release();
            throw;
        }
    }

    private SemaphoreSlim GetTransitionLock(string deviceBindingId)
    {
        lock (_gate)
        {
            if (!_transitionLocks.TryGetValue(deviceBindingId, out var transitionLock))
            {
                transitionLock = new SemaphoreSlim(1, 1);
                _transitionLocks.Add(deviceBindingId, transitionLock);
            }
            return transitionLock;
        }
    }

    private sealed class InMemoryTransitionScope(
        InMemoryReleaseBindingTruthStore store,
        SemaphoreSlim transitionLock) : IReleaseBindingTransitionScope
    {
        private bool _disposed;

        public void Append(ReleaseBindingTruthRecord record)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InMemoryTransitionScope));
            }
            store.Append(record);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            transitionLock.Release();
        }
    }

    private sealed class InMemoryRecoveryScope(
        ActiveReleaseBindingV1 activeBinding,
        SemaphoreSlim transitionLock) : IActiveReleaseBindingRecoveryScope
    {
        private int _disposed;
        public ActiveReleaseBindingV1 ActiveBinding { get; } = activeBinding;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                transitionLock.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    public ReleaseBindingRecoveryFence IssueRecoveryFence(string deviceBindingId)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        lock (_gate)
        {
            var head = HeadLocked(deviceBindingId);
            if (head is null || head.CurrentBinding is not { Status: "active" } active)
            {
                throw new ActiveReleaseBindingException(
                    "recovery fence issuance requires an active release binding");
            }
            return new ReleaseBindingRecoveryFence(
                deviceBindingId,
                head.Receipt.Sequence,
                active.ReleaseBomSha256,
                active.Generation);
        }
    }

    public void CommitRecoveryFence(
        ReleaseBindingRecoveryFence fence,
        Guid recoveryId,
        string recoveryContentSha256)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (recoveryId == Guid.Empty || string.IsNullOrEmpty(recoveryContentSha256))
        {
            throw new ActiveReleaseBindingException(
                "recovery fence commit requires a recovery id and content digest");
        }
        lock (_gate)
        {
            if (_fences.TryGetValue(recoveryId, out var existing))
            {
                // Idempotent redelivery of the exact same recovery content
                // for the exact same fenced journal position; anything else
                // on the same recovery id fails closed.
                if (existing.Fence == fence
                    && string.Equals(existing.ContentSha256, recoveryContentSha256, StringComparison.Ordinal))
                {
                    return;
                }
                throw new ReleaseBindingRecoveryFenceConflictException(
                    "recovery fence commit conflict: the recovery id was already fenced differently");
            }
            var head = HeadLocked(fence.DeviceBindingId);
            if (head is null
                || head.Receipt.Sequence != fence.JournalSequence
                || head.CurrentBinding is not { Status: "active" } active
                || !string.Equals(active.ReleaseBomSha256, fence.ReleaseBomSha256, StringComparison.Ordinal)
                || active.Generation != fence.Generation)
            {
                throw new ReleaseBindingRecoveryFenceConflictException(
                    "recovery fence commit conflict: the release binding revision advanced past the issued fence");
            }
            _fences[recoveryId] = (fence, recoveryContentSha256);
        }
    }

    private long LastSequenceLocked(string deviceBindingId)
        => HeadLocked(deviceBindingId)?.Receipt.Sequence ?? 0;

    private ReleaseBindingTruthRecord? HeadLocked(string deviceBindingId)
    {
        ReleaseBindingTruthRecord? head = null;
        foreach (var existing in _records)
        {
            if (string.Equals(existing.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
            {
                head = existing;
            }
        }
        return head;
    }
}

/// <summary>
/// Deterministic in-process authority for the per-device active Release BOM
/// binding (active.release.binding/v1).
///
/// Activation performs the ACTIVATION-SAFETY SUBSET of signed Release BOM
/// validation — the full candidate validation (module artifacts, evidence,
/// approvals, authority rotation, lineage against git) is the job of the
/// Tools/ci candidate gate and is intentionally NOT repeated here. The
/// subset enforced on every Activate is exactly:
///   1. strict JSON parse; top-level field set identical to
///      candidate_bom_validator._BOM_FIELDS (missing or extra fields reject);
///   2. schema_version == "dps.release-bom/v1" and status == "SIGNED"
///      (candidate_bom_validator._validate_exact_shape, line 1581);
///   3. release_bom_generation positive int64 and activation_token_sha256
///      64-hex (candidate_bom_validator lines 1199-1208);
///   4. RSA-PSS-SHA256 signature over
///      b"dps-release-bom/v1\n" + canonical_bytes(BOM without signature)
///      against the injected purpose="bom" trust keys;
///   5. the caller-presented execution token is canonical Base64 for exactly
///      32 bytes and sha256(token) == BOM.activation_token_sha256 — the
///      token is pre-committed by the out-of-repo signer, never minted here;
///   6. signer-ordinal anti-rollback: release_bom_generation strictly
///      greater than the last activation's for this device (Rollback may
///      revert the ordinal, Activate never may);
///   7. previous stable chain: on non-bootstrap activation the caller supplies
///      the exact externally signed STABLE lifecycle twin of the current
///      activated SIGNED BOM. Its exact-wire digest/id must equal the new
///      candidate's previous_stable_bom_sha256/id, and every signed field
///      other than status/signature must equal the current SIGNED wire.
///      Bootstrap requires both previous fields and previous bytes absent.
///
/// State is recovered from the injected truth store at construction: the
/// journal is replayed with sequence/generation/receipt-identity
/// verification and any fork or regression refuses service. Authoritative
/// reads are revision-aware: before TryReadActive or ReadReceipts serves a
/// device's cached view, the view is proven fresh against the durable
/// per-device journal head and any delta records are replayed through the
/// same recovery validation pipeline. An unreachable store, a journal head
/// behind the cached view, or a delta record that fails validation fails
/// the read closed — TryReadActive returns false, ReadReceipts throws, and
/// no stale or superseded binding is ever served. All public members share
/// one lock. A byte-identical re-submission returns its original receipt only
/// while that receipt's postcondition remains current; once superseded, the
/// consumed request fails closed and can never execute as a fresh transition.
/// Conflicting re-submissions also fail closed.
/// </summary>
public sealed class ActiveReleaseBindingAuthority
    : IActiveReleaseBindingRecoveryCapabilityIssuer,
      IActiveReleaseBindingReader
{
    private const string SchemaVersion = "1.0.0";
    private static readonly byte[] SignatureDomain =
        Encoding.ASCII.GetBytes("dps-release-bom/v1\n");

    private sealed class DeviceState
    {
        public ActiveReleaseBindingV1? Current;
        public ActiveReleaseBindingV1? Previous;
        public byte[]? CurrentBomBytes;
        public byte[]? PreviousBomBytes;
        public long RuntimeGeneration;
        public long Sequence;
        public long LastActivationSignerGeneration;
        public readonly List<ReleaseBindingReceiptV1> Receipts = [];
        public readonly Dictionary<string, ReleaseBindingReceiptV1> RequestReceipts =
            new(StringComparer.Ordinal);
    }

    private readonly Lock _gate = new();
    private readonly IReadOnlyDictionary<string, ReleaseBomTrustKey> _keys;
    private readonly IReleaseBindingTruthStore _store;
    private readonly IActiveReleaseBindingRecoveryCoordinator _recoveryCoordinator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);

    public ActiveReleaseBindingAuthority(
        IReadOnlyList<ReleaseBomTrustKey> bomKeys,
        IReleaseBindingTruthStore store,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(bomKeys);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(utcNow);
        if (bomKeys.Count == 0)
        {
            throw new ActiveReleaseBindingException("at least one bom trust key is required");
        }
        var keys = new Dictionary<string, ReleaseBomTrustKey>(StringComparer.Ordinal);
        foreach (var key in bomKeys)
        {
            var trustedKey = ReleaseBomTrustKey.ValidateAndSnapshot(key);
            if (!keys.TryAdd(trustedKey.KeyId, trustedKey))
            {
                throw new ActiveReleaseBindingException("duplicate bom trust key id");
            }
        }
        _keys = keys;
        _store = store;
        _recoveryCoordinator = store as IActiveReleaseBindingRecoveryCoordinator
            ?? throw new ArgumentException(
                "The release binding truth store must provide the module-private recovery coordination primitive.",
                nameof(store));
        _utcNow = utcNow;
        RecoveryCapability = new ActiveReleaseBindingRecoveryCapability(
            this,
            store is PostgresReleaseBindingTruthStore);
        RecoverFromStore();
    }

    /// <summary>
    /// The sole nominal recovery capability for this authority and its exact
    /// injected truth store. Consumers cannot construct or substitute it.
    /// </summary>
    public ActiveReleaseBindingRecoveryCapability RecoveryCapability { get; }

    bool IActiveReleaseBindingRecoveryCapabilityIssuer.TryReadActive(
        string deviceBindingId,
        out ActiveReleaseBindingV1? binding)
        => TryReadActive(deviceBindingId, out binding);

    async ValueTask<ActiveReleaseBindingRecoveryLease>
        IActiveReleaseBindingRecoveryCapabilityIssuer.AcquireAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        var scope = await _recoveryCoordinator.AcquireAsync(
            deviceBindingId, cancellationToken);
        try
        {
            return new ActiveReleaseBindingRecoveryLease(
                scope.ActiveBinding,
                new RecoveryLeaseRelease(scope));
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class RecoveryLeaseRelease(
        IActiveReleaseBindingRecoveryScope scope)
        : IActiveReleaseBindingRecoveryLeaseRelease
    {
        public ValueTask ReleaseAsync() => scope.DisposeAsync();
    }

    public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding)
    {
        lock (_gate)
        {
            binding = null;
            if (deviceBindingId is null)
            {
                return false;
            }
            _devices.TryGetValue(deviceBindingId, out var state);
            if (!TrySynchronizeDevice(deviceBindingId, state)
                || !_devices.TryGetValue(deviceBindingId, out var fresh)
                || fresh.Current is not { Status: "active" } active)
            {
                return false;
            }
            binding = active;
            return true;
        }
    }

    public IReadOnlyList<ReleaseBindingReceiptV1> ReadReceipts(string deviceBindingId)
    {
        lock (_gate)
        {
            ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
            _devices.TryGetValue(deviceBindingId, out var state);
            if (!TrySynchronizeDevice(deviceBindingId, state))
            {
                throw new ActiveReleaseBindingException(
                    "release binding receipts cannot prove the durable journal head; refusing to serve");
            }
            return _devices.TryGetValue(deviceBindingId, out var fresh)
                ? [.. fresh.Receipts]
                : [];
        }
    }

    /// <summary>
    /// Read-time multi-instance resync: proves the cached per-device view is
    /// not behind the durable journal head before an authoritative read may
    /// serve it. When the head has advanced (another instance activated,
    /// revoked, or rolled back), the missing records are replayed through
    /// <see cref="ApplyRecord"/> — the identical validation pipeline used by
    /// construction-time recovery — so a superseding transition is served
    /// with its exact generation, digest, and token rather than the stale
    /// cache. Returns false, failing the read closed, when the store cannot
    /// be consulted, the head regressed behind the cached view, the delta
    /// violates any recovery rule, or the replay does not land exactly on
    /// the declared head. Must be called under <see cref="_gate"/>; state
    /// mutations happen only through ApplyRecord, and a refused resync
    /// publishes nothing beyond the records that already validated.
    /// </summary>
    private bool TrySynchronizeDevice(string deviceBindingId, DeviceState? state)
    {
        var cachedSequence = state?.Sequence ?? 0;
        ReleaseBindingJournalSnapshot snapshot;
        try
        {
            snapshot = _store.LoadSnapshotAfter(deviceBindingId, cachedSequence);
            if (snapshot.HeadSequence < cachedSequence)
            {
                // The journal is append-only: a durable head behind the
                // cached view means a forked or regressed store.
                return false;
            }
            if (snapshot.HeadSequence == cachedSequence)
            {
                return true;
            }
        }
        catch (Exception)
        {
            // Unreachable or erroring store: serve nothing rather than the
            // possibly superseded cache.
            return false;
        }

        var seenReceiptIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var record in snapshot.Records)
            {
                // A per-device delta carrying a foreign or null record is a
                // store-level fork; ApplyRecord itself re-checks the receipt
                // and binding identity against the record.
                if (record is null
                    || !string.Equals(record.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
                {
                    return false;
                }
                ApplyRecord(record, seenReceiptIds);
            }
        }
        catch (Exception)
        {
            return false;
        }
        return _devices.TryGetValue(deviceBindingId, out var synced)
            && synced.Sequence == snapshot.HeadSequence;
    }

    /// <summary>
    /// Transition-time durable resync. The caller already holds the store's
    /// per-device transition scope and this authority's gate, so no competing
    /// transition for this device can advance the journal between this replay
    /// and the following append. This closes both acknowledgement-loss and
    /// stale-authority windows: a committed exact request is first recovered
    /// through <see cref="ApplyRecord"/> and can return its durable receipt;
    /// an uncommitted request continues normally. Failure to prove the exact
    /// durable head is an exception, never an empty-state fallback.
    /// </summary>
    private DeviceState SynchronizeForTransition(string deviceBindingId)
    {
        _devices.TryGetValue(deviceBindingId, out var cached);
        if (!TrySynchronizeDevice(deviceBindingId, cached))
        {
            throw new ActiveReleaseBindingException(
                "release binding transition cannot prove the durable journal head; refusing to mutate");
        }
        return _devices.TryGetValue(deviceBindingId, out var synchronized)
            ? synchronized
            : new DeviceState();
    }

    public ReleaseBindingReceiptV1 Activate(
        string deviceBindingId,
        ReadOnlySpan<byte> signedBomBytes,
        string executionTokenBase64)
        => Activate(
            deviceBindingId,
            signedBomBytes,
            ReadOnlySpan<byte>.Empty,
            executionTokenBase64);

    /// <summary>
    /// Activates one exact externally signed SIGNED candidate. Every
    /// non-bootstrap activation must also carry the exact externally signed
    /// STABLE lifecycle twin of the currently bound SIGNED BOM. The stable
    /// twin is evidence for the candidate's previous_stable_bom reference;
    /// Control Plane Host verifies and journals it but never creates or signs
    /// it.
    /// </summary>
    public ReleaseBindingReceiptV1 Activate(
        string deviceBindingId,
        ReadOnlySpan<byte> signedBomBytes,
        ReadOnlySpan<byte> previousStableBomBytes,
        string executionTokenBase64)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        // Snapshot caller-owned spans before any verification. A caller may
        // back ReadOnlySpan with a mutable array; every signature check,
        // digest, idempotency identity, and persisted byte must therefore
        // observe this one immutable method-entry copy.
        var bomBytesCopy = signedBomBytes.ToArray();
        var previousStableBomBytesCopy = previousStableBomBytes.IsEmpty
            ? null
            : previousStableBomBytes.ToArray();
        ReadOnlySpan<byte> previousStableSnapshot =
            previousStableBomBytesCopy is null
                ? ReadOnlySpan<byte>.Empty
                : previousStableBomBytesCopy;
        var facts = VerifyReleaseBom(
            bomBytesCopy,
            ReleaseBomWireContract.ExpectedStatus);
        var previousStableFacts = previousStableBomBytesCopy is null
            ? null
            : VerifyReleaseBom(
                previousStableBomBytesCopy,
                ReleaseBomWireContract.PreviousStableStatus);
        var tokenSha256 = RequireCanonicalTokenSha256(executionTokenBase64);
        if (!string.Equals(tokenSha256, facts.ActivationTokenSha256, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "execution token does not match the signer-committed activation_token_sha256");
        }
        var bomSha256 = Convert.ToHexStringLower(SHA256.HashData(bomBytesCopy));
        var requestSha256 = ActivationRequestHash(
            deviceBindingId,
            bomBytesCopy,
            previousStableSnapshot,
            executionTokenBase64);

        // Global lock order (see IReleaseBindingTruthStore.BeginTransition):
        // the per-device durable serialization is acquired BEFORE _gate,
        // never the reverse, so a policy-approval recovery holding the same
        // per-device database advisory lock while briefly entering _gate
        // through the reader can never close a hold-and-wait cycle against
        // this transition. The scope's Append commits before the publish
        // below, keeping the durability-first ordering.
        using var transition = _store.BeginTransition(deviceBindingId);
        lock (_gate)
        {
            var state = SynchronizeForTransition(deviceBindingId);
            // A digest hit alone is not a replay: the original receipt's
            // postcondition must still be the device's current truth,
            // otherwise the state has moved on and returning the old receipt
            // would report a success that no longer holds.
            if (TryReplayExactRequest(
                    deviceBindingId,
                    requestSha256,
                    "activation",
                    state,
                    out var replayed))
            {
                return replayed;
            }
            if (facts.SignerGeneration <= state.LastActivationSignerGeneration)
            {
                throw new ActiveReleaseBindingException(
                    facts.SignerGeneration == state.LastActivationSignerGeneration
                        ? "conflicting re-submission: same signer release_bom_generation with different bytes or token"
                        : "signer release_bom_generation must strictly exceed the last activation (anti-rollback)");
            }
            RequirePreviousStableChain(
                facts,
                previousStableFacts,
                previousStableSnapshot,
                state);

            var generation = checked(state.RuntimeGeneration + 1);
            var now = RequireUtc(_utcNow());
            var binding = new ActiveReleaseBindingV1(
                SchemaVersion,
                "active.release.binding/v1",
                "control-plane-host",
                deviceBindingId,
                bomSha256,
                generation,
                facts.SignerGeneration,
                executionTokenBase64,
                facts.ActivationTokenSha256,
                "active",
                facts.Key.Identity,
                facts.Key.KeyId,
                facts.SignatureSha256,
                now,
                NextReceiptId(deviceBindingId, state.Sequence + 1),
                // Device-scoped truth: identity envelope fields are explicit
                // null; occurred_at is the same injected-clock instant as
                // activated_at; privacy class is fixed.
                SoulId: null,
                PlatformAccountId: null,
                TraceId: null,
                IdempotencyKey: null,
                OccurredAt: now,
                PrivacyClass: "internal");
            binding.Validate();

            // Only a binding that is still "active" is demoted to "previous"
            // and stays reachable for rollback. A revoked binding keeps its
            // revoked status in the receipt trail and never becomes a
            // rollback target; activating over it also drops any older
            // "previous" so no rollback path survives across a revocation.
            var demoted = state.Current is { Status: "active" } priorActive
                ? priorActive with { Status = "previous" }
                : null;
            var receipt = BuildReceipt(
                "activation",
                deviceBindingId,
                state,
                from: state.Current is null
                    ? null
                    : demoted is not null
                        ? new ReleaseBindingEndpointV1(demoted.ReleaseBomSha256, demoted.Generation, "previous")
                        : new ReleaseBindingEndpointV1(
                            state.Current.ReleaseBomSha256, state.Current.Generation, state.Current.Status),
                to: new ReleaseBindingEndpointV1(bomSha256, generation, "active"),
                actorIdentity: facts.Key.Identity,
                occurredAt: now);

            // Durability first: the immutable candidate (binding + receipt +
            // record) is fully built, appended to the truth store, and only
            // a successful append is ever published. An Append failure
            // leaves zero visible change on the read and idempotency
            // surfaces.
            transition.Append(new ReleaseBindingTruthRecord(
                deviceBindingId, receipt, binding, demoted,
                facts.SignerGeneration, requestSha256, bomBytesCopy,
                previousStableBomBytesCopy));
            state.RuntimeGeneration = generation;
            state.Previous = demoted;
            state.PreviousBomBytes = demoted is not null ? state.CurrentBomBytes : null;
            state.Current = binding;
            state.CurrentBomBytes = bomBytesCopy;
            state.Sequence = receipt.Sequence;
            state.LastActivationSignerGeneration = facts.SignerGeneration;
            Publish(deviceBindingId, state, receipt, requestSha256);
            return receipt;
        }
    }

    private void RequirePreviousStableChain(
        ReleaseBomFacts candidate,
        ReleaseBomFacts? previousStable,
        ReadOnlySpan<byte> previousStableBomBytes,
        DeviceState state)
    {
        if (state.Current is null)
        {
            if (candidate.PreviousStableBomId is not null
                || candidate.PreviousStableBomSha256 is not null
                || previousStable is not null
                || !previousStableBomBytes.IsEmpty)
            {
                throw new ActiveReleaseBindingException(
                    "bootstrap activation requires null previous stable references and no previous stable BOM bytes");
            }
            return;
        }

        if (candidate.PreviousStableBomId is null
            || candidate.PreviousStableBomSha256 is null
            || previousStable is null
            || previousStableBomBytes.IsEmpty)
        {
            throw new ActiveReleaseBindingException(
                "non-bootstrap activation requires the exact externally signed previous STABLE BOM wire");
        }
        var previousStableSha256 =
            Convert.ToHexStringLower(SHA256.HashData(previousStableBomBytes));
        if (!string.Equals(
                candidate.PreviousStableBomSha256,
                previousStableSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.PreviousStableBomId,
                previousStable.BomId,
                StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "candidate previous stable BOM reference does not bind the supplied STABLE wire");
        }
        if (string.Equals(candidate.BomId, previousStable.BomId, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "candidate BOM cannot identify itself as its previous stable BOM");
        }

        var currentSignedBomBytes = state.CurrentBomBytes
            ?? throw new ActiveReleaseBindingException(
                "current binding has no exact signed BOM wire for previous stable verification");
        var currentSignedFacts = VerifyReleaseBom(
            currentSignedBomBytes,
            ReleaseBomWireContract.ExpectedStatus);
        if (!string.Equals(
                state.Current.ReleaseBomSha256,
                Convert.ToHexStringLower(SHA256.HashData(currentSignedBomBytes)),
                StringComparison.Ordinal)
            || !string.Equals(
                currentSignedFacts.BomId,
                previousStable.BomId,
                StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(currentSignedFacts.LifecycleIdentitySha256),
                Convert.FromHexString(previousStable.LifecycleIdentitySha256)))
        {
            throw new ActiveReleaseBindingException(
                "supplied previous STABLE BOM is not the lifecycle twin of the current SIGNED binding");
        }
    }

    public ReleaseBindingReceiptV1 Revoke(string deviceBindingId, long generation)
    {
        // Lock order: see Activate — the per-device transition scope is
        // acquired before _gate on every transition path.
        using var transition = _store.BeginTransition(deviceBindingId);
        lock (_gate)
        {
            ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
            var requestSha256 = RequestHash(
                "dps.release.binding.revoke/v1",
                [],
                deviceBindingId + "\n" + generation.ToString(CultureInfo.InvariantCulture));
            var state = SynchronizeForTransition(deviceBindingId);
            if (TryReplayExactRequest(
                    deviceBindingId,
                    requestSha256,
                    "revocation",
                    state,
                    out var replayed))
            {
                return replayed;
            }
            if (state.Current is not { Status: "active" } active)
            {
                throw new ActiveReleaseBindingException("no active release binding to revoke");
            }
            if (generation != active.Generation)
            {
                throw new ActiveReleaseBindingException("revocation generation does not match the active binding");
            }
            var now = RequireUtc(_utcNow());
            var receipt = BuildReceipt(
                "revocation",
                deviceBindingId,
                state,
                from: new ReleaseBindingEndpointV1(active.ReleaseBomSha256, active.Generation, "active"),
                to: new ReleaseBindingEndpointV1(active.ReleaseBomSha256, active.Generation, "revoked"),
                actorIdentity: "control-plane-host",
                occurredAt: now);

            var revoked = active with { Status = "revoked" };
            // Durability first (see Activate): append, then publish.
            transition.Append(new ReleaseBindingTruthRecord(
                deviceBindingId, receipt, revoked, state.Previous,
                state.LastActivationSignerGeneration, requestSha256,
                SignedBomBytes: null,
                PreviousStableBomBytes: null));
            state.Current = revoked;
            state.Sequence = receipt.Sequence;
            Publish(deviceBindingId, state, receipt, requestSha256);
            return receipt;
        }
    }

    public ReleaseBindingReceiptV1 Rollback(string deviceBindingId, string executionTokenBase64)
    {
        var tokenSha256 = RequireCanonicalTokenSha256(executionTokenBase64);
        // Lock order: see Activate — the per-device transition scope is
        // acquired before _gate on every transition path.
        using var transition = _store.BeginTransition(deviceBindingId);
        lock (_gate)
        {
            ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
            // The token uniquely commits the rollback target: sha256(token)
            // must equal the signer's pre-committed activation_token_sha256
            // of the previous binding, so device + token identifies the
            // request (the target digest is implied by that commitment).
            var requestSha256 = RequestHash(
                "dps.release.binding.rollback/v1",
                [],
                deviceBindingId + "\n" + executionTokenBase64);
            var state = SynchronizeForTransition(deviceBindingId);
            // A digest hit alone is NOT a replay: a later activation may have
            // superseded the rolled-back binding, and reporting the stale
            // rollback as success would lie about the active BOM. Only when
            // the original receipt's postcondition is still the current truth
            // is this an idempotent replay; otherwise fall through to the
            // normal fail-closed path (the previous slot is spent).
            if (TryReplayExactRequest(
                    deviceBindingId,
                    requestSha256,
                    "rollback",
                    state,
                    out var replayed))
            {
                return replayed;
            }
            if (state.Previous is not { Status: "previous" } previous)
            {
                throw new ActiveReleaseBindingException("no previous signed release binding to roll back to");
            }
            if (!string.Equals(tokenSha256, previous.ActivationTokenSha256, StringComparison.Ordinal))
            {
                throw new ActiveReleaseBindingException(
                    "rollback token does not match the previous binding's signer-committed activation_token_sha256");
            }
            var abandoned = state.Current
                ?? throw new ActiveReleaseBindingException("rollback requires a current binding to abandon");
            var restoredBomBytes = state.PreviousBomBytes
                ?? throw new ActiveReleaseBindingException("previous signed BOM bytes are missing");
            var generation = checked(state.RuntimeGeneration + 1);
            var now = RequireUtc(_utcNow());
            // Runtime generation stays strictly monotonic; the signer ordinal
            // (release_bom_generation) legitimately reverts to the previous
            // BOM's value and the restored binding records it truthfully.
            var binding = new ActiveReleaseBindingV1(
                SchemaVersion,
                "active.release.binding/v1",
                "control-plane-host",
                deviceBindingId,
                previous.ReleaseBomSha256,
                generation,
                previous.ReleaseBomGeneration,
                executionTokenBase64,
                previous.ActivationTokenSha256,
                "active",
                previous.SignerIdentity,
                previous.SignerKeyId,
                previous.BomSignatureSha256,
                now,
                NextReceiptId(deviceBindingId, state.Sequence + 1),
                SoulId: null,
                PlatformAccountId: null,
                TraceId: null,
                IdempotencyKey: null,
                OccurredAt: now,
                PrivacyClass: "internal");
            binding.Validate();
            var receipt = BuildReceipt(
                "rollback",
                deviceBindingId,
                state,
                from: new ReleaseBindingEndpointV1(abandoned.ReleaseBomSha256, abandoned.Generation, "revoked"),
                to: new ReleaseBindingEndpointV1(previous.ReleaseBomSha256, generation, "active"),
                actorIdentity: "control-plane-host",
                occurredAt: now);

            // Durability first (see Activate): append, then publish.
            transition.Append(new ReleaseBindingTruthRecord(
                deviceBindingId, receipt, binding, PreviousBinding: null,
                state.LastActivationSignerGeneration, requestSha256,
                restoredBomBytes,
                PreviousStableBomBytes: null));
            state.RuntimeGeneration = generation;
            state.Current = binding;
            state.CurrentBomBytes = restoredBomBytes;
            state.Previous = null;
            state.PreviousBomBytes = null;
            state.Sequence = receipt.Sequence;
            Publish(deviceBindingId, state, receipt, requestSha256);
            return receipt;
        }
    }

    private static bool IsReplayPostconditionIntact(
        ReleaseBindingReceiptV1 receipt,
        DeviceState state)
        => state.Current is { } current
           && string.Equals(current.ReleaseBomSha256, receipt.To.ReleaseBomSha256, StringComparison.Ordinal)
           && current.Generation == receipt.To.Generation
           && string.Equals(current.Status, receipt.To.Status, StringComparison.Ordinal);

    private static bool TryReplayExactRequest(
        string deviceBindingId,
        string requestSha256,
        string receiptKind,
        DeviceState state,
        out ReleaseBindingReceiptV1 receipt)
    {
        if (!state.RequestReceipts.TryGetValue(
                requestSha256,
                out var candidate))
        {
            receipt = null!;
            return false;
        }
        if (!string.Equals(
                candidate.DeviceBindingId,
                deviceBindingId,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.ReceiptKind,
                receiptKind,
                StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "committed release-binding request digest resolves to a different device or transition kind");
        }
        if (!IsReplayPostconditionIntact(candidate, state))
        {
            throw new ActiveReleaseBindingException(
                "exact release-binding request was already committed but its postcondition has been superseded");
        }

        receipt = candidate;
        return true;
    }

    private void Publish(
        string deviceBindingId,
        DeviceState state,
        ReleaseBindingReceiptV1 receipt,
        string requestSha256)
    {
        if (state.RequestReceipts.ContainsKey(requestSha256))
        {
            throw new ActiveReleaseBindingException(
                "release-binding request digest was committed more than once");
        }
        state.RequestReceipts.Add(requestSha256, receipt);
        state.Receipts.Add(receipt);
        _devices[deviceBindingId] = state;
    }

    private void RecoverFromStore()
    {
        var seenReceiptIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _store.LoadAll())
        {
            ApplyRecord(record, seenReceiptIds);
        }
    }

    /// <summary>
    /// Replays one journal record onto its device's in-memory view through
    /// the full recovery validation pipeline: sequence contiguity, receipt
    /// identity, receipt/binding snapshot agreement, predecessor projection,
    /// per-kind invariants, and — for activation and rollback — full
    /// re-verification of the exact recorded signed BOM bytes against the
    /// trusted keys. Shared verbatim by construction-time recovery
    /// (<see cref="RecoverFromStore"/>) and by the read-time multi-instance
    /// resync (<see cref="TrySynchronizeDevice"/>); any violation throws
    /// before the record's state is published.
    /// </summary>
    private void ApplyRecord(
        ReleaseBindingTruthRecord record,
        HashSet<string> seenReceiptIds)
    {
        if (record is null)
        {
            throw new ActiveReleaseBindingException("truth store journal contains a null record");
        }
        // Store implementations return caller-owned record objects. Snapshot
        // both raw byte fields before validation so recovery cannot verify one
        // byte sequence and later publish a concurrently mutated sequence.
        record = record with
        {
            SignedBomBytes = record.SignedBomBytes?.ToArray(),
            PreviousStableBomBytes = record.PreviousStableBomBytes?.ToArray()
        };
        var receipt = record.Receipt
            ?? throw new ActiveReleaseBindingException("truth store record has no receipt");
        var current = record.CurrentBinding
            ?? throw new ActiveReleaseBindingException("truth store record has no current binding");
        receipt.Validate();
        current.Validate();
        record.PreviousBinding?.Validate();
        if (!string.Equals(receipt.DeviceBindingId, record.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(current.DeviceBindingId, record.DeviceBindingId, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException("truth store record device identity fork");
        }
        var state = _devices.TryGetValue(record.DeviceBindingId, out var existing)
            ? existing
            : new DeviceState();
        var priorCurrent = state.Current;
        var priorPrevious = state.Previous;
        var priorCurrentBytes = state.CurrentBomBytes;
        var priorPreviousBytes = state.PreviousBomBytes;
        var nextCurrentBytes = priorCurrentBytes;
        var nextPreviousBytes = priorPreviousBytes;
        if (receipt.Sequence != state.Sequence + 1)
        {
            throw new ActiveReleaseBindingException(
                "truth store journal sequence is not contiguous per device");
        }
        if (!string.Equals(
                receipt.ReceiptId,
                NextReceiptId(record.DeviceBindingId, receipt.Sequence),
                StringComparison.Ordinal)
            || !seenReceiptIds.Add(receipt.ReceiptId))
        {
            throw new ActiveReleaseBindingException("truth store journal receipt identity fork");
        }
        // Payload digest self-consistency is enforced by
        // ReleaseBindingReceiptV1.Validate (fixed-time), already invoked
        // above on every journal receipt — no duplicate check here.
        var expectedRuntimeGeneration = receipt.ReceiptKind switch
        {
            "activation" or "rollback" => state.RuntimeGeneration + 1,
            _ => state.RuntimeGeneration
        };
        // receipt.To must BE the recorded post-transition binding.
        if (receipt.To.Generation != expectedRuntimeGeneration
            || current.Generation != expectedRuntimeGeneration
            || !string.Equals(receipt.To.ReleaseBomSha256, current.ReleaseBomSha256, StringComparison.Ordinal)
            || !string.Equals(receipt.To.Status, current.Status, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "truth store journal receipt endpoint and binding diverge");
        }
        // receipt.From must be the projection of the prior record's
        // current binding; the very first record has no predecessor.
        if (priorCurrent is null)
        {
            if (receipt.From is not null)
            {
                throw new ActiveReleaseBindingException(
                    "truth store journal first record cannot have a from endpoint");
            }
        }
        else if (receipt.From is null
            || !string.Equals(receipt.From.ReleaseBomSha256, priorCurrent.ReleaseBomSha256, StringComparison.Ordinal)
            || receipt.From.Generation != priorCurrent.Generation)
        {
            throw new ActiveReleaseBindingException(
                "truth store journal from endpoint does not project the prior binding");
        }
        string expectedRequestSha256;
        switch (receipt.ReceiptKind)
        {
            case "activation":
            {
                var facts = RequireRecordedSignedBom(record, current);
                if (facts.SignerGeneration <= state.LastActivationSignerGeneration
                    || record.LastActivationSignerGeneration != facts.SignerGeneration)
                {
                    throw new ActiveReleaseBindingException(
                        "truth store journal signer generation regressed on activation");
                }
                var previousStableFacts = record.PreviousStableBomBytes is null
                    ? null
                    : VerifyReleaseBom(
                        record.PreviousStableBomBytes,
                        ReleaseBomWireContract.PreviousStableStatus);
                RequirePreviousStableChain(
                    facts,
                    previousStableFacts,
                    record.PreviousStableBomBytes is null
                        ? ReadOnlySpan<byte>.Empty
                        : record.PreviousStableBomBytes,
                    state);
                if (priorCurrent is { Status: "active" })
                {
                    // Activation over an active binding demotes it.
                    if (!string.Equals(receipt.From!.Status, "previous", StringComparison.Ordinal)
                        || record.PreviousBinding != priorCurrent with { Status = "previous" })
                    {
                        throw new ActiveReleaseBindingException(
                            "truth store journal previous binding does not match the demoted active");
                    }
                    nextPreviousBytes = priorCurrentBytes;
                }
                else
                {
                    // First activation or activation over a revoked
                    // binding: no rollback path survives.
                    if ((priorCurrent is not null
                            && !string.Equals(receipt.From!.Status, priorCurrent.Status, StringComparison.Ordinal))
                        || record.PreviousBinding is not null)
                    {
                        throw new ActiveReleaseBindingException(
                            "truth store journal resurrects a rollback path across revocation");
                    }
                    nextPreviousBytes = null;
                }
                if (!string.Equals(receipt.ReceiptId, current.ReceiptId, StringComparison.Ordinal))
                {
                    throw new ActiveReleaseBindingException(
                        "truth store journal binding receipt identity fork");
                }
                nextCurrentBytes = record.SignedBomBytes;
                expectedRequestSha256 = ActivationRequestHash(
                    record.DeviceBindingId,
                    record.SignedBomBytes!,
                    record.PreviousStableBomBytes is null
                        ? ReadOnlySpan<byte>.Empty
                        : record.PreviousStableBomBytes,
                    current.ExecutionTokenBase64);
                break;
            }
            case "revocation":
            {
                if (record.SignedBomBytes is not null
                    || record.PreviousStableBomBytes is not null)
                {
                    throw new ActiveReleaseBindingException(
                        "truth store journal revocation must not carry Release BOM bytes");
                }
                if (priorCurrent is not { Status: "active" }
                    || !string.Equals(receipt.From!.Status, "active", StringComparison.Ordinal)
                    || current != priorCurrent with { Status = "revoked" }
                    || record.PreviousBinding != priorPrevious
                    || record.LastActivationSignerGeneration != state.LastActivationSignerGeneration)
                {
                    throw new ActiveReleaseBindingException(
                        "truth store journal revocation does not follow from the prior state");
                }
                expectedRequestSha256 = RequestHash(
                    "dps.release.binding.revoke/v1",
                    [],
                    record.DeviceBindingId
                    + "\n"
                    + receipt.From!.Generation.ToString(CultureInfo.InvariantCulture));
                break;
            }
            case "rollback":
            {
                var facts = RequireRecordedSignedBom(record, current);
                if (priorPreviousBytes is null
                    || record.SignedBomBytes is null
                    || !record.SignedBomBytes.AsSpan().SequenceEqual(priorPreviousBytes)
                    || priorPrevious is not { Status: "previous" }
                    || !string.Equals(current.ReleaseBomSha256, priorPrevious.ReleaseBomSha256, StringComparison.Ordinal)
                    || current.ReleaseBomGeneration != priorPrevious.ReleaseBomGeneration
                    || !string.Equals(receipt.From!.Status, "revoked", StringComparison.Ordinal)
                    || record.PreviousBinding is not null
                    || record.PreviousStableBomBytes is not null
                    || record.LastActivationSignerGeneration != state.LastActivationSignerGeneration
                    || facts.SignerGeneration != priorPrevious.ReleaseBomGeneration)
                {
                    throw new ActiveReleaseBindingException(
                        "truth store journal rollback does not target the recorded previous BOM");
                }
                if (!string.Equals(receipt.ReceiptId, current.ReceiptId, StringComparison.Ordinal))
                {
                    throw new ActiveReleaseBindingException(
                        "truth store journal binding receipt identity fork");
                }
                nextCurrentBytes = record.SignedBomBytes;
                nextPreviousBytes = null;
                expectedRequestSha256 = RequestHash(
                    "dps.release.binding.rollback/v1",
                    [],
                    record.DeviceBindingId + "\n" + current.ExecutionTokenBase64);
                break;
            }
            default:
                throw new ActiveReleaseBindingException("truth store journal receipt kind is unknown");
        }
        if (!IsLowercaseHex64(record.RequestSha256)
            || !string.Equals(
                record.RequestSha256,
                expectedRequestSha256,
                StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "truth store journal request digest does not bind the recorded transition");
        }
        if (state.RequestReceipts.ContainsKey(record.RequestSha256))
        {
            throw new ActiveReleaseBindingException(
                "truth store journal reuses a committed request digest");
        }
        state.RequestReceipts.Add(record.RequestSha256, receipt);
        state.CurrentBomBytes = nextCurrentBytes;
        state.PreviousBomBytes = nextPreviousBytes;
        state.Current = current;
        state.Previous = record.PreviousBinding;
        state.RuntimeGeneration = expectedRuntimeGeneration;
        state.Sequence = receipt.Sequence;
        state.LastActivationSignerGeneration = record.LastActivationSignerGeneration;
        state.Receipts.Add(receipt);
        _devices[record.DeviceBindingId] = state;
    }

    /// <summary>
    /// Recovery cross-binding: an activation or rollback record must carry
    /// the exact canonical signed BOM it activated, and the recorded binding
    /// must be exactly what verifying those bytes yields — full RSA-PSS
    /// verification, digest, signer ordinal, committed token digest, signer
    /// identity/key, and signature digest all re-checked.
    /// </summary>
    private ReleaseBomFacts RequireRecordedSignedBom(
        ReleaseBindingTruthRecord record,
        ActiveReleaseBindingV1 binding)
    {
        if (record.SignedBomBytes is null || record.SignedBomBytes.Length == 0)
        {
            throw new ActiveReleaseBindingException(
                "truth store journal record has no signed BOM bytes to re-verify");
        }
        var facts = VerifyReleaseBom(
            record.SignedBomBytes,
            ReleaseBomWireContract.ExpectedStatus);
        if (!string.Equals(
                binding.ReleaseBomSha256,
                Convert.ToHexStringLower(SHA256.HashData(record.SignedBomBytes)),
                StringComparison.Ordinal)
            || binding.ReleaseBomGeneration != facts.SignerGeneration
            || !string.Equals(binding.ActivationTokenSha256, facts.ActivationTokenSha256, StringComparison.Ordinal)
            || !string.Equals(binding.SignerIdentity, facts.Key.Identity, StringComparison.Ordinal)
            || !string.Equals(binding.SignerKeyId, facts.Key.KeyId, StringComparison.Ordinal)
            || !string.Equals(binding.BomSignatureSha256, facts.SignatureSha256, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "truth store journal binding is not backed by its recorded signed BOM");
        }
        return facts;
    }

    private ReleaseBindingReceiptV1 BuildReceipt(
        string kind,
        string deviceBindingId,
        DeviceState state,
        ReleaseBindingEndpointV1? from,
        ReleaseBindingEndpointV1 to,
        string actorIdentity,
        DateTimeOffset occurredAt)
    {
        var sequence = checked(state.Sequence + 1);
        var unhashed = new ReleaseBindingReceiptV1(
            SchemaVersion,
            "release.binding.receipt/v1",
            "control-plane-host",
            kind,
            deviceBindingId,
            from,
            to,
            sequence,
            actorIdentity,
            occurredAt,
            new string('0', 64),
            NextReceiptId(deviceBindingId, sequence),
            // Device-scoped truth: identity envelope explicit null, fixed
            // "internal" privacy class (see contract Validate).
            SoulId: null,
            PlatformAccountId: null,
            TraceId: null,
            IdempotencyKey: null,
            PrivacyClass: "internal");
        var receipt = unhashed with { PayloadSha256 = unhashed.ComputePayloadSha256() };
        receipt.Validate();
        return receipt;
    }

    private static string NextReceiptId(string deviceBindingId, long sequence)
    {
        var material = Encoding.UTF8.GetBytes(
            "dps.release.binding.receipt/v1\n"
            + deviceBindingId
            + "\n"
            + sequence.ToString(CultureInfo.InvariantCulture));
        return "receipt_" + Convert.ToHexStringLower(SHA256.HashData(material))[..32];
    }

    private static string RequestHash(string domain, ReadOnlySpan<byte> body, string suffix)
    {
        var prefix = Encoding.UTF8.GetBytes(domain + "\n");
        var tail = Encoding.UTF8.GetBytes("\n" + suffix);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(prefix);
            hash.AppendData(body);
            hash.AppendData(tail);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
            CryptographicOperations.ZeroMemory(tail);
        }
    }

    private static string ActivationRequestHash(
        string deviceBindingId,
        ReadOnlySpan<byte> signedBomBytes,
        ReadOnlySpan<byte> previousStableBomBytes,
        string executionTokenBase64)
    {
        var domain = Encoding.UTF8.GetBytes("dps.release.binding.activate/v2\n");
        var device = Encoding.UTF8.GetBytes(deviceBindingId);
        var token = Encoding.UTF8.GetBytes(executionTokenBase64);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(domain);
            AppendLengthPrefixed(hash, device);
            AppendLengthPrefixed(hash, signedBomBytes);
            AppendLengthPrefixed(hash, previousStableBomBytes);
            AppendLengthPrefixed(hash, token);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domain);
            CryptographicOperations.ZeroMemory(device);
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private static void AppendLengthPrefixed(
        IncrementalHash hash,
        ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
        CryptographicOperations.ZeroMemory(length);
    }

    private static string RequireCanonicalTokenSha256(string executionTokenBase64)
    {
        if (string.IsNullOrEmpty(executionTokenBase64))
        {
            throw new ActiveReleaseBindingException("execution token is required");
        }
        byte[] token;
        try
        {
            token = Convert.FromBase64String(executionTokenBase64);
        }
        catch (FormatException exception)
        {
            throw new ActiveReleaseBindingException(
                "execution token must use Base64 encoding", exception);
        }
        try
        {
            if (token.Length != ReleaseBomWireContract.ExecutionTokenSizeBytes
                || !string.Equals(Convert.ToBase64String(token), executionTokenBase64, StringComparison.Ordinal))
            {
                throw new ActiveReleaseBindingException(
                    "execution token must be canonical Base64 for exactly 256 opaque bits");
            }
            return Convert.ToHexStringLower(SHA256.HashData(token));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        ControlContractValidation.RequireUtc(value, nameof(value));
        return value;
    }

    private sealed record ReleaseBomFacts(
        string BomId,
        string Status,
        long SignerGeneration,
        string ActivationTokenSha256,
        string? PreviousStableBomId,
        string? PreviousStableBomSha256,
        string LifecycleIdentitySha256,
        string SignatureSha256,
        ReleaseBomTrustKey Key);

    private ReleaseBomFacts VerifyReleaseBom(
        ReadOnlySpan<byte> signedBomBytes,
        string expectedStatus)
    {
        if (expectedStatus is not ReleaseBomWireContract.ExpectedStatus
            and not ReleaseBomWireContract.PreviousStableStatus)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedStatus),
                "Release BOM verification status must be SIGNED or STABLE.");
        }
        if (signedBomBytes.IsEmpty || signedBomBytes.Length > 4 * 1024 * 1024)
        {
            throw new ActiveReleaseBindingException(
                "signed release BOM is absent or exceeds the 4 MiB limit");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                signedBomBytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
        }
        catch (JsonException exception)
        {
            throw new ActiveReleaseBindingException("signed release BOM is not valid JSON", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ActiveReleaseBindingException("signed release BOM must be one JSON object");
            }
            // Re-encoding defense: the caller's bytes must BE the one
            // canonical wire (sorted keys, compact separators, canonical
            // escaping, signature object included). Otherwise the digest the
            // binding publishes (sha256 of the exact input bytes) would
            // decouple from the signature, letting equivalent re-encodings
            // of one signed payload mint distinct release_bom_sha256 values.
            var canonicalWire = ReleaseBomCanonicalJson.Serialize(root);
            if (!signedBomBytes.SequenceEqual(canonicalWire))
            {
                throw new ActiveReleaseBindingException(
                    "signed release BOM must be the canonical sorted compact wire");
            }
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!ReleaseBomWireContract.RequiredTopLevelFields.Contains(property.Name))
                {
                    throw new ActiveReleaseBindingException(
                        $"signed release BOM has unknown top-level field '{property.Name}'");
                }
                if (!observed.Add(property.Name))
                {
                    throw new ActiveReleaseBindingException(
                        $"signed release BOM duplicates top-level field '{property.Name}'");
                }
            }
            if (!observed.SetEquals(ReleaseBomWireContract.RequiredTopLevelFields))
            {
                throw new ActiveReleaseBindingException(
                    "signed release BOM is missing required top-level fields");
            }
            if (root.GetProperty("schema_version").GetString() != ReleaseBomWireContract.SchemaVersion)
            {
                throw new ActiveReleaseBindingException(
                    "signed release BOM schema_version must be dps.release-bom/v1");
            }
            if (root.GetProperty("status").GetString() != expectedStatus)
            {
                throw new ActiveReleaseBindingException(
                    $"signed release BOM status must be {expectedStatus}");
            }
            var bomIdElement = root.GetProperty("bom_id");
            if (bomIdElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(bomIdElement.GetString()))
            {
                throw new ActiveReleaseBindingException("bom_id is invalid");
            }
            var bomId = bomIdElement.GetString()!;
            var generationElement = root.GetProperty("release_bom_generation");
            if (generationElement.ValueKind != JsonValueKind.Number
                || !generationElement.TryGetInt64(out var signerGeneration)
                || generationElement.GetRawText().AsSpan().ContainsAny(".eE")
                || signerGeneration < 1)
            {
                throw new ActiveReleaseBindingException(
                    "release_bom_generation must be a positive signed 64-bit integer");
            }
            var activationToken = root.GetProperty("activation_token_sha256");
            if (activationToken.ValueKind != JsonValueKind.String
                || !IsLowercaseHex64(activationToken.GetString()!))
            {
                throw new ActiveReleaseBindingException("activation_token_sha256 is invalid");
            }
            var previousId = root.GetProperty("previous_stable_bom");
            var previousSha = root.GetProperty("previous_stable_bom_sha256");
            string? previousStableBomId;
            string? previousStableBomSha256;
            if (previousId.ValueKind == JsonValueKind.Null)
            {
                // candidate_bom_validator._validate_previous_bom (3210-3215):
                // a null previous id requires a null previous sha.
                if (previousSha.ValueKind != JsonValueKind.Null)
                {
                    throw new ActiveReleaseBindingException(
                        "previous_stable_bom_sha256 must be null when previous_stable_bom is null");
                }
                previousStableBomId = null;
                previousStableBomSha256 = null;
            }
            else
            {
                if (previousId.ValueKind != JsonValueKind.String
                    || previousId.GetString()!.Length < 8
                    || previousSha.ValueKind != JsonValueKind.String
                    || !IsLowercaseHex64(previousSha.GetString()!))
                {
                    throw new ActiveReleaseBindingException("previous stable BOM reference is invalid");
                }
                previousStableBomId = previousId.GetString();
                previousStableBomSha256 = previousSha.GetString();
            }

            if (!root.TryGetProperty("signature", out var signature)
                || signature.ValueKind != JsonValueKind.Object)
            {
                throw new ActiveReleaseBindingException("signed release BOM has no signature object");
            }
            string ReadSignatureField(string name)
            {
                if (!signature.TryGetProperty(name, out var value)
                    || value.ValueKind != JsonValueKind.String)
                {
                    throw new ActiveReleaseBindingException($"BOM signature field '{name}' is missing");
                }
                return value.GetString()!;
            }
            var fieldCount = signature.EnumerateObject().Count();
            if (fieldCount != 3)
            {
                throw new ActiveReleaseBindingException("BOM signature must have exactly algorithm, key_id, value");
            }
            if (ReadSignatureField("algorithm") != "rsa-pss-sha256")
            {
                throw new ActiveReleaseBindingException("only rsa-pss-sha256 BOM signatures are supported");
            }
            if (!_keys.TryGetValue(ReadSignatureField("key_id"), out var key))
            {
                throw new ActiveReleaseBindingException("BOM signature key is not trusted for bom");
            }
            var signatureValue = ReadSignatureField("value");
            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(signatureValue);
            }
            catch (FormatException exception)
            {
                throw new ActiveReleaseBindingException("BOM signature value is not valid base64", exception);
            }
            // Base64 decoding tolerates embedded whitespace; only the one
            // canonical re-encoding of the raw signature bytes is accepted.
            if (!string.Equals(Convert.ToBase64String(signatureBytes), signatureValue, StringComparison.Ordinal))
            {
                throw new ActiveReleaseBindingException("BOM signature value is not canonical base64");
            }
            var modulusHex = key.ModulusHex.Length % 2 == 0
                ? key.ModulusHex
                : "0" + key.ModulusHex;
            var modulus = Convert.FromHexString(modulusHex);
            if (signatureBytes.Length != modulus.Length
                || !UnsignedBigEndianLessThan(signatureBytes, modulus))
            {
                throw new ActiveReleaseBindingException(
                    "BOM signature is not a canonical RSA representative");
            }
            var payloadCanonical = ReleaseBomCanonicalJson.SerializeObjectWithout(root, "signature");
            var message = new byte[SignatureDomain.Length + payloadCanonical.Length];
            SignatureDomain.CopyTo(message, 0);
            payloadCanonical.CopyTo(message, SignatureDomain.Length);
            using var rsa = RSA.Create(new RSAParameters
            {
                Modulus = modulus,
                Exponent = ExponentBytes(key.Exponent)
            });
            if (!rsa.VerifyData(message, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                throw new ActiveReleaseBindingException("bom signature verification failed");
            }
            var lifecycleIdentity = ReleaseBomCanonicalJson.SerializeObjectWithout(
                root,
                "signature",
                "status");
            return new ReleaseBomFacts(
                bomId,
                expectedStatus,
                signerGeneration,
                activationToken.GetString()!,
                previousStableBomId,
                previousStableBomSha256,
                Convert.ToHexStringLower(SHA256.HashData(lifecycleIdentity)),
                Convert.ToHexStringLower(SHA256.HashData(signatureBytes)),
                key);
        }
    }

    private static bool IsLowercaseHex64(string value)
        => value.Length == 64 && !value.AsSpan().ContainsAnyExcept("0123456789abcdef");

    private static bool UnsignedBigEndianLessThan(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> upperExclusive)
    {
        if (value.Length != upperExclusive.Length)
            return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == upperExclusive[index])
                continue;
            return value[index] < upperExclusive[index];
        }
        return false;
    }

    private static byte[] ExponentBytes(int exponent)
    {
        if (exponent < 3 || exponent % 2 == 0)
        {
            throw new ActiveReleaseBindingException("bom key exponent is invalid");
        }
        var bytes = new List<byte>(4);
        var value = exponent;
        while (value > 0)
        {
            bytes.Insert(0, (byte)(value & 0xFF));
            value >>= 8;
        }
        return [.. bytes];
    }
}

/// <summary>
/// Canonical JSON identical to the python reference
/// Tools/ci/candidate_bom_validator.py::canonical_bytes — json.dumps with
/// sort_keys=True, separators=(",", ":"), ensure_ascii=False, UTF-8 encoded.
/// Plain JSON integers retain Python's arbitrary precision. JSON float
/// tokens are parsed as IEEE-754 doubles and rendered with Python's repr
/// thresholds, including negative zero and exponent formatting.
/// </summary>
public static class ReleaseBomCanonicalJson
{
    private const int MaxCanonicalNumberDigits = 4_300;

    private static readonly IComparer<string> UnicodeScalarComparer =
        Comparer<string>.Create(CompareUnicodeScalars);

    public static byte[] SerializeObjectWithout(
        JsonElement root,
        params string[] excludedProperties)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ActiveReleaseBindingException("canonical JSON root must be an object");
        }
        ArgumentNullException.ThrowIfNull(excludedProperties);
        var excluded = new HashSet<string>(excludedProperties, StringComparer.Ordinal);
        if (excluded.Count != excludedProperties.Length
            || excluded.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException(
                "canonical JSON exclusions must be unique non-empty property names",
                nameof(excludedProperties));
        }
        var builder = new StringBuilder(4096);
        WriteObject(builder, root, excluded);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] Serialize(JsonElement value)
    {
        var builder = new StringBuilder(4096);
        WriteValue(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteObject(
        StringBuilder builder,
        JsonElement value,
        IReadOnlySet<string>? excludedProperties)
    {
        builder.Append('{');
        var first = true;
        // Python's json.dumps(sort_keys=True) orders Unicode strings by
        // scalar value. StringComparer.Ordinal orders UTF-16 code units and
        // disagrees when a BMP key at U+E000..U+FFFF is compared with an
        // astral key. The activation reader must reproduce the candidate
        // validator's exact ordering for every schema-admitted map key.
        var names = new SortedSet<string>(UnicodeScalarComparer);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new ActiveReleaseBindingException("canonical JSON object has a duplicate key");
            }
        }
        foreach (var name in names)
        {
            if (excludedProperties?.Contains(name) == true)
            {
                continue;
            }
            if (!first)
            {
                builder.Append(',');
            }
            first = false;
            WriteString(builder, name);
            builder.Append(':');
            WriteValue(builder, value.GetProperty(name));
        }
        builder.Append('}');
    }

    private static int CompareUnicodeScalars(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left is null)
        {
            return -1;
        }
        if (right is null)
        {
            return 1;
        }

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftScalar = char.ConvertToUtf32(left, leftIndex);
            var rightScalar = char.ConvertToUtf32(right, rightIndex);
            if (leftScalar != rightScalar)
            {
                return leftScalar < rightScalar ? -1 : 1;
            }
            leftIndex += char.IsHighSurrogate(left[leftIndex]) ? 2 : 1;
            rightIndex += char.IsHighSurrogate(right[rightIndex]) ? 2 : 1;
        }
        if (leftIndex == left.Length && rightIndex == right.Length)
        {
            return 0;
        }
        return leftIndex == left.Length ? -1 : 1;
    }

    private static void WriteValue(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Number:
                builder.Append(CanonicalNumber(value));
                break;
            case JsonValueKind.String:
                WriteString(builder, value.GetString()!);
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }
                    firstItem = false;
                    WriteValue(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.Object:
                WriteObject(builder, value, null);
                break;
            default:
                throw new ActiveReleaseBindingException("canonical JSON value kind is unsupported");
        }
    }

    private static string CanonicalNumber(JsonElement value)
    {
        var raw = value.GetRawText();
        if (raw.Count(char.IsAsciiDigit) > MaxCanonicalNumberDigits)
        {
            throw new ActiveReleaseBindingException(
                "canonical JSON number exceeds the 4300-digit limit");
        }
        if (!raw.AsSpan().ContainsAny(".eE"))
        {
            if (!BigInteger.TryParse(
                    raw,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var integer))
            {
                throw new ActiveReleaseBindingException(
                    "canonical JSON integer is unsupported");
            }
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (!value.TryGetDouble(out var floatingPoint) || !double.IsFinite(floatingPoint))
        {
            throw new ActiveReleaseBindingException(
                "canonical JSON float must be finite");
        }
        return FormatPythonFloat(floatingPoint);
    }

    private static string FormatPythonFloat(double value)
    {
        if (value == 0d)
        {
            return BitConverter.DoubleToInt64Bits(value) < 0 ? "-0.0" : "0.0";
        }

        var rendered = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var exponentMarker = rendered.IndexOf('e');
        if (exponentMarker >= 0)
        {
            var mantissa = rendered[..exponentMarker];
            var exponent = int.Parse(
                rendered[(exponentMarker + 1)..],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture);
            if (exponent >= -4 && exponent < 16)
            {
                return ScientificToFixed(mantissa, exponent);
            }
            return mantissa + FormatExponent(exponent);
        }

        var unsigned = rendered[0] == '-' ? rendered[1..] : rendered;
        var decimalPoint = unsigned.IndexOf('.');
        var integerPart = decimalPoint >= 0 ? unsigned[..decimalPoint] : unsigned;
        int decimalExponent;
        if (integerPart.Any(character => character != '0'))
        {
            decimalExponent = integerPart.TrimStart('0').Length - 1;
        }
        else
        {
            var fractionalPart = decimalPoint >= 0 ? unsigned[(decimalPoint + 1)..] : string.Empty;
            var firstNonZero = fractionalPart.IndexOfAny(
                ['1', '2', '3', '4', '5', '6', '7', '8', '9']);
            if (firstNonZero < 0)
            {
                throw new ActiveReleaseBindingException(
                    "canonical JSON float formatting lost a non-zero value");
            }
            decimalExponent = -(firstNonZero + 1);
        }

        if (decimalExponent >= 16 || decimalExponent < -4)
        {
            return FixedToScientific(rendered, decimalExponent);
        }
        return decimalPoint >= 0 ? rendered : rendered + ".0";
    }

    private static string FixedToScientific(string rendered, int exponent)
    {
        var negative = rendered[0] == '-';
        var unsigned = negative ? rendered[1..] : rendered;
        var digits = unsigned.Replace(".", string.Empty, StringComparison.Ordinal)
            .TrimStart('0').TrimEnd('0');
        if (digits.Length == 0)
        {
            throw new ActiveReleaseBindingException(
                "canonical JSON float formatting produced no significant digits");
        }
        var mantissa = digits.Length == 1
            ? digits
            : digits[0] + "." + digits[1..];
        return (negative ? "-" : string.Empty) + mantissa + FormatExponent(exponent);
    }

    private static string ScientificToFixed(string mantissa, int exponent)
    {
        var negative = mantissa[0] == '-';
        var unsigned = negative ? mantissa[1..] : mantissa;
        var digits = unsigned.Replace(".", string.Empty, StringComparison.Ordinal);
        var decimalPosition = exponent + 1;
        string fixedPoint;
        if (decimalPosition <= 0)
        {
            fixedPoint = "0." + new string('0', -decimalPosition) + digits;
        }
        else if (decimalPosition >= digits.Length)
        {
            fixedPoint = digits + new string('0', decimalPosition - digits.Length) + ".0";
        }
        else
        {
            fixedPoint = digits[..decimalPosition] + "." + digits[decimalPosition..];
        }
        return (negative ? "-" : string.Empty) + fixedPoint;
    }

    private static string FormatExponent(int exponent)
        => "e" + (exponent >= 0 ? "+" : "-")
            + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u")
                            .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }
}
