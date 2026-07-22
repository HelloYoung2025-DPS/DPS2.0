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
            MatchingActiveReleaseBindingReader(prepared.Snapshot));

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
        var drifted = new StubActiveReleaseBindingReader();
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
            new StubActiveReleaseBindingReader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(
                SignRecovery(recoverySigner, prepared.Recovery),
                cancellationToken));

        var persisted = await recoveryClient.ReadSubmissionAsync(prepared.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 0, cancellationToken);
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

    private static StubActiveReleaseBindingReader MatchingActiveReleaseBindingReader(
        PolicyApprovalAuthoritativeSnapshot snapshot)
    {
        var reader = new StubActiveReleaseBindingReader();
        reader.SetActive(ActiveBinding(snapshot.Approval.DeviceBindingId, snapshot.ReleaseBomSha256, 1));
        return reader;
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

    private sealed class StubActiveReleaseBindingReader : IActiveReleaseBindingReader
    {
        private readonly Dictionary<string, ActiveReleaseBindingV1> _activeBindings = new(StringComparer.Ordinal);

        public void SetActive(ActiveReleaseBindingV1 binding) => _activeBindings[binding.DeviceBindingId] = binding;

        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding)
            => _activeBindings.TryGetValue(deviceBindingId, out binding);
    }
}
