using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dps.CommandOrchestrator.Contracts;
using Dps.ExecutorGateway.Contracts;
using Dps.PolicyApproval.Contracts;
using Xunit;

namespace Dps.ExecutorGateway.Tests;

public sealed class NativeFixtureProcessIntegrationTests
{
    private const string EvidenceKind = "REAL_LOCAL_PROCESS";
    private const string RequestSchema = "dps.native-fixture.request/v1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Device = "db_0123456789abcdef0123456789abcdef";
    private const string Account = "pa_fedcba9876543210fedcba9876543210";
    private const string Trace = "trace_00112233445566778899aabbccddeeff";
    private const string Idempotency = "idem_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly string StableBom = new('a', 64);
    private static readonly string StableToken = Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray());
    private static readonly EcdsaCommandReceiptSigner ReceiptSigner = new(CreateReceiptSigningKey());
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealLocalProcessSuccessBindsFullGenerationTokenRequestAndResult()
    {
        await using var scenario = new FixtureScenario();
        await using var process = await scenario.StartAsync();
        var command = Command();
        var authorization = Authorization(command);
        var fence = new FixtureApprovalFenceProvider(process, scenario.SubmissionAuthorityFilePath);

        var receipt = await Gateway(process, authorization, fence).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);

        receipt.Validate();
        Assert.Equal(CommandReceiptV1.Success, receipt.Outcome);
        Assert.NotNull(process.LastNativeRequest);
        Assert.Equal(command.CommandId, process.LastNativeRequest.CommandId);
        Assert.Equal(command.LeaseId, process.LastNativeRequest.LeaseId);
        Assert.Equal(command.Attempt, process.LastNativeRequest.Attempt);
        Assert.Equal(command.SoulId, process.LastNativeRequest.SoulId);
        Assert.Equal(command.DeviceBindingId, process.LastNativeRequest.DeviceBindingId);
        Assert.Equal(command.PlatformAccountId, process.LastNativeRequest.PlatformAccountId);
        Assert.Equal(StableBom, process.LastNativeRequest.ActiveReleaseBomSha256);
        Assert.Equal(7, process.LastNativeRequest.ActiveReleaseBomGeneration);
        Assert.Equal(StableToken, process.LastNativeRequest.ActiveReleaseBomExecutionTokenBase64);
        Assert.Equal(Authorization(command).ActiveReleaseBomTokenSha256, process.LastNativeRequest.ActiveReleaseBomTokenSha256);
        Assert.NotNull(process.LastSubmissionAck);
        Assert.Equal(NativeSubmissionAck.DurableFlush, process.LastSubmissionAck.Durability);
        Assert.Equal(
            NativeSubmissionProtocolV1.ComputeSubmittedRequestSha256(process.LastNativeRequest),
            process.LastSubmissionAck.SubmittedRequestSha256);
        Assert.NotNull(fence.LastLease);
        Assert.True(fence.LastLease.PendingBegun);
        Assert.True(fence.LastLease.Acknowledged);
        Assert.True(fence.LastLease.ReleasedAfterDurableAcknowledgement);
        var state = await process.ReadStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceKind, state.EvidenceKind);
        Assert.Equal(1, state.SideEffectCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessCrashAfterSubmissionFlushLeavesPendingAndRestartBlocksSameAttemptWithoutSecondSideEffect()
    {
        await using var scenario = new FixtureScenario();
        var command = Command();
        var authorization = Authorization(command);
        await using (var first = await scenario.StartAsync())
        {
            await first.SetModeAsync("crash_after_flush", TestContext.Current.CancellationToken);
            var fence = new FixtureApprovalFenceProvider(first, scenario.SubmissionAuthorityFilePath);
            var unknown = await Gateway(first, authorization, fence).ExecuteAsync(
                command, authorization, TestContext.Current.CancellationToken);
            Assert.Equal(CommandReceiptV1.UnknownOutcome, unknown.Outcome);
            Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, unknown.ResultCode);
            Assert.False(unknown.RetryAllowed);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.False(fence.LastLease.Acknowledged);
            Assert.True(fence.LastLease.GuardRetainedUntilProcessExit);
            Assert.False(fence.LastLease.Disposed);
            fence.LastLease.SimulateExecutorProcessDeath();
        }

        await using var recovered = await scenario.StartAsync();
        var restartedFence = new FixtureApprovalFenceProvider(recovered, scenario.SubmissionAuthorityFilePath);
        var restarted = await Gateway(recovered, authorization, restartedFence).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);
        Assert.Equal(CommandReceiptV1.UnknownOutcome, restarted.Outcome);
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, restarted.ResultCode);
        Assert.False(restarted.RetryAllowed);
        Assert.Equal(0, recovered.SubmissionCallCount);
        Assert.NotNull(restartedFence.LastLease);
        Assert.True(restartedFence.LastLease.Disposed);
        var blockedState = await recovered.ReadStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, blockedState.SideEffectCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessCrashBeforeSubmissionFlushLeavesPendingAndRestartBlocksSameAttemptWithoutSideEffect()
    {
        await using var scenario = new FixtureScenario();
        var command = Command();
        var authorization = Authorization(command);
        await using (var first = await scenario.StartAsync())
        {
            await first.SetModeAsync("crash_before_flush", TestContext.Current.CancellationToken);
            var fence = new FixtureApprovalFenceProvider(first, scenario.SubmissionAuthorityFilePath);
            var unknown = await Gateway(first, authorization, fence).ExecuteAsync(
                command, authorization, TestContext.Current.CancellationToken);
            Assert.Equal(CommandReceiptV1.UnknownOutcome, unknown.Outcome);
            Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, unknown.ResultCode);
            Assert.False(unknown.RetryAllowed);
            Assert.NotNull(fence.LastLease);
            Assert.True(fence.LastLease.PendingBegun);
            Assert.False(fence.LastLease.Acknowledged);
            Assert.True(fence.LastLease.GuardRetainedUntilProcessExit);
            Assert.False(fence.LastLease.Disposed);
            fence.LastLease.SimulateExecutorProcessDeath();
        }

        await using var recovered = await scenario.StartAsync();
        var restartedFence = new FixtureApprovalFenceProvider(recovered, scenario.SubmissionAuthorityFilePath);
        var restarted = await Gateway(recovered, authorization, restartedFence).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);
        Assert.Equal(CommandReceiptV1.UnknownOutcome, restarted.Outcome);
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, restarted.ResultCode);
        Assert.False(restarted.RetryAllowed);
        Assert.Equal(0, recovered.SubmissionCallCount);
        Assert.NotNull(restartedFence.LastLease);
        Assert.True(restartedFence.LastLease.Disposed);
        var state = await recovered.ReadStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, state.SideEffectCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OldAttemptReplayFromIndependentProcessFailsClosed()
    {
        await using var scenario = new FixtureScenario();
        await using var process = await scenario.StartAsync();
        await process.SetModeAsync("old_attempt_result", TestContext.Current.CancellationToken);
        var command = Command();
        var authorization = Authorization(command);

        var receipt = await Gateway(process, authorization).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("NATIVE_CONTRACT_OR_SCOPE_INVALID", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CrossScopeResponseFromIndependentProcessFailsClosed()
    {
        await using var scenario = new FixtureScenario();
        await using var process = await scenario.StartAsync();
        await process.SetModeAsync("cross_scope_result", TestContext.Current.CancellationToken);
        var command = Command();
        var authorization = Authorization(command);

        var receipt = await Gateway(process, authorization).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("NATIVE_CONTRACT_OR_SCOPE_INVALID", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BomSwitchAfterSideEffectFromIndependentProcessIsUnknownOutcome()
    {
        await using var scenario = new FixtureScenario();
        await using var process = await scenario.StartAsync();
        await process.SetModeAsync("switch_bom_after_effect", TestContext.Current.CancellationToken);
        var command = Command();
        var authorization = Authorization(command);

        var receipt = await Gateway(process, authorization).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);
        var state = await process.ReadStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CommandReceiptV1.UnknownOutcome, receipt.Outcome);
        Assert.Equal("ACTIVE_BOM_CHANGED_AFTER_NATIVE", receipt.ResultCode);
        Assert.False(receipt.RetryAllowed);
        Assert.Equal(1, state.SideEffectCount);
        Assert.Equal(8, state.ActiveBinding!.Generation);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AcknowledgedAttemptCannotBlindResubmitOrRepeatSideEffect()
    {
        await using var scenario = new FixtureScenario();
        await using var process = await scenario.StartAsync();
        var command = Command();
        var authorization = Authorization(command);
        var firstFence = new FixtureApprovalFenceProvider(process, scenario.SubmissionAuthorityFilePath);

        var first = await Gateway(process, authorization, firstFence).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);
        var restartedFence = new FixtureApprovalFenceProvider(process, scenario.SubmissionAuthorityFilePath);
        var restarted = await Gateway(process, authorization, restartedFence).ExecuteAsync(
            command, authorization, TestContext.Current.CancellationToken);
        var state = await process.ReadStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CommandReceiptV1.Success, first.Outcome);
        Assert.Equal(CommandReceiptV1.UnknownOutcome, restarted.Outcome);
        Assert.Equal(NativeSubmissionCallbackResult.WaitingExternal, restarted.ResultCode);
        Assert.False(restarted.RetryAllowed);
        Assert.Equal(1, process.SubmissionCallCount);
        Assert.Equal(1, state.SideEffectCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RequiredLocalProcessTestIdsArePresent()
    {
        using var stream = typeof(NativeFixtureProcessIntegrationTests).Assembly.GetManifestResourceStream(
            "Dps.ExecutorGateway.Tests.required-local-process-tests.v2.json");
        Assert.NotNull(stream);
        using var inventory = JsonDocument.Parse(stream);
        Assert.Equal("dps.required-tests/v2", inventory.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("executor-gateway.local-process", inventory.RootElement.GetProperty("suiteId").GetString());
        Assert.Equal(EvidenceKind, inventory.RootElement.GetProperty("evidenceKind").GetString());
        var required = inventory.RootElement.GetProperty("requiredTests").EnumerateArray()
            .Select(value => (
                Id: value.GetProperty("id").GetString()!,
                Category: value.GetProperty("category").GetString()!))
            .ToArray();
        Assert.Equal(required.Length, required.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        var actual = typeof(NativeFixtureProcessIntegrationTests).Assembly.GetTypes()
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
            Assert.Equal("Integration", item.Category);
            Assert.True(actual.TryGetValue(item.Id, out var categories), $"Required test '{item.Id}' is missing.");
            Assert.Single(categories!);
            Assert.Equal(item.Category, categories![0]);
        });
    }

    private static VerifiedExecutorGateway Gateway(
        FixtureProcess process,
        ExecutionAuthorizationV1 authorization,
        IApprovalExecutionFenceProvider? fence = null) => new(
        new FixedClock(Now),
        ReceiptSigner,
        new ExactAuthorizationVerifier(authorization),
        process,
        fence ?? new FixtureApprovalFenceProvider(process),
        process,
        new FixtureFailStop(),
        process,
        TimeSpan.FromSeconds(3));

    private sealed class FixtureFailStop : IExecutorProcessFailStop
    {
        public void TerminateProcess(string reasonCode, Exception cause) =>
            throw new InvalidOperationException($"REAL_LOCAL_PROCESS fixture requested fail-stop: {reasonCode}", cause);
    }

    private static CommandDispatchV1 Command() => new(
        CommandDispatchV1.CurrentSchemaVersion,
        CommandDispatchV1.CurrentContractId,
        CommandDispatchV1.CurrentProducerModule,
        Guid.Parse("81000000-0000-0000-0000-000000000001"),
        Guid.Parse("82000000-0000-0000-0000-000000000002"),
        Guid.Parse("83000000-0000-0000-0000-000000000003"),
        new string('d', 64),
        Soul,
        Device,
        Account,
        Trace,
        Idempotency,
        Now.AddSeconds(-2),
        "internal",
        "fixture.tap",
        true,
        "fixture-platform-authorization",
        Guid.Parse("84000000-0000-0000-0000-000000000004"),
        "worker-process",
        Now.AddMinutes(1),
        1,
        [new CommandStepV1(
            Guid.Parse("85000000-0000-0000-0000-000000000005"),
            "fixture.tap",
            new Dictionary<string, string> { ["selector_ref"] = "fixture.button" },
            false,
            "fixture-state-changed")]);

    private static ExecutionAuthorizationV1 Authorization(CommandDispatchV1 command) => new(
        ExecutionAuthorizationV1.CurrentSchemaVersion,
        ExecutionAuthorizationV1.CurrentContractId,
        ExecutionAuthorizationV1.CurrentProducerModule,
        ExecutionAuthorizationV1.CurrentSignatureDomain,
        ExecutionAuthorizationV1.CurrentCanonicalEncoding,
        ExecutionAuthorizationV1.CurrentCommandDigestAlgorithm,
        ExecutionAuthorizationV1.CurrentSignatureAlgorithm,
        ExecutionAuthorizationV1.CurrentSignatureFormat,
        ExecutionAuthorizationV1.CurrentSignatureEncoding,
        ExecutionAuthorizationV1.CurrentCallerModule,
        ExecutionAuthorizationV1.CurrentAuthScope,
        command.CommandId,
        command.LeaseId,
        command.Attempt,
        command.SoulId,
        command.DeviceBindingId,
        command.PlatformAccountId,
        command.TraceId,
        command.IdempotencyKey,
        Now.AddSeconds(-1),
        "internal",
        ExecutionAuthorizationProtocolV1.ComputeCommandSha256(command),
        StableBom,
        7,
        Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(StableToken))),
        Now.AddSeconds(30),
        false,
        Convert.ToBase64String(new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes]));

    private static byte[] CreateReceiptSigningKey()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return signer.ExportPkcs8PrivateKey();
    }

    private sealed class ExactAuthorizationVerifier(ExecutionAuthorizationV1 expected) : IExecutionAuthorizationVerifier
    {
        public ValueTask<VerifiedExecutionAuthorization> VerifyAsync(
            CommandDispatchV1 command,
            ExecutionAuthorizationV1 authorization,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Equals(expected, authorization)) throw new UnauthorizedAccessException("Integration authorization changed.");
            return ValueTask.FromResult(new VerifiedExecutionAuthorization(expected));
        }
    }

    private sealed class FixtureApprovalFenceProvider(
        FixtureProcess process,
        string? submissionAuthorityFilePath = null) : IApprovalExecutionFenceProvider
    {
        private readonly DurableSubmissionAuthorityFixtureStore? _authorityStore = submissionAuthorityFilePath is null
            ? null
            : new DurableSubmissionAuthorityFixtureStore(submissionAuthorityFilePath);
        public FixtureApprovalFenceLease? LastLease { get; private set; }

        public async Task<IApprovalExecutionFenceLease> AcquireAsync(
            CommandDispatchV1 command,
            ExecutionAuthorizationV1 authorization,
            string nativeRequestBindingSha256,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeContractGuard.RequireSha256(nativeRequestBindingSha256, nameof(nativeRequestBindingSha256));
            var existingRecorded = _authorityStore is not null &&
                await _authorityStore.IsBlockedAsync(nativeRequestBindingSha256, cancellationToken);
            var request = new ApprovalExecutionFenceRequestV1(
                ApprovalExecutionFenceRequestV1.CurrentSchemaVersion,
                ApprovalExecutionFenceRequestV1.CurrentContractId,
                ApprovalExecutionFenceRequestV1.CurrentConsumerModule,
                command.ApprovalId,
                Guid.Parse("86000000-0000-0000-0000-000000000006"),
                command.SoulId,
                command.DeviceBindingId,
                command.PlatformAccountId,
                command.TraceId,
                command.IdempotencyKey,
                command.ApprovalSha256,
                1,
                1,
                new string('e', 64),
                authorization.ReleaseBomSha256);
            var fence = new ApprovalExecutionFenceV1(
                ApprovalExecutionFenceV1.CurrentSchemaVersion,
                ApprovalExecutionFenceV1.CurrentContractId,
                ApprovalExecutionFenceV1.CurrentProducerModule,
                Guid.NewGuid(),
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
                Now.AddMilliseconds(1900),
                "internal");
            LastLease = new FixtureApprovalFenceLease(
                command, authorization, request, fence, nativeRequestBindingSha256, process, _authorityStore,
                existingRecorded);
            return LastLease;
        }
    }

    private sealed class FixtureApprovalFenceLease(
        CommandDispatchV1 command,
        ExecutionAuthorizationV1 authorization,
        ApprovalExecutionFenceRequestV1 request,
        ApprovalExecutionFenceV1 fence,
        string nativeRequestBindingSha256,
        FixtureProcess process,
        DurableSubmissionAuthorityFixtureStore? authorityStore,
        bool existingRecorded) : IApprovalExecutionFenceLease
    {
        private static readonly string LifecycleSignature = Convert.ToBase64String(new byte[64]);
        private static readonly ConcurrentDictionary<Guid, object[]> ProcessGuardian = new();
        private VerifiedSubmissionPendingAuthorization? _pending;
        private Guid? _retentionId;
        public ApprovalExecutionFenceRequestV1 Request { get; } = request;
        public ApprovalExecutionFenceV1 Fence { get; } = fence;
        public string FenceRequestSha256 { get; } = new string('9', 64);
        public string NativeRequestBindingSha256 { get; } = nativeRequestBindingSha256;
        public bool Disposed { get; private set; }
        public bool PendingBegun { get; private set; }
        public bool Acknowledged { get; private set; }
        public bool GuardRetainedUntilProcessExit => _retentionId is not null;
        public Task<ApprovalExecutionFenceV1> RevalidateForNativeDispatchAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PendingBegun)
                throw new UnauthorizedAccessException("FIXTURE: policy transaction is closed after durable PENDING.");
            return Task.FromResult(Fence);
        }

        public async ValueTask<GuardedNativeSubmissionResult> ExecuteFirstNativeSubmissionAsync(
            Func<VerifiedSubmissionPendingAuthorization, CancellationToken, Task<NativeSubmissionCallbackResult>> callback,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var pending = await CreateSubmissionPendingAsync(!existingRecorded, cancellationToken);
            if (existingRecorded)
                return new GuardedNativeSubmissionResult(
                    pending,
                    NativeSubmissionCallbackResult.WaitForExternalReconciliation(),
                    null,
                    null,
                    false);
            var callbackResult = await callback(pending, cancellationToken);
            ArgumentNullException.ThrowIfNull(callbackResult);
            callbackResult.Validate();
            if (callbackResult.IsSubmitted)
            {
                var acknowledged = await CreateSubmissionAcknowledgedAsync(
                    callbackResult.Submission!.Acknowledgement,
                    CancellationToken.None);
                return new GuardedNativeSubmissionResult(pending, callbackResult, acknowledged, null, false);
            }

            if (callbackResult.IsPendingRetained)
                return new GuardedNativeSubmissionResult(pending, callbackResult, null, null, true);
            throw new InvalidDataException(
                "FIXTURE: a first native callback cannot create UNKNOWN_SUBMISSION or return WAITING_EXTERNAL.");
        }

        public NativeSubmissionGuardRetention RetainGuardUntilProcessExit(
            INativeSubmissionAttempt nativeAttempt,
            Task<NativeSubmission>? outstandingSubmission,
            NativeStopRequest expectedStop)
        {
            ArgumentNullException.ThrowIfNull(nativeAttempt);
            expectedStop.Validate();
            if (_retentionId is not null)
                throw new InvalidOperationException("FIXTURE: guard was already retained.");
            var retentionId = Guid.NewGuid();
            if (!ProcessGuardian.TryAdd(
                    retentionId,
                    [this, nativeAttempt, outstandingSubmission ?? Task.CompletedTask]))
                throw new InvalidOperationException("FIXTURE: process guardian registration collided.");
            _retentionId = retentionId;
            return new NativeSubmissionGuardRetention(
                retentionId,
                expectedStop.SubmissionAttemptId,
                expectedStop.NativeRequestBindingSha256,
                expectedStop.SubmittedRequestSha256,
                expectedStop.WorkerInstanceId,
                expectedStop.WorkerGeneration,
                "fixture-process-guardian",
                true);
        }

        public void SimulateExecutorProcessDeath()
        {
            if (_retentionId is Guid retentionId)
                ProcessGuardian.TryRemove(retentionId, out _);
            _retentionId = null;
        }

        private async ValueTask<VerifiedSubmissionPendingAuthorization> CreateSubmissionPendingAsync(
            bool persist,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PendingBegun) throw new InvalidOperationException("Submission PENDING was already begun.");
            var intent = new ApprovalSubmissionIntentV1(
                ApprovalSubmissionIntentV1.CurrentSchemaVersion,
                ApprovalSubmissionIntentV1.CurrentContractId,
                ApprovalSubmissionIntentV1.CurrentProducerModule,
                ApprovalSubmissionIntentV1.CurrentAuthScope,
                Guid.NewGuid(),
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
                Guid.NewGuid(),
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
            _pending = new VerifiedSubmissionPendingAuthorization(intent, pendingState);
            if (persist && authorityStore is not null)
                await authorityStore.PersistAsync(
                    NativeRequestBindingSha256,
                    $"SUBMISSION_PENDING:{intent.SubmissionAttemptId:N}:{pendingState.StateSha256}",
                    cancellationToken);
            PendingBegun = persist;
            return _pending;
        }

        private async ValueTask<VerifiedSubmissionAcknowledgedAuthorization> CreateSubmissionAcknowledgedAsync(
            NativeSubmissionAck acknowledgement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending is null || acknowledgement.SubmissionAttemptId != _pending.Intent.SubmissionAttemptId ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(acknowledgement.PendingStateSha256),
                    Convert.FromHexString(_pending.PendingState.StateSha256)))
                throw new InvalidOperationException("Acknowledgement is not bound to the durable policy-owned SUBMISSION_PENDING state.");

            var intentSha256 = ApprovalSubmissionLifecycleBinding.ComputeIntentSha256(_pending.Intent);
            var pendingStateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(_pending.PendingState);
            var ownerAcknowledgement = new ApprovalSubmissionAcknowledgementV1(
                ApprovalSubmissionAcknowledgementV1.CurrentSchemaVersion,
                ApprovalSubmissionAcknowledgementV1.CurrentContractId,
                ApprovalSubmissionAcknowledgementV1.CurrentProducerModule,
                ApprovalSubmissionAcknowledgementV1.CurrentAuthScope,
                Guid.NewGuid(),
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
                intentSha256,
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
                Guid.NewGuid(),
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
                intentSha256,
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
            if (authorityStore is not null)
                await authorityStore.PersistAsync(
                    NativeRequestBindingSha256,
                    $"SUBMISSION_ACKNOWLEDGED:{acknowledgedState.StateSha256}",
                    cancellationToken);
            Acknowledged = true;
            return new VerifiedSubmissionAcknowledgedAuthorization(ownerAcknowledgement, acknowledgedState);
        }

        public bool ReleasedAfterDurableAcknowledgement { get; private set; }
        public ValueTask DisposeAsync()
        {
            if (_retentionId is not null)
                return ValueTask.FromException(new InvalidOperationException("FIXTURE: retained process guard has no ordinary release path."));
            Disposed = true;
            ReleasedAfterDurableAcknowledgement = Acknowledged && process.LastSubmissionAck is
            {
                Durability: NativeSubmissionAck.DurableFlush
            };
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : ITrustedClock
    {
        public DateTimeOffset GetUtcNow() => now;
    }

    // This file-backed authority is a REAL_LOCAL_PROCESS fixture only. It models durable blocking
    // state across test-host restarts; it does not verify Policy Approval signatures and cannot
    // satisfy the production PostgreSQL adapter or any Windows/device evidence gate.
    private sealed class DurableSubmissionAuthorityFixtureStore(string path)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<bool> IsBlockedAsync(string nativeRequestBindingSha256, CancellationToken cancellationToken)
        {
            NativeContractGuard.RequireSha256(nativeRequestBindingSha256, nameof(nativeRequestBindingSha256));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var prefix = nativeRequestBindingSha256 + ":";
                return (await ReadAsync(cancellationToken)).Any(line => line.StartsWith(prefix, StringComparison.Ordinal));
            }
            finally { _gate.Release(); }
        }

        public async Task PersistAsync(
            string nativeRequestBindingSha256,
            string stateBinding,
            CancellationToken cancellationToken)
        {
            NativeContractGuard.RequireSha256(nativeRequestBindingSha256, nameof(nativeRequestBindingSha256));
            NativeContractGuard.RequireText(stateBinding, 320, nameof(stateBinding));
            if (stateBinding.Contains('\r') || stateBinding.Contains('\n'))
                throw new ArgumentException("Authority fixture state binding must be one line.", nameof(stateBinding));
            var marker = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"dps.executor-gateway.local-submission-authority-fixture/v1:{nativeRequestBindingSha256}:{stateBinding}")));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var entries = await ReadAsync(cancellationToken);
                entries.RemoveAll(line => line.StartsWith(nativeRequestBindingSha256 + ":", StringComparison.Ordinal));
                entries.Add($"{nativeRequestBindingSha256}:{marker}");
                await WriteAsync(entries, cancellationToken);
            }
            finally { _gate.Release(); }
        }

        private async Task<List<string>> ReadAsync(CancellationToken cancellationToken) => File.Exists(path)
            ? (await File.ReadAllLinesAsync(path, cancellationToken))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList()
            : [];

        private async Task WriteAsync(IEnumerable<string> entries, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var payload = string.Join('\n', entries.Order(StringComparer.Ordinal));
            if (payload.Length > 0) payload += "\n";
            var bytes = new UTF8Encoding(false, true).GetBytes(payload);
            try
            {
                await using (var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private sealed class FixtureScenario : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dps-executor-fixture-{Guid.NewGuid():N}");
        public FixtureScenario() => Directory.CreateDirectory(_directory);
        public string SubmissionAuthorityFilePath => Path.Combine(_directory, "submission-authority-fixture.txt");
        public Task<FixtureProcess> StartAsync() => FixtureProcess.StartAsync(Path.Combine(_directory, "state.json"));
        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureProcess : IVerifiedActiveReleaseBomReader, INativeCommandExecutor, IBusinessPostconditionVerifier, IAsyncDisposable
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _exchange = new(1, 1);
        private readonly string _workerInstanceId;
        private FixtureProcess(Process process)
        {
            _process = process;
            _workerInstanceId = $"wi_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
        }
        public NativeExecutionRequestV1? LastNativeRequest { get; private set; }
        public NativeSubmissionAck? LastSubmissionAck { get; private set; }
        public int SubmissionCallCount { get; private set; }

        public static async Task<FixtureProcess> StartAsync(string stateFile)
        {
            var fixtureDll = Path.Combine(AppContext.BaseDirectory, "native-fixture", "Dps.ExecutorGateway.NativeFixture.dll");
            if (!File.Exists(fixtureDll)) throw new FileNotFoundException("Required REAL_LOCAL_PROCESS fixture was not copied by the test build.", fixtureDll);
            var host = ResolveDotnetHost();
            var startInfo = new ProcessStartInfo
            {
                FileName = host,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(fixtureDll);
            startInfo.ArgumentList.Add("--state-file");
            startInfo.ArgumentList.Add(stateFile);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("REAL_LOCAL_PROCESS fixture did not start.");
            var client = new FixtureProcess(process);
            try
            {
                var hello = await client.ExchangeAsync("hello", null, null, null, CancellationToken.None);
                if (hello.EvidenceKind != EvidenceKind) throw new InvalidDataException("Fixture evidence kind is not REAL_LOCAL_PROCESS.");
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public async ValueTask<ActiveReleaseBomBindingV1?> ReadVerifiedActiveAsync(
            string deviceBindingId,
            CancellationToken cancellationToken)
        {
            var response = await ExchangeAsync("read_active", null, null, null, cancellationToken);
            var active = response.ActiveBinding ?? throw new InvalidDataException("Fixture active binding is missing.");
            if (!string.Equals(active.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)) return null;
            return new ActiveReleaseBomBindingV1(
                active.SchemaVersion,
                active.DeviceBindingId,
                active.ReleaseBomSha256,
                active.Generation,
                active.ExecutionTokenBase64,
                "active");
        }

        public INativeSubmissionAttempt CreateInertSubmissionAttempt() => new FixtureSubmissionAttempt(this);

        private async Task<NativeSubmission> SubmitCoreAsync(
            NativeExecutionRequestV1 request,
            CancellationToken cancellationToken)
        {
            SubmissionCallCount++;
            LastNativeRequest = request;
            var response = await ExchangeAsync("submit", null, request, null, cancellationToken);
            var wire = response.SubmissionAck ?? throw new InvalidDataException("Fixture submission acknowledgement is missing.");
            var acknowledgement = new NativeSubmissionAck(
                wire.SchemaVersion,
                wire.ContractId,
                wire.ProducerModule,
                wire.SubmissionId,
                wire.CompletionHandleId,
                wire.CommandId,
                wire.LeaseId,
                wire.Attempt,
                wire.SoulId,
                wire.DeviceBindingId,
                wire.PlatformAccountId,
                wire.TraceId,
                wire.IdempotencyKey,
                wire.OccurredAt,
                wire.PrivacyClass,
                wire.Durability,
                wire.CommandSha256,
                wire.AuthorizationSha256,
                wire.SubmissionAttemptId,
                wire.SubmissionIntentSha256,
                wire.PendingStateSha256,
                wire.ActiveReleaseBomSha256,
                wire.ActiveReleaseBomGeneration,
                wire.ActiveReleaseBomTokenSha256,
                wire.SubmittedRequestSha256,
                wire.AcknowledgementSha256);
            LastSubmissionAck = acknowledgement;
            return new NativeSubmission(acknowledgement, new FixtureCompletion(this, acknowledgement.CompletionHandleId));
        }

        private sealed class FixtureSubmissionAttempt(FixtureProcess owner) : INativeSubmissionAttempt
        {
            private int _submitted;
            public string WorkerInstanceId => owner._workerInstanceId;
            public long WorkerGeneration => 1;
            public Task<NativeSubmission> SubmitFirstByteAsync(
                NativeExecutionRequestV1 request,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref _submitted, 1) != 0)
                    throw new InvalidOperationException("REAL_LOCAL_PROCESS submission attempt is single use.");
                return owner.SubmitCoreAsync(request, cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private async Task<NativeExecutionResponse> CompleteAsync(
            Guid completionHandleId,
            CancellationToken cancellationToken)
        {
            var response = await ExchangeAsync("complete", null, null, completionHandleId, cancellationToken);
            var native = response.NativeResult ?? throw new InvalidDataException("Fixture native result is missing.");
            return new NativeExecutionResponse(
                NativeResultV1.CurrentSchemaVersion,
                NativeResultV1.CurrentContractId,
                NativeResultV1.CurrentProducerModule,
                native.NativeResultId,
                native.CommandId,
                native.LeaseId,
                native.Attempt,
                native.SoulId,
                native.DeviceBindingId,
                native.PlatformAccountId,
                native.TraceId,
                native.IdempotencyKey,
                native.OccurredAt,
                native.ActiveReleaseBomSha256,
                native.ActiveReleaseBomGeneration,
                native.ActiveReleaseBomTokenSha256,
                native.StepResults.Select(step => new NativeStepResultV1(
                    step.StepId, step.StepKind, step.Status, step.NativeCode, step.EvidenceDigest)).ToArray());
        }

        public async Task<PostconditionVerification> VerifyAsync(
            CommandDispatchV1 command,
            NativeResultV1 nativeResult,
            CancellationToken cancellationToken)
        {
            var state = await ReadStateAsync(cancellationToken);
            var expectedCount = command.IsSideEffect ? 1 : 0;
            var verified = nativeResult.StepResults.Count == 1 &&
                           nativeResult.StepResults[0].Status == NativeStepResultV1.Success &&
                           state.SideEffectCount >= expectedCount;
            var evidence = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{nativeResult.NativeResultId:N}:{state.SideEffectCount}:{verified}")));
            return new PostconditionVerification(verified, evidence, verified ? "FIXTURE_STATE_VERIFIED" : "FIXTURE_STATE_MISMATCH");
        }

        public Task SetModeAsync(string mode, CancellationToken cancellationToken) =>
            ExchangeAsync("set_mode", mode, null, null, cancellationToken);

        public Task<FixtureResponse> ReadStateAsync(CancellationToken cancellationToken) =>
            ExchangeAsync("read_state", null, null, null, cancellationToken);

        private async Task<FixtureResponse> ExchangeAsync(
            string operation,
            string? mode,
            NativeExecutionRequestV1? execution,
            Guid? completionHandleId,
            CancellationToken cancellationToken)
        {
            await _exchange.WaitAsync(cancellationToken);
            try
            {
                if (_process.HasExited) throw new IOException($"REAL_LOCAL_PROCESS fixture exited with code {_process.ExitCode}.");
                var requestId = Guid.NewGuid();
                var request = new FixtureRequest(RequestSchema, requestId, operation, mode, execution, completionHandleId);
                var line = JsonSerializer.Serialize(request, Json);
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
                var responseLine = await _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (responseLine is null) throw new IOException("REAL_LOCAL_PROCESS fixture closed stdout before returning a response.");
                var response = JsonSerializer.Deserialize<FixtureResponse>(responseLine, Json)
                    ?? throw new InvalidDataException("Fixture response is empty.");
                if (response.SchemaVersion != "dps.native-fixture.response/v1" ||
                    response.EvidenceKind != EvidenceKind ||
                    response.RequestId != requestId.ToString("D") ||
                    response.Status != "OK" || response.ErrorCode is not null)
                    throw new InvalidDataException($"Fixture rejected the exact request with '{response.ErrorCode ?? "INVALID_RESPONSE"}'.");
                return response;
            }
            finally { _exchange.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            try { _process.StandardInput.Close(); }
            catch (InvalidOperationException) { }
            if (!_process.HasExited)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            }
            _process.Dispose();
            _exchange.Dispose();
        }

        private static string ResolveDotnetHost()
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var userLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
            return File.Exists(userLocal) ? userLocal : "dotnet";
        }

        private sealed class FixtureCompletion(FixtureProcess owner, Guid completionHandleId) : INativeSubmissionCompletion
        {
            public Guid CompletionHandleId { get; } = completionHandleId;
            public Task<NativeExecutionResponse> WaitForResultAsync(CancellationToken cancellationToken) =>
                owner.CompleteAsync(CompletionHandleId, cancellationToken);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed record FixtureRequest(
        string SchemaVersion,
        Guid RequestId,
        string Operation,
        string? Mode,
        NativeExecutionRequestV1? Execution,
        Guid? CompletionHandleId);
    private sealed record FixtureResponse(
        string SchemaVersion,
        string EvidenceKind,
        string RequestId,
        string Status,
        string? ErrorCode,
        FixtureActiveBinding? ActiveBinding,
        FixtureSubmissionAck? SubmissionAck,
        FixtureNativeResult? NativeResult,
        int? SideEffectCount);
    private sealed record FixtureActiveBinding(
        string SchemaVersion,
        string DeviceBindingId,
        string ReleaseBomSha256,
        long Generation,
        string ExecutionTokenBase64);
    private sealed record FixtureSubmissionAck(
        [property: JsonPropertyName("schema_version")] string SchemaVersion,
        [property: JsonPropertyName("contract_id")] string ContractId,
        [property: JsonPropertyName("producer_module")] string ProducerModule,
        [property: JsonPropertyName("submission_id")] Guid SubmissionId,
        [property: JsonPropertyName("completion_handle_id")] Guid CompletionHandleId,
        [property: JsonPropertyName("command_id")] Guid CommandId,
        [property: JsonPropertyName("lease_id")] Guid LeaseId,
        [property: JsonPropertyName("attempt")] int Attempt,
        [property: JsonPropertyName("soul_id")] string SoulId,
        [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
        [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
        [property: JsonPropertyName("trace_id")] string TraceId,
        [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
        [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
        [property: JsonPropertyName("privacy_class")] string PrivacyClass,
        [property: JsonPropertyName("durability")] string Durability,
        [property: JsonPropertyName("command_sha256")] string CommandSha256,
        [property: JsonPropertyName("authorization_sha256")] string AuthorizationSha256,
        [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
        [property: JsonPropertyName("submission_intent_sha256")] string SubmissionIntentSha256,
        [property: JsonPropertyName("pending_state_sha256")] string PendingStateSha256,
        [property: JsonPropertyName("active_release_bom_sha256")] string ActiveReleaseBomSha256,
        [property: JsonPropertyName("active_release_bom_generation")] long ActiveReleaseBomGeneration,
        [property: JsonPropertyName("active_release_bom_token_sha256")] string ActiveReleaseBomTokenSha256,
        [property: JsonPropertyName("submitted_request_sha256")] string SubmittedRequestSha256,
        [property: JsonPropertyName("acknowledgement_sha256")] string AcknowledgementSha256);
    private sealed record FixtureNativeResult(
        Guid NativeResultId,
        Guid CommandId,
        Guid LeaseId,
        int Attempt,
        string SoulId,
        string DeviceBindingId,
        string PlatformAccountId,
        string TraceId,
        string IdempotencyKey,
        DateTimeOffset OccurredAt,
        string ActiveReleaseBomSha256,
        long ActiveReleaseBomGeneration,
        string ActiveReleaseBomTokenSha256,
        IReadOnlyList<FixtureNativeStepResult> StepResults);
    private sealed record FixtureNativeStepResult(
        Guid StepId,
        string StepKind,
        string Status,
        string NativeCode,
        string EvidenceDigest);
}
