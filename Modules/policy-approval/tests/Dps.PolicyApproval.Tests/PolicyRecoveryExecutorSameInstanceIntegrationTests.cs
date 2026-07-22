using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost;
using Dps.ControlPlaneHost.Contracts;
using Dps.ExecutorGateway;
using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using Xunit;

namespace Dps.PolicyApproval.Tests;

/// <summary>
/// REAL_POSTGRESQL (18.4, DPS_TEST_POSTGRES) M1B/R0-C same-instance proof,
/// policy leg: ONE <see cref="ActiveReleaseBindingAuthority"/> instance backed
/// by ONE <see cref="PostgresReleaseBindingTruthStore"/> (unique schema per
/// run) is injected as the composition-fixed
/// <see cref="IActiveReleaseBindingReader"/> into BOTH real consumer code
/// paths on the same running instance:
/// (i) the full production policy recovery pipeline — the real
/// <see cref="PolicyApprovalSubmissionRecoveryClient"/> composition driven
/// end-to-end through the internal segmented lease methods
/// (BeginSubmissionAsync / QuarantineUnknownSubmissionAsync) to a durable
/// RECONCILED_NOT_SUBMITTED predecessor, then the real RecoverAsync whose
/// commit-time RequireActiveReleaseBindingMatchesRecovery re-reads exactly
/// this instance; and (ii) the real executor-side provider-backed
/// <see cref="ControlPlaneHostActiveReleaseBomReader"/> adapter over the
/// same instance.
///
/// Governance wording (PR#6 v2): "policy 与 executor 两侧的消费代码路径，
/// 在指向同一个正在运行的 control-plane-host 实例时，观测到一致的
/// generation/token/status". Assertion vocabulary: "same running instance,
/// same generation/token/status — engineering/integration-level proof per
/// RebuildPlan §4.3 (PR#6 v2); not a production-topology claim (M4)".
///
/// The control-plane-host-side sibling suite
/// (Dps.ControlPlaneHost.ActiveBindingSameInstance.Tests) proves composition,
/// shared observations, restart, and isolation from the host side, but it
/// cannot drive the real RecoverAsync: the segmented lease methods that
/// create the RECONCILED_NOT_SUBMITTED predecessor are internal to
/// policy-approval. This suite runs inside policy-approval's own test
/// assembly (which holds InternalsVisibleTo) and closes that leg. All
/// release-binding state is established exclusively through the production
/// Activate / Revoke API — never by direct journal writes — and all policy
/// submission state through the same production lifecycle composition used
/// by PostgresPolicyApprovalRecoveryReleaseBindingIntegrationTests.
/// </summary>
public sealed partial class PostgresPolicyApprovalIntegrationTests
{
    private static readonly DateTimeOffset SameInstanceNow =
        new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact, Trait("Category", "Integration")]
    public async Task SameInstanceActivationIsObservedIdenticallyByPolicyRecoveryAndExecutorConsumers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await using var bindings = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bomSigner = new SameInstanceBomSigner();
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var releaseBindingStore = bindings.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [bomSigner.TrustKey], releaseBindingStore, () => SameInstanceNow);
        var (bom, token) = bomSigner.SignBom("policy-sameinst-bom-1", 1, null);
        authority.Activate(DeviceA, bom, token);
        var liveSha256 = SameInstanceBomSigner.Sha256Hex(bom);
        // The SAME running instance backs both consumer paths: the real
        // executor adapter and the real recovery composition's commit-time
        // RequireActiveReleaseBindingMatchesRecovery re-read.
        var executorReader = new ControlPlaneHostActiveReleaseBomReader(authority);
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
            liveSha256,
            1,
            "same-instance-accept",
            cancellationToken);
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            releaseBindingStore);

        var authorized = await recoveryClient.AuthorizeSubmissionRecoveryAsync(
            SignRecovery(recoverySigner, RecoveryPinnedToLiveBinding(
                prepared.Intent,
                prepared.Reconciliation,
                liveSha256,
                1,
                Sha256Hex("same-instance-authorization:accept"),
                Sha256Hex("same-instance-native:accept"))),
            cancellationToken);

        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 1, cancellationToken);

        // Field-level equality of the two consumer observations of the same
        // running instance: the executor adapter DTO and the exact binding
        // the policy commit-time check accepted against.
        var executorObserved = await executorReader.ReadVerifiedActiveAsync(DeviceA, cancellationToken);
        Assert.NotNull(executorObserved);
        Assert.True(authority.TryReadActive(DeviceA, out var policyObserved));
        Assert.NotNull(policyObserved);
        Assert.Equal("active", policyObserved!.Status);
        Assert.Equal(policyObserved.ReleaseBomSha256, executorObserved!.ReleaseBomSha256);
        Assert.Equal(policyObserved.Generation, executorObserved.Generation);
        Assert.Equal(policyObserved.ExecutionTokenBase64, executorObserved.ExecutionTokenBase64);
        Assert.Equal(liveSha256, executorObserved.ReleaseBomSha256);
        Assert.Equal(1, executorObserved.Generation);
        Assert.Equal(token, executorObserved.ExecutionTokenBase64);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameInstanceTransitionRejectsStalePolicyRecoveryWhileExecutorObservesCurrentBinding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await using var bindings = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bomSigner = new SameInstanceBomSigner();
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var releaseBindingStore = bindings.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [bomSigner.TrustKey], releaseBindingStore, () => SameInstanceNow);
        var (bom1, token1) = bomSigner.SignBom("policy-sameinst-bom-1", 1, null);
        authority.Activate(DeviceA, bom1, token1);
        var bom1Sha256 = SameInstanceBomSigner.Sha256Hex(bom1);
        var executorReader = new ControlPlaneHostActiveReleaseBomReader(authority);
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
            bom1Sha256,
            1,
            "same-instance-transition",
            cancellationToken);
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            releaseBindingStore);
        var staleRecovery = SignRecovery(recoverySigner, RecoveryPinnedToLiveBinding(
            prepared.Intent,
            prepared.Reconciliation,
            bom1Sha256,
            1,
            Sha256Hex("same-instance-authorization:transition"),
            Sha256Hex("same-instance-native:transition")));

        // Real activation of BOM2 on the SAME running instance; the runtime
        // generation advances to 2 before the stale envelope commits.
        var (bom2, token2) = bomSigner.SignBom("policy-sameinst-bom-2", 2, bom1);
        authority.Activate(DeviceA, bom2, token2);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(staleRecovery, cancellationToken));

        var persisted = await recoveryClient.ReadSubmissionAsync(prepared.Intent.SubmissionAttemptId, cancellationToken);
        Assert.Equal(ApprovalSubmissionStateV1.ReconciledNotSubmitted, persisted.State.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 0, cancellationToken);

        // The executor adapter simultaneously observes the CURRENT (BOM2)
        // values from the same instance that rejected the stale envelope.
        var executorObserved = await executorReader.ReadVerifiedActiveAsync(DeviceA, cancellationToken);
        Assert.NotNull(executorObserved);
        Assert.True(authority.TryReadActive(DeviceA, out var policyObserved));
        Assert.NotNull(policyObserved);
        Assert.Equal("active", policyObserved!.Status);
        Assert.Equal(policyObserved.ReleaseBomSha256, executorObserved!.ReleaseBomSha256);
        Assert.Equal(policyObserved.Generation, executorObserved.Generation);
        Assert.Equal(policyObserved.ExecutionTokenBase64, executorObserved.ExecutionTokenBase64);
        Assert.Equal(SameInstanceBomSigner.Sha256Hex(bom2), executorObserved.ReleaseBomSha256);
        Assert.Equal(2, executorObserved.Generation);
        Assert.Equal(token2, executorObserved.ExecutionTokenBase64);
        Assert.NotEqual(bom1Sha256, executorObserved.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameInstanceRevocationFailsBothConsumersClosedAndRestartedAuthorityServesBothConsistently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await using var bindings = await SameInstanceReleaseBindingDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bomSigner = new SameInstanceBomSigner();
        var authorityTopology = SubmissionTopology(evaluationSigner, revocationSigner, fenceSigner, executorSigner, reconciliationSigner, recoverySigner, stateSigner);
        var releaseBindingStore = bindings.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [bomSigner.TrustKey], releaseBindingStore, () => SameInstanceNow);
        var (bom1, token1) = bomSigner.SignBom("policy-sameinst-bom-1", 1, null);
        authority.Activate(DeviceA, bom1, token1);
        var bom1Sha256 = SameInstanceBomSigner.Sha256Hex(bom1);
        var executorReader = new ControlPlaneHostActiveReleaseBomReader(authority);
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
            bom1Sha256,
            1,
            "same-instance-revocation",
            cancellationToken);
        using var recoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            releaseBindingStore);
        var staleRecovery = SignRecovery(recoverySigner, RecoveryPinnedToLiveBinding(
            prepared.Intent,
            prepared.Reconciliation,
            bom1Sha256,
            1,
            Sha256Hex("same-instance-authorization:revocation"),
            Sha256Hex("same-instance-native:revocation")));

        // Revocation on the same running instance: both consumer paths fail
        // closed — the executor adapter reads null and the real RecoverAsync
        // rejects the envelope and persists nothing.
        authority.Revoke(DeviceA, 1);
        Assert.Null(await executorReader.ReadVerifiedActiveAsync(DeviceA, cancellationToken));
        Assert.False(authority.TryReadActive(DeviceA, out _));
        await Assert.ThrowsAsync<ActiveReleaseBindingException>(() =>
            recoveryClient.AuthorizeSubmissionRecoveryAsync(staleRecovery, cancellationToken));
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 0, cancellationToken);

        // Process restart: a SECOND authority over the same store replays
        // the journal with full re-verification; the revoked truth survives
        // and both consumer paths are re-pointed at the restarted instance.
        var restartedReleaseBindingStore = bindings.CreateStore();
        var restarted = new ActiveReleaseBindingAuthority(
            [bomSigner.TrustKey], restartedReleaseBindingStore, () => SameInstanceNow);
        Assert.False(restarted.TryReadActive(DeviceA, out _));
        var restartedExecutorReader = new ControlPlaneHostActiveReleaseBomReader(restarted);
        Assert.Null(await restartedExecutorReader.ReadVerifiedActiveAsync(DeviceA, cancellationToken));

        // Re-activation on the restarted instance advances the runtime
        // generation; both consumers then observe the identical live truth
        // and the re-pinned envelope is accepted through the same instance.
        var (bom2, token2) = bomSigner.SignBom("policy-sameinst-bom-2", 2, bom1);
        restarted.Activate(DeviceA, bom2, token2);
        var bom2Sha256 = SameInstanceBomSigner.Sha256Hex(bom2);
        using var restartedRecoveryClient = CreateRecoveryClient(
            database,
            authorityTopology,
            executorSigner,
            recoverySigner,
            stateSigner,
            restartedReleaseBindingStore);

        var authorized = await restartedRecoveryClient.AuthorizeSubmissionRecoveryAsync(
            SignRecovery(recoverySigner, RecoveryPinnedToLiveBinding(
                prepared.Intent,
                prepared.Reconciliation,
                bom2Sha256,
                2,
                Sha256Hex("same-instance-authorization:restart"),
                Sha256Hex("same-instance-native:restart"))),
            cancellationToken);

        Assert.Equal(ApprovalSubmissionStateV1.RecoveryAuthorized, authorized.State);
        await AssertRecoveryCountAsync(database, prepared.Intent.SubmissionAttemptId, 1, cancellationToken);

        var executorObserved = await restartedExecutorReader.ReadVerifiedActiveAsync(DeviceA, cancellationToken);
        Assert.NotNull(executorObserved);
        Assert.True(restarted.TryReadActive(DeviceA, out var policyObserved));
        Assert.NotNull(policyObserved);
        Assert.Equal("active", policyObserved!.Status);
        Assert.Equal(policyObserved.ReleaseBomSha256, executorObserved!.ReleaseBomSha256);
        Assert.Equal(policyObserved.Generation, executorObserved.Generation);
        Assert.Equal(policyObserved.ExecutionTokenBase64, executorObserved.ExecutionTokenBase64);
        Assert.Equal(bom2Sha256, executorObserved.ReleaseBomSha256);
        Assert.Equal(2, executorObserved.Generation);
        Assert.Equal(token2, executorObserved.ExecutionTokenBase64);
    }

    /// <summary>
    /// Drives the same production lifecycle path as W2's
    /// PrepareRecoverableSubmissionAsync (evaluate → fence acquire →
    /// segmented begin → crash quarantine → independent reconciliation to
    /// RECONCILED_NOT_SUBMITTED), but pins the policy runtime state and the
    /// submission intent to the live binding facts of the real authority
    /// under test instead of fixture constants.
    /// </summary>
    private static async Task<(
        PolicyApprovalAuthoritativeSnapshot Snapshot,
        ApprovalSubmissionIntentV1 Intent,
        ApprovalSubmissionReconciliationV1 Reconciliation)>
        PrepareSameInstanceRecoverableSubmissionAsync(
            PolicyApprovalTestDatabase database,
            PolicyApprovalSubmissionAuthorityTopology authorityTopology,
            ECDsa evaluationSigner,
            ECDsa revocationSigner,
            ECDsa fenceSigner,
            ECDsa executorSigner,
            ECDsa reconciliationSigner,
            ECDsa recoverySigner,
            ECDsa stateSigner,
            string liveReleaseBomSha256,
            long liveReleaseBomGeneration,
            string label,
            CancellationToken cancellationToken)
    {
        await database.AppendRuntimeStateAsync(
            new PolicyRuntimeStateRevisionV1(
                SoulA, DeviceA, AccountA, 1,
                PolicyRuntimeStateRevisionV1.Active, "1.0.0",
                DeterministicPolicyEvaluator.KnownPolicies.Order(StringComparer.Ordinal).ToArray(),
                false, 100, true, PlatformAuthorization, true, liveReleaseBomSha256,
                DateTimeOffset.UtcNow.AddHours(1)),
            cancellationToken);
        ActionProposalV1 proposal;
        PolicyApprovalAuthoritativeSnapshot snapshot;
        using (var service = database.CreateService(
                   evaluationSigner,
                   revocationSigner,
                   authorityTopology: authorityTopology))
        {
            proposal = Proposal(SoulA, DeviceA, AccountA, "idem-" + label);
            snapshot = (await service.EvaluateAndAppendAsync(
                proposal,
                SignEvaluationWithLiveReleaseBom(evaluationSigner, proposal, liveReleaseBomSha256),
                cancellationToken)).Snapshot;
        }

        var request = FenceRequest(snapshot);
        var firstIntent = SignSubmissionIntent(executorSigner, SubmissionIntent(
            snapshot,
            proposal,
            request,
            releaseBomGeneration: liveReleaseBomGeneration));
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

        return (snapshot, firstIntent, reconciliation);
    }

    /// <summary>
    /// Same signed evaluation envelope (independently signed exact-scope
    /// execution promotion included) as the shared SignEvaluation fixture,
    /// but pinning the live authority binding digest instead of the fixture
    /// constant, because the trust provider requires the envelope, the
    /// promotion, and the policy runtime state to name the same active
    /// release BOM sha256.
    /// </summary>
    private static PolicyEvaluationEnvelope SignEvaluationWithLiveReleaseBom(
        ECDsa signer,
        ActionProposalV1 proposal,
        string liveReleaseBomSha256)
    {
        var promotionValidUntil = DateTimeOffset.UtcNow.AddMinutes(5);
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
            liveReleaseBomSha256,
            1,
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
            if (promotionSignature is not null) CryptographicOperations.ZeroMemory(promotionSignature);
        }

        var unsigned = new PolicyEvaluationEnvelope(
            "control-plane-host",
            "policy:evaluate",
            proposal.ProposalId,
            PolicyAuthorizationBinding.ComputeProposalSha256(proposal),
            liveReleaseBomSha256,
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

    /// <summary>
    /// Same human recovery envelope shape as the shared Recovery fixture,
    /// but with the next release BOM facts (sha256, runtime generation)
    /// pinned explicitly to the live active binding under test — the exact
    /// pair RequireActiveReleaseBindingMatchesRecovery re-reads from the
    /// composition-fixed reader at commit time.
    /// </summary>
    private static ApprovalSubmissionRecoveryV1 RecoveryPinnedToLiveBinding(
        ApprovalSubmissionIntentV1 intent,
        ApprovalSubmissionReconciliationV1 reconciliation,
        string liveReleaseBomSha256,
        long liveReleaseBomGeneration,
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
            Guid.NewGuid(), Guid.NewGuid(), intent.Attempt + 1,
            intent.SoulId, intent.DeviceBindingId, intent.PlatformAccountId, intent.TraceId, intent.IdempotencyKey,
            liveReleaseBomSha256, liveReleaseBomGeneration,
            nextAuthorizationSha256, nextNativeBindingSha256,
            "human_" + Sha256Hex("human:" + intent.SubmissionAttemptId),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(4), "internal",
            Convert.ToBase64String(new byte[64]));

    /// <summary>
    /// Runtime-generated RSA-PSS release BOM signer emitting the exact
    /// canonical sorted compact wire the activation authority accepts
    /// (mirrors the control-plane-host same-instance fixture over public
    /// APIs only).
    /// </summary>
    private sealed class SameInstanceBomSigner : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);

        internal SameInstanceBomSigner(
            string keyId = "test-bom-key-v1",
            string identity = "test-release-controller")
        {
            KeyId = keyId;
            Identity = identity;
        }

        internal string KeyId { get; }
        internal string Identity { get; }

        internal ReleaseBomTrustKey TrustKey
        {
            get
            {
                var parameters = _rsa.ExportParameters(false);
                return new ReleaseBomTrustKey(
                    KeyId,
                    Identity,
                    Convert.ToHexStringLower(parameters.Modulus!),
                    65537);
            }
        }

        internal static string Sha256Hex(byte[] value)
            => Convert.ToHexStringLower(SHA256.HashData(value));

        internal (byte[] Bom, string Token) SignBom(
            string bomId,
            long signerGeneration,
            byte[]? previousBom)
        {
            var token = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes("token:" + bomId)));
            var tokenBytes = Convert.FromBase64String(token);
            var payload = new JsonObject
            {
                ["schema_version"] = "dps.release-bom/v1",
                ["bom_id"] = bomId,
                ["status"] = "SIGNED",
                ["integration_commit"] = new string('a', 40),
                ["created_at"] = "2026-07-14T00:00:00.0000001Z",
                ["release_bom_generation"] = signerGeneration,
                ["activation_token_sha256"] = Convert.ToHexStringLower(SHA256.HashData(tokenBytes)),
                ["modules"] = new JsonArray(),
                ["instruction_hashes"] = new JsonObject(),
                ["contracts"] = new JsonArray(),
                ["database_versions"] = new JsonObject(),
                ["dependency_dag_sha256"] = new string('b', 64),
                ["compatibility_matrix_sha256"] = new string('c', 64),
                ["feature_flags"] = new JsonObject(),
                ["kill_switches"] = new JsonArray(),
                ["ai_toolchain"] = new JsonObject(),
                ["evidence"] = new JsonArray(),
                ["risk"] = new JsonObject(),
                ["release_approval"] = new JsonObject(),
                ["rollout"] = new JsonObject(),
                ["rollback"] = new JsonObject(),
                ["previous_stable_bom"] = previousBom is null
                    ? null
                    : (JsonNode)("bom-previous-" + bomId),
                ["previous_stable_bom_sha256"] = previousBom is null
                    ? null
                    : Sha256Hex(previousBom),
                ["native_stop_authorities"] = new JsonArray(),
                ["device_route_assignment_authorities"] = new JsonArray(),
                ["native_stop_challenge_authorities"] = new JsonArray()
            };
            using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
            var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
            var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
                .Concat(canonical)
                .ToArray();
            var signature = _rsa.SignData(
                message,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            payload["signature"] = new JsonObject
            {
                ["algorithm"] = "rsa-pss-sha256",
                ["key_id"] = KeyId,
                ["value"] = Convert.ToBase64String(signature)
            };
            using var fullDocument = JsonDocument.Parse(payload.ToJsonString());
            return (ReleaseBomCanonicalJson.Serialize(fullDocument.RootElement), token);
        }

        public void Dispose() => _rsa.Dispose();
    }

    /// <summary>
    /// One disposable release binding truth database per test: a fresh
    /// least-privilege runtime login role and a fresh unique schema created
    /// by the production migrator; disposal drops both. Missing
    /// DPS_TEST_POSTGRES is a hard failure, never a skip.
    /// </summary>
    private sealed class SameInstanceReleaseBindingDatabase : IAsyncDisposable
    {
        private readonly string _migrationConnectionString;
        private readonly string _runtimeConnectionString;
        private readonly string _schemaName;
        private readonly string _runtimeRoleName;
        private readonly string _migrationRoleName;

        private SameInstanceReleaseBindingDatabase(
            string migrationConnectionString,
            string runtimeConnectionString,
            string schemaName,
            string runtimeRoleName,
            string migrationRoleName)
        {
            _migrationConnectionString = migrationConnectionString;
            _runtimeConnectionString = runtimeConnectionString;
            _schemaName = schemaName;
            _runtimeRoleName = runtimeRoleName;
            _migrationRoleName = migrationRoleName;
        }

        private static string RequireConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES is required for REAL_POSTGRESQL same-instance Integration; missing infrastructure is not a skip or pass.");
            }

            return value;
        }

        internal static async Task<SameInstanceReleaseBindingDatabase> CreateAsync(
            CancellationToken cancellationToken)
        {
            var migrationConnectionString = RequireConnectionString();
            var migrationBuilder = new NpgsqlConnectionStringBuilder(migrationConnectionString);
            if (migrationBuilder.Port == 55434
                || string.Equals(
                    migrationBuilder.Database,
                    "dps_gbrain_company",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Same-instance Integration refuses the persistent GBrain Company database.");
            }

            if (string.IsNullOrWhiteSpace(migrationBuilder.Username))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES requires an explicit migration username.");
            }

            var suffix = Guid.NewGuid().ToString("N")[..20];
            var schemaName = "policy_sameinst_it_" + suffix;
            var runtimeRoleName = "policy_sameinst_rt_" + suffix;
            var migrationRoleName = migrationBuilder.Username;
            var passwordBytes = RandomNumberGenerator.GetBytes(32);
            string runtimePassword;
            try
            {
                runtimePassword = Convert.ToHexStringLower(passwordBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            try
            {
                await using (var admin = new NpgsqlConnection(migrationConnectionString))
                {
                    await admin.OpenAsync(cancellationToken);
                    await using (var version = new NpgsqlCommand("SHOW server_version_num", admin))
                    {
                        var actual = (string?)await version.ExecuteScalarAsync(cancellationToken);
                        if (!string.Equals(actual, "180004", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Same-instance Integration requires exact PostgreSQL 18.4; server_version_num was '{actual ?? "missing"}'.");
                        }
                    }

                    string createRoleSql;
                    await using (var formatRole = new NpgsqlCommand(
                        "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', @role_name, @password)",
                        admin))
                    {
                        formatRole.Parameters.AddWithValue("role_name", runtimeRoleName);
                        formatRole.Parameters.AddWithValue("password", runtimePassword);
                        createRoleSql = (string)(await formatRole.ExecuteScalarAsync(cancellationToken)
                            ?? throw new InvalidOperationException(
                                "PostgreSQL did not produce the controlled CREATE ROLE statement."));
                    }

                    await using var createRole = new NpgsqlCommand(createRoleSql, admin);
                    await createRole.ExecuteNonQueryAsync(cancellationToken);
                }

                var runtimeBuilder = new NpgsqlConnectionStringBuilder(migrationConnectionString)
                {
                    Username = runtimeRoleName,
                    Password = runtimePassword,
                    Pooling = false,
                    Options = string.Empty,
                    LogParameters = false,
                    IncludeErrorDetail = false,
                    PersistSecurityInfo = false
                };
                var migrator = new PostgresReleaseBindingTruthMigrator(
                    new PostgresReleaseBindingMigrationOptions(
                        migrationConnectionString,
                        schemaName,
                        runtimeRoleName));
                await migrator.InitializeAsync(cancellationToken);
                return new SameInstanceReleaseBindingDatabase(
                    migrationConnectionString,
                    runtimeBuilder.ConnectionString,
                    schemaName,
                    runtimeRoleName,
                    migrationRoleName);
            }
            catch
            {
                await CleanupAsync(
                    migrationConnectionString,
                    schemaName,
                    runtimeRoleName,
                    CancellationToken.None);
                throw;
            }
        }

        internal PostgresReleaseBindingTruthStore CreateStore()
            => new(new PostgresReleaseBindingTruthStoreOptions(
                _runtimeConnectionString,
                _schemaName,
                _runtimeRoleName,
                _migrationRoleName));

        public async ValueTask DisposeAsync()
            => await CleanupAsync(
                _migrationConnectionString,
                _schemaName,
                _runtimeRoleName,
                CancellationToken.None);

        private static async Task CleanupAsync(
            string migrationConnectionString,
            string schemaName,
            string runtimeRoleName,
            CancellationToken cancellationToken)
        {
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schemaName);
            var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRoleName);
            await using var admin = new NpgsqlConnection(migrationConnectionString);
            await admin.OpenAsync(cancellationToken);
            await using var cleanup = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE; DROP ROLE IF EXISTS {quotedRole}",
                admin);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
