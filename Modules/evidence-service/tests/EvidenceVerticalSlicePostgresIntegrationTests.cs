using System.Security.Cryptography;
using System.Text;
using Dps.EvidenceService.Contracts;
using Dps.GBrainProjector;
using Dps.GBrainProjector.Contracts;
using Dps.InterestReducer;
using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry;
using Npgsql;
using DeterministicInterestReducer = Dps.InterestReducer.InterestReducer;

namespace Dps.EvidenceService.Tests;

public sealed class EvidenceVerticalSlicePostgresIntegrationTests
{
    private const long FixtureNonce = 0;
    private static readonly DateTimeOffset FixtureBindingTime =
        new(2026, 7, 14, 1, 55, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_postgres_18_4_proves_scoped_replayable_vertical_slice_and_signed_evidence()
    {
        var connectionString = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "DPS_TEST_POSTGRES is required. Required integration tests fail rather than skip when PostgreSQL is unavailable.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = Guid.NewGuid().ToString("N");
        var soulSchema = $"dps_f2_soul_{runId}";
        var dataSchema = $"dps_f2_data_{runId}";

        try
        {
            await AssertExactPostgresVersionAsync(connectionString, cancellationToken);
            var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"f2-alias-key-{runId}"));
            using var aliasKey = new AliasHmacKey("f2-key-v1", keyBytes);
            CryptographicOperations.ZeroMemory(keyBytes);
            using var registry = new PostgresSoulRegistry(
                new SoulRegistryOptions(connectionString, soulSchema, aliasKey.KeyId, [aliasKey]));
            await registry.InitializeAsync(cancellationToken);

            var ledgerOptions = new MemoryEventLedgerOptions(connectionString, dataSchema);
            var ledger = new PostgresMemoryEventLedger(ledgerOptions);
            await ledger.InitializeAsync(cancellationToken);
            var evidenceStore = new PostgresEvidenceStore(new EvidenceStoreOptions(connectionString, dataSchema));
            await evidenceStore.InitializeAsync(cancellationToken);

            var baseTime = new DateTimeOffset(2026, 7, 14, 2, 0, 0, TimeSpan.Zero);
            var soulA = await RegisterAndResolveAsync(
                registry,
                "alice.f2@example.test",
                "db_" + new string('1', 32),
                "pa_" + new string('2', 32),
                "a",
                baseTime,
                cancellationToken);
            var soulB = await RegisterAndResolveAsync(
                registry,
                "bob.f2@example.test",
                "db_" + new string('5', 32),
                "pa_" + new string('6', 32),
                "b",
                baseTime,
                cancellationToken);

            Assert.NotEqual(soulA.SoulId, soulB.SoulId);
            Assert.NotEqual(soulA.AliasDigest, soulB.AliasDigest);

            var eventA = CreateMemoryEvent(soulA, Guid.NewGuid(), baseTime, "deterministic systems", 'a');
            var eventB = CreateMemoryEvent(soulB, Guid.NewGuid(), baseTime.AddSeconds(1), "gardening", 'b');
            Assert.Equal(AppendDisposition.Inserted, (await ledger.AppendAsync(soulA, eventA, cancellationToken)).Disposition);
            Assert.Equal(AppendDisposition.DuplicateNoOp, (await ledger.AppendAsync(soulA, eventA, cancellationToken)).Disposition);

            var conflictingEvent = eventA with
            {
                Observation = eventA.Observation with { ContentDigest = new string('c', 64) }
            };
            Assert.Equal(
                AppendDisposition.Quarantined,
                (await ledger.AppendAsync(soulA, conflictingEvent, cancellationToken)).Disposition);
            Assert.Equal(1, await ledger.CountQuarantineAsync(cancellationToken));

            var crashEvent = CreateMemoryEvent(soulA, Guid.NewGuid(), baseTime.AddSeconds(2), "resilient queues", 'd');
            var crashLedger = new PostgresMemoryEventLedger(
                ledgerOptions,
                (stage, _) => stage == AppendStage.BeforeCommit
                    ? ValueTask.FromException(new IOException("injected crash before commit"))
                    : ValueTask.CompletedTask);
            await Assert.ThrowsAsync<IOException>(() => crashLedger.AppendAsync(soulA, crashEvent, cancellationToken));
            Assert.Equal(1, await ledger.CountEventsAsync(soulA.SoulId, cancellationToken));
            Assert.Equal(1, await ledger.CountOutboxAsync(soulA.SoulId, cancellationToken));
            Assert.Equal(AppendDisposition.Inserted, (await ledger.AppendAsync(soulA, crashEvent, cancellationToken)).Disposition);
            Assert.Equal(AppendDisposition.Inserted, (await ledger.AppendAsync(soulB, eventB, cancellationToken)).Disposition);

            var unknownMajor = eventA with
            {
                SchemaVersion = "2.0.0",
                EventId = Guid.NewGuid(),
                IdempotencyKey = "event-unknown-major"
            };
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                ledger.AppendAsync(soulA, unknownMajor, cancellationToken));

            var persistedA = await ledger.ReadSoulEventsAsync(soulA.SoulId, cancellationToken);
            var replayedA = await ledger.ReadSoulEventsAsync(soulA.SoulId, cancellationToken);
            var persistedB = await ledger.ReadSoulEventsAsync(soulB.SoulId, cancellationToken);
            Assert.Equal(2, persistedA.Count);
            Assert.Single(persistedB);
            Assert.All(persistedA, item => Assert.Equal(soulA.SoulId, item.SoulId));
            Assert.All(persistedB, item => Assert.Equal(soulB.SoulId, item.SoulId));
            Assert.Equal(
                persistedA.Select(MemoryEventCanonicalizer.Serialize),
                replayedA.Select(MemoryEventCanonicalizer.Serialize));

            var asOf = baseTime.AddDays(1);
            var reductionRequest = new InterestReductionRequest(
                soulA.SoulId,
                soulA.DeviceBindingId,
                soulA.PlatformAccountId,
                soulA.TraceId,
                "reduce-f2-a",
                asOf,
                persistedA);
            var reducer = new DeterministicInterestReducer();
            var snapshotA = reducer.Reduce(reductionRequest, new InterestReducerOptions(86_400m));
            var replayedSnapshotA = reducer.Reduce(reductionRequest, new InterestReducerOptions(86_400m));
            Assert.Equal(
                InterestSnapshotCanonicalizer.ComputeSha256(snapshotA),
                InterestSnapshotCanonicalizer.ComputeSha256(replayedSnapshotA));

            var projectionA = await RenderProjectionFixtureAsync(
                persistedA,
                snapshotA,
                cancellationToken: cancellationToken);
            var replayedProjectionA = await RenderProjectionFixtureAsync(
                replayedA,
                replayedSnapshotA,
                cancellationToken: cancellationToken);
            Assert.Equal(projectionA.ProjectionChecksum, replayedProjectionA.ProjectionChecksum);
            Assert.Equal(
                GBrainSourceIdCandidates.Compute(soulA.SoulId, FixtureNonce),
                projectionA.SourceId);
            Assert.DoesNotContain(projectionA.Events, item => item.EventId == eventB.EventId);

            var evidenceId = Guid.NewGuid();
            var metadata = CreateEvidenceMetadata(evidenceId, baseTime.AddMinutes(2));
            Assert.Throws<InvalidOperationException>(() =>
                VerticalSliceEvidenceComposer.Compose(soulA, [eventA], snapshotA, projectionA, metadata));
            var composed = VerticalSliceEvidenceComposer.Compose(soulA, persistedA, snapshotA, projectionA, metadata);
            Assert.True(TestEvidenceReleaseEvaluator.SatisfiesRequiredGate(composed.Receipt));

            using var runnerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var verifier = EvidenceTestData.Verifier(runnerKey);
            var service = new EvidenceSubmissionService(evidenceStore, verifier);
            var attestation = EvidenceTestData.Sign(composed.Candidate, runnerKey);
            var stored = await service.SubmitAsync(composed.Candidate, attestation, cancellationToken);
            var duplicate = await service.SubmitAsync(composed.Candidate, EvidenceTestData.Sign(composed.Candidate, runnerKey), cancellationToken);

            var conflictingComposition = VerticalSliceEvidenceComposer.Compose(
                soulA,
                persistedA,
                snapshotA,
                projectionA,
                metadata with { TestId = "evidence-service.integration-conflict" });
            var quarantined = await service.SubmitAsync(
                conflictingComposition.Candidate,
                EvidenceTestData.Sign(conflictingComposition.Candidate, runnerKey),
                cancellationToken);

            Assert.Equal(EvidenceStoreDisposition.Stored, stored.Disposition);
            Assert.Equal(EvidenceStoreDisposition.DuplicateNoOp, duplicate.Disposition);
            Assert.Equal(EvidenceStoreDisposition.Quarantined, quarantined.Disposition);
            Assert.Equal(1, await evidenceStore.CountForSoulAsync(soulA.SoulId, cancellationToken));
            Assert.Equal(0, await evidenceStore.CountForSoulAsync(soulB.SoulId, cancellationToken));
            Assert.Equal(1, await evidenceStore.CountQuarantineAsync(cancellationToken));

            var persistedEvidence = await evidenceStore.ReadAsync(
                evidenceId,
                soulA.SoulId,
                soulA.DeviceBindingId,
                soulA.PlatformAccountId,
                cancellationToken);
            Assert.NotNull(persistedEvidence);
            Assert.Equal(EvidenceTestData.RunnerKeyId, persistedEvidence.RunnerKeyId);
            Assert.Equal(
                RunnerAttestationCanonicalizer.ComputeSha256(attestation),
                persistedEvidence.AttestationSha256);
            Assert.Null(await evidenceStore.ReadAsync(
                evidenceId,
                soulB.SoulId,
                soulB.DeviceBindingId,
                soulB.PlatformAccountId,
                cancellationToken));
            var restartedStore = new PostgresEvidenceStore(new EvidenceStoreOptions(connectionString, dataSchema));
            var restartedVerifier = EvidenceTestData.Verifier(runnerKey);
            Assert.True(await restartedStore.VerifyPersistedEvidenceAsync(
                evidenceId,
                soulA.SoulId,
                soulA.DeviceBindingId,
                soulA.PlatformAccountId,
                restartedVerifier,
                cancellationToken));
            Assert.False(await restartedStore.VerifyPersistedEvidenceAsync(
                evidenceId,
                soulB.SoulId,
                soulB.DeviceBindingId,
                soulB.PlatformAccountId,
                restartedVerifier,
                cancellationToken));
            await AssertImmutableArtifactStorageAsync(
                connectionString,
                dataSchema,
                evidenceId,
                composed.RawArtifacts.Count + 1,
                cancellationToken);
        }
        finally
        {
            await DropSchemasAsync(connectionString, [soulSchema, dataSchema]);
        }
    }

    /// <summary>
    /// Executes the producer-backed projection fixture without the PostgreSQL ledger, so
    /// the Source-binding allocation and collision-retry behaviour the composer depends
    /// on is actually run and asserted rather than only reached from the ledger-gated
    /// integration test.
    /// </summary>
    [Fact]
    [Trait("Category", "Contract")]
    public async Task Producer_backed_projection_fixture_allocates_and_retries_source_binding_nonces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var soul = FixtureSoul();
        var baseTime = new DateTimeOffset(2026, 7, 14, 2, 0, 0, TimeSpan.Zero);
        var memoryEvent = new MemoryEventV1(
            MemoryEventV1.CurrentSchemaVersion,
            MemoryEventV1.CurrentContractId,
            MemoryEventV1.CurrentProducerModule,
            Guid.Parse("00000000-0000-0000-0000-0000000000f2"),
            soul.SoulId,
            soul.DeviceBindingId,
            soul.PlatformAccountId,
            soul.TraceId,
            FixtureIdempotency("event-f2-producer"),
            baseTime,
            "personal",
            MemoryEventV1.ObservedContentEventType,
            new MemoryObservationV1(new string('c', 64), true, [new InterestSignalV1("reading", 0.9m)]));
        var snapshot = new DeterministicInterestReducer().Reduce(
            new InterestReductionRequest(
                soul.SoulId,
                soul.DeviceBindingId,
                soul.PlatformAccountId,
                soul.TraceId,
                FixtureIdempotency("reduce-f2-producer"),
                baseTime.AddDays(1),
                [memoryEvent]),
            new InterestReducerOptions(86_400m));

        var atNonceZero = await RenderProjectionFixtureAsync(
            [memoryEvent],
            snapshot,
            cancellationToken: cancellationToken);
        // Pre-occupying the Soul's nonce-0 candidate forces the authority down the
        // collision-retry branch, which the hand-built projection could not reach.
        var afterCollision = await RenderProjectionFixtureAsync(
            [memoryEvent],
            snapshot,
            [GBrainSourceIdCandidates.Compute(soul.SoulId, FixtureNonce)],
            cancellationToken);

        Assert.Equal(FixtureNonce, atNonceZero.SourceBindingNonce);
        Assert.Equal(GBrainSourceIdCandidates.Compute(soul.SoulId, FixtureNonce), atNonceZero.SourceId);
        Assert.Equal(FixtureNonce + 1, afterCollision.SourceBindingNonce);
        Assert.Equal(GBrainSourceIdCandidates.Compute(soul.SoulId, FixtureNonce + 1), afterCollision.SourceId);
        Assert.NotEqual(atNonceZero.SourceId, afterCollision.SourceId);
        Assert.NotEqual(atNonceZero.ProjectionChecksum, afterCollision.ProjectionChecksum);

        // A replay of identical inputs through a fresh authority is byte-identical.
        var replayed = await RenderProjectionFixtureAsync(
            [memoryEvent],
            snapshot,
            cancellationToken: cancellationToken);
        Assert.Equal(
            GBrainProjectionV2Canonicalizer.Serialize(atNonceZero),
            GBrainProjectionV2Canonicalizer.Serialize(replayed));

        // The composer accepts a genuinely allocated non-zero-nonce Source, which is the
        // consumer-side point of the v2 migration.
        var composed = VerticalSliceEvidenceComposer.Compose(
            soul,
            [memoryEvent],
            snapshot,
            afterCollision,
            CreateEvidenceMetadata(
                Guid.Parse("00000000-0000-0000-0000-0000000000e1"),
                baseTime.AddMinutes(2)) with
            {
                TestId = "evidence-service.contract-producer-backed",
                TestType = "contract",
                VerificationLevel = "CONTRACT_VERIFIED",
                EvidenceIdempotencyKey = FixtureIdempotency("evidence-f2-producer")
            });
        Assert.True(TestEvidenceReleaseEvaluator.SatisfiesRequiredGate(composed.Receipt));
    }

    private static string FixtureIdempotency(string discriminator) =>
        "idem_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(discriminator)));

    private static Dps.SoulRegistry.Contracts.SoulResolved FixtureSoul() =>
        new(
            "soul_" + new string('a', 64),
            "db_" + new string('1', 32),
            "pa_" + new string('2', 32),
            "trace_" + new string('3', 32),
            "idem_" + new string('4', 64),
            new DateTimeOffset(2026, 7, 14, 1, 59, 0, TimeSpan.Zero),
            "email",
            new string('c', 64),
            "f2-key-v1");

    private static async Task<Dps.SoulRegistry.Contracts.SoulResolved> RegisterAndResolveAsync(
        PostgresSoulRegistry registry,
        string rawAlias,
        string deviceBindingId,
        string platformAccountId,
        string suffix,
        DateTimeOffset baseTime,
        CancellationToken cancellationToken)
    {
        var verification = new AliasVerification($"verified-alias-{suffix}", baseTime.AddMinutes(-2));
        await registry.RegisterVerifiedAliasAsync(
            new RegisterVerifiedAliasRequest(
                RegisterVerifiedAliasRequest.CurrentSchemaVersion,
                "tenant-f2",
                IdentityAliasKind.Email,
                rawAlias,
                verification,
                deviceBindingId,
                platformAccountId,
                "trace_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("register:" + suffix)))[..32],
                "idem_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("register:" + suffix))),
                baseTime.AddMinutes(-1)),
            cancellationToken);

        return await registry.ResolveAsync(
            new ResolveSoulRequest(
                ResolveSoulRequest.CurrentSchemaVersion,
                "tenant-f2",
                IdentityAliasKind.Email,
                rawAlias,
                verification,
                deviceBindingId,
                platformAccountId,
                "trace_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("resolve:" + suffix)))[..32],
                "idem_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("resolve:" + suffix))),
                baseTime),
            cancellationToken);
    }

    /// <summary>
    /// Producer-backed v2 projection fixture: the Source binding is really allocated by
    /// <see cref="GBrainSourceBindingAuthority"/> (including collision retry) and the
    /// projection is really rendered by <see cref="GBrainProjectionRenderer"/>. Both are
    /// pure in-process work, so the fixture needs no PostgreSQL: only the durable
    /// PostgreSQL store behind <c>GBrainProjectorPostgresRuntime</c> demands the
    /// least-privilege role pair. A fresh authority per call over a fixed clock keeps a
    /// replay of identical inputs byte-identical.
    /// </summary>
    /// <param name="reservedSourceIds">
    /// Source candidates to pre-occupy, so a caller can force the allocator off nonce 0.
    /// </param>
    private static async Task<GBrainProjectionV2> RenderProjectionFixtureAsync(
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 snapshot,
        IReadOnlyCollection<string>? reservedSourceIds = null,
        CancellationToken cancellationToken = default)
    {
        var authority = GBrainSourceBindingAuthority.CreateInMemory(
            new FixtureTimeProvider(FixtureBindingTime),
            reservedSourceIds);
        var binding = await authority.ResolveAsync(snapshot.SoulId, cancellationToken);
        return new GBrainProjectionRenderer(authority).Render(binding, memoryEvents, snapshot);
    }

    private sealed class FixtureTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static MemoryEventV1 CreateMemoryEvent(
        Dps.SoulRegistry.Contracts.SoulResolved soul,
        Guid eventId,
        DateTimeOffset occurredAt,
        string topic,
        char digestCharacter)
        => new(
            MemoryEventV1.CurrentSchemaVersion,
            MemoryEventV1.CurrentContractId,
            MemoryEventV1.CurrentProducerModule,
            eventId,
            soul.SoulId,
            soul.DeviceBindingId,
            soul.PlatformAccountId,
            soul.TraceId,
            $"event-{eventId:N}",
            occurredAt,
            "personal",
            MemoryEventV1.ObservedContentEventType,
            new MemoryObservationV1(
                new string(digestCharacter, 64),
                true,
                [new InterestSignalV1(topic, 0.9m)]));

    private static VerticalSliceEvidenceMetadata CreateEvidenceMetadata(Guid evidenceId, DateTimeOffset finishedAt)
        => new(
            EvidenceId: evidenceId,
            TestId: "evidence-service.integration",
            ModuleId: "evidence-service",
            EvidenceIdempotencyKey: $"evidence-{evidenceId:N}",
            BaselineCommit: EvidenceTestData.BaselineCommit,
            InstructionReceiptId: "instruction-receipt:f2-evidence",
            InstructionReceiptSha256: EvidenceTestData.InstructionSha,
            ImplementerIdentity: "agent:implementer",
            EvidenceIssuerIdentity: EvidenceTestData.EvidenceIssuerIdentity,
            ReleaseApproverIdentity: null,
            TestType: "integration",
            ExecutionEnvironment: "local",
            Required: true,
            Status: "PASS",
            VerificationLevel: "INTEGRATION_VERIFIED",
            CommandSha256: EvidenceTestData.Sha256("dotnet test --filter-trait Category=Integration"),
            ExitCode: 0,
            ReasonCode: null,
            StartedAt: finishedAt.AddMinutes(-1),
            FinishedAt: finishedAt);

    private static async Task AssertExactPostgresVersionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        Assert.Equal(180004, version);
    }

    private static async Task AssertImmutableArtifactStorageAsync(
        string connectionString,
        string schemaName,
        Guid evidenceId,
        int expectedArtifactCount,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema_name AND table_name = 'test_evidence'
            ORDER BY ordinal_position
            """,
            connection);
        command.Parameters.AddWithValue("schema_name", schemaName);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        Assert.NotEmpty(columns);
        Assert.DoesNotContain(columns, static name =>
            name.Contains("payload", StringComparison.Ordinal) ||
            name.Contains("content", StringComparison.Ordinal) ||
            name.Contains("json", StringComparison.Ordinal) ||
            name.Contains("raw", StringComparison.Ordinal));

        await reader.DisposeAsync();
        await using var artifactCount = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {schemaName}.evidence_artifacts WHERE evidence_id = @evidence_id",
            connection);
        artifactCount.Parameters.AddWithValue("evidence_id", evidenceId);
        Assert.Equal(expectedArtifactCount, Convert.ToInt32(await artifactCount.ExecuteScalarAsync(cancellationToken)));

        await using var rawAliasProbe = new NpgsqlCommand(
            $"""
            SELECT COUNT(*)
            FROM {schemaName}.evidence_artifacts
            WHERE evidence_id = @evidence_id
              AND convert_from(content_bytes, 'UTF8') LIKE '%@example.test%'
            """,
            connection);
        rawAliasProbe.Parameters.AddWithValue("evidence_id", evidenceId);
        Assert.Equal(0, Convert.ToInt32(await rawAliasProbe.ExecuteScalarAsync(cancellationToken)));

        await using var tamper = new NpgsqlCommand(
            $"""
            UPDATE {schemaName}.evidence_artifacts
            SET content_bytes = content_bytes || decode('00', 'hex')
            WHERE evidence_id = @evidence_id
            """,
            connection);
        tamper.Parameters.AddWithValue("evidence_id", evidenceId);
        await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task DropSchemasAsync(string connectionString, IReadOnlyList<string> schemaNames)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        foreach (var schemaName in schemaNames)
        {
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schemaName} CASCADE", connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
