using System.Security.Cryptography;
using System.Text;
using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed partial class PostgresPolicyApprovalIntegrationTests
{
    private const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DeviceA = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DeviceB = "db_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AccountA = "pa_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AccountB = "pa_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PlatformAuthorization = "platform-authorization-fixture";
    private static readonly string ReleaseBom = new('d', 64);

    [Fact, Trait("Category", "Integration")]
    public async Task ApprovedDecisionReceiptOutboxAndRestartReadbackCommitTogether()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);
        var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-approved-restart");
        var envelope = SignEvaluation(evaluationSigner, proposal);

        var inserted = await service.EvaluateAndAppendAsync(proposal, envelope, cancellationToken);
        var duplicate = await service.EvaluateAndAppendAsync(proposal, envelope, cancellationToken);

        Assert.Equal(PolicyApprovalAppendDisposition.Inserted, inserted.Disposition);
        Assert.Equal(ApprovalDecisionV1.Approved, inserted.Snapshot.Approval.Decision);
        Assert.False(inserted.Snapshot.Approval.ShadowOnly);
        Assert.Equal(PolicyApprovalAppendDisposition.DuplicateNoOp, duplicate.Disposition);
        Assert.Equal(inserted.Snapshot.CanonicalSha256, duplicate.Snapshot.CanonicalSha256);
        Assert.Equal(1, await service.CountAsync("decisions", cancellationToken));
        Assert.Equal(1, await service.CountAsync("statuses", cancellationToken));
        Assert.Equal(1, await service.CountAsync("receipts", cancellationToken));
        Assert.Equal(1, await service.CountAsync("outbox", cancellationToken));
        Assert.Equal(0, await service.CountAsync("quarantine", cancellationToken));

        var restarted = new PolicyApprovalAuthoritativeClient(database.Options);
        var readback = await restarted.ReadAsync(ReadRequest(inserted.Snapshot), cancellationToken);
        Assert.Equal(PolicyApprovalAuthoritativeSnapshot.Active, readback.Status);
        Assert.Equal(inserted.Snapshot.CanonicalSha256, readback.CanonicalSha256);
        Assert.Equal(PolicyApprovalDecisionCanonical.ComputeSha256(readback.Approval), readback.CanonicalSha256);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentDuplicateHasOneDecisionStatusReceiptAndOutbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);
        var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-concurrent");
        var envelope = SignEvaluation(evaluationSigner, proposal);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => service.EvaluateAndAppendAsync(proposal, envelope, cancellationToken)));

        Assert.Single(results, result => result.Disposition == PolicyApprovalAppendDisposition.Inserted);
        Assert.Equal(15, results.Count(result => result.Disposition == PolicyApprovalAppendDisposition.DuplicateNoOp));
        Assert.Single(results.Select(result => result.Snapshot.CanonicalSha256).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, await service.CountAsync("decisions", cancellationToken));
        Assert.Equal(1, await service.CountAsync("statuses", cancellationToken));
        Assert.Equal(1, await service.CountAsync("receipts", cancellationToken));
        Assert.Equal(1, await service.CountAsync("outbox", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task EvaluationUsesOneRuntimeConnectionWhenPoolSizeIsOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var runtime = new NpgsqlConnectionStringBuilder(database.Options.RuntimeConnectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
            ApplicationName = "policy-approval-single-connection-" + Guid.NewGuid().ToString("N")
        };
        var options = new PostgresPolicyApprovalOptions(
            runtime.ConnectionString,
            database.SchemaName,
            database.RuntimeRoleName);

        try
        {
            var authorityTopology = PolicyApprovalTestAuthorities.TopologyFor(evaluationSigner, revocationSigner);
            using var service = PostgresPolicyApprovalService.CreateProduction(
                options,
                authorityTopology,
                evaluationSigner.ExportSubjectPublicKeyInfo(),
                PolicyApprovalTestAuthorities.PromotionFor(evaluationSigner).ExportSubjectPublicKeyInfo(),
                revocationSigner.ExportSubjectPublicKeyInfo());
            var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-single-pool-connection");
            var result = await service.EvaluateAndAppendAsync(
                proposal, SignEvaluation(evaluationSigner, proposal), cancellationToken);
            Assert.Equal(PolicyApprovalAppendDisposition.Inserted, result.Disposition);
            Assert.Equal(ApprovalDecisionV1.Approved, result.Snapshot.Approval.Decision);
        }
        finally
        {
            using var poolIdentity = new NpgsqlConnection(runtime.ConnectionString);
            NpgsqlConnection.ClearPool(poolIdentity);
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameScopedIdempotencyWithDifferentProposalIsHashOnlyQuarantined()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);
        var original = Proposal(SoulA, DeviceA, AccountA, "idem-conflict", "fixture.type", true,
            new Dictionary<string, string> { ["selector_ref"] = "fixture.input", ["value_ref"] = "first" });
        await service.EvaluateAndAppendAsync(original, SignEvaluation(evaluationSigner, original), cancellationToken);
        var conflict = original with
        {
            Parameters = new Dictionary<string, string>
            {
                ["selector_ref"] = "fixture.input",
                ["value_ref"] = "second"
            }
        };

        await Assert.ThrowsAsync<PolicyApprovalIdempotencyConflictException>(
            () => service.EvaluateAndAppendAsync(conflict, SignEvaluation(evaluationSigner, conflict), cancellationToken));

        Assert.Equal(1, await service.CountAsync("decisions", cancellationToken));
        Assert.Equal(1, await service.CountAsync("receipts", cancellationToken));
        Assert.Equal(1, await service.CountAsync("outbox", cancellationToken));
        Assert.Equal(1, await service.CountAsync("quarantine", cancellationToken));
        await database.AssertQuarantineContainsHashesOnlyAsync(cancellationToken);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task QuarantineCommitsBeforePostCommitFaultAndSurvivesRestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = Proposal(SoulA, DeviceA, AccountA, "idem-quarantine-crash", "fixture.type", true,
            new Dictionary<string, string> { ["selector_ref"] = "fixture.input", ["value_ref"] = "first" });
        using (var service = database.CreateService(evaluationSigner, revocationSigner))
            await service.EvaluateAndAppendAsync(original, SignEvaluation(evaluationSigner, original), cancellationToken);
        var conflict = original with
        {
            Parameters = new Dictionary<string, string>
            {
                ["selector_ref"] = "fixture.input",
                ["value_ref"] = "second"
            }
        };
        using (var failing = database.CreateService(
            evaluationSigner,
            revocationSigner,
            (stage, _) => stage == PolicyApprovalMutationStage.QuarantineWritten
                ? ValueTask.FromException(new InvalidOperationException("post-commit quarantine fault"))
                : ValueTask.CompletedTask))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failing.EvaluateAndAppendAsync(
                    conflict,
                    SignEvaluation(evaluationSigner, conflict),
                    cancellationToken));
        }

        using var restarted = database.CreateService(evaluationSigner, revocationSigner);
        Assert.Equal(1, await restarted.CountAsync("decisions", cancellationToken));
        Assert.Equal(1, await restarted.CountAsync("quarantine", cancellationToken));
        await database.AssertQuarantineContainsHashesOnlyAsync(cancellationToken);
    }

    [Theory]
    [InlineData(PolicyApprovalMutationStage.DecisionWritten)]
    [InlineData(PolicyApprovalMutationStage.RateConsumed)]
    [InlineData(PolicyApprovalMutationStage.StatusRevisionWritten)]
    [InlineData(PolicyApprovalMutationStage.ReceiptWritten)]
    [InlineData(PolicyApprovalMutationStage.OutboxWritten)]
    [InlineData(PolicyApprovalMutationStage.BeforeCommit)]
    [Trait("Category", "Integration")]
    public async Task CrashWindowRollsBackEveryRowAndRestartRetryRecovers(PolicyApprovalMutationStage failureStage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var injected = 0;
        using (var failing = database.CreateService(
            evaluationSigner,
            revocationSigner,
            (stage, _) => stage == failureStage && Interlocked.Exchange(ref injected, 1) == 0
                ? ValueTask.FromException(new InvalidOperationException("injected policy-approval crash window"))
                : ValueTask.CompletedTask))
        {
            var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-crash-" + failureStage);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => failing.EvaluateAndAppendAsync(proposal, SignEvaluation(evaluationSigner, proposal), cancellationToken));
        }

        using var recovered = database.CreateService(evaluationSigner, revocationSigner);
        Assert.Equal(0, await recovered.CountAsync("decisions", cancellationToken));
        Assert.Equal(0, await recovered.CountAsync("statuses", cancellationToken));
        Assert.Equal(0, await recovered.CountAsync("receipts", cancellationToken));
        Assert.Equal(0, await recovered.CountAsync("outbox", cancellationToken));
        var retry = Proposal(SoulA, DeviceA, AccountA, "idem-crash-" + failureStage);
        await recovered.EvaluateAndAppendAsync(retry, SignEvaluation(evaluationSigner, retry), cancellationToken);
        Assert.Equal(1, await recovered.CountAsync("decisions", cancellationToken));
        Assert.Equal(1, await recovered.CountAsync("statuses", cancellationToken));
        Assert.Equal(1, await recovered.CountAsync("receipts", cancellationToken));
        Assert.Equal(1, await recovered.CountAsync("outbox", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SignedRevocationIsAppendOnlyAndOldRevisionCannotBeReused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);
        var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-before-revoke");
        var issued = await service.EvaluateAndAppendAsync(proposal, SignEvaluation(evaluationSigner, proposal), cancellationToken);
        var revoke = new PolicyApprovalRevocationRequest(
            issued.Snapshot.Approval.ApprovalId,
            issued.Snapshot.Approval.ProposalId,
            SoulA,
            DeviceA,
            AccountA,
            TraceId("revoke"),
            IdempotencyKey("revoke"),
            issued.Snapshot.CanonicalSha256,
            1);
        var revoked = await service.RevokeAsync(revoke, SignRevocation(revocationSigner, revoke), cancellationToken);

        Assert.Equal(PolicyApprovalAuthoritativeSnapshot.Revoked, revoked.Snapshot.Status);
        Assert.Equal(2, revoked.Snapshot.StatusRevision);
        Assert.Equal(2, await service.CountAsync("statuses", cancellationToken));
        Assert.Equal(2, await service.CountAsync("receipts", cancellationToken));
        Assert.Equal(2, await service.CountAsync("outbox", cancellationToken));
        var restarted = new PolicyApprovalAuthoritativeClient(database.Options);
        Assert.Equal(PolicyApprovalAuthoritativeSnapshot.Revoked,
            (await restarted.ReadAsync(ReadRequest(issued.Snapshot), cancellationToken)).Status);

        var stale = revoke with { TraceId = TraceId("stale-revoke"), IdempotencyKey = IdempotencyKey("stale-revoke") };
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.RevokeAsync(stale, SignRevocation(revocationSigner, stale), cancellationToken));
        Assert.Equal(2, await service.CountAsync("statuses", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FinalDatabaseClockRejectsEvaluationAndRevocationThatExpireBeforeCommit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PolicyApprovalMutationFaultInjector delayBeforeCommit = async (stage, token) =>
        {
            if (stage == PolicyApprovalMutationStage.BeforeCommit)
                await Task.Delay(TimeSpan.FromMilliseconds(750), token);
        };

        var expiringProposal = Proposal(SoulA, DeviceA, AccountA, "idem-expiring-evaluation");
        using (var expiring = database.CreateService(
            evaluationSigner,
            revocationSigner,
            delayBeforeCommit))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                expiring.EvaluateAndAppendAsync(
                    expiringProposal,
                    SignEvaluation(
                        evaluationSigner,
                        expiringProposal,
                        DateTimeOffset.UtcNow.AddMilliseconds(350)),
                    cancellationToken));
        }

        using var service = database.CreateService(evaluationSigner, revocationSigner);
        Assert.Equal(0, await service.CountAsync("decisions", cancellationToken));
        var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-before-expiring-revoke");
        var issued = await service.EvaluateAndAppendAsync(
            proposal,
            SignEvaluation(evaluationSigner, proposal),
            cancellationToken);
        var request = new PolicyApprovalRevocationRequest(
            issued.Snapshot.Approval.ApprovalId,
            issued.Snapshot.Approval.ProposalId,
            issued.Snapshot.Approval.SoulId,
            issued.Snapshot.Approval.DeviceBindingId,
            issued.Snapshot.Approval.PlatformAccountId,
            TraceId("expiring-revoke"),
            IdempotencyKey("expiring-revoke"),
            issued.Snapshot.CanonicalSha256,
            issued.Snapshot.StatusRevision);
        using (var expiring = database.CreateService(
            evaluationSigner,
            revocationSigner,
            delayBeforeCommit))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                expiring.RevokeAsync(
                    request,
                    SignRevocation(
                        revocationSigner,
                        request,
                        DateTimeOffset.UtcNow.AddMilliseconds(350)),
                    cancellationToken));
        }
        Assert.Equal(1, await service.CountAsync("statuses", cancellationToken));
        Assert.Equal(1, await service.CountAsync("receipts", cancellationToken));
        Assert.Equal(1, await service.CountAsync("outbox", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExecutionFenceHoldsApprovalAndRuntimeGenerationUntilNativeDispatchBoundary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = PolicyApprovalTestAuthorities.CreateTopology(
            evaluationSigner,
            revocationSigner,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner);
        using var service = database.CreateService(
            evaluationSigner,
            revocationSigner,
            authorityTopology: authorityTopology);
        var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-fence-issue");
        var issued = await service.EvaluateAndAppendAsync(
            proposal,
            SignEvaluation(evaluationSigner, proposal),
            cancellationToken);
        var fenceRequest = FenceRequest(issued.Snapshot);
        var intent = SignSubmissionIntent(
            executorSigner,
            SubmissionIntent(issued.Snapshot, proposal, fenceRequest));
        using var fenceClient = CreateSubmissionClient(
            database,
            authorityTopology,
            fenceSigner,
            executorSigner,
            reconciliationSigner,
            recoverySigner,
            stateSigner);
        await using var lease = await fenceClient.AcquireAsync(
            fenceRequest,
            SignFenceAuthorization(fenceSigner, fenceRequest),
            intent,
            cancellationToken);
        Assert.True(lease.Fence.ValidUntil - lease.Fence.AcquiredAt <= TimeSpan.FromSeconds(2));
        Assert.Equal(lease.Fence, await lease.RevalidateForNativeDispatchAsync(cancellationToken));

        var revoke = new PolicyApprovalRevocationRequest(
            issued.Snapshot.Approval.ApprovalId,
            issued.Snapshot.Approval.ProposalId,
            issued.Snapshot.Approval.SoulId,
            issued.Snapshot.Approval.DeviceBindingId,
            issued.Snapshot.Approval.PlatformAccountId,
            TraceId("fence-revoke"),
            IdempotencyKey("fence-revoke"),
            issued.Snapshot.CanonicalSha256,
            issued.Snapshot.StatusRevision);
        var revokeTask = service.RevokeAsync(
            revoke,
            SignRevocation(revocationSigner, revoke),
            cancellationToken);
        var killSwitchTask = database.AppendRuntimeStateAsync(
            State(SoulA, DeviceA, AccountA, 2) with { KillSwitchEnabled = true },
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        Assert.False(revokeTask.IsCompleted);
        Assert.False(killSwitchTask.IsCompleted);

        await lease.DisposeAsync();
        var revokedAfterRelease = await revokeTask;
        await killSwitchTask;
        Assert.Equal(PolicyApprovalAuthoritativeSnapshot.Revoked, revokedAfterRelease.Snapshot.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fenceClient.AcquireAsync(
                fenceRequest,
                SignFenceAuthorization(fenceSigner, fenceRequest),
                intent,
                cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExactScopeNeverLeaksAcrossSoulDeviceOrAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulB, DeviceB, AccountB), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);
        var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-scope");
        var issued = await service.EvaluateAndAppendAsync(proposal, SignEvaluation(evaluationSigner, proposal), cancellationToken);
        var request = ReadRequest(issued.Snapshot);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReadAuthoritativeAsync(request with { SoulId = SoulB }, cancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReadAuthoritativeAsync(request with { DeviceBindingId = DeviceB }, cancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReadAuthoritativeAsync(request with { PlatformAccountId = AccountB }, cancellationToken));

        var other = Proposal(SoulB, DeviceB, AccountB, proposal.IdempotencyKey);
        var otherIssued = await service.EvaluateAndAppendAsync(other, SignEvaluation(evaluationSigner, other), cancellationToken);
        Assert.NotEqual(issued.Snapshot.Approval.ApprovalId, otherIssued.Snapshot.Approval.ApprovalId);
        Assert.Equal(2, await service.CountAsync("decisions", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task KillSwitchRatePlatformAuthorizationAndPromptInjectionInputFailClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);

        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA) with { KillSwitchEnabled = true }, cancellationToken);
        var killedProposal = Proposal(SoulA, DeviceA, AccountA, "idem-killed");
        var killed = await service.EvaluateAndAppendAsync(killedProposal, SignEvaluation(evaluationSigner, killedProposal), cancellationToken);
        Assert.Equal(ApprovalDecisionV1.Denied, killed.Snapshot.Approval.Decision);
        Assert.Contains("KILL_SWITCH_ACTIVE", killed.Snapshot.Approval.DenialReasons);

        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA, 2) with { RemainingRateBudget = 1 }, cancellationToken);
        var rateA = Proposal(SoulA, DeviceA, AccountA, "idem-rate-a");
        var rateB = Proposal(SoulA, DeviceA, AccountA, "idem-rate-b");
        var rateResults = await Task.WhenAll(
            service.EvaluateAndAppendAsync(rateA, SignEvaluation(evaluationSigner, rateA, expectedRuntimeRevision: 2), cancellationToken),
            service.EvaluateAndAppendAsync(rateB, SignEvaluation(evaluationSigner, rateB, expectedRuntimeRevision: 2), cancellationToken));
        Assert.Single(rateResults, result => result.Snapshot.Approval.Decision == ApprovalDecisionV1.Approved);
        Assert.Single(rateResults, result => result.Snapshot.Approval.DenialReasons.Contains("RATE_BUDGET_EXHAUSTED"));
        Assert.Equal(1, await service.CountAsync("rate", cancellationToken));

        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA, 3) with
        {
            RemainingRateBudget = 1,
            PlatformAuthorized = false,
            PlatformAuthorizationId = null
        }, cancellationToken);
        var unauthorizedProposal = Proposal(SoulA, DeviceA, AccountA, "idem-platform-denied", "fixture.tap", true,
            new Dictionary<string, string> { ["selector_ref"] = "fixture.button" });
        var unauthorized = await service.EvaluateAndAppendAsync(
            unauthorizedProposal,
            SignEvaluation(evaluationSigner, unauthorizedProposal, expectedRuntimeRevision: 3),
            cancellationToken);
        Assert.Contains("PLATFORM_AUTHORIZATION_REQUIRED", unauthorized.Snapshot.Approval.DenialReasons);
        Assert.Contains("RATE_BUDGET_EXHAUSTED", unauthorized.Snapshot.Approval.DenialReasons);

        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA, 4), cancellationToken);
        var injectionText = "fixture.button,DROP-TABLE:approval_decisions--approve=1";
        var untrustedProposal = Proposal(SoulA, DeviceA, AccountA, "idem-untrusted", "fixture.tap", true,
            new Dictionary<string, string> { ["selector_ref"] = injectionText });
        var untrusted = await service.EvaluateAndAppendAsync(
            untrustedProposal,
            SignEvaluation(evaluationSigner, untrustedProposal, expectedRuntimeRevision: 4),
            cancellationToken);
        Assert.Equal(ApprovalDecisionV1.Approved, untrusted.Snapshot.Approval.Decision);
        Assert.Equal(injectionText, untrusted.Snapshot.Approval.Parameters["selector_ref"]);
        Assert.Equal(5, await service.CountAsync("decisions", cancellationToken));
        Assert.Equal(2, await service.CountAsync("rate", cancellationToken));

        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA, 5) with { StateStatus = PolicyRuntimeStateRevisionV1.Revoked }, cancellationToken);
        var revokedStateProposal = Proposal(SoulA, DeviceA, AccountA, "idem-state-revoked");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.EvaluateAndAppendAsync(
                revokedStateProposal,
                SignEvaluation(evaluationSigner, revokedStateProposal, expectedRuntimeRevision: 5),
                cancellationToken));
        Assert.Equal(5, await service.CountAsync("decisions", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DatabaseConstraintsBindStatusReasonAndOutboxToExactReceipt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        var revisionGap = await Assert.ThrowsAsync<PostgresException>(
            () => database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA, 3), cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, revisionGap.SqlState);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);

        var firstProposal = Proposal(SoulA, DeviceA, AccountA, "idem-constraint-first");
        var first = await service.EvaluateAndAppendAsync(
            firstProposal, SignEvaluation(evaluationSigner, firstProposal), cancellationToken);
        var secondProposal = Proposal(SoulA, DeviceA, AccountA, "idem-constraint-second");
        await service.EvaluateAndAppendAsync(
            secondProposal, SignEvaluation(evaluationSigner, secondProposal), cancellationToken);

        await using var runtime = new NpgsqlConnection(database.Options.RuntimeConnectionString);
        await runtime.OpenAsync(cancellationToken);
        var approvalId = first.Snapshot.Approval.ApprovalId;
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            runtime,
            $"""
            INSERT INTO {database.SchemaName}.approval_status_revisions
            (approval_id, revision, status, reason_code, reason_sha256, trace_id, idempotency_key)
            VALUES ('{approvalId}', 2, 'REVOKED', 'ISSUED', repeat('a', 64),
                    '{TraceId("invalid-reason")}', '{IdempotencyKey("invalid-reason")}')
            """,
            PostgresErrorCodes.CheckViolation,
            cancellationToken);

        await using (var validStatus = new NpgsqlCommand(
            $"""
            INSERT INTO {database.SchemaName}.approval_status_revisions
            (approval_id, revision, status, reason_code, reason_sha256, trace_id, idempotency_key)
            VALUES (@approval_id, 2, 'REVOKED', 'CONTROL_PLANE_REVOKED', repeat('b', 64),
                    @trace_id, @idempotency_key)
            """,
            runtime) { CommandTimeout = 5 })
        {
            validStatus.Parameters.AddWithValue("approval_id", approvalId);
            validStatus.Parameters.AddWithValue("trace_id", TraceId("valid-reason"));
            validStatus.Parameters.AddWithValue("idempotency_key", IdempotencyKey("valid-reason"));
            await validStatus.ExecuteNonQueryAsync(cancellationToken);
        }

        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            runtime,
            $"""
            INSERT INTO {database.SchemaName}.approval_outbox
            (outbox_id, approval_id, status_revision, soul_id, device_binding_id,
             platform_account_id, trace_id, idempotency_key, topic, payload_sha256, payload_json)
            VALUES ('{Guid.NewGuid()}', '{approvalId}', 2, '{SoulA}', '{DeviceA}', '{AccountA}',
                    '{TraceId("mismatched-receipt")}', '{secondProposal.IdempotencyKey}',
                    'policy-approval.status/internal-v1', repeat('c', 64), jsonb_build_object())
            """,
            PostgresErrorCodes.ForeignKeyViolation,
            cancellationToken);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RuntimeRoleCannotMutateDeleteTruncateOrDdlAndOwnerTriggersHold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await database.AppendRuntimeStateAsync(State(SoulA, DeviceA, AccountA), cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var service = database.CreateService(evaluationSigner, revocationSigner);
        var proposal = Proposal(SoulA, DeviceA, AccountA, "idem-append-only");
        var issued = await service.EvaluateAndAppendAsync(proposal, SignEvaluation(evaluationSigner, proposal), cancellationToken);

        var masquerade = new NpgsqlConnectionStringBuilder(database.AdminConnectionString) { Pooling = false };
        masquerade.Options = string.IsNullOrWhiteSpace(masquerade.Options)
            ? $"-c role={database.RuntimeRoleName}"
            : $"{masquerade.Options} -c role={database.RuntimeRoleName}";
        var unsafeClient = new PolicyApprovalAuthoritativeClient(
            new PostgresPolicyApprovalOptions(
                masquerade.ConnectionString,
                database.SchemaName,
                database.RuntimeRoleName));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => unsafeClient.ReadAsync(ReadRequest(issued.Snapshot), cancellationToken));

        await using var runtime = new NpgsqlConnection(database.Options.RuntimeConnectionString);
        await runtime.OpenAsync(cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(runtime,
            $"UPDATE {database.SchemaName}.approval_decisions SET decision = decision",
            PostgresErrorCodes.InsufficientPrivilege, cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(runtime,
            $"DELETE FROM {database.SchemaName}.approval_status_revisions",
            PostgresErrorCodes.InsufficientPrivilege, cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(runtime,
            $"TRUNCATE {database.SchemaName}.approval_outbox",
            PostgresErrorCodes.InsufficientPrivilege, cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(runtime,
            $"ALTER TABLE {database.SchemaName}.approval_decisions ADD COLUMN forbidden text",
            PostgresErrorCodes.InsufficientPrivilege, cancellationToken);

        await using var owner = new NpgsqlConnection(database.AdminConnectionString);
        await owner.OpenAsync(cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(owner,
            $"UPDATE {database.SchemaName}.approval_decisions SET decision = decision",
            "P0001", cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(owner,
            $"DELETE FROM {database.SchemaName}.approval_status_revisions",
            "P0001", cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(owner,
            $"TRUNCATE {database.SchemaName}.approval_outbox",
            "P0001", cancellationToken);
    }

    private static PolicyRuntimeStateRevisionV1 State(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long revision = 1)
        => new(
            soulId, deviceBindingId, platformAccountId, revision,
            PolicyRuntimeStateRevisionV1.Active, "1.0.0",
            DeterministicPolicyEvaluator.KnownPolicies.Order(StringComparer.Ordinal).ToArray(),
            false, 100, true, PlatformAuthorization, true, ReleaseBom,
            DateTimeOffset.UtcNow.AddHours(1));

    private static ActionProposalV1 Proposal(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string idempotencyKey,
        string action = "observe",
        bool sideEffect = false,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var canonicalIdempotencyKey = IdempotencyKey(idempotencyKey);
        return new ActionProposalV1(
            ActionProposalV1.CurrentSchemaVersion,
            ActionProposalV1.CurrentContractId,
            ActionProposalV1.CurrentProducerModule,
            ActionProposalIdentity.Create(
                soulId,
                deviceBindingId,
                platformAccountId,
                canonicalIdempotencyKey),
            soulId,
            deviceBindingId,
            platformAccountId,
            TraceId(idempotencyKey),
            canonicalIdempotencyKey,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "internal",
            action,
            sideEffect,
            true,
            parameters ?? new Dictionary<string, string>(),
            ["evidence:authorized-fixture"]);
    }

    private static PolicyEvaluationEnvelope SignEvaluation(
        ECDsa signer,
        ActionProposalV1 proposal,
        DateTimeOffset? validUntil = null,
        long expectedRuntimeRevision = 1)
    {
        var promotionValidUntil = validUntil ?? DateTimeOffset.UtcNow.AddMinutes(5);
        var unsignedPromotion = new ActionExecutionPromotionV1(
            ActionExecutionPromotionV1.CurrentSchemaVersion,
            ActionExecutionPromotionV1.CurrentContractId,
            ActionExecutionPromotionV1.CurrentProducerModule,
            ActionExecutionPromotionV1.CurrentAuthScope,
            Guid.NewGuid(),
            proposal.ProposalId,
            Guid.NewGuid(),
            proposal.SoulId,
            proposal.DeviceBindingId,
            proposal.PlatformAccountId,
            proposal.TraceId,
            proposal.IdempotencyKey,
            PolicyAuthorizationBinding.ComputeProposalSha256(proposal),
            ReleaseBom,
            expectedRuntimeRevision,
            DateTimeOffset.UtcNow,
            promotionValidUntil,
            "internal",
            Convert.ToBase64String(new byte[64]));
        var promotionCanonical = ActionExecutionPromotionV1Canonical.CanonicalBytes(unsignedPromotion);
        byte[]? promotionSignature = null;
        ActionExecutionPromotionV1 promotion;
        try
        {
            promotionSignature = PolicyApprovalTestAuthorities.SignPromotion(
                signer,
                promotionCanonical);
            promotion = unsignedPromotion with
            {
                SignatureBase64 = Convert.ToBase64String(promotionSignature)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(promotionCanonical);
            if (promotionSignature is not null)
                CryptographicOperations.ZeroMemory(promotionSignature);
        }

        var unsigned = new PolicyEvaluationEnvelope(
            "control-plane-host",
            "policy:evaluate",
            proposal.ProposalId,
            PolicyAuthorizationBinding.ComputeProposalSha256(proposal),
            ReleaseBom,
            promotionValidUntil,
            string.Empty,
            PolicyEvaluationEnvelope.Execute,
            promotion);
        var canonical = EcdsaPolicyTrustProvider.CanonicalBytes(unsigned);
        byte[]? signature = null;
        try
        {
            signature = signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string TraceId(string label)
        => "trace_" + Sha256Hex(label)[..32];

    private static string IdempotencyKey(string label)
        => label.Length == 69
           && label.StartsWith("idem_", StringComparison.Ordinal)
           && label.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") < 0
            ? label
            : "idem_" + Sha256Hex(label);

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static PolicyApprovalRevocationEnvelope SignRevocation(
        ECDsa signer,
        PolicyApprovalRevocationRequest request,
        DateTimeOffset? validUntil = null)
    {
        var unsigned = new PolicyApprovalRevocationEnvelope(
            "control-plane-host",
            "policy:revoke",
            request.ApprovalId,
            PolicyApprovalRevocationBinding.ComputeSha256(request),
            ReleaseBom,
            validUntil ?? DateTimeOffset.UtcNow.AddMinutes(5),
            string.Empty);
        var canonical = EcdsaPolicyRevocationAuthorizer.CanonicalBytes(unsigned);
        byte[]? signature = null;
        try
        {
            signature = signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static PolicyApprovalReadRequest ReadRequest(PolicyApprovalAuthoritativeSnapshot snapshot)
        => new(
            snapshot.Approval.ApprovalId,
            snapshot.Approval.ProposalId,
            snapshot.Approval.SoulId,
            snapshot.Approval.DeviceBindingId,
            snapshot.Approval.PlatformAccountId,
            snapshot.Approval.TraceId,
            snapshot.Approval.IdempotencyKey,
            snapshot.CanonicalSha256);

    private static ApprovalExecutionFenceRequestV1 FenceRequest(
        PolicyApprovalAuthoritativeSnapshot snapshot)
        => new(
            ApprovalExecutionFenceRequestV1.CurrentSchemaVersion,
            ApprovalExecutionFenceRequestV1.CurrentContractId,
            ApprovalExecutionFenceRequestV1.CurrentConsumerModule,
            snapshot.Approval.ApprovalId,
            snapshot.Approval.ProposalId,
            snapshot.Approval.SoulId,
            snapshot.Approval.DeviceBindingId,
            snapshot.Approval.PlatformAccountId,
            snapshot.Approval.TraceId,
            snapshot.Approval.IdempotencyKey,
            snapshot.CanonicalSha256,
            snapshot.StatusRevision,
            snapshot.RuntimeRevision,
            snapshot.RuntimeStateSha256,
            snapshot.ReleaseBomSha256);

    private static ApprovalExecutionFenceAuthorizationV1 SignFenceAuthorization(
        ECDsa signer,
        ApprovalExecutionFenceRequestV1 request)
    {
        var unsigned = new ApprovalExecutionFenceAuthorizationV1(
            ApprovalExecutionFenceAuthorizationV1.CurrentCallerModule,
            ApprovalExecutionFenceAuthorizationV1.CurrentAuthScope,
            PolicyApprovalExecutionFenceBinding.ComputeRequestSha256(request),
            request.ExpectedReleaseBomSha256,
            DateTimeOffset.UtcNow.AddMinutes(1),
            Convert.ToBase64String(new byte[64]));
        var canonical = PolicyApprovalExecutionFenceBinding.CanonicalAuthorizationBytes(unsigned);
        byte[]? signature = null;
        try
        {
            signature = signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static ApprovalSubmissionIntentV1 SubmissionIntent(
        PolicyApprovalAuthoritativeSnapshot snapshot,
        ActionProposalV1 proposal,
        ApprovalExecutionFenceRequestV1 fenceRequest,
        Guid? submissionAttemptId = null,
        Guid? commandId = null,
        Guid? leaseId = null,
        int attempt = 1,
        long releaseBomGeneration = 1,
        string? executionAuthorizationSha256 = null,
        string? nativeRequestBindingSha256 = null)
        => new(
            ApprovalSubmissionIntentV1.CurrentSchemaVersion,
            ApprovalSubmissionIntentV1.CurrentContractId,
            ApprovalSubmissionIntentV1.CurrentProducerModule,
            ApprovalSubmissionIntentV1.CurrentAuthScope,
            submissionAttemptId ?? Guid.NewGuid(),
            PolicyApprovalExecutionFenceBinding.ComputeRequestSha256(fenceRequest),
            snapshot.Approval.ApprovalId,
            snapshot.Approval.ProposalId,
            commandId ?? Guid.NewGuid(),
            leaseId ?? Guid.NewGuid(),
            attempt,
            snapshot.Approval.SoulId,
            snapshot.Approval.DeviceBindingId,
            snapshot.Approval.PlatformAccountId,
            snapshot.Approval.TraceId,
            snapshot.Approval.IdempotencyKey,
            snapshot.CanonicalSha256,
            PolicyAuthorizationBinding.ComputeProposalSha256(proposal),
            snapshot.StatusRevision,
            snapshot.RuntimeRevision,
            snapshot.RuntimeStateSha256,
            snapshot.ReleaseBomSha256,
            releaseBomGeneration,
            executionAuthorizationSha256 ?? new string('e', 64),
            nativeRequestBindingSha256 ?? Sha256Hex("native:" + proposal.ProposalId + ":" + attempt),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "internal",
            Convert.ToBase64String(new byte[64]));

    private static ApprovalSubmissionIntentV1 SignSubmissionIntent(
        ECDsa signer,
        ApprovalSubmissionIntentV1 intent)
    {
        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalIntentBytes(intent);
        byte[]? signature = null;
        try
        {
            signature = signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return intent with { SignatureBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }
}

internal static class PolicyApprovalTestAuthorities
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ECDsa, ECDsa>
        PromotionAuthorities = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ECDsa, SubmissionAuthorities>
        SubmissionAuthoritySets = new();

    internal static ECDsa PromotionFor(ECDsa evaluationSigner)
    {
        ArgumentNullException.ThrowIfNull(evaluationSigner);
        return PromotionAuthorities.GetValue(
            evaluationSigner,
            static _ => ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

    internal static byte[] SignPromotion(
        ECDsa evaluationSigner,
        ReadOnlySpan<byte> canonical)
    {
        var promotionSigner = PromotionFor(evaluationSigner);
        lock (promotionSigner)
        {
            return promotionSigner.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    internal static PolicyApprovalSubmissionAuthorityTopology TopologyFor(
        ECDsa evaluationSigner,
        ECDsa revocationSigner)
    {
        var submission = SubmissionAuthoritySets.GetValue(
            evaluationSigner,
            static _ => new SubmissionAuthorities());
        return CreateTopology(
            evaluationSigner,
            revocationSigner,
            submission.Fence,
            submission.Executor,
            submission.Reconciliation,
            submission.Recovery,
            submission.State);
    }

    internal static PolicyApprovalSubmissionAuthorityTopology CreateTopology(
        ECDsa evaluationSigner,
        ECDsa revocationSigner,
        ECDsa fenceSigner,
        ECDsa executorSigner,
        ECDsa reconciliationSigner,
        ECDsa recoverySigner,
        ECDsa stateSigner)
        => PolicyApprovalSubmissionAuthorityTopology.Create(
            evaluationSigner.ExportSubjectPublicKeyInfo(),
            PromotionFor(evaluationSigner).ExportSubjectPublicKeyInfo(),
            revocationSigner.ExportSubjectPublicKeyInfo(),
            fenceSigner.ExportSubjectPublicKeyInfo(),
            executorSigner.ExportSubjectPublicKeyInfo(),
            reconciliationSigner.ExportSubjectPublicKeyInfo(),
            recoverySigner.ExportSubjectPublicKeyInfo(),
            stateSigner.ExportSubjectPublicKeyInfo());

    private sealed class SubmissionAuthorities
    {
        internal ECDsa Fence { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa Executor { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa Reconciliation { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa Recovery { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa State { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }
}

internal sealed class PolicyApprovalTestDatabase : IAsyncDisposable
{
    private PolicyApprovalTestDatabase(
        string adminConnectionString,
        string runtimeConnectionString,
        string submissionExecutorConnectionString,
        string reconciliationConnectionString,
        string recoveryConnectionString,
        string schemaName,
        string runtimeRoleName,
        string submissionExecutorRoleName,
        string reconciliationRoleName,
        string recoveryRoleName)
    {
        AdminConnectionString = adminConnectionString;
        Options = new PostgresPolicyApprovalOptions(runtimeConnectionString, schemaName, runtimeRoleName);
        SubmissionExecutorOptions = new PostgresPolicyApprovalSubmissionExecutorOptions(
            submissionExecutorConnectionString,
            schemaName,
            submissionExecutorRoleName);
        SubmissionReconciliationOptions = new PostgresPolicyApprovalSubmissionReconciliationOptions(
            reconciliationConnectionString,
            schemaName,
            reconciliationRoleName);
        SubmissionRecoveryOptions = new PostgresPolicyApprovalSubmissionRecoveryOptions(
            recoveryConnectionString,
            schemaName,
            recoveryRoleName);
        SchemaName = schemaName;
        RuntimeRoleName = runtimeRoleName;
        SubmissionExecutorRoleName = submissionExecutorRoleName;
        ReconciliationRoleName = reconciliationRoleName;
        RecoveryRoleName = recoveryRoleName;
    }

    public string AdminConnectionString { get; }
    public PostgresPolicyApprovalOptions Options { get; }
    public PostgresPolicyApprovalSubmissionExecutorOptions SubmissionExecutorOptions { get; }
    public PostgresPolicyApprovalSubmissionReconciliationOptions SubmissionReconciliationOptions { get; }
    public PostgresPolicyApprovalSubmissionRecoveryOptions SubmissionRecoveryOptions { get; }
    public string SchemaName { get; }
    public string RuntimeRoleName { get; }
    public string SubmissionExecutorRoleName { get; }
    public string ReconciliationRoleName { get; }
    public string RecoveryRoleName { get; }

    public static async Task<PolicyApprovalTestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("INFRA_ERROR: DPS_TEST_POSTGRES is required; Policy Approval Integration fails rather than skipping or using a mock.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Port == 55434 || string.Equals(settings.Database, "dps_gbrain_company", StringComparison.Ordinal))
            throw new InvalidOperationException("Policy Approval Integration refuses PostgreSQL 55434 and the GBrain Company database.");

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            if (string.Equals(connection.Database, "dps_gbrain_company", StringComparison.Ordinal))
                throw new InvalidOperationException("Policy Approval Integration refuses the GBrain Company database.");
            await using var version = new NpgsqlCommand("SHOW server_version_num", connection) { CommandTimeout = 5 };
            var versionNumber = (string?)await version.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(versionNumber, "180004", StringComparison.Ordinal))
                throw new InvalidOperationException($"INFRA_ERROR: PostgreSQL 18.4 is required; server_version_num was '{versionNumber ?? "missing"}'.");
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException(
                "INFRA_ERROR: DPS_TEST_POSTGRES could not establish the required PostgreSQL 18.4 admin connection.",
                exception);
        }

        var suffix = Guid.NewGuid().ToString("N");
        var schemaName = "dps_policy_approval_" + suffix;
        var runtimeRoleName = "dps_policy_runtime_" + suffix;
        var submissionExecutorRoleName = "dps_policy_submit_" + suffix;
        var reconciliationRoleName = "dps_policy_reconcile_" + suffix;
        var recoveryRoleName = "dps_policy_recovery_" + suffix;
        var roles = new[]
        {
            (Name: runtimeRoleName, Password: Guid.NewGuid().ToString("N") + "Aa1"),
            (Name: submissionExecutorRoleName, Password: Guid.NewGuid().ToString("N") + "Aa1"),
            (Name: reconciliationRoleName, Password: Guid.NewGuid().ToString("N") + "Aa1"),
            (Name: recoveryRoleName, Password: Guid.NewGuid().ToString("N") + "Aa1")
        };

        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                foreach (var role in roles)
                {
                    await using var createRole = new NpgsqlCommand(
                        $"CREATE ROLE {role.Name} LOGIN PASSWORD '{role.Password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS",
                        connection) { CommandTimeout = 5 };
                    await createRole.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var seedLegacySubmissionBypass = new NpgsqlCommand(
                    $"""
                    CREATE SCHEMA {schemaName};
                    CREATE FUNCTION {schemaName}.assert_submission_runtime_role()
                    RETURNS void LANGUAGE sql SECURITY DEFINER
                    AS 'SELECT NULL::void';
                    CREATE FUNCTION {schemaName}.begin_approval_submission(uuid, jsonb, text, jsonb, text)
                    RETURNS text LANGUAGE sql SECURITY DEFINER
                    AS 'SELECT ''LEGACY_BYPASS''::text';
                    GRANT EXECUTE ON FUNCTION {schemaName}.assert_submission_runtime_role() TO {runtimeRoleName};
                    GRANT EXECUTE ON FUNCTION {schemaName}.begin_approval_submission(uuid, jsonb, text, jsonb, text) TO {runtimeRoleName};
                    """,
                    connection) { CommandTimeout = 5 };
                await seedLegacySubmissionBypass.ExecuteNonQueryAsync(cancellationToken);
            }
            await new PostgresPolicyApprovalMigrator(
                new PolicyApprovalMigrationOptions(
                    connectionString,
                    schemaName,
                    runtimeRoleName,
                    submissionExecutorRoleName,
                    reconciliationRoleName,
                    recoveryRoleName))
                .InitializeAsync(cancellationToken);
            var runtimeConnection = BuildRoleConnectionString(connectionString, roles[0]);
            var executorConnection = BuildRoleConnectionString(connectionString, roles[1]);
            var reconciliationConnection = BuildRoleConnectionString(connectionString, roles[2]);
            var recoveryConnection = BuildRoleConnectionString(connectionString, roles[3]);
            var database = new PolicyApprovalTestDatabase(
                connectionString,
                runtimeConnection,
                executorConnection,
                reconciliationConnection,
                recoveryConnection,
                schemaName,
                runtimeRoleName,
                submissionExecutorRoleName,
                reconciliationRoleName,
                recoveryRoleName);
            foreach (var (roleConnectionString, expectedRole) in new[]
                     {
                         (database.Options.RuntimeConnectionString, runtimeRoleName),
                         (database.SubmissionExecutorOptions.ExecutorConnectionString, submissionExecutorRoleName),
                         (database.SubmissionReconciliationOptions.ReconciliationConnectionString, reconciliationRoleName),
                         (database.SubmissionRecoveryOptions.RecoveryConnectionString, recoveryRoleName)
                     })
            {
                await using var roleConnection = new NpgsqlConnection(roleConnectionString);
                await roleConnection.OpenAsync(cancellationToken);
                await using var currentRole = new NpgsqlCommand("SELECT current_user", roleConnection) { CommandTimeout = 5 };
                Assert.Equal(expectedRole, await currentRole.ExecuteScalarAsync(cancellationToken));
            }
            return database;
        }
        catch
        {
            await CleanupAsync(connectionString, schemaName, roles.Select(static role => role.Name).ToArray());
            throw;
        }
    }

    private static string BuildRoleConnectionString(
        string connectionString,
        (string Name, string Password) role)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5,
            Username = role.Name,
            Password = role.Password
        };
        return builder.ConnectionString;
    }

    public PostgresPolicyApprovalService CreateService(
        ECDsa evaluationSigner,
        ECDsa revocationSigner,
        PolicyApprovalMutationFaultInjector? faultInjector = null,
        PolicyApprovalSubmissionAuthorityTopology? authorityTopology = null)
        => PostgresPolicyApprovalService.CreateProduction(
            Options,
            authorityTopology ?? PolicyApprovalTestAuthorities.TopologyFor(evaluationSigner, revocationSigner),
            evaluationSigner.ExportSubjectPublicKeyInfo(),
            PolicyApprovalTestAuthorities.PromotionFor(evaluationSigner)
                .ExportSubjectPublicKeyInfo(),
            revocationSigner.ExportSubjectPublicKeyInfo(),
            faultInjector);

    public async Task AppendRuntimeStateAsync(
        PolicyRuntimeStateRevisionV1 state,
        CancellationToken cancellationToken)
    {
        state.Validate();
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {SchemaName}.policy_runtime_revisions
            (soul_id, device_binding_id, platform_account_id, revision, state_status,
             policy_version, enabled_policy_ids, kill_switch_enabled, remaining_rate_budget,
             platform_authorized, platform_authorization_id, execution_enabled,
             release_bom_sha256, valid_until, state_sha256)
            VALUES
            (@soul_id, @device_binding_id, @platform_account_id, @revision, @state_status,
             @policy_version, @enabled_policy_ids, @kill_switch_enabled, @remaining_rate_budget,
             @platform_authorized, @platform_authorization_id, @execution_enabled,
             @release_bom_sha256, @valid_until, @state_sha256)
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("soul_id", state.SoulId);
        command.Parameters.AddWithValue("device_binding_id", state.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", state.PlatformAccountId);
        command.Parameters.AddWithValue("revision", state.Revision);
        command.Parameters.AddWithValue("state_status", state.StateStatus);
        command.Parameters.AddWithValue("policy_version", state.PolicyVersion);
        command.Parameters.AddWithValue("enabled_policy_ids", NpgsqlDbType.Array | NpgsqlDbType.Text, state.EnabledPolicyIds.ToArray());
        command.Parameters.AddWithValue("kill_switch_enabled", state.KillSwitchEnabled);
        command.Parameters.AddWithValue("remaining_rate_budget", state.RemainingRateBudget);
        command.Parameters.AddWithValue("platform_authorized", state.PlatformAuthorized);
        command.Parameters.AddWithValue("platform_authorization_id", NpgsqlDbType.Text, (object?)state.PlatformAuthorizationId ?? DBNull.Value);
        command.Parameters.AddWithValue("execution_enabled", state.ExecutionEnabled);
        command.Parameters.AddWithValue("release_bom_sha256", state.ReleaseBomSha256);
        command.Parameters.AddWithValue("valid_until", state.ValidUntil);
        command.Parameters.AddWithValue("state_sha256", PolicyRuntimeStateCommitment.ComputeSha256(state));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AssertQuarantineContainsHashesOnlyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var columns = new NpgsqlCommand(
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema_name
              AND table_name = 'approval_idempotency_quarantine'
            """, connection) { CommandTimeout = 5 };
        columns.Parameters.AddWithValue("schema_name", SchemaName);
        var names = new List<string>();
        await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        Assert.DoesNotContain("soul_id", names);
        Assert.DoesNotContain("device_binding_id", names);
        Assert.DoesNotContain("platform_account_id", names);
        Assert.DoesNotContain("idempotency_key", names);
        Assert.Contains("scope_sha256", names);
        Assert.Contains("idempotency_sha256", names);
    }

    public static async Task AssertSqlStateAsync(
        NpgsqlConnection connection,
        string sql,
        string expectedSqlState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal(expectedSqlState, exception.SqlState);
    }

    public async ValueTask DisposeAsync()
        => await CleanupAsync(
            AdminConnectionString,
            SchemaName,
            RuntimeRoleName,
            SubmissionExecutorRoleName,
            ReconciliationRoleName,
            RecoveryRoleName);

    private static async Task CleanupAsync(string connectionString, string schemaName, params string[] roleNames)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using (var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schemaName} CASCADE", connection) { CommandTimeout = 5 })
        {
            await dropSchema.ExecuteNonQueryAsync(CancellationToken.None);
        }
        foreach (var roleName in roleNames.Reverse())
        {
            await using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS {roleName}", connection) { CommandTimeout = 5 };
            await dropRole.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
