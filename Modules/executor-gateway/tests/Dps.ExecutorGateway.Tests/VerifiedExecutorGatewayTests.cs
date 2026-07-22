using Dps.CommandOrchestrator.Contracts;
using Dps.ExecutorGateway.Contracts;
using Dps.PolicyApproval.Contracts;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Dps.ExecutorGateway.Tests;

public sealed class VerifiedExecutorGatewayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Device = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Account = "pa_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Trace = "trace_cccccccccccccccccccccccccccccccc";
    private const string Idempotency = "idem_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string OtherDevice = "db_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string OtherTrace = "trace_ffffffffffffffffffffffffffffffff";
    private const string OtherIdempotency = "idem_1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly string StableBom = new('a', 64);
    private static readonly string StableToken = Convert.ToBase64String(Enumerable.Repeat((byte)0x42, ActiveReleaseBomBindingV1.ExecutionTokenSizeBytes).ToArray());
    private const string StopProofKeyId = "test-native-stop-proof-key";
    private static readonly EcdsaCommandReceiptSigner ReceiptSigner = new(CreateReceiptSigningKey());
    private static readonly ECDsa StopProofSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly EcdsaNativeStopProofVerifier StopProofVerifier = new(
        StopProofSigner.ExportSubjectPublicKeyInfo(),
        StopProofKeyId);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NativeSuccessPlusVerifiedPostconditionIsSuccess()
    {
        var command = Command();
        var native = new FakeNative(NativeStepResultV1.Success);
        var receipt = await Execute(command, Envelope(command), native, new FakePostcondition(true));

        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);
        Assert.True(receipt.NativeResultVerified);
        Assert.True(receipt.PostconditionVerified);
        Assert.NotNull(native.LastRequest);
        Assert.Equal(StableToken, native.LastRequest.ActiveReleaseBomExecutionTokenBase64);
        Assert.Equal(Envelope(command).ActiveReleaseBomTokenSha256, native.LastRequest.ActiveReleaseBomTokenSha256);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NativeSuccessWithoutBusinessPostconditionBlocksFalseSuccess()
    {
        var command = Command();
        var receipt = await Execute(command, Envelope(command), new FakeNative(NativeStepResultV1.Success), new FakePostcondition(false));
        Assert.Equal(CommandReceiptV1.Failed, receipt.Outcome);
        Assert.True(receipt.NativeResultVerified);
        Assert.False(receipt.PostconditionVerified);
        Assert.Equal("POSTCONDITION_FAILED", receipt.ResultCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RoleScopeAndShadowAttacksFailBeforeNativeExecution()
    {
        var command = Command();
        var native = new FakeNative(NativeStepResultV1.Success);
        await Assert.ThrowsAsync<NotSupportedException>(() => Execute(command, Envelope(command) with { CallerModule = "model" }, native, new FakePostcondition(true)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Execute(command, Envelope(command) with { AuthScope = "admin" }, native, new FakePostcondition(true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(command, Envelope(command) with { ShadowMode = true }, native, new FakePostcondition(true)));
        Assert.Equal(0, native.CallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TimeoutAndNativeUnknownBecomeNonRetryableUnknownOutcome()
    {
        var command = Command();
        var timeout = await Execute(command, Envelope(command), new NonCooperativeNative(), new FakePostcondition(true), timeout: TimeSpan.FromMilliseconds(10));
        var unknown = await Execute(command, Envelope(command), new FakeNative(NativeStepResultV1.Unknown), new FakePostcondition(true));
        Assert.Equal(CommandReceiptV1.UnknownOutcome, timeout.Outcome);
        Assert.False(timeout.RetryAllowed);
        Assert.Equal(CommandReceiptV1.UnknownOutcome, unknown.Outcome);
        Assert.False(unknown.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NativeStepResultsAreDeepSnapshottedBeforePostconditionAwait()
    {
        var command = Command();
        var native = new MutableStepNative();
        var postcondition = new BlockingPostcondition();
        var execution = Execute(command, Envelope(command), native, postcondition);

        await postcondition.Entered.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        native.StepResults.Clear();
        native.StepResults.Add(new NativeStepResultV1(command.Steps[0].StepId, command.Steps[0].StepKind, NativeStepResultV1.Unknown, "MUTATED", new string('f', 64)));
        Assert.NotNull(postcondition.NativeResult);
        Assert.Single(postcondition.NativeResult.StepResults);
        Assert.Equal(NativeStepResultV1.Success, postcondition.NativeResult.StepResults[0].Status);
        postcondition.Release();

        var receipt = await execution;
        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);
        Assert.True(receipt.NativeResultVerified);
        Assert.True(receipt.PostconditionVerified);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ThrowingCancellationCallbackCannotOverrideSubmissionTimeoutUnknown()
    {
        var command = Command();
        var receipt = await Execute(command, Envelope(command), new ThrowingCancellationNative(), new FakePostcondition(true), timeout: TimeSpan.FromMilliseconds(10));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NonCooperativePostconditionTimeoutCannotLateSucceed()
    {
        var command = Command();
        var postcondition = new NonCooperativePostcondition();
        var receipt = await Execute(command, Envelope(command), new FakeNative(NativeStepResultV1.Success), postcondition, timeout: TimeSpan.FromMilliseconds(10));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("POSTCONDITION_TIMEOUT", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
        postcondition.CompleteSuccess();
        await Task.Yield();
        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TrustedClockIsOwnedByGatewayAndSlowBomReadCannotReuseStaleTime()
    {
        var command = Command();
        var native = new FakeNative(NativeStepResultV1.Success);
        var clock = new SequenceClock(Now, Now.AddSeconds(31));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(command, Envelope(command), native, new FakePostcondition(true), clock: clock));
        Assert.Equal(0, native.CallCount);
        Assert.DoesNotContain(typeof(VerifiedExecutorGateway).GetMethod(nameof(VerifiedExecutorGateway.ExecuteAsync))!.GetParameters(), parameter => parameter.ParameterType == typeof(DateTimeOffset));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AuthorizationExpiryAfterNativeIsUnknownAndNeverRetried()
    {
        var command = Command();
        var receipt = await Execute(
            command,
            Envelope(command),
            new FakeNative(NativeStepResultV1.Success),
            new FakePostcondition(true),
            clock: new SequenceClock(Now, Now, Now, Now, Now, Now, Now, Now.AddSeconds(31)));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("AUTH_EXPIRED_AFTER_NATIVE", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActiveBomGenerationSwitchAfterNativeIsUnknownAndNeverRetried()
    {
        var command = Command();
        var stable = Binding();
        var switched = Binding(generation: stable.Generation + 1, digest: new string('e', 64));
        var postcondition = new FakePostcondition(true);
        var receipt = await Execute(command, Envelope(command, stable), new FakeNative(NativeStepResultV1.Success), postcondition, new FakeActiveReleaseBomReader(stable, stable, stable, switched));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("ACTIVE_BOM_CHANGED_AFTER_NATIVE", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
        Assert.Equal(0, postcondition.CallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActiveBomGenerationSwitchBeforeReceiptRefusesSuccess()
    {
        var command = Command();
        var stable = Binding();
        var switched = Binding(generation: stable.Generation + 1, digest: new string('e', 64));
        var receipt = await Execute(command, Envelope(command, stable), new FakeNative(NativeStepResultV1.Success), new FakePostcondition(true), new FakeActiveReleaseBomReader(stable, stable, stable, stable, switched));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("ACTIVE_BOM_CHANGED_BEFORE_RECEIPT", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActiveBomTokenSwitchAfterNativeIsUnknownAndNeverRetried()
    {
        var command = Command();
        var stable = Binding();
        var rotated = Binding(token: Convert.ToBase64String(Enumerable.Repeat((byte)0x43, 32).ToArray()));
        var postcondition = new FakePostcondition(true);
        var receipt = await Execute(command, Envelope(command, stable), new FakeNative(NativeStepResultV1.Success), postcondition, new FakeActiveReleaseBomReader(stable, stable, stable, rotated));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("ACTIVE_BOM_CHANGED_AFTER_NATIVE", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
        Assert.Equal(0, postcondition.CallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActiveBomTokenSwitchBeforeReceiptRefusesSuccess()
    {
        var command = Command();
        var stable = Binding();
        var rotated = Binding(token: Convert.ToBase64String(Enumerable.Repeat((byte)0x43, 32).ToArray()));
        var receipt = await Execute(command, Envelope(command, stable), new FakeNative(NativeStepResultV1.Success), new FakePostcondition(true), new FakeActiveReleaseBomReader(stable, stable, stable, stable, rotated));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("ACTIVE_BOM_CHANGED_BEFORE_RECEIPT", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SignedDispatchIsDeepFrozenBeforeFirstAwaitAndNativeUsesSnapshot()
    {
        var arguments = new Dictionary<string, string> { ["selector_ref"] = "fixture.original" };
        var steps = new List<CommandStepV1>
        {
            Command().Steps[0] with { StepKind = "ui.locate", Arguments = arguments, PostconditionKind = "selector-resolved" }
        };
        var command = Command() with { ActionKind = "locate", Steps = steps };
        var authorization = Envelope(command);
        var verifier = new BlockingAuthorizationVerifier(authorization);
        var native = new FakeNative(NativeStepResultV1.Success);
        var execution = new VerifiedExecutorGateway(
            new FixedClock(Now), ReceiptSigner, verifier, new FakeActiveReleaseBomReader(Binding()),
            new FakeApprovalExecutionFenceProvider(), native,
            new ThrowingProcessFailStop(), new FakePostcondition(true), TimeSpan.FromSeconds(1)).ExecuteAsync(
                command, authorization, TestContext.Current.CancellationToken);

        await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        arguments["selector_ref"] = "fixture.mutated-after-signature";
        steps.Clear();
        verifier.Release();

        var receipt = await execution;
        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);
        Assert.NotNull(native.LastRequest);
        Assert.Single(native.LastRequest.Command.Steps);
        Assert.Equal("fixture.original", native.LastRequest.Command.Steps[0].Arguments["selector_ref"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NativeCrossCommandOldAttemptAndCrossScopeReplayFailClosed()
    {
        var command = Command();
        var mutations = new Func<NativeExecutionResponse, NativeExecutionResponse>[]
        {
            response => response with { SchemaVersion = "2.0.0" },
            response => response with { ContractId = "native.result/v2" },
            response => response with { ProducerModule = "caller" },
            response => response with { CommandId = Guid.NewGuid() },
            response => response with { LeaseId = Guid.NewGuid(), Attempt = 2 },
            response => response with { SoulId = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            response => response with { DeviceBindingId = OtherDevice },
            response => response with { ActiveReleaseBomGeneration = response.ActiveReleaseBomGeneration + 1 },
            response => response with { ActiveReleaseBomTokenSha256 = new string('f', 64) }
        };

        foreach (var mutate in mutations)
        {
            var receipt = await Execute(command, Envelope(command), new FakeNative(NativeStepResultV1.Success, mutate), new FakePostcondition(true));
            Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
            Assert.Equal("NATIVE_CONTRACT_OR_SCOPE_INVALID", receipt.ResultCode);
            Assert.False(receipt.RetryAllowed);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MissingWrongScopeUnknownOrWrongTokenActiveBomFailsBeforeNative()
    {
        var command = Command();
        var native = new FakeNative(NativeStepResultV1.Success);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(command, Envelope(command), native, new FakePostcondition(true), new FakeActiveReleaseBomReader((ActiveReleaseBomBindingV1?)null)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(command, Envelope(command), native, new FakePostcondition(true), new FakeActiveReleaseBomReader(Binding(device: OtherDevice))));
        await Assert.ThrowsAsync<NotSupportedException>(() => Execute(command, Envelope(command), native, new FakePostcondition(true), new FakeActiveReleaseBomReader(Binding(schema: "dps.active-release-bom-binding/v2"))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(command, Envelope(command), native, new FakePostcondition(true), new FakeActiveReleaseBomReader(Binding(token: Convert.ToBase64String(new byte[32])))));
        Assert.Equal(0, native.CallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForgedActionStepSideEffectCombinationIsRejectedBeforeNativeExecution()
    {
        var command = Command();
        var forged = command with
        {
            Steps = [command.Steps[0] with { StepKind = "fixture.tap", RetrySafe = false, PostconditionKind = "fixture-state-changed", Arguments = new Dictionary<string, string> { ["selector_ref"] = "fixture.button" } }],
            ActionKind = "observe", IsSideEffect = false, PlatformAuthorizationId = null
        };
        var native = new FakeNative(NativeStepResultV1.Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(forged, Envelope(forged), native, new FakePostcondition(true)));
        Assert.Equal(0, native.CallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConflictingPayloadWithSameCommandIdCannotReuseVerifiedAuthorization()
    {
        var authorized = Command();
        var conflicting = Command() with { TraceId = OtherTrace, IdempotencyKey = OtherIdempotency };
        var native = new FakeNative(NativeStepResultV1.Success);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(conflicting, Envelope(authorized), native, new FakePostcondition(true)));
        Assert.Equal(0, native.CallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovalFenceRevalidatesBeforePendingAndLeaseLifetimeExtendsUntilDurableAck()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider();
        var native = new BlockingSubmissionNative();

        var execution = Execute(command, Envelope(command), native, new FakePostcondition(true), fence: fence);
        await native.SubmissionEntered.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.NotNull(fence.LastLease);
        Assert.False(fence.LastLease.Disposed);
        Assert.True(fence.LastLease.PendingBegun);
        Assert.False(fence.LastLease.Acknowledged);
        Assert.Equal(1, fence.LastLease.RevalidationCount);
        native.ReleaseDurableAcknowledgement();

        var receipt = await execution;

        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);
        Assert.True(fence.LastLease.Acknowledged);
        Assert.True(fence.LastLease.Disposed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PolicyRevocationWaitsForSingleNativeCallbackAndDurableTerminalState()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider();
        var native = new BlockingSubmissionNative();

        var execution = Execute(command, Envelope(command), native, new FakePostcondition(true), fence: fence);
        await native.SubmissionEntered.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.NotNull(fence.LastLease);
        Assert.Equal(1, fence.LastLease.NativeCallbackCount);
        Assert.False(fence.LastLease.RollbackableBusinessTransactionOpenAtCallback);

        var revocation = fence.LastLease.AttemptPolicyRevocationAsync(TestContext.Current.CancellationToken);
        await fence.LastLease.RevocationEntered.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(revocation.IsCompleted);

        native.ReleaseDurableAcknowledgement();
        var receipt = await execution;
        await revocation.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);
        Assert.True(fence.LastLease.Acknowledged);
        Assert.True(fence.LastLease.RevocationObservedTerminalState);
        Assert.Equal(1, native.CallCount);
        Assert.Equal(1, fence.LastLease.NativeCallbackCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GatewayCannotAbandonPolicyGuardedCallAtItsLocalTimeout()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider(blockAfterTerminal: true);
        var execution = Execute(
            command,
            Envelope(command),
            new FakeNative(NativeStepResultV1.Success),
            new FakePostcondition(true),
            fence: fence,
            timeout: TimeSpan.FromMilliseconds(10));

        while (fence.LastLease is null) await Task.Yield();
        await fence.LastLease.TerminalReturnEntered.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        await Task.Delay(40, TestContext.Current.CancellationToken);
        Assert.False(execution.IsCompleted);

        fence.LastLease.ReleaseTerminalReturn();
        var receipt = await execution.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UncertainSubmissionRootsLeaseAttemptAndLateTaskUntilProcessDeath()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider();
        var native = new AbortUnconfirmedNative();

        var receipt = await Execute(
            command,
            Envelope(command),
            native,
            new FakePostcondition(true),
            fence: fence,
            timeout: TimeSpan.FromMilliseconds(10));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
        Assert.NotNull(fence.LastLease);
        Assert.True(fence.LastLease.GuardRetainedUntilProcessExit);
        Assert.False(fence.LastLease.Quarantined);
        Assert.False(fence.LastLease.Disposed);

        var revocation = fence.LastLease.AttemptPolicyRevocationAsync(TestContext.Current.CancellationToken);
        await fence.LastLease.RevocationEntered.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(40, TestContext.Current.CancellationToken);
        Assert.False(revocation.IsCompleted);
        Assert.False(native.LateWriteOccurred);

        native.CompleteLateSubmission();
        await Task.Yield();
        Assert.True(native.LateWriteOccurred);
        Assert.False(revocation.IsCompleted);

        fence.LastLease.SimulateProcessDeath();
        await revocation.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(fence.LastLease.RevocationObservedTerminalState);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessGuardianIsOnlyStrongRootAcrossDisposeAndForcedGc()
    {
        var (weakLease, retentionId) = await CreateOrphanedRetainedGuardAsync();
        ForceFullGc();
        await AssertGuardianRootedAndDisposeBlockedAsync(weakLease);
        ForceFullGc();
        Assert.True(IsAlive(weakLease));

        FakeApprovalExecutionFenceLease.SimulateGuardedProcessDeath(retentionId);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ForceFullGc();
            if (!IsAlive(weakLease)) break;
            await Task.Yield();
        }
        Assert.False(IsAlive(weakLease));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<FakeApprovalExecutionFenceLease> WeakLease, Guid RetentionId)>
        CreateOrphanedRetainedGuardAsync()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider();
        var native = new AbortUnconfirmedNative();
        var receipt = await Execute(
            command,
            Envelope(command),
            native,
            new FakePostcondition(true),
            fence: fence,
            timeout: TimeSpan.FromMilliseconds(10));
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
        var lease = Assert.IsType<FakeApprovalExecutionFenceLease>(fence.LastLease);
        var retentionId = Assert.IsType<Guid>(lease.RetentionId);
        var weakLease = new WeakReference<FakeApprovalExecutionFenceLease>(lease);
        fence.DropLastLeaseReference();
        return (weakLease, retentionId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AssertGuardianRootedAndDisposeBlockedAsync(
        WeakReference<FakeApprovalExecutionFenceLease> weakLease)
    {
        Assert.True(weakLease.TryGetTarget(out var rootedLease));
        Assert.NotNull(rootedLease);
        Assert.True(rootedLease.GuardHeld);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rootedLease.DisposeAsync().AsTask());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<FakeApprovalExecutionFenceLease> weakLease) =>
        weakLease.TryGetTarget(out _);

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProviderFailureAfterDurablePendingInvokesFailStopWithoutSignedReceipt()
    {
        foreach (var failure in new Func<Exception>[]
        {
            () => new InvalidOperationException("FAKE: provider failed after durable PENDING."),
            () => new OperationCanceledException("FAKE: provider abandoned the guarded callback.")
        })
        {
            var command = Command();
            var fence = new FakeApprovalExecutionFenceProvider(afterPendingFailure: failure);
            var native = new FakeNative(NativeStepResultV1.Success);
            var failStop = new ThrowingProcessFailStop();

            await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
                command,
                Envelope(command),
                native,
                new FakePostcondition(true),
                fence: fence,
                failStop: failStop));

            Assert.Equal(1, failStop.CallCount);
            Assert.Equal("APPROVAL_GUARDED_SUBMISSION_UNCERTAIN", failStop.LastReasonCode);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.False(fence.LastLease.Disposed);
            Assert.Equal(0, native.SubmissionCallCount);
            Assert.Equal(0, native.AbortCallCount);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GuardianRegistrationFailureOrInvalidReceiptInvokesFailStop()
    {
        var attacks = new FakeApprovalExecutionFenceProvider[]
        {
            new(retainThrows: true),
            new(retentionMutation: retention => retention with { ProcessRooted = false }),
            new(retentionMutation: retention => retention with { WorkerGeneration = retention.WorkerGeneration + 1 })
        };

        foreach (var fence in attacks)
        {
            var command = Command();
            var native = new AbortUnconfirmedNative();
            var failStop = new ThrowingProcessFailStop();

            await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
                command,
                Envelope(command),
                native,
                new FakePostcondition(true),
                fence: fence,
                failStop: failStop,
                timeout: TimeSpan.FromMilliseconds(10)));

            Assert.Equal(1, failStop.CallCount);
            Assert.Equal("APPROVAL_GUARDED_SUBMISSION_UNCERTAIN", failStop.LastReasonCode);
            Assert.Equal(1, native.SubmissionCallCount);
            Assert.Equal(0, native.AbortCallCount);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.False(fence.LastLease.Quarantined);
            Assert.False(fence.LastLease.Disposed);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProviderCannotInjectLegacyStopProofOrUnknownStateIntoCallback()
    {
        var command = Command();
        var callbackInjection = new FakeApprovalExecutionFenceProvider(
            callbackResultMutation: _ => NativeSubmissionCallbackResult.WaitForExternalReconciliation());
        var callbackFailStop = new ThrowingProcessFailStop();
        await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
            command,
            Envelope(command),
            new NonCooperativeNative(),
            new FakePostcondition(true),
            fence: callbackInjection,
            failStop: callbackFailStop,
            timeout: TimeSpan.FromMilliseconds(10)));
        Assert.Equal("APPROVAL_GUARDED_SUBMISSION_UNCERTAIN", callbackFailStop.LastReasonCode);
        Assert.Equal(1, callbackInjection.LastLease!.NativeCallbackCount);
        Assert.False(callbackInjection.LastLease.Quarantined);

        var postCallbackRewrite = new FakeApprovalExecutionFenceProvider(
            guardedResultMutation: result => new GuardedNativeSubmissionResult(
                result.Pending,
                NativeSubmissionCallbackResult.WaitForExternalReconciliation(),
                null,
                null,
                false));
        var rewriteNative = new FakeNative(NativeStepResultV1.Success);
        var rewriteFailStop = new ThrowingProcessFailStop();
        await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
            command,
            Envelope(command),
            rewriteNative,
            new FakePostcondition(true),
            fence: postCallbackRewrite,
            failStop: rewriteFailStop));
        Assert.Equal("APPROVAL_SUBMISSION_TERMINAL_INVALID", rewriteFailStop.LastReasonCode);
        Assert.Equal(1, rewriteNative.CallCount);
        Assert.Equal(1, postCallbackRewrite.LastLease!.NativeCallbackCount);

        var repeatedCallback = new FakeApprovalExecutionFenceProvider(invokeCallbackTwice: true);
        var repeatedNative = new FakeNative(NativeStepResultV1.Success);
        var repeatedFailStop = new ThrowingProcessFailStop();
        await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
            command,
            Envelope(command),
            repeatedNative,
            new FakePostcondition(true),
            fence: repeatedCallback,
            failStop: repeatedFailStop));
        Assert.Equal("APPROVAL_GUARDED_SUBMISSION_UNCERTAIN", repeatedFailStop.LastReasonCode);
        Assert.Equal(1, repeatedNative.CallCount);

        var historicalTamper = new FakeApprovalExecutionFenceProvider(
            beginMaySubmit: false,
            existingUnknown: true,
            unknownMutation: unknown =>
            {
                var changed = unknown.UnknownState with
                {
                    CommandId = Guid.NewGuid(),
                    StateSha256 = new string('0', 64)
                };
                changed = changed with
                {
                    StateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(changed)
                };
                return new VerifiedSubmissionUnknownAuthorization(changed);
            });
        var historicalFailStop = new ThrowingProcessFailStop();
        await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
            command,
            Envelope(command),
            new FakeNative(NativeStepResultV1.Success),
            new FakePostcondition(true),
            fence: historicalTamper,
            failStop: historicalFailStop));
        Assert.Equal("APPROVAL_SUBMISSION_TERMINAL_INVALID", historicalFailStop.LastReasonCode);
        Assert.Equal(0, historicalTamper.LastLease!.NativeCallbackCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LegacyStopProofV1CannotUnlockPendingGuard()
    {
        foreach (var attack in new[] { "attempt", "worker", "bom" })
        {
            var command = Command();
            var fence = new FakeApprovalExecutionFenceProvider();
            var native = new ForgedStopProofNative(attack);
            var receipt = await Execute(
                command,
                Envelope(command),
                native,
                new FakePostcondition(true),
                fence: fence,
                timeout: TimeSpan.FromMilliseconds(10));

            Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
            Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
            Assert.False(receipt.RetryAllowed);
            Assert.Equal(1, native.SubmissionCallCount);
            Assert.Equal(0, native.AbortCallCount);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.True(fence.LastLease.GuardRetainedUntilProcessExit);
            Assert.False(fence.LastLease.Quarantined);
            Assert.False(fence.LastLease.Disposed);
            fence.LastLease.SimulateProcessDeath();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ColdSubmissionTaskCannotEscapeFenceAndTimesOutUnknown()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider();
        var native = new ColdSubmissionNative();

        var receipt = await Execute(
            command, Envelope(command), native, new FakePostcondition(true),
            fence: fence, timeout: TimeSpan.FromMilliseconds(20));

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
        Assert.False(native.ColdDelegateRan);
        Assert.NotNull(fence.LastLease);
        Assert.True(fence.LastLease.PendingBegun);
        Assert.False(fence.LastLease.Acknowledged);
        Assert.True(fence.LastLease.GuardRetainedUntilProcessExit);
        Assert.False(fence.LastLease.Quarantined);
        Assert.False(fence.LastLease.Disposed);
        fence.LastLease.SimulateProcessDeath();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SynchronousThrowNullAndFaultedSubmissionRetainPendingUntilProcessDeath()
    {
        foreach (var failureMode in Enum.GetValues<NativeSubmitFailureMode>())
        {
            var command = Command();
            var fence = new FakeApprovalExecutionFenceProvider();
            var native = new FailingSubmissionNative(failureMode);

            var receipt = await Execute(
                command,
                Envelope(command),
                native,
                new FakePostcondition(true),
                fence: fence);

            Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
            Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
            Assert.False(receipt.RetryAllowed);
            Assert.Equal(1, native.CallCount);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.False(fence.LastLease.Acknowledged);
            Assert.True(fence.LastLease.GuardRetainedUntilProcessExit);
            Assert.False(fence.LastLease.Quarantined);
            Assert.False(fence.LastLease.Disposed);
            fence.LastLease.SimulateProcessDeath();
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task NativeSubmissionAckBindsCanonicalCommandAuthorizationBomAndFullScope()
    {
        var command = Command();
        var mutations = new Func<NativeSubmissionAck, NativeSubmissionAck>[]
        {
            acknowledgement => acknowledgement with { CommandId = Guid.NewGuid() },
            acknowledgement => acknowledgement with { LeaseId = Guid.NewGuid(), Attempt = 2 },
            acknowledgement => acknowledgement with { SoulId = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            acknowledgement => acknowledgement with { DeviceBindingId = OtherDevice },
            acknowledgement => acknowledgement with { PlatformAccountId = "pa_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" },
            acknowledgement => acknowledgement with { TraceId = OtherTrace, IdempotencyKey = OtherIdempotency },
            acknowledgement => acknowledgement with { CommandSha256 = new string('e', 64) },
            acknowledgement => acknowledgement with { AuthorizationSha256 = new string('e', 64) },
            acknowledgement => acknowledgement with { SubmissionAttemptId = Guid.NewGuid() },
            acknowledgement => acknowledgement with { SubmissionIntentSha256 = new string('e', 64) },
            acknowledgement => acknowledgement with { PendingStateSha256 = new string('e', 64) },
            acknowledgement => acknowledgement with { PrivacyClass = "personal" },
            acknowledgement => acknowledgement with { ActiveReleaseBomSha256 = new string('e', 64) },
            acknowledgement => acknowledgement with { ActiveReleaseBomGeneration = acknowledgement.ActiveReleaseBomGeneration + 1 },
            acknowledgement => acknowledgement with { ActiveReleaseBomTokenSha256 = new string('e', 64) },
            acknowledgement => acknowledgement with { SubmittedRequestSha256 = new string('e', 64) },
            acknowledgement => acknowledgement with { CompletionHandleId = Guid.NewGuid() },
            acknowledgement => acknowledgement with { AcknowledgementSha256 = new string('e', 64) }
        };

        foreach (var mutate in mutations)
        {
            var fence = new FakeApprovalExecutionFenceProvider();
            var receipt = await Execute(
                command,
                Envelope(command),
                new FakeNative(NativeStepResultV1.Success, mutateAcknowledgement: mutate),
                new FakePostcondition(true),
                fence: fence);
            Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
            Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
            Assert.False(receipt.RetryAllowed);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.False(fence.LastLease.Acknowledged);
            Assert.True(fence.LastLease.GuardRetainedUntilProcessExit);
            Assert.False(fence.LastLease.Quarantined);
            Assert.False(fence.LastLease.Disposed);
            fence.LastLease.SimulateProcessDeath();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokedExpiredUnknownOrCrossScopeApprovalFenceFailsBeforeNative()
    {
        var command = Command();
        var attacks = new[]
        {
            new FakeApprovalExecutionFenceProvider(requestMutation: request => request with { DeviceBindingId = OtherDevice }),
            new FakeApprovalExecutionFenceProvider(fenceMutation: fence => fence with { SchemaVersion = "2.0.0" }),
            new FakeApprovalExecutionFenceProvider(fenceMutation: fence => fence with { AcquiredAt = Now.AddSeconds(-1), ValidUntil = Now.AddMilliseconds(-1) }),
            new FakeApprovalExecutionFenceProvider(revalidationFails: true)
        };

        foreach (var attack in attacks)
        {
            var native = new FakeNative(NativeStepResultV1.Success);
            await Assert.ThrowsAnyAsync<Exception>(() => Execute(
                command, Envelope(command), native, new FakePostcondition(true), fence: attack));
            Assert.Equal(0, native.CallCount);
        }

        var invalidPendingFence = new FakeApprovalExecutionFenceProvider(
            pendingMutation: pending => pending with
            {
                PendingState = pending.PendingState with { StateSha256 = new string('f', 64) }
        });
        var pendingNative = new FakeNative(NativeStepResultV1.Success);
        var failStop = new ThrowingProcessFailStop();
        await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
            command, Envelope(command), pendingNative, new FakePostcondition(true),
            fence: invalidPendingFence, failStop: failStop));
        Assert.Equal("APPROVAL_SUBMISSION_TERMINAL_INVALID", failStop.LastReasonCode);
        Assert.Equal(0, pendingNative.CallCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MalformedOwnerAcknowledgedWrapperFailsClosedBeforeLeaseRelease()
    {
        var mutations = new Func<VerifiedSubmissionAcknowledgedAuthorization, VerifiedSubmissionAcknowledgedAuthorization>[]
        {
            verified => verified with
            {
                Acknowledgement = verified.Acknowledgement with
                {
                    SignatureBase64 = Convert.ToBase64String(new byte[63])
                }
            },
            verified => verified with
            {
                AcknowledgedState = verified.AcknowledgedState with
                {
                    StateSha256 = new string('f', 64)
                }
            }
        };

        foreach (var mutate in mutations)
        {
            var command = Command();
            var fence = new FakeApprovalExecutionFenceProvider(acknowledgedMutation: mutate);
            var failStop = new ThrowingProcessFailStop();
            await Assert.ThrowsAsync<TestFailStopException>(() => Execute(
                command,
                Envelope(command),
                new FakeNative(NativeStepResultV1.Success),
                new FakePostcondition(true),
                fence: fence,
                failStop: failStop));

            Assert.Equal("APPROVAL_SUBMISSION_TERMINAL_INVALID", failStop.LastReasonCode);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.False(fence.LastLease.Disposed);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExistingPendingBeginDispositionNeverReachesNativeSubmission()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider(beginMaySubmit: false);
        var native = new FakeNative(NativeStepResultV1.Success);

        var failStop = new ThrowingProcessFailStop();
        var receipt = await Execute(
            command,
            Envelope(command),
            native,
            new FakePostcondition(true),
            fence: fence,
            failStop: failStop);

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
        Assert.Equal(0, failStop.CallCount);
        Assert.Equal(0, native.CallCount);
        Assert.NotNull(fence.LastLease);
        Assert.False(fence.LastLease.PendingBegun);
        Assert.False(fence.LastLease.Acknowledged);
        Assert.True(fence.LastLease.Disposed);
        Assert.Equal(0, fence.LastLease.NativeCallbackCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentExistingPendingAndUnknownStatesNeverDispatchOrRetry()
    {
        const int contenders = 16;
        var command = Command();
        var authorization = Envelope(command);
        var authority = new SharedConcurrentSubmissionAuthority();
        var firstWaveProvider = new FakeApprovalExecutionFenceProvider(
            sharedAuthority: authority,
            acquireBarrier: new ConcurrentAcquireBarrier(contenders));
        var firstWaveNative = new FakeNative(NativeStepResultV1.Success);

        var firstWave = await Task.WhenAll(Enumerable.Range(0, contenders).Select(_ => Execute(
            command,
            authorization,
            firstWaveNative,
            new FakePostcondition(true),
            fence: firstWaveProvider)));

        Assert.Equal(1, authority.FirstInsertCount);
        Assert.Equal(1, authority.NativeCallbackCount);
        Assert.Equal(1, firstWaveNative.CallCount);
        Assert.Single(firstWave, receipt => string.Equals(
            receipt.Outcome,
            CommandReceiptV1.Success,
            StringComparison.Ordinal));
        Assert.Equal(contenders - 1, firstWave.Count(receipt =>
            string.Equals(receipt.Outcome, CommandReceiptV1.UnknownOutcome, StringComparison.Ordinal) &&
            string.Equals(receipt.ResultCode, NativeSubmissionCallbackResult.WaitingExternal, StringComparison.Ordinal) &&
            !receipt.RetryAllowed));

        var existingWaveProvider = new FakeApprovalExecutionFenceProvider(
            sharedAuthority: authority,
            acquireBarrier: new ConcurrentAcquireBarrier(contenders));
        var existingWaveNative = new FakeNative(NativeStepResultV1.Success);
        var existingWave = await Task.WhenAll(Enumerable.Range(0, contenders).Select(_ => Execute(
            command,
            authorization,
            existingWaveNative,
            new FakePostcondition(true),
            fence: existingWaveProvider)));

        Assert.All(existingWave, receipt =>
        {
            Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
            Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, receipt.ResultCode);
            Assert.False(receipt.RetryAllowed);
        });
        Assert.Equal(0, existingWaveNative.CallCount);
        Assert.Equal(1, authority.FirstInsertCount);
        Assert.Equal(1, authority.NativeCallbackCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FenceReleaseFailureAfterDispatchIsNonRetryableUnknown()
    {
        var command = Command();
        var fence = new FakeApprovalExecutionFenceProvider(disposalFails: true);
        var native = new FakeNative(NativeStepResultV1.Success);

        var receipt = await Execute(command, Envelope(command), native, new FakePostcondition(true), fence: fence);

        Assert.Equal(1, native.CallCount);
        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("APPROVAL_FENCE_RELEASE_UNCERTAIN", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task UnknownStepMajorAndMismatchedNativeStepFailClosed()
    {
        var command = Command();
        await Assert.ThrowsAsync<NotSupportedException>(() => Execute(command with { Steps = [command.Steps[0] with { StepKind = "shell" }] }, Envelope(command), new FakeNative(NativeStepResultV1.Success), new FakePostcondition(true)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Execute(command with { SchemaVersion = "2.0.0" }, Envelope(command), new FakeNative(NativeStepResultV1.Success), new FakePostcondition(true)));
        var mismatch = await Execute(command, Envelope(command), new FakeNative(NativeStepResultV1.Success, response => response with { StepResults = [response.StepResults[0] with { StepId = Guid.NewGuid() }] }), new FakePostcondition(true));
        Assert.Equal(CommandReceiptV1.UnknownOutcome, mismatch.Outcome);
        Assert.Equal("NATIVE_CONTRACT_OR_SCOPE_INVALID", mismatch.ResultCode);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void V1DispatchAndNativeResultRequireExactlyOneOrderedStepAndFullScope()
    {
        var command = Command();
        Assert.Throws<InvalidOperationException>(() => (command with { Steps = [command.Steps[0], command.Steps[0] with { StepId = Guid.NewGuid() }] }).Validate());
        var result = new NativeResultV1(
            NativeResultV1.CurrentSchemaVersion, NativeResultV1.CurrentContractId, NativeResultV1.CurrentProducerModule,
            Guid.NewGuid(), command.CommandId, command.LeaseId, command.Attempt, command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.TraceId, command.IdempotencyKey, Now, "internal", StableBom, 7, Binding().ComputeExecutionTokenSha256(),
            [StepResult(command.Steps[0]), StepResult(command.Steps[0] with { StepId = Guid.NewGuid() })]);
        Assert.Throws<InvalidOperationException>(() => result.Validate());
        Assert.Throws<ArgumentException>(() => (result with { ActiveReleaseBomTokenSha256 = string.Empty, StepResults = [StepResult(command.Steps[0])] }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task RealEcdsaVerifierBindsCommandScopeSignatureFormatBomGenerationAndToken()
    {
        var command = Command();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaExecutionAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        var signed = Sign(Envelope(command), signer);
        var gateway = new VerifiedExecutorGateway(
            new FixedClock(Now), ReceiptSigner, verifier, new FakeActiveReleaseBomReader(Binding()),
            new FakeApprovalExecutionFenceProvider(), new FakeNative(NativeStepResultV1.Success),
            new ThrowingProcessFailStop(), new FakePostcondition(true), TimeSpan.FromSeconds(1));
        var receipt = await gateway.ExecuteAsync(command, signed, TestContext.Current.CancellationToken);
        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.ExecuteAsync(
            command, signed with { ReleaseBomSha256 = new string('b', 64) }, TestContext.Current.CancellationToken));
        var otherGeneration = Sign(Envelope(command) with { ActiveReleaseBomGeneration = 8 }, signer);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.ExecuteAsync(
            command, otherGeneration, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void VerifierRejectsNonP256PublicKeysAndMalformedActiveToken()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => new EcdsaExecutionAuthorizationVerifier(p384.ExportSubjectPublicKeyInfo()));
        Assert.Throws<ArgumentException>(() => Binding(token: Convert.ToBase64String(new byte[31])).Validate());
        Assert.Throws<ArgumentException>(() => Binding(token: " " + StableToken).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void LegacyStopProofV1HistoricalVerifierBehaviorIsFrozenButNeverRuntimeAuthority()
    {
        var expected = new NativeStopRequest(
            Guid.Parse("7b000000-0000-0000-0000-00000000000b"),
            Guid.Parse("71000000-0000-0000-0000-000000000001"),
            Guid.Parse("72000000-0000-0000-0000-000000000002"),
            1,
            new string('1', 64),
            new string('2', 64),
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            StableBom,
            7,
            new string('3', 64),
            "wi_0123456789abcdef0123456789abcdef",
            1);
        var proof = StopConfirmation(expected);

        Assert.Equal(proof, StopProofVerifier.Verify(proof, expected, Now).Confirmation);
        Assert.Throws<UnauthorizedAccessException>(() => StopProofVerifier.Verify(
            proof with { SignatureBase64 = Convert.ToBase64String(new byte[NativeAbortConfirmation.P1363SignatureSizeBytes]) },
            expected,
            Now));
        Assert.Throws<UnauthorizedAccessException>(() => StopProofVerifier.Verify(
            proof with { KeyId = "another-key" },
            expected,
            Now));
        Assert.Throws<InvalidDataException>(() => StopProofVerifier.Verify(
            proof with { EvidenceSha256 = new string('f', 64) },
            expected,
            Now));
        Assert.Throws<UnauthorizedAccessException>(() => StopProofVerifier.Verify(
            proof,
            expected with { WorkerGeneration = 2 },
            Now));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void LegacyStopProofV1FrozenSchemaRequiresOccurredAtAndRejectsAlias()
    {
        using var schemaStream = typeof(VerifiedExecutorGatewayTests).Assembly.GetManifestResourceStream(
            "Dps.ExecutorGateway.Tests.native.stop.proof.v1.schema.json");
        Assert.NotNull(schemaStream);
        using var schema = JsonDocument.Parse(schemaStream);
        var required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.Contains("occurred_at", required);
        Assert.True(properties.TryGetProperty("occurred_at", out _));
        Assert.DoesNotContain("confirmed_at", required);
        Assert.False(properties.TryGetProperty("confirmed_at", out _));

        var expected = new NativeStopRequest(
            Guid.Parse("7b000000-0000-0000-0000-00000000000b"),
            Guid.Parse("71000000-0000-0000-0000-000000000001"),
            Guid.Parse("72000000-0000-0000-0000-000000000002"),
            1,
            new string('1', 64),
            new string('2', 64),
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            StableBom,
            7,
            new string('3', 64),
            "wi_0123456789abcdef0123456789abcdef",
            1);
        var proof = StopConfirmation(expected);
        var json = ExecutorGatewayContractJson.SerializeNativeStopProof(proof);
        Assert.Equal(proof, ExecutorGatewayContractJson.DeserializeNativeStopProof(json));
        using var instance = JsonDocument.Parse(json);
        Assert.True(instance.RootElement.TryGetProperty("occurred_at", out _));
        Assert.False(instance.RootElement.TryGetProperty("confirmed_at", out _));
        Assert.DoesNotContain("OccurredAt", json, StringComparison.Ordinal);
        Assert.Contains("\"occurred_at\":\"2026-01-01T00:00:00.0000000Z\"", json, StringComparison.Ordinal);

        var missingOccurredAt = required.Any(name => !string.Equals(name, "occurred_at", StringComparison.Ordinal) &&
            !instance.RootElement.TryGetProperty(name, out _));
        Assert.False(missingOccurredAt);
        using var aliasOnly = JsonDocument.Parse(json.Replace(
            "\"occurred_at\"",
            "\"confirmed_at\"",
            StringComparison.Ordinal));
        Assert.Contains(required, name => !aliasOnly.RootElement.TryGetProperty(name, out _));
        Assert.ThrowsAny<Exception>(() => ExecutorGatewayContractJson.DeserializeNativeStopProof(aliasOnly.RootElement.GetRawText()));
        Assert.ThrowsAny<Exception>(() => ExecutorGatewayContractJson.DeserializeNativeStopProof(json.Replace(
            "\"occurred_at\":\"2026-01-01T00:00:00.0000000Z\"",
            "\"occurred_at\":\"2026-01-01T00:00:00+00:00\"",
            StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => ExecutorGatewayContractJson.DeserializeNativeStopProof(json.Replace(
            "{",
            "{\"occurred_at\":\"2026-01-01T00:00:00.0000000Z\",",
            StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void LegacyNativeStopProofV1ArtifactsAreByteFrozenAndManifestIsQuarantineOnly()
    {
        using var freeze = JsonDocument.Parse(File.ReadAllBytes(RepositoryFile(
            "Modules/executor-gateway/contracts/provided/native.stop.proof.v1.freeze.json")));
        Assert.Equal("native.stop.proof/v1", freeze.RootElement.GetProperty("contractId").GetString());
        Assert.Equal("quarantine-only", freeze.RootElement.GetProperty("mode").GetString());
        Assert.Equal("executor-gateway", freeze.RootElement.GetProperty("ownerModule").GetString());
        Assert.False(freeze.RootElement.GetProperty("releaseEligible").GetBoolean());
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Modules/executor-gateway/contracts/provided/native.stop.proof.v1.schema.json"] =
                "8a7fbfca9f0358618c8a27dd3737ca7d034bf5b968870babf816a98020671dd4",
            ["Modules/executor-gateway/contracts/provided/Dps.ExecutorGateway.Contracts/NativeStopProofV1.cs"] =
                "bf4f2eebaef979597bee94149e6cc97b2187116b1dda4bcb3f3ad4aeae3e3076",
            ["Modules/executor-gateway/contracts/provided/Dps.ExecutorGateway.Contracts/ExecutorGatewayContractJson.cs"] =
                "6d6935f13d65eb10745d1ba7e5ec4905c0260147e9fc7d887e789e8c070cad88",
            ["Modules/executor-gateway/src/Dps.ExecutorGateway/EcdsaNativeStopProofVerifier.cs"] =
                "3ac3c8e26ec2e1ab4736fa78bab73a111874247b61cbe834751eef1a8c1468f2",
            ["Modules/executor-gateway/contracts/provided/Dps.ExecutorGateway.Contracts/Dps.ExecutorGateway.Contracts.csproj"] =
                "b05316a9f9bd07c06ae2a1b8e778986cd85fdc590e31075e3ef1b38853690ee8",
            ["Modules/executor-gateway/contracts/provided/Dps.ExecutorGateway.Contracts/LegacyNativeStopProofV1QuarantineClassifier.cs"] =
                "4444f2b177b3327adac5033183a3fcacd36d53f675e5993dad1ee31657db88d5",
            ["Modules/executor-gateway/contracts/provided/native.stop.proof.v1.corpus.json"] =
                "fce6fcf46ea25cc4bcc6b53c13abaf1bd7aa1e796bab6470d60b07dac655135a"
        };
        var declared = freeze.RootElement.GetProperty("artifacts").EnumerateArray().ToDictionary(
            item => item.GetProperty("path").GetString()!,
            item => item.GetProperty("sha256").GetString()!,
            StringComparer.Ordinal);
        Assert.Equal(expected.Count, declared.Count);
        Assert.All(expected, item => Assert.Equal(item.Value, declared[item.Key]));
        Assert.All(expected, item => Assert.Equal(
            item.Value,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RepositoryFile(item.Key))))));
        var runtimePolicy = freeze.RootElement.GetProperty("runtimePolicy");
        Assert.Equal("forbidden", runtimePolicy.GetProperty("emit").GetString());
        Assert.Equal("forbidden", runtimePolicy.GetProperty("verifyForDomainState").GetString());
        Assert.Equal("forbidden", runtimePolicy.GetProperty("authorizeUnknownSubmission").GetString());
        Assert.Equal("forbidden", runtimePolicy.GetProperty("authorizeGuardRelease").GetString());
        Assert.Equal("forbidden", runtimePolicy.GetProperty("authorizeRetry").GetString());
        Assert.Equal("forbidden", runtimePolicy.GetProperty("businessSuccess").GetString());
        Assert.Equal("bounded-identify-quarantine-and-audit", runtimePolicy.GetProperty("allowedUse").GetString());

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(RepositoryFile(
            "Modules/executor-gateway/module.yaml")));
        Assert.False(manifest.RootElement.GetProperty("module").GetProperty("releaseEligible").GetBoolean());
        var declaration = manifest.RootElement.GetProperty("contracts").GetProperty("provided")
            .EnumerateArray().Single(item => string.Equals(
                item.GetProperty("contractId").GetString(),
                "native.stop.proof",
                StringComparison.Ordinal));
        Assert.Equal("deprecated", declaration.GetProperty("status").GetString());
        Assert.Equal("quarantine-only", declaration.GetProperty("mode").GetString());
        Assert.Equal("executor-gateway", declaration.GetProperty("ownerModule").GetString());
        Assert.DoesNotContain(
            manifest.RootElement.GetProperty("communication").GetProperty("inbound").EnumerateArray(),
            item => string.Equals(
                item.GetProperty("contractId").GetString(),
                "native.stop.proof",
                StringComparison.Ordinal));

        var repositoryRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(RepositoryFile("governance/schemas/module-manifest.schema.json"))!,
            "..",
            ".."));
        var ownerDeclarations = new List<string>();
        var runtimeEdges = new List<string>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     Path.Combine(repositoryRoot, "Modules"),
                     "module.yaml",
                     SearchOption.AllDirectories))
        {
            using var candidate = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var candidateRoot = candidate.RootElement;
            var moduleId = candidateRoot.GetProperty("module").GetProperty("id").GetString()!;
            foreach (var provided in candidateRoot.GetProperty("contracts").GetProperty("provided").EnumerateArray())
            {
                if (string.Equals(provided.GetProperty("contractId").GetString(), "native.stop.proof", StringComparison.Ordinal))
                    ownerDeclarations.Add($"{moduleId}:{provided.GetProperty("ownerModule").GetString()}:{provided.GetProperty("status").GetString()}:{provided.GetProperty("mode").GetString()}");
            }
            foreach (var direction in new[] { "inbound", "outbound" })
            {
                foreach (var edge in candidateRoot.GetProperty("communication").GetProperty(direction).EnumerateArray())
                {
                    if (string.Equals(edge.GetProperty("contractId").GetString(), "native.stop.proof", StringComparison.Ordinal))
                        runtimeEdges.Add($"{moduleId}:{direction}:{edge.GetProperty("peerModule").GetString()}");
                }
            }
        }
        Assert.Equal(
            new[] { "executor-gateway:executor-gateway:deprecated:quarantine-only" },
            ownerDeclarations);
        Assert.Empty(runtimeEdges);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void LegacyNativeStopProofV1CorpusRejectsSerializationAttacksAndReplayNeverGainsAuthority()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(RepositoryFile(
            "Modules/executor-gateway/contracts/provided/native.stop.proof.v1.corpus.json")));
        var root = corpus.RootElement;
        Assert.Equal("quarantine-only", root.GetProperty("mode").GetString());
        Assert.False(root.GetProperty("issuanceAllowed").GetBoolean());
        Assert.False(root.GetProperty("verificationMayAuthorizeDomainState").GetBoolean());
        Assert.False(root.GetProperty("businessSuccessAllowed").GetBoolean());

        var baseJson = root.GetProperty("base").GetRawText();
        var baseWire = Encoding.UTF8.GetBytes(baseJson);
        var first = LegacyNativeStopProofV1QuarantineClassifier.Classify(baseWire);
        var replay = LegacyNativeStopProofV1QuarantineClassifier.Classify(baseWire);
        Assert.Equal(first, replay);
        Assert.Equal("native.stop.proof/v1", first.ContractId);
        Assert.Equal("quarantine-only", first.Mode);
        Assert.Equal("QUARANTINE", first.Disposition);

        var proof = ExecutorGatewayContractJson.DeserializeNativeStopProof(baseJson);
        var canonical = ExecutorGatewayContractJson.SerializeNativeStopProof(proof);
        var canonicalMetadata = LegacyNativeStopProofV1QuarantineClassifier.Classify(
            Encoding.UTF8.GetBytes(canonical));
        Assert.Equal("QUARANTINE", canonicalMetadata.Disposition);

        var attacks = new byte[][]
        {
            Encoding.UTF8.GetBytes(canonical.Replace(
                "\"contract_id\":\"native.stop.proof/v1\"",
                "\"contract_id\":\"native.stop.proof/v1\",\"contract_id\":\"native.stop.proof/v1\"",
                StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(canonical.Insert(canonical.Length - 1, ",\"unknown\":true")),
            Enumerable.Repeat((byte)' ', LegacyNativeStopProofV1QuarantineClassifier.MaximumWireBytes + 1).ToArray(),
            [0xff],
            Encoding.UTF8.GetBytes(canonical.Replace("\"attempt\":1", "\"attempt\":0", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(canonical.Replace(Soul, "person@example.com", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(canonical.Replace("native.stop.proof/v1", "native.stop.proof/v2", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(canonical.Replace("\"schema_version\":\"1.0.0\"", "\"schema_version\":\"1\"", StringComparison.Ordinal))
        };
        Assert.All(attacks, attack => Assert.ThrowsAny<Exception>(() =>
            LegacyNativeStopProofV1QuarantineClassifier.Classify(attack)));

        var caseIds = root.GetProperty("cases").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(caseIds.SetEquals(
            new HashSet<string>(
            [
                "canonical-shape-is-still-quarantined",
                "duplicate-contract-id",
                "unknown-field",
                "oversized-wire",
                "invalid-utf8",
                "attempt-zero",
                "identity-shape-attack",
                "unknown-major",
                "noncanonical-version"
            ], StringComparer.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void RuntimeSurfacesExposeNoLegacyNativeStopProofAuthority()
    {
        static bool ReferencesLegacyAuthority(Type type)
        {
            if (type == typeof(NativeAbortConfirmation) || type == typeof(INativeStopProofVerifier))
                return true;
            if (type.HasElementType && type.GetElementType() is Type element && ReferencesLegacyAuthority(element))
                return true;
            return type.IsGenericType && type.GetGenericArguments().Any(ReferencesLegacyAuthority);
        }

        var positiveSurfaces = new[]
        {
            typeof(VerifiedExecutorGateway),
            typeof(ICommandExecutionGateway),
            typeof(INativeCommandExecutor),
            typeof(INativeSubmissionAttempt),
            typeof(IApprovalExecutionFenceLease),
            typeof(NativeSubmissionCallbackResult),
            typeof(GuardedNativeSubmissionResult)
        };
        foreach (var surface in positiveSurfaces)
        {
            Assert.DoesNotContain(
                surface.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => ReferencesLegacyAuthority(parameter.ParameterType));
            Assert.DoesNotContain(
                surface.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
                method => ReferencesLegacyAuthority(method.ReturnType) ||
                          method.GetParameters().Any(parameter => ReferencesLegacyAuthority(parameter.ParameterType)));
            Assert.DoesNotContain(
                surface.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
                property => ReferencesLegacyAuthority(property.PropertyType));
        }
        Assert.DoesNotContain(
            typeof(INativeSubmissionAttempt).GetMethods(),
            method => string.Equals(method.Name, "AbortAndConfirmStoppedAsync", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(VerifiedExecutorGateway).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(INativeStopProofVerifier));
        Assert.DoesNotContain(
            typeof(VerifiedExecutorGateway).GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
            field => ReferencesLegacyAuthority(field.FieldType));
        Assert.Null(typeof(VerifiedExecutorGateway).Assembly.GetType(
            "Dps.ExecutorGateway.INativeStopProofVerifier",
            throwOnError: false,
            ignoreCase: false));
        Assert.Null(typeof(VerifiedExecutorGateway).Assembly.GetType(
            "Dps.ExecutorGateway.EcdsaNativeStopProofVerifier",
            throwOnError: false,
            ignoreCase: false));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task OpaqueIdBaselinesRejectPiiTokenAndDelimiterShapesAcrossGatewayContracts()
    {
        var first = Command();
        var second = Command() with { TraceId = OtherTrace, IdempotencyKey = OtherIdempotency };
        Assert.NotEqual(ExecutionAuthorizationBinding.ComputeCommandSha256(first), ExecutionAuthorizationBinding.ComputeCommandSha256(second));
        Assert.Throws<ArgumentException>(() => (first with { DeviceBindingId = "db_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { PlatformAccountId = "pa_user@example.com" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { TraceId = "trace|segment" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { IdempotencyKey = "Bearer secret-token" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { DeviceBindingId = Device + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { PlatformAccountId = Account + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { TraceId = Trace + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (first with { IdempotencyKey = Idempotency + "\n" }).Validate());

        var native = ResultFor(first);
        Assert.Throws<ArgumentException>(() => (native with { DeviceBindingId = "db_60123456789" }).Validate());
        Assert.Throws<ArgumentException>(() => (native with { PlatformAccountId = "pa_user@example.com" }).Validate());
        Assert.Throws<ArgumentException>(() => (native with { TraceId = "trace|segment" }).Validate());
        Assert.Throws<ArgumentException>(() => (native with { IdempotencyKey = "Bearer secret-token" }).Validate());
        Assert.Throws<ArgumentException>(() => (native with { DeviceBindingId = Device + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (native with { PlatformAccountId = Account + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (native with { TraceId = Trace + "\n" }).Validate());
        Assert.Throws<ArgumentException>(() => (native with { IdempotencyKey = Idempotency + "\n" }).Validate());

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaExecutionAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        var firstEnvelope = Envelope(first);
        var secondEnvelope = Envelope(second);
        var forged = secondEnvelope with { SignatureBase64 = Sign(firstEnvelope, signer).SignatureBase64 };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await verifier.VerifyAsync(second, forged, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SchemaAndRuntimeVersionAndLengthRulesAgree()
    {
        var command = Command();
        foreach (var invalidVersion in new[] { "01", "1.beta", "1.2.3.4" })
            Assert.Throws<NotSupportedException>(() => (command with { SchemaVersion = invalidVersion }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { PlatformAuthorizationId = new string('x', 257) }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Steps = [command.Steps[0] with { StepKind = "ui.locate", PostconditionKind = "selector-resolved", Arguments = new Dictionary<string, string> { ["selector_ref"] = string.Empty } }] }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Steps = [command.Steps[0] with { StepKind = "ui.locate", PostconditionKind = "selector-resolved", Arguments = new Dictionary<string, string> { ["selector_ref"] = new string('x', 257) } }] }).Validate());

        var result = ResultFor(command);
        foreach (var invalidVersion in new[] { "01", "1.beta", "1.2.3.4" })
            Assert.Throws<NotSupportedException>(() => (result with { SchemaVersion = invalidVersion }).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void NativeSubmissionAckStrictJsonRoundTripRejectsAliasDuplicateDateAndEmptyUuid()
    {
        var acknowledgement = ContractAcknowledgement();
        var json = ExecutorGatewayContractJson.SerializeNativeSubmissionAck(acknowledgement);
        var roundTrip = ExecutorGatewayContractJson.DeserializeNativeSubmissionAck(json);

        Assert.Equal(acknowledgement, roundTrip);
        Assert.Contains("\"schema_version\":\"1.0.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"producer_module\":\"windows-edge-worker\"", json, StringComparison.Ordinal);
        Assert.Contains("\"occurred_at\":\"2026-01-01T00:00:00.0000000Z\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);

        var submissionId = acknowledgement.SubmissionId.ToString("D");
        var mixedHexUuid = acknowledgement.SubmissionAttemptId.ToString("D");
        var attacks = new[]
        {
            json.Replace("{", "{\"unknown_field\":true,", StringComparison.Ordinal),
            json.Replace("{", "{\"schema_version\":\"1.0.0\",", StringComparison.Ordinal),
            json.Replace("2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:00+00:00", StringComparison.Ordinal),
            json.Replace(submissionId, Guid.Empty.ToString("D"), StringComparison.Ordinal),
            json.Replace(mixedHexUuid, mixedHexUuid.ToUpperInvariant(), StringComparison.Ordinal),
            json.Replace("\"privacy_class\":\"internal\",", string.Empty, StringComparison.Ordinal)
        };

        foreach (var attack in attacks)
            Assert.ThrowsAny<Exception>(() => ExecutorGatewayContractJson.DeserializeNativeSubmissionAck(attack));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void RuntimeExecuteSurfaceCannotAcceptPerCallAuthoritiesOrNativeBoundaries()
    {
        Assert.Equal(typeof(NativeResultV1).Assembly, typeof(NativeSubmissionAck).Assembly);
        Assert.Equal(typeof(NativeResultV1).Assembly, typeof(NativeAbortConfirmation).Assembly);
        Assert.Equal(typeof(NativeResultV1).Assembly, typeof(NativeStopProofProtocolV1).Assembly);
        Assert.Equal("Dps.ExecutorGateway.Contracts", typeof(NativeSubmissionAck).Assembly.GetName().Name);
        var interfaceMethod = typeof(ICommandExecutionGateway).GetMethod(nameof(ICommandExecutionGateway.ExecuteAsync));
        Assert.NotNull(interfaceMethod);
        Assert.Equal(
            new[] { typeof(CommandDispatchV1), typeof(ExecutionAuthorizationV1), typeof(CancellationToken) },
            interfaceMethod.GetParameters().Select(parameter => parameter.ParameterType));

        var publicExecuteMethods = typeof(VerifiedExecutorGateway).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(VerifiedExecutorGateway.ExecuteAsync))
            .ToArray();
        Assert.Single(publicExecuteMethods);
        Assert.Equal(
            new[] { typeof(CommandDispatchV1), typeof(ExecutionAuthorizationV1), typeof(CancellationToken) },
            publicExecuteMethods[0].GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            publicExecuteMethods[0].GetParameters(),
            parameter => parameter.ParameterType == typeof(IExecutionAuthorizationVerifier) ||
                         parameter.ParameterType == typeof(IVerifiedActiveReleaseBomReader) ||
                         parameter.ParameterType == typeof(IApprovalExecutionFenceProvider) ||
                         parameter.ParameterType == typeof(INativeCommandExecutor) ||
                         parameter.ParameterType == typeof(IBusinessPostconditionVerifier) ||
                         parameter.ParameterType == typeof(EcdsaCommandReceiptSigner) ||
                         parameter.ParameterType == typeof(TimeSpan));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void RequiredSecurityContractTestIdsArePresent()
    {
        using var stream = typeof(VerifiedExecutorGatewayTests).Assembly.GetManifestResourceStream("Dps.ExecutorGateway.Tests.required-security-tests.v2.json");
        Assert.NotNull(stream);
        using var inventory = JsonDocument.Parse(stream);
        Assert.Equal("dps.required-tests/v2", inventory.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("executor-gateway.contract", inventory.RootElement.GetProperty("suiteId").GetString());
        var required = inventory.RootElement.GetProperty("requiredTests").EnumerateArray()
            .Select(value => (
                Id: value.GetProperty("id").GetString()!,
                Category: value.GetProperty("category").GetString()!))
            .ToArray();
        Assert.Equal(required.Length, required.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        var actual = typeof(VerifiedExecutorGatewayTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null && method.DeclaringType is not null)
            .ToDictionary(
                method => $"{method.DeclaringType!.FullName}.{method.Name}",
                method => method.GetCustomAttributesData()
                    .Where(attribute =>
                        attribute.AttributeType == typeof(TraitAttribute) &&
                        attribute.ConstructorArguments.Count == 2 &&
                        string.Equals(attribute.ConstructorArguments[0].Value as string, "Category", StringComparison.Ordinal))
                    .Select(attribute => attribute.ConstructorArguments[1].Value as string)
                    .Where(value => value is not null)
                    .Cast<string>()
                    .ToArray(),
                StringComparer.Ordinal);
        Assert.All(required, item =>
        {
            Assert.Contains(item.Category, new[] { "Unit", "Contract" });
            Assert.True(actual.TryGetValue(item.Id, out var categories), $"Required test '{item.Id}' is missing.");
            Assert.Single(categories!);
            Assert.Equal(item.Category, categories![0]);
        });
    }

    private static CommandDispatchV1 Command() => new(
        CommandDispatchV1.CurrentSchemaVersion, CommandDispatchV1.CurrentContractId, CommandDispatchV1.CurrentProducerModule,
        Guid.Parse("71000000-0000-0000-0000-000000000001"), Guid.Parse("72000000-0000-0000-0000-000000000002"), Guid.Parse("73000000-0000-0000-0000-000000000003"), new string('d', 64),
        Soul, Device, Account, Trace, Idempotency, Now.AddSeconds(-2), "internal", "observe", false, null,
        Guid.Parse("74000000-0000-0000-0000-000000000004"), "worker-a", Now.AddMinutes(1), 1,
        [new CommandStepV1(Guid.Parse("75000000-0000-0000-0000-000000000005"), "ui.observe", new Dictionary<string, string>(), true, "native-read-complete")]);

    private static ExecutionAuthorizationV1 Envelope(CommandDispatchV1 command, ActiveReleaseBomBindingV1? binding = null)
    {
        binding ??= Binding();
        return new ExecutionAuthorizationV1(
            ExecutionAuthorizationV1.CurrentSchemaVersion, ExecutionAuthorizationV1.CurrentContractId, ExecutionAuthorizationV1.CurrentProducerModule,
            ExecutionAuthorizationV1.CurrentSignatureDomain, ExecutionAuthorizationV1.CurrentCanonicalEncoding, ExecutionAuthorizationV1.CurrentCommandDigestAlgorithm,
            ExecutionAuthorizationV1.CurrentSignatureAlgorithm, ExecutionAuthorizationV1.CurrentSignatureFormat, ExecutionAuthorizationV1.CurrentSignatureEncoding,
            ExecutionAuthorizationV1.CurrentCallerModule, ExecutionAuthorizationV1.CurrentAuthScope, command.CommandId, command.LeaseId, command.Attempt,
            command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.TraceId, command.IdempotencyKey, Now.AddSeconds(-1), "internal",
            ExecutionAuthorizationBinding.ComputeCommandSha256(command), binding.ReleaseBomSha256, binding.Generation, binding.ComputeExecutionTokenSha256(), Now.AddSeconds(30), false,
            Convert.ToBase64String(new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes]));
    }

    private static ActiveReleaseBomBindingV1 Binding(
        string? digest = null,
        string? device = null,
        string? schema = null,
        long generation = 7,
        string? token = null,
        string status = "active") =>
        new(
            schema ?? ActiveReleaseBomBindingV1.CurrentSchemaVersion,
            device ?? Device,
            digest ?? StableBom,
            generation,
            token ?? StableToken,
            status);

    private static ExecutionAuthorizationV1 Sign(ExecutionAuthorizationV1 authorization, ECDsa signer)
    {
        var payload = EcdsaExecutionAuthorizationVerifier.CanonicalBytes(authorization);
        try
        {
            var signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            try { return authorization with { SignatureBase64 = Convert.ToBase64String(signature) }; }
            finally { CryptographicOperations.ZeroMemory(signature); }
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static NativeResultV1 ResultFor(CommandDispatchV1 command) => new(
        NativeResultV1.CurrentSchemaVersion, NativeResultV1.CurrentContractId, NativeResultV1.CurrentProducerModule,
        Guid.Parse("76000000-0000-0000-0000-000000000006"), command.CommandId, command.LeaseId, command.Attempt,
        command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.TraceId, command.IdempotencyKey, Now, "internal",
        StableBom, 7, Binding().ComputeExecutionTokenSha256(), [StepResult(command.Steps[0])]);

    private static NativeStepResultV1 StepResult(CommandStepV1 step) => new(step.StepId, step.StepKind, NativeStepResultV1.Success, "FAKE", new string('b', 64));

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Repository file is unavailable for the contract test.", relativePath);
    }

    private static Task<SignedCommandReceiptV1> Execute(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        INativeCommandExecutor native,
        IBusinessPostconditionVerifier postcondition,
        IVerifiedActiveReleaseBomReader? active = null,
        IApprovalExecutionFenceProvider? fence = null,
        ITrustedClock? clock = null,
        IExecutorProcessFailStop? failStop = null,
        TimeSpan? timeout = null) =>
        new VerifiedExecutorGateway(
            clock ?? new FixedClock(Now), ReceiptSigner,
            new FakeAuthorizationVerifier(new VerifiedExecutionAuthorization(authorization)),
            active ?? new FakeActiveReleaseBomReader(Binding()), fence ?? new FakeApprovalExecutionFenceProvider(),
            native, failStop ?? new ThrowingProcessFailStop(), postcondition,
            timeout ?? TimeSpan.FromSeconds(1)).ExecuteAsync(
                command, authorization, TestContext.Current.CancellationToken);

    private sealed class TestFailStopException(string reasonCode, Exception cause)
        : Exception($"TEST FAIL-STOP: {reasonCode}", cause);

    private sealed class ThrowingProcessFailStop : IExecutorProcessFailStop
    {
        public int CallCount { get; private set; }
        public string? LastReasonCode { get; private set; }

        public void TerminateProcess(string reasonCode, Exception cause)
        {
            CallCount++;
            LastReasonCode = reasonCode;
            throw new TestFailStopException(reasonCode, cause);
        }
    }

    private sealed class FakeAuthorizationVerifier(VerifiedExecutionAuthorization authorization, bool valid = true) : IExecutionAuthorizationVerifier
    {
        public ValueTask<VerifiedExecutionAuthorization> VerifyAsync(CommandDispatchV1 command, ExecutionAuthorizationV1 envelope, CancellationToken cancellationToken)
        {
            if (!valid) throw new UnauthorizedAccessException("FAKE: signature rejected.");
            return ValueTask.FromResult(authorization);
        }
    }

    private sealed class BlockingAuthorizationVerifier(ExecutionAuthorizationV1 authorization) : IExecutionAuthorizationVerifier
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult(true);
        public async ValueTask<VerifiedExecutionAuthorization> VerifyAsync(CommandDispatchV1 command, ExecutionAuthorizationV1 envelope, CancellationToken cancellationToken)
        {
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return new VerifiedExecutionAuthorization(authorization);
        }
    }

    private sealed class FakeActiveReleaseBomReader(params ActiveReleaseBomBindingV1?[] bindings) : IVerifiedActiveReleaseBomReader
    {
        private readonly Queue<ActiveReleaseBomBindingV1?> _bindings = new(bindings);
        private ActiveReleaseBomBindingV1? _last = bindings.LastOrDefault();
        public int CallCount { get; private set; }
        public ValueTask<ActiveReleaseBomBindingV1?> ReadVerifiedActiveAsync(string deviceBindingId, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_bindings.Count > 0) _last = _bindings.Dequeue();
            return ValueTask.FromResult(_last);
        }
    }

    private sealed class SharedConcurrentSubmissionAuthority
    {
        private int _firstInsertClaimed;
        private int _firstInsertCount;
        private int _nativeCallbackCount;

        public int FirstInsertCount => Volatile.Read(ref _firstInsertCount);
        public int NativeCallbackCount => Volatile.Read(ref _nativeCallbackCount);

        public bool TryClaimFirstInsert()
        {
            if (Interlocked.CompareExchange(ref _firstInsertClaimed, 1, 0) != 0) return false;
            Interlocked.Increment(ref _firstInsertCount);
            return true;
        }

        public void RecordNativeCallback() => Interlocked.Increment(ref _nativeCallbackCount);
    }

    private sealed class ConcurrentAcquireBarrier(int participantCount)
    {
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participantCount;

        public Task ArriveAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remaining) <= 0) _released.TrySetResult(true);
            return _released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeApprovalExecutionFenceProvider(
        Func<ApprovalExecutionFenceRequestV1, ApprovalExecutionFenceRequestV1>? requestMutation = null,
        Func<ApprovalExecutionFenceV1, ApprovalExecutionFenceV1>? fenceMutation = null,
        Func<ApprovalExecutionFenceV1, ApprovalExecutionFenceV1>? revalidationMutation = null,
        Func<VerifiedSubmissionPendingAuthorization, VerifiedSubmissionPendingAuthorization>? pendingMutation = null,
        Func<VerifiedSubmissionAcknowledgedAuthorization, VerifiedSubmissionAcknowledgedAuthorization>? acknowledgedMutation = null,
        Func<NativeSubmissionCallbackResult, NativeSubmissionCallbackResult>? callbackResultMutation = null,
        Func<GuardedNativeSubmissionResult, GuardedNativeSubmissionResult>? guardedResultMutation = null,
        Func<VerifiedSubmissionUnknownAuthorization, VerifiedSubmissionUnknownAuthorization>? unknownMutation = null,
        Func<NativeSubmissionGuardRetention, NativeSubmissionGuardRetention>? retentionMutation = null,
        bool beginMaySubmit = true,
        bool revalidationFails = false,
        bool disposalFails = false,
        bool blockAfterTerminal = false,
        Func<Exception>? afterPendingFailure = null,
        bool retainThrows = false,
        bool existingUnknown = false,
        bool invokeCallbackTwice = false,
        SharedConcurrentSubmissionAuthority? sharedAuthority = null,
        ConcurrentAcquireBarrier? acquireBarrier = null) : IApprovalExecutionFenceProvider
    {
        public FakeApprovalExecutionFenceLease? LastLease { get; private set; }
        public void DropLastLeaseReference() => LastLease = null;

        public Task<IApprovalExecutionFenceLease> AcquireAsync(
            CommandDispatchV1 command,
            ExecutionAuthorizationV1 authorization,
            string nativeRequestBindingSha256,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ApprovalExecutionFenceRequestV1(
                ApprovalExecutionFenceRequestV1.CurrentSchemaVersion,
                ApprovalExecutionFenceRequestV1.CurrentContractId,
                ApprovalExecutionFenceRequestV1.CurrentConsumerModule,
                command.ApprovalId,
                Guid.Parse("79000000-0000-0000-0000-000000000009"),
                command.SoulId,
                command.DeviceBindingId,
                command.PlatformAccountId,
                command.TraceId,
                command.IdempotencyKey,
                command.ApprovalSha256,
                3,
                4,
                new string('e', 64),
                authorization.ReleaseBomSha256);
            request = requestMutation?.Invoke(request) ?? request;
            var fence = new ApprovalExecutionFenceV1(
                ApprovalExecutionFenceV1.CurrentSchemaVersion,
                ApprovalExecutionFenceV1.CurrentContractId,
                ApprovalExecutionFenceV1.CurrentProducerModule,
                Guid.Parse("7a000000-0000-0000-0000-00000000000a"),
                request.ApprovalId,
                request.ProposalId,
                request.SoulId,
                request.DeviceBindingId,
                request.PlatformAccountId,
                request.TraceId,
                request.IdempotencyKey,
                request.ApprovalSha256,
                request.ExpectedStatusRevision,
                request.ExpectedRuntimeRevision,
                request.ExpectedRuntimeStateSha256,
                request.ExpectedReleaseBomSha256,
                Now.AddMilliseconds(-100),
                Now.AddSeconds(1),
                "internal");
            fence = fenceMutation?.Invoke(fence) ?? fence;
            LastLease = new FakeApprovalExecutionFenceLease(
                command, authorization, request, fence, nativeRequestBindingSha256,
                revalidationMutation, pendingMutation, acknowledgedMutation, callbackResultMutation,
                guardedResultMutation,
                unknownMutation, retentionMutation, beginMaySubmit, revalidationFails, disposalFails,
                blockAfterTerminal, afterPendingFailure, retainThrows, existingUnknown,
                invokeCallbackTwice, sharedAuthority, acquireBarrier);
            return Task.FromResult<IApprovalExecutionFenceLease>(LastLease);
        }
    }

    private sealed class FakeApprovalExecutionFenceLease(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        ApprovalExecutionFenceRequestV1 request,
        ApprovalExecutionFenceV1 fence,
        string nativeRequestBindingSha256,
        Func<ApprovalExecutionFenceV1, ApprovalExecutionFenceV1>? revalidationMutation,
        Func<VerifiedSubmissionPendingAuthorization, VerifiedSubmissionPendingAuthorization>? pendingMutation,
        Func<VerifiedSubmissionAcknowledgedAuthorization, VerifiedSubmissionAcknowledgedAuthorization>? acknowledgedMutation,
        Func<NativeSubmissionCallbackResult, NativeSubmissionCallbackResult>? callbackResultMutation,
        Func<GuardedNativeSubmissionResult, GuardedNativeSubmissionResult>? guardedResultMutation,
        Func<VerifiedSubmissionUnknownAuthorization, VerifiedSubmissionUnknownAuthorization>? unknownMutation,
        Func<NativeSubmissionGuardRetention, NativeSubmissionGuardRetention>? retentionMutation,
        bool beginMaySubmit,
        bool revalidationFails,
        bool disposalFails,
        bool blockAfterTerminal,
        Func<Exception>? afterPendingFailure,
        bool retainThrows,
        bool existingUnknown,
        bool invokeCallbackTwice,
        SharedConcurrentSubmissionAuthority? sharedAuthority,
        ConcurrentAcquireBarrier? acquireBarrier) : IApprovalExecutionFenceLease
    {
        private static readonly string LifecycleSignature = Convert.ToBase64String(new byte[64]);
        private static readonly ConcurrentDictionary<Guid, object[]> ProcessGuardian = new();
        private readonly SemaphoreSlim _crossCommitGuard = new(1, 1);
        private readonly TaskCompletionSource<bool> _revocationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _terminalReturnEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _terminalReturnRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private VerifiedSubmissionPendingAuthorization? _pending;
        private bool _guardHeld;
        private Guid? _retentionId;
        public ApprovalExecutionFenceRequestV1 Request { get; } = request;
        public ApprovalExecutionFenceV1 Fence { get; } = fence;
        public string FenceRequestSha256 { get; } = new string('9', 64);
        public string NativeRequestBindingSha256 { get; } = nativeRequestBindingSha256;
        public int RevalidationCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool PendingBegun { get; private set; }
        public bool Acknowledged { get; private set; }
        public bool Quarantined { get; private set; }
        public int NativeCallbackCount { get; private set; }
        public bool RollbackableBusinessTransactionOpenAtCallback { get; private set; }
        public bool GuardRetainedUntilProcessExit => _retentionId is not null;
        public Guid? RetentionId => _retentionId;
        public bool GuardHeld => _guardHeld;
        public Task RevocationEntered => _revocationEntered.Task;
        public bool RevocationObservedTerminalState { get; private set; }
        public Task TerminalReturnEntered => _terminalReturnEntered.Task;
        public void ReleaseTerminalReturn() => _terminalReturnRelease.TrySetResult(true);

        public Task<ApprovalExecutionFenceV1> RevalidateForNativeDispatchAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PendingBegun)
                throw new UnauthorizedAccessException("FAKE: policy transaction is closed after durable PENDING.");
            RevalidationCount++;
            if (revalidationFails) throw new UnauthorizedAccessException("FAKE: approval was revoked before native dispatch.");
            return Task.FromResult(revalidationMutation?.Invoke(Fence) ?? Fence);
        }

        public async ValueTask<GuardedNativeSubmissionResult> ExecuteFirstNativeSubmissionAsync(
            Func<VerifiedSubmissionPendingAuthorization, CancellationToken, Task<NativeSubmissionCallbackResult>> callback,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(callback);
            cancellationToken.ThrowIfCancellationRequested();
            await _crossCommitGuard.WaitAsync(cancellationToken);
            _guardHeld = true;
            var maySubmit = beginMaySubmit;
            if (sharedAuthority is not null)
            {
                if (acquireBarrier is not null)
                    await acquireBarrier.ArriveAsync(cancellationToken);
                maySubmit = maySubmit && sharedAuthority.TryClaimFirstInsert();
            }
            if (!maySubmit)
            {
                var existingPending = CreateSubmissionPending(cancellationToken, markInserted: false);
                VerifiedSubmissionUnknownAuthorization? unknown = null;
                if (existingUnknown)
                {
                    unknown = CreateSubmissionUnknown(new string('e', 64));
                    unknown = unknownMutation?.Invoke(unknown) ?? unknown;
                    Quarantined = true;
                }
                _guardHeld = false;
                _crossCommitGuard.Release();
                var existingResult = new GuardedNativeSubmissionResult(
                    existingPending,
                    NativeSubmissionCallbackResult.WaitForExternalReconciliation(),
                    null,
                    unknown,
                    false);
                return guardedResultMutation?.Invoke(existingResult) ?? existingResult;
            }

            var pending = CreateSubmissionPending(cancellationToken);
            // The fake marks its rollbackable business transaction closed before invoking the
            // callback. This models the required boundary but is not PostgreSQL evidence.
            RollbackableBusinessTransactionOpenAtCallback = false;
            if (afterPendingFailure is not null)
                throw afterPendingFailure();
            NativeCallbackCount++;
            sharedAuthority?.RecordNativeCallback();
            var callbackResult = await callback(pending, cancellationToken);
            if (invokeCallbackTwice)
            {
                NativeCallbackCount++;
                _ = await callback(pending, cancellationToken);
            }
            ArgumentNullException.ThrowIfNull(callbackResult);
            callbackResult = callbackResultMutation?.Invoke(callbackResult) ?? callbackResult;
            callbackResult.Validate();
            if (callbackResult.IsSubmitted)
            {
                var acknowledged = CreateSubmissionAcknowledged(
                    callbackResult.Submission!.Acknowledgement,
                    CancellationToken.None);
                var submittedResult = await ReturnAfterTerminalAsync(
                    new GuardedNativeSubmissionResult(pending, callbackResult, acknowledged, null, false));
                return guardedResultMutation?.Invoke(submittedResult) ?? submittedResult;
            }

            if (callbackResult.IsPendingRetained)
            {
                var retainedResult = new GuardedNativeSubmissionResult(pending, callbackResult, null, null, true);
                return guardedResultMutation?.Invoke(retainedResult) ?? retainedResult;
            }
            throw new InvalidDataException("FAKE: a first native callback cannot create UNKNOWN_SUBMISSION or return WAITING_EXTERNAL.");
        }

        private async ValueTask<GuardedNativeSubmissionResult> ReturnAfterTerminalAsync(
            GuardedNativeSubmissionResult result)
        {
            if (!blockAfterTerminal) return result;
            _terminalReturnEntered.TrySetResult(true);
            await _terminalReturnRelease.Task;
            return result;
        }

        public NativeSubmissionGuardRetention RetainGuardUntilProcessExit(
            INativeSubmissionAttempt nativeAttempt,
            Task<NativeSubmission>? outstandingSubmission,
            NativeStopRequest expectedStop)
        {
            ArgumentNullException.ThrowIfNull(nativeAttempt);
            ArgumentNullException.ThrowIfNull(expectedStop);
            expectedStop.Validate();
            if (retainThrows)
                throw new InvalidOperationException("FAKE: process guardian registration failed.");
            if (!_guardHeld || _retentionId is not null)
                throw new InvalidOperationException("FAKE: session guard cannot be retained twice or after release.");
            var retentionId = Guid.NewGuid();
            if (!ProcessGuardian.TryAdd(
                    retentionId,
                    [this, nativeAttempt, outstandingSubmission ?? Task.CompletedTask]))
                throw new InvalidOperationException("FAKE: process guardian registration collided.");
            _retentionId = retentionId;
            var retention = new NativeSubmissionGuardRetention(
                retentionId,
                expectedStop.SubmissionAttemptId,
                expectedStop.NativeRequestBindingSha256,
                expectedStop.SubmittedRequestSha256,
                expectedStop.WorkerInstanceId,
                expectedStop.WorkerGeneration,
                "test-process-guardian",
                true);
            return retentionMutation?.Invoke(retention) ?? retention;
        }

        public void SimulateProcessDeath()
        {
            if (_retentionId is Guid retentionId)
                ProcessGuardian.TryRemove(retentionId, out _);
            _retentionId = null;
            if (_guardHeld)
            {
                _guardHeld = false;
                _crossCommitGuard.Release();
            }
        }

        public static void SimulateGuardedProcessDeath(Guid retentionId)
        {
            if (!ProcessGuardian.TryGetValue(retentionId, out var roots) ||
                roots[0] is not FakeApprovalExecutionFenceLease lease)
                throw new InvalidOperationException("FAKE: process-rooted guardian registration is missing.");
            lease.SimulateProcessDeath();
        }

        public async Task AttemptPolicyRevocationAsync(CancellationToken cancellationToken = default)
        {
            _revocationEntered.TrySetResult(true);
            await _crossCommitGuard.WaitAsync(cancellationToken);
            try { RevocationObservedTerminalState = Acknowledged || Quarantined; }
            finally { _crossCommitGuard.Release(); }
        }

        private VerifiedSubmissionPendingAuthorization CreateSubmissionPending(
            CancellationToken cancellationToken = default,
            bool markInserted = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingBegun = markInserted;
            var intent = new ApprovalSubmissionIntentV1(
                ApprovalSubmissionIntentV1.CurrentSchemaVersion,
                ApprovalSubmissionIntentV1.CurrentContractId,
                ApprovalSubmissionIntentV1.CurrentProducerModule,
                ApprovalSubmissionIntentV1.CurrentAuthScope,
                Guid.Parse("7b000000-0000-0000-0000-00000000000b"),
                FenceRequestSha256,
                command.ApprovalId,
                Request.ProposalId,
                command.CommandId,
                command.LeaseId,
                command.Attempt,
                command.SoulId,
                command.DeviceBindingId,
                command.PlatformAccountId,
                command.TraceId,
                command.IdempotencyKey,
                command.ApprovalSha256,
                new string('8', 64),
                Fence.StatusRevision,
                Fence.RuntimeRevision,
                Fence.RuntimeStateSha256,
                authorization.ReleaseBomSha256,
                authorization.ActiveReleaseBomGeneration,
                ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization),
                NativeRequestBindingSha256,
                Now,
                Fence.ValidUntil,
                "internal",
                LifecycleSignature);
            var intentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(intent);
            var unsignedState = new ApprovalSubmissionStateV1(
                ApprovalSubmissionStateV1.CurrentSchemaVersion,
                ApprovalSubmissionStateV1.CurrentContractId,
                ApprovalSubmissionStateV1.CurrentProducerModule,
                Guid.Parse("7c000000-0000-0000-0000-00000000000c"),
                intent.SubmissionAttemptId,
                intent.ApprovalId,
                intent.ProposalId,
                intent.CommandId,
                intent.LeaseId,
                intent.Attempt,
                intent.SoulId,
                intent.DeviceBindingId,
                intent.PlatformAccountId,
                intent.TraceId,
                intent.IdempotencyKey,
                intent.ReleaseBomSha256,
                intent.ReleaseBomGeneration,
                intent.NativeRequestBindingSha256,
                intentSha256,
                ApprovalSubmissionStateV1.SubmissionPending,
                null,
                intentSha256,
                Now,
                "internal",
                new string('0', 64),
                LifecycleSignature);
            var pendingState = unsignedState with
            {
                StateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(unsignedState)
            };
            _pending = pendingMutation?.Invoke(new VerifiedSubmissionPendingAuthorization(intent, pendingState))
                ?? new VerifiedSubmissionPendingAuthorization(intent, pendingState);
            return _pending;
        }

        private VerifiedSubmissionAcknowledgedAuthorization CreateSubmissionAcknowledged(
            NativeSubmissionAck acknowledgement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending is null || acknowledgement.SubmissionAttemptId != _pending.Intent.SubmissionAttemptId)
                throw new InvalidOperationException("FAKE: acknowledgement is not bound to the pending submission.");

            var pendingStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(_pending.PendingState);
            var ownerAcknowledgement = new ApprovalSubmissionAcknowledgementV1(
                ApprovalSubmissionAcknowledgementV1.CurrentSchemaVersion,
                ApprovalSubmissionAcknowledgementV1.CurrentContractId,
                ApprovalSubmissionAcknowledgementV1.CurrentProducerModule,
                ApprovalSubmissionAcknowledgementV1.CurrentAuthScope,
                Guid.Parse("7d000000-0000-0000-0000-00000000000d"),
                _pending.Intent.SubmissionAttemptId,
                _pending.Intent.ApprovalId,
                _pending.Intent.ProposalId,
                acknowledgement.CommandId,
                acknowledgement.LeaseId,
                acknowledgement.Attempt,
                acknowledgement.SoulId,
                acknowledgement.DeviceBindingId,
                acknowledgement.PlatformAccountId,
                acknowledgement.TraceId,
                acknowledgement.IdempotencyKey,
                acknowledgement.ActiveReleaseBomSha256,
                acknowledgement.ActiveReleaseBomGeneration,
                NativeRequestBindingSha256,
                ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(_pending.Intent),
                pendingStateSha256,
                acknowledgement.SubmittedRequestSha256,
                acknowledgement.SubmissionId,
                acknowledgement.CompletionHandleId,
                acknowledgement.AcknowledgementSha256,
                Now,
                Fence.ValidUntil,
                "internal",
                LifecycleSignature);
            var ownerAcknowledgementSha256 = ApprovalSubmissionLifecycleBinding.ComputeAcknowledgementSha256(ownerAcknowledgement);
            var unsignedState = new ApprovalSubmissionStateV1(
                ApprovalSubmissionStateV1.CurrentSchemaVersion,
                ApprovalSubmissionStateV1.CurrentContractId,
                ApprovalSubmissionStateV1.CurrentProducerModule,
                Guid.Parse("7e000000-0000-0000-0000-00000000000e"),
                _pending.Intent.SubmissionAttemptId,
                _pending.Intent.ApprovalId,
                _pending.Intent.ProposalId,
                acknowledgement.CommandId,
                acknowledgement.LeaseId,
                acknowledgement.Attempt,
                acknowledgement.SoulId,
                acknowledgement.DeviceBindingId,
                acknowledgement.PlatformAccountId,
                acknowledgement.TraceId,
                acknowledgement.IdempotencyKey,
                acknowledgement.ActiveReleaseBomSha256,
                acknowledgement.ActiveReleaseBomGeneration,
                NativeRequestBindingSha256,
                ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(_pending.Intent),
                ApprovalSubmissionStateV1.SubmissionAcknowledged,
                pendingStateSha256,
                ownerAcknowledgementSha256,
                Now,
                "internal",
                new string('0', 64),
                LifecycleSignature);
            var acknowledgedState = unsignedState with
            {
                StateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(unsignedState)
            };
            Acknowledged = true;
            var verified = new VerifiedSubmissionAcknowledgedAuthorization(
                ownerAcknowledgement,
                acknowledgedState);
            return acknowledgedMutation?.Invoke(verified) ?? verified;
        }

        private VerifiedSubmissionUnknownAuthorization CreateSubmissionUnknown(string stopProofEvidenceSha256)
        {
            if (_pending is null) throw new InvalidOperationException("FAKE: no pending submission exists to quarantine.");
            var intentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(_pending.Intent);
            var pendingStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(_pending.PendingState);
            NativeContractGuard.RequireSha256(stopProofEvidenceSha256, nameof(stopProofEvidenceSha256));
            var unsignedState = new ApprovalSubmissionStateV1(
                ApprovalSubmissionStateV1.CurrentSchemaVersion,
                ApprovalSubmissionStateV1.CurrentContractId,
                ApprovalSubmissionStateV1.CurrentProducerModule,
                Guid.Parse("7f000000-0000-0000-0000-00000000000f"),
                _pending.Intent.SubmissionAttemptId,
                _pending.Intent.ApprovalId,
                _pending.Intent.ProposalId,
                _pending.Intent.CommandId,
                _pending.Intent.LeaseId,
                _pending.Intent.Attempt,
                _pending.Intent.SoulId,
                _pending.Intent.DeviceBindingId,
                _pending.Intent.PlatformAccountId,
                _pending.Intent.TraceId,
                _pending.Intent.IdempotencyKey,
                _pending.Intent.ReleaseBomSha256,
                _pending.Intent.ReleaseBomGeneration,
                NativeRequestBindingSha256,
                intentSha256,
                ApprovalSubmissionStateV1.UnknownSubmission,
                pendingStateSha256,
                stopProofEvidenceSha256,
                Now,
                "internal",
                new string('0', 64),
                LifecycleSignature);
            var unknownState = unsignedState with
            {
                StateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(unsignedState)
            };
            return new VerifiedSubmissionUnknownAuthorization(unknownState);
        }

        public ValueTask DisposeAsync()
        {
            if (_retentionId is not null)
                return ValueTask.FromException(new InvalidOperationException("FAKE: process-rooted retained guard has no ordinary release path."));
            Disposed = true;
            if (_guardHeld)
            {
                _guardHeld = false;
                _crossCommitGuard.Release();
            }
            return disposalFails
                ? ValueTask.FromException(new InvalidOperationException("FAKE: approval fence release failed."))
                : ValueTask.CompletedTask;
        }
    }

    private abstract class TestNativeExecutor : INativeCommandExecutor
    {
        public int SubmissionCallCount { get; private set; }
        public int AbortCallCount { get; private set; }
        public virtual string WorkerInstanceId => "wi_0123456789abcdef0123456789abcdef";
        public virtual long WorkerGeneration => 1;
        public INativeSubmissionAttempt CreateInertSubmissionAttempt() => new TestNativeSubmissionAttempt(this);
        public abstract Task<NativeSubmission> SubmitCoreAsync(
            NativeExecutionRequestV1 request,
            CancellationToken cancellationToken);
        public virtual ValueTask<NativeAbortConfirmation> AbortCoreAsync(
            NativeStopRequest expectedStop,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(StopConfirmation(expectedStop));

        private sealed class TestNativeSubmissionAttempt(TestNativeExecutor owner) : INativeSubmissionAttempt
        {
            private int _submitted;
            public string WorkerInstanceId => owner.WorkerInstanceId;
            public long WorkerGeneration => owner.WorkerGeneration;
            public Task<NativeSubmission> SubmitFirstByteAsync(
                NativeExecutionRequestV1 request,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref _submitted, 1) != 0)
                    throw new InvalidOperationException("FAKE: first-byte submission capability is single use.");
                owner.SubmissionCallCount++;
                return owner.SubmitCoreAsync(request, cancellationToken);
            }

            public ValueTask<NativeAbortConfirmation> AbortAndConfirmStoppedAsync(
                NativeStopRequest expectedStop,
                CancellationToken cancellationToken)
            {
                owner.AbortCallCount++;
                return owner.AbortCoreAsync(expectedStop, cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeNative(
        string status,
        Func<NativeExecutionResponse, NativeExecutionResponse>? mutate = null,
        Action? onDispatch = null,
        Func<NativeSubmissionAck, NativeSubmissionAck>? mutateAcknowledgement = null) : TestNativeExecutor
    {
        public int CallCount { get; private set; }
        public NativeExecutionRequestV1? LastRequest { get; private set; }
        public override Task<NativeSubmission> SubmitCoreAsync(NativeExecutionRequestV1 request, CancellationToken cancellationToken)
        {
            onDispatch?.Invoke();
            CallCount++;
            LastRequest = request;
            var response = ResponseFor(request, status);
            return Task.FromResult(SubmissionFor(
                request,
                mutate is null ? response : mutate(response),
                mutateAcknowledgement));
        }
    }

    private sealed class FakePostcondition(bool verified) : IBusinessPostconditionVerifier
    {
        public int CallCount { get; private set; }
        public Task<PostconditionVerification> VerifyAsync(CommandDispatchV1 command, NativeResultV1 nativeResult, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new PostconditionVerification(verified, new string('c', 64), verified ? "MATCH" : "MISMATCH"));
        }
    }

    private sealed class BlockingPostcondition : IBusinessPostconditionVerifier
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public NativeResultV1? NativeResult { get; private set; }
        public void Release() => _release.TrySetResult(true);
        public async Task<PostconditionVerification> VerifyAsync(CommandDispatchV1 command, NativeResultV1 nativeResult, CancellationToken cancellationToken)
        {
            NativeResult = nativeResult;
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return new PostconditionVerification(true, new string('c', 64), "MATCH");
        }
    }

    private sealed class NonCooperativePostcondition : IBusinessPostconditionVerifier
    {
        private readonly TaskCompletionSource<PostconditionVerification> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PostconditionVerification> VerifyAsync(CommandDispatchV1 command, NativeResultV1 nativeResult, CancellationToken cancellationToken) => _completion.Task;
        public void CompleteSuccess() => _completion.TrySetResult(new PostconditionVerification(true, new string('c', 64), "LATE_MATCH"));
    }

    private sealed class TimeoutNative : TestNativeExecutor
    {
        public override async Task<NativeSubmission> SubmitCoreAsync(NativeExecutionRequestV1 request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class NonCooperativeNative : TestNativeExecutor
    {
        private readonly TaskCompletionSource<NativeSubmission> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<NativeSubmission> SubmitCoreAsync(NativeExecutionRequestV1 request, CancellationToken cancellationToken) => _never.Task;
    }

    private sealed class AbortUnconfirmedNative : TestNativeExecutor
    {
        private readonly TaskCompletionSource<NativeSubmission> _submission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private NativeExecutionRequestV1? _request;
        public bool LateWriteOccurred { get; private set; }

        public override Task<NativeSubmission> SubmitCoreAsync(
            NativeExecutionRequestV1 request,
            CancellationToken cancellationToken)
        {
            _request = request;
            return _submission.Task;
        }

        public override ValueTask<NativeAbortConfirmation> AbortCoreAsync(
            NativeStopRequest expectedStop,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<NativeAbortConfirmation>(
                new IOException("FAKE: transport ignored abort; stop cannot be confirmed."));

        public void CompleteLateSubmission()
        {
            var request = _request ?? throw new InvalidOperationException("FAKE: submission was not started.");
            LateWriteOccurred = true;
            _submission.TrySetResult(SubmissionFor(
                request,
                ResponseFor(request, NativeStepResultV1.Success)));
        }
    }

    private sealed class ForgedStopProofNative(string attack) : TestNativeExecutor
    {
        private readonly TaskCompletionSource<NativeSubmission> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<NativeSubmission> SubmitCoreAsync(
            NativeExecutionRequestV1 request,
            CancellationToken cancellationToken) => _never.Task;

        public override ValueTask<NativeAbortConfirmation> AbortCoreAsync(
            NativeStopRequest expectedStop,
            CancellationToken cancellationToken)
        {
            var proof = attack switch
            {
                "attempt" => StopConfirmation(expectedStop with { SubmissionAttemptId = Guid.NewGuid() }),
                "worker" => StopConfirmation(
                    expectedStop,
                    workerInstanceId: "wi_ffffffffffffffffffffffffffffffff"),
                "bom" => StopConfirmation(expectedStop with
                {
                    ActiveReleaseBomGeneration = expectedStop.ActiveReleaseBomGeneration + 1
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(attack))
            };
            return ValueTask.FromResult(proof);
        }
    }

    private sealed class ThrowingCancellationNative : TestNativeExecutor
    {
        private readonly TaskCompletionSource<NativeSubmission> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<NativeSubmission> SubmitCoreAsync(NativeExecutionRequestV1 request, CancellationToken cancellationToken)
        {
            cancellationToken.Register(static () => throw new InvalidOperationException("hostile cancellation callback"));
            return _never.Task;
        }
    }

    private sealed class MutableStepNative : TestNativeExecutor
    {
        public List<NativeStepResultV1> StepResults { get; } = [];
        public override Task<NativeSubmission> SubmitCoreAsync(NativeExecutionRequestV1 request, CancellationToken cancellationToken)
        {
            StepResults.Add(new NativeStepResultV1(request.StepId, request.StepKind, NativeStepResultV1.Success, "FAKE", new string('b', 64)));
            var response = new NativeExecutionResponse(
                NativeResultV1.CurrentSchemaVersion, NativeResultV1.CurrentContractId, NativeResultV1.CurrentProducerModule,
                Guid.Parse("76000000-0000-0000-0000-000000000006"), request.CommandId, request.LeaseId, request.Attempt,
                request.SoulId, request.DeviceBindingId, request.PlatformAccountId, request.TraceId, request.IdempotencyKey, Now,
                request.ActiveReleaseBomSha256, request.ActiveReleaseBomGeneration, request.ActiveReleaseBomTokenSha256, StepResults);
            return Task.FromResult(SubmissionFor(request, response));
        }
    }

    private sealed class FakeCompletion(Guid completionHandleId, Task<NativeExecutionResponse> result) : INativeSubmissionCompletion
    {
        public Guid CompletionHandleId { get; } = completionHandleId;
        public Task<NativeExecutionResponse> WaitForResultAsync(CancellationToken cancellationToken) => result;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSubmissionNative : TestNativeExecutor
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SubmissionEntered => _entered.Task;
        public int CallCount { get; private set; }
        public void ReleaseDurableAcknowledgement() => _release.TrySetResult(true);
        public override async Task<NativeSubmission> SubmitCoreAsync(NativeExecutionRequestV1 request, CancellationToken cancellationToken)
        {
            CallCount++;
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return SubmissionFor(request, ResponseFor(request, NativeStepResultV1.Success));
        }
    }

    private sealed class ColdSubmissionNative : TestNativeExecutor
    {
        public bool ColdDelegateRan { get; private set; }
        public override Task<NativeSubmission> SubmitCoreAsync(NativeExecutionRequestV1 request, CancellationToken cancellationToken) =>
            new(() =>
            {
                ColdDelegateRan = true;
                return SubmissionFor(request, ResponseFor(request, NativeStepResultV1.Success));
            }, CancellationToken.None);
    }

    private enum NativeSubmitFailureMode
    {
        SynchronousThrow,
        NullTask,
        FaultedTask
    }

    private sealed class FailingSubmissionNative(NativeSubmitFailureMode failureMode) : TestNativeExecutor
    {
        public int CallCount { get; private set; }

        public override Task<NativeSubmission> SubmitCoreAsync(
            NativeExecutionRequestV1 request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return failureMode switch
            {
                NativeSubmitFailureMode.SynchronousThrow => throw new IOException("FAKE: synchronous native boundary failure."),
                NativeSubmitFailureMode.NullTask => null!,
                NativeSubmitFailureMode.FaultedTask => Task.FromException<NativeSubmission>(
                    new IOException("FAKE: asynchronous native boundary failure.")),
                _ => throw new ArgumentOutOfRangeException(nameof(failureMode))
            };
        }
    }

    private static NativeExecutionResponse ResponseFor(NativeExecutionRequestV1 request, string status) => new(
        NativeResultV1.CurrentSchemaVersion, NativeResultV1.CurrentContractId, NativeResultV1.CurrentProducerModule,
        Guid.Parse("76000000-0000-0000-0000-000000000006"), request.CommandId, request.LeaseId, request.Attempt,
        request.SoulId, request.DeviceBindingId, request.PlatformAccountId, request.TraceId, request.IdempotencyKey, Now,
        request.ActiveReleaseBomSha256, request.ActiveReleaseBomGeneration, request.ActiveReleaseBomTokenSha256,
        [new NativeStepResultV1(request.StepId, request.StepKind, status, "FAKE", new string('b', 64))]);

    private static NativeAbortConfirmation StopConfirmation(
        NativeStopRequest expected,
        string resultCode = NativeAbortConfirmation.TransportAborted,
        string workerInstanceId = "wi_0123456789abcdef0123456789abcdef",
        long workerGeneration = 1)
    {
        var unsigned = new NativeAbortConfirmation(
            NativeAbortConfirmation.CurrentSchemaVersion,
            NativeAbortConfirmation.CurrentContractId,
            NativeAbortConfirmation.CurrentProducerModule,
            true,
            expected.SubmissionAttemptId,
            expected.CommandId,
            expected.LeaseId,
            expected.Attempt,
            expected.NativeRequestBindingSha256,
            expected.SubmittedRequestSha256,
            expected.SoulId,
            expected.DeviceBindingId,
            expected.PlatformAccountId,
            expected.TraceId,
            expected.IdempotencyKey,
            expected.ActiveReleaseBomSha256,
            expected.ActiveReleaseBomGeneration,
            expected.ActiveReleaseBomTokenSha256,
            workerInstanceId,
            workerGeneration,
            resultCode,
            new string('0', 64),
            Now,
            NativeAbortConfirmation.CurrentPrivacyClass,
            NativeAbortConfirmation.CurrentAuthScope,
            StopProofKeyId,
            Convert.ToBase64String(new byte[NativeAbortConfirmation.P1363SignatureSizeBytes]));
        var withEvidence = unsigned with
        {
            EvidenceSha256 = NativeStopProofProtocolV1.ComputeEvidenceSha256(unsigned)
        };
        var payload = NativeStopProofProtocolV1.CanonicalSigningBytes(withEvidence);
        byte[] signature;
        try
        {
            lock (StopProofSigner)
                signature = StopProofSigner.SignData(
                    payload,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
        try { return withEvidence with { SignatureBase64 = Convert.ToBase64String(signature) }; }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    private static NativeSubmissionAck ContractAcknowledgement()
    {
        var unsigned = new NativeSubmissionAck(
            NativeSubmissionAck.CurrentSchemaVersion,
            NativeSubmissionAck.CurrentContractId,
            NativeSubmissionAck.CurrentProducerModule,
            Guid.Parse("78000000-0000-0000-0000-000000000008"),
            Guid.Parse("77000000-0000-0000-0000-000000000007"),
            Guid.Parse("71000000-0000-0000-0000-000000000001"),
            Guid.Parse("72000000-0000-0000-0000-000000000002"),
            1,
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            Now,
            NativeSubmissionAck.CurrentPrivacyClass,
            NativeSubmissionAck.DurableFlush,
            new string('1', 64),
            new string('2', 64),
            Guid.Parse("7b000000-0000-0000-0000-00000000000b"),
            new string('3', 64),
            new string('4', 64),
            StableBom,
            7,
            new string('5', 64),
            new string('6', 64),
            new string('0', 64));
        return unsigned with
        {
            AcknowledgementSha256 = NativeSubmissionProtocolV1.ComputeAcknowledgementSha256(unsigned)
        };
    }

    private static NativeSubmission SubmissionFor(
        NativeExecutionRequestV1 request,
        NativeExecutionResponse response,
        Func<NativeSubmissionAck, NativeSubmissionAck>? mutateAcknowledgement = null)
    {
        var completionHandleId = Guid.Parse("77000000-0000-0000-0000-000000000007");
        var unsigned = new NativeSubmissionAck(
            NativeSubmissionAck.CurrentSchemaVersion,
            NativeSubmissionAck.CurrentContractId,
            NativeSubmissionAck.CurrentProducerModule,
            Guid.Parse("78000000-0000-0000-0000-000000000008"),
            completionHandleId,
            request.CommandId,
            request.LeaseId,
            request.Attempt,
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            Now,
            NativeSubmissionAck.CurrentPrivacyClass,
            NativeSubmissionAck.DurableFlush,
            request.CommandSha256,
            request.AuthorizationSha256,
            request.SubmissionAttemptId,
            request.SubmissionIntentSha256,
            request.PendingStateSha256,
            request.ActiveReleaseBomSha256,
            request.ActiveReleaseBomGeneration,
            request.ActiveReleaseBomTokenSha256,
            NativeSubmissionProtocolV1.ComputeSubmittedRequestSha256(request),
            new string('0', 64));
        var acknowledgement = unsigned with
        {
            AcknowledgementSha256 = NativeSubmissionProtocolV1.ComputeAcknowledgementSha256(unsigned)
        };
        acknowledgement = mutateAcknowledgement?.Invoke(acknowledgement) ?? acknowledgement;
        return new NativeSubmission(acknowledgement, new FakeCompletion(completionHandleId, Task.FromResult(response)));
    }

    private sealed class FixedClock(DateTimeOffset now) : ITrustedClock { public DateTimeOffset GetUtcNow() => now; }

    private sealed class SequenceClock(params DateTimeOffset[] values) : ITrustedClock
    {
        private readonly Queue<DateTimeOffset> _values = new(values);
        private DateTimeOffset _last = values.Last();
        public DateTimeOffset GetUtcNow()
        {
            if (_values.Count > 0) _last = _values.Dequeue();
            return _last;
        }
    }

    private static byte[] CreateReceiptSigningKey()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return signer.ExportPkcs8PrivateKey();
    }
}
