using System.Text;
using System.Text.Json;
using Dps.GBrainProjector.Contracts;
using Dps.InterestReducer;
using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulMemoryAdapter.Contracts;
using Dps.SoulRegistry.Contracts;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using DeterministicInterestReducer = Dps.InterestReducer.InterestReducer;

namespace Dps.SoulMemoryAdapter.Tests;

public sealed class SoulMemoryAdapterPostgresIntegrationTests
{
    private const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OfflineTarget = "local-postgresql-offline-projection-fixture";
    private const long FixtureNonce = 0;
    private static readonly DateTimeOffset BaseTime =
        new(2026, 7, 14, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProjectionTime = BaseTime.AddDays(1);
    private static readonly DateTimeOffset BindingTime = BaseTime.AddMinutes(-5);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealPostgresOutboxFlowsToOfflineExactReadback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await OfflineProjectionDatabase.CreateAsync(cancellationToken);
        var ledger = database.CreateLedger();
        var soul = CreateSoul('a');
        var memoryEvent = CreateEvent(soul, Guid.NewGuid(), "event-full-chain", 'c', "systems", 0.9m);

        var append = await ledger.AppendAsync(soul, memoryEvent, cancellationToken);
        var flow = await ProjectPersistedSoulAsync(
            database,
            ledger,
            soul,
            "full-chain",
            ProjectionTime,
            cancellationToken);

        Assert.Equal(AppendDisposition.Inserted, append.Disposition);
        Assert.Equal(SoulMemoryReadbackV1.PreparedStatus, flow.Prepared.Status);
        Assert.Equal(SoulMemoryReadbackV1.VerifiedStatus, flow.Verified.Status);
        Assert.Equal(flow.Projection.ProjectionChecksum, flow.Verified.ReadbackChecksum);
        Assert.Equal(OfflineProjectionWriteDisposition.Inserted, flow.WriteDisposition);
        Assert.False(flow.Readback.LiveGBrain);
        Assert.Equal(OfflineTarget, flow.Readback.VerificationTarget);

        var outbox = Assert.Single(flow.PendingOutbox);
        Assert.Equal(memoryEvent.EventId, outbox.EventId);
        Assert.Equal(MemoryEventCanonicalizer.ComputeSha256(memoryEvent), outbox.PayloadSha256);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TwoSoulsAndSourcesRemainIsolatedInPostgresAndReadback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await OfflineProjectionDatabase.CreateAsync(cancellationToken);
        var ledger = database.CreateLedger();
        var soulA = CreateSoul('a');
        var soulB = CreateSoul('b');

        await ledger.AppendAsync(
            soulA,
            CreateEvent(soulA, Guid.NewGuid(), "event-isolation-a", 'c', "robotics", 0.8m),
            cancellationToken);
        await ledger.AppendAsync(
            soulB,
            CreateEvent(soulB, Guid.NewGuid(), "event-isolation-b", 'd', "gardening", 0.7m),
            cancellationToken);

        var flowA = await ProjectPersistedSoulAsync(
            database,
            ledger,
            soulA,
            "isolation-a",
            ProjectionTime,
            cancellationToken);
        var flowB = await ProjectPersistedSoulAsync(
            database,
            ledger,
            soulB,
            "isolation-b",
            ProjectionTime,
            cancellationToken);

        Assert.NotEqual(flowA.Projection.SourceId, flowB.Projection.SourceId);
        Assert.NotEqual(flowA.Projection.ProjectionRevision, flowB.Projection.ProjectionRevision);
        Assert.All(flowA.PendingOutbox, item => Assert.Equal(SoulA, item.SoulId));
        Assert.All(flowB.PendingOutbox, item => Assert.Equal(SoulB, item.SoulId));
        Assert.Null(await database.TryReadProjectionAsync(
            flowA.Projection.SourceId,
            flowB.Projection.ProjectionRevision,
            cancellationToken));
        Assert.Null(await database.TryReadProjectionAsync(
            flowB.Projection.SourceId,
            flowA.Projection.ProjectionRevision,
            cancellationToken));
        Assert.Equal(
            [flowA.Projection.SourceId, flowB.Projection.SourceId],
            await database.ReadSourceIdsAsync(cancellationToken));
        Assert.Equal(2, await database.CountProjectionsAsync(cancellationToken: cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DuplicateDeliveryCreatesOneEventOutboxAndOfflineProjection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await OfflineProjectionDatabase.CreateAsync(cancellationToken);
        var ledger = database.CreateLedger();
        var soul = CreateSoul('a');
        var memoryEvent = CreateEvent(soul, Guid.NewGuid(), "event-duplicate", 'c', "databases", 0.9m);

        var firstAppend = await ledger.AppendAsync(soul, memoryEvent, cancellationToken);
        var duplicateAppend = await ledger.AppendAsync(soul, memoryEvent, cancellationToken);
        var firstFlow = await ProjectPersistedSoulAsync(
            database,
            ledger,
            soul,
            "duplicate",
            ProjectionTime,
            cancellationToken);
        var duplicateFlow = await ProjectPersistedSoulAsync(
            database,
            database.CreateLedger(),
            soul,
            "duplicate",
            ProjectionTime,
            cancellationToken);

        Assert.Equal(AppendDisposition.Inserted, firstAppend.Disposition);
        Assert.Equal(AppendDisposition.DuplicateNoOp, duplicateAppend.Disposition);
        Assert.Equal(OfflineProjectionWriteDisposition.Inserted, firstFlow.WriteDisposition);
        Assert.Equal(OfflineProjectionWriteDisposition.DuplicateNoOp, duplicateFlow.WriteDisposition);
        Assert.Equal(firstFlow.Verified, duplicateFlow.Verified);
        Assert.Equal(1, await ledger.CountEventsAsync(soul.SoulId, cancellationToken));
        Assert.Equal(1, await ledger.CountOutboxAsync(soul.SoulId, cancellationToken));
        Assert.Equal(1, await database.CountProjectionsAsync(soul.SoulId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConflictingEventIsQuarantinedWithoutProjectionOverwrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await OfflineProjectionDatabase.CreateAsync(cancellationToken);
        var ledger = database.CreateLedger();
        var soul = CreateSoul('a');
        var eventId = Guid.NewGuid();
        var original = CreateEvent(soul, eventId, "event-conflict", 'c', "security", 0.8m);
        var conflict = CreateEvent(soul, eventId, "event-conflict", 'd', "security", 0.8m);

        var firstAppend = await ledger.AppendAsync(soul, original, cancellationToken);
        var conflictingAppend = await ledger.AppendAsync(soul, conflict, cancellationToken);
        var flow = await ProjectPersistedSoulAsync(
            database,
            ledger,
            soul,
            "conflict",
            ProjectionTime,
            cancellationToken);

        Assert.Equal(AppendDisposition.Inserted, firstAppend.Disposition);
        Assert.Equal(AppendDisposition.Quarantined, conflictingAppend.Disposition);
        Assert.Equal(1, await ledger.CountEventsAsync(soul.SoulId, cancellationToken));
        Assert.Equal(1, await ledger.CountOutboxAsync(soul.SoulId, cancellationToken));
        Assert.Equal(1, await ledger.CountQuarantineAsync(cancellationToken));
        Assert.Equal(1, await database.CountProjectionsAsync(soul.SoulId, cancellationToken));
        Assert.Equal(original.Observation.ContentDigest, Assert.Single(flow.Projection.Events).ContentDigest);
        Assert.Equal(
            MemoryEventCanonicalizer.ComputeSha256(original),
            Assert.Single(flow.PendingOutbox).PayloadSha256);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OfflineDatabaseReadbackRejectsWrongScopeRevisionAndChecksum()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await OfflineProjectionDatabase.CreateAsync(cancellationToken);
        var ledger = database.CreateLedger();
        var soul = CreateSoul('a');
        await ledger.AppendAsync(
            soul,
            CreateEvent(soul, Guid.NewGuid(), "event-mismatch", 'c', "verification", 0.95m),
            cancellationToken);
        var flow = await ProjectPersistedSoulAsync(
            database,
            ledger,
            soul,
            "mismatch",
            ProjectionTime,
            cancellationToken);

        var adapter = new DeterministicSoulMemoryAdapter();
        var prepared = adapter.Prepare(flow.Readback.Projection);
        var exact = ObservationFrom(flow.Readback);
        var occurredAt = ProjectionTime.AddSeconds(1);

        AssertReadbackMismatch(adapter, prepared, exact with
        {
            SoulId = SoulB,
            SourceId = GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce)
        }, "wrong-soul", occurredAt);
        AssertReadbackMismatch(
            adapter,
            prepared,
            exact with { DeviceBindingId = "db_99999999999999999999999999999999" },
            "wrong-device",
            occurredAt);
        AssertReadbackMismatch(
            adapter,
            prepared,
            exact with { PlatformAccountId = "pa_88888888888888888888888888888888" },
            "wrong-account",
            occurredAt);
        AssertReadbackMismatch(
            adapter,
            prepared,
            exact with { SourceId = GBrainSourceIdCandidates.Compute(SoulB, FixtureNonce) },
            "wrong-source",
            occurredAt);
        AssertReadbackMismatch(
            adapter,
            prepared,
            exact with { ProjectionRevision = new string('e', 64) },
            "wrong-revision",
            occurredAt);
        AssertReadbackMismatch(
            adapter,
            prepared,
            exact with { ReadbackChecksum = new string('f', 64) },
            "wrong-checksum",
            occurredAt);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RestartAndReplayProduceTheSameProjectionAndReceipt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await OfflineProjectionDatabase.CreateAsync(cancellationToken);
        var soul = CreateSoul('a');
        var firstLedger = database.CreateLedger();
        await firstLedger.AppendAsync(
            soul,
            CreateEvent(soul, Guid.NewGuid(), "event-restart", 'c', "resilience", 0.85m),
            cancellationToken);
        var beforeRestart = await ProjectPersistedSoulAsync(
            database,
            firstLedger,
            soul,
            "restart",
            ProjectionTime,
            cancellationToken);

        var restartedLedger = database.CreateLedger();
        await restartedLedger.InitializeAsync(cancellationToken);
        var afterRestart = await ProjectPersistedSoulAsync(
            database,
            restartedLedger,
            soul,
            "restart",
            ProjectionTime,
            cancellationToken);

        Assert.Equal(OfflineProjectionWriteDisposition.Inserted, beforeRestart.WriteDisposition);
        Assert.Equal(OfflineProjectionWriteDisposition.DuplicateNoOp, afterRestart.WriteDisposition);
        Assert.Equal(
            GBrainProjectionV2Canonicalizer.Serialize(beforeRestart.Projection),
            GBrainProjectionV2Canonicalizer.Serialize(afterRestart.Projection));
        Assert.Equal(beforeRestart.Readback.CanonicalUtf8, afterRestart.Readback.CanonicalUtf8);
        Assert.Equal(beforeRestart.Verified, afterRestart.Verified);
        Assert.Equal(1, await database.CountProjectionsAsync(soul.SoulId, cancellationToken));
    }

    [Theory]
    [InlineData(AppendStage.EventInserted)]
    [InlineData(AppendStage.OutboxInserted)]
    [InlineData(AppendStage.BeforeCommit)]
    [Trait("Category", "Integration")]
    public async Task FailedLedgerTransactionLeavesNoPartialStateAndRecoverySucceeds(
        AppendStage failureStage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await OfflineProjectionDatabase.CreateAsync(cancellationToken);
        var soul = CreateSoul('a');
        var memoryEvent = CreateEvent(
            soul,
            Guid.NewGuid(),
            "event-recovery-" + failureStage,
            'c',
            "transactions",
            0.9m);
        var failingLedger = database.CreateLedger((stage, _) =>
            stage == failureStage
                ? ValueTask.FromException(new InvalidOperationException("Injected append failure."))
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingLedger.AppendAsync(soul, memoryEvent, cancellationToken));

        var recoveredLedger = database.CreateLedger();
        Assert.Equal(0, await recoveredLedger.CountEventsAsync(soul.SoulId, cancellationToken));
        Assert.Equal(0, await recoveredLedger.CountOutboxAsync(soul.SoulId, cancellationToken));
        Assert.Equal(0, await database.CountProjectionsAsync(soul.SoulId, cancellationToken));

        var recoveredAppend = await recoveredLedger.AppendAsync(soul, memoryEvent, cancellationToken);
        var flow = await ProjectPersistedSoulAsync(
            database,
            recoveredLedger,
            soul,
            "recovery-" + failureStage,
            ProjectionTime,
            cancellationToken);

        Assert.Equal(AppendDisposition.Inserted, recoveredAppend.Disposition);
        Assert.Equal(SoulMemoryReadbackV1.VerifiedStatus, flow.Verified.Status);
        Assert.Equal(1, await recoveredLedger.CountEventsAsync(soul.SoulId, cancellationToken));
        Assert.Equal(1, await recoveredLedger.CountOutboxAsync(soul.SoulId, cancellationToken));
        Assert.Equal(1, await database.CountProjectionsAsync(soul.SoulId, cancellationToken));
    }

    private static async Task<ProjectionFlowResult> ProjectPersistedSoulAsync(
        OfflineProjectionDatabase database,
        PostgresMemoryEventLedger ledger,
        SoulResolved soul,
        string flowKey,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        var persistedEvents = await ledger.ReadSoulEventsAsync(soul.SoulId, cancellationToken);
        var pendingOutbox = await ledger.ReadPendingOutboxAsync(soul.SoulId, cancellationToken);
        Assert.NotEmpty(persistedEvents);
        Assert.Equal(persistedEvents.Count, pendingOutbox.Count);

        foreach (var memoryEvent in persistedEvents)
        {
            var outbox = Assert.Single(pendingOutbox, item => item.EventId == memoryEvent.EventId);
            Assert.Equal(memoryEvent.SoulId, outbox.SoulId);
            Assert.Equal(memoryEvent.DeviceBindingId, outbox.DeviceBindingId);
            Assert.Equal(memoryEvent.PlatformAccountId, outbox.PlatformAccountId);
            Assert.Equal(memoryEvent.TraceId, outbox.TraceId);
            Assert.Equal(MemoryEventCanonicalizer.ComputeSha256(memoryEvent), outbox.PayloadSha256);
        }

        var snapshot = new DeterministicInterestReducer().Reduce(
            new InterestReductionRequest(
                soul.SoulId,
                soul.DeviceBindingId,
                soul.PlatformAccountId,
                soul.TraceId,
                CanonicalIdempotency("reduce-" + flowKey),
                asOf,
                persistedEvents),
            new InterestReducerOptions(86_400m));
        var projection = RenderOfflineProjection(persistedEvents, snapshot);
        var adapter = new DeterministicSoulMemoryAdapter();
        var prepared = adapter.Prepare(projection);
        var writeDisposition = await database.StoreProjectionAsync(projection, cancellationToken);
        var readback = await database.ReadProjectionAsync(
            projection.SourceId,
            projection.ProjectionRevision,
            cancellationToken);

        Assert.False(readback.LiveGBrain);
        Assert.Equal(OfflineTarget, readback.VerificationTarget);
        var verified = adapter.Verify(new VerifyReadbackCommand(
            prepared,
            ObservationFrom(readback),
            soul.TraceId,
            CanonicalIdempotency("verify-" + flowKey),
            asOf.AddSeconds(1)));

        return new ProjectionFlowResult(
            projection,
            prepared,
            verified,
            readback,
            writeDisposition,
            pendingOutbox);
    }

    /// <summary>
    /// Deterministic offline v2 projection fixture. The producer's renderer is no
    /// longer publicly constructible (its runtime demands a dedicated least-privilege
    /// PostgreSQL role pair), so this fixture assembles the same event/interest
    /// mapping from public contract APIs. Its correctness proof is the final
    /// projection.Validate(), which recomputes the source-binding and checksum chain.
    /// </summary>
    private static GBrainProjectionV2 RenderOfflineProjection(
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 snapshot)
    {
        snapshot.Validate();
        var binding = GBrainSourceBindingV1.Create(
            snapshot.SoulId,
            GBrainSourceIdCandidates.Compute(snapshot.SoulId, FixtureNonce),
            FixtureNonce,
            BindingTime);
        var uniqueEvents = new Dictionary<Guid, (MemoryEventV1 Event, string Hash)>();
        foreach (var memoryEvent in memoryEvents)
        {
            memoryEvent.Validate();
            var hash = MemoryEventCanonicalizer.ComputeSha256(memoryEvent);
            if (uniqueEvents.TryGetValue(memoryEvent.EventId, out var existing))
            {
                Assert.Equal(existing.Hash, hash);
                continue;
            }

            uniqueEvents.Add(memoryEvent.EventId, (memoryEvent, hash));
        }

        Assert.Equal(snapshot.SourceEventCount, uniqueEvents.Count);
        var projectedEvents = uniqueEvents.Values
            .Select(static item => new ProjectionEventV1(
                item.Event.EventId,
                item.Hash,
                item.Event.Observation.ContentDigest.ToLowerInvariant(),
                item.Event.OccurredAt))
            .OrderBy(static item => item.OccurredAt)
            .ThenBy(static item => item.EventId)
            .ToArray();
        var projectedInterests = snapshot.Interests
            .Select(static item => new ProjectionInterestV1(
                item.Topic,
                item.OriginalConfidence,
                item.DecayedConfidence,
                item.HalfLifeSeconds,
                item.AlgorithmVersion,
                item.Evidence.Select(static evidence => new ProjectionInterestEvidenceV1(
                        evidence.EventId,
                        evidence.EventHash.ToLowerInvariant(),
                        evidence.OccurredAt,
                        evidence.OriginalConfidence,
                        evidence.DecayedConfidence))
                    .OrderBy(static evidence => evidence.OccurredAt)
                    .ThenBy(static evidence => evidence.EventId)
                    .ToArray()))
            .OrderBy(static item => item.Topic, StringComparer.Ordinal)
            .ToArray();
        // Deterministic fixture revision derived from the binding proof and the exact
        // input set, so a replay over identical inputs yields an identical projection.
        var revision = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join(
                '\n',
                binding.BindingRevision,
                binding.BindingChecksum,
                snapshot.SoulId,
                snapshot.DeviceBindingId,
                snapshot.PlatformAccountId,
                InterestSnapshotCanonicalizer.ComputeSha256(snapshot),
                string.Join(
                    ',',
                    projectedEvents.Select(static item => item.EventId + ":" + item.EventHash))))));
        var withoutChecksum = new GBrainProjectionV2(
            GBrainProjectionV2.CurrentSchemaVersion,
            GBrainProjectionV2.CurrentContractId,
            GBrainProjectionV2.CurrentProducerModule,
            snapshot.SoulId,
            snapshot.DeviceBindingId,
            snapshot.PlatformAccountId,
            snapshot.TraceId,
            snapshot.IdempotencyKey,
            snapshot.AsOf,
            snapshot.PrivacyClass,
            binding.SourceId,
            binding.Algorithm,
            binding.Nonce,
            binding.SoulHash,
            binding.AllocatedAt,
            binding.BindingRevision,
            binding.BindingChecksum,
            revision,
            new string('0', 64),
            GBrainProjectionV2.RenderedNotWrittenStatus,
            projectedEvents.Length,
            projectedEvents,
            projectedInterests);
        var projection = withoutChecksum with
        {
            ProjectionChecksum = GBrainProjectionV2Canonicalizer.ComputeSha256(withoutChecksum)
        };
        projection.Validate();
        return projection;
    }

    private static GBrainReadbackObservation ObservationFrom(OfflineProjectionReadback readback)
    {
        if (readback.LiveGBrain || !string.Equals(
                readback.VerificationTarget,
                OfflineTarget,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The local integration fixture cannot produce a live GBrain observation.");
        }

        var projection = readback.Projection;
        return new GBrainReadbackObservation(
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            projection.SchemaVersion,
            projection.ContractId,
            projection.ProjectionRevision,
            projection.ProjectionChecksum);
    }

    private static void AssertReadbackMismatch(
        DeterministicSoulMemoryAdapter adapter,
        SoulMemoryReadbackV1 prepared,
        GBrainReadbackObservation readback,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        Assert.Throws<GBrainReadbackMismatchException>(() => adapter.Verify(new VerifyReadbackCommand(
            prepared,
            readback,
            prepared.TraceId,
            CanonicalIdempotency(idempotencyKey),
            occurredAt)));
    }

    private static SoulResolved CreateSoul(char discriminator)
    {
        var soulId = discriminator switch
        {
            'a' => SoulA,
            'b' => SoulB,
            _ => throw new ArgumentOutOfRangeException(nameof(discriminator))
        };
        var digestCharacter = discriminator == 'a' ? 'c' : 'd';
        return new SoulResolved(
            soulId,
            "db_" + new string(discriminator, 32),
            "pa_" + new string(discriminator, 32),
            "trace_" + new string(discriminator, 32),
            "idem_" + new string(discriminator, 64),
            BaseTime.AddMinutes(-1),
            "email",
            new string(digestCharacter, 64),
            "integration-test-key-v1");
    }

    private static MemoryEventV1 CreateEvent(
        SoulResolved soul,
        Guid eventId,
        string idempotencyKey,
        char contentDigestCharacter,
        string topic,
        decimal confidence)
    {
        return new MemoryEventV1(
            MemoryEventV1.CurrentSchemaVersion,
            MemoryEventV1.CurrentContractId,
            MemoryEventV1.CurrentProducerModule,
            eventId,
            soul.SoulId,
            soul.DeviceBindingId,
            soul.PlatformAccountId,
            soul.TraceId,
            CanonicalIdempotency(idempotencyKey),
            BaseTime,
            "personal",
            MemoryEventV1.ObservedContentEventType,
            new MemoryObservationV1(
                new string(contentDigestCharacter, 64),
                true,
                [new InterestSignalV1(topic, confidence)]));
    }

    private sealed record ProjectionFlowResult(
        GBrainProjectionV2 Projection,
        SoulMemoryReadbackV1 Prepared,
        SoulMemoryReadbackV1 Verified,
        OfflineProjectionReadback Readback,
        OfflineProjectionWriteDisposition WriteDisposition,
        IReadOnlyList<MemoryOutboxV1> PendingOutbox);

    private static string CanonicalIdempotency(string discriminator) =>
        "idem_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(discriminator)));

    private sealed record OfflineProjectionReadback(
        GBrainProjectionV2 Projection,
        byte[] CanonicalUtf8,
        string VerificationTarget,
        bool LiveGBrain);

    private enum OfflineProjectionWriteDisposition
    {
        Inserted,
        DuplicateNoOp
    }

    private sealed class OfflineProjectionDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;

        private OfflineProjectionDatabase(string connectionString)
        {
            _connectionString = connectionString;
            SchemaName = "dps_sma_" + Guid.NewGuid().ToString("N");
        }

        public string SchemaName { get; }

        public static async Task<OfflineProjectionDatabase> CreateAsync(
            CancellationToken cancellationToken)
        {
            var connectionString = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES is required. The real PostgreSQL Integration suite fails rather than skips when it is unavailable.");
            }

            var database = new OfflineProjectionDatabase(connectionString);
            try
            {
                await database.InitializeAsync(cancellationToken);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public PostgresMemoryEventLedger CreateLedger(AppendFaultInjector? faultInjector = null)
        {
            return new PostgresMemoryEventLedger(
                new MemoryEventLedgerOptions(_connectionString, SchemaName),
                faultInjector);
        }

        public async Task<OfflineProjectionWriteDisposition> StoreProjectionAsync(
            GBrainProjectionV2 projection,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(projection);
            projection.Validate();
            var canonicalUtf8 = Encoding.UTF8.GetBytes(
                GBrainProjectionV2Canonicalizer.Serialize(projection));

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {SchemaName}.offline_projection_fixture
                    (source_id, soul_id, device_binding_id, platform_account_id,
                     projection_schema_version, projection_contract_id,
                     projection_revision, projection_checksum, canonical_utf8,
                     verification_target, live_gbrain)
                VALUES
                    (@source_id, @soul_id, @device_binding_id, @platform_account_id,
                     @projection_schema_version, @projection_contract_id,
                     @projection_revision, @projection_checksum, @canonical_utf8,
                     @verification_target, false)
                ON CONFLICT (source_id, projection_revision) DO NOTHING
                """,
                connection);
            command.Parameters.AddWithValue("source_id", projection.SourceId);
            command.Parameters.AddWithValue("soul_id", projection.SoulId);
            command.Parameters.AddWithValue("device_binding_id", projection.DeviceBindingId);
            command.Parameters.AddWithValue("platform_account_id", projection.PlatformAccountId);
            command.Parameters.AddWithValue("projection_schema_version", projection.SchemaVersion);
            command.Parameters.AddWithValue("projection_contract_id", projection.ContractId);
            command.Parameters.AddWithValue("projection_revision", projection.ProjectionRevision);
            command.Parameters.AddWithValue("projection_checksum", projection.ProjectionChecksum);
            command.Parameters.AddWithValue("canonical_utf8", NpgsqlDbType.Bytea, canonicalUtf8);
            command.Parameters.AddWithValue("verification_target", OfflineTarget);
            var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;

            if (inserted)
            {
                return OfflineProjectionWriteDisposition.Inserted;
            }

            var existing = await ReadProjectionAsync(
                projection.SourceId,
                projection.ProjectionRevision,
                cancellationToken);
            if (!canonicalUtf8.AsSpan().SequenceEqual(existing.CanonicalUtf8) ||
                existing.LiveGBrain ||
                !string.Equals(existing.VerificationTarget, OfflineTarget, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Conflicting offline projection reuse failed closed.");
            }

            return OfflineProjectionWriteDisposition.DuplicateNoOp;
        }

        public async Task<OfflineProjectionReadback> ReadProjectionAsync(
            string sourceId,
            string projectionRevision,
            CancellationToken cancellationToken)
        {
            return await TryReadProjectionAsync(sourceId, projectionRevision, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The exact offline projection Source and revision were not found.");
        }

        public async Task<OfflineProjectionReadback?> TryReadProjectionAsync(
            string sourceId,
            string projectionRevision,
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"""
                SELECT source_id, soul_id, device_binding_id, platform_account_id,
                       projection_schema_version, projection_contract_id,
                       projection_revision, projection_checksum, canonical_utf8,
                       verification_target, live_gbrain
                FROM {SchemaName}.offline_projection_fixture
                WHERE source_id = @source_id AND projection_revision = @projection_revision
                """,
                connection);
            command.Parameters.AddWithValue("source_id", sourceId);
            command.Parameters.AddWithValue("projection_revision", projectionRevision);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var storedSourceId = reader.GetString(0);
            var storedSoulId = reader.GetString(1);
            var storedDeviceBindingId = reader.GetString(2);
            var storedPlatformAccountId = reader.GetString(3);
            var storedProjectionSchemaVersion = reader.GetString(4);
            var storedProjectionContractId = reader.GetString(5);
            var storedProjectionRevision = reader.GetString(6);
            var storedProjectionChecksum = reader.GetString(7);
            var canonicalUtf8 = reader.GetFieldValue<byte[]>(8);
            var verificationTarget = reader.GetString(9);
            var liveGBrain = reader.GetBoolean(10);

            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "An exact offline Source and revision query returned multiple rows.");
            }

            var projection = JsonSerializer.Deserialize<GBrainProjectionV2>(canonicalUtf8)
                ?? throw new InvalidOperationException(
                    "Stored offline projection bytes could not be deserialized.");
            projection.Validate();
            var recanonicalized = Encoding.UTF8.GetBytes(
                GBrainProjectionV2Canonicalizer.Serialize(projection));

            if (!canonicalUtf8.AsSpan().SequenceEqual(recanonicalized) ||
                !string.Equals(storedSourceId, projection.SourceId, StringComparison.Ordinal) ||
                !string.Equals(storedSoulId, projection.SoulId, StringComparison.Ordinal) ||
                !string.Equals(storedDeviceBindingId, projection.DeviceBindingId, StringComparison.Ordinal) ||
                !string.Equals(storedPlatformAccountId, projection.PlatformAccountId, StringComparison.Ordinal) ||
                !string.Equals(storedProjectionSchemaVersion, projection.SchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(storedProjectionContractId, projection.ContractId, StringComparison.Ordinal) ||
                !string.Equals(storedProjectionRevision, projection.ProjectionRevision, StringComparison.Ordinal) ||
                !string.Equals(storedProjectionChecksum, projection.ProjectionChecksum, StringComparison.Ordinal) ||
                liveGBrain ||
                !string.Equals(verificationTarget, OfflineTarget, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Offline projection columns, canonical bytes, or evidence boundary do not match.");
            }

            return new OfflineProjectionReadback(
                projection,
                canonicalUtf8,
                verificationTarget,
                liveGBrain);
        }

        public async Task<long> CountProjectionsAsync(
            string? soulId = null,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var predicate = soulId is null ? string.Empty : " WHERE soul_id = @soul_id";
            await using var command = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM {SchemaName}.offline_projection_fixture{predicate}",
                connection);
            if (soulId is not null)
            {
                command.Parameters.AddWithValue("soul_id", soulId);
            }

            return (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "PostgreSQL did not return the offline projection count."));
        }

        public async Task<IReadOnlyList<string>> ReadSourceIdsAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"SELECT DISTINCT source_id FROM {SchemaName}.offline_projection_fixture ORDER BY source_id",
                connection);
            var results = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE",
                connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await CreateLedger().InitializeAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $$"""
                CREATE TABLE {{SchemaName}}.offline_projection_fixture (
                    source_id text NOT NULL CHECK (char_length(source_id) = 32 AND source_id ~ '^dps-[0-9a-f]{28}$'),
                    soul_id text NOT NULL CHECK (char_length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
                    device_binding_id text NOT NULL CHECK (char_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
                    platform_account_id text NOT NULL CHECK (char_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[0-9a-f]{32}$'),
                    projection_schema_version text NOT NULL CHECK (projection_schema_version ~ '^2([.][0-9]+){0,2}$'),
                    projection_contract_id text NOT NULL CHECK (projection_contract_id = 'gbrain.projection/v2'),
                    projection_revision text NOT NULL CHECK (projection_revision ~ '^[0-9a-f]{64}$'),
                    projection_checksum text NOT NULL CHECK (projection_checksum ~ '^[0-9a-f]{64}$'),
                    canonical_utf8 bytea NOT NULL,
                    verification_target text NOT NULL CHECK (verification_target = '{{OfflineTarget}}'),
                    live_gbrain boolean NOT NULL CHECK (live_gbrain = false),
                    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    PRIMARY KEY (source_id, projection_revision)
                    -- v2 source_id is a domain-separated SHA-256 over (soul, nonce); it
                    -- is not SQL-derivable, so the source binding is proven by
                    -- GBrainProjectionV2.Validate() on every store and read path.
                )
                """,
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<NpgsqlConnection> OpenConnectionAsync(
            CancellationToken cancellationToken)
        {
            var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }
}
