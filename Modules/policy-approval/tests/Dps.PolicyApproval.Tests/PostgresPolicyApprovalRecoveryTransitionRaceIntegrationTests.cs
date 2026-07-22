using System.Diagnostics;
using System.Security.Cryptography;
using Dps.ControlPlaneHost;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using Xunit;

namespace Dps.PolicyApproval.Tests;

/// <summary>
/// REAL_POSTGRESQL (18.4, DPS_TEST_POSTGRES) deterministic concurrency proof
/// for the recovery commit linearization point: RecoverAsync's transaction
/// takes the per-device pg_advisory_xact_lock keyed
/// hashtextextended('release-binding:' || device_binding_id, 0) — the exact
/// key control-plane-host's journal append/fence functions take — before the
/// final active-binding comparison, and every control-plane-host transition
/// acquires the same lock through the store transition scope BEFORE entering
/// the authority's in-process gate. One real
/// <see cref="ActiveReleaseBindingAuthority"/> backed by one real
/// <see cref="PostgresReleaseBindingTruthStore"/> is the composition-fixed
/// reader of the real <see cref="PolicyApprovalSubmissionRecoveryClient"/>
/// pipeline (the same composition as the same-instance suite).
///
/// Determinism mechanism: a raw blocker connection first takes the device's
/// advisory lock in an open transaction. The intended race winner is started
/// next and proven queued — pg_locks shows its advisory lock request
/// waiting — before the loser is started and proven queued behind it.
/// PostgreSQL grants an exclusive advisory lock to waiters in request order,
/// so releasing the blocker replays exactly the queued order: either the
/// transition commits first and the recovery must roll back with zero rows,
/// or the recovery commits first and the transition lands only afterwards.
/// The orchestration itself asserts the recovery really waits on the shared
/// lock (it can never be observed completing early), and every path stays
/// far inside the 5-second command/lock timeouts.
///
/// Deadlock freedom (lock order): the recovery holds the advisory lock while
/// the reader briefly enters the authority gate; the transition holds the
/// advisory lock (via its scope) before the gate and never waits on the
/// advisory lock while holding the gate. No path holds the gate while
/// waiting on the advisory lock, so the hold-and-wait cycle the lock order
/// forbids cannot form; these tests would hit their timeouts if it did.
/// </summary>
public sealed partial class PostgresPolicyApprovalIntegrationTests
{
    private const string DeviceC = "db_cccccccccccccccccccccccccccccccc";

    [Fact, Trait("Category", "Integration")]
    public async Task ActivationWinningTransitionRaceRollsRecoveryBackWithZeroRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-activation-wins", 1, cancellationToken);
        var (bom2, token2) = context.BomSigner.SignBom("race-activation-wins-bom-2", 2, context.Bom(1));
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-activation-wins-authorization"),
            Sha256Hex("race-activation-wins-native")));
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceA, cancellationToken);
        await using var observer = new NpgsqlConnection(context.Database.AdminConnectionString);
        await observer.OpenAsync(cancellationToken);

        // Queue the activation first, the recovery behind it; releasing the
        // blocker then commits the transition before the recovery compares.
        var transitionTask = Task.Run(
            () => context.Authority.Activate(DeviceA, bom2, token2),
            cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 1, cancellationToken);
        var recoveryTask = context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 2, cancellationToken);
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recoveryTask);
        var transitionReceipt = await transitionTask;
        Assert.Equal("activation", transitionReceipt.ReceiptKind);
        Assert.Equal(2, transitionReceipt.Sequence);

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 0, cancellationToken);
        var persisted = await context.RecoveryClient.ReadSubmissionAsync(
            context.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        Assert.True(context.Authority.TryReadActive(DeviceA, out var active));
        Assert.NotNull(active);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(bom2), active!.ReleaseBomSha256);
        Assert.Equal(2, active.Generation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RecoveryWinningActivationRaceCommitsThenTransitionLandsAfter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-recovery-wins-activation", 1, cancellationToken);
        var (bom2, token2) = context.BomSigner.SignBom("race-recovery-wins-activation-bom-2", 2, context.Bom(1));
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-recovery-wins-activation-authorization"),
            Sha256Hex("race-recovery-wins-activation-native")));
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceA, cancellationToken);
        await using var observer = new NpgsqlConnection(context.Database.AdminConnectionString);
        await observer.OpenAsync(cancellationToken);

        // Queue the recovery first, the activation behind it: the recovery
        // compares and commits under the lock; the transition's append may
        // land only after that commit.
        var recoveryTask = context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 1, cancellationToken);
        var transitionTask = Task.Run(
            () => context.Authority.Activate(DeviceA, bom2, token2),
            cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 2, cancellationToken);
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();

        var authorized = await recoveryTask;
        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        var transitionReceipt = await transitionTask;
        Assert.Equal("activation", transitionReceipt.ReceiptKind);
        Assert.Equal(2, transitionReceipt.Sequence);

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 1, cancellationToken);
        Assert.True(context.Authority.TryReadActive(DeviceA, out var active));
        Assert.NotNull(active);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(bom2), active!.ReleaseBomSha256);
        Assert.Equal(2, active.Generation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RevocationWinningTransitionRaceRollsRecoveryBackWithZeroRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-revocation-wins", 1, cancellationToken);
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-revocation-wins-authorization"),
            Sha256Hex("race-revocation-wins-native")));
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceA, cancellationToken);
        await using var observer = new NpgsqlConnection(context.Database.AdminConnectionString);
        await observer.OpenAsync(cancellationToken);

        var transitionTask = Task.Run(
            () => context.Authority.Revoke(DeviceA, context.LiveReleaseBomGeneration),
            cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 1, cancellationToken);
        var recoveryTask = context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 2, cancellationToken);
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();

        await Assert.ThrowsAsync<ActiveReleaseBindingException>(() => recoveryTask);
        var transitionReceipt = await transitionTask;
        Assert.Equal("revocation", transitionReceipt.ReceiptKind);
        Assert.Equal(2, transitionReceipt.Sequence);

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 0, cancellationToken);
        var persisted = await context.RecoveryClient.ReadSubmissionAsync(
            context.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        Assert.False(context.Authority.TryReadActive(DeviceA, out _));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RecoveryWinningRevocationRaceCommitsThenTransitionLandsAfter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-recovery-wins-revocation", 1, cancellationToken);
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-recovery-wins-revocation-authorization"),
            Sha256Hex("race-recovery-wins-revocation-native")));
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceA, cancellationToken);
        await using var observer = new NpgsqlConnection(context.Database.AdminConnectionString);
        await observer.OpenAsync(cancellationToken);

        var recoveryTask = context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 1, cancellationToken);
        var transitionTask = Task.Run(
            () => context.Authority.Revoke(DeviceA, context.LiveReleaseBomGeneration),
            cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 2, cancellationToken);
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();

        var authorized = await recoveryTask;
        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        var transitionReceipt = await transitionTask;
        Assert.Equal("revocation", transitionReceipt.ReceiptKind);
        Assert.Equal(2, transitionReceipt.Sequence);

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 1, cancellationToken);
        Assert.False(context.Authority.TryReadActive(DeviceA, out _));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RollbackWinningTransitionRaceRollsRecoveryBackWithZeroRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-rollback-wins", 2, cancellationToken);
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-rollback-wins-authorization"),
            Sha256Hex("race-rollback-wins-native")));
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceA, cancellationToken);
        await using var observer = new NpgsqlConnection(context.Database.AdminConnectionString);
        await observer.OpenAsync(cancellationToken);

        var transitionTask = Task.Run(
            () => context.Authority.Rollback(DeviceA, context.Token(1)),
            cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 1, cancellationToken);
        var recoveryTask = context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 2, cancellationToken);
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recoveryTask);
        var transitionReceipt = await transitionTask;
        Assert.Equal("rollback", transitionReceipt.ReceiptKind);
        Assert.Equal(3, transitionReceipt.Sequence);

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 0, cancellationToken);
        var persisted = await context.RecoveryClient.ReadSubmissionAsync(
            context.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        Assert.True(context.Authority.TryReadActive(DeviceA, out var active));
        Assert.NotNull(active);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(context.Bom(1)), active!.ReleaseBomSha256);
        Assert.Equal(3, active.Generation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RecoveryWinningRollbackRaceCommitsThenTransitionLandsAfter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-recovery-wins-rollback", 2, cancellationToken);
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-recovery-wins-rollback-authorization"),
            Sha256Hex("race-recovery-wins-rollback-native")));
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceA, cancellationToken);
        await using var observer = new NpgsqlConnection(context.Database.AdminConnectionString);
        await observer.OpenAsync(cancellationToken);

        var recoveryTask = context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 1, cancellationToken);
        var transitionTask = Task.Run(
            () => context.Authority.Rollback(DeviceA, context.Token(1)),
            cancellationToken);
        await WaitForAdvisoryWaitersAsync(observer, DeviceA, 2, cancellationToken);
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();

        var authorized = await recoveryTask;
        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        var transitionReceipt = await transitionTask;
        Assert.Equal("rollback", transitionReceipt.ReceiptKind);
        Assert.Equal(3, transitionReceipt.Sequence);

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 1, cancellationToken);
        Assert.True(context.Authority.TryReadActive(DeviceA, out var active));
        Assert.NotNull(active);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(context.Bom(1)), active!.ReleaseBomSha256);
        Assert.Equal(3, active.Generation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DifferentDevicesDoNotBlockRecoveryOrTransition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-cross-device", 1, cancellationToken);
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-cross-device-authorization"),
            Sha256Hex("race-cross-device-native")));
        var (bomC, tokenC) = context.BomSigner.SignBom("race-cross-device-bom-c", 1, null);
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        // A second device's lock is held for the whole test: any accidental
        // global or cross-device key derivation would deadlock both
        // contenders below into their 5-second timeouts.
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceB, cancellationToken);

        var recoveryTask = context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);
        var foreignActivationTask = Task.Run(
            () => context.Authority.Activate(DeviceC, bomC, tokenC),
            cancellationToken);

        var authorized = await recoveryTask;
        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        var foreignReceipt = await foreignActivationTask;
        Assert.Equal("activation", foreignReceipt.ReceiptKind);
        Assert.Equal(1, foreignReceipt.Sequence);

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 1, cancellationToken);
        Assert.True(context.Authority.TryReadActive(DeviceC, out var activeC));
        Assert.NotNull(activeC);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(bomC), activeC!.ReleaseBomSha256);
        Assert.Equal(1, activeC.Generation);
        Assert.False(context.Authority.TryReadActive(DeviceB, out _));
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ReleaseBindingTransitionLockWaitTimeoutPersistsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RecoveryRaceContext.CreateAsync(
            "race-lock-timeout", 1, cancellationToken);
        var recovery = SignRecovery(context.RecoverySigner, RecoveryPinnedToLiveBinding(
            context.Intent,
            context.Reconciliation,
            context.LiveReleaseBomSha256,
            context.LiveReleaseBomGeneration,
            Sha256Hex("race-lock-timeout-authorization"),
            Sha256Hex("race-lock-timeout-native")));
        await using var blocker = new NpgsqlConnection(context.Database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        // Held past the 5-second bounded command timeout: the recovery's
        // lock acquisition is cancelled and the transaction rolls back.
        var blockerTransaction = await HoldReleaseBindingAdvisoryLockAsync(blocker, DeviceA, cancellationToken);

        await Assert.ThrowsAnyAsync<NpgsqlException>(() =>
            context.RecoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken));

        await AssertRecoveryCountAsync(context.Database, context.Intent.SubmissionAttemptId, 0, cancellationToken);
        var persisted = await context.RecoveryClient.ReadSubmissionAsync(
            context.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        await blockerTransaction.RollbackAsync(cancellationToken);
        await blockerTransaction.DisposeAsync();
    }

    /// <summary>
    /// Takes the per-device release-binding advisory lock on a raw
    /// connection's open transaction — the byte-identical key expression the
    /// contenders use — so the test controls exactly when the queue ahead of
    /// them drains.
    /// </summary>
    private static async Task<NpgsqlTransaction> HoldReleaseBindingAdvisoryLockAsync(
        NpgsqlConnection blocker,
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        var transaction = await blocker.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended('release-binding:' || @device_binding_id, 0))",
            blocker, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    /// <summary>
    /// Polls pg_locks until at least <paramref name="expectedWaiters"/>
    /// sessions are queued (granted = false) on the device's release-binding
    /// advisory key — the deterministic proof that each contender has reached
    /// the shared serialization point in the intended order before the
    /// blocker releases it. pg_locks splits the bigint advisory key into its
    /// high/low 32-bit halves, recomputed here with the same key derivation.
    /// </summary>
    private static async Task WaitForAdvisoryWaitersAsync(
        NpgsqlConnection observer,
        string deviceBindingId,
        int expectedWaiters,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            await using var command = new NpgsqlCommand(
                """
                WITH advisory_key AS (
                    SELECT pg_catalog.hashtextextended('release-binding:' || @device_binding_id, 0) AS v)
                SELECT count(*)
                FROM pg_catalog.pg_locks locks
                CROSS JOIN advisory_key
                WHERE locks.locktype = 'advisory'
                  AND NOT locks.granted
                  AND locks.classid::bigint = ((advisory_key.v >> 32) & 4294967295)
                  AND locks.objid::bigint = (advisory_key.v & 4294967295)
                """,
                observer) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
            var waiters = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("pg_locks waiter probe returned no row."));
            if (waiters >= expectedWaiters)
            {
                return;
            }
            if (deadline.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new InvalidOperationException(
                    $"Timed out waiting for {expectedWaiters} queued advisory-lock waiter(s) on '{deviceBindingId}' (observed {waiters}).");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    /// <summary>
    /// One deterministic race fixture: a fresh policy database and a fresh
    /// control-plane-host release-binding schema in the SAME PostgreSQL
    /// database, one real authority over the real store with
    /// <paramref name="activatedBomCount"/> BOMs activated in chain on
    /// DeviceA, a durable RECONCILED_NOT_SUBMITTED predecessor produced by
    /// the production lifecycle path and pinned to the live binding, and the
    /// real recovery client composition with the same authority injected as
    /// the composition-fixed reader.
    /// </summary>
    private sealed class RecoveryRaceContext : IAsyncDisposable
    {
        private readonly List<ECDsa> _signers;
        private readonly List<byte[]> _boms;
        private readonly List<string> _tokens;

        private RecoveryRaceContext(
            PolicyApprovalTestDatabase database,
            SameInstanceReleaseBindingDatabase bindings,
            ActiveReleaseBindingAuthority authority,
            SameInstanceBomSigner bomSigner,
            ECDsa recoverySigner,
            List<ECDsa> signers,
            PolicyApprovalSubmissionRecoveryClient recoveryClient,
            ApprovalSubmissionIntentV1 intent,
            ApprovalSubmissionReconciliationV1 reconciliation,
            string liveReleaseBomSha256,
            long liveReleaseBomGeneration,
            List<byte[]> boms,
            List<string> tokens)
        {
            Database = database;
            Bindings = bindings;
            Authority = authority;
            BomSigner = bomSigner;
            RecoverySigner = recoverySigner;
            _signers = signers;
            RecoveryClient = recoveryClient;
            Intent = intent;
            Reconciliation = reconciliation;
            LiveReleaseBomSha256 = liveReleaseBomSha256;
            LiveReleaseBomGeneration = liveReleaseBomGeneration;
            _boms = boms;
            _tokens = tokens;
        }

        internal PolicyApprovalTestDatabase Database { get; }
        internal SameInstanceReleaseBindingDatabase Bindings { get; }
        internal ActiveReleaseBindingAuthority Authority { get; }
        internal SameInstanceBomSigner BomSigner { get; }
        internal ECDsa RecoverySigner { get; }
        internal PolicyApprovalSubmissionRecoveryClient RecoveryClient { get; }
        internal ApprovalSubmissionIntentV1 Intent { get; }
        internal ApprovalSubmissionReconciliationV1 Reconciliation { get; }
        internal string LiveReleaseBomSha256 { get; }
        internal long LiveReleaseBomGeneration { get; }

        internal byte[] Bom(int oneBasedIndex) => _boms[oneBasedIndex - 1];

        internal string Token(int oneBasedIndex) => _tokens[oneBasedIndex - 1];

        internal static async Task<RecoveryRaceContext> CreateAsync(
            string label,
            int activatedBomCount,
            CancellationToken cancellationToken)
        {
            var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
            SameInstanceReleaseBindingDatabase? bindings = null;
            SameInstanceBomSigner? bomSigner = null;
            PolicyApprovalSubmissionRecoveryClient? recoveryClient = null;
            var signers = new List<ECDsa>();
            try
            {
                bindings = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
                ECDsa CreateSigner()
                {
                    var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    signers.Add(signer);
                    return signer;
                }
                var evaluationSigner = CreateSigner();
                var revocationSigner = CreateSigner();
                var fenceSigner = CreateSigner();
                var executorSigner = CreateSigner();
                var reconciliationSigner = CreateSigner();
                var recoverySigner = CreateSigner();
                var stateSigner = CreateSigner();
                var authorityTopology = SubmissionTopology(
                    evaluationSigner, revocationSigner, fenceSigner, executorSigner,
                    reconciliationSigner, recoverySigner, stateSigner);
                bomSigner = new SameInstanceBomSigner();
                var releaseBindingStore = bindings.CreateStore();
                var authority = new ActiveReleaseBindingAuthority(
                    [bomSigner.TrustKey], releaseBindingStore, () => SameInstanceNow);
                var boms = new List<byte[]>();
                var tokens = new List<string>();
                for (var index = 1; index <= activatedBomCount; index++)
                {
                    var (bom, token) = bomSigner.SignBom(
                        label + "-bom-" + index,
                        index,
                        index == 1 ? null : boms[^1]);
                    authority.Activate(DeviceA, bom, token);
                    boms.Add(bom);
                    tokens.Add(token);
                }
                var liveReleaseBomSha256 = SameInstanceBomSigner.Sha256Hex(boms[^1]);
                var prepared = await PrepareSameInstanceRecoverableSubmissionAsync(
                    database,
                    authorityTopology,
                    evaluationSigner,
                    revocationSigner,
                    fenceSigner,
                    executorSigner,
                    reconciliationSigner,
                    recoverySigner,
                    stateSigner,
                    liveReleaseBomSha256,
                    activatedBomCount,
                    label,
                    cancellationToken);
                recoveryClient = CreateRecoveryClient(
                    database,
                    authorityTopology,
                    executorSigner,
                    recoverySigner,
                    stateSigner,
                    authority.RecoveryCapability);
                return new RecoveryRaceContext(
                    database,
                    bindings,
                    authority,
                    bomSigner,
                    recoverySigner,
                    signers,
                    recoveryClient,
                    prepared.Intent,
                    prepared.Reconciliation,
                    liveReleaseBomSha256,
                    activatedBomCount,
                    boms,
                    tokens);
            }
            catch
            {
                recoveryClient?.Dispose();
                bomSigner?.Dispose();
                foreach (var signer in signers)
                {
                    signer.Dispose();
                }
                if (bindings is not null)
                {
                    await bindings.DisposeAsync();
                }
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            RecoveryClient.Dispose();
            BomSigner.Dispose();
            foreach (var signer in _signers)
            {
                signer.Dispose();
            }
            await Bindings.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
