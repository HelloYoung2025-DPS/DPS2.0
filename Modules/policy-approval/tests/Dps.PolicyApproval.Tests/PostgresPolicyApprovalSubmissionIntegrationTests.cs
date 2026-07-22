using System.Security.Cryptography;
using Dps.ControlPlaneHost.Contracts;
using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed partial class PostgresPolicyApprovalIntegrationTests
{
    [Theory]
    [InlineData("PENDING_AFTER_COMMIT_BEFORE_NATIVE")]
    [InlineData("NATIVE_AFTER_FLUSH_BEFORE_ACK")]
    [InlineData("ACK_VALIDATED_BEFORE_POLICY_TRANSITION")]
    [Trait("Category", "Integration")]
    public async Task DurablePendingCrashWindowsRemainUnknownAndBlockRestart(string crashWindow)
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
        var (proposal, snapshot) = await IssueApprovedAsync(database, evaluationSigner, revocationSigner, authorityTopology, "pending-" + crashWindow, cancellationToken);
        var fenceRequest = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, fenceRequest));

        using (var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner))
        {
            var lease = await client.AcquireAsync(fenceRequest, SignFenceAuthorization(fenceSigner, fenceRequest), intent, cancellationToken);
            Assert.Equal(lease.Fence, await lease.RevalidateForNativeDispatchAsync(cancellationToken));
            var begin = await lease.BeginSubmissionAsync(intent, cancellationToken);
            Assert.True(begin.MaySubmit);
            Assert.Equal(PolicyApprovalSubmissionBeginDisposition.Inserted, begin.Disposition);
            Assert.Equal(ApprovalSubmissionStateV1.SubmissionPending, begin.PendingReceipt.State);

            if (crashWindow == "NATIVE_AFTER_FLUSH_BEFORE_ACK")
            {
                var unknown = await lease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
                Assert.Equal(ApprovalSubmissionStateV1.UnknownSubmission, unknown.State);
                await lease.DisposeAsync();
            }
            else if (crashWindow == "ACK_VALIDATED_BEFORE_POLICY_TRANSITION")
            {
                var unknown = await lease.QuarantineUnknownSubmissionAsync("AUTHORITY_TRANSITION_UNCERTAIN", cancellationToken);
                Assert.Equal(ApprovalSubmissionStateV1.UnknownSubmission, unknown.State);
                await lease.DisposeAsync();
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => lease.DisposeAsync().AsTask());
            }
        }

        using var restarted = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var persisted = await restarted.ReadSubmissionAsync(intent.SubmissionAttemptId, cancellationToken);
        Assert.True(persisted.RequiresReconciliation);
        Assert.Contains(persisted.State.State, new[] { ApprovalSubmissionStateV1.SubmissionPending, ApprovalSubmissionStateV1.UnknownSubmission });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.AcquireAsync(
            fenceRequest, SignFenceAuthorization(fenceSigner, fenceRequest), intent, cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DuplicateBeginReturnsSamePendingReceiptButNeverExecutionPermission()
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
        var (proposal, snapshot) = await IssueApprovedAsync(database, evaluationSigner, revocationSigner, authorityTopology, "duplicate-begin", cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var lease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken);

        var inserted = await lease.BeginSubmissionAsync(intent, cancellationToken);
        var duplicate = await lease.BeginSubmissionAsync(intent, cancellationToken);

        Assert.True(inserted.MaySubmit);
        Assert.False(duplicate.MaySubmit);
        Assert.Equal(PolicyApprovalSubmissionBeginDisposition.ExistingUnknownSubmission, duplicate.Disposition);
        Assert.Equal(inserted.PendingReceipt.SubmissionAttemptId, duplicate.PendingReceipt.SubmissionAttemptId);
        Assert.Equal(inserted.PendingReceipt.StateSha256, duplicate.PendingReceipt.StateSha256);

        await using (var executorRole = new NpgsqlConnection(database.SubmissionExecutorOptions.ExecutorConnectionString))
        {
            await executorRole.OpenAsync(cancellationToken);
            foreach (var maliciousIntent in new[]
                     {
                         "attempt.intent_json || '{\"unexpected_field\":true}'::jsonb",
                         "attempt.intent_json - 'signature_base64'"
                     })
            {
                await PolicyApprovalTestDatabase.AssertSqlStateAsync(
                    executorRole,
                    $"""
                    SELECT {database.SchemaName}.begin_approval_submission(
                        attempt.fence_id,
                        clock_timestamp() + interval '1 second',
                        {maliciousIntent},
                        attempt.intent_sha256,
                        attempt.pending_state_json,
                        attempt.pending_state_sha256)
                    FROM {database.SchemaName}.approval_submission_attempts AS attempt
                    WHERE attempt.submission_attempt_id = '{intent.SubmissionAttemptId:D}'::uuid
                    """,
                    "42501",
                    cancellationToken);
            }
        }
        _ = await lease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
        await lease.DisposeAsync();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentAcquireWaitsBehindCommandLockThenRejectsCommittedPending()
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
        var (proposal, snapshot) = await IssueApprovedAsync(
            database, evaluationSigner, revocationSigner, authorityTopology, "concurrent-acquire", cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var firstClient = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        using var secondClient = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var firstLease = await firstClient.AcquireAsync(
            request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken);

        var secondAcquire = secondClient.AcquireAsync(
            request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        Assert.False(secondAcquire.IsCompleted);

        var firstBegin = await firstLease.BeginSubmissionAsync(intent, cancellationToken);
        Assert.True(firstBegin.MaySubmit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await secondAcquire);
        _ = await firstLease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
        await firstLease.DisposeAsync();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExactDurableAcknowledgementTransitionsStateAndOldAttemptNeverReopens()
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
        var (proposal, snapshot) = await IssueApprovedAsync(database, evaluationSigner, revocationSigner, authorityTopology, "durable-ack", cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var lease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken);
        var pending = (await lease.BeginSubmissionAsync(intent, cancellationToken)).PendingReceipt;
        var acknowledgement = SignAcknowledgement(executorSigner, Acknowledgement(intent, pending));

        var acknowledged = await lease.AcknowledgeSubmissionAsync(acknowledgement, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.SubmissionAcknowledged, acknowledged.State);
        Assert.Equal(ApprovalSubmissionLifecycleBinding.ComputeAcknowledgementSha256(acknowledgement), acknowledged.EvidenceSha256);
        await lease.DisposeAsync();

        using var restarted = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        Assert.Equal(ApprovalSubmissionStateV1.SubmissionAcknowledged,
            (await restarted.ReadSubmissionAsync(intent.SubmissionAttemptId, cancellationToken)).State.State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.AcquireAsync(
            request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentAcknowledgementAndReconciliationCommitExactlyOneTerminalBranch()
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
        var (proposal, snapshot) = await IssueApprovedAsync(database, evaluationSigner, revocationSigner, authorityTopology, "terminal-race", cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        using var reconciliationClient = CreateReconciliationClient(database, authorityTopology, executorSigner, reconciliationSigner, stateSigner);
        var lease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken);
        var pending = (await lease.BeginSubmissionAsync(intent, cancellationToken)).PendingReceipt;
        var acknowledgement = SignAcknowledgement(executorSigner, Acknowledgement(intent, pending));
        var reconciliation = SignReconciliation(reconciliationSigner, Reconciliation(intent, pending));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var blocker = new NpgsqlConnection(database.AdminConnectionString);
        await blocker.OpenAsync(cancellationToken);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(cancellationToken);
        await using (var holdCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
                         blocker,
                         blockerTransaction) { CommandTimeout = 5 })
        {
            holdCommand.Parameters.AddWithValue("lock_key", "submission-command:" + intent.CommandId.ToString("N"));
            _ = await holdCommand.ExecuteScalarAsync(cancellationToken);
        }

        var acknowledgementTask = CaptureTransitionAsync(async () =>
        {
            await start.Task.WaitAsync(cancellationToken);
            _ = await lease.AcknowledgeSubmissionAsync(acknowledgement, cancellationToken);
        });
        var reconciliationTask = CaptureTransitionAsync(async () =>
        {
            await start.Task.WaitAsync(cancellationToken);
            _ = await reconciliationClient.ReconcileSubmissionAsync(reconciliation, cancellationToken);
        });
        start.SetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        Assert.False(acknowledgementTask.IsCompleted && reconciliationTask.IsCompleted);
        await blockerTransaction.CommitAsync(cancellationToken);
        var outcomes = await Task.WhenAll(acknowledgementTask, reconciliationTask);

        Assert.Equal(1, outcomes.Count(static outcome => outcome.Success));
        Assert.All(outcomes.Where(static outcome => !outcome.Success), static outcome =>
            Assert.True(outcome.Error is UnauthorizedAccessException or InvalidDataException or PostgresException,
                $"Unexpected losing transition error: {outcome.Error}"));

        if (outcomes[0].Success)
            await lease.DisposeAsync();
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.DisposeAsync().AsTask());

        await using var owner = new NpgsqlConnection(database.AdminConnectionString);
        await owner.OpenAsync(cancellationToken);
        await using var counts = new NpgsqlCommand(
            $"""
            SELECT
                (SELECT count(*) FROM {database.SchemaName}.approval_submission_acknowledgements WHERE submission_attempt_id = @attempt_id),
                (SELECT count(*) FROM {database.SchemaName}.approval_submission_reconciliations WHERE submission_attempt_id = @attempt_id),
                (SELECT count(*) FROM {database.SchemaName}.approval_submission_recoveries WHERE submission_attempt_id = @attempt_id)
            """, owner);
        counts.Parameters.AddWithValue("attempt_id", intent.SubmissionAttemptId);
        await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var acknowledgementCount = reader.GetInt64(0);
        var reconciliationCount = reader.GetInt64(1);
        var recoveryCount = reader.GetInt64(2);
        Assert.Equal(1, acknowledgementCount + reconciliationCount);
        Assert.Equal(0, recoveryCount);

        var persisted = await client.ReadSubmissionAsync(intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(
            acknowledgementCount == 1
                ? ApprovalSubmissionStateV1.SubmissionAcknowledged
                : ApprovalSubmissionStateV1.ReconciledNotSubmitted,
            persisted.State.State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CrossScopeAndForgedReconciliationFailBeforeExactIndependentReceipt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var (proposal, snapshot) = await IssueApprovedAsync(database, evaluationSigner, revocationSigner, authorityTopology, "reconciliation-attacks", cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        using var reconciliationClient = CreateReconciliationClient(database, authorityTopology, executorSigner, reconciliationSigner, stateSigner);
        var lease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken);
        var pending = (await lease.BeginSubmissionAsync(intent, cancellationToken)).PendingReceipt;
        _ = await lease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
        await lease.DisposeAsync();

        var exact = Reconciliation(intent, pending);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reconciliationClient.ReconcileSubmissionAsync(SignReconciliation(wrongSigner, exact), cancellationToken));
        var crossScope = SignReconciliation(reconciliationSigner, exact with { SoulId = SoulB });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reconciliationClient.ReconcileSubmissionAsync(crossScope, cancellationToken));

        var reconciled = await reconciliationClient.ReconcileSubmissionAsync(SignReconciliation(reconciliationSigner, exact), cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, reconciled.State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task IndependentReconciliationAndHumanRecoveryAuthorizeOnlyFreshExactAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongRecoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var (proposal, snapshot) = await IssueApprovedAsync(database, evaluationSigner, revocationSigner, authorityTopology, "approved-recovery", cancellationToken);
        var request = FenceRequest(snapshot);
        var commandId = Guid.NewGuid();
        var firstIntent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request, commandId: commandId));
        using var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        using var reconciliationClient = CreateReconciliationClient(database, authorityTopology, executorSigner, reconciliationSigner, stateSigner);
        using var recoveryClient = CreateRecoveryClient(database, authorityTopology, executorSigner, recoverySigner, stateSigner, MatchingActiveReleaseBindingReader(snapshot));
        var firstLease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), firstIntent, cancellationToken);
        var pending = (await firstLease.BeginSubmissionAsync(firstIntent, cancellationToken)).PendingReceipt;
        _ = await firstLease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
        await firstLease.DisposeAsync();
        var reconciliation = SignReconciliation(reconciliationSigner, Reconciliation(firstIntent, pending));
        var reconciled = await reconciliationClient.ReconcileSubmissionAsync(reconciliation, cancellationToken);
        var nextAttemptId = Guid.NewGuid();
        var nextLeaseId = Guid.NewGuid();
        var nextAuthorizationSha256 = new string('8', 64);
        var nextNativeBindingSha256 = Sha256Hex("recovered-native:" + nextAttemptId);
        var recovery = Recovery(firstIntent, reconciliation, nextAttemptId, nextLeaseId, nextAuthorizationSha256, nextNativeBindingSha256);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(SignRecovery(wrongRecoverySigner, recovery), cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(
                SignRecovery(recoverySigner, recovery with { NextSubmissionAttemptId = recovery.SubmissionAttemptId }),
                cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(
                SignRecovery(recoverySigner, recovery with { NextLeaseId = recovery.PreviousLeaseId }),
                cancellationToken));
        var authorized = await recoveryClient.AuthorizeSubmissionRecoveryAsync(SignRecovery(recoverySigner, recovery), cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        Assert.Equal(reconciled.StateSha256, authorized.PredecessorStateSha256);

        var secondIntent = SignSubmissionIntent(executorSigner, SubmissionIntent(
            snapshot, proposal, request,
            submissionAttemptId: nextAttemptId,
            commandId: commandId,
            leaseId: nextLeaseId,
            attempt: 2,
            releaseBomGeneration: 1,
            executionAuthorizationSha256: nextAuthorizationSha256,
            nativeRequestBindingSha256: nextNativeBindingSha256));
        var secondLease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), secondIntent, cancellationToken);
        var secondBegin = await secondLease.BeginSubmissionAsync(secondIntent, cancellationToken);
        Assert.True(secondBegin.MaySubmit);
        Assert.NotEqual(firstIntent.SubmissionAttemptId, secondBegin.PendingReceipt.SubmissionAttemptId);
        _ = await secondLease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
        await secondLease.DisposeAsync();

        using var restarted = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.AcquireAsync(
            request,
            SignFenceAuthorization(fenceSigner, request),
            secondIntent,
            cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RecoveryCannotBeReusedAcrossCommandApprovalOrIdentityScope()
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
        var (proposal, snapshot) = await IssueApprovedForScopeAsync(
            database, evaluationSigner, revocationSigner, authorityTopology, "recovery-scope-a",
            SoulA, DeviceA, AccountA, cancellationToken);
        var request = FenceRequest(snapshot);
        var commandId = Guid.NewGuid();
        var firstIntent = SignSubmissionIntent(executorSigner, SubmissionIntent(
            snapshot, proposal, request, commandId: commandId));
        using var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        using var reconciliationClient = CreateReconciliationClient(database, authorityTopology, executorSigner, reconciliationSigner, stateSigner);
        using var recoveryClient = CreateRecoveryClient(database, authorityTopology, executorSigner, recoverySigner, stateSigner, MatchingActiveReleaseBindingReader(snapshot));
        var firstLease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), firstIntent, cancellationToken);
        var pending = (await firstLease.BeginSubmissionAsync(firstIntent, cancellationToken)).PendingReceipt;
        _ = await firstLease.QuarantineUnknownSubmissionAsync("PROCESS_CRASH", cancellationToken);
        await firstLease.DisposeAsync();
        var reconciliation = SignReconciliation(reconciliationSigner, Reconciliation(firstIntent, pending));
        _ = await reconciliationClient.ReconcileSubmissionAsync(reconciliation, cancellationToken);
        var nextAttemptId = Guid.NewGuid();
        var nextLeaseId = Guid.NewGuid();
        var nextAuthorizationSha256 = Sha256Hex("recovery-scope-authorization:" + nextAttemptId);
        var nextNativeBindingSha256 = Sha256Hex("recovery-scope-native:" + nextAttemptId);
        var recovery = SignRecovery(recoverySigner, Recovery(
            firstIntent,
            reconciliation,
            nextAttemptId,
            nextLeaseId,
            nextAuthorizationSha256,
            nextNativeBindingSha256));
        _ = await recoveryClient.AuthorizeSubmissionRecoveryAsync(recovery, cancellationToken);

        var wrongCommandIntent = SignSubmissionIntent(executorSigner, SubmissionIntent(
            snapshot,
            proposal,
            request,
            submissionAttemptId: nextAttemptId,
            commandId: Guid.NewGuid(),
            leaseId: nextLeaseId,
            attempt: 2,
            releaseBomGeneration: 1,
            executionAuthorizationSha256: nextAuthorizationSha256,
            nativeRequestBindingSha256: nextNativeBindingSha256));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.AcquireAsync(
            request,
            SignFenceAuthorization(fenceSigner, request),
            wrongCommandIntent,
            cancellationToken));

        var (otherProposal, otherSnapshot) = await IssueApprovedForScopeAsync(
            database, evaluationSigner, revocationSigner, authorityTopology, "recovery-scope-b",
            SoulB, DeviceB, AccountB, cancellationToken);
        var otherRequest = FenceRequest(otherSnapshot);
        var crossIdentityIntent = SignSubmissionIntent(executorSigner, SubmissionIntent(
            otherSnapshot,
            otherProposal,
            otherRequest,
            submissionAttemptId: nextAttemptId,
            commandId: commandId,
            leaseId: nextLeaseId,
            attempt: 2,
            releaseBomGeneration: 1,
            executionAuthorizationSha256: nextAuthorizationSha256,
            nativeRequestBindingSha256: nextNativeBindingSha256));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.AcquireAsync(
            otherRequest,
            SignFenceAuthorization(fenceSigner, otherRequest),
            crossIdentityIntent,
            cancellationToken));

        await using var owner = new NpgsqlConnection(database.AdminConnectionString);
        await owner.OpenAsync(cancellationToken);
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM {database.SchemaName}.approval_submission_attempts",
            owner) { CommandTimeout = 5 };
        Assert.Equal(1L, await count.ExecuteScalarAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FutureDatedSignedAcknowledgementReconciliationAndRecoveryFailAtLockedDatabaseClock()
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
        var (proposal, snapshot) = await IssueApprovedAsync(
            database, evaluationSigner, revocationSigner, authorityTopology, "future-lifecycle", cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var client = CreateSubmissionClient(database, authorityTopology, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        using var reconciliationClient = CreateReconciliationClient(database, authorityTopology, executorSigner, reconciliationSigner, stateSigner);
        using var recoveryClient = CreateRecoveryClient(database, authorityTopology, executorSigner, recoverySigner, stateSigner, MatchingActiveReleaseBindingReader(snapshot));
        var lease = await client.AcquireAsync(request, SignFenceAuthorization(fenceSigner, request), intent, cancellationToken);
        var pending = (await lease.BeginSubmissionAsync(intent, cancellationToken)).PendingReceipt;
        var future = DateTimeOffset.UtcNow.AddYears(1);

        var futureAcknowledgement = SignAcknowledgement(executorSigner, Acknowledgement(intent, pending) with
        {
            OccurredAt = future,
            ValidUntil = future.AddMinutes(1)
        });
        var acknowledgementError = await Assert.ThrowsAsync<PostgresException>(() =>
            lease.AcknowledgeSubmissionAsync(futureAcknowledgement, cancellationToken));
        Assert.Equal("42501", acknowledgementError.SqlState);

        var futureReconciliation = SignReconciliation(reconciliationSigner, Reconciliation(intent, pending) with
        {
            OccurredAt = future,
            ValidUntil = future.AddMinutes(4)
        });
        var reconciliationError = await Assert.ThrowsAsync<PostgresException>(() =>
            reconciliationClient.ReconcileSubmissionAsync(futureReconciliation, cancellationToken));
        Assert.Equal("42501", reconciliationError.SqlState);

        var current = DateTimeOffset.UtcNow;
        var expiringAcknowledgement = SignAcknowledgement(executorSigner, Acknowledgement(intent, pending) with
        {
            OccurredAt = current,
            ValidUntil = current.AddMilliseconds(250)
        });
        await using (var blocker = new NpgsqlConnection(database.AdminConnectionString))
        {
            await blocker.OpenAsync(cancellationToken);
            await using var blockerTransaction = await blocker.BeginTransactionAsync(cancellationToken);
            await using (var holdCommand = new NpgsqlCommand(
                             "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
                             blocker,
                             blockerTransaction) { CommandTimeout = 5 })
            {
                holdCommand.Parameters.AddWithValue("lock_key", "submission-command:" + intent.CommandId.ToString("N"));
                _ = await holdCommand.ExecuteScalarAsync(cancellationToken);
            }
            var expiringAcknowledgementTask = lease.AcknowledgeSubmissionAsync(
                expiringAcknowledgement,
                cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            Assert.False(expiringAcknowledgementTask.IsCompleted);
            await blockerTransaction.CommitAsync(cancellationToken);
            var expiredError = await Assert.ThrowsAsync<PostgresException>(async () =>
                await expiringAcknowledgementTask);
            Assert.Equal("42501", expiredError.SqlState);
        }

        var reconciliation = SignReconciliation(reconciliationSigner, Reconciliation(intent, pending));
        _ = await reconciliationClient.ReconcileSubmissionAsync(reconciliation, cancellationToken);
        var futureRecovery = SignRecovery(recoverySigner, Recovery(
            intent,
            reconciliation,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Sha256Hex("future-recovery-authorization"),
            Sha256Hex("future-recovery-native")) with
        {
            OccurredAt = future,
            ValidUntil = future.AddMinutes(4)
        });
        var recoveryError = await Assert.ThrowsAsync<PostgresException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(futureRecovery, cancellationToken));
        Assert.Equal("42501", recoveryError.SqlState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.DisposeAsync().AsTask());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RuntimeCannotBypassSecurityDefinerOrMutateSubmissionTables()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await using var runtime = new NpgsqlConnection(database.SubmissionExecutorOptions.ExecutorConnectionString);
        await runtime.OpenAsync(cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            runtime,
            $"INSERT INTO {database.SchemaName}.approval_submission_attempts DEFAULT VALUES",
            "42501",
            cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            runtime,
            $"SELECT {database.SchemaName}.assert_submission_executor_role()",
            "42501",
            cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            runtime,
            $"SELECT {database.SchemaName}.reconcile_approval_submission(NULL::jsonb, NULL::text, NULL::jsonb, NULL::text)",
            "42501",
            cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            runtime,
            $"SELECT {database.SchemaName}.recover_approval_submission(NULL::jsonb, NULL::text, NULL::jsonb, NULL::text)",
            "42501",
            cancellationToken);

        await using var reconciliation = new NpgsqlConnection(database.SubmissionReconciliationOptions.ReconciliationConnectionString);
        await reconciliation.OpenAsync(cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            reconciliation,
            $"SELECT {database.SchemaName}.acknowledge_approval_submission(NULL::jsonb, NULL::text, NULL::jsonb, NULL::text)",
            "42501",
            cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            reconciliation,
            $"SELECT {database.SchemaName}.recover_approval_submission(NULL::jsonb, NULL::text, NULL::jsonb, NULL::text)",
            "42501",
            cancellationToken);

        await using var recovery = new NpgsqlConnection(database.SubmissionRecoveryOptions.RecoveryConnectionString);
        await recovery.OpenAsync(cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            recovery,
            $"SELECT {database.SchemaName}.reconcile_approval_submission(NULL::jsonb, NULL::text, NULL::jsonb, NULL::text)",
            "42501",
            cancellationToken);

        await using var owner = new NpgsqlConnection(database.AdminConnectionString);
        await owner.OpenAsync(cancellationToken);
        await using (var removedLegacyBypass = new NpgsqlCommand(
                         $"""
                         SELECT
                             to_regprocedure('{database.SchemaName}.begin_approval_submission(uuid,jsonb,text,jsonb,text)') IS NULL,
                             to_regprocedure('{database.SchemaName}.assert_submission_runtime_role()') IS NULL
                         """, owner) { CommandTimeout = 5 })
        await using (var removed = await removedLegacyBypass.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await removed.ReadAsync(cancellationToken));
            Assert.True(removed.GetBoolean(0));
            Assert.True(removed.GetBoolean(1));
        }

        await using (var transferFunctionOwnership = new NpgsqlCommand(
                         $"""
                         ALTER FUNCTION {database.SchemaName}.serialize_policy_runtime_revision() OWNER TO {database.RuntimeRoleName};
                         ALTER FUNCTION {database.SchemaName}.begin_approval_submission(uuid, timestamptz, jsonb, text, jsonb, text) OWNER TO {database.SubmissionExecutorRoleName};
                         """, owner) { CommandTimeout = 5 })
            await transferFunctionOwnership.ExecuteNonQueryAsync(cancellationToken);

        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        using var runtimeService = database.CreateService(
            evaluationSigner,
            revocationSigner,
            authorityTopology: authorityTopology);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runtimeService.CountAsync("decisions", cancellationToken));
        using var functionOwnerClient = CreateSubmissionClient(
            database,
            authorityTopology,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            functionOwnerClient.ReadSubmissionAsync(Guid.NewGuid(), cancellationToken));

        await using (var restoreOwnersAndGrantUnknownObjects = new NpgsqlCommand(
                         $"""
                         ALTER FUNCTION {database.SchemaName}.serialize_policy_runtime_revision() OWNER TO CURRENT_USER;
                         ALTER FUNCTION {database.SchemaName}.begin_approval_submission(uuid, timestamptz, jsonb, text, jsonb, text) OWNER TO CURRENT_USER;
                         CREATE FUNCTION {database.SchemaName}.submission_bypass()
                         RETURNS void LANGUAGE sql SECURITY DEFINER
                         AS 'SELECT NULL::void';
                         CREATE VIEW {database.SchemaName}.submission_bypass_view
                         AS SELECT * FROM {database.SchemaName}.approval_submission_attempts;
                         GRANT EXECUTE ON FUNCTION {database.SchemaName}.submission_bypass()
                             TO {database.RuntimeRoleName}, {database.SubmissionExecutorRoleName}, {database.ReconciliationRoleName}, {database.RecoveryRoleName};
                         GRANT SELECT, INSERT ON {database.SchemaName}.submission_bypass_view
                             TO {database.RuntimeRoleName}, {database.SubmissionExecutorRoleName}, {database.ReconciliationRoleName}, {database.RecoveryRoleName};
                         """, owner) { CommandTimeout = 5 })
            await restoreOwnersAndGrantUnknownObjects.ExecuteNonQueryAsync(cancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runtimeService.CountAsync("decisions", cancellationToken));
        using var aclDriftClient = CreateSubmissionClient(
            database,
            authorityTopology,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            aclDriftClient.ReadSubmissionAsync(Guid.NewGuid(), cancellationToken));
        using var aclDriftReconciliationClient = CreateReconciliationClient(
            database,
            authorityTopology,
            executorSigner,
            reconciliationSigner,
            stateSigner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            aclDriftReconciliationClient.ReadSubmissionAsync(Guid.NewGuid(), cancellationToken));
        using var aclDriftRecoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            new StubActiveReleaseBindingReader());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            aclDriftRecoveryClient.ReadSubmissionAsync(Guid.NewGuid(), cancellationToken));

        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            owner,
            $"TRUNCATE {database.SchemaName}.approval_submission_attempts, {database.SchemaName}.approval_submission_acknowledgements, {database.SchemaName}.approval_submission_quarantines, {database.SchemaName}.approval_submission_reconciliations, {database.SchemaName}.approval_submission_recoveries, {database.SchemaName}.native_stop_challenge_issues, {database.SchemaName}.native_stop_challenge_consumptions",
            "P0001",
            cancellationToken);
    }

    private static async Task<(ActionProposalV1 Proposal, PolicyApprovalAuthoritativeSnapshot Snapshot)> IssueApprovedAsync(
        PolicyApprovalTestDatabase database,
        ECDsa evaluationSigner,
        ECDsa revocationSigner,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        string label,
        CancellationToken cancellationToken)
        => await IssueApprovedForScopeAsync(
            database,
            evaluationSigner,
            revocationSigner,
            authorityTopology,
            label,
            SoulA,
            DeviceA,
            AccountA,
            cancellationToken);

    private static async Task<(ActionProposalV1 Proposal, PolicyApprovalAuthoritativeSnapshot Snapshot)> IssueApprovedForScopeAsync(
        PolicyApprovalTestDatabase database,
        ECDsa evaluationSigner,
        ECDsa revocationSigner,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        string label,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken)
    {
        await database.AppendRuntimeStateAsync(State(soulId, deviceBindingId, platformAccountId), cancellationToken);
        using var service = database.CreateService(
            evaluationSigner,
            revocationSigner,
            authorityTopology: authorityTopology);
        var proposal = Proposal(soulId, deviceBindingId, platformAccountId, "idem-" + label);
        var result = await service.EvaluateAndAppendAsync(proposal, SignEvaluation(evaluationSigner, proposal), cancellationToken);
        return (proposal, result.Snapshot);
    }

    private static async Task<(bool Success, Exception? Error)> CaptureTransitionAsync(Func<Task> transition)
    {
        try
        {
            await transition();
            return (true, null);
        }
        catch (Exception exception)
        {
            return (false, exception);
        }
    }

    private static PolicyApprovalSubmissionAuthorityTopology SubmissionTopology(
        ECDsa evaluationSigner,
        ECDsa revocationSigner,
        ECDsa fenceSigner,
        ECDsa executorSigner,
        ECDsa reconciliationSigner,
        ECDsa recoverySigner,
        ECDsa stateSigner)
        => PolicyApprovalTestAuthorities.CreateTopology(
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner);

    private static PolicyApprovalExecutionFenceClient CreateSubmissionClient(
        PolicyApprovalTestDatabase database,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ECDsa fenceSigner,
        ECDsa executorSigner,
        ECDsa reconciliationSigner,
        ECDsa recoverySigner,
        ECDsa stateSigner)
    {
        var privateKey = stateSigner.ExportPkcs8PrivateKey();
        try
        {
            return PolicyApprovalExecutionFenceClient.CreateProduction(
                database.SubmissionExecutorOptions,
                authorityTopology,
                fenceSigner.ExportSubjectPublicKeyInfo(),
                executorSigner.ExportSubjectPublicKeyInfo(),
                reconciliationSigner.ExportSubjectPublicKeyInfo(),
                recoverySigner.ExportSubjectPublicKeyInfo(),
                privateKey);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    private static PolicyApprovalSubmissionReconcilerClient CreateReconciliationClient(
        PolicyApprovalTestDatabase database,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ECDsa executorSigner,
        ECDsa reconciliationSigner,
        ECDsa stateSigner)
    {
        var privateKey = stateSigner.ExportPkcs8PrivateKey();
        try
        {
            return PolicyApprovalSubmissionReconcilerClient.CreateProduction(
                database.SubmissionReconciliationOptions,
                authorityTopology,
                executorSigner.ExportSubjectPublicKeyInfo(),
                reconciliationSigner.ExportSubjectPublicKeyInfo(),
                privateKey);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    private static PolicyApprovalSubmissionRecoveryClient CreateRecoveryClient(
        PolicyApprovalTestDatabase database,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ECDsa executorSigner,
        ECDsa recoverySigner,
        ECDsa stateSigner,
        IActiveReleaseBindingReader activeReleaseBindingReader)
    {
        var privateKey = stateSigner.ExportPkcs8PrivateKey();
        try
        {
            return PolicyApprovalSubmissionRecoveryClient.CreateProduction(
                database.SubmissionRecoveryOptions,
                authorityTopology,
                executorSigner.ExportSubjectPublicKeyInfo(),
                recoverySigner.ExportSubjectPublicKeyInfo(),
                privateKey,
                activeReleaseBindingReader);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    private static ApprovalSubmissionAcknowledgementV1 Acknowledgement(
        ApprovalSubmissionIntentV1 intent,
        ApprovalSubmissionStateV1 pending)
        => new(
            ApprovalSubmissionAcknowledgementV1.CurrentSchemaVersion,
            ApprovalSubmissionAcknowledgementV1.CurrentContractId,
            ApprovalSubmissionAcknowledgementV1.CurrentProducerModule,
            ApprovalSubmissionAcknowledgementV1.CurrentAuthScope,
            Guid.NewGuid(), intent.SubmissionAttemptId,
            intent.ApprovalId, intent.ProposalId, intent.CommandId, intent.LeaseId, intent.Attempt,
            intent.SoulId, intent.DeviceBindingId, intent.PlatformAccountId, intent.TraceId, intent.IdempotencyKey,
            intent.ReleaseBomSha256, intent.ReleaseBomGeneration, intent.NativeRequestBindingSha256,
            ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent), pending.StateSha256,
            Sha256Hex("submitted:" + intent.SubmissionAttemptId), Guid.NewGuid(), Guid.NewGuid(),
            Sha256Hex("native-ack:" + intent.SubmissionAttemptId),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "internal",
            Convert.ToBase64String(new byte[64]));

    private static ApprovalSubmissionAcknowledgementV1 SignAcknowledgement(ECDsa signer, ApprovalSubmissionAcknowledgementV1 value)
        => SignLifecycle(value, signer, ApprovalSubmissionLifecycleBinding.CanonicalAcknowledgementBytes,
            static (item, signature) => item with { SignatureBase64 = signature });

    private static ApprovalSubmissionReconciliationV1 Reconciliation(
        ApprovalSubmissionIntentV1 intent,
        ApprovalSubmissionStateV1 pending)
        => new(
            ApprovalSubmissionReconciliationV1.CurrentSchemaVersion,
            ApprovalSubmissionReconciliationV1.CurrentContractId,
            ApprovalSubmissionReconciliationV1.CurrentProducerModule,
            ApprovalSubmissionReconciliationV1.CurrentAuthScope,
            ApprovalSubmissionReconciliationV1.CurrentAuthorityRole,
            Guid.NewGuid(), intent.SubmissionAttemptId,
            intent.ApprovalId, intent.ProposalId, intent.CommandId, intent.LeaseId, intent.Attempt,
            intent.SoulId, intent.DeviceBindingId, intent.PlatformAccountId, intent.TraceId, intent.IdempotencyKey,
            ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent), pending.StateSha256,
            ApprovalSubmissionReconciliationV1.ConfirmedNotSubmitted,
            Sha256Hex("reconcile-evidence:" + intent.SubmissionAttemptId),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(4), "internal",
            Convert.ToBase64String(new byte[64]));

    private static ApprovalSubmissionReconciliationV1 SignReconciliation(ECDsa signer, ApprovalSubmissionReconciliationV1 value)
        => SignLifecycle(value, signer, ApprovalSubmissionLifecycleBinding.CanonicalReconciliationBytes,
            static (item, signature) => item with { SignatureBase64 = signature });

    private static ApprovalSubmissionRecoveryV1 Recovery(
        ApprovalSubmissionIntentV1 intent,
        ApprovalSubmissionReconciliationV1 reconciliation,
        Guid nextAttemptId,
        Guid nextLeaseId,
        string nextAuthorizationSha256,
        string nextNativeBindingSha256)
        => new(
            ApprovalSubmissionRecoveryV1.CurrentSchemaVersion,
            ApprovalSubmissionRecoveryV1.CurrentContractId,
            ApprovalSubmissionRecoveryV1.CurrentProducerModule,
            ApprovalSubmissionRecoveryV1.CurrentAuthScope,
            ApprovalSubmissionRecoveryV1.CurrentAuthorityRole,
            Guid.NewGuid(), intent.SubmissionAttemptId,
            reconciliation.ReconciliationId,
            ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(reconciliation),
            intent.ApprovalId, intent.ProposalId, intent.CommandId, intent.LeaseId, intent.Attempt,
            nextAttemptId, nextLeaseId, intent.Attempt + 1,
            intent.SoulId, intent.DeviceBindingId, intent.PlatformAccountId, intent.TraceId, intent.IdempotencyKey,
            intent.ReleaseBomSha256, intent.ReleaseBomGeneration,
            nextAuthorizationSha256, nextNativeBindingSha256,
            "human_" + Sha256Hex("human:" + intent.SubmissionAttemptId),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(4), "internal",
            Convert.ToBase64String(new byte[64]));

    private static ApprovalSubmissionRecoveryV1 SignRecovery(ECDsa signer, ApprovalSubmissionRecoveryV1 value)
        => SignLifecycle(value, signer, ApprovalSubmissionLifecycleBinding.CanonicalRecoveryBytes,
            static (item, signature) => item with { SignatureBase64 = signature });

    private static T SignLifecycle<T>(
        T value,
        ECDsa signer,
        Func<T, byte[]> canonicalize,
        Func<T, string, T> attachSignature)
    {
        var canonical = canonicalize(value);
        byte[]? signature = null;
        try
        {
            signature = signer.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return attachSignature(value, Convert.ToBase64String(signature));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }
}
