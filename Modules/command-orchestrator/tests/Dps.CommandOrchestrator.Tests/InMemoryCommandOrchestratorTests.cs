using Dps.CommandOrchestrator.Contracts;
using Dps.OperationCompiler.Contracts;
using System.Security.Cryptography;
using Xunit;

namespace Dps.CommandOrchestrator.Tests;

public sealed class InMemoryCommandOrchestratorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    [Trait("Category", "Unit")]
    public void DuplicateIsNoOpAndConflictingDeliveryIsQuarantined()
    {
        var service = Service(); var operation = Operation(); var first = service.Enqueue(operation); var duplicate = service.Enqueue(operation);
        var conflictingArguments = new Dictionary<string, string> { ["selector_ref"] = "fixture.status" };
        var conflictingOperation = Recanonicalize(operation with { ActionKind = "verify", Steps = [CanonicalStep(operation.OperationId, "ui.verify", conflictingArguments, true, "assertion-satisfied")] });
        var conflict = service.Enqueue(conflictingOperation);
        Assert.Equal(EnqueueDisposition.Inserted, first.Disposition); Assert.Equal(EnqueueDisposition.DuplicateNoOp, duplicate.Disposition); Assert.Equal(first.CommandId, duplicate.CommandId); Assert.Equal(EnqueueDisposition.Quarantined, conflict.Disposition); Assert.Equal(1, service.QuarantineCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimeoutBeforeDispatchRequeuesButCrashWindowAfterDispatchRequiresReconciliation()
    {
        var pre = Service(); var preId = pre.Enqueue(Operation()).CommandId!.Value; pre.AcquireLease(preId, Soul, Device, Account, "worker-a", Now, TimeSpan.FromSeconds(5)); pre.RecoverExpiredLeases(Now.AddSeconds(6)); Assert.Equal(CommandState.Pending, pre.GetSnapshot(preId, Soul, Device, Account).State);
        var post = Service(); var postId = post.Enqueue(Operation()).CommandId!.Value; var dispatch = post.AcquireLease(postId, Soul, Device, Account, "worker-a", Now, TimeSpan.FromSeconds(5)); post.MarkDispatched(postId, dispatch.LeaseId, Authorization(dispatch), Now.AddSeconds(1)); post.RecoverExpiredLeases(Now.AddSeconds(6)); Assert.Equal(CommandState.ReconciliationRequired, post.GetSnapshot(postId, Soul, Device, Account).State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UnknownOutcomeIsNeverRetried()
    {
        var service = Service(); var id = service.Enqueue(Operation()).CommandId!.Value; var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker", Now, TimeSpan.FromMinutes(1)); service.MarkDispatched(id, dispatch.LeaseId, Authorization(dispatch), Now.AddSeconds(1));
        var result = service.RecordReceipt(Receipt(dispatch, CommandReceiptV1.UnknownOutcome, retry: false, native: false, post: false));
        Assert.Equal(CommandState.ReconciliationRequired, result.State); Assert.Throws<InvalidOperationException>(() => service.AcquireLease(id, Soul, Device, Account, "worker", Now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => RawReceipt(dispatch, CommandReceiptV1.UnknownOutcome, retry: true, native: false, post: false).Validate());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OutOfOrderReceiptAndCrossScopeAccessFailClosed()
    {
        var service = Service(); var id = service.Enqueue(Operation()).CommandId!.Value;
        var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker", Now, TimeSpan.FromMinutes(1));
        Assert.Throws<InvalidOperationException>(() => service.RecordReceipt(Receipt(dispatch, CommandReceiptV1.Failed, false, false, false)));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetSnapshot(id, "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Device, Account));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetSnapshot(id, Soul, OtherDevice, Account));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetSnapshot(id, Soul, Device, OtherAccount));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OldAttemptReceiptCannotAffectANewLease()
    {
        var service = Service(); var id = service.Enqueue(Operation()).CommandId!.Value;
        var firstDispatch = service.AcquireLease(id, Soul, Device, Account, "worker-a", Now, TimeSpan.FromMinutes(1));
        service.MarkDispatched(id, firstDispatch.LeaseId, Authorization(firstDispatch), Now.AddSeconds(1));
        var firstReceipt = Receipt(firstDispatch, CommandReceiptV1.Failed, retry: true, native: true, post: false);
        Assert.Equal(CommandState.Pending, service.RecordReceipt(firstReceipt).State);
        var secondDispatch = service.AcquireLease(id, Soul, Device, Account, "worker-b", Now.AddSeconds(3), TimeSpan.FromMinutes(1));
        service.MarkDispatched(id, secondDispatch.LeaseId, Authorization(secondDispatch), Now.AddSeconds(4));
        Assert.Equal(ReceiptDisposition.DuplicateNoOp, service.RecordReceipt(firstReceipt).Disposition);
        Assert.Equal(CommandState.Dispatched, service.GetSnapshot(id, Soul, Device, Account).State);
        Assert.Throws<UnauthorizedAccessException>(() => service.RecordReceipt(firstReceipt with { Receipt = firstReceipt.Receipt with { ReceiptId = Guid.NewGuid() } }));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OperationDigestBindsPreviouslyOmittedEnvelopeFields()
    {
        var operation = Operation();
        AssertOperationConflict(operation, Recanonicalize(operation with { SchemaVersion = "1.0.1" }));
        AssertOperationConflict(operation, Recanonicalize(operation with { ProposalId = Guid.Parse("63000000-0000-0000-0000-000000000099") }));
        AssertOperationConflict(operation, Recanonicalize(operation with { ApprovalSha256 = new string('e', 64) }));
        AssertOperationConflict(operation, Recanonicalize(operation with { TraceId = OtherTrace }));
        AssertOperationConflict(operation, Recanonicalize(operation with { OccurredAt = Now.AddMilliseconds(1) }));
        AssertOperationConflict(operation, Recanonicalize(operation with { PlatformAuthorizationId = "non-side-effect-context" }));

        var service = Service();
        var original = service.Enqueue(operation);
        var otherIdempotencyIdentity = service.Enqueue(Recanonicalize(operation with { IdempotencyKey = OtherIdempotency }));
        Assert.Equal(EnqueueDisposition.Inserted, otherIdempotencyIdentity.Disposition);
        Assert.NotEqual(original.PayloadSha256, otherIdempotencyIdentity.PayloadSha256);

        var dispatchService = Service();
        var dispatchId = dispatchService.Enqueue(operation).CommandId!.Value;
        var dispatch = dispatchService.AcquireLease(dispatchId, Soul, Device, Account, "worker-approval-proof", Now, TimeSpan.FromMinutes(1));
        Assert.Equal(operation.ApprovalSha256, dispatch.ApprovalSha256);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FormerArgumentDelimiterCollisionIsQuarantined()
    {
        var firstArguments = new Dictionary<string, string>
        {
            ["selector_ref"] = "a,value_ref=b",
            ["value_ref"] = "c"
        };
        var operation = Recanonicalize(Operation() with
        {
            ActionKind = "fixture.type",
            IsSideEffect = true,
            PlatformAuthorizationId = "fixture-authorization",
            Steps = [CanonicalStep(Operation().OperationId, "fixture.type", firstArguments, false, "fixture-value-matched")]
        });
        var formerArguments = new Dictionary<string, string>
        {
            ["selector_ref"] = "a",
            ["value_ref"] = "b,value_ref=c"
        };
        var formerCollision = Recanonicalize(operation with
        {
            Steps = [CanonicalStep(operation.OperationId, "fixture.type", formerArguments, false, "fixture-value-matched")]
        });

        var service = Service();
        var first = service.Enqueue(operation);
        var conflict = service.Enqueue(formerCollision);

        Assert.Equal(EnqueueDisposition.Inserted, first.Disposition);
        Assert.Equal(EnqueueDisposition.Quarantined, conflict.Disposition);
        Assert.NotEqual(first.PayloadSha256, conflict.PayloadSha256);
        Assert.Equal(1, service.QuarantineCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConflictingOpaqueReceiptIsQuarantinedWithoutChangingUnknownOutcomeState()
    {
        var operation = Operation();
        var service = Service();
        var id = service.Enqueue(operation).CommandId!.Value;
        var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker", Now, TimeSpan.FromMinutes(1));
        service.MarkDispatched(id, dispatch.LeaseId, Authorization(dispatch), Now.AddSeconds(1));
        var receipt = Receipt(dispatch, CommandReceiptV1.UnknownOutcome, retry: false, native: false, post: false);

        var applied = service.RecordReceipt(receipt);
        var conflictPayload = receipt.Receipt with { TraceId = OtherTrace, IdempotencyKey = OtherIdempotency };
        var conflictEnvelope = SignReceipt(receipt with
        {
            TraceId = OtherTrace,
            IdempotencyKey = OtherIdempotency,
            Receipt = conflictPayload,
            ReceiptSha256 = CommandReceiptProtocolV1.ComputeReceiptSha256(conflictPayload)
        });
        var conflict = service.RecordReceipt(conflictEnvelope);

        Assert.Equal(CommandState.ReconciliationRequired, applied.State);
        Assert.Equal(ReceiptDisposition.Quarantined, conflict.Disposition);
        Assert.Equal(CommandState.ReconciliationRequired, conflict.State);
        Assert.Equal(1, service.QuarantineCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReceiptDigestBindsPreviouslyOmittedEnvelopeFieldsAndExactDuplicateIsNoOp()
    {
        AssertReceiptConflict(receipt => receipt with { SchemaVersion = "1.0.1" });
        AssertReceiptConflict(receipt => receipt with { OccurredAt = receipt.OccurredAt.AddMilliseconds(1) });

        var (service, receipt) = ApplyFailedReceipt();
        var duplicate = service.RecordReceipt(receipt);
        Assert.Equal(ReceiptDisposition.DuplicateNoOp, duplicate.Disposition);
        Assert.Equal(CommandState.Pending, duplicate.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FirstReceiptMustBindTraceAndIdempotencyToTheOperation()
    {
        var service = Service();
        var id = service.Enqueue(Operation()).CommandId!.Value;
        var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker", Now, TimeSpan.FromMinutes(1));
        service.MarkDispatched(id, dispatch.LeaseId, Authorization(dispatch), Now.AddSeconds(1));
        var receipt = Receipt(dispatch, CommandReceiptV1.Failed, retry: true, native: true, post: false);

        Assert.Throws<UnauthorizedAccessException>(() => service.RecordReceipt(receipt with { Receipt = receipt.Receipt with { TraceId = OtherTrace } }));
        Assert.Throws<UnauthorizedAccessException>(() => service.RecordReceipt(receipt with { Receipt = receipt.Receipt with { IdempotencyKey = OtherIdempotency } }));
        Assert.Equal(CommandState.Dispatched, service.GetSnapshot(id, Soul, Device, Account).State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanonicalOperationEncodingBindsStepCountOrdinalAndArgumentSemantics()
    {
        var locate = new OperationStepV1(
            Guid.Parse("64000000-0000-0000-0000-000000000010"),
            "ui.locate",
            new Dictionary<string, string> { ["selector_ref"] = "fixture.first" },
            true,
            "selector-resolved");
        var verify = new OperationStepV1(
            Guid.Parse("64000000-0000-0000-0000-000000000011"),
            "ui.verify",
            new Dictionary<string, string> { ["selector_ref"] = "fixture.second" },
            true,
            "assertion-satisfied");
        var twoSteps = Operation() with { Steps = [locate, verify] };

        Assert.NotEqual(
            CommandCanonicalEncoding.OperationDigest(twoSteps),
            CommandCanonicalEncoding.OperationDigest(twoSteps with { Steps = [verify, locate] }));
        Assert.NotEqual(
            CommandCanonicalEncoding.OperationDigest(twoSteps),
            CommandCanonicalEncoding.OperationDigest(twoSteps with { Steps = [locate] }));

        var argumentsInFirstOrder = new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "fixture.value"
        };
        var argumentsInReverseOrder = new Dictionary<string, string>
        {
            ["value_ref"] = "fixture.value",
            ["selector_ref"] = "fixture.input"
        };
        var typed = Operation() with
        {
            Steps = [new OperationStepV1(locate.StepId, "fixture.type", argumentsInFirstOrder, false, "fixture-value-matched")]
        };
        var reorderedMap = typed with { Steps = [typed.Steps[0] with { Arguments = argumentsInReverseOrder }] };
        var changedArgument = typed with
        {
            Steps = [typed.Steps[0] with { Arguments = new Dictionary<string, string>(argumentsInFirstOrder) { ["value_ref"] = "fixture.changed" } }]
        };

        Assert.Equal(CommandCanonicalEncoding.OperationDigest(typed), CommandCanonicalEncoding.OperationDigest(reorderedMap));
        Assert.NotEqual(CommandCanonicalEncoding.OperationDigest(typed), CommandCanonicalEncoding.OperationDigest(changedArgument));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanonicalEncodingBindsNullPresenceAndOpaqueScopeComponents()
    {
        var operation = Operation();
        Assert.NotEqual(
            CommandCanonicalEncoding.OperationDigest(operation),
            CommandCanonicalEncoding.OperationDigest(operation with { PlatformAuthorizationId = "present" }));

        var canonicalService = Service();
        var canonicalId = canonicalService.Enqueue(Operation()).CommandId!.Value;
        var canonicalDispatch = canonicalService.AcquireLease(canonicalId, Soul, Device, Account, "worker-canonical", Now, TimeSpan.FromMinutes(1));
        var receipt = RawReceipt(canonicalDispatch, CommandReceiptV1.Failed, false, false, false);
        Assert.NotEqual(
            CommandCanonicalEncoding.ReceiptDigest(receipt),
            CommandCanonicalEncoding.ReceiptDigest(receipt with { NativeResultId = Guid.Parse("69000000-0000-0000-0000-000000000009") }));

        Assert.NotEqual(
            CommandCanonicalEncoding.IdempotencyScopeKey(Soul, Device, Account, Idempotency),
            CommandCanonicalEncoding.IdempotencyScopeKey(Soul, OtherDevice, Account, Idempotency));
        Assert.Throws<ArgumentException>(() => CommandCanonicalEncoding.IdempotencyScopeKey(Soul, "db_60123456789", Account, Idempotency));
        Assert.Throws<ArgumentException>(() => CommandCanonicalEncoding.IdempotencyScopeKey(Soul, Device, Account, "Bearer secret-token"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnqueueSnapshotsStepArgumentsBeforeDispatch()
    {
        var arguments = new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "fixture.original"
        };
        var operation = Recanonicalize(Operation() with
        {
            ActionKind = "fixture.type",
            IsSideEffect = true,
            PlatformAuthorizationId = "fixture-authorization",
            Steps = [CanonicalStep(Operation().OperationId, "fixture.type", arguments, false, "fixture-value-matched")]
        });
        var service = Service();
        var id = service.Enqueue(operation).CommandId!.Value;
        arguments["value_ref"] = "fixture.mutated";

        var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker", Now, TimeSpan.FromMinutes(1));

        Assert.Equal("fixture.original", dispatch.Steps[0].Arguments["value_ref"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InvalidUtf16TraceAndArgumentValuesFailClosedBeforeHashing()
    {
        foreach (var invalid in new[] { "\uD800", "\uD801" })
        {
            Assert.Throws<System.Text.EncoderFallbackException>(() => Recanonicalize(Operation() with { TraceId = invalid }));
            var invalidArgument = Operation() with
            {
                ActionKind = "fixture.type",
                IsSideEffect = true,
                PlatformAuthorizationId = "fixture-authorization",
                Steps = [new OperationStepV1(
                    Guid.Parse("64000000-0000-0000-0000-000000000004"),
                    "fixture.type",
                    new Dictionary<string, string> { ["selector_ref"] = "fixture.input", ["value_ref"] = invalid },
                    false,
                    "fixture-value-matched")]
            };
            Assert.Throws<System.Text.EncoderFallbackException>(() => Recanonicalize(invalidArgument));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RawReceiptEntryPointIsAbsentAndForgedSuccessCannotAdvanceState()
    {
        Assert.False(typeof(InMemoryCommandOrchestrator).IsPublic);
        Assert.DoesNotContain(
            typeof(InMemoryCommandOrchestrator).GetMethods(),
            method => method.Name == nameof(InMemoryCommandOrchestrator.RecordReceipt) &&
                      method.GetParameters().SingleOrDefault()?.ParameterType == typeof(CommandReceiptV1));
        var (service, dispatch) = PrepareDispatched();
        var authentic = Receipt(dispatch, CommandReceiptV1.Success, retry: false, native: true, post: true);
        var forged = authentic with { SignatureBase64 = Convert.ToBase64String(new byte[CommandReceiptProtocolV1.P1363SignatureSizeBytes]) };

        Assert.Throws<UnauthorizedAccessException>(() => service.RecordReceipt(forged));
        Assert.Equal(CommandState.Dispatched, service.GetSnapshot(dispatch.CommandId, Soul, Device, Account).State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AuthenticSignedSuccessIsTheOnlyPathToSucceeded()
    {
        var (service, dispatch) = PrepareDispatched();
        var result = service.RecordReceipt(Receipt(dispatch, CommandReceiptV1.Success, retry: false, native: true, post: true));

        Assert.Equal(ReceiptDisposition.Applied, result.Disposition);
        Assert.Equal(CommandState.Succeeded, result.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RetryableFailedReceiptRequiresAuthenticSignatureAndVerifiedNativeResult()
    {
        var (forgedService, forgedDispatch) = PrepareDispatched();
        var nonRetry = Receipt(forgedDispatch, CommandReceiptV1.Failed, retry: false, native: true, post: false);
        var forgedPayload = nonRetry.Receipt with { RetryAllowed = true };
        Assert.ThrowsAny<Exception>(() => forgedService.RecordReceipt(nonRetry with { Receipt = forgedPayload }));
        Assert.Equal(CommandState.Dispatched, forgedService.GetSnapshot(forgedDispatch.CommandId, Soul, Device, Account).State);
        Assert.Throws<InvalidOperationException>(() => RawReceipt(forgedDispatch, CommandReceiptV1.Failed, retry: true, native: false, post: false).Validate());

        var (validService, validDispatch) = PrepareDispatched();
        var result = validService.RecordReceipt(Receipt(validDispatch, CommandReceiptV1.Failed, retry: true, native: true, post: false));
        Assert.Equal(CommandState.Pending, result.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SignedReceiptTamperingCommandScopeAttemptEvidenceAndBomFailsClosed()
    {
        var mutations = new Func<SignedCommandReceiptV1, SignedCommandReceiptV1>[]
        {
            value => value with { CommandSha256 = new string('c', 64) },
            value => value with { AuthorizationSha256 = new string('c', 64) },
            value => value with { ReleaseBomSha256 = new string('c', 64) },
            value => value with { ActiveReleaseBomGeneration = value.ActiveReleaseBomGeneration + 1 },
            value => value with { ActiveReleaseBomTokenSha256 = new string('c', 64) },
            value => value with { NativeEvidenceSha256 = new string('c', 64) },
            value => value with { PostconditionEvidenceSha256 = new string('c', 64) },
            value => value with { ReceiptId = Guid.NewGuid() },
            value => value with { CommandId = Guid.NewGuid() },
            value => value with { LeaseId = Guid.NewGuid() },
            value => value with { Attempt = 2 },
            value => value with { SoulId = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            value => value with { DeviceBindingId = OtherDevice },
            value => value with { PlatformAccountId = OtherAccount },
            value => value with { TraceId = OtherTrace },
            value => value with { IdempotencyKey = OtherIdempotency },
            value => value with { DeviceBindingId = Device + "\n" },
            value => value with { PlatformAccountId = Account + "\n" },
            value => value with { TraceId = Trace + "\n" },
            value => value with { IdempotencyKey = Idempotency + "\n" },
            value => value with { OccurredAt = value.OccurredAt.AddMilliseconds(1) },
            value => value with { PrivacyClass = "public" },
            value => value with { Receipt = value.Receipt with { CommandId = Guid.NewGuid() } },
            value => value with { Receipt = value.Receipt with { LeaseId = Guid.NewGuid() } },
            value => value with { Receipt = value.Receipt with { Attempt = 2 } },
            value => value with { Receipt = value.Receipt with { SoulId = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" } },
            value => value with { Receipt = value.Receipt with { DeviceBindingId = OtherDevice } },
            value => value with { Receipt = value.Receipt with { PlatformAccountId = OtherAccount } },
            value => value with { Receipt = value.Receipt with { TraceId = OtherTrace } },
            value => value with { Receipt = value.Receipt with { IdempotencyKey = OtherIdempotency } },
            value => value with { Receipt = value.Receipt with { DeviceBindingId = Device + "\n" } },
            value => value with { Receipt = value.Receipt with { PlatformAccountId = Account + "\n" } },
            value => value with { Receipt = value.Receipt with { TraceId = Trace + "\n" } },
            value => value with { Receipt = value.Receipt with { IdempotencyKey = Idempotency + "\n" } }
        };

        foreach (var mutate in mutations)
        {
            var (service, dispatch) = PrepareDispatched();
            var authentic = Receipt(dispatch, CommandReceiptV1.Success, retry: false, native: true, post: true);
            Assert.ThrowsAny<Exception>(() => service.RecordReceipt(mutate(authentic)));
            Assert.Equal(CommandState.Dispatched, service.GetSnapshot(dispatch.CommandId, Soul, Device, Account).State);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BomOrAuthorizationMismatchIsRejectedEvenWhenSignedByTrustedKey()
    {
        foreach (var mutate in new Func<SignedCommandReceiptV1, SignedCommandReceiptV1>[]
        {
            value => value with { ActiveReleaseBomGeneration = value.ActiveReleaseBomGeneration + 1 },
            value => value with { ActiveReleaseBomTokenSha256 = new string('c', 64) },
            value => value with { ReleaseBomSha256 = new string('c', 64) },
            value => value with { AuthorizationSha256 = new string('c', 64) }
        })
        {
            var (service, dispatch) = PrepareDispatched();
            var mismatchedButAuthentic = SignReceipt(mutate(Receipt(dispatch, CommandReceiptV1.Success, retry: false, native: true, post: true)));
            Assert.Throws<UnauthorizedAccessException>(() => service.RecordReceipt(mismatchedButAuthentic));
            Assert.Equal(CommandState.Dispatched, service.GetSnapshot(dispatch.CommandId, Soul, Device, Account).State);
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void UnknownStepMajorAndFalseSuccessAreRejected()
    {
        var service = Service(); var id = service.Enqueue(Operation()).CommandId!.Value; var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker", Now, TimeSpan.FromMinutes(1));
        Assert.Throws<NotSupportedException>(() => (dispatch with { SchemaVersion = "2.0.0" }).Validate());
        Assert.Throws<NotSupportedException>(() => (dispatch with { Steps = [dispatch.Steps[0] with { StepKind = "shell" }] }).Validate());
        Assert.Throws<InvalidOperationException>(() => RawReceipt(dispatch, CommandReceiptV1.Success, false, true, false).Validate());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void SignedReceiptRejectsUnknownAlgorithmsMalformedSignatureAndNonP256TrustAnchor()
    {
        var (_, dispatch) = PrepareDispatched();
        var receipt = Receipt(dispatch, CommandReceiptV1.Success, retry: false, native: true, post: true);
        Assert.Throws<NotSupportedException>(() => (receipt with { SignatureAlgorithm = "rsa-pss-sha256" }).Validate());
        Assert.Throws<ArgumentException>(() => (receipt with { SignatureBase64 = Convert.ToBase64String(new byte[63]) }).Validate());
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => new InMemoryCommandOrchestrator(p384.ExportSubjectPublicKeyInfo()));
        var runtimeCapability = RandomNumberGenerator.GetBytes(32);
        try
        {
            var options = new PostgresCommandOrchestratorOptions(
                "Host=127.0.0.1;Port=5432;Database=dps_command_contract;Username=contract_migrator;Password=unused;Pooling=false",
                "Host=127.0.0.1;Port=5432;Database=dps_command_contract;Username=contract_runtime;Password=unused;Pooling=false",
                "command_contract",
                "contract_migrator",
                "contract_runtime");
            Assert.Throws<ArgumentException>(() => new PostgresCommandOrchestrator(
                options,
                new NeverSigningPolicyPort(),
                ReceiptKeys.PublicKeySpki,
                ReceiptKeys.PublicKeySpki,
                runtimeCapability));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(runtimeCapability);
        }
    }

    private sealed class NeverSigningPolicyPort : IPolicyExecutionAuthorizationSignerV1
    {
        public string ProtocolId => IPolicyExecutionAuthorizationSignerV1.CurrentProtocolId;
        public string SignerModule => IPolicyExecutionAuthorizationSignerV1.CurrentSignerModule;
        public string KeyId => "sha256:" + new string('0', 64);

        public ValueTask<ExecutionAuthorizationV1> SignAsync(
            ExecutionAuthorizationV1 unsignedAuthorization,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ExecutionAuthorizationV1>(new InvalidOperationException());
    }

    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Device = "db_11111111111111111111111111111111";
    private const string Account = "pa_22222222222222222222222222222222";
    private const string Trace = "trace_33333333333333333333333333333333";
    private const string Idempotency = "idem_4444444444444444444444444444444444444444444444444444444444444444";
    private const string OtherDevice = "db_99999999999999999999999999999999";
    private const string OtherAccount = "pa_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherTrace = "trace_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherIdempotency = "idem_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly string ApprovalSha256 = new('d', 64); private static readonly ReceiptKeyPair ReceiptKeys = CreateReceiptKeys();
    private static CompiledOperationV1 Operation()
    {
        var approvalId = Guid.Parse("62000000-0000-0000-0000-000000000002");
        var proposalId = Guid.Parse("63000000-0000-0000-0000-000000000003");
        var operationId = OperationCompiledV1CanonicalIds.ComputeOperationId(
            CompiledOperationV1.CurrentSchemaVersion, CompiledOperationV1.CurrentContractId, CompiledOperationV1.CurrentProducerModule,
            approvalId, proposalId, ApprovalSha256, Soul, Device, Account, Trace, Idempotency, Now,
            "internal", "observe", false, false, null);
        return new CompiledOperationV1(CompiledOperationV1.CurrentSchemaVersion, CompiledOperationV1.CurrentContractId, CompiledOperationV1.CurrentProducerModule,
            operationId, approvalId, proposalId, ApprovalSha256, Soul, Device, Account,
            Trace, Idempotency, Now, "internal", "observe", false, false, null,
            [CanonicalStep(operationId, "ui.observe", new Dictionary<string, string>(), true, "native-read-complete")]);
    }
    private static CompiledOperationV1 Recanonicalize(CompiledOperationV1 operation)
    {
        var operationId = OperationCompiledV1CanonicalIds.ComputeOperationId(
            operation.SchemaVersion, operation.ContractId, operation.ProducerModule, operation.ApprovalId, operation.ProposalId,
            operation.ApprovalSha256, operation.SoulId, operation.DeviceBindingId, operation.PlatformAccountId,
            operation.TraceId, operation.IdempotencyKey, operation.OccurredAt, operation.PrivacyClass, operation.ActionKind,
            operation.IsSideEffect, operation.ShadowOnly, operation.PlatformAuthorizationId);
        var steps = operation.Steps.Select(step => CanonicalStep(operationId, step.StepKind, step.Arguments, step.RetrySafe, step.PostconditionKind)).ToArray();
        return operation with { OperationId = operationId, Steps = steps };
    }
    private static OperationStepV1 CanonicalStep(Guid operationId, string kind, IReadOnlyDictionary<string, string> arguments, bool retrySafe, string postcondition) =>
        new(OperationCompiledV1CanonicalIds.ComputeStepId(operationId, kind, arguments, retrySafe, postcondition), kind, arguments, retrySafe, postcondition);
    private static InMemoryCommandOrchestrator Service() => new(ReceiptKeys.PublicKeySpki);

    private static ExecutionAuthorizationV1 Authorization(CommandDispatchV1 dispatch) => new(
        ExecutionAuthorizationV1.CurrentSchemaVersion, ExecutionAuthorizationV1.CurrentContractId, ExecutionAuthorizationV1.CurrentProducerModule,
        ExecutionAuthorizationV1.CurrentSignatureDomain, ExecutionAuthorizationV1.CurrentCanonicalEncoding, ExecutionAuthorizationV1.CurrentCommandDigestAlgorithm,
        ExecutionAuthorizationV1.CurrentSignatureAlgorithm, ExecutionAuthorizationV1.CurrentSignatureFormat, ExecutionAuthorizationV1.CurrentSignatureEncoding,
        ExecutionAuthorizationV1.CurrentCallerModule, ExecutionAuthorizationV1.CurrentAuthScope, dispatch.CommandId, dispatch.LeaseId, dispatch.Attempt,
        dispatch.SoulId, dispatch.DeviceBindingId, dispatch.PlatformAccountId, dispatch.TraceId, dispatch.IdempotencyKey, dispatch.OccurredAt, "internal",
        ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch), new string('a', 64), 7, new string('b', 64), dispatch.LeaseExpiresAt, false,
        Convert.ToBase64String(new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes]));

    private static CommandReceiptV1 RawReceipt(CommandDispatchV1 dispatch, string outcome, bool retry, bool native, bool post)
    {
        var nativeEvidence = native ? new string('e', 64) : null;
        var postEvidence = post ? new string('f', 64) : null;
        return new CommandReceiptV1(
            CommandReceiptV1.CurrentSchemaVersion, CommandReceiptV1.CurrentContractId, CommandReceiptV1.CurrentProducerModule,
            Guid.Parse("65000000-0000-0000-0000-000000000005"), dispatch.CommandId, dispatch.LeaseId, dispatch.Attempt,
            dispatch.SoulId, dispatch.DeviceBindingId, dispatch.PlatformAccountId, dispatch.TraceId, dispatch.IdempotencyKey,
            dispatch.OccurredAt.AddSeconds(2), "internal", outcome,
            native ? Guid.Parse("66000000-0000-0000-0000-000000000006") : null,
            native, post, CommandReceiptProtocolV1.ComputeEvidenceDigest(nativeEvidence, postEvidence), retry,
            outcome == CommandReceiptV1.Success ? "VERIFIED" : "NOT_VERIFIED");
    }

    private static SignedCommandReceiptV1 Receipt(CommandDispatchV1 dispatch, string outcome, bool retry, bool native, bool post)
    {
        var authorization = Authorization(dispatch);
        var nativeEvidence = native ? new string('e', 64) : null;
        var postEvidence = post ? new string('f', 64) : null;
        var receipt = RawReceipt(dispatch, outcome, retry, native, post);
        return SignReceipt(new SignedCommandReceiptV1(
            SignedCommandReceiptV1.CurrentSchemaVersion, SignedCommandReceiptV1.CurrentContractId, SignedCommandReceiptV1.CurrentProducerModule,
            SignedCommandReceiptV1.CurrentSignatureDomain, SignedCommandReceiptV1.CurrentCanonicalEncoding,
            SignedCommandReceiptV1.CurrentReceiptDigestAlgorithm, SignedCommandReceiptV1.CurrentCommandDigestAlgorithm,
            SignedCommandReceiptV1.CurrentEvidenceDigestAlgorithm, SignedCommandReceiptV1.CurrentSignatureAlgorithm,
            SignedCommandReceiptV1.CurrentSignatureFormat, SignedCommandReceiptV1.CurrentSignatureEncoding,
            SignedCommandReceiptV1.CurrentSignerModule, SignedCommandReceiptV1.CurrentAuthScope,
            receipt.ReceiptId, receipt.CommandId, receipt.LeaseId, receipt.Attempt, receipt.SoulId,
            receipt.DeviceBindingId, receipt.PlatformAccountId, receipt.TraceId, receipt.IdempotencyKey,
            receipt.OccurredAt, receipt.PrivacyClass,
            CommandReceiptProtocolV1.ComputeReceiptSha256(receipt), ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch),
            ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization), authorization.ReleaseBomSha256,
            authorization.ActiveReleaseBomGeneration, authorization.ActiveReleaseBomTokenSha256, nativeEvidence, postEvidence, receipt,
            Convert.ToBase64String(new byte[CommandReceiptProtocolV1.P1363SignatureSizeBytes])));
    }

    private static SignedCommandReceiptV1 SignReceipt(SignedCommandReceiptV1 unsigned)
    {
        unsigned.ValidatePayload();
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(ReceiptKeys.PrivateKeyPkcs8, out var bytesRead);
        Assert.Equal(ReceiptKeys.PrivateKeyPkcs8.Length, bytesRead);
        var payload = CommandReceiptProtocolV1.CanonicalSignedReceiptBytes(unsigned);
        try
        {
            var signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            try { return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) }; }
            finally { CryptographicOperations.ZeroMemory(signature); }
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static ReceiptKeyPair CreateReceiptKeys()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new ReceiptKeyPair(signer.ExportPkcs8PrivateKey(), signer.ExportSubjectPublicKeyInfo());
    }

    private sealed record ReceiptKeyPair(byte[] PrivateKeyPkcs8, byte[] PublicKeySpki);

    private static (InMemoryCommandOrchestrator Service, CommandDispatchV1 Dispatch) PrepareDispatched()
    {
        var service = Service();
        var id = service.Enqueue(Operation()).CommandId!.Value;
        var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker-receipt-authority", Now, TimeSpan.FromMinutes(1));
        service.MarkDispatched(id, dispatch.LeaseId, Authorization(dispatch), Now.AddSeconds(1));
        return (service, dispatch);
    }

    private static void AssertOperationConflict(CompiledOperationV1 original, CompiledOperationV1 changed)
    {
        var service = Service();
        var first = service.Enqueue(original);
        var conflict = service.Enqueue(changed);
        Assert.Equal(EnqueueDisposition.Inserted, first.Disposition);
        Assert.Equal(EnqueueDisposition.Quarantined, conflict.Disposition);
        Assert.NotEqual(first.PayloadSha256, conflict.PayloadSha256);
    }

    private static void AssertReceiptConflict(Func<CommandReceiptV1, CommandReceiptV1> mutate)
    {
        var (service, receipt) = ApplyFailedReceipt();
        var changedReceipt = mutate(receipt.Receipt);
        var conflictEnvelope = SignReceipt(receipt with
        {
            ReceiptId = changedReceipt.ReceiptId,
            CommandId = changedReceipt.CommandId,
            LeaseId = changedReceipt.LeaseId,
            Attempt = changedReceipt.Attempt,
            SoulId = changedReceipt.SoulId,
            DeviceBindingId = changedReceipt.DeviceBindingId,
            PlatformAccountId = changedReceipt.PlatformAccountId,
            TraceId = changedReceipt.TraceId,
            IdempotencyKey = changedReceipt.IdempotencyKey,
            OccurredAt = changedReceipt.OccurredAt,
            PrivacyClass = changedReceipt.PrivacyClass,
            Receipt = changedReceipt,
            ReceiptSha256 = CommandReceiptProtocolV1.ComputeReceiptSha256(changedReceipt)
        });
        var conflict = service.RecordReceipt(conflictEnvelope);
        Assert.Equal(ReceiptDisposition.Quarantined, conflict.Disposition);
        Assert.Equal(CommandState.Pending, conflict.State);
        Assert.Equal(1, service.QuarantineCount);
    }

    private static (InMemoryCommandOrchestrator Service, SignedCommandReceiptV1 Receipt) ApplyFailedReceipt()
    {
        var service = Service();
        var id = service.Enqueue(Operation()).CommandId!.Value;
        var dispatch = service.AcquireLease(id, Soul, Device, Account, "worker", Now, TimeSpan.FromMinutes(1));
        service.MarkDispatched(id, dispatch.LeaseId, Authorization(dispatch), Now.AddSeconds(1));
        var receipt = Receipt(dispatch, CommandReceiptV1.Failed, retry: true, native: true, post: false);
        Assert.Equal(CommandState.Pending, service.RecordReceipt(receipt).State);
        return (service, receipt);
    }
}
