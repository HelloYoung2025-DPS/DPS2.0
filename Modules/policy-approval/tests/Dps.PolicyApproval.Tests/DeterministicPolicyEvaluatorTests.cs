using Dps.Planner.Contracts;
using Dps.PolicyApproval.Contracts;
using System.Security.Cryptography;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed class DeterministicPolicyEvaluatorTests
{
    private const string Soul = "soul_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Device = "db_cccccccccccccccccccccccccccccccc";
    private const string OtherDevice = "db_dddddddddddddddddddddddddddddddd";
    private const string Account = "pa_cccccccccccccccccccccccccccccccc";
    private const string OtherAccount = "pa_dddddddddddddddddddddddddddddddd";
    private const string Trace = "trace_cccccccccccccccccccccccccccccccc";
    private const string Idempotency = "idem_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ShadowProposalIsDeniedEvenWhenTrustedChecksPass()
    {
        var result = await Evaluate(Evaluator(Context()), Proposal(), Envelope());
        Assert.Equal(ApprovalDecisionV1.Denied, result.Decision);
        Assert.Contains("SHADOW_ONLY", result.DenialReasons);
    }

    [Theory]
    [InlineData(true, 1, "KILL_SWITCH_ACTIVE")]
    [InlineData(false, 0, "RATE_BUDGET_EXHAUSTED")]
    [Trait("Category", "Unit")]
    public async Task KillSwitchAndRateBudgetFailClosed(bool killSwitch, int budget, string reason)
    {
        var result = await Evaluate(Evaluator(Context() with { KillSwitchEnabled = killSwitch, RemainingRateBudget = budget }), Proposal(), Envelope());
        Assert.Contains(reason, result.DenialReasons);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SideEffectWithoutTrustedPlatformAuthorizationIsDenied()
    {
        var result = await Evaluate(Evaluator(Context() with { PlatformAuthorized = false, PlatformAuthorizationId = null }), Proposal("fixture.tap", true), Envelope());
        Assert.Contains("PLATFORM_AUTHORIZATION_REQUIRED", result.DenialReasons);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OnlyTrustedExecutionPromotionCanProduceNonShadowApproval()
    {
        var proposal = Proposal();
        var promotion = Promotion(proposal);
        var promoted = await Evaluate(
            Evaluator(Context() with
            {
                ExecutionAuthorized = true,
                ExecutionPromotionSha256 = ActionExecutionPromotionV1Canonical.ComputeSignedSha256(promotion)
            }),
            proposal,
            Envelope(proposal) with
            {
                RequestedMode = PolicyEvaluationEnvelope.Execute,
                ExecutionPromotion = promotion
            });
        Assert.Equal(ApprovalDecisionV1.Approved, promoted.Decision);
        Assert.False(promoted.ShadowOnly);

        var denied = await Evaluate(
            Evaluator(Context()),
            proposal,
            Envelope(proposal) with { RequestedMode = PolicyEvaluationEnvelope.Execute });
        Assert.Equal(ApprovalDecisionV1.Denied, denied.Decision);
        Assert.True(denied.ShadowOnly);
        Assert.Contains("SHADOW_ONLY", denied.DenialReasons);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ModelOrUnauthenticatedCallerCannotSelfReportApprovalContext()
    {
        var provider = new FakeTrustProvider(Context(), authorized: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await Evaluate(new DeterministicPolicyEvaluator(provider), Proposal(), Envelope() with { CallerModule = "model" }); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await Evaluate(Evaluator(Context()), Proposal(), Envelope() with { SignatureBase64 = "" }); });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnknownPolicyFailsClosed()
    {
        var policies = new HashSet<string>(DeterministicPolicyEvaluator.KnownPolicies, StringComparer.Ordinal) { "UNKNOWN-POLICY-999" };
        await Assert.ThrowsAsync<NotSupportedException>(async () => { await Evaluate(Evaluator(Context() with { EnabledPolicyIds = policies }), Proposal(), Envelope()); });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CrossSoulDeviceAndAccountTrustedContextIsRejected()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await Evaluate(Evaluator(Context() with { SoulId = "soul_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" }), Proposal(), Envelope()); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await Evaluate(Evaluator(Context() with { DeviceBindingId = OtherDevice }), Proposal(), Envelope()); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await Evaluate(Evaluator(Context() with { PlatformAccountId = OtherAccount }), Proposal(), Envelope()); });
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task InvalidIdentityOrTrustEvidenceCannotCrossPolicyBoundary()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => { await Evaluate(Evaluator(Context()), Proposal() with { SoulId = Guid.NewGuid().ToString() }, Envelope()); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await Evaluate(Evaluator(Context() with { TrustEvidenceSha256 = "forged" }), Proposal(), Envelope()); });
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task ForgedModelAuthorityAndUnknownMajorAreRejected()
    {
        var denied = await Evaluate(Evaluator(Context()), Proposal(), Envelope());
        Assert.Throws<NotSupportedException>(() => (denied with { Authority = "model" }).Validate());
        Assert.Throws<NotSupportedException>(() => (denied with { SchemaVersion = "2.0.0" }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task RealEcdsaTrustProviderBindsCallerProposalAndSignedBom()
    {
        var proposal = Proposal(); using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var promoter = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = SignEnvelope(signer, Envelope(proposal));
        using var provider = new EcdsaPolicyTrustProvider(signer.ExportSubjectPublicKeyInfo(), promoter.ExportSubjectPublicKeyInfo(), new FakeRuntimeStateSource(RuntimeState()));
        var result = await Evaluate(new DeterministicPolicyEvaluator(provider), proposal, signed); Assert.Equal(ApprovalDecisionV1.Denied, result.Decision); Assert.Contains("SHADOW_ONLY", result.DenialReasons);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await Evaluate(new DeterministicPolicyEvaluator(provider), proposal, signed with { ReleaseBomSha256 = new string('b', 64) }); });
        await Assert.ThrowsAsync<ArgumentException>(async () => { await Evaluate(new DeterministicPolicyEvaluator(provider), proposal with { SoulId = "soul_ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" }, signed); });
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task IndependentPromotionBindsExactProposalRuntimeBomAndSignature()
    {
        var proposal = Proposal();
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var promotionSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = new EcdsaPolicyTrustProvider(
            evaluationSigner.ExportSubjectPublicKeyInfo(),
            promotionSigner.ExportSubjectPublicKeyInfo(),
            new FakeRuntimeStateSource(RuntimeState() with { ExecutionEnabled = true }));
        var evaluator = new DeterministicPolicyEvaluator(provider);
        var promotion = SignPromotion(promotionSigner, Promotion(proposal));
        var promotedEnvelope = SignEnvelope(
            evaluationSigner,
            Envelope(proposal) with
            {
                RequestedMode = PolicyEvaluationEnvelope.Execute,
                ExecutionPromotion = promotion
            });
        var approved = await Evaluate(evaluator, proposal, promotedEnvelope);
        Assert.Equal(ApprovalDecisionV1.Approved, approved.Decision);
        Assert.False(approved.ShadowOnly);

        var bareExecute = SignEnvelope(
            evaluationSigner,
            Envelope(proposal) with { RequestedMode = PolicyEvaluationEnvelope.Execute });
        var denied = await Evaluate(evaluator, proposal, bareExecute);
        Assert.Equal(ApprovalDecisionV1.Denied, denied.Decision);
        Assert.True(denied.ShadowOnly);

        var tamperedPromotion = promotion with { ReleaseApprovalId = Guid.NewGuid() };
        var tamperedEnvelope = SignEnvelope(
            evaluationSigner,
            Envelope(proposal) with
            {
                RequestedMode = PolicyEvaluationEnvelope.Execute,
                ExecutionPromotion = tamperedPromotion
            });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await Evaluate(evaluator, proposal, tamperedEnvelope);
        });
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task EcdsaBoundaryRejectsP384DerAndOversizedSignatures()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() =>
            new EcdsaPolicyTrustProvider(
                p384.ExportSubjectPublicKeyInfo(),
                p384.ExportSubjectPublicKeyInfo(),
                new FakeRuntimeStateSource(RuntimeState())));
        Assert.Throws<ArgumentException>(() =>
            new EcdsaPolicyRevocationAuthorizer(p384.ExportSubjectPublicKeyInfo()));

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() =>
            new EcdsaPolicyTrustProvider(
                signer.ExportSubjectPublicKeyInfo(),
                signer.ExportSubjectPublicKeyInfo(),
                new FakeRuntimeStateSource(RuntimeState())));
        using var promotionSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var serviceRevocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityTopology = PolicyApprovalSubmissionAuthorityTopology.Create(
            signer.ExportSubjectPublicKeyInfo(),
            promotionSigner.ExportSubjectPublicKeyInfo(),
            serviceRevocationSigner.ExportSubjectPublicKeyInfo(),
            fenceSigner.ExportSubjectPublicKeyInfo(),
            executorSigner.ExportSubjectPublicKeyInfo(),
            reconciliationSigner.ExportSubjectPublicKeyInfo(),
            recoverySigner.ExportSubjectPublicKeyInfo(),
            stateSigner.ExportSubjectPublicKeyInfo());
        using var provider = new EcdsaPolicyTrustProvider(
            signer.ExportSubjectPublicKeyInfo(),
            promotionSigner.ExportSubjectPublicKeyInfo(),
            new FakeRuntimeStateSource(RuntimeState()));
        Assert.Throws<UnauthorizedAccessException>(() =>
            PostgresPolicyApprovalService.CreateProduction(
                new PostgresPolicyApprovalOptions(
                    "Host=localhost;Database=dps_test;Username=dps_policy_runtime;Pooling=false",
                    "dps_policy_test",
                    "dps_policy_runtime"),
                authorityTopology,
                signer.ExportSubjectPublicKeyInfo(),
                promotionSigner.ExportSubjectPublicKeyInfo(),
                signer.ExportSubjectPublicKeyInfo()));
        Assert.Throws<ArgumentException>(() => PolicyApprovalSubmissionAuthorityTopology.Create(
            signer.ExportSubjectPublicKeyInfo(),
            promotionSigner.ExportSubjectPublicKeyInfo(),
            serviceRevocationSigner.ExportSubjectPublicKeyInfo(),
            signer.ExportSubjectPublicKeyInfo(),
            executorSigner.ExportSubjectPublicKeyInfo(),
            reconciliationSigner.ExportSubjectPublicKeyInfo(),
            recoverySigner.ExportSubjectPublicKeyInfo(),
            stateSigner.ExportSubjectPublicKeyInfo()));
        var evaluator = new DeterministicPolicyEvaluator(provider);
        var unsigned = Envelope() with { SignatureBase64 = string.Empty };
        var canonical = EcdsaPolicyTrustProvider.CanonicalBytes(unsigned);
        byte[]? der = null;
        try
        {
            der = signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await Evaluate(
                    evaluator,
                    Proposal(),
                    unsigned with { SignatureBase64 = Convert.ToBase64String(der) });
            });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await Evaluate(
                    evaluator,
                    Proposal(),
                    unsigned with { SignatureBase64 = new string('A', 4096) });
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (der is not null) CryptographicOperations.ZeroMemory(der);
        }
    }

    private static DeterministicPolicyEvaluator Evaluator(VerifiedPolicyEvaluationContext context) => new(new FakeTrustProvider(context, authorized: true));
    private static ValueTask<ApprovalDecisionV1> Evaluate(DeterministicPolicyEvaluator evaluator, ActionProposalV1 proposal, PolicyEvaluationEnvelope envelope)
        => evaluator.EvaluateAsync(proposal, envelope, TestContext.Current.CancellationToken);
    private static PolicyEvaluationEnvelope Envelope(ActionProposalV1? proposal = null)
    {
        proposal ??= Proposal();
        return new PolicyEvaluationEnvelope(
            "control-plane-host",
            "policy:evaluate",
            proposal.ProposalId,
            PolicyAuthorizationBinding.ComputeProposalSha256(proposal),
            new string('d', 64),
            Now.AddMinutes(1),
            "fake-signature");
    }
    private static ActionProposalV1 Proposal(string action = "observe", bool sideEffect = false) => new(
        ActionProposalV1.CurrentSchemaVersion, ActionProposalV1.CurrentContractId, ActionProposalV1.CurrentProducerModule,
        ActionProposalIdentity.Create(Soul, Device, Account, Idempotency), Soul, Device, Account, Trace, Idempotency, Now,
        "internal", action, sideEffect, true,
        action == "fixture.tap" ? new Dictionary<string, string> { ["selector_ref"] = "fixture.button" } : new Dictionary<string, string>(),
        ["evidence:synthetic"]);
    private static VerifiedPolicyEvaluationContext Context() => new(
        Soul, Device, Account, "1.0.0", new HashSet<string>(DeterministicPolicyEvaluator.KnownPolicies, StringComparer.Ordinal),
        false, 10, true, "approval_platform_test", Now, new string('a', 64));
    private static PolicyRuntimeState RuntimeState() => new(Soul, Device, Account, "1.0.0", new HashSet<string>(DeterministicPolicyEvaluator.KnownPolicies, StringComparer.Ordinal), false, 10, true, "approval_platform_test", Now, false, new string('d', 64), 1, new string('e', 64), Now.AddMinutes(2));

    private static ActionExecutionPromotionV1 Promotion(ActionProposalV1 proposal) => new(
        ActionExecutionPromotionV1.CurrentSchemaVersion,
        ActionExecutionPromotionV1.CurrentContractId,
        ActionExecutionPromotionV1.CurrentProducerModule,
        ActionExecutionPromotionV1.CurrentAuthScope,
        Guid.Parse("55000000-0000-0000-0000-000000000005"),
        proposal.ProposalId,
        Guid.Parse("66000000-0000-0000-0000-000000000006"),
        proposal.SoulId,
        proposal.DeviceBindingId,
        proposal.PlatformAccountId,
        proposal.TraceId,
        proposal.IdempotencyKey,
        PolicyAuthorizationBinding.ComputeProposalSha256(proposal),
        new string('d', 64),
        1,
        Now,
        Now.AddMinutes(1),
        "internal",
        Convert.ToBase64String(new byte[64]));

    private static ActionExecutionPromotionV1 SignPromotion(
        ECDsa signer,
        ActionExecutionPromotionV1 promotion)
    {
        var canonical = ActionExecutionPromotionV1Canonical.CanonicalBytes(promotion);
        byte[]? signature = null;
        try
        {
            signature = signer.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return promotion with { SignatureBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static PolicyEvaluationEnvelope SignEnvelope(
        ECDsa signer,
        PolicyEvaluationEnvelope envelope)
    {
        var unsigned = envelope with { SignatureBase64 = string.Empty };
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

    private sealed class FakeTrustProvider(VerifiedPolicyEvaluationContext context, bool authorized) : IPolicyTrustProvider
    {
        public ValueTask<VerifiedPolicyEvaluationContext> ResolveVerifiedContextAsync(ActionProposalV1 proposal, PolicyEvaluationEnvelope envelope, CancellationToken cancellationToken)
        {
            if (!authorized) throw new UnauthorizedAccessException("FAKE: envelope signature rejected.");
            return ValueTask.FromResult(context);
        }
    }

    private sealed class FakeRuntimeStateSource(PolicyRuntimeState state) : IPolicyRuntimeStateSource
    {
        public ValueTask<PolicyRuntimeState> ReadVerifiedStateAsync(ActionProposalV1 proposal, CancellationToken cancellationToken) => ValueTask.FromResult(state);
    }
}
