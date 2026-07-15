using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeSupervisor.Contracts;

namespace Dps.WindowsEdgeSupervisor;

public enum WorkerSlot
{
    A,
    B
}

public sealed record WorkerArtifact(
    WorkerSlot Slot,
    string Version,
    string VersionDirectory,
    string BinaryPath,
    string Sha256,
    string HealthEvidencePath,
    string HealthEvidenceSha256,
    string ShadowEvidencePath,
    string ShadowEvidenceSha256,
    string RuntimeManifestPath,
    string RuntimeManifestSha256,
    string VersionDirectorySecuritySha256,
    string SignatureBase64,
    string SigningKeyId);

public static class WorkerArtifactSigning
{
    public static byte[] CreateStatement(
        WorkerSlot slot,
        string version,
        string artifactSha256,
        string healthEvidenceSha256,
        string shadowEvidenceSha256,
        string runtimeManifestSha256,
        string versionDirectorySecuritySha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(healthEvidenceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowEvidenceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeManifestSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectorySecuritySha256);
        if (!Enum.IsDefined(slot))
            throw new ArgumentOutOfRangeException(nameof(slot));
        return Encoding.UTF8.GetBytes(string.Join(
            "\n",
            "dps.worker-artifact/v2",
            slot.ToString(),
            version,
            artifactSha256,
            healthEvidenceSha256,
            shadowEvidenceSha256,
            runtimeManifestSha256,
            versionDirectorySecuritySha256));
    }
}

public sealed record RouteSnapshot(
    string DeviceBindingId,
    WorkerSlot Slot,
    string Version,
    string ArtifactSha256,
    long RoutingEpoch);

public sealed record DrainScope(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record DrainExpectation(
    string DrainId,
    WorkerSlot Slot,
    string WorkerVersion,
    string ArtifactSha256,
    long RoutingEpoch,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    string OccurredAt);

public sealed record SupervisorDeploymentBinding(
    string HostId,
    string ReleaseBomSha256,
    string ProtectedPolicySha256,
    string BridgeServerKeyId,
    string JournalArtifactSha256,
    string ArtifactTrustFingerprint,
    string CapabilityTrustFingerprint,
    string DrainDirectiveTrustFingerprint,
    string DrainDirectiveSigningKeyId,
    string WorkerDrainTrustFingerprint,
    string JournalDrainTrustFingerprint);

public sealed class CandidateLaunchAuthorization
{
    private readonly object _sync = new();
    private bool _launchConsumed;

    internal CandidateLaunchAuthorization(
        WorkerArtifact artifact,
        string capabilityEvidenceSha256,
        string capabilityAttestationKeyId,
        DateTimeOffset expiresAt,
        WorkerRuntimeClosureProof runtimeClosure)
    {
        Artifact = artifact;
        CapabilityEvidenceSha256 = capabilityEvidenceSha256;
        CapabilityAttestationKeyId = capabilityAttestationKeyId;
        ExpiresAt = expiresAt;
        RuntimeClosure = runtimeClosure;
        AuthorizationNonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    public WorkerArtifact Artifact { get; }
    public string CapabilityEvidenceSha256 { get; }
    public string CapabilityAttestationKeyId { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string AuthorizationNonce { get; }
    internal WorkerRuntimeClosureProof RuntimeClosure { get; }

    internal LockedWorkerRuntimeClosure ConsumeForLaunch()
    {
        lock (_sync)
        {
            if (_launchConsumed)
                throw new InvalidOperationException("candidate launch authorization was already consumed");
            if (DateTimeOffset.UtcNow >= ExpiresAt)
                throw new InvalidOperationException("candidate launch authorization expired before process launch");
            var locked = RuntimeClosure.LockForLaunch();
            _launchConsumed = true;
            return locked;
        }
    }

    internal void RequireFreshConsumedLaunch()
    {
        lock (_sync)
        {
            if (!_launchConsumed)
                throw new InvalidOperationException("candidate process was not launched by the authorized controller");
            if (DateTimeOffset.UtcNow >= ExpiresAt)
                throw new InvalidOperationException("candidate capability expired before durable staging commit");
            RuntimeClosure.Revalidate();
        }
    }
}

public sealed class AbWorkerSupervisor
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _sync = new();
    private readonly string _approvedRoot;
    private readonly HashSet<string> _requiredCapabilities;
    private readonly PinnedRsaTrustStore _artifactTrustStore;
    private readonly PinnedRsaTrustStore _drainDirectiveTrustStore;
    private readonly PinnedRsaTrustStore _workerDrainTrustStore;
    private readonly PinnedRsaTrustStore _journalDrainTrustStore;
    private readonly IDrainDirectiveSigningBroker _drainDirectiveSigningBroker;
    private readonly IJournalDrainAttestationProvider _journalDrainAttestationProvider;
    private readonly SupervisorDeploymentBinding _deployment;
    private readonly DurableSupervisorStateStore _stateStore;
    private readonly Dictionary<WorkerSlot, SlotState> _slots = new();
    private readonly Dictionary<string, WorkerSlot> _bindingRoutes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _drainDirectiveIssueGate = new(1, 1);
    private WorkerSlot _activeSlot;
    private WorkerSlot? _previousSlot;
    private DrainExpectation? _activeDrain;
    private PersistedPreparedDrainDirective? _preparedDrainDirective;
    private string? _lastWorkerDrainReceiptWireSha256;
    private string? _lastJournalDrainAttestationWireSha256;
    private CandidateLaunchAuthorization? _preparedCandidateLaunch;
    private long _routingEpoch;
    private long _stateGeneration;
    private string _stateSha256 = string.Empty;
    private bool _faulted;

    private AbWorkerSupervisor(
        string approvedVersionRoot,
        IEnumerable<string> requiredCapabilities,
        PinnedRsaTrustStore artifactTrustStore,
        PinnedRsaTrustStore drainDirectiveTrustStore,
        PinnedRsaTrustStore workerDrainTrustStore,
        PinnedRsaTrustStore journalDrainTrustStore,
        IDrainDirectiveSigningBroker drainDirectiveSigningBroker,
        IJournalDrainAttestationProvider journalDrainAttestationProvider,
        SupervisorDeploymentBinding deployment,
        DurableSupervisorStateStore stateStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedVersionRoot);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        ArgumentNullException.ThrowIfNull(artifactTrustStore);
        ArgumentNullException.ThrowIfNull(drainDirectiveTrustStore);
        ArgumentNullException.ThrowIfNull(workerDrainTrustStore);
        ArgumentNullException.ThrowIfNull(journalDrainTrustStore);
        ArgumentNullException.ThrowIfNull(drainDirectiveSigningBroker);
        ArgumentNullException.ThrowIfNull(journalDrainAttestationProvider);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(stateStore);
        _approvedRoot = ResolveDirectory(Path.GetFullPath(approvedVersionRoot));
        _requiredCapabilities = new HashSet<string>(requiredCapabilities, StringComparer.Ordinal);
        if (_requiredCapabilities.Count == 0 || _requiredCapabilities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("at least one non-empty required capability is required");
        _artifactTrustStore = artifactTrustStore;
        _drainDirectiveTrustStore = drainDirectiveTrustStore;
        _workerDrainTrustStore = workerDrainTrustStore;
        _journalDrainTrustStore = journalDrainTrustStore;
        _drainDirectiveSigningBroker = drainDirectiveSigningBroker;
        _journalDrainAttestationProvider = journalDrainAttestationProvider;
        _deployment = deployment;
        _stateStore = stateStore;
        ValidateDeploymentBinding(deployment);
        if (artifactTrustStore.StoreFingerprint != deployment.ArtifactTrustFingerprint ||
            drainDirectiveTrustStore.StoreFingerprint != deployment.DrainDirectiveTrustFingerprint ||
            workerDrainTrustStore.StoreFingerprint != deployment.WorkerDrainTrustFingerprint ||
            journalDrainTrustStore.StoreFingerprint != deployment.JournalDrainTrustFingerprint)
            throw new InvalidOperationException("Supervisor trust stores do not match the process-bound deployment fingerprints");
        if (drainDirectiveSigningBroker.KeyId != deployment.DrainDirectiveSigningKeyId)
            throw new InvalidOperationException(
                "drain-directive signing broker does not match the process-bound signing identity");
        using var directiveKey = drainDirectiveTrustStore.CloneRequiredPublicKey(
            deployment.DrainDirectiveSigningKeyId);
        var roleKeySets = new[]
        {
            artifactTrustStore.KeyIds,
            drainDirectiveTrustStore.KeyIds,
            workerDrainTrustStore.KeyIds,
            journalDrainTrustStore.KeyIds
        };
        for (var left = 0; left < roleKeySets.Length; left++)
        for (var right = left + 1; right < roleKeySets.Length; right++)
        {
            if (roleKeySets[left].Intersect(roleKeySets[right], StringComparer.Ordinal).Any())
                throw new InvalidOperationException(
                    "artifact, Supervisor, Worker, and Journal trust key sets must be pairwise disjoint");
        }
        if (roleKeySets.Any(keys => keys.Contains(deployment.BridgeServerKeyId, StringComparer.Ordinal)))
            throw new InvalidOperationException(
                "bridge server identity must be disjoint from artifact and drain trust roles");
        if (drainDirectiveTrustStore.KeyIds.Intersect(
                workerDrainTrustStore.KeyIds,
                StringComparer.Ordinal).Any() ||
            drainDirectiveTrustStore.KeyIds.Intersect(
                journalDrainTrustStore.KeyIds,
                StringComparer.Ordinal).Any() ||
            workerDrainTrustStore.KeyIds.Intersect(
                journalDrainTrustStore.KeyIds,
                StringComparer.Ordinal).Any())
            throw new InvalidOperationException(
                "Supervisor, Worker, and Journal drain trust key sets must be pairwise disjoint");
    }

    internal static AbWorkerSupervisor Bootstrap(
        string approvedVersionRoot,
        WorkerArtifact initialArtifact,
        IEnumerable<string> requiredCapabilities,
        PinnedRsaTrustStore artifactTrustStore,
        PinnedRsaTrustStore drainDirectiveTrustStore,
        PinnedRsaTrustStore workerDrainTrustStore,
        PinnedRsaTrustStore journalDrainTrustStore,
        IDrainDirectiveSigningBroker drainDirectiveSigningBroker,
        IJournalDrainAttestationProvider journalDrainAttestationProvider,
        SupervisorDeploymentBinding deployment,
        DurableSupervisorStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(initialArtifact);
        var supervisor = new AbWorkerSupervisor(
            approvedVersionRoot,
            requiredCapabilities,
            artifactTrustStore,
            drainDirectiveTrustStore,
            workerDrainTrustStore,
            journalDrainTrustStore,
            drainDirectiveSigningBroker,
            journalDrainAttestationProvider,
            deployment,
            stateStore);
        var initialValidation = supervisor.ValidateArtifact(initialArtifact, requireShadow: false);
        supervisor._activeSlot = initialArtifact.Slot;
        supervisor._slots.Add(initialArtifact.Slot, new SlotState(
            initialArtifact,
            accepting: true,
            validated: initialValidation.HealthValidated,
            previouslyStable: true,
            inFlight: 0,
            capabilityEvidenceSha256: null,
            initialValidation.RuntimeClosure));
        var stored = stateStore.Initialize(supervisor.CapturePersistentState());
        supervisor._stateGeneration = stored.Generation;
        supervisor._stateSha256 = stored.StateSha256;
        return supervisor;
    }

    internal static AbWorkerSupervisor Resume(
        string approvedVersionRoot,
        IEnumerable<string> requiredCapabilities,
        PinnedRsaTrustStore artifactTrustStore,
        PinnedRsaTrustStore drainDirectiveTrustStore,
        PinnedRsaTrustStore workerDrainTrustStore,
        PinnedRsaTrustStore journalDrainTrustStore,
        IDrainDirectiveSigningBroker drainDirectiveSigningBroker,
        IJournalDrainAttestationProvider journalDrainAttestationProvider,
        SupervisorDeploymentBinding deployment,
        DurableSupervisorStateStore stateStore)
    {
        var supervisor = new AbWorkerSupervisor(
            approvedVersionRoot,
            requiredCapabilities,
            artifactTrustStore,
            drainDirectiveTrustStore,
            workerDrainTrustStore,
            journalDrainTrustStore,
            drainDirectiveSigningBroker,
            journalDrainAttestationProvider,
            deployment,
            stateStore);
        var stored = stateStore.LoadRequired();
        supervisor.Restore(stored);
        return supervisor;
    }

    public WorkerSlot ActiveSlot
    {
        get { lock (_sync) return _activeSlot; }
    }

    public long RoutingEpoch
    {
        get { lock (_sync) return _routingEpoch; }
    }

    public long DurableStateGeneration
    {
        get { lock (_sync) return _stateGeneration; }
    }

    internal void StageCandidateForSimulation(WorkerArtifact candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_sync)
        {
            EnsureOperational();
            EnsureCandidateSlotAvailable(candidate);
            var validation = ValidateArtifact(candidate, requireShadow: true);
            _slots[candidate.Slot] = new SlotState(
                candidate,
                accepting: false,
                validated: true,
                previouslyStable: false,
                inFlight: 0,
                capabilityEvidenceSha256: null,
                validation.RuntimeClosure);
            PersistOrFault();
        }
    }

    internal CandidateLaunchAuthorization PrepareCandidateLaunch(
        WorkerArtifact candidate,
        CapabilityEvidenceVerification capabilityVerification)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(capabilityVerification);
        lock (_sync)
        {
            EnsureOperational();
            if (_preparedCandidateLaunch is not null)
                throw new InvalidOperationException("another candidate launch is already prepared");
            EnsureCandidateSlotAvailable(candidate);
            var expiresAt = ValidateCapabilityBinding(candidate, capabilityVerification);
            var validation = ValidateArtifact(candidate, requireShadow: true);
            var authorization = new CandidateLaunchAuthorization(
                candidate,
                capabilityVerification.WireSha256,
                capabilityVerification.Evidence.AttestationKeyId!,
                expiresAt,
                validation.RuntimeClosure);
            _preparedCandidateLaunch = authorization;
            return authorization;
        }
    }

    internal void CommitCandidateLaunch(CandidateLaunchAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_sync)
        {
            EnsureOperational();
            if (!ReferenceEquals(_preparedCandidateLaunch, authorization))
                throw new InvalidOperationException("candidate launch authorization is stale or was not issued by this Supervisor");
            EnsureCandidateSlotAvailable(authorization.Artifact);
            authorization.RequireFreshConsumedLaunch();
            _slots[authorization.Artifact.Slot] = new SlotState(
                authorization.Artifact,
                accepting: false,
                validated: true,
                previouslyStable: false,
                inFlight: 0,
                capabilityEvidenceSha256: authorization.CapabilityEvidenceSha256,
                authorization.RuntimeClosure);
            PersistOrFault();
            _preparedCandidateLaunch = null;
        }
    }

    internal void AbortCandidateLaunch(CandidateLaunchAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_sync)
        {
            if (ReferenceEquals(_preparedCandidateLaunch, authorization))
                _preparedCandidateLaunch = null;
        }
    }

    internal RouteLease AcquireRoute(string deviceBindingId)
    {
        ValidateDeviceBindingId(deviceBindingId);
        lock (_sync)
        {
            EnsureOperational();
            var active = _slots[_activeSlot];
            if (!active.Accepting)
                throw new InvalidOperationException("active slot is draining and not accepting new commands");
            active.RuntimeClosure.Revalidate();
            if (_bindingRoutes.TryGetValue(deviceBindingId, out var assigned) && assigned != _activeSlot)
                throw new InvalidOperationException("stale device route remains after cutover");

            _bindingRoutes[deviceBindingId] = _activeSlot;
            var routedSlot = _activeSlot;
            active.InFlight++;
            PersistOrFault();
            var snapshot = new RouteSnapshot(
                deviceBindingId,
                _activeSlot,
                active.Artifact.Version,
                active.Artifact.Sha256,
                _routingEpoch);
            return new RouteLease(snapshot, () => Complete(routedSlot));
        }
    }

    internal DrainExpectation BeginDrain(DrainScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateDrainScope(scope);
        lock (_sync)
        {
            EnsureOperational();
            if (_activeDrain is not null)
                throw new InvalidOperationException("the active slot already has a drain in progress");
            if (_preparedDrainDirective is not null)
                throw new InvalidOperationException(
                    "a prepared drain directive exists without an active drain");
            var active = _slots[_activeSlot];
            active.Accepting = false;
            _activeDrain = new DrainExpectation(
                "drain-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                _activeSlot,
                active.Artifact.Version,
                active.Artifact.Sha256,
                _routingEpoch,
                scope.SoulId,
                scope.DeviceBindingId,
                scope.PlatformAccountId,
                scope.TraceId,
                scope.IdempotencyKey,
                scope.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            PersistOrFault();
            return _activeDrain;
        }
    }

    /// <summary>
    /// Creates the one signed directive for the current active drain and durably
    /// records the exact raw wire before returning it to a transport caller.
    /// Every retry returns the same bytes; an expired prepared directive is never
    /// silently re-signed under the same drain ID.
    /// </summary>
    internal async Task<byte[]> PrepareDrainDirectiveAsync(
        CancellationToken cancellationToken = default)
    {
        await _drainDirectiveIssueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DrainExpectation drain;
            PersistedPreparedDrainDirective? existing;
            lock (_sync)
            {
                EnsureOperational();
                drain = _activeDrain ?? throw new InvalidOperationException(
                    "a drain directive can be prepared only for the current active drain");
                existing = _preparedDrainDirective;
            }

            if (existing is not null)
                return ValidatePreparedDrainDirectiveContinuation(existing, drain);

            var issuedAt = DateTimeOffset.UtcNow;
            var issuedAtText = issuedAt.ToString("O", CultureInfo.InvariantCulture);
            var claims = new DrainDirectiveClaimsV1(
                DrainDirectiveV1Codec.SchemaVersion,
                DrainDirectiveV1Codec.ContractId,
                DrainDirectiveV1Codec.ProducerModule,
                drain.SoulId,
                drain.DeviceBindingId,
                drain.PlatformAccountId,
                drain.TraceId,
                drain.IdempotencyKey,
                drain.OccurredAt,
                "internal",
                drain.DrainId,
                drain.Slot.ToString(),
                drain.WorkerVersion,
                drain.ArtifactSha256,
                _deployment.JournalArtifactSha256,
                _deployment.ReleaseBomSha256,
                _deployment.ProtectedPolicySha256,
                drain.RoutingEpoch,
                issuedAtText,
                issuedAtText,
                issuedAt.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture),
                _deployment.DrainDirectiveSigningKeyId,
                DrainDirectiveV1Codec.SignatureAlgorithm);
            var statement = DrainDirectiveV1Codec.CreateSigningStatement(claims);
            var signature = await _drainDirectiveSigningBroker
                .SignDrainDirectiveStatementAsync(statement, cancellationToken)
                .ConfigureAwait(false);
            var envelope = DrainDirectiveV1Codec.AttachSignature(claims, signature);
            var wire = DrainDirectiveV1Codec.Serialize(envelope);
            using (var publicKey = _drainDirectiveTrustStore.CloneRequiredPublicKey(
                       _deployment.DrainDirectiveSigningKeyId))
            {
                _ = DrainDirectiveV1Codec.DecodeAndVerify(
                    wire,
                    CreateDirectiveExpectation(drain),
                    publicKey,
                    issuedAt);
            }
            var prepared = new PersistedPreparedDrainDirective(
                "PREPARED",
                drain.DrainId,
                Convert.ToBase64String(wire),
                DrainDirectiveV1Codec.ComputeSha256(wire));

            lock (_sync)
            {
                EnsureOperational();
                if (_activeDrain != drain)
                    throw new InvalidOperationException(
                        "the active drain changed while its signed directive was being prepared");
                if (_preparedDrainDirective is not null)
                    throw new InvalidOperationException(
                        "a conflicting signed drain directive was prepared concurrently");
                _preparedDrainDirective = prepared;
                PersistOrFault();
            }
            return wire.ToArray();
        }
        finally
        {
            _drainDirectiveIssueGate.Release();
        }
    }

    internal async Task<bool> TryCutoverAsync(
        ReadOnlyMemory<byte> signedWorkerDrainReceipt,
        CancellationToken cancellationToken = default)
    {
        DrainExpectation drain;
        PersistedPreparedDrainDirective preparedDirective;
        WorkerSlot oldSlot;
        lock (_sync)
        {
            EnsureOperational();
            oldSlot = _activeSlot;
            var oldState = _slots[oldSlot];
            if (_activeDrain is null || oldState.Accepting || oldState.InFlight != 0 ||
                _preparedDrainDirective is null ||
                _preparedDrainDirective.DrainId != _activeDrain.DrainId)
                return false;
            var candidateSlot = oldSlot == WorkerSlot.A ? WorkerSlot.B : WorkerSlot.A;
            if (!_slots.TryGetValue(candidateSlot, out var candidate) || !candidate.Validated)
                return false;
            candidate.RuntimeClosure.Revalidate();
            drain = _activeDrain;
            preparedDirective = _preparedDrainDirective;
        }

        var evidence = await TryAcquireDrainEvidenceAsync(
            signedWorkerDrainReceipt,
            drain,
            cancellationToken).ConfigureAwait(false);
        if (evidence is null) return false;

        lock (_sync)
        {
            EnsureOperational();
            if (_activeDrain != drain || _activeSlot != oldSlot ||
                _preparedDrainDirective != preparedDirective)
                return false;
            var oldState = _slots[oldSlot];
            if (oldState.Accepting || oldState.InFlight != 0)
                return false;
            var candidateSlot = oldSlot == WorkerSlot.A ? WorkerSlot.B : WorkerSlot.A;
            if (!_slots.TryGetValue(candidateSlot, out var candidate) || !candidate.Validated)
                return false;
            candidate.RuntimeClosure.Revalidate();

            _previousSlot = oldSlot;
            _activeSlot = candidateSlot;
            candidate.Accepting = true;
            candidate.PreviouslyStable = true;
            _bindingRoutes.Clear();
            _routingEpoch++;
            _activeDrain = null;
            _preparedDrainDirective = null;
            _lastWorkerDrainReceiptWireSha256 = evidence.WorkerWireSha256;
            _lastJournalDrainAttestationWireSha256 = evidence.JournalWireSha256;
            PersistOrFault();
            return true;
        }
    }

    internal async Task<bool> TryRollbackAsync(
        ReadOnlyMemory<byte> signedWorkerDrainReceipt,
        CancellationToken cancellationToken = default)
    {
        DrainExpectation drain;
        PersistedPreparedDrainDirective preparedDirective;
        WorkerSlot currentSlot;
        WorkerSlot rollbackSlot;
        lock (_sync)
        {
            EnsureOperational();
            if (_previousSlot is null)
                return false;
            currentSlot = _activeSlot;
            rollbackSlot = _previousSlot.Value;
            var current = _slots[currentSlot];
            if (_activeDrain is null || current.Accepting || current.InFlight != 0 ||
                _preparedDrainDirective is null ||
                _preparedDrainDirective.DrainId != _activeDrain.DrainId)
                return false;
            if (!_slots.TryGetValue(rollbackSlot, out var rollback) ||
                !rollback.Validated || !rollback.PreviouslyStable)
                return false;
            rollback.RuntimeClosure.Revalidate();
            drain = _activeDrain;
            preparedDirective = _preparedDrainDirective;
        }

        var evidence = await TryAcquireDrainEvidenceAsync(
            signedWorkerDrainReceipt,
            drain,
            cancellationToken).ConfigureAwait(false);
        if (evidence is null) return false;

        lock (_sync)
        {
            EnsureOperational();
            if (_activeDrain != drain || _activeSlot != currentSlot ||
                _previousSlot != rollbackSlot || _preparedDrainDirective != preparedDirective)
                return false;
            var current = _slots[currentSlot];
            if (current.Accepting || current.InFlight != 0 ||
                !_slots.TryGetValue(rollbackSlot, out var rollback) ||
                !rollback.Validated || !rollback.PreviouslyStable)
                return false;
            rollback.RuntimeClosure.Revalidate();

            _activeSlot = rollbackSlot;
            _previousSlot = null;
            rollback.Accepting = true;
            rollback.PreviouslyStable = true;
            _bindingRoutes.Clear();
            _routingEpoch++;
            _activeDrain = null;
            _preparedDrainDirective = null;
            _lastWorkerDrainReceiptWireSha256 = evidence.WorkerWireSha256;
            _lastJournalDrainAttestationWireSha256 = evidence.JournalWireSha256;
            PersistOrFault();
            return true;
        }
    }

    public int InFlight(WorkerSlot slot)
    {
        lock (_sync) return _slots.TryGetValue(slot, out var state) ? state.InFlight : 0;
    }

    private async Task<VerifiedDurableDrainEvidence?> TryAcquireDrainEvidenceAsync(
        ReadOnlyMemory<byte> signedWorkerDrainReceipt,
        DrainExpectation drain,
        CancellationToken cancellationToken)
    {
        var expectation = new DrainReceiptExpectation(
            drain.DrainId,
            drain.Slot,
            drain.WorkerVersion,
            drain.ArtifactSha256,
            _deployment.JournalArtifactSha256,
            _deployment.ReleaseBomSha256,
            _deployment.ProtectedPolicySha256,
            drain.RoutingEpoch,
            drain.SoulId,
            drain.DeviceBindingId,
            drain.PlatformAccountId,
            drain.TraceId,
            drain.IdempotencyKey,
            drain.OccurredAt);
        try
        {
            var worker = DurableDrainEvidenceVerifier.DecodeAndVerifyWorker(
                signedWorkerDrainReceipt.Span,
                expectation,
                _workerDrainTrustStore);
            var journalRequestId = CreateJournalDrainRequestId(drain, worker.WireSha256);
            var request = new JournalDrainAttestationRequest(
                journalRequestId,
                drain.DrainId,
                "worker-drain-" + drain.DrainId["drain-".Length..],
                worker.Envelope.WorkerArtifactSha256,
                worker.Envelope.WorkerVersion,
                worker.Envelope.Slot,
                worker.Envelope.JournalArtifactSha256,
                worker.Envelope.ReleaseBomSha256,
                worker.Envelope.ProtectedPolicySha256,
                worker.Envelope.RoutingEpoch,
                worker.Envelope.IntakeStopped,
                worker.Envelope.WorkerDrained,
                worker.Envelope.RemainingInFlight,
                worker.WireSha256,
                TimeSpan.FromMinutes(5));
            JournalDrainAttestation attestation;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    attestation = await _journalDrainAttestationProvider.IssueDrainAttestationAsync(
                            request,
                            timeout.Token)
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
            }
            var journalWire = Encoding.UTF8.GetBytes(JournalDrainAttestationCodec.Serialize(attestation));
            return DurableDrainEvidenceVerifier.DecodeAndVerifyPair(
                worker,
                journalWire,
                expectation,
                journalRequestId,
                _journalDrainTrustStore,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or CryptographicException or EncoderFallbackException or
                JournalConflictException or JournalCorruptionException or JournalQuarantinedException or
                JournalAttestationUnavailableException or JournalAttestationStateChangedException or IOException or
                TimeoutException)
        {
            return null;
        }
    }

    private static string CreateJournalDrainRequestId(DrainExpectation drain, string workerWireSha256) =>
        "drainreq_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            "dps.windows-edge-supervisor.journal-drain-request-id/v1",
            drain.DrainId,
            workerWireSha256))));

    private byte[] ValidatePreparedDrainDirectiveContinuation(
        PersistedPreparedDrainDirective prepared,
        DrainExpectation drain)
    {
        if (prepared.State != "PREPARED" || prepared.DrainId != drain.DrainId)
            throw new InvalidDataException(
                "prepared drain directive does not belong to the current active drain");
        var wire = DecodePreparedDrainDirectiveWire(prepared);
        using var publicKey = _drainDirectiveTrustStore.CloneRequiredPublicKey(
            _deployment.DrainDirectiveSigningKeyId);
        _ = DrainDirectiveV1Codec.DecodeAndVerifyDurableContinuation(
            wire,
            CreateDirectiveExpectation(drain),
            publicKey);
        return wire;
    }

    private static byte[] DecodePreparedDrainDirectiveWire(
        PersistedPreparedDrainDirective prepared)
    {
        byte[] wire;
        try
        {
            wire = Convert.FromBase64String(prepared.WireBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "prepared drain directive wire is not canonical Base64", exception);
        }
        if (Convert.ToBase64String(wire) != prepared.WireBase64 ||
            DrainDirectiveV1Codec.ComputeSha256(wire) != prepared.WireSha256)
            throw new InvalidDataException(
                "prepared drain directive exact raw wire digest is invalid");
        return wire;
    }

    private DrainDirectiveExpectationV1 CreateDirectiveExpectation(DrainExpectation drain) => new(
        drain.DrainId,
        drain.Slot.ToString(),
        drain.WorkerVersion,
        drain.ArtifactSha256,
        _deployment.JournalArtifactSha256,
        _deployment.ReleaseBomSha256,
        _deployment.ProtectedPolicySha256,
        drain.RoutingEpoch,
        drain.SoulId,
        drain.DeviceBindingId,
        drain.PlatformAccountId,
        drain.TraceId,
        drain.IdempotencyKey,
        drain.OccurredAt);

    private void Complete(WorkerSlot slot)
    {
        lock (_sync)
        {
            EnsureOperational();
            var state = _slots[slot];
            if (state.InFlight <= 0)
                throw new InvalidOperationException("route lease was completed more than once");
            state.InFlight--;
            PersistOrFault();
        }
    }

    private void EnsureCandidateSlotAvailable(WorkerArtifact candidate)
    {
        if (!Enum.IsDefined(candidate.Slot))
            throw new InvalidOperationException("candidate Worker slot must be A or B");
        if (_activeDrain is not null)
            throw new InvalidOperationException(
                "candidate slot and runtime closure are frozen while a drain transition is active");
        if (candidate.Slot == _activeSlot)
            throw new InvalidOperationException("candidate must use the inactive slot");
        if (_previousSlot == candidate.Slot)
            throw new InvalidOperationException(
                "the previous stable rollback slot is protected until rollback or a separately authorized soak release");
        if (_slots.TryGetValue(candidate.Slot, out var existing) && existing.InFlight != 0)
            throw new InvalidOperationException("cannot replace a slot with in-flight commands");
    }

    private DateTimeOffset ValidateCapabilityBinding(
        WorkerArtifact candidate,
        CapabilityEvidenceVerification verification)
    {
        var assessment = verification.Assessment;
        var evidence = verification.Evidence;
        if (!verification.AttestationVerified ||
            assessment.Status != "PASS" || assessment.VerificationClaim != "WINDOWS_VERIFIED" ||
            assessment.Missing.Count != 0)
            throw new InvalidOperationException("candidate staging requires a cryptographically verified PASS capability receipt");
        if (verification.TrustStoreFingerprint != _deployment.CapabilityTrustFingerprint ||
            evidence.HostId != _deployment.HostId ||
            evidence.ReleaseBomSha256 != _deployment.ReleaseBomSha256 ||
            evidence.ProtectedPolicySha256 != _deployment.ProtectedPolicySha256 ||
            evidence.WorkerArtifactSha256 != candidate.Sha256 ||
            evidence.WorkerVersion != candidate.Version ||
            evidence.WorkerSlot != candidate.Slot.ToString() ||
            evidence.PeerAuthKeyId != _deployment.BridgeServerKeyId)
            throw new InvalidOperationException("capability receipt does not bind the protected deployment and exact candidate artifact");
        if (evidence.AttestationKeyId is null ||
            evidence.AttestationKeyId == candidate.SigningKeyId ||
            evidence.AttestationKeyId == _deployment.BridgeServerKeyId ||
            _artifactTrustStore.KeyIds.Contains(evidence.AttestationKeyId, StringComparer.Ordinal) ||
            _drainDirectiveTrustStore.KeyIds.Contains(evidence.AttestationKeyId, StringComparer.Ordinal) ||
            _workerDrainTrustStore.KeyIds.Contains(evidence.AttestationKeyId, StringComparer.Ordinal) ||
            _journalDrainTrustStore.KeyIds.Contains(evidence.AttestationKeyId, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "capability attestation identity must be independent from artifact, bridge, Supervisor, Worker, and Journal roles");
        if (!DateTimeOffset.TryParse(
                evidence.ExpiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("capability receipt expired before candidate staging");
        return expiresAt;
    }

    private ArtifactValidation ValidateArtifact(WorkerArtifact artifact, bool requireShadow)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!Enum.IsDefined(artifact.Slot))
            throw new ArgumentOutOfRangeException(nameof(artifact), "Worker slot must be A or B");
        if (string.IsNullOrWhiteSpace(artifact.Version))
            throw new ArgumentException("worker version is required");
        ValidateLowerSha256(artifact.Sha256, "worker artifact");
        ValidateLowerSha256(artifact.HealthEvidenceSha256, "health evidence");
        ValidateLowerSha256(artifact.ShadowEvidenceSha256, "shadow evidence");
        ValidateLowerSha256(artifact.RuntimeManifestSha256, "runtime manifest");
        ValidateLowerSha256(artifact.VersionDirectorySecuritySha256, "version directory security");
        if (string.IsNullOrWhiteSpace(artifact.SigningKeyId) || string.IsNullOrWhiteSpace(artifact.SignatureBase64))
            throw new ArgumentException("worker artifact signing proof is required");

        var runtimeClosure = WorkerRuntimeClosureProof.Capture(_approvedRoot, artifact);
        var lexicalVersionDirectory = Path.GetFullPath(artifact.VersionDirectory);
        var lexicalBinaryPath = Path.GetFullPath(artifact.BinaryPath);
        EnsureUnderApprovedRoot(ResolveDirectory(lexicalVersionDirectory));
        var relativeBinary = Path.GetRelativePath(lexicalVersionDirectory, lexicalBinaryPath);
        if (Path.IsPathRooted(relativeBinary) || relativeBinary == ".." ||
            relativeBinary.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("worker binary must exist inside its immutable version directory");

        var versionDirectory = ResolveDirectory(lexicalVersionDirectory);
        var binaryPath = ResolveFile(Path.Combine(versionDirectory, relativeBinary));
        EnsureUnderApprovedRoot(binaryPath);
        if (!IsWithin(binaryPath, versionDirectory))
            throw new InvalidOperationException("worker binary resolved outside its version directory");
        var signatureStatement = WorkerArtifactSigning.CreateStatement(
            artifact.Slot,
            artifact.Version,
            artifact.Sha256,
            artifact.HealthEvidenceSha256,
            artifact.ShadowEvidenceSha256,
            artifact.RuntimeManifestSha256,
            artifact.VersionDirectorySecuritySha256);
        if (!_artifactTrustStore.VerifyPssSha256Base64(
                artifact.SigningKeyId,
                signatureStatement,
                artifact.SignatureBase64))
            throw new InvalidOperationException("worker artifact signature is not trusted by the pinned Release BOM key store");

        var health = ReadEvidence(
            artifact.HealthEvidencePath,
            artifact.HealthEvidenceSha256,
            versionDirectory,
            artifact.Sha256,
            requireNoSideEffects: false);
        var missing = _requiredCapabilities.Except(health.Capabilities!, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException("candidate capabilities are missing: " + string.Join(",", missing));
        if (requireShadow)
        {
            _ = ReadEvidence(
                artifact.ShadowEvidencePath,
                artifact.ShadowEvidenceSha256,
                versionDirectory,
                artifact.Sha256,
                requireNoSideEffects: true);
        }
        runtimeClosure.Revalidate();
        return new ArtifactValidation(
            HealthValidated: true,
            ShadowValidated: requireShadow,
            runtimeClosure);
    }

    private static WorkerEvidence ReadEvidence(
        string evidencePath,
        string expectedEvidenceSha256,
        string versionDirectory,
        string artifactSha256,
        bool requireNoSideEffects)
    {
        if (string.IsNullOrWhiteSpace(evidencePath))
            throw new InvalidOperationException("worker evidence path is missing");
        var lexicalPath = Path.GetFullPath(evidencePath);
        var relative = Path.GetRelativePath(versionDirectory, lexicalPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("worker evidence path is outside its version directory");
        var physicalPath = ResolveFile(Path.Combine(versionDirectory, relative));
        if (!IsWithin(physicalPath, versionDirectory))
            throw new InvalidOperationException("worker evidence resolved outside its version directory");
        byte[] bytes;
        using (var stream = new FileStream(
                   physicalPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > 1024 * 1024)
                throw new InvalidOperationException("worker evidence exceeds its fixed size limit");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (digest != expectedEvidenceSha256)
            throw new InvalidOperationException("worker evidence digest mismatch");
        WorkerEvidence evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<WorkerEvidence>(bytes, EvidenceJsonOptions) ??
                throw new InvalidOperationException("worker evidence is empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("worker evidence JSON is invalid", exception);
        }
        if (evidence.Status != "PASS" || evidence.ArtifactSha256 != artifactSha256)
            throw new InvalidOperationException("worker evidence does not bind a passing artifact");
        if (evidence.Capabilities is null || evidence.Capabilities.Length is < 1 or > 64 ||
            evidence.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            evidence.Capabilities.Any(capability => capability.Length > 64 ||
                capability.Any(character =>
                    character is not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and not '_' and not '-')) ||
            evidence.Capabilities.Distinct(StringComparer.Ordinal).Count() != evidence.Capabilities.Length ||
            evidence.SideEffects < 0)
            throw new InvalidOperationException("worker evidence capabilities or side-effect count is invalid");
        if (requireNoSideEffects && evidence.SideEffects != 0)
            throw new InvalidOperationException("shadow evidence reports real side effects");
        return evidence;
    }

    private void Restore(StoredSupervisorState stored)
    {
        var payload = stored.Payload;
        if (payload.Slots is null || payload.BindingRoutes is null ||
            payload.Slots.Any(static slot => slot is null) ||
            payload.BindingRoutes.Any(static route => route is null))
            throw new InvalidDataException("durable Supervisor state arrays cannot be null");
        if (payload.HostId != _deployment.HostId ||
            payload.ReleaseBomSha256 != _deployment.ReleaseBomSha256 ||
            payload.ProtectedPolicySha256 != _deployment.ProtectedPolicySha256 ||
            payload.BridgeServerKeyId != _deployment.BridgeServerKeyId ||
            payload.JournalArtifactSha256 != _deployment.JournalArtifactSha256 ||
            payload.ArtifactTrustFingerprint != _deployment.ArtifactTrustFingerprint ||
            payload.CapabilityTrustFingerprint != _deployment.CapabilityTrustFingerprint ||
            payload.DrainDirectiveTrustFingerprint != _deployment.DrainDirectiveTrustFingerprint ||
            payload.DrainDirectiveSigningKeyId != _deployment.DrainDirectiveSigningKeyId ||
            payload.WorkerDrainTrustFingerprint != _deployment.WorkerDrainTrustFingerprint ||
            payload.JournalDrainTrustFingerprint != _deployment.JournalDrainTrustFingerprint)
            throw new InvalidDataException("durable Supervisor state does not match the protected deployment binding");
        if (!Enum.IsDefined(payload.ActiveSlot) ||
            payload.PreviousSlot is not null && !Enum.IsDefined(payload.PreviousSlot.Value) ||
            payload.RoutingEpoch < 0 || payload.Slots.Length is < 1 or > 2 ||
            payload.Slots.Select(slot => slot!.Artifact.Slot).Distinct().Count() != payload.Slots.Length)
            throw new InvalidDataException("durable Supervisor slot or routing state is invalid");
        if ((payload.LastWorkerDrainReceiptWireSha256 is null) !=
            (payload.LastJournalDrainAttestationWireSha256 is null))
            throw new InvalidDataException(
                "durable Supervisor state must persist Worker and Journal drain proof digests as one pair");
        if (payload.LastWorkerDrainReceiptWireSha256 is not null)
        {
            ValidateLowerSha256(
                payload.LastWorkerDrainReceiptWireSha256,
                "last Worker drain receipt wire");
            ValidateLowerSha256(
                payload.LastJournalDrainAttestationWireSha256!,
                "last Journal drain attestation wire");
        }
        foreach (var persisted in payload.Slots)
        {
            if (persisted is null)
                throw new InvalidDataException("durable Supervisor state contains a null slot");
            if (!Enum.IsDefined(persisted.Artifact.Slot))
                throw new InvalidDataException("durable Supervisor state contains an unknown Worker slot");
            if (persisted.InFlight < 0)
                throw new InvalidDataException("durable Supervisor in-flight count is negative");
            if (persisted.InFlight != 0)
                throw new InvalidOperationException("durable Supervisor restart found in-flight commands and fails closed pending reconciliation");
            if (persisted.CapabilityEvidenceSha256 is not null)
                ValidateLowerSha256(persisted.CapabilityEvidenceSha256, "capability evidence");
            var mustBeStable = persisted.Artifact.Slot == payload.ActiveSlot ||
                persisted.Artifact.Slot == payload.PreviousSlot;
            var validation = ValidateArtifact(
                persisted.Artifact,
                requireShadow: persisted.Validated && !persisted.PreviouslyStable);
            if (mustBeStable && (!persisted.PreviouslyStable || !persisted.Validated || !validation.HealthValidated))
                throw new InvalidDataException("durable Supervisor active or rollback slot is not a validated stable artifact");
            _slots.Add(persisted.Artifact.Slot, new SlotState(
                persisted.Artifact,
                persisted.Accepting,
                validated: mustBeStable,
                previouslyStable: mustBeStable,
                inFlight: 0,
                capabilityEvidenceSha256: persisted.CapabilityEvidenceSha256,
                validation.RuntimeClosure));
        }
        if (!_slots.ContainsKey(payload.ActiveSlot))
            throw new InvalidDataException("durable Supervisor active slot is missing");
        if (payload.PreviousSlot is not null && !_slots.ContainsKey(payload.PreviousSlot.Value))
            throw new InvalidDataException("durable Supervisor previous slot is missing");
        if (payload.ActiveDrain is not null)
        {
            ValidateDrainExpectation(payload.ActiveDrain);
            if (payload.ActiveDrain.Slot != payload.ActiveSlot || _slots[payload.ActiveSlot].Accepting)
                throw new InvalidDataException("durable Supervisor drain state is inconsistent");
            if (payload.PreparedDrainDirective is not null)
            {
                ValidatePreparedDrainDirectiveContinuation(
                    payload.PreparedDrainDirective,
                    payload.ActiveDrain);
            }
        }
        else if (!_slots[payload.ActiveSlot].Accepting)
        {
            throw new InvalidDataException("durable Supervisor active slot is stopped without a persisted drain");
        }
        else if (payload.PreparedDrainDirective is not null)
        {
            throw new InvalidDataException(
                "durable Supervisor prepared directive exists without an active drain");
        }
        foreach (var route in payload.BindingRoutes)
        {
            if (route is null)
                throw new InvalidDataException("durable Supervisor state contains a null binding route");
            if (!Enum.IsDefined(route.Slot))
                throw new InvalidDataException("durable Supervisor state contains an unknown route slot");
            ValidateDeviceBindingId(route.DeviceBindingId);
            if (route.Slot != payload.ActiveSlot || !_bindingRoutes.TryAdd(route.DeviceBindingId, route.Slot))
                throw new InvalidDataException("durable Supervisor binding route is stale or duplicated");
        }
        _activeSlot = payload.ActiveSlot;
        _previousSlot = payload.PreviousSlot;
        _routingEpoch = payload.RoutingEpoch;
        _activeDrain = payload.ActiveDrain;
        _preparedDrainDirective = payload.PreparedDrainDirective;
        _lastWorkerDrainReceiptWireSha256 = payload.LastWorkerDrainReceiptWireSha256;
        _lastJournalDrainAttestationWireSha256 = payload.LastJournalDrainAttestationWireSha256;
        _stateGeneration = stored.Generation;
        _stateSha256 = stored.StateSha256;
    }

    private SupervisorStatePayload CapturePersistentState() => new(
        _deployment.HostId,
        _deployment.ReleaseBomSha256,
        _deployment.ProtectedPolicySha256,
        _deployment.BridgeServerKeyId,
        _deployment.JournalArtifactSha256,
        _deployment.ArtifactTrustFingerprint,
        _deployment.CapabilityTrustFingerprint,
        _deployment.DrainDirectiveTrustFingerprint,
        _deployment.DrainDirectiveSigningKeyId,
        _deployment.WorkerDrainTrustFingerprint,
        _deployment.JournalDrainTrustFingerprint,
        _activeSlot,
        _previousSlot,
        _routingEpoch,
        _activeDrain,
        _preparedDrainDirective,
        _lastWorkerDrainReceiptWireSha256,
        _lastJournalDrainAttestationWireSha256,
        _slots.Values.OrderBy(slot => slot.Artifact.Slot).Select(slot => new PersistedSlotState(
            slot.Artifact,
            slot.Accepting,
            slot.Validated,
            slot.PreviouslyStable,
            slot.InFlight,
            slot.CapabilityEvidenceSha256)).ToArray(),
        _bindingRoutes.OrderBy(route => route.Key, StringComparer.Ordinal)
            .Select(route => new PersistedBindingRoute(route.Key, route.Value)).ToArray());

    private void PersistOrFault()
    {
        try
        {
            var stored = _stateStore.Advance(
                CapturePersistentState(),
                _stateGeneration,
                _stateSha256);
            _stateGeneration = stored.Generation;
            _stateSha256 = stored.StateSha256;
        }
        catch
        {
            _faulted = true;
            foreach (var slot in _slots.Values) slot.Accepting = false;
            throw;
        }
    }

    private void EnsureOperational()
    {
        if (_faulted)
            throw new InvalidOperationException("Supervisor durable state faulted; all routing remains fail closed");
    }

    private static void ValidateDeploymentBinding(SupervisorDeploymentBinding deployment)
    {
        ValidatePrefixedLowerHex(deployment.HostId, "host_", 64, "host_id");
        ValidateLowerSha256(deployment.ReleaseBomSha256, "Release BOM");
        ValidateLowerSha256(deployment.ProtectedPolicySha256, "protected policy");
        ValidatePrefixedLowerHex(deployment.BridgeServerKeyId, "sha256_", 64, "bridge_server_key_id");
        ValidateLowerSha256(deployment.JournalArtifactSha256, "journal artifact");
        ValidateLowerSha256(deployment.ArtifactTrustFingerprint, "artifact trust fingerprint");
        ValidateLowerSha256(deployment.CapabilityTrustFingerprint, "capability trust fingerprint");
        ValidateLowerSha256(deployment.DrainDirectiveTrustFingerprint, "drain directive trust fingerprint");
        ValidatePrefixedLowerHex(
            deployment.DrainDirectiveSigningKeyId,
            "sha256_",
            64,
            "drain_directive_signing_key_id");
        ValidateLowerSha256(deployment.WorkerDrainTrustFingerprint, "worker drain trust fingerprint");
        ValidateLowerSha256(deployment.JournalDrainTrustFingerprint, "journal drain trust fingerprint");
        if (deployment.WorkerDrainTrustFingerprint == deployment.JournalDrainTrustFingerprint ||
            deployment.DrainDirectiveTrustFingerprint == deployment.WorkerDrainTrustFingerprint ||
            deployment.DrainDirectiveTrustFingerprint == deployment.JournalDrainTrustFingerprint)
            throw new InvalidDataException(
                "Supervisor, Worker, and Journal drain trust stores must be cryptographically distinct");
    }

    private static void ValidateDrainScope(DrainScope scope)
    {
        ValidatePrefixedLowerHex(scope.SoulId, "soul_", 64, "soul_id");
        ValidateDeviceBindingId(scope.DeviceBindingId);
        ValidatePrefixedLowerHex(scope.PlatformAccountId, "pa_", 32, "platform_account_id");
        ValidatePrefixedLowerHex(scope.TraceId, "trace_", 32, "trace_id");
        ValidatePrefixedLowerHex(scope.IdempotencyKey, "idem_", 64, "idempotency_key");
        if (scope.OccurredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("drain occurred_at must use an explicit zero UTC offset");
    }

    private static void ValidateDrainExpectation(DrainExpectation drain)
    {
        if (!Enum.IsDefined(drain.Slot))
            throw new InvalidDataException("drain expectation contains an unknown Worker slot");
        ValidatePrefixedLowerHex(drain.DrainId, "drain-", 64, "drain_id");
        ValidateLowerSha256(drain.ArtifactSha256, "drain artifact");
        ValidateDrainScope(new DrainScope(
            drain.SoulId,
            drain.DeviceBindingId,
            drain.PlatformAccountId,
            drain.TraceId,
            drain.IdempotencyKey,
            DateTimeOffset.Parse(drain.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
    }

    private void EnsureUnderApprovedRoot(string path)
    {
        if (!IsWithin(path, _approvedRoot))
            throw new InvalidOperationException("worker path is outside the approved version root");
    }

    private static string ResolveDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException(path);
        return (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
    }

    private static string ResolveFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("worker artifact file is missing", path);
        return (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void ValidateDeviceBindingId(string? value) =>
        ValidatePrefixedLowerHex(value, "db_", 32, "device_binding_id");

    private static void ValidatePrefixedLowerHex(string? value, string prefix, int bodyLength, string field)
    {
        if (value is null || value.Length != prefix.Length + bodyLength ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.AsSpan(prefix.Length).ToString().Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new ArgumentException(field + " is invalid");
    }

    private static void ValidateLowerSha256(string? value, string field)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new ArgumentException(field + " SHA-256 is not canonical");
    }

    private sealed class SlotState(
        WorkerArtifact artifact,
        bool accepting,
        bool validated,
        bool previouslyStable,
        int inFlight,
        string? capabilityEvidenceSha256,
        WorkerRuntimeClosureProof runtimeClosure)
    {
        public WorkerArtifact Artifact { get; } = artifact;
        public bool Accepting { get; set; } = accepting;
        public bool Validated { get; } = validated;
        public bool PreviouslyStable { get; set; } = previouslyStable;
        public int InFlight { get; set; } = inFlight;
        public string? CapabilityEvidenceSha256 { get; } = capabilityEvidenceSha256;
        public WorkerRuntimeClosureProof RuntimeClosure { get; } = runtimeClosure;
    }

    private sealed record ArtifactValidation(
        bool HealthValidated,
        bool ShadowValidated,
        WorkerRuntimeClosureProof RuntimeClosure);

    private sealed class WorkerEvidence
    {
        [JsonRequired] public required string Status { get; init; }
        [JsonRequired] public required string ArtifactSha256 { get; init; }
        [JsonRequired] public required string[]? Capabilities { get; init; }
        [JsonRequired] public int SideEffects { get; init; }
    }
}

public sealed class RouteLease : IDisposable
{
    private Action? _complete;

    internal RouteLease(RouteSnapshot snapshot, Action complete)
    {
        Snapshot = snapshot;
        _complete = complete;
    }

    public RouteSnapshot Snapshot { get; }
    public void Dispose() => Interlocked.Exchange(ref _complete, null)?.Invoke();
}
