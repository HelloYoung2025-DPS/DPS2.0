using System.Security.Cryptography;
using System.Text;
using Dps.ControlPlaneHost.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed partial class PostgresPolicyApprovalIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task CommitTimeActiveReleaseBindingMatchAuthorizesRecovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        // The commit-time coordination proof: the recovery commit lock
        // requires the control-plane-host release-binding baseline marker in
        // this database (advisory locks are database-local).
        await using var releaseBindingBaseline = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var prepared = await PrepareRecoverableSubmissionAsync(
            database,
            authorityTopology,
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner,
            "commit-binding-match",
            cancellationToken);
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            MatchingActiveReleaseBindingCoordinator(prepared.Snapshot));

        var authorized = await recoveryClient.AuthorizeSubmissionRecoveryAsync(
            SignRecovery(recoverySigner, prepared.Recovery),
            cancellationToken);

        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 1, cancellationToken);
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("generation")]
    [InlineData("status")]
    [Trait("Category", "Integration")]
    public async Task CommitTimeActiveReleaseBindingDriftRejectsRecoveryAndPersistsNothing(string drift)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await using var releaseBindingBaseline = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var prepared = await PrepareRecoverableSubmissionAsync(
            database,
            authorityTopology,
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner,
            "commit-binding-drift-" + drift,
            cancellationToken);
        var drifted = new StubActiveReleaseBindingRecoveryCoordinator();
        drifted.SetActive(ActiveBinding(
            prepared.Snapshot.Approval.DeviceBindingId,
            drift == "sha256" ? new string('f', 64) : prepared.Snapshot.ReleaseBomSha256,
            drift == "generation" ? 2 : 1,
            drift == "status" ? "revoked" : "active"));
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            drifted);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(
                SignRecovery(recoverySigner, prepared.Recovery),
                cancellationToken));

        var persisted = await recoveryClient.ReadSubmissionAsync(prepared.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 0, cancellationToken);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CommitTimeMissingActiveReleaseBindingRejectsRecoveryAndPersistsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await using var releaseBindingBaseline = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var prepared = await PrepareRecoverableSubmissionAsync(
            database,
            authorityTopology,
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner,
            "commit-binding-missing",
            cancellationToken);
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            new StubActiveReleaseBindingRecoveryCoordinator());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(
                SignRecovery(recoverySigner, prepared.Recovery),
                cancellationToken));

        var persisted = await recoveryClient.ReadSubmissionAsync(prepared.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 0, cancellationToken);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CommitTimeTokenMutationWithUnchangedDeclaredDigestRejectsRecoveryAndPersistsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(
            evaluationSigner, revocationSigner, fenceSigner, executorSigner,
            reconciliationSigner, recoverySigner, stateSigner);
        var prepared = await PrepareRecoverableSubmissionAsync(
            database,
            authorityTopology,
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner,
            "commit-binding-token-mutation",
            cancellationToken);
        var coordinator = MatchingActiveReleaseBindingCoordinator(prepared.Snapshot);
        var valid = ActiveBinding(
            prepared.Snapshot.Approval.DeviceBindingId,
            prepared.Snapshot.ReleaseBomSha256,
            1);
        coordinator.SetActive(valid with
        {
            ExecutionTokenBase64 = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes("mutated-policy-token")))
        });
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            coordinator);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(
                SignRecovery(recoverySigner, prepared.Recovery),
                cancellationToken));

        await AssertRecoveryCountAsync(
            database, prepared.Intent.SubmissionAttemptId, 0, cancellationToken);
    }

    [Fact, Trait("Category", "Unit")]
    public void ProductionRecoveryFactoryAcceptsOnlyTheSealedCphCapability()
    {
        var factory = Assert.Single(
            typeof(PolicyApprovalSubmissionRecoveryClient)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            static method => method.Name == "CreateProduction");
        Assert.Equal(
            typeof(ActiveReleaseBindingRecoveryCapability),
            factory.GetParameters()[^1].ParameterType);
        Assert.True(typeof(ActiveReleaseBindingRecoveryCapability).IsSealed);
        Assert.DoesNotContain(
            typeof(ActiveReleaseBindingRecoveryCapability).GetConstructors(),
            static constructor => constructor.IsPublic || constructor.IsFamily);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CommitTimeReaderExceptionRollsRecoveryBackAndPersistsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await using var releaseBindingBaseline = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var prepared = await PrepareRecoverableSubmissionAsync(
            database,
            authorityTopology,
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner,
            "commit-binding-reader-fault",
            cancellationToken);
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            new ThrowingActiveReleaseBindingRecoveryCoordinator());

        // An exception out of the final comparison propagates and the
        // transaction rolls back: no recovery row, no state transition.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(
                SignRecovery(recoverySigner, prepared.Recovery),
                cancellationToken));

        var persisted = await recoveryClient.ReadSubmissionAsync(prepared.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 0, cancellationToken);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task StoreIssuedCoordinationDoesNotDependOnPolicyDatabaseMarker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // NO control-plane-host release-binding baseline exists in the
        // policy database. Coordination belongs to the injected CPH store,
        // not to a same-database marker or a policy-side advisory lock.
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var prepared = await PrepareRecoverableSubmissionAsync(
            database,
            authorityTopology,
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner,
            "commit-binding-uncoordinated",
            cancellationToken);
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            MatchingActiveReleaseBindingCoordinator(prepared.Snapshot));

        var authorized = await recoveryClient.AuthorizeSubmissionRecoveryAsync(
            SignRecovery(recoverySigner, prepared.Recovery),
            cancellationToken);

        var persisted = await recoveryClient.ReadSubmissionAsync(prepared.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, persisted.State.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 1, cancellationToken);
    }

    private sealed record RecoverableSubmission(
        PolicyApprovalAuthoritativeSnapshot Snapshot,
        ApprovalSubmissionIntentV1 Intent,
        ApprovalSubmissionRecoveryV1 Recovery);

    private static async Task<RecoverableSubmission> PrepareRecoverableSubmissionAsync(
        PolicyApprovalTestDatabase database,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ECDsa evaluationSigner,
        ECDsa revocationSigner,
        ECDsa fenceSigner,
        ECDsa executorSigner,
        ECDsa reconciliationSigner,
        ECDsa recoverySigner,
        ECDsa stateSigner,
        string label,
        CancellationToken cancellationToken)
    {
        var (proposal, snapshot) = await IssueApprovedAsync(
            database, evaluationSigner, revocationSigner, authorityTopology, label, cancellationToken);
        var request = FenceRequest(snapshot);
        var firstIntent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        ApprovalSubmissionStateV1 pending;
        using (var client = CreateSubmissionClient(
                   database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner))
        {
            var lease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), firstIntent, cancellationToken);
            pending = (await lease.BeginSubmissionAsync(firstIntent, cancellationToken)).PendingReceipt;
            _ = await lease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
            await lease.DisposeAsync();
        }

        ApprovalSubmissionReconciliationV1 reconciliation;
        using (var reconciliationClient = CreateReconciliationClient(
                   database, authorityTopology, executorSigner, reconciliationSigner, stateSigner))
        {
            reconciliation = SignReconciliation(reconciliationSigner, Reconciliation(firstIntent, pending));
            _ = await reconciliationClient.ReconcileSubmissionAsync(reconciliation, cancellationToken);
        }

        var recovery = Recovery(
            firstIntent,
            reconciliation,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Sha256Hex("commit-binding-authorization:" + label),
            Sha256Hex("commit-binding-native:" + label));
        return new RecoverableSubmission(snapshot, firstIntent, recovery);
    }

    private static StubActiveReleaseBindingRecoveryCoordinator MatchingActiveReleaseBindingCoordinator(
        PolicyApprovalAuthoritativeSnapshot snapshot)
    {
        var coordinator = new StubActiveReleaseBindingRecoveryCoordinator();
        coordinator.SetActive(ActiveBinding(
            snapshot.Approval.DeviceBindingId,
            snapshot.ReleaseBomSha256,
            1));
        return coordinator;
    }

    private static ActiveReleaseBindingV1 ActiveBinding(
        string deviceBindingId,
        string releaseBomSha256,
        long generation,
        string status = "active")
    {
        var token = SHA256.HashData(Encoding.UTF8.GetBytes("execution-token:" + deviceBindingId + ":" + generation));
        try
        {
            var activationTokenSha256 = Convert.ToHexString(SHA256.HashData(token)).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            return new ActiveReleaseBindingV1(
                "1.0.0",
                "active.release.binding/v1",
                "control-plane-host",
                deviceBindingId,
                releaseBomSha256,
                generation,
                generation,
                Convert.ToBase64String(token),
                activationTokenSha256,
                status,
                "test-release-signer",
                "test-release-key-1",
                Sha256Hex("bom-signature:" + deviceBindingId + ":" + generation),
                now,
                "receipt_" + Sha256Hex("binding-receipt:" + deviceBindingId + ":" + generation)[..32],
                SoulId: null,
                PlatformAccountId: null,
                TraceId: null,
                IdempotencyKey: null,
                OccurredAt: now,
                PrivacyClass: "internal");
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private static async Task AssertRecoveryCountAsync(
        PolicyApprovalTestDatabase database,
        Guid submissionAttemptId,
        long expected,
        CancellationToken cancellationToken)
    {
        await using var owner = new NpgsqlConnection(database.AdminConnectionString);
        await owner.OpenAsync(cancellationToken);
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM {database.SchemaName}.approval_submission_recoveries WHERE submission_attempt_id = @submission_attempt_id",
            owner) { CommandTimeout = 5 };
        count.Parameters.AddWithValue("submission_attempt_id", submissionAttemptId);
        Assert.Equal(expected, await count.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class StubActiveReleaseBindingRecoveryCoordinator
        : IPolicyActiveReleaseBindingRecoverySource
    {
        private readonly Dictionary<string, ActiveReleaseBindingV1> _activeBindings = new(StringComparer.Ordinal);

        public void SetActive(ActiveReleaseBindingV1 binding) => _activeBindings[binding.DeviceBindingId] = binding;

        public ValueTask<IPolicyActiveReleaseBindingRecoveryScope> AcquireAsync(
            string deviceBindingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activeBindings.TryGetValue(deviceBindingId, out var binding))
            {
                throw new UnauthorizedAccessException(
                    "test recovery coordinator has no active binding for the requested device");
            }
            return ValueTask.FromResult<IPolicyActiveReleaseBindingRecoveryScope>(
                new StubActiveReleaseBindingRecoveryScope(binding));
        }
    }

    private sealed class StubActiveReleaseBindingRecoveryScope(
        ActiveReleaseBindingV1 activeBinding) : IPolicyActiveReleaseBindingRecoveryScope
    {
        public ActiveReleaseBindingV1 ActiveBinding { get; } = activeBinding;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingActiveReleaseBindingRecoveryCoordinator
        : IPolicyActiveReleaseBindingRecoverySource
    {
        public ValueTask<IPolicyActiveReleaseBindingRecoveryScope> AcquireAsync(
            string deviceBindingId,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("active release binding read faulted at commit time");
    }
}
