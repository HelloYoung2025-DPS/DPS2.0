using System.Globalization;
using System.Security.Cryptography;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeSupervisor.Contracts;
using Dps.WindowsEdgeWorker;
using Xunit;

namespace Dps.WindowsEdgeWorker.Tests;

public sealed class WorkerDrainReceiptIssuerTests
{
    private static readonly DateTimeOffset Start =
        DateTimeOffset.ParseExact(
            "2026-07-15T08:00:00.0000000+00:00",
            "O",
            CultureInfo.InvariantCulture);

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Signed_directive_produces_independent_exact_Worker_wire_only_after_durable_Journal_receipt()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var supervisorKey = RSA.Create(2048);
        using var workerSigner = new TestWorkerDrainSigningAuthority();
        using var workerPublicKey = workerSigner.CreatePublicKey();
        var expectation = Expectation();
        var directive = CreateDirective(supervisorKey, expectation, Start, TimeSpan.FromMinutes(2));
        await using var receiptStore = DurableWorkerDrainReceiptStore.Open(runtime.Path);
        await using var journal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: false);
        var processor = Processor(journal);
        var issuer = new WorkerDrainReceiptIssuer(
            processor,
            journal,
            receiptStore,
            workerSigner,
            new SequenceTimeProvider(Start, Start.AddSeconds(3)));

        var result = await issuer.IssueAsync(
            directive,
            expectation,
            supervisorKey,
            TestContext.Current.CancellationToken);

        var verified = WorkerDrainReceiptContractCodec.DecodeAndVerify(
            result.ExactWorkerReceiptWireUtf8,
            ReceiptExpectation(expectation),
            workerPublicKey,
            Start.AddSeconds(3));
        Assert.Equal(result.WorkerReceiptWireSha256, verified.WireSha256);
        Assert.Equal("worker-drain-" + expectation.DrainId["drain-".Length..], result.JournalEntryId);
        Assert.Equal(1, result.JournalSequence);
        Assert.Equal(1, workerSigner.SignCalls);
        Assert.DoesNotContain("journal_receipt", System.Text.Encoding.UTF8.GetString(
            result.ExactWorkerReceiptWireUtf8), StringComparison.Ordinal);
        var persisted = receiptStore.ReadExisting(expectation.DrainId);
        Assert.NotNull(persisted);
        Assert.Equal(WorkerDrainReceiptPersistenceState.Committed, persisted.State);
        Assert.Equal(result.ExactWorkerReceiptWireUtf8, persisted.ExactWireUtf8);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Restart_after_durable_append_reuses_exact_randomized_PSS_wire_and_commits_duplicate_receipt()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var supervisorKey = RSA.Create(2048);
        using var workerSigner = new TestWorkerDrainSigningAuthority();
        var expectation = Expectation();
        var directive = CreateDirective(supervisorKey, expectation, Start, TimeSpan.FromMinutes(2));
        byte[] preparedWire;

        await using (var receiptStore = DurableWorkerDrainReceiptStore.Open(runtime.Path))
        await using (var journal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: true))
        {
            var issuer = new WorkerDrainReceiptIssuer(
                Processor(journal),
                journal,
                receiptStore,
                workerSigner,
                new SequenceTimeProvider(Start, Start.AddSeconds(1)));
            await Assert.ThrowsAsync<IOException>(() => issuer.IssueAsync(
                directive,
                expectation,
                supervisorKey,
                TestContext.Current.CancellationToken));
            var prepared = receiptStore.ReadExisting(expectation.DrainId);
            Assert.NotNull(prepared);
            Assert.Equal(WorkerDrainReceiptPersistenceState.Prepared, prepared.State);
            preparedWire = prepared.ExactWireUtf8;
            Assert.Equal(1, workerSigner.SignCalls);
        }

        await using (var receiptStore = DurableWorkerDrainReceiptStore.Open(runtime.Path))
        await using (var journal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: false))
        {
            var retryDirective = CreateDirective(
                supervisorKey,
                expectation,
                Start,
                TimeSpan.FromMinutes(2));
            Assert.NotEqual(directive, retryDirective);
            var issuer = new WorkerDrainReceiptIssuer(
                Processor(journal),
                journal,
                receiptStore,
                workerSigner,
                new SequenceTimeProvider(Start.AddMinutes(10)));
            var invalidEnvelope = DrainDirectiveV1Codec.Deserialize(retryDirective) with
            {
                Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(256))
            };
            var invalidRetryDirective = DrainDirectiveV1Codec.Serialize(invalidEnvelope);
            await Assert.ThrowsAsync<CryptographicException>(() => issuer.IssueAsync(
                invalidRetryDirective,
                expectation,
                supervisorKey,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                WorkerDrainReceiptPersistenceState.Prepared,
                receiptStore.ReadExisting(expectation.DrainId)!.State);

            var result = await issuer.IssueAsync(
                retryDirective,
                expectation,
                supervisorKey,
                TestContext.Current.CancellationToken);

            Assert.Equal(preparedWire, result.ExactWorkerReceiptWireUtf8);
            Assert.Equal(1, workerSigner.SignCalls);
            Assert.Equal(1, result.JournalSequence);
            Assert.Equal(
                WorkerDrainReceiptPersistenceState.Committed,
                receiptStore.ReadExisting(expectation.DrainId)!.State);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Same_drain_with_changed_signed_epoch_is_durably_quarantined()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var supervisorKey = RSA.Create(2048);
        using var workerSigner = new TestWorkerDrainSigningAuthority();
        var expectation = Expectation();
        var firstDirective = CreateDirective(supervisorKey, expectation, Start, TimeSpan.FromMinutes(2));

        await using (var receiptStore = DurableWorkerDrainReceiptStore.Open(runtime.Path))
        await using (var journal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: true))
        {
            var issuer = new WorkerDrainReceiptIssuer(
                Processor(journal),
                journal,
                receiptStore,
                workerSigner,
                new SequenceTimeProvider(Start, Start.AddSeconds(1)));
            await Assert.ThrowsAsync<IOException>(() => issuer.IssueAsync(
                firstDirective,
                expectation,
                supervisorKey,
                TestContext.Current.CancellationToken));
        }

        var changedExpectation = expectation with { RoutingEpoch = 18 };
        var secondDirective = CreateDirective(
            supervisorKey,
            changedExpectation,
            Start,
            TimeSpan.FromMinutes(2));
        Assert.NotEqual(firstDirective, secondDirective);
        await using var recoveredStore = DurableWorkerDrainReceiptStore.Open(runtime.Path);
        await using var recoveredJournal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: false);
        var recoveredIssuer = new WorkerDrainReceiptIssuer(
            Processor(recoveredJournal),
            recoveredJournal,
            recoveredStore,
            workerSigner,
            new SequenceTimeProvider(Start.AddSeconds(30)));

        await Assert.ThrowsAsync<WorkerDrainReceiptConflictException>(() =>
            recoveredIssuer.IssueAsync(
                secondDirective,
                changedExpectation,
                supervisorKey,
                TestContext.Current.CancellationToken));
        Assert.Throws<WorkerDrainReceiptConflictException>(() =>
            recoveredStore.ReadExisting(expectation.DrainId));
        Assert.Equal(1, workerSigner.SignCalls);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public async Task Invalid_scope_and_expired_directive_fail_before_stopping_intake_or_signing()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var supervisorKey = RSA.Create(2048);
        using var workerSigner = new TestWorkerDrainSigningAuthority();
        var expectation = Expectation();
        var directive = CreateDirective(supervisorKey, expectation, Start, TimeSpan.FromMinutes(1));
        await using var receiptStore = DurableWorkerDrainReceiptStore.Open(runtime.Path);
        await using var journal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: false);
        var processor = Processor(journal);
        var issuer = new WorkerDrainReceiptIssuer(
            processor,
            journal,
            receiptStore,
            workerSigner,
            new SequenceTimeProvider(Start.AddMinutes(2)));

        await Assert.ThrowsAsync<InvalidDataException>(() => issuer.IssueAsync(
            directive,
            expectation,
            supervisorKey,
            TestContext.Current.CancellationToken));
        Assert.False(processor.IsDrained);
        Assert.Equal(0, workerSigner.SignCalls);
        Assert.Null(receiptStore.ReadExisting(expectation.DrainId));

        var wrongScope = expectation with { Slot = "B" };
        var currentIssuer = new WorkerDrainReceiptIssuer(
            processor,
            journal,
            receiptStore,
            workerSigner,
            new SequenceTimeProvider(Start));
        await Assert.ThrowsAsync<InvalidDataException>(() => currentIssuer.IssueAsync(
            directive,
            wrongScope,
            supervisorKey,
            TestContext.Current.CancellationToken));
        Assert.False(processor.IsDrained);
        Assert.Equal(0, workerSigner.SignCalls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("EvidenceKind", "SIMULATION")]
    public async Task Receipt_uses_fresh_completion_window_after_long_drain_but_clock_rollback_fails_closed()
    {
        using var supervisorKey = RSA.Create(2048);
        using var workerSigner = new TestWorkerDrainSigningAuthority();
        using var workerPublicKey = workerSigner.CreatePublicKey();
        var expectation = Expectation();
        var directive = CreateDirective(supervisorKey, expectation, Start, TimeSpan.FromMinutes(1));

        using (var runtime = new DrainReceiptTemporaryRuntimeDirectory())
        {
            await using var receiptStore = DurableWorkerDrainReceiptStore.Open(runtime.Path);
            await using var journal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: false);
            var completion = Start.AddMinutes(10);
            var issuer = new WorkerDrainReceiptIssuer(
                Processor(journal),
                journal,
                receiptStore,
                workerSigner,
                new SequenceTimeProvider(Start, completion));

            var result = await issuer.IssueAsync(
                directive,
                expectation,
                supervisorKey,
                TestContext.Current.CancellationToken);
            var verified = WorkerDrainReceiptContractCodec.DecodeAndVerify(
                result.ExactWorkerReceiptWireUtf8,
                ReceiptExpectation(expectation),
                workerPublicKey,
                completion);
            Assert.Equal(Format(completion), verified.Envelope.IssuedAt);
            Assert.Equal(Format(completion), verified.Envelope.NotBefore);
            Assert.Equal(Format(completion.AddMinutes(4)), verified.Envelope.ExpiresAt);
        }

        using (var rollbackRuntime = new DrainReceiptTemporaryRuntimeDirectory())
        {
            await using var rollbackStore = DurableWorkerDrainReceiptStore.Open(rollbackRuntime.Path);
            await using var rollbackJournal = await OpenJournalAsync(
                rollbackRuntime.Path,
                failAfterFirstDurableAppend: false);
            var rollbackIssuer = new WorkerDrainReceiptIssuer(
                Processor(rollbackJournal),
                rollbackJournal,
                rollbackStore,
                workerSigner,
                new SequenceTimeProvider(Start, Start.AddTicks(-1)));

            await Assert.ThrowsAsync<InvalidDataException>(() => rollbackIssuer.IssueAsync(
                directive,
                expectation,
                supervisorKey,
                TestContext.Current.CancellationToken));
            Assert.Null(rollbackStore.ReadExisting(expectation.DrainId));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Non_UTC_TimeProvider_fails_before_any_drain_state_change()
    {
        using var runtime = new DrainReceiptTemporaryRuntimeDirectory();
        using var supervisorKey = RSA.Create(2048);
        using var workerSigner = new TestWorkerDrainSigningAuthority();
        var expectation = Expectation();
        var directive = CreateDirective(supervisorKey, expectation, Start, TimeSpan.FromMinutes(1));
        await using var receiptStore = DurableWorkerDrainReceiptStore.Open(runtime.Path);
        await using var journal = await OpenJournalAsync(runtime.Path, failAfterFirstDurableAppend: false);
        var processor = Processor(journal);
        var nonUtc = Start.ToOffset(TimeSpan.FromHours(8));
        var issuer = new WorkerDrainReceiptIssuer(
            processor,
            journal,
            receiptStore,
            workerSigner,
            new SequenceTimeProvider(nonUtc));

        await Assert.ThrowsAsync<InvalidDataException>(() => issuer.IssueAsync(
            directive,
            expectation,
            supervisorKey,
            TestContext.Current.CancellationToken));
        Assert.False(processor.IsDrained);
        Assert.Null(receiptStore.ReadExisting(expectation.DrainId));
        Assert.Equal(0, workerSigner.SignCalls);
    }

    private static CommandProcessor Processor(EdgeLocalJournalAdapter journal) => new(
        new NeverDispatchTransport(),
        journal,
        new InMemoryCommandStateStore(),
        TimeProvider.System,
        WorkerRuntimeMode.Simulation);

    private static async Task<EdgeLocalJournalAdapter> OpenJournalAsync(
        string runtimeDirectory,
        bool failAfterFirstDurableAppend)
    {
        var store = await JournalStore.OpenAsync(
            System.IO.Path.Combine(runtimeDirectory, "worker-journal.jsonl"));
        IJournalAppendClient client = failAfterFirstDurableAppend
            ? new ThrowAfterDurableAppendClient(store)
            : new NarrowJournalClient(store);
        try
        {
            return EdgeLocalJournalAdapter.Bind(client, runtimeDirectory, ownsStore: true);
        }
        catch
        {
            await store.DisposeAsync();
            throw;
        }
    }

    private static DrainDirectiveExpectationV1 Expectation() => new(
        "drain-" + new string('a', 64),
        "A",
        "1.2.3",
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        new string('4', 64),
        17,
        "soul_" + new string('5', 64),
        "db_" + new string('6', 32),
        "pa_" + new string('7', 32),
        "trace_" + new string('8', 32),
        "idem_" + new string('9', 64),
        Format(Start.AddSeconds(-1)));

    private static WorkerDrainReceiptExpectationV1 ReceiptExpectation(
        DrainDirectiveExpectationV1 expectation) => new(
        expectation.DrainId,
        expectation.Slot,
        expectation.WorkerVersion,
        expectation.WorkerArtifactSha256,
        expectation.JournalArtifactSha256,
        expectation.ReleaseBomSha256,
        expectation.ProtectedPolicySha256,
        expectation.RoutingEpoch,
        expectation.SoulId,
        expectation.DeviceBindingId,
        expectation.PlatformAccountId,
        expectation.TraceId,
        expectation.IdempotencyKey,
        expectation.OccurredAt);

    private static byte[] CreateDirective(
        RSA supervisorKey,
        DrainDirectiveExpectationV1 expectation,
        DateTimeOffset issuedAt,
        TimeSpan validFor)
    {
        var keyId = DrainDirectiveV1Codec.ComputeKeyId(supervisorKey);
        var claims = new DrainDirectiveClaimsV1(
            DrainDirectiveV1Codec.SchemaVersion,
            DrainDirectiveV1Codec.ContractId,
            DrainDirectiveV1Codec.ProducerModule,
            expectation.SoulId,
            expectation.DeviceBindingId,
            expectation.PlatformAccountId,
            expectation.TraceId,
            expectation.IdempotencyKey,
            expectation.OccurredAt,
            "internal",
            expectation.DrainId,
            expectation.Slot,
            expectation.WorkerVersion,
            expectation.WorkerArtifactSha256,
            expectation.JournalArtifactSha256,
            expectation.ReleaseBomSha256,
            expectation.ProtectedPolicySha256,
            expectation.RoutingEpoch,
            Format(issuedAt),
            Format(issuedAt),
            Format(issuedAt.Add(validFor)),
            keyId,
            DrainDirectiveV1Codec.SignatureAlgorithm);
        var statement = DrainDirectiveV1Codec.CreateSigningStatement(claims);
        var signature = supervisorKey.SignData(
            statement,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        try
        {
            return DrainDirectiveV1Codec.Serialize(
                DrainDirectiveV1Codec.AttachSignature(
                    claims,
                    Convert.ToBase64String(signature)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statement);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string Format(DateTimeOffset value) => value.ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
        CultureInfo.InvariantCulture);

    private sealed class NeverDispatchTransport : INativeTransport
    {
        public Task<NativeDispatchResult> DispatchAsync(
            WorkerCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<NativeDispatchResult>(new InvalidOperationException(
                "drain receipt tests never dispatch native actions"));
    }

    private sealed class ThrowAfterDurableAppendClient(JournalStore store) :
        IJournalAppendClient,
        IJournalReadiness,
        IAsyncDisposable
    {
        private int _fail = 1;

        public int Count => store.Count;

        public bool IsQuarantined => store.IsQuarantined;

        public async Task<JournalReceipt> AppendAsync(
            JournalAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            var receipt = await store.AppendAsync(request, cancellationToken);
            if (Interlocked.Exchange(ref _fail, 0) == 1)
                throw new IOException("injected crash after durable Journal append");
            return receipt;
        }

        public ValueTask DisposeAsync() => store.DisposeAsync();
    }

    private sealed class TestWorkerDrainSigningAuthority :
        IWorkerDrainSigningAuthority,
        IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);

        public string KeyId => WorkerDrainReceiptContractCodec.ComputeKeyId(_key);

        public int SignCalls { get; private set; }

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> canonicalStatement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignCalls++;
            return ValueTask.FromResult(_key.SignData(
                canonicalStatement.Span,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss));
        }

        public bool Verify(
            ReadOnlySpan<byte> canonicalStatement,
            ReadOnlySpan<byte> signature) =>
            _key.VerifyData(
                canonicalStatement,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);

        public RSA CreatePublicKey()
        {
            var publicKey = RSA.Create();
            publicKey.ImportSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo(), out _);
            return publicKey;
        }

        public void Dispose() => _key.Dispose();
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly Queue<DateTimeOffset> _values = new(values);
        private DateTimeOffset _last = values.Length == 0
            ? throw new ArgumentException("at least one time value is required", nameof(values))
            : values[^1];

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                if (_values.Count > 0)
                    _last = _values.Dequeue();
                return _last;
            }
        }
    }
}
