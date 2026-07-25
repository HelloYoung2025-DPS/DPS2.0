using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.OperationCompiler.Contracts;
using Dps.PolicyApproval.Contracts;
using Xunit;

namespace Dps.OperationCompiler.Tests;

public sealed class OperationCompilerIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-01-01T00:05:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void SignedActiveApprovalTraversesProductionPortCompilerAndStrictCommandWire()
    {
        using var fixture = new IntegrationFixture();
        var request = fixture.Authority.Write(Approval(
            "fixture.type",
            true,
            new Dictionary<string, string>
            {
                ["selector_ref"] = "fixture.input",
                ["value_ref"] = "fixture.value"
            }));
        var consumer = new DurableStrictCommandConsumer(fixture.CommandDirectory);

        var operation = CompileAndAccept(fixture.Boundary(request, consumer), request);

        Assert.Equal("operation.compiled/v1", operation.ContractId);
        Assert.Equal("fixture.type", Assert.Single(operation.Steps).StepKind);
        Assert.Equal(request.SoulId, operation.SoulId);
        Assert.Equal(request.DeviceBindingId, operation.DeviceBindingId);
        Assert.Equal(request.PlatformAccountId, operation.PlatformAccountId);
        Assert.Equal(1, consumer.AcceptedWrites);
        Assert.Equal(operation.OperationId, consumer.ReadCommitted().OperationId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void ForgedDeniedShadowExpiredAndRevokedApprovalsFailClosed()
    {
        using var fixture = new IntegrationFixture();
        var request = fixture.Authority.Write(Approval("observe"));
        fixture.Authority.CorruptSignature();
        Assert.Throws<UnauthorizedAccessException>(() =>
            CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request));

        var denied = Approval("observe") with
        {
            Decision = ApprovalDecisionV1.Denied,
            DenialReasons = ["POLICY_DENIED"]
        };
        request = fixture.Authority.Write(denied);
        Assert.Throws<UnauthorizedAccessException>(() =>
            CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request));

        var approved = Approval("observe");
        var approvedRequest = Request(approved);
        var shadowWire = ApprovalDecisionV1Codec.Serialize(approved);
        try
        {
            var shadowText = StrictUtf8.GetString(shadowWire).Replace(
                "\"shadow_only\":false",
                "\"shadow_only\":true",
                StringComparison.Ordinal);
            fixture.Authority.WriteRaw(
                approvedRequest,
                StrictUtf8.GetBytes(shadowText),
                approvedRequest.ApprovalSha256);
            Assert.NotNull(Record.Exception(() =>
                CompileAndAccept(fixture.Boundary(approvedRequest, new CapturingConsumer()), approvedRequest)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shadowWire);
        }

        request = fixture.Authority.Write(
            Approval("observe"),
            validUntil: Now.AddTicks(-1));
        Assert.Throws<UnauthorizedAccessException>(() =>
            CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request));

        request = fixture.Authority.Write(
            Approval("observe"),
            status: "REVOKED");
        Assert.Throws<UnauthorizedAccessException>(() =>
            CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request));

        request = fixture.Authority.Write(
            Approval("observe"),
            statusRevision: SignedApprovalAuthority.ExpectedStatusRevision + 1);
        Assert.Throws<UnauthorizedAccessException>(() =>
            CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request));

        request = fixture.Authority.Write(
            Approval("observe"),
            runtimeRevision: SignedApprovalAuthority.ExpectedRuntimeRevision + 1);
        Assert.Throws<UnauthorizedAccessException>(() =>
            CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request));

        request = fixture.Authority.Write(
            Approval("observe"),
            runtimeStateSha256: new string('c', 64));
        Assert.Throws<UnauthorizedAccessException>(() =>
            CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void SignedReceiptCannotCrossSoulDeviceAccountTraceOrApprovalLookupScope()
    {
        using var fixture = new IntegrationFixture();
        var request = fixture.Authority.Write(Approval("observe"));
        var changedRequests = new[]
        {
            request with { SoulId = "soul_ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" },
            request with { DeviceBindingId = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            request with { PlatformAccountId = "pa_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            request with { TraceId = "trace_cccccccccccccccccccccccccccccccc" },
            request with { ApprovalId = Guid.Parse("51000000-0000-0000-0000-000000000099") }
        };

        foreach (var changed in changedRequests)
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                CompileAndAccept(fixture.Boundary(changed, new CapturingConsumer()), changed));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void UnknownMajorActionStepAndCoordinateFallbackFailAtStrictBoundaries()
    {
        using var fixture = new IntegrationFixture();
        var baseline = Approval("observe");
        var request = Request(baseline);
        AssertSignedApprovalWireRejected(fixture, baseline, request,
            text => text.Replace("\"schema_version\":\"1.0.0\"", "\"schema_version\":\"2.0.0\"", StringComparison.Ordinal));
        AssertSignedApprovalWireRejected(fixture, baseline, request,
            text => text.Replace("\"action_kind\":\"observe\"", "\"action_kind\":\"shell\"", StringComparison.Ordinal));

        var tap = Approval(
            "fixture.tap",
            true,
            new Dictionary<string, string> { ["selector_ref"] = "fixture.button" });
        AssertSignedApprovalWireRejected(fixture, tap, Request(tap),
            text => text.Replace(
                "\"parameters\":{\"selector_ref\":\"fixture.button\"}",
                "\"parameters\":{\"x\":\"10\",\"y\":\"20\"}",
                StringComparison.Ordinal));

        var malformedSemanticApprovals = new[]
        {
            Approval("wait", parameters: new Dictionary<string, string> { ["duration_ms"] = "01" }),
            Approval("fixture.tap", true, new Dictionary<string, string> { ["selector_ref"] = "tap the submit button" }),
            Approval("fixture.tap", true, new Dictionary<string, string> { ["selector_ref"] = "x=10,y=20" }),
            Approval("fixture.type", true, new Dictionary<string, string>
            {
                ["selector_ref"] = "s" + new string('a', 128),
                ["value_ref"] = "fixture.value"
            })
        };
        foreach (var malformed in malformedSemanticApprovals)
        {
            var malformedRequest = fixture.Authority.Write(malformed);
            Assert.Throws<ArgumentException>(() =>
                CompileAndAccept(fixture.Boundary(malformedRequest, new CapturingConsumer()), malformedRequest));
        }

        var validRequest = fixture.Authority.Write(baseline);
        var capture = new CapturingConsumer();
        _ = CompileAndAccept(fixture.Boundary(validRequest, capture), validRequest);
        var unknownStep = StrictUtf8.GetString(capture.Wire!).Replace(
            "\"step_kind\":\"ui.observe\"",
            "\"step_kind\":\"coordinate.tap\"",
            StringComparison.Ordinal);
        var strictConsumer = new DurableStrictCommandConsumer(fixture.CommandDirectory);
        Assert.Throws<NotSupportedException>(() => Accept(strictConsumer, StrictUtf8.GetBytes(unknownStep)));
        Assert.Equal(0, strictConsumer.AcceptedWrites);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void ExactReplayIsDeterministicNoOpAndConflictingReplayIsQuarantined()
    {
        using var fixture = new IntegrationFixture();
        var approval = Approval("observe");
        var request = fixture.Authority.Write(approval);
        var firstConsumer = new DurableStrictCommandConsumer(fixture.CommandDirectory);
        var first = CompileAndAccept(fixture.Boundary(request, firstConsumer), request);

        var replayConsumer = new DurableStrictCommandConsumer(fixture.CommandDirectory);
        var replay = CompileAndAccept(fixture.Boundary(request, replayConsumer), request);
        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal(first.Steps[0].StepId, replay.Steps[0].StepId);
        Assert.Equal(1, replayConsumer.DuplicateNoOps);
        Assert.Equal(0, replayConsumer.AcceptedWrites);

        var conflictingApproval = approval with { PolicyVersion = "1.0.1" };
        var conflictingRequest = fixture.Authority.Write(conflictingApproval);
        var conflictingConsumer = new DurableStrictCommandConsumer(fixture.CommandDirectory);
        Assert.Throws<InvalidOperationException>(() =>
            CompileAndAccept(fixture.Boundary(conflictingRequest, conflictingConsumer), conflictingRequest));
        Assert.Equal(1, conflictingConsumer.QuarantinedConflicts);
        Assert.Equal(first.OperationId, new DurableStrictCommandConsumer(fixture.CommandDirectory).ReadCommitted().OperationId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void CrashAfterDurablePendingRecoversOnRestartWithoutDuplicateOrDivergence()
    {
        using var fixture = new IntegrationFixture();
        var request = fixture.Authority.Write(Approval("observe"));
        var crashingConsumer = new DurableStrictCommandConsumer(
            fixture.CommandDirectory,
            crashAfterPendingFlush: true);

        Assert.Throws<SimulatedCrashException>(() =>
            CompileAndAccept(fixture.Boundary(request, crashingConsumer), request));
        Assert.True(crashingConsumer.HasPending);
        Assert.False(crashingConsumer.HasCommitted);

        var restartedConsumer = new DurableStrictCommandConsumer(fixture.CommandDirectory);
        var recovered = CompileAndAccept(fixture.Boundary(request, restartedConsumer), request);
        Assert.False(restartedConsumer.HasPending);
        Assert.True(restartedConsumer.HasCommitted);
        Assert.Equal(1, restartedConsumer.AcceptedWrites);
        Assert.Equal(recovered.OperationId, restartedConsumer.ReadCommitted().OperationId);

        var secondRestart = new DurableStrictCommandConsumer(fixture.CommandDirectory);
        var replay = CompileAndAccept(fixture.Boundary(request, secondRestart), request);
        Assert.Equal(recovered.OperationId, replay.OperationId);
        Assert.Equal(1, secondRestart.DuplicateNoOps);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void MutableCallerCollectionsCannotChangeSignedReceiptOrImmutableResult()
    {
        using var fixture = new IntegrationFixture();
        var parameters = new Dictionary<string, string>
        {
            ["selector_ref"] = "fixture.input",
            ["value_ref"] = "original"
        };
        var policies = new List<string> { "SOUL-ISO-001", "CMD-IDEMP-001", "RESULT-VERIFY-001" };
        var approval = Approval("fixture.type", true, parameters) with { EvaluatedPolicyIds = policies };
        var request = fixture.Authority.Write(approval);
        parameters["value_ref"] = "mutated";
        policies.Clear();

        var firstCapture = new CapturingConsumer();
        var first = CompileAndAccept(fixture.Boundary(request, firstCapture), request);
        var secondCapture = new CapturingConsumer();
        var second = CompileAndAccept(fixture.Boundary(request, secondCapture), request);

        Assert.Equal("original", first.Steps[0].Arguments["value_ref"]);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(first.Steps[0].StepId, second.Steps[0].StepId);
        Assert.Equal(firstCapture.Wire, secondCapture.Wire);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)first.Steps[0].Arguments)["value_ref"] = "late-mutation");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public void StrictConsumerRejectsDuplicateNonCanonicalAndInvalidUtf8WithoutSuccess()
    {
        using var fixture = new IntegrationFixture();
        var request = fixture.Authority.Write(Approval("observe"));
        var capture = new CapturingConsumer();
        _ = CompileAndAccept(fixture.Boundary(request, capture), request);
        var valid = StrictUtf8.GetString(capture.Wire!);

        var duplicate = StrictUtf8.GetBytes(valid.Insert(1, "\"schema_version\":\"1.0.0\","));
        var whitespace = StrictUtf8.GetBytes(valid + "\n");
        var invalidUtf8 = new byte[] { 0xff, 0xfe, 0xfd };
        var consumer = new DurableStrictCommandConsumer(fixture.CommandDirectory);
        try
        {
            Assert.Throws<System.Text.Json.JsonException>(() => Accept(consumer, duplicate));
            Assert.NotNull(Record.Exception(() => Accept(consumer, whitespace)));
            Assert.Throws<DecoderFallbackException>(() => Accept(consumer, invalidUtf8));
            Assert.Equal(0, consumer.AcceptedWrites);
            Assert.False(consumer.HasCommitted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(duplicate);
            CryptographicOperations.ZeroMemory(whitespace);
            CryptographicOperations.ZeroMemory(invalidUtf8);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public async Task ApprovalReadPastDeadlineIsCancelledQuarantinedAndNeverReachesConsumer()
    {
        var approval = Approval("observe");
        var request = Request(approval);
        var snapshot = new AuthoritativeApprovalSnapshotV1(
            approval,
            request.ApprovalSha256,
            AuthoritativeApprovalSnapshotV1.Active);
        var reader = new ControlledAuthoritativeApprovalReader();
        var consumer = new CapturingConsumer();
        var quarantine = new RecordingOperationBoundaryQuarantine();
        var boundary = new OperationCompilationBoundary(
            new AllowlistedOperationCompiler(reader),
            consumer,
            quarantine,
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => boundary.CompileAndAcceptAsync(request, TestContext.Current.CancellationToken));
        Assert.True(reader.CancellationToken.IsCancellationRequested);
        reader.Complete(snapshot);
        await boundary.DrainLateQuarantinesAsync(TestContext.Current.CancellationToken);

        Assert.Null(consumer.Wire);
        var outcome = Assert.Single(quarantine.Outcomes);
        Assert.Equal("authoritative-approval-read", outcome.Phase);
        Assert.Equal("DEADLINE_EXCEEDED", outcome.Trigger);
        Assert.Equal("CANCELLED", outcome.TerminalState);
        Assert.Equal(request.ApprovalId, outcome.ApprovalId);
        Assert.Null(outcome.OperationId);
        Assert.Null(outcome.OperationWireSha256);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "LocalCryptographicStorageSimulation")]
    public async Task CommandAcceptPastDeadlineCannotReturnSuccessAndLateCompletionIsQuarantined()
    {
        var approval = Approval("observe");
        var request = Request(approval);
        var snapshot = new AuthoritativeApprovalSnapshotV1(
            approval,
            request.ApprovalSha256,
            AuthoritativeApprovalSnapshotV1.Active);
        var reader = new ImmediateAuthoritativeApprovalReader(snapshot);
        var consumer = new ControlledCommandConsumer();
        var quarantine = new RecordingOperationBoundaryQuarantine();
        var boundary = new OperationCompilationBoundary(
            new AllowlistedOperationCompiler(reader),
            consumer,
            quarantine,
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => boundary.CompileAndAcceptAsync(request, TestContext.Current.CancellationToken));
        Assert.NotNull(consumer.Wire);
        Assert.True(consumer.CancellationToken.IsCancellationRequested);
        consumer.Complete();
        await boundary.DrainLateQuarantinesAsync(TestContext.Current.CancellationToken);

        var outcome = Assert.Single(quarantine.Outcomes);
        Assert.Equal("command-accept", outcome.Phase);
        Assert.Equal("DEADLINE_EXCEEDED", outcome.Trigger);
        Assert.Equal("COMPLETED", outcome.TerminalState);
        Assert.NotNull(outcome.OperationId);
        Assert.Equal(64, outcome.OperationWireSha256?.Length);
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static CompiledOperationV1 CompileAndAccept(
        OperationCompilationBoundary boundary,
        ApprovalCompilationRequestV1 request)
        => boundary.CompileAndAcceptAsync(request).GetAwaiter().GetResult();

    private static void Accept(
        ICompiledOperationCommandConsumer consumer,
        ReadOnlyMemory<byte> wire)
        => consumer.AcceptAsync(wire, CancellationToken.None).GetAwaiter().GetResult();

    private static void AssertSignedApprovalWireRejected(
        IntegrationFixture fixture,
        ApprovalDecisionV1 approval,
        ApprovalCompilationRequestV1 request,
        Func<string, string> mutate)
    {
        var wire = ApprovalDecisionV1Codec.Serialize(approval);
        try
        {
            var mutated = StrictUtf8.GetBytes(mutate(StrictUtf8.GetString(wire)));
            try
            {
                fixture.Authority.WriteRaw(request, mutated, request.ApprovalSha256);
                Assert.NotNull(Record.Exception(() =>
                    CompileAndAccept(fixture.Boundary(request, new CapturingConsumer()), request)));
            }
            finally { CryptographicOperations.ZeroMemory(mutated); }
        }
        finally { CryptographicOperations.ZeroMemory(wire); }
    }

    private static ApprovalDecisionV1 Approval(
        string action,
        bool sideEffect = false,
        IReadOnlyDictionary<string, string>? parameters = null) => new(
        ApprovalDecisionV1.CurrentSchemaVersion,
        ApprovalDecisionV1.CurrentContractId,
        ApprovalDecisionV1.CurrentProducerModule,
        Guid.Parse("51000000-0000-0000-0000-000000000001"),
        Guid.Parse("52000000-0000-0000-0000-000000000002"),
        "soul_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
        "db_11111111111111111111111111111111",
        "pa_22222222222222222222222222222222",
        "trace_33333333333333333333333333333333",
        "idem_4444444444444444444444444444444444444444444444444444444444444444",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        "internal",
        action,
        sideEffect,
        false,
        parameters ?? new Dictionary<string, string>(),
        ApprovalDecisionV1.Approved,
        ApprovalDecisionV1.DeterministicAuthority,
        "1.0.0",
        ["SOUL-ISO-001", "CMD-IDEMP-001", "RESULT-VERIFY-001"],
        sideEffect ? "platform-auth:test" : null,
        []);

    private static ApprovalCompilationRequestV1 Request(ApprovalDecisionV1 approval)
        => new(
            approval.ApprovalId,
            approval.ProposalId,
            approval.SoulId,
            approval.DeviceBindingId,
            approval.PlatformAccountId,
            approval.TraceId,
            approval.IdempotencyKey,
            ApprovalSnapshotV1Canonical.ComputeSha256(approval));

    private sealed class IntegrationFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "dps-operation-compiler-" + Guid.NewGuid().ToString("N"));

        public IntegrationFixture()
        {
            Directory.CreateDirectory(_root);
            Authority = new SignedApprovalAuthority(Path.Combine(_root, "authority"), Now);
            CommandDirectory = Path.Combine(_root, "commands");
        }

        public SignedApprovalAuthority Authority { get; }
        public string CommandDirectory { get; }

        public OperationCompilationBoundary Boundary(
            ApprovalCompilationRequestV1 request,
            ICompiledOperationCommandConsumer consumer)
        {
            _ = request;
            var reader = new SignedFileAuthoritativeApprovalReader(
                Authority.ReceiptPath,
                Authority.SubjectPublicKeyInfo,
                Authority.KeyId,
                Now,
                SignedApprovalAuthority.ExpectedStatusRevision,
                SignedApprovalAuthority.ExpectedRuntimeRevision,
                SignedApprovalAuthority.ExpectedRuntimeStateSha256,
                SignedApprovalAuthority.ExpectedReleaseBomSha256);
            return new OperationCompilationBoundary(
                new AllowlistedOperationCompiler(reader),
                consumer,
                new RecordingOperationBoundaryQuarantine());
        }

        public void Dispose()
        {
            Authority.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class SignedApprovalAuthority : IDisposable
    {
        private const string Algorithm = "ECDSA-P256-SHA256-P1363";
        private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly DateTimeOffset _now;

        public const long ExpectedStatusRevision = 1;
        public const long ExpectedRuntimeRevision = 7;
        public const string ExpectedRuntimeStateSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string ExpectedReleaseBomSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        public SignedApprovalAuthority(string directory, DateTimeOffset now)
        {
            Directory.CreateDirectory(directory);
            ReceiptPath = Path.Combine(directory, "approval-receipt.bin");
            _now = now;
        }

        public string ReceiptPath { get; }
        public string KeyId => "policy-test-p256-v1";
        public byte[] SubjectPublicKeyInfo => _signer.ExportSubjectPublicKeyInfo();

        public ApprovalCompilationRequestV1 Write(
            ApprovalDecisionV1 approval,
            string status = AuthoritativeApprovalSnapshotV1.Active,
            DateTimeOffset? validUntil = null,
            long statusRevision = ExpectedStatusRevision,
            long runtimeRevision = ExpectedRuntimeRevision,
            string runtimeStateSha256 = ExpectedRuntimeStateSha256,
            string releaseBomSha256 = ExpectedReleaseBomSha256)
        {
            var wire = ApprovalDecisionV1Codec.Serialize(approval);
            try
            {
                var snapshot = ApprovalDecisionV1Codec.Deserialize(wire);
                var request = Request(snapshot);
                WriteRaw(
                    request,
                    wire,
                    request.ApprovalSha256,
                    status,
                    validUntil,
                    statusRevision,
                    runtimeRevision,
                    runtimeStateSha256,
                    releaseBomSha256);
                return request;
            }
            finally { CryptographicOperations.ZeroMemory(wire); }
        }

        public void WriteRaw(
            ApprovalCompilationRequestV1 request,
            ReadOnlySpan<byte> decisionWire,
            string canonicalSha256,
            string status = AuthoritativeApprovalSnapshotV1.Active,
            DateTimeOffset? validUntil = null,
            long statusRevision = ExpectedStatusRevision,
            long runtimeRevision = ExpectedRuntimeRevision,
            string runtimeStateSha256 = ExpectedRuntimeStateSha256,
            string releaseBomSha256 = ExpectedReleaseBomSha256)
        {
            var frame = new ApprovalReceiptFrame(
                status,
                statusRevision,
                validUntil ?? _now.AddHours(1),
                runtimeRevision,
                runtimeStateSha256,
                releaseBomSha256,
                canonicalSha256,
                KeyId,
                Algorithm,
                decisionWire.ToArray(),
                []);
            var signed = ApprovalReceiptFrameCodec.CanonicalSignatureBytes(request, frame);
            byte[] signature;
            try
            {
                signature = _signer.SignData(
                    signed,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            finally { CryptographicOperations.ZeroMemory(signed); }
            var complete = frame with { Signature = signature };
            try { ApprovalReceiptFrameCodec.Write(ReceiptPath, complete); }
            finally
            {
                CryptographicOperations.ZeroMemory(frame.DecisionWire);
                CryptographicOperations.ZeroMemory(signature);
            }
        }

        public void CorruptSignature()
        {
            var bytes = File.ReadAllBytes(ReceiptPath);
            try
            {
                bytes[^1] ^= 0x01;
                File.WriteAllBytes(ReceiptPath, bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public void Dispose() => _signer.Dispose();
    }

    private sealed class SignedFileAuthoritativeApprovalReader : IAuthoritativeApprovalReader
    {
        private const string Algorithm = "ECDSA-P256-SHA256-P1363";
        private readonly string _receiptPath;
        private readonly byte[] _subjectPublicKeyInfo;
        private readonly string _keyId;
        private readonly DateTimeOffset _now;
        private readonly long _expectedStatusRevision;
        private readonly long _expectedRuntimeRevision;
        private readonly string _expectedRuntimeStateSha256;
        private readonly string _expectedReleaseBomSha256;

        public SignedFileAuthoritativeApprovalReader(
            string receiptPath,
            byte[] subjectPublicKeyInfo,
            string keyId,
            DateTimeOffset now,
            long expectedStatusRevision,
            long expectedRuntimeRevision,
            string expectedRuntimeStateSha256,
            string expectedReleaseBomSha256)
        {
            _receiptPath = receiptPath;
            _subjectPublicKeyInfo = subjectPublicKeyInfo.ToArray();
            _keyId = keyId;
            _now = now;
            _expectedStatusRevision = expectedStatusRevision;
            _expectedRuntimeRevision = expectedRuntimeRevision;
            _expectedRuntimeStateSha256 = expectedRuntimeStateSha256;
            _expectedReleaseBomSha256 = expectedReleaseBomSha256;
        }

        public Task<AuthoritativeApprovalSnapshotV1> ReadAsync(
            ApprovalCompilationRequestV1 request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = ApprovalReceiptFrameCodec.Read(_receiptPath);
            try
            {
                if (!string.Equals(frame.KeyId, _keyId, StringComparison.Ordinal)
                    || !string.Equals(frame.Algorithm, Algorithm, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Unknown approval receipt signing key or algorithm.");
                if (!string.Equals(frame.Status, AuthoritativeApprovalSnapshotV1.Active, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Only an ACTIVE approval receipt is accepted.");
                if (frame.StatusRevision != _expectedStatusRevision
                    || frame.RuntimeRevision != _expectedRuntimeRevision)
                    throw new UnauthorizedAccessException("Approval receipt does not match the pinned current state revisions.");
                if (frame.ValidUntil.Offset != TimeSpan.Zero || frame.ValidUntil <= _now)
                    throw new UnauthorizedAccessException("Approval receipt is expired or not canonical UTC.");
                OperationContractGuard.RequireSha256(frame.CanonicalSha256, nameof(frame.CanonicalSha256));
                OperationContractGuard.RequireSha256(frame.RuntimeStateSha256, nameof(frame.RuntimeStateSha256));
                OperationContractGuard.RequireSha256(frame.ReleaseBomSha256, nameof(frame.ReleaseBomSha256));
                if (!FixedDigestEquals(frame.RuntimeStateSha256, _expectedRuntimeStateSha256)
                    || !FixedDigestEquals(frame.ReleaseBomSha256, _expectedReleaseBomSha256))
                    throw new UnauthorizedAccessException("Approval receipt runtime state or Release BOM is stale.");
                if (frame.Signature.Length != 64)
                    throw new UnauthorizedAccessException("Approval receipt signature is not P-256 P1363.");

                var canonical = ApprovalReceiptFrameCodec.CanonicalSignatureBytes(request, frame);
                try
                {
                    using var verifier = ECDsa.Create();
                    verifier.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out var read);
                    if (read != _subjectPublicKeyInfo.Length
                        || verifier.KeySize != 256
                        || !verifier.VerifyData(
                            canonical,
                            frame.Signature,
                            HashAlgorithmName.SHA256,
                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                        throw new UnauthorizedAccessException("Approval receipt signature verification failed.");
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }

                var approval = ApprovalDecisionV1Codec.Deserialize(frame.DecisionWire);
                var digest = ApprovalSnapshotV1Canonical.ComputeSha256(approval);
                if (!FixedDigestEquals(digest, frame.CanonicalSha256)
                    || !FixedDigestEquals(digest, request.ApprovalSha256))
                    throw new UnauthorizedAccessException("Signed approval receipt digest does not match the request.");
                if (frame.ValidUntil <= approval.OccurredAt)
                    throw new UnauthorizedAccessException("Approval validity must follow the decision time.");
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AuthoritativeApprovalSnapshotV1(
                    approval,
                    digest,
                    AuthoritativeApprovalSnapshotV1.Active));
            }
            finally { frame.Clear(); }
        }

        private static bool FixedDigestEquals(string left, string right)
        {
            byte[]? leftBytes = null;
            byte[]? rightBytes = null;
            try
            {
                leftBytes = Convert.FromHexString(left);
                rightBytes = Convert.FromHexString(right);
                return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
            }
            catch (FormatException) { return false; }
            finally
            {
                if (leftBytes is not null) CryptographicOperations.ZeroMemory(leftBytes);
                if (rightBytes is not null) CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
    }

    private sealed record ApprovalReceiptFrame(
        string Status,
        long StatusRevision,
        DateTimeOffset ValidUntil,
        long RuntimeRevision,
        string RuntimeStateSha256,
        string ReleaseBomSha256,
        string CanonicalSha256,
        string KeyId,
        string Algorithm,
        byte[] DecisionWire,
        byte[] Signature)
    {
        public void Clear()
        {
            CryptographicOperations.ZeroMemory(DecisionWire);
            CryptographicOperations.ZeroMemory(Signature);
        }
    }

    private static class ApprovalReceiptFrameCodec
    {
        private static readonly byte[] Magic = "DPSAPR01"u8.ToArray();
        private const int MaximumFrameBytes = 128 * 1024;
        private const int MaximumDecisionBytes = 64 * 1024;
        private const string SignatureDomain = "dps.operation-compiler.signed-approval-read/v1";

        public static void Write(string path, ApprovalReceiptFrame frame)
        {
            using var buffer = new MemoryStream();
            buffer.Write(Magic);
            WriteText(buffer, frame.Status);
            WriteInt64(buffer, frame.StatusRevision);
            WriteText(buffer, frame.ValidUntil.ToString("O", CultureInfo.InvariantCulture));
            WriteInt64(buffer, frame.RuntimeRevision);
            WriteText(buffer, frame.RuntimeStateSha256);
            WriteText(buffer, frame.ReleaseBomSha256);
            WriteText(buffer, frame.CanonicalSha256);
            WriteText(buffer, frame.KeyId);
            WriteText(buffer, frame.Algorithm);
            WriteBytes(buffer, frame.DecisionWire);
            WriteBytes(buffer, frame.Signature);
            if (buffer.Length > MaximumFrameBytes) throw new InvalidOperationException("Approval receipt frame is oversized.");
            using var file = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            buffer.Position = 0;
            buffer.CopyTo(file);
            file.Flush(flushToDisk: true);
        }

        public static ApprovalReceiptFrame Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumFrameBytes)
                throw new UnauthorizedAccessException("Approval receipt frame is missing or oversized.");
            var bytes = File.ReadAllBytes(path);
            try
            {
                var offset = 0;
                RequireBytes(bytes, ref offset, Magic);
                var frame = new ApprovalReceiptFrame(
                    ReadText(bytes, ref offset, 16),
                    ReadInt64(bytes, ref offset),
                    DateTimeOffset.ParseExact(
                        ReadText(bytes, ref offset, 40),
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None),
                    ReadInt64(bytes, ref offset),
                    ReadText(bytes, ref offset, 64),
                    ReadText(bytes, ref offset, 64),
                    ReadText(bytes, ref offset, 64),
                    ReadText(bytes, ref offset, 64),
                    ReadText(bytes, ref offset, 64),
                    ReadBytes(bytes, ref offset, MaximumDecisionBytes),
                    ReadBytes(bytes, ref offset, 64));
                if (offset != bytes.Length)
                {
                    frame.Clear();
                    throw new UnauthorizedAccessException("Approval receipt frame has trailing data.");
                }
                return frame;
            }
            catch (Exception exception) when (exception is not UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException("Approval receipt frame is malformed.", exception);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public static byte[] CanonicalSignatureBytes(
            ApprovalCompilationRequestV1 request,
            ApprovalReceiptFrame frame)
        {
            using var stream = new MemoryStream();
            WriteText(stream, SignatureDomain);
            WriteText(stream, request.ApprovalId.ToString("D"));
            WriteText(stream, request.ProposalId.ToString("D"));
            WriteText(stream, request.SoulId);
            WriteText(stream, request.DeviceBindingId);
            WriteText(stream, request.PlatformAccountId);
            WriteText(stream, request.TraceId);
            WriteText(stream, request.IdempotencyKey);
            WriteText(stream, request.ApprovalSha256);
            WriteText(stream, frame.Status);
            WriteText(stream, frame.StatusRevision.ToString(CultureInfo.InvariantCulture));
            WriteText(stream, frame.ValidUntil.ToString("O", CultureInfo.InvariantCulture));
            WriteText(stream, frame.RuntimeRevision.ToString(CultureInfo.InvariantCulture));
            WriteText(stream, frame.RuntimeStateSha256);
            WriteText(stream, frame.ReleaseBomSha256);
            WriteText(stream, frame.CanonicalSha256);
            WriteText(stream, frame.KeyId);
            WriteText(stream, frame.Algorithm);
            WriteText(stream, Convert.ToHexString(SHA256.HashData(frame.DecisionWire)).ToLowerInvariant());
            return stream.ToArray();
        }

        private static void WriteText(Stream stream, string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            try { WriteBytes(stream, bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private static void WriteBytes(Stream stream, ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
            stream.Write(length);
            stream.Write(value);
        }

        private static void WriteInt64(Stream stream, long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static long ReadInt64(byte[] bytes, ref int offset)
        {
            if (bytes.Length - offset < sizeof(long)) throw new InvalidDataException("Truncated Int64.");
            var value = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset, sizeof(long)));
            offset += sizeof(long);
            return value;
        }

        private static string ReadText(byte[] bytes, ref int offset, int maximumBytes)
        {
            var value = ReadBytes(bytes, ref offset, maximumBytes);
            try { return StrictUtf8.GetString(value); }
            finally { CryptographicOperations.ZeroMemory(value); }
        }

        private static byte[] ReadBytes(byte[] bytes, ref int offset, int maximumBytes)
        {
            if (bytes.Length - offset < sizeof(uint)) throw new InvalidDataException("Truncated field length.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)));
            offset += sizeof(uint);
            if (length > maximumBytes || length > bytes.Length - offset)
                throw new InvalidDataException("Field length exceeds its bound.");
            var value = bytes.AsSpan(offset, checked((int)length)).ToArray();
            offset += checked((int)length);
            return value;
        }

        private static void RequireBytes(byte[] bytes, ref int offset, ReadOnlySpan<byte> expected)
        {
            if (bytes.Length - offset < expected.Length
                || !bytes.AsSpan(offset, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException("Approval receipt magic is invalid.");
            offset += expected.Length;
        }
    }

    private sealed class ImmediateAuthoritativeApprovalReader(
        AuthoritativeApprovalSnapshotV1 snapshot) : IAuthoritativeApprovalReader
    {
        public Task<AuthoritativeApprovalSnapshotV1> ReadAsync(
            ApprovalCompilationRequestV1 request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ControlledAuthoritativeApprovalReader : IAuthoritativeApprovalReader
    {
        private readonly TaskCompletionSource<AuthoritativeApprovalSnapshotV1> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken { get; private set; }

        public Task<AuthoritativeApprovalSnapshotV1> ReadAsync(
            ApprovalCompilationRequestV1 request,
            CancellationToken cancellationToken)
        {
            _ = request;
            CancellationToken = cancellationToken;
            // Deliberately ignore cancellation to prove that the production
            // boundary, not adapter goodwill, owns the deadline and quarantine.
            return _completion.Task;
        }

        public void Complete(AuthoritativeApprovalSnapshotV1 snapshot)
            => _completion.TrySetResult(snapshot);
    }

    private sealed class ControlledCommandConsumer : ICompiledOperationCommandConsumer
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[]? Wire { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task AcceptAsync(
            ReadOnlyMemory<byte> canonicalOperationWire,
            CancellationToken cancellationToken)
        {
            Wire = canonicalOperationWire.Span.ToArray();
            CancellationToken = cancellationToken;
            // Deliberately ignore cancellation. The caller times out, then the
            // eventual successful terminal state must be routed to quarantine.
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class RecordingOperationBoundaryQuarantine : IOperationBoundaryQuarantine
    {
        private readonly object _gate = new();
        private readonly List<LateOperationBoundaryOutcome> _outcomes = [];

        public IReadOnlyList<LateOperationBoundaryOutcome> Outcomes
        {
            get
            {
                lock (_gate) return _outcomes.ToArray();
            }
        }

        public Task QuarantineAsync(
            LateOperationBoundaryOutcome outcome,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingConsumer : ICompiledOperationCommandConsumer
    {
        public byte[]? Wire { get; private set; }
        public Task AcceptAsync(
            ReadOnlyMemory<byte> canonicalOperationWire,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Wire = canonicalOperationWire.Span.ToArray();
            _ = OperationCompiledV1Json.Deserialize(StrictUtf8.GetString(Wire));
            return Task.CompletedTask;
        }
    }

    private sealed class DurableStrictCommandConsumer : ICompiledOperationCommandConsumer
    {
        private const int MaximumWireBytes = 64 * 1024;
        private readonly string _directory;
        private readonly bool _crashAfterPendingFlush;
        private string PendingPath => Path.Combine(_directory, "operation.pending");
        private string CommittedPath => Path.Combine(_directory, "operation.committed");

        public DurableStrictCommandConsumer(string directory, bool crashAfterPendingFlush = false)
        {
            _directory = directory;
            _crashAfterPendingFlush = crashAfterPendingFlush;
            Directory.CreateDirectory(directory);
        }

        public int AcceptedWrites { get; private set; }
        public int DuplicateNoOps { get; private set; }
        public int QuarantinedConflicts { get; private set; }
        public bool HasPending => File.Exists(PendingPath);
        public bool HasCommitted => File.Exists(CommittedPath);

        public Task AcceptAsync(
            ReadOnlyMemory<byte> canonicalOperationWire,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canonicalOperationWire.Length is <= 0 or > MaximumWireBytes)
                throw new InvalidDataException("Compiled operation wire exceeds the command boundary budget.");
            var candidate = canonicalOperationWire.Span.ToArray();
            try
            {
                var text = StrictUtf8.GetString(candidate);
                var operation = OperationCompiledV1Json.Deserialize(text);
                var canonical = StrictUtf8.GetBytes(OperationCompiledV1Json.Serialize(operation));
                try
                {
                    if (!candidate.AsSpan().SequenceEqual(canonical))
                        throw new InvalidDataException("Compiled operation wire is not canonical.");
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }

                if (File.Exists(CommittedPath))
                {
                    if (FileEquals(CommittedPath, candidate))
                    {
                        DuplicateNoOps++;
                        return Task.CompletedTask;
                    }
                    Quarantine(candidate);
                    throw new InvalidOperationException("Conflicting operation replay was quarantined.");
                }
                if (File.Exists(PendingPath))
                {
                    if (!FileEquals(PendingPath, candidate))
                    {
                        Quarantine(candidate);
                        throw new InvalidOperationException("Conflicting crash-window replay was quarantined.");
                    }
                }
                else
                {
                    WriteDurable(PendingPath, candidate);
                }
                if (_crashAfterPendingFlush) throw new SimulatedCrashException();
                File.Move(PendingPath, CommittedPath);
                AcceptedWrites++;
                return Task.CompletedTask;
            }
            finally { CryptographicOperations.ZeroMemory(candidate); }
        }

        public CompiledOperationV1 ReadCommitted()
        {
            var bytes = File.ReadAllBytes(CommittedPath);
            try { return OperationCompiledV1Json.Deserialize(StrictUtf8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private void Quarantine(byte[] candidate)
        {
            var digest = Convert.ToHexString(SHA256.HashData(candidate)).ToLowerInvariant();
            WriteDurable(Path.Combine(_directory, "quarantine-" + digest + ".bin"), candidate);
            QuarantinedConflicts++;
        }

        private static bool FileEquals(string path, byte[] candidate)
        {
            var existing = File.ReadAllBytes(path);
            try { return existing.AsSpan().SequenceEqual(candidate); }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }

        private static void WriteDurable(string path, byte[] bytes)
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException() : base("Simulated crash after durable pending write.") { }
    }
}
