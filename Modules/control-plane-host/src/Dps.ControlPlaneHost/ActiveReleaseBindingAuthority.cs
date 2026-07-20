using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.ControlPlaneHost.Contracts;

namespace Dps.ControlPlaneHost;

/// <summary>
/// Raised whenever active release binding material fails a fail-closed check.
/// No state mutation ever precedes this exception.
/// </summary>
public sealed class ActiveReleaseBindingException : Exception
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
                || purposes.ValueKind != JsonValueKind.Array
                || !purposes.EnumerateArray().Any(static value =>
                    value.ValueKind == JsonValueKind.String && value.GetString() == "bom"))
            {
                continue;
            }
            if (key.GetProperty("algorithm").GetString() != "rsa-pss-sha256")
            {
                throw new ActiveReleaseBindingException("bom key algorithm must be rsa-pss-sha256");
            }
            parsed.Add(new ReleaseBomTrustKey(
                key.GetProperty("key_id").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key_id is missing"),
                key.GetProperty("identity").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key identity is missing"),
                key.GetProperty("modulus_hex").GetString()
                    ?? throw new ActiveReleaseBindingException("bom key modulus is missing"),
                key.GetProperty("exponent").GetInt32()));
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
/// backs.
/// </summary>
public sealed record ReleaseBindingTruthRecord(
    string DeviceBindingId,
    ReleaseBindingReceiptV1 Receipt,
    ActiveReleaseBindingV1 CurrentBinding,
    ActiveReleaseBindingV1? PreviousBinding,
    long LastActivationSignerGeneration,
    string RequestSha256,
    byte[]? SignedBomBytes);

/// <summary>
/// Append-only truth store for release binding transitions. The authority
/// appends one record per successful transition and loads the full journal
/// at construction to recover state. NOTE: only a deterministic in-memory
/// implementation exists in this batch; a durable PostgreSQL adapter is a
/// registered obligation for a later batch — nothing is persisted across
/// process death yet.
/// </summary>
public interface IReleaseBindingTruthStore
{
    void Append(ReleaseBindingTruthRecord record);
    IReadOnlyList<ReleaseBindingTruthRecord> LoadAll();
}

/// <summary>
/// Deterministic in-memory truth store for tests and restart-recovery tests.
/// Not durable: process death loses the journal.
/// </summary>
public sealed class InMemoryReleaseBindingTruthStore : IReleaseBindingTruthStore
{
    private readonly Lock _gate = new();
    private readonly List<ReleaseBindingTruthRecord> _records = [];

    public void Append(ReleaseBindingTruthRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var receipt = record.Receipt
            ?? throw new ActiveReleaseBindingException("truth store append requires a receipt");
        lock (_gate)
        {
            // Minimal compare-and-swap guard: the journal accepts only the
            // exactly-next sequence per device, so two authority instances
            // sharing one store cannot both land the same generation — the
            // loser faults instead of silently forking the journal. The
            // durable PostgreSQL CAS journal remains a later batch.
            long lastSequence = 0;
            foreach (var existing in _records)
            {
                if (string.Equals(existing.DeviceBindingId, record.DeviceBindingId, StringComparison.Ordinal))
                {
                    lastSequence = existing.Receipt.Sequence;
                }
            }
            if (receipt.Sequence != lastSequence + 1)
            {
                throw new ActiveReleaseBindingException(
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
///   7. previous_stable_bom_sha256 chain: equal to the current binding's
///      ReleaseBomSha256 when the device has binding history, and JSON null
///      (together with previous_stable_bom null) when it has none —
///      mirroring candidate_bom_validator._validate_previous_bom lines
///      3210-3215 where a null previous id requires a null previous sha
///      (bootstrap shape).
///
/// State is recovered from the injected truth store at construction: the
/// journal is replayed with sequence/generation/receipt-identity
/// verification and any fork or regression refuses service. All public
/// members share one lock; byte-identical re-submissions return the original
/// receipt without state change and conflicting re-submissions fail closed.
/// </summary>
public sealed class ActiveReleaseBindingAuthority : IActiveReleaseBindingReader
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
            if (!keys.TryAdd(key.KeyId, key))
            {
                throw new ActiveReleaseBindingException("duplicate bom trust key id");
            }
        }
        _keys = keys;
        _store = store;
        _utcNow = utcNow;
        RecoverFromStore();
    }

    public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding)
    {
        lock (_gate)
        {
            binding = null;
            if (deviceBindingId is null
                || !_devices.TryGetValue(deviceBindingId, out var state)
                || state.Current is not { Status: "active" } active)
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
            return _devices.TryGetValue(deviceBindingId, out var state)
                ? [.. state.Receipts]
                : [];
        }
    }

    public ReleaseBindingReceiptV1 Activate(
        string deviceBindingId,
        ReadOnlySpan<byte> signedBomBytes,
        string executionTokenBase64)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        var facts = VerifySignedBom(signedBomBytes);
        var tokenSha256 = RequireCanonicalTokenSha256(executionTokenBase64);
        if (!string.Equals(tokenSha256, facts.ActivationTokenSha256, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "execution token does not match the signer-committed activation_token_sha256");
        }
        var bomSha256 = Convert.ToHexStringLower(SHA256.HashData(signedBomBytes));
        var requestSha256 = RequestHash(
            "dps.release.binding.activate/v1", signedBomBytes, executionTokenBase64);

        var bomBytesCopy = signedBomBytes.ToArray();

        lock (_gate)
        {
            var state = _devices.TryGetValue(deviceBindingId, out var existing)
                ? existing
                : new DeviceState();
            // A digest hit alone is not a replay: the original receipt's
            // postcondition must still be the device's current truth,
            // otherwise the state has moved on and returning the old receipt
            // would report a success that no longer holds.
            if (state.RequestReceipts.TryGetValue(requestSha256, out var replayed)
                && IsReplayPostconditionIntact(replayed, state))
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
            if (state.Current is { } current)
            {
                if (!string.Equals(facts.PreviousStableBomSha256, current.ReleaseBomSha256, StringComparison.Ordinal))
                {
                    throw new ActiveReleaseBindingException(
                        "previous_stable_bom_sha256 must equal the device's current binding digest");
                }
            }
            else if (facts.PreviousStableBomSha256 is not null)
            {
                throw new ActiveReleaseBindingException(
                    "previous_stable_bom_sha256 must be null for a device with no binding history");
            }

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
            _store.Append(new ReleaseBindingTruthRecord(
                deviceBindingId, receipt, binding, demoted,
                facts.SignerGeneration, requestSha256, bomBytesCopy));
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

    public ReleaseBindingReceiptV1 Revoke(string deviceBindingId, long generation)
    {
        lock (_gate)
        {
            ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
            var requestSha256 = RequestHash(
                "dps.release.binding.revoke/v1",
                [],
                deviceBindingId + "\n" + generation.ToString(CultureInfo.InvariantCulture));
            if (_devices.TryGetValue(deviceBindingId, out var state)
                && state.RequestReceipts.TryGetValue(requestSha256, out var replayed)
                && IsReplayPostconditionIntact(replayed, state))
            {
                return replayed;
            }
            if (state is null || state.Current is not { Status: "active" } active)
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
            _store.Append(new ReleaseBindingTruthRecord(
                deviceBindingId, receipt, revoked, state.Previous,
                state.LastActivationSignerGeneration, requestSha256, SignedBomBytes: null));
            state.Current = revoked;
            state.Sequence = receipt.Sequence;
            Publish(deviceBindingId, state, receipt, requestSha256);
            return receipt;
        }
    }

    public ReleaseBindingReceiptV1 Rollback(string deviceBindingId, string executionTokenBase64)
    {
        var tokenSha256 = RequireCanonicalTokenSha256(executionTokenBase64);
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
            // A digest hit alone is NOT a replay: a later activation may have
            // superseded the rolled-back binding, and reporting the stale
            // rollback as success would lie about the active BOM. Only when
            // the original receipt's postcondition is still the current truth
            // is this an idempotent replay; otherwise fall through to the
            // normal fail-closed path (the previous slot is spent).
            if (_devices.TryGetValue(deviceBindingId, out var state)
                && state.RequestReceipts.TryGetValue(requestSha256, out var replayed)
                && IsReplayPostconditionIntact(replayed, state))
            {
                return replayed;
            }
            if (state is null || state.Previous is not { Status: "previous" } previous)
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
            _store.Append(new ReleaseBindingTruthRecord(
                deviceBindingId, receipt, binding, PreviousBinding: null,
                state.LastActivationSignerGeneration, requestSha256, restoredBomBytes));
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

    private void Publish(
        string deviceBindingId,
        DeviceState state,
        ReleaseBindingReceiptV1 receipt,
        string requestSha256)
    {
        state.Receipts.Add(receipt);
        state.RequestReceipts[requestSha256] = receipt;
        _devices[deviceBindingId] = state;
    }

    private void RecoverFromStore()
    {
        var seenReceiptIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _store.LoadAll())
        {
            if (record is null)
            {
                throw new ActiveReleaseBindingException("truth store journal contains a null record");
            }
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
                    // Live Activate previous-chain invariant, replayed
                    // verbatim: a device with binding history (active or
                    // revoked alike) requires the BOM to chain to the
                    // current binding digest; a first activation requires
                    // an explicit null chain. Without this, a validly
                    // signed BOM from another chain position could be
                    // journaled into a position live Activate would refuse.
                    if (priorCurrent is null
                        ? facts.PreviousStableBomSha256 is not null
                        : !string.Equals(
                            facts.PreviousStableBomSha256,
                            priorCurrent.ReleaseBomSha256,
                            StringComparison.Ordinal))
                    {
                        throw new ActiveReleaseBindingException(
                            "truth store journal activation breaks the previous stable BOM chain");
                    }
                    if (priorCurrent is { Status: "active" })
                    {
                        // Activation over an active binding demotes it.
                        if (!string.Equals(receipt.From!.Status, "previous", StringComparison.Ordinal)
                            || record.PreviousBinding != priorCurrent with { Status = "previous" })
                        {
                            throw new ActiveReleaseBindingException(
                                "truth store journal previous binding does not match the demoted active");
                        }
                        state.PreviousBomBytes = priorCurrentBytes;
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
                        state.PreviousBomBytes = null;
                    }
                    if (!string.Equals(receipt.ReceiptId, current.ReceiptId, StringComparison.Ordinal))
                    {
                        throw new ActiveReleaseBindingException(
                            "truth store journal binding receipt identity fork");
                    }
                    state.CurrentBomBytes = record.SignedBomBytes;
                    break;
                }
                case "revocation":
                {
                    if (record.SignedBomBytes is not null)
                    {
                        throw new ActiveReleaseBindingException(
                            "truth store journal revocation must not carry signed BOM bytes");
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
                    state.CurrentBomBytes = record.SignedBomBytes;
                    state.PreviousBomBytes = null;
                    break;
                }
                default:
                    throw new ActiveReleaseBindingException("truth store journal receipt kind is unknown");
            }
            if (string.IsNullOrEmpty(record.RequestSha256))
            {
                throw new ActiveReleaseBindingException("truth store journal request digest is missing");
            }
            // Mirror runtime semantics: a later legitimate transition with
            // the same request digest (e.g. rolling back to the same BOM
            // twice across an intervening activation) supersedes the older
            // idempotency entry. Duplicated or forked records are already
            // rejected by the sequence and receipt-identity checks above.
            state.RequestReceipts[record.RequestSha256] = receipt;
            state.Current = current;
            state.Previous = record.PreviousBinding;
            state.RuntimeGeneration = expectedRuntimeGeneration;
            state.Sequence = receipt.Sequence;
            state.LastActivationSignerGeneration = record.LastActivationSignerGeneration;
            state.Receipts.Add(receipt);
            _devices[record.DeviceBindingId] = state;
        }
    }

    /// <summary>
    /// Recovery cross-binding: an activation or rollback record must carry
    /// the exact canonical signed BOM it activated, and the recorded binding
    /// must be exactly what verifying those bytes yields — full RSA-PSS
    /// verification, digest, signer ordinal, committed token digest, signer
    /// identity/key, and signature digest all re-checked.
    /// </summary>
    private SignedBomFacts RequireRecordedSignedBom(
        ReleaseBindingTruthRecord record,
        ActiveReleaseBindingV1 binding)
    {
        if (record.SignedBomBytes is null || record.SignedBomBytes.Length == 0)
        {
            throw new ActiveReleaseBindingException(
                "truth store journal record has no signed BOM bytes to re-verify");
        }
        var facts = VerifySignedBom(record.SignedBomBytes);
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
        var material = new byte[prefix.Length + body.Length + tail.Length];
        prefix.CopyTo(material, 0);
        body.CopyTo(material.AsSpan(prefix.Length));
        tail.CopyTo(material.AsSpan(prefix.Length + body.Length));
        return Convert.ToHexStringLower(SHA256.HashData(material));
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

    private sealed record SignedBomFacts(
        long SignerGeneration,
        string ActivationTokenSha256,
        string? PreviousStableBomSha256,
        string SignatureSha256,
        ReleaseBomTrustKey Key);

    private SignedBomFacts VerifySignedBom(ReadOnlySpan<byte> signedBomBytes)
    {
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
            if (root.GetProperty("status").GetString() != ReleaseBomWireContract.ExpectedStatus)
            {
                throw new ActiveReleaseBindingException(
                    "signed release BOM status must be SIGNED");
            }
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
            var payloadCanonical = ReleaseBomCanonicalJson.SerializeObjectWithout(root, "signature");
            var message = new byte[SignatureDomain.Length + payloadCanonical.Length];
            SignatureDomain.CopyTo(message, 0);
            payloadCanonical.CopyTo(message, SignatureDomain.Length);
            using var rsa = RSA.Create(new RSAParameters
            {
                Modulus = Convert.FromHexString(key.ModulusHex),
                Exponent = ExponentBytes(key.Exponent)
            });
            if (!rsa.VerifyData(message, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                throw new ActiveReleaseBindingException("bom signature verification failed");
            }
            return new SignedBomFacts(
                signerGeneration,
                activationToken.GetString()!,
                previousStableBomSha256,
                Convert.ToHexStringLower(SHA256.HashData(signatureBytes)),
                key);
        }
    }

    private static bool IsLowercaseHex64(string value)
        => value.Length == 64 && !value.AsSpan().ContainsAnyExcept("0123456789abcdef");

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
/// Integers render invariantly; non-integer numbers pass through as raw
/// text (the signature round trip pins the single signed encoding — see
/// the number case below).
/// </summary>
public static class ReleaseBomCanonicalJson
{
    public static byte[] SerializeObjectWithout(JsonElement root, string excludedProperty)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ActiveReleaseBindingException("canonical JSON root must be an object");
        }
        var builder = new StringBuilder(4096);
        WriteObject(builder, root, excludedProperty);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] Serialize(JsonElement value)
    {
        var builder = new StringBuilder(4096);
        WriteValue(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteObject(StringBuilder builder, JsonElement value, string? excludedProperty)
    {
        builder.Append('{');
        var first = true;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new ActiveReleaseBindingException("canonical JSON object has a duplicate key");
            }
        }
        foreach (var name in names)
        {
            if (excludedProperty is not null && name == excludedProperty)
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
                if (value.TryGetInt64(out var integer)
                    && !value.GetRawText().AsSpan().ContainsAny(".eE"))
                {
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    // Non-integer numbers (legal e.g. as float feature
                    // flags, admitted by candidate_bom_validator.py:1684)
                    // pass through as their exact raw text. Uniqueness is
                    // guaranteed end-to-end by the signature: the verified
                    // message is THIS canonical serialization and the input
                    // bytes must equal it byte-for-byte, so a re-encoded
                    // float (0.5 -> 5e-1) survives the round-trip check but
                    // changes the message away from the bytes the signer
                    // actually signed and fails RSA-PSS verification. The
                    // published digest therefore always names the single
                    // signed encoding.
                    builder.Append(value.GetRawText());
                }
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
