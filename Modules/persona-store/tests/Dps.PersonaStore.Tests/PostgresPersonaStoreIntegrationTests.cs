using Dps.Binding.Contracts;
using Dps.PersonaStore.Contracts;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Dps.PersonaStore.Tests;

public sealed class PostgresPersonaStoreIntegrationTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSoul = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BindingId = "db_cccccccccccccccccccccccccccccccc";
    private const string AccountId = "pa_dddddddddddddddddddddddddddddddd";
    private static readonly DateTimeOffset OccurredAt = new(2026, 7, 14, 4, 0, 0, TimeSpan.Zero);
    private static readonly string RequestHmacKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x6b, 32).ToArray());
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task SameKeySameHashIsOneAtomicRevisionReceiptOutboxAndCurrentRead()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var command = Put(0, "persona-pg-idempotent");

        var first = await database.Store.PutAsync(command, TestCancellation);
        var duplicate = await database.Store.PutAsync(command, TestCancellation);

        Assert.Equal(first, duplicate);
        Assert.Equal(first, await database.Store.GetCurrentAsync(Soul, BindingId, AccountId, TestCancellation));
        Assert.Equal("calm", (await database.Store.ExportHistoryV1Async(Export("persona-pg-export-idempotent"), TestCancellation)).Revisions[^1].Traits!["tone"]);
        Assert.Equal(1, await database.Store.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountTraitPayloadsAsync(TestCancellation));
        var outbox = Assert.Single(await database.Store.ReadPendingOutboxAsync(Soul, BindingId, AccountId, TestCancellation));
        Assert.Equal(first, outbox.Payload);
        Assert.Matches("^[a-f0-9]{64}$", outbox.PayloadSha256);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameKeyDifferentHashIsQuarantinedWithoutAnotherMutation()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var command = Put(0, "persona-pg-conflict");
        _ = await database.Store.PutAsync(command, TestCancellation);

        var conflicting = command with
        {
            Traits = new Dictionary<string, string> { ["tone"] = "direct" }
        };
        var crashAfterQuarantineCommit = database.CreateStore((stage, _) =>
            stage == PersonaMutationStage.ConflictQuarantineCommitted
                ? ValueTask.FromException(new SimulatedPersonaCrashException())
                : ValueTask.CompletedTask);
        await Assert.ThrowsAsync<SimulatedPersonaCrashException>(async () =>
            await crashAfterQuarantineCommit.PutAsync(conflicting, TestCancellation));
        Assert.Equal(1, await database.Store.CountQuarantineAsync(TestCancellation));
        await Assert.ThrowsAsync<PersonaIdempotencyConflictException>(async () =>
            await database.Store.PutAsync(conflicting, TestCancellation));

        Assert.Equal(1, await database.Store.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountQuarantineAsync(TestCancellation));
        Assert.False(await database.TableContainsAsync("idempotency_quarantine", "persona-pg-conflict"));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentOptimisticRevisionHasExactlyOneWinnerAndConcurrentReplayIsNoOp()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var first = await database.Store.PutAsync(Put(0, "persona-pg-concurrency-seed"), TestCancellation);
        var competing = Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                await database.Store.PutAsync(Put(first.PersonaRevision, "persona-pg-competing-" + index) with
                {
                    Traits = new Dictionary<string, string> { ["tone"] = index % 2 == 0 ? "warm" : "direct" }
                }, TestCancellation);
                return true;
            }
            catch (PersonaRevisionConflictException)
            {
                return false;
            }
        });
        var outcomes = await Task.WhenAll(competing);
        Assert.Single(outcomes, static value => value);

        var replay = Put(2, "persona-pg-concurrent-replay") with
        {
            Traits = new Dictionary<string, string> { ["tone"] = "formal" },
            OccurredAt = OccurredAt.AddMinutes(3)
        };
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => database.Store.PutAsync(replay, TestCancellation).AsTask()));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(3, await database.Store.CountRevisionsAsync(TestCancellation));
        Assert.Equal(3, await database.Store.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RestartReplayAndExactScopeNeverLeakAcrossSoulDeviceOrAccount()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var first = await database.Store.PutAsync(Put(0, "persona-pg-restart"), TestCancellation);
        var restarted = database.CreateStore();
        var current = await restarted.GetCurrentAsync(Soul, BindingId, AccountId, TestCancellation);

        Assert.Equal(first, current);
        Assert.Equal(first, await restarted.PutAsync(Put(0, "persona-pg-restart"), TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await restarted.GetCurrentAsync(OtherSoul, BindingId, AccountId, TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await restarted.GetCurrentAsync(Soul, "db_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", AccountId, TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await restarted.GetCurrentAsync(Soul, BindingId, "pa_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", TestCancellation));
        Assert.Empty(await database.ReadRowsForScopeAsync(OtherSoul, BindingId, AccountId));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CorrectionAppendsAndLivePrimaryLogicalDeletionRemovesPayloadBehindAuditedTombstone()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var first = await database.Store.PutAsync(Put(0, "persona-pg-first"), TestCancellation);
        var corrected = await database.Store.PutAsync(Put(first.PersonaRevision, "persona-pg-correction") with
        {
            Traits = new Dictionary<string, string> { ["tone"] = "warm" }
        }, TestCancellation);
        var retainedHistory = await database.Store.ExportHistoryV1Async(Export("persona-pg-export-retained"), TestCancellation);
        Assert.Equal("persona.history.export/v1", retainedHistory.ContractId);
        Assert.Equal("sensitive", retainedHistory.PrivacyClass);
        Assert.Equal("retained", retainedHistory.LivePrimaryPayloadState);
        Assert.Equal([1L, 2L], retainedHistory.Revisions.Select(static value => value.Revision.PersonaRevision));
        Assert.Equal(["calm", "warm"], retainedHistory.Revisions.Select(static value => value.Traits!["tone"]));
        Assert.All(retainedHistory.Revisions, static value => Assert.Equal("retained", value.LivePrimaryPayloadState));
        Assert.Equal(1, await database.AdminCountAsync("persona_hmac_keys"));
        var deleted = await database.Store.DeleteAsync(new DeletePersonaCommand(
            Soul,
            BindingId,
            AccountId,
            corrected.PersonaRevision,
            [new string('c', 64)],
            Trace("persona-delete"),
            Idem("persona-pg-delete"),
            OccurredAt.AddMinutes(3)), TestCancellation);

        Assert.Equal("deleted", deleted.Status);
        Assert.Equal(0, await database.Store.CountTraitPayloadsAsync(TestCancellation));
        Assert.Equal(0, await database.AdminCountAsync("persona_hmac_keys"));
        Assert.Equal(1, await database.Store.CountErasureAuditAsync(TestCancellation));
        var deletionAudit = await database.ReadDeletionAuditAsync();
        Assert.Equal("live-postgresql-primary-only", deletionAudit.DeletionScope);
        Assert.Equal("audited-live-store-logical-deletion", deletionAudit.PolicyAction);
        Assert.Null(deletionAudit.ExternalDestructionReceiptSha256);
        Assert.Equal([1L, 2L, 3L], (await database.Store.ReadHistoryAsync(Soul, BindingId, AccountId, TestCancellation)).Select(static value => value.PersonaRevision));
        var deletedHistory = await database.Store.ExportHistoryV1Async(Export("persona-pg-export-deleted"), TestCancellation);
        Assert.Equal("live-primary-logically-deleted", deletedHistory.LivePrimaryPayloadState);
        Assert.Equal([1L, 2L, 3L], deletedHistory.Revisions.Select(static value => value.Revision.PersonaRevision));
        Assert.All(deletedHistory.Revisions, static value =>
        {
            Assert.Equal("live-primary-logically-deleted", value.LivePrimaryPayloadState);
            Assert.Null(value.Traits);
        });
        Assert.False(await database.TableContainsAsync("persona_revisions", "calm"));
        Assert.False(await database.TableContainsAsync("persona_revisions", "warm"));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ImmutableExportReceiptReplaysTheExactSnapshotAcrossCorrectionDeletionAndRestart()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var first = await database.Store.PutAsync(Put(0, "persona-pg-export-stable-seed"), TestCancellation);
        var exportCommand = Export("persona-pg-export-stable");
        var duplicateGate = await database.HoldSoulAdvisoryLockAsync(Soul);
        Task<PersonaHistoryExportV1> firstExportTask;
        Task<PersonaHistoryExportV1> duplicateExportTask;
        try
        {
            firstExportTask = database.Store.ExportHistoryV1Async(exportCommand, TestCancellation).AsTask();
            duplicateExportTask = database.CreateStore().ExportHistoryV1Async(exportCommand, TestCancellation).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(250), TestCancellation);
        }
        finally
        {
            await duplicateGate.DisposeAsync();
        }
        var duplicateExports = await Task.WhenAll(firstExportTask, duplicateExportTask);
        var initial = duplicateExports[0];
        Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(duplicateExports[1]));
        Assert.Equal(1, await database.AdminCountAsync("persona_export_receipts"));
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(initial).Length,
            await database.AdminExportWireBytesAsync(initial.IdempotencyKey));

        var corrected = await database.Store.PutAsync(Put(first.PersonaRevision, "persona-pg-export-stable-correction") with
        {
            Traits = new Dictionary<string, string> { ["tone"] = "warm" }
        }, TestCancellation);
        var mutationReachedBeforeCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowMutationCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletingStore = database.CreateStore(async (stage, cancellationToken) =>
        {
            if (stage != PersonaMutationStage.BeforeCommit) return;
            mutationReachedBeforeCommit.TrySetResult();
            await allowMutationCommit.Task.WaitAsync(cancellationToken);
        });
        Task<PersonaRevisionV1> deletionTask;
        Task<PersonaHistoryExportV1> afterDeletionExportTask;
        try
        {
            deletionTask = deletingStore.DeleteAsync(new DeletePersonaCommand(
                Soul, BindingId, AccountId, corrected.PersonaRevision, [new string('d', 64)],
                Trace("persona-pg-export-stable-delete"), Idem("persona-pg-export-stable-delete"),
                OccurredAt.AddMinutes(4)), TestCancellation).AsTask();
            await mutationReachedBeforeCommit.Task.WaitAsync(TestCancellation);
            afterDeletionExportTask = database.CreateStore().ExportHistoryV1Async(
                Export("persona-pg-export-after-concurrent-delete"),
                TestCancellation).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(250), TestCancellation);
            Assert.False(afterDeletionExportTask.IsCompleted);
        }
        finally
        {
            allowMutationCommit.TrySetResult();
        }
        var deletion = await deletionTask;
        var afterDeletionExport = await afterDeletionExportTask;
        Assert.Equal(deletion.PersonaRevision, afterDeletionExport.SnapshotPersonaRevision);
        Assert.Equal("live-primary-logically-deleted", afterDeletionExport.LivePrimaryPayloadState);
        Assert.All(afterDeletionExport.Revisions, static item => Assert.Null(item.Traits));

        var replay = await database.CreateStore().ExportHistoryV1Async(exportCommand, TestCancellation);
        Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(replay));
        Assert.Equal(2, await database.AdminCountAsync("persona_export_receipts"));
        var latest = await database.Store.ExportHistoryV1Async(Export("persona-pg-export-latest"), TestCancellation);
        Assert.Equal(3, latest.SnapshotPersonaRevision);
        await Assert.ThrowsAsync<PersonaIdempotencyConflictException>(async () =>
            await database.Store.ExportHistoryV1Async(exportCommand with { TraceId = Trace("persona-pg-export-conflict") }, TestCancellation));
        Assert.Equal(1, await database.AdminCountAsync("persona_export_receipt_quarantine"));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await database.Store.ExportHistoryV1Async(Export("persona-pg-export-wrong-device") with
            {
                DeviceBindingId = "db_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
            }, TestCancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await database.Store.ExportHistoryV1Async(Export("persona-pg-export-wrong-account") with
            {
                PlatformAccountId = "pa_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
            }, TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RetainedTraitTamperingIsRejectedByThePostgresExportHmacCheck()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var revision = await database.Store.PutAsync(Put(0, "persona-pg-export-hmac-seed"), TestCancellation);
        var exportCommand = Export("persona-pg-export-direct-malformed");
        var requestHmacKey = Convert.FromBase64String(RequestHmacKey);
        try
        {
            var validExport = PersonaMutationCanonicalizer.CreateHistoryExport(
                PersonaMutationCanonicalizer.Normalize(exportCommand),
                PersonaHistoryExportItemV1.Retained,
                Array.AsReadOnly(new[]
                {
                    new PersonaHistoryExportItemV1(
                        revision,
                        PersonaHistoryExportItemV1.Retained,
                        new Dictionary<string, string> { ["tone"] = "calm" })
                }),
                requestHmacKey);
            var malformedOuter = JsonNode.Parse(JsonSerializer.Serialize(validExport))!.AsObject();
            malformedOuter["trace_id"] = null;
            malformedOuter["occurred_at"] = null;
            malformedOuter["live_primary_payload_state"] = null;
            var malformedNested = JsonNode.Parse(JsonSerializer.Serialize(validExport))!.AsObject();
            malformedNested["revisions"] = new JsonArray(new JsonObject
            {
                ["revision"] = new JsonObject { ["persona_revision"] = 1 },
                ["live_primary_payload_state"] = PersonaHistoryExportItemV1.Retained,
                ["traits"] = new JsonObject { ["tone"] = "calm" }
            });

            foreach (var malformed in new[] { malformedOuter, malformedNested })
            {
                await Assert.ThrowsAsync<PostgresException>(() =>
                    database.RuntimeRecordMalformedExportReceiptAsync(validExport, malformed.ToJsonString(), requestHmacKey));
                Assert.Equal(0, await database.AdminCountAsync("persona_export_receipts"));
            }

            var forgedPayloadSha256 = new string(validExport.ExportPayloadSha256[0] == 'f' ? 'e' : 'f', 64);
            var forgedReceiptHmacSha256 = PersonaMutationCanonicalizer.HashExportReceipt(
                validExport.ExportRequestHmacSha256,
                validExport.SnapshotPersonaRevision,
                validExport.SnapshotCursorHmacSha256,
                forgedPayloadSha256,
                requestHmacKey);
            var selfConsistentForgery = validExport with
            {
                ExportPayloadSha256 = forgedPayloadSha256,
                ExportReceiptHmacSha256 = forgedReceiptHmacSha256,
                ExportReceiptId = "pexport_" + forgedReceiptHmacSha256
            };
            await Assert.ThrowsAsync<PostgresException>(() => database.RuntimeRecordMalformedExportReceiptAsync(
                selfConsistentForgery,
                JsonSerializer.Serialize(selfConsistentForgery),
                requestHmacKey));
            Assert.Equal(0, await database.AdminCountAsync("persona_export_receipts"));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(requestHmacKey);
        }

        await database.TamperRetainedTraitsAsMigratorAsync();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await database.Store.ExportHistoryV1Async(Export("persona-pg-export-hmac-tampered"), TestCancellation));
        Assert.Equal(0, await database.AdminCountAsync("persona_export_receipts"));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(PersonaMutationStage.BindingFenceHeld)]
    [InlineData(PersonaMutationStage.BeforeCommit)]
    public async Task AtomicApiRollbackBoundaryLeavesNoPartialMutationAndCleanRestartRecovers(PersonaMutationStage stage)
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var crashing = database.CreateStore((actual, _) =>
            actual == stage ? ValueTask.FromException(new SimulatedPersonaCrashException()) : ValueTask.CompletedTask);
        var command = Put(0, "persona-pg-crash");

        await Assert.ThrowsAsync<SimulatedPersonaCrashException>(async () =>
            await crashing.PutAsync(command, TestCancellation));
        await database.AssertNoMutationAsync();

        var recovered = await database.CreateStore().PutAsync(command, TestCancellation);
        Assert.Equal("active", recovered.Status);
        Assert.Equal(1, await database.Store.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task BindingFenceRemainsHeldAcrossPersonaCommitBeforeReleaseAndThenFailsClosed()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var fenceReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedStore = database.CreateStore(async (stage, cancellationToken) =>
        {
            if (stage != PersonaMutationStage.TransactionCommittedWithBindingFenceHeld) return;
            fenceReached.TrySetResult();
            await allowCommit.Task.WaitAsync(cancellationToken);
        });

        var mutation = gatedStore.PutAsync(Put(0, "persona-pg-fenced"), TestCancellation).AsTask();
        await fenceReached.Task.WaitAsync(TimeSpan.FromSeconds(2), TestCancellation);
        var revoke = database.Binding.RevokeAsync(TestCancellation);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestCancellation);
        Assert.False(revoke.IsCompleted);

        allowCommit.TrySetResult();
        var committed = await mutation;
        await revoke;
        Assert.Equal("active", committed.Status);
        Assert.Equal(committed, await database.Store.PutAsync(Put(0, "persona-pg-fenced"), TestCancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await database.Store.PutAsync(Put(committed.PersonaRevision, "persona-pg-after-revoke"), TestCancellation));
        Assert.Equal(1, await database.Store.CountRevisionsAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LogicalDeletionRollbackBeforeCommitRestoresPayloadKeyAndAudit()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        var first = await database.Store.PutAsync(Put(0, "persona-pg-before-logical-deletion"), TestCancellation);
        var crashing = database.CreateStore((stage, _) =>
            stage == PersonaMutationStage.BeforeCommit
                ? ValueTask.FromException(new SimulatedPersonaCrashException())
                : ValueTask.CompletedTask);
        await Assert.ThrowsAsync<SimulatedPersonaCrashException>(async () =>
            await crashing.DeleteAsync(new DeletePersonaCommand(
                Soul, BindingId, AccountId, first.PersonaRevision, [new string('c', 64)],
                Trace("delete-crash"), Idem("persona-pg-delete-crash"), OccurredAt.AddMinutes(2)), TestCancellation));
        Assert.Equal(1, await database.Store.CountTraitPayloadsAsync(TestCancellation));
        Assert.Equal(1, await database.AdminCountAsync("persona_hmac_keys"));
        Assert.Equal(0, await database.Store.CountErasureAuditAsync(TestCancellation));
        Assert.Equal("active", (await database.Store.GetCurrentAsync(Soul, BindingId, AccountId, TestCancellation)).Status);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task AppendOnlyTriggersAndRuntimeRoleBoundaryFailClosed()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        _ = await database.Store.PutAsync(Put(0, "persona-pg-immutable"), TestCancellation);

        foreach (var operation in new[] { "UPDATE", "DELETE", "TRUNCATE" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => database.MutateLedgerAsMigratorAsync(operation));
            await Assert.ThrowsAsync<PostgresException>(() => database.MutateLedgerAsRuntimeAsync(operation));
        }
        await Assert.ThrowsAsync<PostgresException>(() => database.MutateLedgerAsRuntimeAsync("INSERT"));
        await Assert.ThrowsAsync<PostgresException>(database.RuntimeReadRawKeyAsync);
        await Assert.ThrowsAsync<PostgresException>(database.RuntimeCallInternalHelperAsync);
        await Assert.ThrowsAsync<PostgresException>(database.RuntimeAdvanceSequenceAsync);
        await Assert.ThrowsAsync<PostgresException>(() => database.DeletePersonaMaterialAsMigratorAsync("trait_payloads"));
        await Assert.ThrowsAsync<PostgresException>(() => database.DeletePersonaMaterialAsMigratorAsync("persona_hmac_keys"));
        await Assert.ThrowsAsync<PostgresException>(database.RuntimeCreateTableAsync);
        await Assert.ThrowsAsync<PostgresException>(database.RuntimeReadExportReceiptsAsync);
        Assert.Equal(1, await database.Store.CountRevisionsAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DatabaseStoresNoRawAliasesPhoneCredentialOrSecretColumns()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        _ = await database.Store.PutAsync(Put(0, "persona-pg-privacy"), TestCancellation);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await database.Store.PutAsync(Put(1, "invalid-email") with { IdempotencyKey = "person@example.com" }, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await database.Store.PutAsync(Put(1, "invalid-phone") with { IdempotencyKey = "60123456789" }, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await database.Store.PutAsync(Put(1, "persona-pg-private-value") with
            {
                Traits = new Dictionary<string, string> { ["tone"] = "secret-token" }
            }, TestCancellation));

        var columns = await database.ColumnNamesAsync();
        var forbidden = new[] { "email", "phone", "credential", "secret", "password", "raw_alias", "embedding", "vector" };
        Assert.DoesNotContain(columns, column => forbidden.Any(term => column.Contains(term, StringComparison.OrdinalIgnoreCase)));
        Assert.False(await database.SchemaContainsAsync("person@example.com"));
        Assert.False(await database.SchemaContainsAsync("60123456789"));
        Assert.False(await database.SchemaContainsAsync("secret-token"));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DeferredBundleRejectsMismatchedRevisionReceiptAndOutboxDigest()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        _ = await database.Store.PutAsync(Put(0, "persona-pg-bundle-seed"), TestCancellation);

        await Assert.ThrowsAsync<PostgresException>(database.InsertMismatchedBundleAsync);

        Assert.Equal(1, await database.Store.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Store.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CatalogDriftFailsBeforeIdempotentMigrationCanRepairIt()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        await database.DropTriggerAsMigratorAsync("persona_revisions_append_only_rows");

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.InitializeAsync(TestCancellation));

        Assert.False(await database.TriggerExistsAsync("persona_revisions_append_only_rows"));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("function-security-definer")]
    [InlineData("table-acl")]
    [InlineData("sequence-acl")]
    [InlineData("schema-acl")]
    [InlineData("table-owner")]
    [InlineData("column-acl")]
    public async Task OwnerAclSequenceAndFunctionSecurityDriftFailBeforeMigrationDdl(string driftKind)
    {
        await using var database = await PersonaDatabase.CreateAsync();
        await database.DriftCatalogAsMigratorAsync(driftKind);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.InitializeAsync(TestCancellation));
        if (driftKind == "column-acl") Assert.True(await database.HasExplicitRuntimeColumnPrivilegeAsync());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExistingRuntimeOwnedEmptySchemaIsRejectedBeforeAnyDdlOrDcl()
    {
        await using var database = await PersonaDatabase.CreateRuntimeOwnedEmptySchemaAsync();
        var before = await database.ReadSchemaBaselineAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.InitializeAsync(TestCancellation));

        var after = await database.ReadSchemaBaselineAsync();
        Assert.Equal(before, after);
        Assert.Equal(database.RuntimeRole, after.Owner);
        Assert.Equal(0, after.ObjectCount);
        Assert.False(after.HasMigrationLedger);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MigrationLedgerHashTamperingFailsBeforeMigrationDdl()
    {
        await using var database = await PersonaDatabase.CreateAsync();
        await database.TamperMigrationLedgerAsMigratorAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.InitializeAsync(TestCancellation));
        Assert.Contains("migration ledger checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Integration")]
    public void MissingDatabaseConfigurationFailsRatherThanSkipping()
    {
        var configured = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Throws<InvalidOperationException>(PersonaDatabase.RequireConnectionString);
            return;
        }
        Assert.Equal(configured, PersonaDatabase.RequireConnectionString());
    }

    private static PutPersonaCommand Put(long expectedRevision, string idempotencyKey) => new(
        Soul,
        BindingId,
        AccountId,
        expectedRevision,
        new Dictionary<string, string> { ["tone"] = "calm" },
        [new string('a', 64)],
        Trace("persona-pg"),
        Idem(idempotencyKey),
        OccurredAt.AddMinutes(expectedRevision));

    private static ExportPersonaHistoryCommand Export(string idempotencyKey) => new(
        Soul,
        BindingId,
        AccountId,
        Trace(idempotencyKey),
        Idem(idempotencyKey),
        OccurredAt.AddMinutes(10));

    private static string Trace(string label) => "trace_" + Digest("trace:" + label)[..32];
    private static string Idem(string label) => "idem_" + Digest("idempotency:" + label);
    private static string Digest(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class SimulatedPersonaCrashException : Exception
    {
    }

    private sealed class MutableBindingFenceClient : IBindingMutationFenceClient
    {
        private readonly SemaphoreSlim _fence = new(1, 1);
        private long _revision = 7;
        private long _sequence;
        private bool _active = true;

        public async Task<IBindingMutationFenceLease> AcquireAsync(
            AcquireBindingMutationFenceCommand command,
            CancellationToken cancellationToken = default)
        {
            await _fence.WaitAsync(cancellationToken);
            try
            {
                if (command.SoulId != Soul || command.DeviceBindingId != BindingId || command.PlatformAccountId != AccountId)
                    throw new KeyNotFoundException("Unknown binding scope.");
                if (!_active) throw new InvalidOperationException("Binding is not active.");
                var sequence = Interlocked.Increment(ref _sequence);
                return new Lease(_fence, new BindingMutationFenceV1(
                    "1.0.0",
                    "identity.binding.mutation.fence/v1",
                    "binding",
                    Soul,
                    BindingId,
                    AccountId,
                    command.TraceId,
                    command.IdempotencyKey,
                    command.OccurredAt,
                    "sensitive",
                    _revision,
                    "bfence_" + new string('f', 63) + (sequence % 16).ToString("x", System.Globalization.CultureInfo.InvariantCulture),
                    sequence,
                    "held"));
            }
            catch
            {
                _fence.Release();
                throw;
            }
        }

        public async Task RevokeAsync(CancellationToken cancellationToken = default)
        {
            await _fence.WaitAsync(cancellationToken);
            try
            {
                _active = false;
                _revision++;
            }
            finally
            {
                _fence.Release();
            }
        }

        private sealed class Lease(SemaphoreSlim fence, BindingMutationFenceV1 receipt) : IBindingMutationFenceLease
        {
            private int _disposed;
            public BindingMutationFenceV1 Receipt { get; } = receipt;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) fence.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class PersonaDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _runtimeConnectionString;
        private readonly string _schema;
        private readonly string _runtimeRole;

        private PersonaDatabase(
            string adminConnectionString,
            string runtimeConnectionString,
            string schema,
            string runtimeRole,
            MutableBindingFenceClient binding)
        {
            _adminConnectionString = adminConnectionString;
            _runtimeConnectionString = runtimeConnectionString;
            _schema = schema;
            _runtimeRole = runtimeRole;
            Binding = binding;
            Store = CreateStore();
        }

        public MutableBindingFenceClient Binding { get; }
        public PostgresPersonaStore Store { get; }
        public string RuntimeRole => _runtimeRole;

        public static Task<PersonaDatabase> CreateAsync() => CreateCoreAsync(initialize: true, createRuntimeOwnedEmptySchema: false);

        public static Task<PersonaDatabase> CreateRuntimeOwnedEmptySchemaAsync() =>
            CreateCoreAsync(initialize: false, createRuntimeOwnedEmptySchema: true);

        private static string QuoteLiteral(string value) =>
            '\'' + value.Replace("'", "''", StringComparison.Ordinal) + '\'';

        private static async Task<PersonaDatabase> CreateCoreAsync(bool initialize, bool createRuntimeOwnedEmptySchema)
        {
            var adminConnectionString = RequireConnectionString();
            var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            if (adminBuilder.Port == 55434 ||
                string.Equals(adminBuilder.Database, "dps_gbrain_company", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Persona Store Integration must never use the persistent GBrain Company database.");
            }
            if (string.IsNullOrWhiteSpace(adminBuilder.Username))
                throw new InvalidOperationException("DPS_TEST_POSTGRES requires an explicit migrator username.");

            var suffix = Guid.NewGuid().ToString("N")[..20];
            var schema = "persona_it_" + suffix;
            var runtimeRole = "persona_rt_" + suffix;
            var runtimePassword = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            await using (var admin = new NpgsqlConnection(adminConnectionString))
            {
                await admin.OpenAsync(TestCancellation);
                await using (var version = new NpgsqlCommand("SHOW server_version_num", admin))
                {
                    var exact = Convert.ToInt32(await version.ExecuteScalarAsync(TestCancellation), System.Globalization.CultureInfo.InvariantCulture);
                    if (exact != 180004) throw new InvalidOperationException($"Persona Store Integration requires exact PostgreSQL 18.4; server_version_num was {exact}.");
                }
                var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRole);
                await using var role = new NpgsqlCommand($"CREATE ROLE {quotedRole} LOGIN PASSWORD {QuoteLiteral(runtimePassword)} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT", admin);
                await role.ExecuteNonQueryAsync(TestCancellation);
                if (createRuntimeOwnedEmptySchema)
                {
                    var builder = new NpgsqlCommandBuilder();
                    var quotedSchema = builder.QuoteIdentifier(schema);
                    await using var createSchema = new NpgsqlCommand(
                        $"CREATE SCHEMA {quotedSchema} AUTHORIZATION {quotedRole}", admin);
                    await createSchema.ExecuteNonQueryAsync(TestCancellation);
                }
            }

            var runtimeBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Username = runtimeRole,
                Password = runtimePassword,
                Pooling = false
            };
            var binding = new MutableBindingFenceClient();
            var database = new PersonaDatabase(adminConnectionString, runtimeBuilder.ConnectionString, schema, runtimeRole, binding);
            try
            {
                if (initialize) await database.Store.InitializeAsync(TestCancellation);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public static string RequireConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("DPS_TEST_POSTGRES is required for persona-store Integration tests; missing infrastructure is not a skip.");
            return value;
        }

        public PostgresPersonaStore CreateStore(PersonaMutationFaultInjector? faultInjector = null) => new(
            new PostgresPersonaStoreOptions(_adminConnectionString, _runtimeConnectionString, _schema, RequestHmacKey),
            Binding,
            faultInjector);

        public async Task<IAsyncDisposable> HoldSoulAdvisoryLockAsync(string soulId)
        {
            var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, TestCancellation);
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@soul_id, 730202))",
                    connection,
                    transaction);
                command.Parameters.AddWithValue("soul_id", soulId);
                await command.ExecuteNonQueryAsync(TestCancellation);
                return new AdvisoryLockLease(connection, transaction);
            }
            catch
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task RuntimeRecordMalformedExportReceiptAsync(
            PersonaHistoryExportV1 validExport,
            string malformedResultJson,
            byte[] requestHmacKey)
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand(
                $"SELECT {quotedSchema}.record_persona_export_receipt_v1(" +
                "@soul, @binding, @account, @idempotency_hash, @request_hmac, @snapshot_revision, " +
                "@cursor_hmac, @payload_sha, @receipt_hmac, @receipt_id, @result_document, @request_hmac_key)",
                connection);
            command.Parameters.AddWithValue("soul", validExport.SoulId);
            command.Parameters.AddWithValue("binding", validExport.DeviceBindingId);
            command.Parameters.AddWithValue("account", validExport.PlatformAccountId);
            command.Parameters.AddWithValue("idempotency_hash", Digest(validExport.IdempotencyKey));
            command.Parameters.AddWithValue("request_hmac", validExport.ExportRequestHmacSha256);
            command.Parameters.AddWithValue("snapshot_revision", validExport.SnapshotPersonaRevision);
            command.Parameters.AddWithValue("cursor_hmac", validExport.SnapshotCursorHmacSha256);
            command.Parameters.AddWithValue("payload_sha", validExport.ExportPayloadSha256);
            command.Parameters.AddWithValue("receipt_hmac", validExport.ExportReceiptHmacSha256);
            command.Parameters.AddWithValue("receipt_id", validExport.ExportReceiptId);
            command.Parameters.Add("result_document", NpgsqlTypes.NpgsqlDbType.Jsonb).Value = malformedResultJson;
            command.Parameters.Add("request_hmac_key", NpgsqlTypes.NpgsqlDbType.Bytea).Value = requestHmacKey;
            await command.ExecuteScalarAsync(TestCancellation);
        }

        public async Task AssertNoMutationAsync()
        {
            Assert.Equal(0, await Store.CountRevisionsAsync(TestCancellation));
            Assert.Equal(0, await Store.CountReceiptsAsync(TestCancellation));
            Assert.Equal(0, await Store.CountOutboxAsync(TestCancellation));
            Assert.Equal(0, await Store.CountTraitPayloadsAsync(TestCancellation));
        }

        public async Task<IReadOnlyList<string>> ReadRowsForScopeAsync(string soulId, string bindingId, string accountId)
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand(
                $"SELECT result_json::text FROM {quotedSchema}.persona_revisions WHERE soul_id=@soul AND device_binding_id=@binding AND platform_account_id=@account",
                connection);
            command.Parameters.AddWithValue("soul", soulId);
            command.Parameters.AddWithValue("binding", bindingId);
            command.Parameters.AddWithValue("account", accountId);
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(TestCancellation);
            while (await reader.ReadAsync(TestCancellation)) rows.Add(reader.GetString(0));
            return rows;
        }

        public async Task<bool> TableContainsAsync(string table, string text)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schema) + "." + new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {qualified} row_value WHERE row_to_json(row_value)::text LIKE @pattern)", connection);
            command.Parameters.AddWithValue("pattern", "%" + text + "%");
            return (bool)(await command.ExecuteScalarAsync(TestCancellation) ?? false);
        }

        public async Task<long> AdminCountAsync(string table)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schema) + "." + new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {qualified}", connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(TestCancellation), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<int> AdminExportWireBytesAsync(string idempotencyKey)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schema) + ".persona_export_receipts";
            await using var command = new NpgsqlCommand(
                $"SELECT result_wire_bytes FROM {qualified} WHERE idempotency_key_sha256 = @idempotency_hash",
                connection);
            command.Parameters.AddWithValue("idempotency_hash", Digest(idempotencyKey));
            return Convert.ToInt32(await command.ExecuteScalarAsync(TestCancellation), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<(string DeletionScope, string PolicyAction, string? ExternalDestructionReceiptSha256)> ReadDeletionAuditAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schema) + ".erasure_audit";
            await using var command = new NpgsqlCommand(
                $"SELECT deletion_scope, policy_action, external_destruction_receipt_sha256 FROM {qualified} WHERE soul_id = @soul",
                connection);
            command.Parameters.AddWithValue("soul", Soul);
            await using var reader = await command.ExecuteReaderAsync(TestCancellation);
            Assert.True(await reader.ReadAsync(TestCancellation));
            return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        public async Task DeletePersonaMaterialAsMigratorAsync(string table)
        {
            if (table is not ("trait_payloads" or "persona_hmac_keys")) throw new ArgumentOutOfRangeException(nameof(table));
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schema) + "." + new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var command = new NpgsqlCommand($"DELETE FROM {qualified}", connection);
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task<bool> SchemaContainsAsync(string text)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                "SELECT table_name FROM information_schema.tables WHERE table_schema=@schema AND table_type='BASE TABLE' ORDER BY table_name",
                connection);
            command.Parameters.AddWithValue("schema", _schema);
            var tables = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(TestCancellation))
            {
                while (await reader.ReadAsync(TestCancellation)) tables.Add(reader.GetString(0));
            }
            foreach (var table in tables)
            {
                var qualified = new NpgsqlCommandBuilder().QuoteIdentifier(_schema) + "." + new NpgsqlCommandBuilder().QuoteIdentifier(table);
                await using var contains = new NpgsqlCommand(
                    $"SELECT EXISTS (SELECT 1 FROM {qualified} row_value WHERE row_to_json(row_value)::text LIKE @pattern)",
                    connection);
                contains.Parameters.AddWithValue("pattern", "%" + text + "%");
                if ((bool)(await contains.ExecuteScalarAsync(TestCancellation) ?? false)) return true;
            }
            return false;
        }

        public Task MutateLedgerAsMigratorAsync(string operation) => MutateLedgerAsync(_adminConnectionString, operation);

        public Task MutateLedgerAsRuntimeAsync(string operation) => MutateLedgerAsync(_runtimeConnectionString, operation);

        private async Task MutateLedgerAsync(string connectionString, string operation)
        {
            var statement = operation switch
            {
                "UPDATE" => "UPDATE persona_revisions SET status='deleted'",
                "DELETE" => "DELETE FROM persona_revisions",
                "TRUNCATE" => "TRUNCATE persona_revisions",
                "INSERT" => "INSERT INTO persona_revisions DEFAULT VALUES",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestCancellation);
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand($"SET search_path TO {quotedSchema}; {statement}", connection);
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task RuntimeCreateTableAsync()
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand($"CREATE TABLE {quotedSchema}.forbidden_runtime_ddl(value integer)", connection);
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task RuntimeReadRawKeyAsync()
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            var schema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand($"SELECT key_material FROM {schema}.persona_hmac_keys LIMIT 1", connection);
            await command.ExecuteScalarAsync(TestCancellation);
        }

        public async Task RuntimeReadExportReceiptsAsync()
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            var schema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand($"SELECT result_json FROM {schema}.persona_export_receipts LIMIT 1", connection);
            await command.ExecuteScalarAsync(TestCancellation);
        }

        public async Task TamperRetainedTraitsAsMigratorAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var schema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand(
                $"ALTER TABLE {schema}.trait_payloads DISABLE TRIGGER trait_payloads_no_update; " +
                $"UPDATE {schema}.trait_payloads SET traits_json = '{{\"tone\":\"warm\"}}'::jsonb WHERE soul_id = @soul; " +
                $"ALTER TABLE {schema}.trait_payloads ENABLE TRIGGER trait_payloads_no_update",
                connection);
            command.Parameters.AddWithValue("soul", Soul);
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task<bool> HasExplicitRuntimeColumnPrivilegeAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_attribute attribute
                    JOIN pg_catalog.pg_class relation ON relation.oid = attribute.attrelid
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                    CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
                    JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee
                    WHERE namespace.nspname = @schema AND grantee.rolname = @runtime_role)
                """,
                connection);
            command.Parameters.AddWithValue("schema", _schema);
            command.Parameters.AddWithValue("runtime_role", _runtimeRole);
            return Convert.ToBoolean(await command.ExecuteScalarAsync(TestCancellation), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<SchemaBaseline> ReadSchemaBaselineAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                """
                SELECT pg_catalog.pg_get_userbyid(namespace.nspowner),
                       COALESCE(namespace.nspacl::text, ''),
                       (SELECT count(*) FROM pg_catalog.pg_class relation WHERE relation.relnamespace = namespace.oid)
                         + (SELECT count(*) FROM pg_catalog.pg_proc function_value WHERE function_value.pronamespace = namespace.oid)
                         + (SELECT count(*) FROM pg_catalog.pg_type type_value WHERE type_value.typnamespace = namespace.oid),
                       pg_catalog.to_regclass(@qualified_ledger) IS NOT NULL
                FROM pg_catalog.pg_namespace namespace
                WHERE namespace.nspname = @schema
                """,
                connection);
            command.Parameters.AddWithValue("schema", _schema);
            command.Parameters.AddWithValue("qualified_ledger", _schema + ".schema_migrations");
            await using var reader = await command.ExecuteReaderAsync(TestCancellation);
            if (!await reader.ReadAsync(TestCancellation)) throw new InvalidOperationException("Persona test schema is missing.");
            return new SchemaBaseline(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3));
        }

        public async Task RuntimeCallInternalHelperAsync()
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            var schema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand($"SELECT {schema}.erase_persona_material(@soul, 1)", connection);
            command.Parameters.AddWithValue("soul", Soul);
            await command.ExecuteScalarAsync(TestCancellation);
        }

        public async Task RuntimeAdvanceSequenceAsync()
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            var qualifiedTable = _schema + ".idempotency_quarantine";
            await using var command = new NpgsqlCommand(
                "SELECT nextval(pg_get_serial_sequence(@qualified_table, 'quarantine_id'))",
                connection);
            command.Parameters.AddWithValue("qualified_table", qualifiedTable);
            await command.ExecuteScalarAsync(TestCancellation);
        }

        public async Task<IReadOnlyList<string>> ColumnNamesAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema=@schema ORDER BY table_name, ordinal_position",
                connection);
            command.Parameters.AddWithValue("schema", _schema);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(TestCancellation);
            while (await reader.ReadAsync(TestCancellation)) columns.Add(reader.GetString(0));
            return columns;
        }

        public async Task InsertMismatchedBundleAsync()
        {
            var put = Put(1, "mismatched-bundle") with
            {
                Traits = new Dictionary<string, string> { ["tone"] = "warm" },
                EvidenceSha256 = [new string('8', 64)],
                OccurredAt = OccurredAt.AddMinutes(5)
            };
            var normalized = PersonaMutationCanonicalizer.Normalize(put);
            var requestKey = Convert.FromBase64String(RequestHmacKey);
            string requestSha256;
            try { requestSha256 = PersonaMutationCanonicalizer.HashPut(normalized, requestKey); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(requestKey); }
            await using var lease = await Binding.AcquireAsync(new AcquireBindingMutationFenceCommand(
                put.SoulId,
                put.DeviceBindingId,
                put.PlatformAccountId,
                put.TraceId,
                put.IdempotencyKey,
                put.OccurredAt), TestCancellation);

            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var transaction = await connection.BeginTransactionAsync(TestCancellation);
            var schema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using (var mutation = new NpgsqlCommand(
                $"""
                SELECT {schema}.mutate_persona_v1(
                    'put', @soul, @binding, @account, 1, CAST(@traits AS jsonb), @evidence,
                    @trace, @idempotency_key, @idempotency_key_sha256, @request_sha256,
                    @occurred_at, @outbox_id, CAST(@fence AS jsonb), @attestation_sha256,
                    @release_bom_sha256, 1, 1)
                """,
                connection,
                transaction))
            {
                mutation.Parameters.AddWithValue("soul", put.SoulId);
                mutation.Parameters.AddWithValue("binding", put.DeviceBindingId);
                mutation.Parameters.AddWithValue("account", put.PlatformAccountId);
                mutation.Parameters.AddWithValue("traits", JsonSerializer.Serialize(put.Traits));
                mutation.Parameters.AddWithValue("evidence", normalized.EvidenceSha256);
                mutation.Parameters.AddWithValue("trace", put.TraceId);
                mutation.Parameters.AddWithValue("idempotency_key", put.IdempotencyKey);
                mutation.Parameters.AddWithValue("idempotency_key_sha256", PersonaMutationCanonicalizer.HashUtf8(put.IdempotencyKey));
                mutation.Parameters.AddWithValue("request_sha256", requestSha256);
                mutation.Parameters.AddWithValue("occurred_at", put.OccurredAt);
                mutation.Parameters.AddWithValue("outbox_id", PersonaMutationCanonicalizer.DeterministicOutboxId(Soul, 2));
                mutation.Parameters.AddWithValue("fence", JsonSerializer.Serialize(lease.Receipt));
                mutation.Parameters.AddWithValue("attestation_sha256", new string('0', 64));
                mutation.Parameters.AddWithValue("release_bom_sha256", new string('0', 64));
                await mutation.ExecuteNonQueryAsync(TestCancellation);
            }
            await using var tamper = new NpgsqlCommand(
                $"ALTER TABLE {schema}.idempotency_receipts DISABLE TRIGGER idempotency_receipts_append_only_rows; " +
                $"UPDATE {schema}.idempotency_receipts SET operation = 'delete' WHERE soul_id = @soul AND persona_revision = 2",
                connection,
                transaction);
            tamper.Parameters.AddWithValue("soul", Soul);
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync(TestCancellation));
            await transaction.CommitAsync(TestCancellation);
        }

        public async Task DropTriggerAsMigratorAsync(string triggerName)
        {
            if (triggerName != "persona_revisions_append_only_rows") throw new ArgumentOutOfRangeException(nameof(triggerName));
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var builder = new NpgsqlCommandBuilder();
            var schema = builder.QuoteIdentifier(_schema);
            var trigger = builder.QuoteIdentifier(triggerName);
            await using var command = new NpgsqlCommand($"DROP TRIGGER {trigger} ON {schema}.persona_revisions", connection);
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task DriftCatalogAsMigratorAsync(string driftKind)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var builder = new NpgsqlCommandBuilder();
            var schema = builder.QuoteIdentifier(_schema);
            var runtimeRole = builder.QuoteIdentifier(_runtimeRole);
            string statement;
            switch (driftKind)
            {
                case "function-security-definer":
                    statement = $"ALTER FUNCTION {schema}.mutate_persona_v1(text, text, text, text, bigint, jsonb, text[], text, text, text, text, timestamp with time zone, uuid, jsonb, text, text, bigint, bigint) SECURITY INVOKER";
                    break;
                case "table-acl":
                    statement = $"GRANT INSERT ON {schema}.persona_revisions TO {runtimeRole}";
                    break;
                case "sequence-acl":
                {
                    await using var find = new NpgsqlCommand(
                        "SELECT pg_get_serial_sequence(@qualified_table, 'quarantine_id')",
                        connection);
                    find.Parameters.AddWithValue("qualified_table", _schema + ".idempotency_quarantine");
                    var sequence = await find.ExecuteScalarAsync(TestCancellation) as string
                        ?? throw new InvalidOperationException("Persona quarantine sequence was not found.");
                    statement = $"GRANT USAGE ON SEQUENCE {string.Join(".", sequence.Split('.').Select(builder.QuoteIdentifier))} TO {runtimeRole}";
                    break;
                }
                case "schema-acl":
                    statement = $"GRANT CREATE ON SCHEMA {schema} TO {runtimeRole}";
                    break;
                case "table-owner":
                    statement = $"ALTER TABLE {schema}.persona_current OWNER TO {runtimeRole}";
                    break;
                case "column-acl":
                    statement = $"GRANT SELECT (key_material) ON {schema}.persona_hmac_keys TO {runtimeRole}";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(driftKind));
            }
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task TamperMigrationLedgerAsMigratorAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            var schema = new NpgsqlCommandBuilder().QuoteIdentifier(_schema);
            await using var command = new NpgsqlCommand(
                $"ALTER TABLE {schema}.schema_migrations DISABLE TRIGGER schema_migrations_append_only_rows; " +
                $"UPDATE {schema}.schema_migrations SET migration_sha256 = repeat('9', 64) WHERE migration_version = 1",
                connection);
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task<bool> TriggerExistsAsync(string triggerName)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_trigger trigger_value
                    JOIN pg_catalog.pg_class relation ON relation.oid = trigger_value.tgrelid
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema
                      AND trigger_value.tgname = @trigger
                      AND NOT trigger_value.tgisinternal)
                """,
                connection);
            command.Parameters.AddWithValue("schema", _schema);
            command.Parameters.AddWithValue("trigger", triggerName);
            return Convert.ToBoolean(await command.ExecuteScalarAsync(TestCancellation), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            try
            {
                await connection.OpenAsync();
                var builder = new NpgsqlCommandBuilder();
                var quotedSchema = builder.QuoteIdentifier(_schema);
                var quotedRole = builder.QuoteIdentifier(_runtimeRole);
                await using (var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE", connection))
                    await dropSchema.ExecuteNonQueryAsync();
                await using (var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS {quotedRole}", connection))
                    await dropRole.ExecuteNonQueryAsync();
            }
            catch
            {
                // Preserve the primary test failure; random schema and role names are safe for later cleanup.
            }
        }

        private sealed class AdvisoryLockLease(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction) : IAsyncDisposable
        {
            private int _disposed;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try
                {
                    await transaction.RollbackAsync(TestCancellation);
                }
                finally
                {
                    await transaction.DisposeAsync();
                    await connection.DisposeAsync();
                }
            }
        }

        public sealed record SchemaBaseline(string Owner, string SchemaAcl, long ObjectCount, bool HasMigrationLedger);
    }
}
