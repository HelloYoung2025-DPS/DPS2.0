using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Dps.ControlPlaneHost.Tests;

public sealed class PostgresControlPlaneTruthStoreIntegrationTests
{
    private const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BindingA = "db_11111111111111111111111111111111";
    private const string BindingB = "db_22222222222222222222222222222222";
    private const string AccountA = "pa_33333333333333333333333333333333";
    private const string AccountB = "pa_44444444444444444444444444444444";
    private const string TraceA = "trace_55555555555555555555555555555555";
    private const string TraceB = "trace_66666666666666666666666666666666";
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 14, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken TestCancellation
        => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task MigrationRefusesUnmarkedPreexistingSchemaWithoutBlessingItsConstraints()
    {
        await ControlPlaneTestDatabase.AssertUnmarkedPreexistingSchemaRejectedAsync(
            TestCancellation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task TruthReceiptOutboxAndVerifiedGbrainReadbackCommitAtomicallyAcrossRestart()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var payload = Spec(
            contractId: "soul.memory.readback/v1",
            idempotencyKey: IdempotencyFor("gbrain-verified"));
        var signed = database.Sign(payload);

        var receipt = await database.Store.IngestAsync(signed, TestCancellation);
        var restarted = database.CreateStore();
        var readback = await restarted.GetAsync(
            SoulA,
            BindingA,
            AccountA,
            payload.ContractId,
            payload.IdempotencyKey,
            TestCancellation);

        Assert.Equal(receipt, readback);
        Assert.Equal("verified", payload.Status);
        Assert.Equal("accepted", readback.Decision);
        Assert.Equal(1, await restarted.CountTruthAsync(TestCancellation));
        Assert.Equal(1, await restarted.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await restarted.CountOutboxAsync(TestCancellation));
        Assert.Equal(0, await restarted.CountQuarantineAsync(TestCancellation));
        var outbox = Assert.Single(await restarted.ReadPendingOutboxAsync(
            SoulA,
            BindingA,
            AccountA,
            TestCancellation));
        Assert.Equal(receipt, outbox.Payload);
        Assert.Matches("^[a-f0-9]{64}$", outbox.PayloadSha256);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("device.registered/v1")]
    [InlineData("platform.account.authorized/v1")]
    [InlineData("identity.binding/v1")]
    [InlineData("persona.revision/v1")]
    [InlineData("soul.memory.readback/v1")]
    public async Task EveryConsumedV1SchemaAcceptsItsExactSignedRawJson(
        string contractId)
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var payload = Spec(
            contractId,
            idempotencyKey: IdempotencyFor("exact-" + contractId));
        var signed = database.Sign(payload);

        var receipt = await database.Store.IngestAsync(signed, TestCancellation);

        Assert.Equal(contractId, receipt.SourceContractId);
        Assert.Equal(payload.ProducerModule, receipt.SourceProducerModule);
        Assert.Equal(Sha256(signed.PayloadUtf8.Span), receipt.SourcePayloadSha256);
        Assert.Equal(1, await database.Store.CountTruthAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentExactRedeliveryCreatesOneTruthReceiptAndOutbox()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("concurrent-duplicate")));

        var receipts = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => database.CreateStore().IngestAsync(signed, TestCancellation)));

        Assert.Single(receipts.Select(static value => value.ReceiptId).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, await database.Store.CountTruthAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
        Assert.Equal(0, await database.Store.CountQuarantineAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameScopedIdempotencyKeyWithDifferentRawPayloadIsQuarantinedWithoutSecondTruth()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var first = Spec(idempotencyKey: IdempotencyFor("conflict"));
        await database.Store.IngestAsync(database.Sign(first), TestCancellation);

        await Assert.ThrowsAsync<ControlPlaneIdempotencyConflictException>(() =>
            database.CreateStore().IngestAsync(
                database.Sign(first with { Variant = 2 }),
                TestCancellation));

        Assert.Equal(1, await database.Store.CountTruthAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountQuarantineAsync(TestCancellation));
        var quarantine = Assert.Single(await database.Store.ReadQuarantineAsync(
            SoulA,
            BindingA,
            AccountA,
            TestCancellation));
        Assert.NotEqual(quarantine.ExistingRecordSha256, quarantine.IncomingRecordSha256);
        Assert.Matches("^[a-f0-9]{64}$", quarantine.ScopeSha256);
        Assert.Matches("^[a-f0-9]{64}$", quarantine.IdempotencyKeySha256);
        Assert.False(await database.TableContainsAsync(
            "idempotency_quarantine",
            first.IdempotencyKey,
            TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExactSoulDeviceAccountQueriesAndQuarantineNeverLeakAcrossScope()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var first = Spec(idempotencyKey: IdempotencyFor("scope-a"));
        var second = Spec(
            soulId: SoulB,
            deviceBindingId: BindingB,
            platformAccountId: AccountB,
            traceId: TraceB,
            idempotencyKey: IdempotencyFor("scope-b"));
        await database.Store.IngestAsync(database.Sign(first), TestCancellation);
        await database.Store.IngestAsync(database.Sign(second), TestCancellation);
        await Assert.ThrowsAsync<ControlPlaneIdempotencyConflictException>(() =>
            database.Store.IngestAsync(
                database.Sign(first with { Variant = 2 }),
                TestCancellation));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => database.Store.GetAsync(
            SoulB,
            BindingA,
            AccountA,
            first.ContractId,
            first.IdempotencyKey,
            TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => database.Store.GetAsync(
            SoulA,
            BindingB,
            AccountB,
            second.ContractId,
            second.IdempotencyKey,
            TestCancellation));
        Assert.Single(await database.Store.ReadPendingOutboxAsync(
            SoulA,
            BindingA,
            AccountA,
            TestCancellation));
        Assert.Single(await database.Store.ReadPendingOutboxAsync(
            SoulB,
            BindingB,
            AccountB,
            TestCancellation));
        Assert.Single(await database.Store.ReadQuarantineAsync(
            SoulA,
            BindingA,
            AccountA,
            TestCancellation));
        Assert.Empty(await database.Store.ReadQuarantineAsync(
            SoulB,
            BindingB,
            AccountB,
            TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task TruncatedGbrainSourceCollisionStillUsesFullSoulScope()
    {
        const string collidingSoul =
            "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var first = Spec(
            "soul.memory.readback/v1",
            idempotencyKey: IdempotencyFor("gbrain-source-collision-a"));
        var second = Spec(
            "soul.memory.readback/v1",
            soulId: collidingSoul,
            deviceBindingId: BindingB,
            platformAccountId: AccountB,
            traceId: TraceB,
            idempotencyKey: IdempotencyFor("gbrain-source-collision-b"));
        Assert.Equal(
            "dps-" + SoulA.AsSpan("soul_".Length, 28).ToString(),
            "dps-" + collidingSoul.AsSpan("soul_".Length, 28).ToString());

        await database.Store.IngestAsync(database.Sign(first), TestCancellation);
        await database.Store.IngestAsync(database.Sign(second), TestCancellation);

        Assert.Equal(2, await database.Store.CountTruthAsync(TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => database.Store.GetAsync(
            collidingSoul,
            BindingA,
            AccountA,
            first.ContractId,
            first.IdempotencyKey,
            TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => database.Store.GetAsync(
            SoulA,
            BindingB,
            AccountB,
            second.ContractId,
            second.IdempotencyKey,
            TestCancellation));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("unknown-major")]
    [InlineData("malformed-major")]
    [InlineData("unknown-contract")]
    [InlineData("wrong-producer")]
    [InlineData("binding-bad-status")]
    [InlineData("device-bad-status")]
    [InlineData("persona-bad-status")]
    [InlineData("gbrain-unverified")]
    public async Task InvalidMajorContractProducerOrStatusFailsClosedWithZeroWrites(
        string invalidCase)
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var json = InvalidEnvelopeJson(invalidCase);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            database.Store.IngestAsync(database.WrapUntrustedJson(json), TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("TruthWritten")]
    [InlineData("ReceiptWritten")]
    [InlineData("OutboxWritten")]
    [InlineData("BeforeCommit")]
    public async Task EveryCrashWindowRollsBackAndCleanRestartRecovers(
        string crashStageName)
    {
        var crashStage = Enum.Parse<ControlPlaneMutationStage>(crashStageName);
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var crashing = database.CreateStore((stage, _) =>
            stage == crashStage
                ? ValueTask.FromException(new SimulatedControlPlaneCrashException())
                : ValueTask.CompletedTask);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("crash-" + crashStage)));

        await Assert.ThrowsAsync<SimulatedControlPlaneCrashException>(() =>
            crashing.IngestAsync(signed, TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);

        var recovered = await database.CreateStore().IngestAsync(signed, TestCancellation);
        Assert.Equal("accepted", recovered.Decision);
        Assert.Equal(1, await database.Store.CountTruthAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CancellationAfterReceiptWriteRollsBackAndSameSignedRequestCanRetry()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var canceled = new CancellationToken(canceled: true);
        var store = database.CreateStore((stage, _) =>
            stage == ControlPlaneMutationStage.ReceiptWritten
                ? ValueTask.FromCanceled(canceled)
                : ValueTask.CompletedTask);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("cancel")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.IngestAsync(signed, TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);

        var recovered = await database.CreateStore().IngestAsync(signed, TestCancellation);
        Assert.Equal("accepted", recovered.Decision);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RuntimeRoleCannotDdlEscalateOrMutateAndOwnerTriggersBlockAllRewrites()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("acl")));
        await database.Store.IngestAsync(signed, TestCancellation);

        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteRuntimeAsync("CREATE TABLE forbidden_runtime_table(value integer)", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteRuntimeAsync("CREATE TEMP TABLE forbidden_runtime_temp(value integer)", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteRuntimeAsync("UPDATE runtime_truth SET result_status='revoked'", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteRuntimeAsync("DELETE FROM idempotency_receipts", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteRuntimeAsync("TRUNCATE outbox", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteRuntimeAsync("INSERT INTO provider_trust_states DEFAULT VALUES", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteRuntimeSetRoleToMigratorAsync(TestCancellation));

        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteOwnerAsync("UPDATE provider_trust_states SET status='REVOKED'", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteOwnerAsync("UPDATE runtime_truth SET result_status='revoked'", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteOwnerAsync("DELETE FROM idempotency_receipts", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteOwnerAsync("TRUNCATE outbox", TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.InsertUnknownProducerDirectlyAsync(signed, TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.InsertTrailingIdentifierIntoConstraintCloneAsOwnerAsync(
                "device_binding_id",
                BindingA + "\n",
                TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.InsertTrailingIdentifierIntoConstraintCloneAsOwnerAsync(
                "platform_account_id",
                AccountA + "\n",
                TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.InsertTrailingIdentifierIntoConstraintCloneAsOwnerAsync(
                "trace_id",
                TraceA + "\n",
                TestCancellation));
        await Assert.ThrowsAsync<PostgresException>(() =>
            database.InsertTrailingIdentifierIntoConstraintCloneAsOwnerAsync(
                "idempotency_key",
                IdempotencyFor("db-constraint") + "\n",
                TestCancellation));

        Assert.Equal(1, await database.Store.CountTruthAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("signature")]
    [InlineData("bom")]
    [InlineData("key")]
    public async Task SignatureBomOrKeyMismatchFailsClosedBeforeAnyRuntimeWrite(
        string mismatch)
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("auth-" + mismatch)));
        signed = mismatch switch
        {
            "signature" => CorruptSignature(signed),
            "bom" => signed with { ActiveReleaseBomSha256 = new string('c', 64) },
            "key" => signed with { ProviderKeyId = "provider-test-key-v2" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            database.Store.IngestAsync(signed, TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LatestRevokedProviderTrustRejectsPreviouslyValidSignature()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("revoked-key")));
        await database.AppendTrustStateAsync(
            "identity.binding/v1",
            "REVOKED",
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TestCancellation);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            database.Store.IngestAsync(signed, TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExactCommittedRedeliveryReturnsSameReceiptAfterProviderKeyRevocation()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("committed-then-revoked")));
        var committed = await database.Store.IngestAsync(signed, TestCancellation);
        await database.AppendTrustStateAsync(
            "identity.binding/v1",
            "REVOKED",
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TestCancellation);

        var duplicate = await database.CreateStore().IngestAsync(signed, TestCancellation);

        Assert.Equal(committed, duplicate);
        Assert.Equal(1, await database.Store.CountTruthAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
        Assert.Equal(0, await database.Store.CountQuarantineAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LatestExpiredProviderTrustRejectsPreviouslyValidSignature()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var signed = database.Sign(Spec(idempotencyKey: IdempotencyFor("expired-key")));
        await database.AppendTrustStateAsync(
            "identity.binding/v1",
            "ACTIVE",
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TestCancellation);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            database.Store.IngestAsync(signed, TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task GbrainVerifiedLabelWithDifferentExactReadbackChecksumFailsClosed()
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var json = BuildPayloadJson(Spec(
            "soul.memory.readback/v1",
            idempotencyKey: IdempotencyFor("gbrain-mismatch")));
        json = ReplaceExact(
            json,
            "\"readback_checksum\":\"" + new string('c', 64) + "\"",
            "\"readback_checksum\":\"" + new string('d', 64) + "\"");

        await Assert.ThrowsAnyAsync<Exception>(() => database.Store.IngestAsync(
            database.WrapUntrustedJson(json),
            TestCancellation));

        var wrongSource = ReplaceExact(
            BuildPayloadJson(Spec(
                "soul.memory.readback/v1",
                idempotencyKey: IdempotencyFor("gbrain-wrong-source"))),
            "\"source_id\":\"dps-" + new string('a', 28) + "\"",
            "\"source_id\":\"dps-" + new string('b', 28) + "\"");
        await Assert.ThrowsAnyAsync<Exception>(() => database.Store.IngestAsync(
            database.WrapUntrustedJson(wrongSource),
            TestCancellation));
        var zeroOffset = ReplaceExact(
            BuildPayloadJson(Spec(
                "soul.memory.readback/v1",
                idempotencyKey: IdempotencyFor("gbrain-zero-offset"))),
            "\"occurred_at\":\"2026-07-14T08:00:00Z\"",
            "\"occurred_at\":\"2026-07-14T08:00:00+00:00\"");
        await Assert.ThrowsAnyAsync<Exception>(() => database.Store.IngestAsync(
            database.WrapUntrustedJson(zeroOffset),
            TestCancellation));
        var trailingFractionZero = ReplaceExact(
            BuildPayloadJson(Spec(
                "soul.memory.readback/v1",
                idempotencyKey: IdempotencyFor("gbrain-fraction-zero"))),
            "\"occurred_at\":\"2026-07-14T08:00:00Z\"",
            "\"occurred_at\":\"2026-07-14T08:00:00.1200Z\"");
        await Assert.ThrowsAnyAsync<Exception>(() => database.Store.IngestAsync(
            database.WrapUntrustedJson(trailingFractionZero),
            TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("duplicate-field")]
    [InlineData("additional-field")]
    public async Task DuplicateOrAdditionalJsonFieldFailsClosedWithZeroWrites(
        string invalidCase)
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var json = BuildPayloadJson(Spec(idempotencyKey: IdempotencyFor("json-" + invalidCase)));
        json = invalidCase switch
        {
            "duplicate-field" => json.Insert(1, "\"schema_version\":\"1.0.0\","),
            "additional-field" => json[..^1] + ",\"unexpected\":\"untrusted\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };

        await Assert.ThrowsAnyAsync<Exception>(() => database.Store.IngestAsync(
            database.WrapUntrustedJson(json),
            TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("slug")]
    [InlineData("approval")]
    [InlineData("persona-deleted-traits")]
    [InlineData("persona-unknown-trait")]
    [InlineData("unsorted-array")]
    [InlineData("duplicate-array")]
    [InlineData("rfc3339-offset")]
    [InlineData("platform-length")]
    [InlineData("persona-year")]
    public async Task ProviderParserBoundaryViolationsFailClosedWithZeroWrites(
        string boundary)
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        var json = BoundaryViolationJson(boundary);

        await Assert.ThrowsAnyAsync<Exception>(() => database.Store.IngestAsync(
            database.WrapUntrustedJson(json),
            TestCancellation));
        await database.AssertNoMutationAsync(TestCancellation);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("trigger")]
    [InlineData("constraint")]
    [InlineData("constraint-parentheses")]
    [InlineData("function")]
    [InlineData("function-strict")]
    [InlineData("index")]
    [InlineData("acl")]
    [InlineData("column-insert-acl")]
    [InlineData("column")]
    [InlineData("extra-table")]
    [InlineData("role-membership")]
    [InlineData("role-inherit")]
    [InlineData("collation-shadow")]
    public async Task RuntimeAttestationRejectsCatalogOrAclTampering(
        string tamper)
    {
        await using var database = await ControlPlaneTestDatabase.CreateAsync(TestCancellation);
        await database.TamperSchemaAsync(tamper, TestCancellation);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            database.CreateStore().CountTruthAsync(TestCancellation));
        await database.AssertNoMutationWithoutRuntimeAttestationAsync(TestCancellation);
    }

    private static ProviderPayloadSpec Spec(
        string contractId = "identity.binding/v1",
        string? schemaVersion = null,
        string? producerModule = null,
        string? status = null,
        string soulId = SoulA,
        string deviceBindingId = BindingA,
        string platformAccountId = AccountA,
        string traceId = TraceA,
        string? idempotencyKey = null,
        DateTimeOffset? occurredAt = null)
        => new(
            schemaVersion ?? "1.0.0",
            contractId,
            producerModule ?? ProducerFor(contractId),
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey ?? IdempotencyFor("default"),
            occurredAt ?? OccurredAt,
            status ?? DefaultStatus(contractId),
            Variant: 1);

    private static string InvalidEnvelopeJson(string invalidCase)
        => invalidCase switch
        {
            "unknown-major" => BuildPayloadJson(Spec(schemaVersion: "2.0.0")),
            "malformed-major" => BuildPayloadJson(Spec(schemaVersion: "1.evil")),
            "unknown-contract" => ReplaceExact(
                BuildPayloadJson(Spec()),
                "\"contract_id\":\"identity.binding/v1\"",
                "\"contract_id\":\"unknown.contract/v1\""),
            "wrong-producer" => ReplaceExact(
                BuildPayloadJson(Spec()),
                "\"producer_module\":\"binding\"",
                "\"producer_module\":\"planner\""),
            "binding-bad-status" => BuildPayloadJson(Spec(status: "prepared")),
            "device-bad-status" => BuildPayloadJson(Spec("device.registered/v1", status: "active")),
            "persona-bad-status" => BuildPayloadJson(Spec("persona.revision/v1", status: "prepared")),
            "gbrain-unverified" => BuildPayloadJson(Spec("soul.memory.readback/v1", status: "prepared")),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };

    private static string BoundaryViolationJson(string boundary)
    {
        switch (boundary)
        {
            case "slug":
                return ReplaceExact(
                    BuildPayloadJson(Spec("device.registered/v1")),
                    "\"capabilities\":[\"locate\",\"tap\"]",
                    "\"capabilities\":[\".tap\",\"locate\"]");
            case "approval":
                return ReplaceExact(
                    BuildPayloadJson(Spec("platform.account.authorized/v1")),
                    "\"authorization_evidence_id\":\"approval_fixture\"",
                    "\"authorization_evidence_id\":\"approval_\"");
            case "persona-deleted-traits":
                return ReplaceExact(
                    BuildPayloadJson(Spec("persona.revision/v1", status: "deleted")),
                    "\"trait_keys\":[]",
                    "\"trait_keys\":[\"curiosity\"]");
            case "persona-unknown-trait":
                return ReplaceExact(
                    BuildPayloadJson(Spec("persona.revision/v1")),
                    "\"trait_keys\":[\"curiosity\",\"tone\"]",
                    "\"trait_keys\":[\"curiosity\",\"wisdom\"]");
            case "unsorted-array":
                return ReplaceExact(
                    BuildPayloadJson(Spec("device.registered/v1")),
                    "\"capabilities\":[\"locate\",\"tap\"]",
                    "\"capabilities\":[\"tap\",\"locate\"]");
            case "duplicate-array":
                return ReplaceExact(
                    BuildPayloadJson(Spec("persona.revision/v1")),
                    "\"evidence_sha256\":[\"" + new string('e', 64) + "\",\"" + new string('f', 64) + "\"]",
                    "\"evidence_sha256\":[\"" + new string('e', 64) + "\",\"" + new string('e', 64) + "\"]");
            case "rfc3339-offset":
                return ReplaceExact(
                    BuildPayloadJson(Spec()),
                    "\"occurred_at\":\"2026-07-14T08:00:00Z\"",
                    "\"occurred_at\":\"2026-07-14T09:00:00+01:00\"");
            case "platform-length":
                return ReplaceExact(
                    BuildPayloadJson(Spec("platform.account.authorized/v1")),
                    "\"platform\":\"fixture.platform\"",
                    "\"platform\":\"" + new string('a', 65) + "\"");
            case "persona-year":
                return ReplaceExact(
                    BuildPayloadJson(Spec("persona.revision/v1")),
                    "\"occurred_at\":\"2026-07-14T08:00:00Z\"",
                    "\"occurred_at\":\"2019-12-31T23:59:59Z\"");
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary));
        }
    }

    private static string BuildPayloadJson(ProviderPayloadSpec payload)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema_version"] = payload.SchemaVersion,
            ["contract_id"] = payload.ContractId,
            ["producer_module"] = payload.ProducerModule,
            ["soul_id"] = payload.SoulId,
            ["device_binding_id"] = payload.DeviceBindingId,
            ["platform_account_id"] = payload.PlatformAccountId,
            ["trace_id"] = payload.TraceId,
            ["idempotency_key"] = payload.IdempotencyKey,
            ["occurred_at"] = payload.OccurredAt.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture)
        };

        switch (payload.ContractId)
        {
            case "device.registered/v1":
                values["privacy_class"] = "sensitive";
                values["device_id"] = "device_" + new string('1', 32);
                values["fingerprint_hmac_sha256"] = new string('2', 64);
                values["fingerprint_key_id"] = "fpkey_" + new string('7', 32);
                values["fingerprint_key_epoch"] = 1;
                values["capability_revision"] = payload.Variant;
                values["capabilities"] = new[] { "locate", "tap" };
                values["status"] = payload.Status;
                break;
            case "platform.account.authorized/v1":
                values["privacy_class"] = "sensitive";
                values["platform"] = "fixture.platform";
                values["alias_digest"] = new string('3', 64);
                values["alias_key_id"] = "alias-key-v1";
                values["alias_key_epoch"] = 1;
                values["authorization_evidence_id"] = "approval_fixture";
                values["authorization_revision"] = payload.Variant;
                values["status"] = payload.Status;
                break;
            case "identity.binding/v1":
            case "unknown.contract/v1":
                values["privacy_class"] = "sensitive";
                values["device_id"] = "device_" + new string('4', 32);
                values["binding_revision"] = payload.Variant;
                values["status"] = payload.Status;
                values["device_registration_revision"] = payload.Variant;
                values["account_authorization_revision"] = payload.Variant;
                break;
            case "persona.revision/v1":
                values["privacy_class"] = "personal";
                values["persona_revision"] = payload.Variant;
                values["traits_sha256"] = new string('5', 64);
                values["trait_keys"] = string.Equals(payload.Status, "deleted", StringComparison.Ordinal)
                    ? Array.Empty<string>()
                    : new[] { "curiosity", "tone" };
                values["evidence_sha256"] = new[] { new string('e', 64), new string('f', 64) };
                values["status"] = payload.Status;
                break;
            case "soul.memory.readback/v1":
                values["privacy_class"] = "personal";
                values["source_id"] =
                    "dps-" + payload.SoulId.AsSpan("soul_".Length, 28).ToString();
                values["projection_schema_version"] = "1.0.0";
                values["projection_contract_id"] = "gbrain.projection/v1";
                values["projection_revision"] = new string('6', 64);
                values["projection_checksum"] = new string('c', 64);
                values["readback_checksum"] = new string('c', 64);
                values["status"] = payload.Status;
                break;
            default:
                throw new NotSupportedException("Test payload builder has no exact schema for the contract.");
        }

        return JsonSerializer.Serialize(values);
    }

    private static string ReplaceExact(string value, string oldValue, string newValue)
    {
        var replaced = value.Replace(oldValue, newValue, StringComparison.Ordinal);
        return string.Equals(replaced, value, StringComparison.Ordinal)
            ? throw new InvalidOperationException("The test payload mutation did not match its exact source token.")
            : replaced;
    }

    private static SignedProviderResultV1 CorruptSignature(SignedProviderResultV1 signed)
    {
        var signature = Convert.FromBase64String(signed.SignatureBase64);
        try
        {
            signature[0] ^= 0x80;
            return signed with { SignatureBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string ProducerFor(string contractId)
        => contractId switch
        {
            "device.registered/v1" => "device-registry",
            "platform.account.authorized/v1" => "platform-account-registry",
            "identity.binding/v1" => "binding",
            "persona.revision/v1" => "persona-store",
            "soul.memory.readback/v1" => "soul-memory-adapter",
            _ => "binding"
        };

    private static string DefaultStatus(string contractId)
        => contractId switch
        {
            "device.registered/v1" => "registered",
            "platform.account.authorized/v1" => "authorized",
            "identity.binding/v1" => "active",
            "persona.revision/v1" => "active",
            "soul.memory.readback/v1" => "verified",
            _ => "active"
        };

    private static string IdempotencyFor(string testCase)
    {
        var bytes = Encoding.UTF8.GetBytes(testCase);
        try
        {
            return "idem_" + Sha256(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed record ProviderPayloadSpec(
        string SchemaVersion,
        string ContractId,
        string ProducerModule,
        string SoulId,
        string DeviceBindingId,
        string PlatformAccountId,
        string TraceId,
        string IdempotencyKey,
        DateTimeOffset OccurredAt,
        string Status,
        int Variant);

    private sealed class SimulatedControlPlaneCrashException : Exception
    {
    }

    private sealed class ControlPlaneTestDatabase : IAsyncDisposable
    {
        private static readonly (string ContractId, string ProducerModule)[] Providers =
        [
            ("device.registered/v1", "device-registry"),
            ("platform.account.authorized/v1", "platform-account-registry"),
            ("identity.binding/v1", "binding"),
            ("persona.revision/v1", "persona-store"),
            ("soul.memory.readback/v1", "soul-memory-adapter")
        ];
        private const string WidenedAllowlistConstraintSql = """
            ALTER TABLE runtime_truth
            DROP CONSTRAINT runtime_truth_allowlisted_result;
            ALTER TABLE runtime_truth
            ADD CONSTRAINT runtime_truth_allowlisted_result CHECK (
                (source_contract_id = 'device.registered/v1'
                    AND source_producer_module = 'device-registry'
                    AND result_status = 'registered')
                OR result_status = 'retired'
                OR (source_contract_id = 'platform.account.authorized/v1'
                    AND source_producer_module = 'platform-account-registry'
                    AND (result_status = 'authorized' OR result_status = 'suspended' OR result_status = 'revoked'))
                OR (source_contract_id = 'identity.binding/v1'
                    AND source_producer_module = 'binding'
                    AND (result_status = 'active' OR result_status = 'revoked'))
                OR (source_contract_id = 'persona.revision/v1'
                    AND source_producer_module = 'persona-store'
                    AND (result_status = 'active' OR result_status = 'deleted'))
                OR (source_contract_id = 'soul.memory.readback/v1'
                    AND source_producer_module = 'soul-memory-adapter'
                    AND result_status = 'verified'))
            """;

        private readonly string _migrationConnectionString;
        private readonly string _runtimeConnectionString;
        private readonly string _schemaName;
        private readonly string _runtimeRoleName;
        private readonly string _migrationRoleName;
        private readonly ECDsa _providerKey;
        private readonly PostgresControlPlaneTruthMigrator _migrator;
        private readonly Dictionary<string, long> _trustRevisions =
            new(StringComparer.Ordinal);
        private readonly string _providerPublicKeySpkiBase64;

        private ControlPlaneTestDatabase(
            string migrationConnectionString,
            string runtimeConnectionString,
            string schemaName,
            string runtimeRoleName,
            string migrationRoleName,
            ECDsa providerKey,
            PostgresControlPlaneTruthMigrator migrator)
        {
            _migrationConnectionString = migrationConnectionString;
            _runtimeConnectionString = runtimeConnectionString;
            _schemaName = schemaName;
            _runtimeRoleName = runtimeRoleName;
            _migrationRoleName = migrationRoleName;
            _providerKey = providerKey;
            _migrator = migrator;
            var publicKey = _providerKey.ExportSubjectPublicKeyInfo();
            try
            {
                _providerPublicKeySpkiBase64 = Convert.ToBase64String(publicKey);
                ProviderPublicKeySha256 = Sha256(publicKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }
            Store = CreateStore();
        }

        public const string ActiveReleaseBomSha256 =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public const string ProviderKeyId = "provider-test-key-v1";

        public string ProviderPublicKeySha256 { get; }
        public PostgresControlPlaneTruthStore Store { get; }

        public static async Task<ControlPlaneTestDatabase> CreateAsync(
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
                    "Control Plane Integration refuses the persistent GBrain Company database.");
            }

            if (string.IsNullOrWhiteSpace(migrationBuilder.Username))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES requires an explicit migration username.");
            }

            var suffix = Guid.NewGuid().ToString("N")[..20];
            var schemaName = "control_it_" + suffix;
            var runtimeRoleName = "control_rt_" + suffix;
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

            ECDsa? providerKey = null;
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
                                $"Control Plane Integration requires exact PostgreSQL 18.4; server_version_num was '{actual ?? "missing"}'.");
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
                            ?? throw new InvalidOperationException("PostgreSQL did not produce the controlled CREATE ROLE statement."));
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
                var migrator = new PostgresControlPlaneTruthMigrator(
                    new PostgresControlPlaneMigrationOptions(
                        migrationConnectionString,
                        schemaName,
                        runtimeRoleName));
                await migrator.InitializeAsync(cancellationToken);
                providerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var database = new ControlPlaneTestDatabase(
                    migrationConnectionString,
                    runtimeBuilder.ConnectionString,
                    schemaName,
                    runtimeRoleName,
                    migrationRoleName,
                    providerKey,
                    migrator);
                providerKey = null;
                await database.AppendInitialTrustStatesAsync(cancellationToken);
                await database.AssertRuntimeIdentityAsync(cancellationToken);
                return database;
            }
            catch
            {
                providerKey?.Dispose();
                await CleanupAsync(
                    migrationConnectionString,
                    schemaName,
                    runtimeRoleName,
                    CancellationToken.None);
                throw;
            }
        }

        public static async Task AssertUnmarkedPreexistingSchemaRejectedAsync(
            CancellationToken cancellationToken)
        {
            var migrationConnectionString = RequireConnectionString();
            var suffix = Guid.NewGuid().ToString("N")[..20];
            var schemaName = "control_unmarked_" + suffix;
            var runtimeRoleName = "control_unmarked_rt_" + suffix;
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schemaName);
            var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRoleName);

            await using var admin = new NpgsqlConnection(migrationConnectionString);
            await admin.OpenAsync(cancellationToken);
            try
            {
                await using (var prepare = new NpgsqlCommand(
                                 $"""
                                 CREATE ROLE {quotedRole}
                                     NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
                                     NOREPLICATION NOBYPASSRLS;
                                 """,
                                 admin))
                {
                    await prepare.ExecuteNonQueryAsync(cancellationToken);
                }

                var migrator = new PostgresControlPlaneTruthMigrator(
                    new PostgresControlPlaneMigrationOptions(
                        migrationConnectionString,
                        schemaName,
                        runtimeRoleName));
                await migrator.InitializeAsync(cancellationToken);
                var initial = await ReadConstraintBaselineStateAsync(
                    admin,
                    schemaName,
                    cancellationToken);

                await using (var tamper = new NpgsqlCommand(
                                 $"""
                                 SET search_path TO {quotedSchema};
                                 {WidenedAllowlistConstraintSql};
                                 COMMENT ON SCHEMA {quotedSchema} IS NULL;
                                 """,
                                 admin))
                {
                    await tamper.ExecuteNonQueryAsync(cancellationToken);
                }
                var altered = await ReadConstraintBaselineStateAsync(
                    admin,
                    schemaName,
                    cancellationToken);
                Assert.NotEqual(initial.Definition, altered.Definition);
                Assert.Null(altered.SchemaComment);
                Assert.Null(altered.ConstraintComment);

                await Assert.ThrowsAnyAsync<Exception>(() =>
                    migrator.InitializeAsync(cancellationToken));
                var afterRejectedMigration = await ReadConstraintBaselineStateAsync(
                    admin,
                    schemaName,
                    cancellationToken);
                Assert.Equal(altered.Definition, afterRejectedMigration.Definition);
                Assert.Null(afterRejectedMigration.SchemaComment);
                Assert.Null(afterRejectedMigration.ConstraintComment);
            }
            finally
            {
                await using var cleanup = new NpgsqlCommand(
                    $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE; DROP ROLE IF EXISTS {quotedRole}",
                    admin);
                await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        private static async Task<(
            string Definition,
            string? ConstraintComment,
            string? SchemaComment)> ReadConstraintBaselineStateAsync(
            NpgsqlConnection connection,
            string schemaName,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT pg_catalog.pg_get_constraintdef(constraint_value.oid, false),
                       pg_catalog.obj_description(
                           constraint_value.oid,
                           'pg_constraint'),
                       pg_catalog.obj_description(
                           namespace_value.oid,
                           'pg_namespace')
                FROM pg_catalog.pg_constraint constraint_value
                JOIN pg_catalog.pg_class table_value
                  ON table_value.oid = constraint_value.conrelid
                JOIN pg_catalog.pg_namespace namespace_value
                  ON namespace_value.oid = table_value.relnamespace
                WHERE namespace_value.nspname = @schema_name
                  AND table_value.relname = 'runtime_truth'
                  AND constraint_value.conname = 'runtime_truth_allowlisted_result'
                """,
                connection);
            command.Parameters.AddWithValue("schema_name", schemaName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            var value = (
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
            Assert.False(await reader.ReadAsync(cancellationToken));
            return value;
        }

        public static string RequireConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES is required for REAL_POSTGRESQL Control Plane Integration; missing infrastructure is not a skip or pass.");
            }

            return value;
        }

        public SignedProviderResultV1 Sign(ProviderPayloadSpec payload)
            => SignJson(BuildPayloadJson(payload));

        public SignedProviderResultV1 SignJson(string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            try
            {
                var unsigned = new SignedProviderResultV1(
                    ActiveReleaseBomSha256,
                    ProviderKeyId,
                    payload.ToArray(),
                    Convert.ToBase64String(new byte[64]));
                var parsed = ProviderResultAuthorization.Parse(unsigned);
                var digest = Convert.FromHexString(
                    ProviderResultAuthorization.ComputeAuthorizationDigest(unsigned, parsed));
                try
                {
                    var signature = _providerKey.SignHash(
                        digest,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                    try
                    {
                        return unsigned with
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
                    CryptographicOperations.ZeroMemory(digest);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        public SignedProviderResultV1 WrapUntrustedJson(string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            return new SignedProviderResultV1(
                ActiveReleaseBomSha256,
                ProviderKeyId,
                payload,
                Convert.ToBase64String(new byte[64]));
        }

        public PostgresControlPlaneTruthStore CreateStore(
            ControlPlaneMutationFaultInjector? faultInjector = null)
            => new(
                new PostgresControlPlaneTruthStoreOptions(
                    _runtimeConnectionString,
                    _schemaName,
                    _runtimeRoleName,
                    _migrationRoleName),
                faultInjector);

        public async Task AppendTrustStateAsync(
            string contractId,
            string status,
            DateTimeOffset validFrom,
            DateTimeOffset validUntil,
            CancellationToken cancellationToken)
        {
            var revision = _trustRevisions[contractId] + 1;
            await _migrator.AppendProviderTrustStateAsync(
                TrustState(
                    revision,
                    contractId,
                    ProducerFor(contractId),
                    status,
                    validFrom,
                    validUntil),
                cancellationToken);
            _trustRevisions[contractId] = revision;
        }

        public async Task AssertNoMutationAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(0, await Store.CountTruthAsync(cancellationToken));
            Assert.Equal(0, await Store.CountReceiptsAsync(cancellationToken));
            Assert.Equal(0, await Store.CountQuarantineAsync(cancellationToken));
            Assert.Equal(0, await Store.CountOutboxAsync(cancellationToken));
        }

        public async Task AssertNoMutationWithoutRuntimeAttestationAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_migrationConnectionString);
            await connection.OpenAsync(cancellationToken);
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_schemaName);
            foreach (var table in new[]
                     {
                         "runtime_truth",
                         "idempotency_receipts",
                         "idempotency_quarantine",
                         "outbox"
                     })
            {
                var quotedTable = new NpgsqlCommandBuilder().QuoteIdentifier(table);
                await using var command = new NpgsqlCommand(
                    $"SELECT count(*) FROM {quotedSchema}.{quotedTable}",
                    connection);
                Assert.Equal(
                    0L,
                    Convert.ToInt64(
                        await command.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture));
            }
        }

        public async Task<bool> TableContainsAsync(
            string tableName,
            string value,
            CancellationToken cancellationToken)
        {
            var allowed = tableName switch
            {
                "idempotency_quarantine" => tableName,
                _ => throw new ArgumentOutOfRangeException(nameof(tableName))
            };
            await using var connection = new NpgsqlConnection(_migrationConnectionString);
            await connection.OpenAsync(cancellationToken);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schemaName)
                + "."
                + new NpgsqlCommandBuilder().QuoteIdentifier(allowed);
            await using var command = new NpgsqlCommand(
                $"SELECT EXISTS (SELECT 1 FROM {qualified} row_value WHERE row_to_json(row_value)::text LIKE @pattern)",
                connection);
            command.Parameters.AddWithValue("pattern", "%" + value + "%");
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }

        public Task ExecuteRuntimeAsync(string statement, CancellationToken cancellationToken)
            => ExecuteAsync(_runtimeConnectionString, statement, setSearchPath: true, cancellationToken);

        public Task ExecuteOwnerAsync(string statement, CancellationToken cancellationToken)
            => ExecuteAsync(_migrationConnectionString, statement, setSearchPath: true, cancellationToken);

        public async Task ExecuteRuntimeSetRoleToMigratorAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(cancellationToken);
            var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(_migrationRoleName);
            await using var command = new NpgsqlCommand($"SET ROLE {quotedRole}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task InsertUnknownProducerDirectlyAsync(
            SignedProviderResultV1 signed,
            CancellationToken cancellationToken)
        {
            var parsed = ProviderResultAuthorization.Parse(signed);
            var providerAuthorizationSha256 =
                ProviderResultAuthorization.ComputeAuthorizationDigest(signed, parsed);
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(cancellationToken);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schemaName) + ".runtime_truth";
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {qualified}
                    (truth_id, business_key_sha256, scope_sha256, idempotency_key_sha256,
                     record_sha256, schema_version, source_contract_id,
                     source_producer_module, soul_id, device_binding_id,
                     platform_account_id, trace_id, idempotency_key, occurred_at,
                     source_payload_sha256, source_payload_bytes, result_status,
                     active_release_bom_sha256, provider_key_id,
                     provider_trust_revision, provider_public_key_sha256,
                     provider_signature_base64, provider_authorization_sha256)
                VALUES
                    (@truth_id, @business_hash, @scope_hash, @idempotency_hash,
                     @record_hash, '1.0.0', 'identity.binding/v1',
                     'planner', @soul, @binding, @account, @trace_id,
                     @idempotency_key, @occurred_at, @payload_hash,
                     @payload_bytes, 'active', @bom, @key_id, 1, @public_key_hash,
                     @signature, @authorization_hash)
                """,
                connection);
            command.Parameters.AddWithValue("truth_id", Guid.NewGuid());
            command.Parameters.AddWithValue("business_hash", new string('7', 64));
            command.Parameters.AddWithValue("scope_hash", new string('8', 64));
            command.Parameters.AddWithValue("idempotency_hash", new string('9', 64));
            command.Parameters.AddWithValue("record_hash", new string('a', 64));
            command.Parameters.AddWithValue("soul", SoulA);
            command.Parameters.AddWithValue("binding", BindingA);
            command.Parameters.AddWithValue("account", AccountA);
            command.Parameters.AddWithValue("trace_id", TraceA);
            command.Parameters.AddWithValue(
                "idempotency_key",
                IdempotencyFor("invalid-direct"));
            command.Parameters.AddWithValue("occurred_at", OccurredAt);
            command.Parameters.AddWithValue("payload_hash", parsed.PayloadSha256);
            command.Parameters.AddWithValue("payload_bytes", signed.PayloadUtf8.ToArray());
            command.Parameters.AddWithValue("bom", signed.ActiveReleaseBomSha256);
            command.Parameters.AddWithValue("key_id", signed.ProviderKeyId);
            command.Parameters.AddWithValue("public_key_hash", ProviderPublicKeySha256);
            command.Parameters.AddWithValue("signature", signed.SignatureBase64);
            command.Parameters.AddWithValue("authorization_hash", providerAuthorizationSha256);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task InsertTrailingIdentifierIntoConstraintCloneAsOwnerAsync(
            string field,
            string invalidValue,
            CancellationToken cancellationToken)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["device_binding_id"] = BindingA,
                ["platform_account_id"] = AccountA,
                ["trace_id"] = TraceA,
                ["idempotency_key"] = IdempotencyFor("db-constraint")
            };
            if (!values.ContainsKey(field))
            {
                throw new ArgumentOutOfRangeException(nameof(field));
            }
            values[field] = invalidValue;

            await using var connection = new NpgsqlConnection(_migrationConnectionString);
            await connection.OpenAsync(cancellationToken);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schemaName)
                + ".runtime_truth";
            await using (var create = new NpgsqlCommand(
                             $"CREATE TEMP TABLE control_plane_id_constraint_probe "
                             + $"(LIKE {qualified} INCLUDING DEFAULTS INCLUDING CONSTRAINTS)",
                             connection))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            var payloadBytes = Encoding.UTF8.GetBytes("{}");
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO pg_temp.control_plane_id_constraint_probe
                    (truth_id, business_key_sha256, scope_sha256, idempotency_key_sha256,
                     record_sha256, schema_version, source_contract_id,
                     source_producer_module, soul_id, device_binding_id,
                     platform_account_id, trace_id, idempotency_key, occurred_at,
                     source_payload_sha256, source_payload_bytes, result_status,
                     active_release_bom_sha256, provider_key_id,
                     provider_trust_revision, provider_public_key_sha256,
                     provider_signature_base64, provider_authorization_sha256)
                VALUES
                    (@truth_id, @business_hash, @scope_hash, @idempotency_hash,
                     @record_hash, '1.0.0', 'identity.binding/v1', 'binding',
                     @soul, @binding, @account, @trace_id, @idempotency_key,
                     @occurred_at, @payload_hash, @payload_bytes, 'active', @bom,
                     'provider-test-key:v1', 1, @public_key_hash, @signature,
                     @authorization_hash)
                """,
                connection);
            insert.Parameters.AddWithValue("truth_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("business_hash", new string('1', 64));
            insert.Parameters.AddWithValue("scope_hash", new string('2', 64));
            insert.Parameters.AddWithValue("idempotency_hash", new string('3', 64));
            insert.Parameters.AddWithValue("record_hash", new string('4', 64));
            insert.Parameters.AddWithValue("soul", SoulA);
            insert.Parameters.AddWithValue("binding", values["device_binding_id"]);
            insert.Parameters.AddWithValue("account", values["platform_account_id"]);
            insert.Parameters.AddWithValue("trace_id", values["trace_id"]);
            insert.Parameters.AddWithValue("idempotency_key", values["idempotency_key"]);
            insert.Parameters.AddWithValue("occurred_at", OccurredAt);
            insert.Parameters.AddWithValue(
                "payload_hash",
                Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant());
            insert.Parameters.AddWithValue("payload_bytes", payloadBytes);
            insert.Parameters.AddWithValue("bom", ActiveReleaseBomSha256);
            insert.Parameters.AddWithValue("public_key_hash", ProviderPublicKeySha256);
            insert.Parameters.AddWithValue(
                "signature",
                Convert.ToBase64String(new byte[64]));
            insert.Parameters.AddWithValue("authorization_hash", new string('5', 64));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        public Task TamperSchemaAsync(string tamper, CancellationToken cancellationToken)
            => tamper switch
            {
                "trigger" => ExecuteOwnerAsync(
                    "DROP TRIGGER runtime_truth_provider_trust ON runtime_truth",
                    cancellationToken),
                "constraint" => ExecuteOwnerAsync(
                    "ALTER TABLE runtime_truth DROP CONSTRAINT runtime_truth_allowlisted_result",
                    cancellationToken),
                "constraint-parentheses" => ExecuteOwnerAsync(
                    WidenedAllowlistConstraintSql,
                    cancellationToken),
                "function" => ExecuteOwnerAsync(
                    """
                    CREATE OR REPLACE FUNCTION reject_control_plane_row_mutation()
                    RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER
                    SET search_path=pg_catalog
                    AS $function$ BEGIN RETURN OLD; END; $function$
                    """,
                    cancellationToken),
                "function-strict" => ExecuteOwnerAsync(
                    """
                    ALTER FUNCTION commit_control_plane_atom(
                        uuid, text, text, text, text, text, text, text, text,
                        text, text, text, text, timestamp with time zone, text,
                        bytea, text, text, text, bigint, text, text, text, text,
                        jsonb, uuid, text, text)
                    STRICT
                    """,
                    cancellationToken),
                "index" => ExecuteOwnerAsync(
                    "DROP INDEX outbox_exact_scope_idx",
                    cancellationToken),
                "acl" => ExecuteOwnerAsync(
                    $"GRANT INSERT ON runtime_truth TO {new NpgsqlCommandBuilder().QuoteIdentifier(_runtimeRoleName)}",
                    cancellationToken),
                "column-insert-acl" => ExecuteOwnerAsync(
                    $"GRANT INSERT (truth_id) ON runtime_truth TO {new NpgsqlCommandBuilder().QuoteIdentifier(_runtimeRoleName)}",
                    cancellationToken),
                "column" => ExecuteOwnerAsync(
                    "ALTER TABLE runtime_truth ADD COLUMN unexpected text COLLATE \"C\" NOT NULL DEFAULT 'x'",
                    cancellationToken),
                "extra-table" => ExecuteOwnerAsync(
                    "CREATE TABLE unregistered_control_plane_table(value integer)",
                    cancellationToken),
                "role-membership" => ExecuteOwnerAsync(
                    $"GRANT {new NpgsqlCommandBuilder().QuoteIdentifier(_runtimeRoleName)} TO {new NpgsqlCommandBuilder().QuoteIdentifier(_migrationRoleName)}",
                    cancellationToken),
                "role-inherit" => ExecuteOwnerAsync(
                    $"ALTER ROLE {new NpgsqlCommandBuilder().QuoteIdentifier(_runtimeRoleName)} INHERIT",
                    cancellationToken),
                "collation-shadow" => ExecuteOwnerAsync(
                    CollationShadowTamperSql(),
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(tamper))
            };

        private string CollationShadowTamperSql()
        {
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_schemaName);
            return $"""
                CREATE COLLATION "C" FROM pg_catalog."C";
                ALTER TABLE runtime_truth ALTER COLUMN result_status
                    TYPE text COLLATE {quotedSchema}."C" USING result_status::text;
                DO $verify_collation$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_attribute attribute_value
                        JOIN pg_catalog.pg_class table_value
                          ON table_value.oid = attribute_value.attrelid
                        JOIN pg_catalog.pg_namespace table_namespace
                          ON table_namespace.oid = table_value.relnamespace
                        JOIN pg_catalog.pg_collation collation_value
                          ON collation_value.oid = attribute_value.attcollation
                        JOIN pg_catalog.pg_namespace collation_namespace
                          ON collation_namespace.oid = collation_value.collnamespace
                        WHERE table_namespace.nspname = '{_schemaName}'
                          AND table_value.relname = 'runtime_truth'
                          AND attribute_value.attname = 'result_status'
                          AND collation_namespace.nspname = '{_schemaName}')
                    THEN
                        RAISE EXCEPTION 'collation shadow tamper fixture did not bind';
                    END IF;
                END;
                $verify_collation$;
                """;
        }

        private async Task AppendInitialTrustStatesAsync(CancellationToken cancellationToken)
        {
            var validFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var validUntil = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
            foreach (var provider in Providers)
            {
                await _migrator.AppendProviderTrustStateAsync(
                    TrustState(
                        revision: 1,
                        provider.ContractId,
                        provider.ProducerModule,
                        status: "ACTIVE",
                        validFrom,
                        validUntil),
                    cancellationToken);
                _trustRevisions.Add(provider.ContractId, 1);
            }
        }

        private ProviderTrustStateV1 TrustState(
            long revision,
            string contractId,
            string producerModule,
            string status,
            DateTimeOffset validFrom,
            DateTimeOffset validUntil)
            => new(
                revision,
                contractId,
                producerModule,
                ActiveReleaseBomSha256,
                ProviderKeyId,
                _providerPublicKeySpkiBase64,
                ProviderPublicKeySha256,
                status,
                validFrom,
                validUntil);

        private async Task ExecuteAsync(
            string connectionString,
            string statement,
            bool setSearchPath,
            CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_schemaName);
            var sql = setSearchPath
                ? $"SET search_path TO {quotedSchema}; {statement}"
                : statement;
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task AssertRuntimeIdentityAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT session_user::text, current_user::text",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(_runtimeRoleName, reader.GetString(0));
            Assert.Equal(_runtimeRoleName, reader.GetString(1));
            Assert.False(await reader.ReadAsync(cancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            _providerKey.Dispose();
            await CleanupAsync(
                _migrationConnectionString,
                _schemaName,
                _runtimeRoleName,
                CancellationToken.None);
        }

        private static async Task CleanupAsync(
            string connectionString,
            string schemaName,
            string runtimeRoleName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schemaName);
                var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRoleName);
                await using var command = new NpgsqlCommand(
                    $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE; DROP ROLE IF EXISTS {quotedRole}",
                    connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // Cleanup cannot replace the original test failure.
            }
        }
    }
}
