using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dps.ControlPlaneHost.Contracts;
using Dps.PolicyApproval.Contracts;
using Xunit;

namespace Dps.ControlPlaneHost.Tests;

public sealed class ControlPlaneSubmissionLifecycleTests
{
    private const string Soul =
        "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Binding = "db_11111111111111111111111111111111";
    private const string Account = "pa_22222222222222222222222222222222";
    private const string Trace = "trace_33333333333333333333333333333333";
    private const string Idempotency =
        "idem_4444444444444444444444444444444444444444444444444444444444444444";
    private const string Bom =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string NativeBinding =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Intent =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string Evidence =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string ZeroSha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly string ZeroSignature =
        Convert.ToBase64String(new byte[64]);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid SubmissionAttemptId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ApprovalId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid ProposalId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid CommandId =
        Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid LeaseId =
        Guid.Parse("55555555-5555-4555-8555-555555555555");

    [Fact, Trait("Category", "Unit")]
    public void AuthorityTopologyRejectsMissingSharedOrCollapsedCapabilities()
    {
        using var policyStateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var sharedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stateFingerprint = Fingerprint(policyStateKey);
        var collapsed = new CollapsedSigner(sharedKey);

        Assert.Throws<InvalidOperationException>(() =>
            new ControlPlaneSubmissionLifecycleProducer(
                collapsed,
                collapsed,
                stateFingerprint,
                FactsSource()));
        Assert.Throws<InvalidOperationException>(() =>
            new ControlPlaneSubmissionLifecycleProducer(
                new TestReconciliationSigner(sharedKey),
                new TestRecoverySigner(sharedKey),
                stateFingerprint,
                FactsSource()));
        using var separateRecoveryKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidOperationException>(() =>
            new ControlPlaneSubmissionLifecycleProducer(
                new TestReconciliationSigner(policyStateKey),
                new TestRecoverySigner(separateRecoveryKey),
                stateFingerprint,
                FactsSource()));
        Assert.Throws<ArgumentNullException>(() =>
            new ControlPlaneSubmissionLifecycleProducer(
                null!,
                new TestRecoverySigner(sharedKey),
                stateFingerprint,
                FactsSource()));
    }

    [Fact, Trait("Category", "Contract")]
    public void StateConsumerRequiresOwnerCodecCommitmentSignatureAndExactScope()
    {
        using var policyStateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var consumer = new ControlPlaneSubmissionStateConsumer(
            policyStateKey.ExportSubjectPublicKeyInfo());
        var state = SignedState(
            policyStateKey,
            ApprovalSubmissionStateV1.SubmissionPending,
            predecessor: null,
            evidence: Intent);
        var wire = ApprovalSubmissionStateV1Codec.Serialize(state);
        var expectation = Expectation(state);

        var verified = consumer.Consume(wire, expectation);
        Assert.Equal(state, verified.Value);
        Assert.Equal(Fingerprint(policyStateKey), verified.AuthorityFingerprintSha256);

        Assert.Throws<UnauthorizedAccessException>(() =>
            consumer.Consume(wire, expectation with { SoulId = OtherSoul() }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            consumer.Consume(wire, expectation with { ReleaseBomSha256 = Evidence }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            consumer.Consume(wire, expectation with { IdempotencyKey = OtherIdempotency() }));

        var forged = state with { SignatureBase64 = ZeroSignature };
        Assert.Throws<UnauthorizedAccessException>(() =>
            consumer.Consume(
                ApprovalSubmissionStateV1Codec.Serialize(forged),
                expectation));

        var json = Encoding.UTF8.GetString(wire);
        var duplicate = Encoding.UTF8.GetBytes(json.Replace(
            "\"schema_version\":\"1.0.0\",",
            "\"schema_version\":\"1.0.0\",\"schema_version\":\"1.0.0\",",
            StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() =>
            consumer.Consume(duplicate, expectation));
    }

    [Fact, Trait("Category", "Contract")]
    public async Task ReconciliationProducerUsesPolicyCodecAndIndependentP256Authority()
    {
        using var fixture = new Fixture();
        var pending = fixture.ConsumePending();
        var request = ReconciliationRequest();

        var envelope = await fixture.Producer.CreateReconciliationAsync(
            pending,
            request,
            TestContext.Current.CancellationToken);
        var decoded = ApprovalSubmissionReconciliationV1Codec.Deserialize(
            envelope.CopyWireBytes());

        Assert.Equal(envelope.Value, decoded);
        Assert.Equal(Soul, decoded.SoulId);
        Assert.Equal(Binding, decoded.DeviceBindingId);
        Assert.Equal(Account, decoded.PlatformAccountId);
        Assert.Equal(Trace, decoded.TraceId);
        Assert.Equal(Idempotency, decoded.IdempotencyKey);
        Assert.Equal(Bom, pending.Value.ReleaseBomSha256);
        Assert.Equal(Intent, decoded.SubmissionIntentSha256);
        Assert.Equal(pending.Value.StateSha256, decoded.PendingStateSha256);
        Assert.Equal(
            ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(decoded),
            envelope.CommitmentSha256);
        Assert.True(Verify(
            fixture.ReconciliationKey,
            ApprovalSubmissionLifecycleBinding.CanonicalReconciliationBytes(decoded),
            decoded.SignatureBase64));
        Assert.False(Verify(
            fixture.RecoveryKey,
            ApprovalSubmissionLifecycleBinding.CanonicalReconciliationBytes(decoded),
            decoded.SignatureBase64));

        var unknown = fixture.ConsumeUnknown(pending.Value);
        var fromUnknown = await fixture.Producer.CreateReconciliationAsync(
            unknown,
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            pending.Value.StateSha256,
            fromUnknown.Value.PendingStateSha256);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Producer.CreateReconciliationAsync(
                pending,
                request with { Finding = "MAYBE_NOT_SUBMITTED" },
                TestContext.Current.CancellationToken));
    }

    [Fact, Trait("Category", "Contract")]
    public async Task RecoveryProducerRequiresExactReconciledChainAndFreshBoundedAttempt()
    {
        using var fixture = new Fixture();
        var pending = fixture.ConsumePending();
        var reconciliation = await fixture.Producer.CreateReconciliationAsync(
            pending,
            ReconciliationRequest(),
            TestContext.Current.CancellationToken);
        var reconciled = fixture.ConsumeReconciled(pending.Value, reconciliation);
        var request = RecoveryRequest();

        var envelope = await fixture.Producer.CreateRecoveryAsync(
            reconciled,
            reconciliation,
            request,
            TestContext.Current.CancellationToken);
        var decoded = ApprovalSubmissionRecoveryV1Codec.Deserialize(
            envelope.CopyWireBytes());

        Assert.Equal(envelope.Value, decoded);
        Assert.Equal(reconciliation.Value.ReconciliationId, decoded.ReconciliationId);
        Assert.Equal(reconciliation.CommitmentSha256, decoded.ReconciliationSha256);
        Assert.Equal(Bom, decoded.NextReleaseBomSha256);
        Assert.Equal(8, decoded.NextReleaseBomGeneration);
        Assert.Equal(2, decoded.NextAttempt);
        Assert.Equal(
            ApprovalSubmissionLifecycleBinding.ComputeRecoverySha256(decoded),
            envelope.CommitmentSha256);
        Assert.True(Verify(
            fixture.RecoveryKey,
            ApprovalSubmissionLifecycleBinding.CanonicalRecoveryBytes(decoded),
            decoded.SignatureBase64));
        Assert.False(Verify(
            fixture.ReconciliationKey,
            ApprovalSubmissionLifecycleBinding.CanonicalRecoveryBytes(decoded),
            decoded.SignatureBase64));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Producer.CreateRecoveryAsync(
            reconciled,
            reconciliation,
            request with { NextSubmissionAttemptId = SubmissionAttemptId },
            TestContext.Current.CancellationToken));
        var otherReconciliation = await fixture.Producer.CreateReconciliationAsync(
            fixture.ConsumePending(OtherSoul()),
            ReconciliationRequest(),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Producer.CreateRecoveryAsync(
            reconciled,
            otherReconciliation,
            request,
            TestContext.Current.CancellationToken));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task RecoveryFailsClosedWhenNextBomFactsDivergeFromTheLiveActiveBinding()
    {
        using var fixture = new Fixture();
        var pending = fixture.ConsumePending();
        var reconciliation = await fixture.Producer.CreateReconciliationAsync(
            pending,
            ReconciliationRequest(),
            TestContext.Current.CancellationToken);
        var reconciled = fixture.ConsumeReconciled(pending.Value, reconciliation);
        var request = RecoveryRequest();

        // The caller-declared NextReleaseBom* facts must equal the live
        // active binding read at issuance; every divergence is a visible
        // fail-closed refusal, never a silent overwrite.
        using var divergentGeneration = new ControlPlaneSubmissionLifecycleProducer(
            fixture.ReconciliationSigner,
            fixture.RecoverySigner,
            fixture.Consumer.AuthorityFingerprintSha256,
            FactsSource(generation: 9));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            divergentGeneration.CreateRecoveryAsync(
                reconciled,
                reconciliation,
                request,
                TestContext.Current.CancellationToken));

        using var divergentBom = new ControlPlaneSubmissionLifecycleProducer(
            fixture.ReconciliationSigner,
            fixture.RecoverySigner,
            fixture.Consumer.AuthorityFingerprintSha256,
            FactsSource(releaseBomSha256: new string('e', 64)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            divergentBom.CreateRecoveryAsync(
                reconciled,
                reconciliation,
                request,
                TestContext.Current.CancellationToken));

        using var noActiveBinding = new ControlPlaneSubmissionLifecycleProducer(
            fixture.ReconciliationSigner,
            fixture.RecoverySigner,
            fixture.Consumer.AuthorityFingerprintSha256,
            FactsSource(absent: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            noActiveBinding.CreateRecoveryAsync(
                reconciled,
                reconciliation,
                request,
                TestContext.Current.CancellationToken));

        Assert.Throws<ArgumentNullException>(() =>
            new ControlPlaneSubmissionLifecycleProducer(
                fixture.ReconciliationSigner,
                fixture.RecoverySigner,
                fixture.Consumer.AuthorityFingerprintSha256,
                null!));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task RecoveryRefusesTheSignedEnvelopeWhenTheBindingChangesDuringSigning()
    {
        using var fixture = new Fixture();
        var pending = fixture.ConsumePending();
        var reconciliation = await fixture.Producer.CreateReconciliationAsync(
            pending,
            ReconciliationRequest(),
            TestContext.Current.CancellationToken);
        var reconciled = fixture.ConsumeReconciled(pending.Value, reconciliation);
        var request = RecoveryRequest();

        // TOCTOU narrowing: the first facts read passes, then the binding
        // changes while the producer awaits the human signer. The post-signing
        // re-verification must refuse the already-signed envelope fail-closed.
        var reader = new MutableLifecycleBindingReader { Binding = ActiveBinding() };
        var signer = new TestRecoverySigner(fixture.RecoveryKey)
        {
            // Case 1: a new BOM is activated during signing.
            WhileSigning = () => reader.Binding =
                ActiveBinding(generation: 9, releaseBomSha256: new string('e', 64))
        };
        using var producer = new ControlPlaneSubmissionLifecycleProducer(
            fixture.ReconciliationSigner,
            signer,
            fixture.Consumer.AuthorityFingerprintSha256,
            new PolicyBoundReleaseBomFactsSource(reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            producer.CreateRecoveryAsync(
                reconciled,
                reconciliation,
                request,
                TestContext.Current.CancellationToken));
        Assert.Equal(1, signer.CallCount);

        // Case 2: the binding is revoked (no active binding) during signing.
        reader.Binding = ActiveBinding();
        signer.WhileSigning = () => reader.Binding = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            producer.CreateRecoveryAsync(
                reconciled,
                reconciliation,
                request,
                TestContext.Current.CancellationToken));

        // Regression: with a stable binding the happy path still signs.
        reader.Binding = ActiveBinding();
        signer.WhileSigning = null;
        var envelope = await producer.CreateRecoveryAsync(
            reconciled,
            reconciliation,
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(Bom, envelope.Value.NextReleaseBomSha256);
        Assert.Equal(8, envelope.Value.NextReleaseBomGeneration);
    }

    [Fact, Trait("Category", "Contract")]
    public async Task CoordinatorExecutesSeparatedProducerPortAndConsumerRoundTrip()
    {
        using var fixture = new Fixture();
        var pendingState = fixture.PendingState();
        var pendingWire = ApprovalSubmissionStateV1Codec.Serialize(pendingState);
        var reconciliationPort = new ReconciliationPort(fixture.PolicyStateKey);
        var recoveryPort = new RecoveryPort(
            fixture.PolicyStateKey,
            reconciliationPort);
        var coordinator = new ControlPlaneSubmissionLifecycleCoordinator(
            fixture.Consumer,
            fixture.Producer,
            reconciliationPort,
            recoveryPort);

        var reconciliation = await coordinator.ReconcileAsync(
            pendingWire,
            Expectation(pendingState),
            ReconciliationRequest(),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ApprovalSubmissionStateV1.ReconciledNotSubmitted,
            reconciliation.State.Value.State);
        Assert.Equal(1, reconciliationPort.CallCount);

        var recovery = await coordinator.RecoverAsync(
            reconciliation,
            RecoveryRequest(),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ApprovalSubmissionStateV1.RecoveryAuthorized,
            recovery.State.Value.State);
        Assert.Equal(1, recoveryPort.CallCount);
        var collapsedPort = new CollapsedPort();
        Assert.Throws<InvalidOperationException>(() =>
            new ControlPlaneSubmissionLifecycleCoordinator(
                fixture.Consumer,
                fixture.Producer,
                collapsedPort,
                collapsedPort));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task CoordinatorRequiresExactPortIdentityAndEnforcesTimeout()
    {
        using var fixture = new Fixture();
        var validRecovery = new RecoveryPort(
            fixture.PolicyStateKey,
            new ReconciliationPort(fixture.PolicyStateKey));

        Assert.Throws<UnauthorizedAccessException>(() =>
            new ControlPlaneSubmissionLifecycleCoordinator(
                fixture.Consumer,
                fixture.Producer,
                new NeverCompletingReconciliationPort(
                    authScope: ApprovalSubmissionRecoveryV1.CurrentAuthScope),
                validRecovery));
        Assert.Throws<InvalidOperationException>(() =>
            new ControlPlaneSubmissionLifecycleCoordinator(
                fixture.Consumer,
                fixture.Producer,
                new NeverCompletingReconciliationPort(
                    credentialFingerprintSha256:
                        RecoveryPort.CredentialFingerprintSha256),
                validRecovery));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ControlPlaneSubmissionLifecycleCoordinator(
                fixture.Consumer,
                fixture.Producer,
                new NeverCompletingReconciliationPort(),
                validRecovery,
                TimeSpan.FromSeconds(6)));

        var neverPort = new NeverCompletingReconciliationPort();
        var coordinator = new ControlPlaneSubmissionLifecycleCoordinator(
            fixture.Consumer,
            fixture.Producer,
            neverPort,
            validRecovery,
            TimeSpan.FromMilliseconds(25));
        var pending = fixture.PendingState();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ReconcileAsync(
                ApprovalSubmissionStateV1Codec.Serialize(pending),
                Expectation(pending),
                ReconciliationRequest(),
                cancelled.Token));
        Assert.Equal(0, fixture.ReconciliationSigner.CallCount);

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.ReconcileAsync(
            ApprovalSubmissionStateV1Codec.Serialize(pending),
            Expectation(pending),
            ReconciliationRequest(),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.ReconciliationSigner.CallCount);

        using var neverAuthorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var delayedAuthority = new DelayedUncooperativeReconciliationSigner(
            neverAuthorityKey);
        using var authorityTimeoutProducer =
            new ControlPlaneSubmissionLifecycleProducer(
                delayedAuthority,
                fixture.RecoverySigner,
                fixture.Consumer.AuthorityFingerprintSha256,
                FactsSource(),
                TimeSpan.FromMilliseconds(25));
        var untouchedPort = new ReconciliationPort(fixture.PolicyStateKey);
        var authorityTimeoutCoordinator =
            new ControlPlaneSubmissionLifecycleCoordinator(
                fixture.Consumer,
                authorityTimeoutProducer,
                untouchedPort,
                new RecoveryPort(fixture.PolicyStateKey, untouchedPort));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            authorityTimeoutCoordinator.ReconcileAsync(
                ApprovalSubmissionStateV1Codec.Serialize(pending),
                Expectation(pending),
                ReconciliationRequest(),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, delayedAuthority.CallCount);
        Assert.True(delayedAuthority.HasPayload);
        Assert.False(delayedAuthority.PayloadIsAllZero);
        Assert.Equal(0, untouchedPort.CallCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authorityTimeoutCoordinator.ReconcileAsync(
                ApprovalSubmissionStateV1Codec.Serialize(pending),
                Expectation(pending),
                ReconciliationRequest(),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, delayedAuthority.CallCount);
        var lateSignature = delayedAuthority.CompleteWithLateSignature();
        Assert.True(SpinWait.SpinUntil(
            () => delayedAuthority.PayloadIsAllZero
                && Array.TrueForAll(lateSignature, value => value == 0),
            TimeSpan.FromSeconds(1)));

        using var cancellationRaceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cancellationRaceSource = new CancellationTokenSource();
        var cancellationRaceSigner = new CancelAfterSigningReconciliationSigner(
            cancellationRaceKey,
            cancellationRaceSource);
        using var cancellationRaceProducer =
            new ControlPlaneSubmissionLifecycleProducer(
                cancellationRaceSigner,
                fixture.RecoverySigner,
                fixture.Consumer.AuthorityFingerprintSha256,
                FactsSource());
        var cancellationRacePort = new ReconciliationPort(fixture.PolicyStateKey);
        var cancellationRaceCoordinator =
            new ControlPlaneSubmissionLifecycleCoordinator(
                fixture.Consumer,
                cancellationRaceProducer,
                cancellationRacePort,
                new RecoveryPort(fixture.PolicyStateKey, cancellationRacePort));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellationRaceCoordinator.ReconcileAsync(
                ApprovalSubmissionStateV1Codec.Serialize(pending),
                Expectation(pending),
                ReconciliationRequest(),
                cancellationRaceSource.Token));
        Assert.Equal(1, cancellationRaceSigner.CallCount);
        Assert.Equal(0, cancellationRacePort.CallCount);
    }

    [Theory, Trait("Category", "Contract")]
    [InlineData("declared-key-mismatch")]
    [InlineData("malformed-p1363")]
    [InlineData("non-p1363-der")]
    [InlineData("wrong-length")]
    [InlineData("late-signature")]
    [InlineData("key-replacement")]
    public async Task OutboundLifecycleSignerAttacksFailClosedBeforePolicyPorts(
        string attack)
    {
        await AssertReconciliationSignerAttackFailsClosed(attack);
        await AssertRecoverySignerAttackFailsClosed(attack);
    }

    private static async Task AssertReconciliationSignerAttackFailsClosed(
        string attack)
    {
        using var policyStateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var declaredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rogueKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoveryKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var consumer = new ControlPlaneSubmissionStateConsumer(
            policyStateKey.ExportSubjectPublicKeyInfo());
        var responder = new AdversarialSignatureResponder(
            declaredKey,
            attack == "declared-key-mismatch" ? rogueKey : declaredKey,
            attack);
        var signer = new AdversarialReconciliationSigner(responder);
        using var producer = new ControlPlaneSubmissionLifecycleProducer(
            signer,
            new TestRecoverySigner(recoveryKey),
            consumer.AuthorityFingerprintSha256,
            FactsSource(),
            AttackTimeout(attack));
        if (attack == "key-replacement")
            responder.ReplaceSigningKey(rogueKey);

        var reconciliationPort = new ReconciliationPort(policyStateKey);
        var recoveryPort = new RecoveryPort(policyStateKey, reconciliationPort);
        var coordinator = new ControlPlaneSubmissionLifecycleCoordinator(
            consumer,
            producer,
            reconciliationPort,
            recoveryPort);
        var pending = SignedState(
            policyStateKey,
            ApprovalSubmissionStateV1.SubmissionPending,
            predecessor: null,
            evidence: Intent);

        await AssertSignerAttackFailure(
            attack,
            () => coordinator.ReconcileAsync(
                ApprovalSubmissionStateV1Codec.Serialize(pending),
                Expectation(pending),
                ReconciliationRequest(),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, responder.CallCount);
        Assert.Equal(0, reconciliationPort.CallCount);
        Assert.Equal(0, recoveryPort.CallCount);
        CompleteAndVerifyLateCleanup(attack, responder);
    }

    private static async Task AssertRecoverySignerAttackFailsClosed(string attack)
    {
        using var policyStateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var declaredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rogueKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var consumer = new ControlPlaneSubmissionStateConsumer(
            policyStateKey.ExportSubjectPublicKeyInfo());
        var responder = new AdversarialSignatureResponder(
            declaredKey,
            attack == "declared-key-mismatch" ? rogueKey : declaredKey,
            attack);
        var signer = new AdversarialRecoverySigner(responder);
        using var producer = new ControlPlaneSubmissionLifecycleProducer(
            new TestReconciliationSigner(reconciliationKey),
            signer,
            consumer.AuthorityFingerprintSha256,
            FactsSource(),
            AttackTimeout(attack));
        if (attack == "key-replacement")
            responder.ReplaceSigningKey(rogueKey);

        var reconciliationPort = new ReconciliationPort(policyStateKey);
        var recoveryPort = new RecoveryPort(policyStateKey, reconciliationPort);
        var coordinator = new ControlPlaneSubmissionLifecycleCoordinator(
            consumer,
            producer,
            reconciliationPort,
            recoveryPort);
        var pending = SignedState(
            policyStateKey,
            ApprovalSubmissionStateV1.SubmissionPending,
            predecessor: null,
            evidence: Intent);
        var reconciliation = await coordinator.ReconcileAsync(
            ApprovalSubmissionStateV1Codec.Serialize(pending),
            Expectation(pending),
            ReconciliationRequest(),
            TestContext.Current.CancellationToken);

        await AssertSignerAttackFailure(
            attack,
            () => coordinator.RecoverAsync(
                reconciliation,
                RecoveryRequest(),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, responder.CallCount);
        Assert.Equal(1, reconciliationPort.CallCount);
        Assert.Equal(0, recoveryPort.CallCount);
        CompleteAndVerifyLateCleanup(attack, responder);
    }

    private static TimeSpan? AttackTimeout(string attack)
        => attack == "late-signature" ? TimeSpan.FromMilliseconds(25) : null;

    private static async Task AssertSignerAttackFailure(
        string attack,
        Func<Task> action)
    {
        var exception = await Record.ExceptionAsync(action);
        Assert.NotNull(exception);
        if (attack == "late-signature")
            Assert.IsType<TimeoutException>(exception);
        else if (attack is "non-p1363-der" or "wrong-length")
            Assert.IsType<InvalidOperationException>(exception);
        else
            Assert.IsType<UnauthorizedAccessException>(exception);
    }

    private static void CompleteAndVerifyLateCleanup(
        string attack,
        AdversarialSignatureResponder responder)
    {
        if (attack != "late-signature")
            return;

        var lateSignature = responder.CompleteLateSignature();
        Assert.True(SpinWait.SpinUntil(
            () => responder.LatePayloadIsAllZero
                && Array.TrueForAll(lateSignature, value => value == 0),
            TimeSpan.FromSeconds(1)));
    }

    private static ControlPlaneReconciliationRequest ReconciliationRequest()
        => new(
            Guid.Parse("66666666-6666-4666-8666-666666666666"),
            ApprovalSubmissionReconciliationV1.ConfirmedNotSubmitted,
            Evidence,
            Now,
            Now.AddMinutes(4));

    private static ControlPlaneRecoveryRequest RecoveryRequest()
        => new(
            Guid.Parse("77777777-7777-4777-8777-777777777777"),
            Guid.Parse("88888888-8888-4888-8888-888888888888"),
            Guid.Parse("99999999-9999-4999-8999-999999999999"),
            2,
            Bom,
            8,
            new string('1', 64),
            new string('2', 64),
            "human_" + new string('3', 64),
            Now.AddMinutes(1),
            Now.AddMinutes(5));

    /// <summary>
    /// Fixture facts source matching the recovery request's next-BOM facts
    /// (Bom / generation 8) unless a divergence is requested. The producer
    /// reads these facts live at recovery issuance and fail-closes on any
    /// mismatch with the caller-declared NextReleaseBom* values.
    /// </summary>
    private static PolicyBoundReleaseBomFactsSource FactsSource(
        long generation = 8,
        string? releaseBomSha256 = null,
        bool absent = false)
        => new(new FixedLifecycleBindingReader(absent
            ? null
            : ActiveBinding(generation, releaseBomSha256)));

    private static ActiveReleaseBindingV1 ActiveBinding(
        long generation = 8,
        string? releaseBomSha256 = null)
        => new(
            "1.0.0",
            "active.release.binding/v1",
            "control-plane-host",
            Binding,
            releaseBomSha256 ?? Bom,
            generation,
            7,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",
            "active",
            "deployed-release-controller",
            "deployed-controller-key-v1",
            new string('d', 64),
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            "receipt_99999999999999999999999999999999");

    private sealed class FixedLifecycleBindingReader : IActiveReleaseBindingReader
    {
        private readonly ActiveReleaseBindingV1? _binding;

        public FixedLifecycleBindingReader(ActiveReleaseBindingV1? binding)
        {
            _binding = binding;
        }

        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding)
        {
            binding = _binding;
            return binding is not null;
        }
    }

    private sealed class MutableLifecycleBindingReader : IActiveReleaseBindingReader
    {
        internal ActiveReleaseBindingV1? Binding { get; set; }

        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding)
        {
            binding = Binding;
            return binding is not null;
        }
    }

    private static ApprovalSubmissionStateExpectation Expectation(
        ApprovalSubmissionStateV1 state)
        => new(
            state.SubmissionAttemptId,
            state.ApprovalId,
            state.ProposalId,
            state.CommandId,
            state.LeaseId,
            state.Attempt,
            state.SoulId,
            state.DeviceBindingId,
            state.PlatformAccountId,
            state.TraceId,
            state.IdempotencyKey,
            state.ReleaseBomSha256,
            state.ReleaseBomGeneration,
            state.NativeRequestBindingSha256,
            state.SubmissionIntentSha256,
            state.State,
            state.PredecessorStateSha256,
            state.EvidenceSha256);

    private static ApprovalSubmissionStateV1 SignedState(
        ECDsa key,
        string state,
        string? predecessor,
        string evidence,
        string? soul = null,
        string? idempotency = null,
        string? releaseBom = null,
        long releaseBomGeneration = 7,
        string? nativeBinding = null,
        string? intent = null,
        Guid? stateEventId = null)
    {
        var unsigned = new ApprovalSubmissionStateV1(
            ApprovalSubmissionStateV1.CurrentSchemaVersion,
            ApprovalSubmissionStateV1.CurrentContractId,
            ApprovalSubmissionStateV1.CurrentProducerModule,
            stateEventId ?? Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            SubmissionAttemptId,
            ApprovalId,
            ProposalId,
            CommandId,
            LeaseId,
            1,
            soul ?? Soul,
            Binding,
            Account,
            Trace,
            idempotency ?? Idempotency,
            releaseBom ?? Bom,
            releaseBomGeneration,
            nativeBinding ?? NativeBinding,
            intent ?? Intent,
            state,
            predecessor,
            evidence,
            Now,
            "internal",
            ZeroSha256,
            ZeroSignature);
        var withDigest = unsigned with
        {
            StateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(unsigned)
        };
        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalStateBytes(withDigest);
        try
        {
            var signature = key.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            try
            {
                return withDigest with
                {
                    SignatureBase64 = Convert.ToBase64String(signature)
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static byte[] TransitionWire(
        ECDsa policyStateKey,
        ApprovalSubmissionStateV1 prior,
        string state,
        string evidence,
        Guid stateEventId)
        => ApprovalSubmissionStateV1Codec.Serialize(SignedState(
            policyStateKey,
            state,
            prior.StateSha256,
            evidence,
            prior.SoulId,
            prior.IdempotencyKey,
            prior.ReleaseBomSha256,
            prior.ReleaseBomGeneration,
            prior.NativeRequestBindingSha256,
            prior.SubmissionIntentSha256,
            stateEventId));

    private static bool Verify(ECDsa key, byte[] canonical, string signatureBase64)
    {
        var signature = Convert.FromBase64String(signatureBase64);
        try
        {
            return key.VerifyData(
                canonical,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string Fingerprint(ECDsa key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(spki));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(spki);
        }
    }

    private static string OtherSoul()
        => "soul_ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private static string OtherIdempotency()
        => "idem_ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private sealed class TestReconciliationSigner
        : IControlPlaneReconciliationSigningAuthority
    {
        private readonly ECDsa _key;
        private readonly object _gate = new();

        internal TestReconciliationSigner(ECDsa key) => _key = key;

        internal int CallCount { get; private set; }

        public byte[] ExportSubjectPublicKeyInfo()
            => _key.ExportSubjectPublicKeyInfo();

        public Task<byte[]> SignReconciliationAsync(
            ApprovalSubmissionReconciliationV1 unsignedReconciliation,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(
                ApprovalSubmissionReconciliationV1.CurrentAuthorityRole,
                unsignedReconciliation.AuthorityRole);
            Assert.Equal(
                ApprovalSubmissionReconciliationV1.CurrentAuthScope,
                unsignedReconciliation.AuthScope);
            lock (_gate)
            {
                CallCount++;
                return Task.FromResult(_key.SignData(
                    canonicalPayload.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
        }
    }

    private sealed class TestRecoverySigner
        : IControlPlaneHumanRecoveryApprovalAuthority
    {
        private readonly ECDsa _key;
        private readonly object _gate = new();

        internal TestRecoverySigner(ECDsa key) => _key = key;

        internal int CallCount { get; private set; }

        /// <summary>
        /// Runs while the producer awaits the human recovery signer — the
        /// exact window the post-signing facts re-verification narrows.
        /// Tests use it to change the active binding mid-signature.
        /// </summary>
        internal Action? WhileSigning { get; set; }

        public byte[] ExportSubjectPublicKeyInfo()
            => _key.ExportSubjectPublicKeyInfo();

        public Task<byte[]> AuthorizeAndSignRecoveryAsync(
            ApprovalSubmissionRecoveryV1 unsignedRecovery,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(
                ApprovalSubmissionRecoveryV1.CurrentAuthorityRole,
                unsignedRecovery.AuthorityRole);
            Assert.Equal(
                ApprovalSubmissionRecoveryV1.CurrentAuthScope,
                unsignedRecovery.AuthScope);
            Assert.StartsWith("human_", unsignedRecovery.HumanApprovalId);
            WhileSigning?.Invoke();
            lock (_gate)
            {
                CallCount++;
                return Task.FromResult(_key.SignData(
                    canonicalPayload.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
        }
    }

    private sealed class CollapsedSigner :
        IControlPlaneReconciliationSigningAuthority,
        IControlPlaneHumanRecoveryApprovalAuthority
    {
        private readonly ECDsa _key;

        internal CollapsedSigner(ECDsa key) => _key = key;

        public byte[] ExportSubjectPublicKeyInfo()
            => _key.ExportSubjectPublicKeyInfo();

        public Task<byte[]> SignReconciliationAsync(
            ApprovalSubmissionReconciliationV1 unsignedReconciliation,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
            => Task.FromResult(Sign(canonicalPayload.Span));

        public Task<byte[]> AuthorizeAndSignRecoveryAsync(
            ApprovalSubmissionRecoveryV1 unsignedRecovery,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
            => Task.FromResult(Sign(canonicalPayload.Span));

        private byte[] Sign(ReadOnlySpan<byte> canonicalPayload)
            => _key.SignData(
                canonicalPayload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private sealed class DelayedUncooperativeReconciliationSigner(ECDsa key)
        : IControlPlaneReconciliationSigningAuthority
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<byte[]> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private ReadOnlyMemory<byte> _payload;

        internal int CallCount { get; private set; }

        internal bool HasPayload { get; private set; }

        internal bool PayloadIsAllZero
        {
            get
            {
                lock (_gate)
                {
                    if (!HasPayload)
                        return false;
                    foreach (var value in _payload.Span)
                    {
                        if (value != 0)
                            return false;
                    }
                    return true;
                }
            }
        }

        public byte[] ExportSubjectPublicKeyInfo()
            => key.ExportSubjectPublicKeyInfo();

        public Task<byte[]> SignReconciliationAsync(
            ApprovalSubmissionReconciliationV1 unsignedReconciliation,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                CallCount++;
                _payload = canonicalPayload;
                HasPayload = true;
            }
            return _completion.Task;
        }

        internal byte[] CompleteWithLateSignature()
        {
            byte[] signature;
            lock (_gate)
            {
                if (!HasPayload)
                    throw new InvalidOperationException("No authority payload was captured.");
                signature = key.SignData(
                    _payload.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            _completion.SetResult(signature);
            return signature;
        }
    }

    private sealed class CancelAfterSigningReconciliationSigner(
        ECDsa key,
        CancellationTokenSource cancellationSource)
        : IControlPlaneReconciliationSigningAuthority
    {
        internal int CallCount { get; private set; }

        public byte[] ExportSubjectPublicKeyInfo()
            => key.ExportSubjectPublicKeyInfo();

        public Task<byte[]> SignReconciliationAsync(
            ApprovalSubmissionReconciliationV1 unsignedReconciliation,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var signature = key.SignData(
                canonicalPayload.Span,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            cancellationSource.Cancel();
            return Task.FromResult(signature);
        }
    }

    private sealed class AdversarialSignatureResponder(
        ECDsa declaredKey,
        ECDsa initialSigningKey,
        string attack)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<byte[]> _lateCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private ECDsa _signingKey = initialSigningKey;
        private ReadOnlyMemory<byte> _latePayload;

        internal int CallCount { get; private set; }

        internal bool HasLatePayload { get; private set; }

        internal bool LatePayloadIsAllZero
        {
            get
            {
                lock (_gate)
                {
                    if (!HasLatePayload)
                        return false;
                    foreach (var value in _latePayload.Span)
                    {
                        if (value != 0)
                            return false;
                    }
                    return true;
                }
            }
        }

        internal byte[] ExportSubjectPublicKeyInfo()
            => declaredKey.ExportSubjectPublicKeyInfo();

        internal void ReplaceSigningKey(ECDsa replacementKey)
        {
            ArgumentNullException.ThrowIfNull(replacementKey);
            lock (_gate)
                _signingKey = replacementKey;
        }

        internal Task<byte[]> SignAsync(
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                CallCount++;
                if (attack == "late-signature")
                {
                    _latePayload = canonicalPayload;
                    HasLatePayload = true;
                    return _lateCompletion.Task;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(attack switch
                {
                    "malformed-p1363" => new byte[64],
                    "non-p1363-der" => _signingKey.SignData(
                        canonicalPayload.Span,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence),
                    "wrong-length" => new byte[63],
                    "declared-key-mismatch" or "key-replacement" =>
                        _signingKey.SignData(
                            canonicalPayload.Span,
                            HashAlgorithmName.SHA256,
                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(attack),
                        attack,
                        "Unknown lifecycle signer attack.")
                });
            }
        }

        internal byte[] CompleteLateSignature()
        {
            byte[] signature;
            lock (_gate)
            {
                if (!HasLatePayload)
                {
                    throw new InvalidOperationException(
                        "No late lifecycle authority payload was captured.");
                }
                signature = declaredKey.SignData(
                    _latePayload.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            _lateCompletion.SetResult(signature);
            return signature;
        }
    }

    private sealed class AdversarialReconciliationSigner(
        AdversarialSignatureResponder responder)
        : IControlPlaneReconciliationSigningAuthority
    {
        public byte[] ExportSubjectPublicKeyInfo()
            => responder.ExportSubjectPublicKeyInfo();

        public Task<byte[]> SignReconciliationAsync(
            ApprovalSubmissionReconciliationV1 unsignedReconciliation,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
            => responder.SignAsync(canonicalPayload, cancellationToken);
    }

    private sealed class AdversarialRecoverySigner(
        AdversarialSignatureResponder responder)
        : IControlPlaneHumanRecoveryApprovalAuthority
    {
        public byte[] ExportSubjectPublicKeyInfo()
            => responder.ExportSubjectPublicKeyInfo();

        public Task<byte[]> AuthorizeAndSignRecoveryAsync(
            ApprovalSubmissionRecoveryV1 unsignedRecovery,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
            => responder.SignAsync(canonicalPayload, cancellationToken);
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            PolicyStateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ReconciliationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            RecoveryKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Consumer = new ControlPlaneSubmissionStateConsumer(
                PolicyStateKey.ExportSubjectPublicKeyInfo());
            ReconciliationSigner = new TestReconciliationSigner(ReconciliationKey);
            RecoverySigner = new TestRecoverySigner(RecoveryKey);
            Producer = new ControlPlaneSubmissionLifecycleProducer(
                ReconciliationSigner,
                RecoverySigner,
                Consumer.AuthorityFingerprintSha256,
                FactsSource());
        }

        internal ECDsa PolicyStateKey { get; }

        internal ECDsa ReconciliationKey { get; }

        internal ECDsa RecoveryKey { get; }

        internal TestReconciliationSigner ReconciliationSigner { get; }

        internal TestRecoverySigner RecoverySigner { get; }

        internal ControlPlaneSubmissionStateConsumer Consumer { get; }

        internal ControlPlaneSubmissionLifecycleProducer Producer { get; }

        internal ApprovalSubmissionStateV1 PendingState(string? soul = null)
            => SignedState(
                PolicyStateKey,
                ApprovalSubmissionStateV1.SubmissionPending,
                null,
                Intent,
                soul: soul);

        internal VerifiedApprovalSubmissionState ConsumePending(string? soul = null)
        {
            var state = PendingState(soul);
            return Consumer.Consume(
                ApprovalSubmissionStateV1Codec.Serialize(state),
                Expectation(state));
        }

        internal VerifiedApprovalSubmissionState ConsumeUnknown(
            ApprovalSubmissionStateV1 pending)
        {
            var state = SignedState(
                PolicyStateKey,
                ApprovalSubmissionStateV1.UnknownSubmission,
                pending.StateSha256,
                Evidence,
                stateEventId: Guid.Parse(
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
            return Consumer.Consume(
                ApprovalSubmissionStateV1Codec.Serialize(state),
                Expectation(state));
        }

        internal VerifiedApprovalSubmissionState ConsumeReconciled(
            ApprovalSubmissionStateV1 pending,
            SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionReconciliationV1>
                reconciliation)
        {
            var state = SignedState(
                PolicyStateKey,
                ApprovalSubmissionStateV1.ReconciledNotSubmitted,
                pending.StateSha256,
                reconciliation.CommitmentSha256,
                stateEventId: Guid.Parse(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
            return Consumer.Consume(
                ApprovalSubmissionStateV1Codec.Serialize(state),
                Expectation(state));
        }

        public void Dispose()
        {
            Producer.Dispose();
            Consumer.Dispose();
            PolicyStateKey.Dispose();
            ReconciliationKey.Dispose();
            RecoveryKey.Dispose();
        }
    }

    private sealed class ReconciliationPort(ECDsa policyStateKey)
        : IApprovalSubmissionReconciliationPort
    {
        internal const string CredentialFingerprintSha256 =
            "4444444444444444444444444444444444444444444444444444444444444444";

        internal int CallCount { get; private set; }

        internal ApprovalSubmissionStateV1? LastState { get; private set; }

        public string AuthScope
            => ApprovalSubmissionReconciliationV1.CurrentAuthScope;

        public string CredentialAuthorityFingerprintSha256
            => CredentialFingerprintSha256;

        public Task<ReadOnlyMemory<byte>> ReconcileAsync(
            ReadOnlyMemory<byte> canonicalReconciliationWire,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var reconciliation = ApprovalSubmissionReconciliationV1Codec.Deserialize(
                canonicalReconciliationWire.Span);
            var pending = SignedState(
                policyStateKey,
                ApprovalSubmissionStateV1.SubmissionPending,
                null,
                reconciliation.SubmissionIntentSha256);
            var wire = TransitionWire(
                policyStateKey,
                pending,
                ApprovalSubmissionStateV1.ReconciledNotSubmitted,
                ApprovalSubmissionLifecycleBinding.ComputeReconciliationSha256(
                    reconciliation),
                Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
            LastState = ApprovalSubmissionStateV1Codec.Deserialize(wire);
            return Task.FromResult<ReadOnlyMemory<byte>>(wire);
        }
    }

    private sealed class RecoveryPort(
        ECDsa policyStateKey,
        ReconciliationPort reconciliationPort)
        : IApprovalSubmissionRecoveryPort
    {
        internal const string CredentialFingerprintSha256 =
            "5555555555555555555555555555555555555555555555555555555555555555";

        internal int CallCount { get; private set; }

        public string AuthScope
            => ApprovalSubmissionRecoveryV1.CurrentAuthScope;

        public string CredentialAuthorityFingerprintSha256
            => CredentialFingerprintSha256;

        public Task<ReadOnlyMemory<byte>> RecoverAsync(
            ReadOnlyMemory<byte> canonicalRecoveryWire,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var recovery = ApprovalSubmissionRecoveryV1Codec.Deserialize(
                canonicalRecoveryWire.Span);
            var prior = reconciliationPort.LastState
                ?? throw new InvalidOperationException(
                    "Recovery port did not observe a reconciled state.");
            var wire = TransitionWire(
                policyStateKey,
                prior,
                ApprovalSubmissionStateV1.RecoveryAuthorized,
                ApprovalSubmissionLifecycleBinding.ComputeRecoverySha256(recovery),
                Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"));
            return Task.FromResult<ReadOnlyMemory<byte>>(wire);
        }
    }

    private sealed class CollapsedPort :
        IApprovalSubmissionReconciliationPort,
        IApprovalSubmissionRecoveryPort
    {
        public string AuthScope
            => ApprovalSubmissionReconciliationV1.CurrentAuthScope;

        public string CredentialAuthorityFingerprintSha256
            => ReconciliationPort.CredentialFingerprintSha256;

        public Task<ReadOnlyMemory<byte>> ReconcileAsync(
            ReadOnlyMemory<byte> canonicalReconciliationWire,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReadOnlyMemory<byte>> RecoverAsync(
            ReadOnlyMemory<byte> canonicalRecoveryWire,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NeverCompletingReconciliationPort(
        string? authScope = null,
        string? credentialFingerprintSha256 = null)
        : IApprovalSubmissionReconciliationPort
    {
        public string AuthScope { get; } =
            authScope ?? ApprovalSubmissionReconciliationV1.CurrentAuthScope;

        public string CredentialAuthorityFingerprintSha256 { get; } =
            credentialFingerprintSha256
            ?? ReconciliationPort.CredentialFingerprintSha256;

        public Task<ReadOnlyMemory<byte>> ReconcileAsync(
            ReadOnlyMemory<byte> canonicalReconciliationWire,
            CancellationToken cancellationToken)
            => new TaskCompletionSource<ReadOnlyMemory<byte>>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

}
