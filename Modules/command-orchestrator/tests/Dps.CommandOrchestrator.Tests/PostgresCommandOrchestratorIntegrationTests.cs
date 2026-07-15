using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.CommandOrchestrator.Contracts;
using Dps.OperationCompiler.Contracts;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Dps.CommandOrchestrator.Tests;

public sealed class PostgresCommandOrchestratorIntegrationTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSoul = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Device = "db_11111111111111111111111111111111";
    private const string OtherDevice = "db_99999999999999999999999999999999";
    private const string Account = "pa_22222222222222222222222222222222";
    private const string OtherAccount = "pa_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherTrace = "trace_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherIdempotency = "idem_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task InitializeAttestsExactPostgres184RolesCatalogAndRuntimeApiOnlyBoundary()
    {
        await using var database = await CommandDatabase.CreateAsync();

        var attestation = await database.ReadRuntimeAttestationAsync();

        Assert.Equal("1", attestation.SchemaVersion);
        Assert.Matches("^[a-f0-9]{64}$", attestation.MigrationSha256);
        Assert.Matches("^[a-f0-9]{64}$", attestation.CatalogSha256);
        Assert.Equal(180004, attestation.ServerVersionNumber);
        Assert.Equal(database.MigratorRole, attestation.MigratorRole);
        Assert.Equal(database.RuntimeRole, attestation.RuntimeRole);
        Assert.Equal(11, await database.CountRuntimeExecutableApiFunctionsAsync());
        Assert.False(await database.RuntimeHasAnyDirectRelationOrSequencePrivilegeAsync());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RuntimeDirectTableColumnSequenceAndDdlAccessFailsAndOwnerCannotRewriteHistory()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var commandId = (await database.Store.EnqueueAsync(
            Operation("append-only"), TestCancellation)).CommandId!.Value;
        var dispatch = await database.Store.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-runtime-boundary",
            TimeSpan.FromMinutes(1), TestCancellation);
        var authorization = Authorization(database, dispatch);

        var tableRead = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteRuntimeAsync(
            $"SELECT count(*) FROM {database.QuotedSchema}.commands"));
        var columnRead = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteRuntimeAsync(
            $"SELECT command_id FROM {database.QuotedSchema}.commands"));
        var sequenceUse = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteRuntimeAsync(
            $"SELECT nextval('{database.Schema}.attempt_events_event_seq_seq'::regclass)"));
        var ddl = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteRuntimeAsync(
            $"CREATE TABLE {database.QuotedSchema}.forbidden_runtime_table(id integer)"));
        Assert.All([tableRead, columnRead, sequenceUse, ddl],
            exception => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState));

        var forgedDispatch = await Assert.ThrowsAsync<PostgresException>(
            () => database.AttemptRuntimeCredentialOnlyDispatchAsync(dispatch, authorization));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forgedDispatch.SqlState);
        Assert.Equal(CommandState.Leased, (await database.Store.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);

        await database.Store.MarkDispatchedAsync(
            commandId, dispatch.LeaseId, authorization, TestCancellation);
        var authenticReceipt = Receipt(
            database, dispatch, CommandReceiptV1.Success, false, true, true,
            "runtime-credential-only-receipt");
        var forgedReceipt = await Assert.ThrowsAsync<PostgresException>(
            () => database.AttemptRuntimeCredentialOnlyReceiptAsync(authenticReceipt));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forgedReceipt.SqlState);
        Assert.Equal(CommandState.Dispatched, (await database.Store.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);
        Assert.Equal(0, await database.CountRowsAsync("signed_receipts"));
        Assert.Equal(0, await database.CountRowsAsync("outbox"));

        var update = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteMigratorAsync(
            $"UPDATE {database.QuotedSchema}.commands SET retry_safe = false"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteMigratorAsync(
            $"DELETE FROM {database.QuotedSchema}.commands"));
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteMigratorAsync(
            $"TRUNCATE {database.QuotedSchema}.commands"));
        Assert.All([update, delete, truncate], exception => Assert.Equal("55000", exception.SqlState));
        Assert.Equal(1, await database.CountRowsAsync("commands"));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task EnqueueDuplicateIsNoOpConflictIsQuarantinedAndRestartReadsCommittedTruth()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var operation = Operation("enqueue-restart");
        await using var crashing = database.CreateStore((stage, _) =>
            stage == PostgresCommandMutationStage.EnqueueCommitted
                ? ValueTask.FromException(new SimulatedCrashException())
                : ValueTask.CompletedTask);
        await crashing.InitializeAsync(TestCancellation);

        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.EnqueueAsync(
            operation, TestCancellation).AsTask());
        var duplicate = await database.Store.EnqueueAsync(operation, TestCancellation);
        var inserted = await database.Store.EnqueueAsync(Operation("enqueue-normal-insert"), TestCancellation);
        var conflict = await database.Store.EnqueueAsync(Recanonicalize(operation with
        {
            ActionKind = "verify",
            Steps = [CanonicalStep(operation.OperationId, "ui.verify", new Dictionary<string, string>
            {
                ["selector_ref"] = "fixture.status"
            }, true, "assertion-satisfied")]
        }), TestCancellation);

        Assert.Equal(EnqueueDisposition.DuplicateNoOp, duplicate.Disposition);
        Assert.Equal(EnqueueDisposition.Inserted, inserted.Disposition);
        Assert.Equal(EnqueueDisposition.Quarantined, conflict.Disposition);
        Assert.Null(conflict.CommandId);
        Assert.Equal(1, await database.Store.GetQuarantineCountAsync(TestCancellation));
        Assert.Equal(2, await database.CountRowsAsync("commands"));

        await using var restarted = database.CreateStore();
        await restarted.InitializeAsync(TestCancellation);
        var snapshot = await restarted.GetSnapshotAsync(duplicate.CommandId!.Value, Soul, Device, Account, TestCancellation);
        Assert.Equal(CommandState.Pending, snapshot.State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentLeaseAcquisitionHasExactlyOneWinner()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var commandId = (await database.Store.EnqueueAsync(Operation("concurrent-lease"), TestCancellation)).CommandId!.Value;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                _ = await database.Store.AcquireLeaseAsync(
                    commandId, Soul, Device, Account, "worker-" + index,
                    TimeSpan.FromSeconds(30), TestCancellation);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        Assert.Single(outcomes, static won => won);
        Assert.Equal(1, await database.CountRowsAsync("leases"));
        var snapshot = await database.Store.GetSnapshotAsync(commandId, Soul, Device, Account, TestCancellation);
        Assert.Equal(CommandState.Leased, snapshot.State);
        Assert.Equal(1, snapshot.Attempt);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CrashAfterLeaseReservationRecoversPreDispatchAndUsesNextAttempt()
    {
        foreach (var crashStage in new[]
        {
            PostgresCommandMutationStage.LeaseReservationCommitted,
            PostgresCommandMutationStage.LeaseBoundCommitted
        })
        {
            await using var database = await CommandDatabase.CreateAsync();
            var label = "pre-dispatch-crash-" + crashStage;
            var commandId = (await database.Store.EnqueueAsync(Operation(label), TestCancellation)).CommandId!.Value;
            await using var crashing = database.CreateStore((stage, _) =>
                stage == crashStage
                    ? ValueTask.FromException(new SimulatedCrashException())
                    : ValueTask.CompletedTask);
            await crashing.InitializeAsync(TestCancellation);

            await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.AcquireLeaseAsync(
                commandId, Soul, Device, Account, "worker-crash",
                TimeSpan.FromSeconds(1), TestCancellation).AsTask());

            await using var restarted = database.CreateStore();
            await restarted.InitializeAsync(TestCancellation);
            await Task.Delay(TimeSpan.FromMilliseconds(1200), TestCancellation);
            Assert.Equal(1, await restarted.RecoverExpiredLeasesAsync(TestCancellation));
            Assert.Equal(CommandState.Pending, (await restarted.GetSnapshotAsync(
                commandId, Soul, Device, Account, TestCancellation)).State);
            var recovered = await restarted.AcquireLeaseAsync(
                commandId, Soul, Device, Account, "worker-recovered",
                TimeSpan.FromSeconds(30), TestCancellation);
            Assert.Equal(2, recovered.Attempt);
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExpiredPostDispatchLeaseRequiresReconciliationAndCannotBeBlindlyRetried()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var commandId = (await database.Store.EnqueueAsync(
            Operation("post-dispatch-expiry"), TestCancellation)).CommandId!.Value;
        await using var crashing = database.CreateStore((stage, _) =>
            stage == PostgresCommandMutationStage.DispatchCommitted
                ? ValueTask.FromException(new SimulatedCrashException())
                : ValueTask.CompletedTask);
        await crashing.InitializeAsync(TestCancellation);
        var dispatch = await crashing.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-post-dispatch-crash",
            TimeSpan.FromSeconds(1), TestCancellation);
        var forgedAuthorization = Authorization(database, dispatch) with
        {
            SignatureBase64 = Convert.ToBase64String(
                new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes])
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => crashing.MarkDispatchedAsync(
            commandId, dispatch.LeaseId, forgedAuthorization, TestCancellation).AsTask());
        Assert.Equal(CommandState.Leased, (await crashing.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.MarkDispatchedAsync(
            commandId, dispatch.LeaseId, Authorization(database, dispatch), TestCancellation).AsTask());

        await using var restarted = database.CreateStore();
        await restarted.InitializeAsync(TestCancellation);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestCancellation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AcquireLeaseAsync(
            dispatch.CommandId, Soul, Device, Account, "worker-expiry-probe",
            TimeSpan.FromSeconds(30), TestCancellation).AsTask());
        var snapshot = await restarted.GetSnapshotAsync(dispatch.CommandId, Soul, Device, Account, TestCancellation);
        Assert.Equal(CommandState.ReconciliationRequired, snapshot.State);
        Assert.Equal(0, await restarted.RecoverExpiredLeasesAsync(TestCancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AcquireLeaseAsync(
            dispatch.CommandId, Soul, Device, Account, "worker-forbidden-retry",
            TimeSpan.FromSeconds(30), TestCancellation).AsTask());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task AuthenticSuccessIsAtomicReplaySafeChecksummedAndDurableAcrossRestart()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var dispatch = await PrepareDispatchedAsync(database, "success-restart", TimeSpan.FromMinutes(1));
        var receipt = Receipt(database, dispatch, CommandReceiptV1.Success, false, true, true, "success-restart");

        var applied = await database.Store.RecordReceiptAsync(receipt, TestCancellation);
        var duplicate = await database.Store.RecordReceiptAsync(receipt, TestCancellation);
        var outbox = Assert.Single(await database.Store.ReadOutboxAsync(0, 10, TestCancellation));

        Assert.Equal(ReceiptDisposition.Applied, applied.Disposition);
        Assert.Equal(CommandState.Succeeded, applied.State);
        Assert.Equal(ReceiptDisposition.DuplicateNoOp, duplicate.Disposition);
        Assert.Equal(receipt.Receipt, outbox.Payload);
        Assert.Equal(receipt.ReceiptSha256, outbox.PayloadSha256);
        Assert.Equal(1, await database.CountRowsAsync("signed_receipts"));
        Assert.Equal(1, await database.CountRowsAsync("outbox"));

        await using var restarted = database.CreateStore();
        await restarted.InitializeAsync(TestCancellation);
        Assert.Equal(CommandState.Succeeded, (await restarted.GetSnapshotAsync(
            dispatch.CommandId, Soul, Device, Account, TestCancellation)).State);
        Assert.Single(await restarted.ReadOutboxAsync(0, 10, TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameReceiptIdWithDifferentAuthenticDigestIsQuarantinedWithoutStateChange()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var dispatch = await PrepareDispatchedAsync(database, "receipt-conflict", TimeSpan.FromMinutes(1));
        var receipt = Receipt(database, dispatch, CommandReceiptV1.UnknownOutcome, false, false, false, "receipt-conflict");
        var applied = await database.Store.RecordReceiptAsync(receipt, TestCancellation);
        var authenticatedReplayWithFreshP1363Signature = MalleateP256Signature(receipt);
        Assert.NotEqual(receipt.SignatureBase64, authenticatedReplayWithFreshP1363Signature.SignatureBase64);
        Assert.Equal(
            ReceiptDisposition.DuplicateNoOp,
            (await database.Store.RecordReceiptAsync(
                authenticatedReplayWithFreshP1363Signature,
                TestCancellation)).Disposition);
        var conflicting = Receipt(
            database, dispatch, CommandReceiptV1.UnknownOutcome, false, false, false,
            "receipt-conflict", receipt.ReceiptId, OtherTrace, OtherIdempotency);

        var conflict = await database.Store.RecordReceiptAsync(conflicting, TestCancellation);

        Assert.Equal(CommandState.ReconciliationRequired, applied.State);
        Assert.Equal(ReceiptDisposition.Quarantined, conflict.Disposition);
        Assert.Equal(CommandState.ReconciliationRequired, conflict.State);
        Assert.Equal(1, await database.Store.GetQuarantineCountAsync(TestCancellation));
        Assert.Equal(1, await database.CountRowsAsync("signed_receipts"));
        Assert.Equal(1, await database.CountRowsAsync("outbox"));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task UnknownOutcomeNeverReturnsToTheLeaseableState()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var dispatch = await PrepareDispatchedAsync(database, "unknown-outcome", TimeSpan.FromMinutes(1));

        var result = await database.Store.RecordReceiptAsync(
            Receipt(database, dispatch, CommandReceiptV1.UnknownOutcome, false, false, false, "unknown-outcome"),
            TestCancellation);

        Assert.Equal(CommandState.ReconciliationRequired, result.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.AcquireLeaseAsync(
            dispatch.CommandId, Soul, Device, Account, "worker-no-blind-retry",
            TimeSpan.FromSeconds(30), TestCancellation).AsTask());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RetryableFailureIsBoundedToThreeDurableAttempts()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var commandId = (await database.Store.EnqueueAsync(Operation("bounded-retry"), TestCancellation)).CommandId!.Value;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var dispatch = await database.Store.AcquireLeaseAsync(
                commandId, Soul, Device, Account, "worker-retry-" + attempt,
                TimeSpan.FromSeconds(8), TestCancellation);
            Assert.Equal(attempt, dispatch.Attempt);
            await database.Store.MarkDispatchedAsync(
                commandId, dispatch.LeaseId, Authorization(database, dispatch), TestCancellation);
            var result = await database.Store.RecordReceiptAsync(
                Receipt(database, dispatch, CommandReceiptV1.Failed, true, true, false, "bounded-retry-" + attempt),
                TestCancellation);
            Assert.Equal(attempt < 3 ? CommandState.Pending : CommandState.Failed, result.State);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-fourth",
            TimeSpan.FromSeconds(30), TestCancellation).AsTask());
        Assert.Equal(3, await database.CountRowsAsync("leases"));
        Assert.Equal(CommandState.Failed, (await database.Store.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LateAuthenticReceiptReconcilesAnExpiredPostDispatchLease()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var dispatch = await PrepareDispatchedAsync(database, "late-receipt", TimeSpan.FromSeconds(1));
        var lateReceipt = Receipt(database, dispatch, CommandReceiptV1.Success, false, true, true, "late-receipt");

        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestCancellation);
        await database.Store.RecoverExpiredLeasesAsync(TestCancellation);
        var reconciled = await database.Store.RecordReceiptAsync(lateReceipt, TestCancellation);

        Assert.Equal(ReceiptDisposition.Applied, reconciled.Disposition);
        Assert.Equal(CommandState.Succeeded, reconciled.State);
        Assert.Equal(CommandState.Succeeded, (await database.Store.GetSnapshotAsync(
            dispatch.CommandId, Soul, Device, Account, TestCancellation)).State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CrossScopeAccessAndStaleAttemptReceiptsFailClosed()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var commandId = (await database.Store.EnqueueAsync(Operation("scope-stale"), TestCancellation)).CommandId!.Value;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.GetSnapshotAsync(
            commandId, OtherSoul, Device, Account, TestCancellation).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.GetSnapshotAsync(
            commandId, Soul, OtherDevice, Account, TestCancellation).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.GetSnapshotAsync(
            commandId, Soul, Device, OtherAccount, TestCancellation).AsTask());

        var first = await database.Store.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-first",
            TimeSpan.FromSeconds(30), TestCancellation);
        await database.Store.MarkDispatchedAsync(
            commandId, first.LeaseId, Authorization(database, first), TestCancellation);
        var firstReceipt = Receipt(database, first, CommandReceiptV1.Failed, true, true, false, "scope-stale-first");
        Assert.Equal(CommandState.Pending, (await database.Store.RecordReceiptAsync(firstReceipt, TestCancellation)).State);

        var second = await database.Store.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-second",
            TimeSpan.FromSeconds(30), TestCancellation);
        await database.Store.MarkDispatchedAsync(
            commandId, second.LeaseId, Authorization(database, second), TestCancellation);
        Assert.Equal(ReceiptDisposition.DuplicateNoOp,
            (await database.Store.RecordReceiptAsync(firstReceipt, TestCancellation)).Disposition);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.RecordReceiptAsync(
            Receipt(database, first, CommandReceiptV1.Failed, true, true, false, "scope-stale-new-id"),
            TestCancellation).AsTask());
        Assert.Equal(CommandState.Dispatched, (await database.Store.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CrashAfterReceiptCommitRestartsAsExactNoOpWithoutDuplicateOutbox()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var dispatch = await PrepareDispatchedAsync(database, "receipt-crash", TimeSpan.FromMinutes(1));
        var receipt = Receipt(database, dispatch, CommandReceiptV1.Success, false, true, true, "receipt-crash");
        await using var crashing = database.CreateStore((stage, _) =>
            stage == PostgresCommandMutationStage.ReceiptCommitted
                ? ValueTask.FromException(new SimulatedCrashException())
                : ValueTask.CompletedTask);
        await crashing.InitializeAsync(TestCancellation);

        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.RecordReceiptAsync(
            receipt, TestCancellation).AsTask());

        await using var restarted = database.CreateStore();
        await restarted.InitializeAsync(TestCancellation);
        var replay = await restarted.RecordReceiptAsync(receipt, TestCancellation);
        Assert.Equal(ReceiptDisposition.DuplicateNoOp, replay.Disposition);
        Assert.Equal(CommandState.Succeeded, replay.State);
        Assert.Equal(1, await database.CountRowsAsync("signed_receipts"));
        Assert.Equal(1, await database.CountRowsAsync("outbox"));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FunctionSecurityAndTableAclDriftBothStopInitializationBeforeRuntimeWork()
    {
        await using (var functionDrift = await CommandDatabase.CreateAsync())
        {
            await functionDrift.MakeRuntimeApiSecurityInvokerAsync();
            await using var restarted = functionDrift.CreateStore();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.InitializeAsync(TestCancellation));
        }

        await using (var aclDrift = await CommandDatabase.CreateAsync())
        {
            await aclDrift.GrantRuntimeDirectTableAccessAsync();
            await using var restarted = aclDrift.CreateStore();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.InitializeAsync(TestCancellation));
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RuntimeOwnedPreexistingSchemaIsRejectedWithoutMigration()
    {
        await using var database = await CommandDatabase.CreateRuntimeOwnedSchemaAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.InitializeAsync(TestCancellation));

        Assert.Equal(0, await database.CountSchemaObjectsAsync());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task AppliedMigrationDigestTamperStopsRestartBeforeRuntimeWork()
    {
        await using var database = await CommandDatabase.CreateAsync();
        await database.TamperMigrationDigestAsync();
        await using var restarted = database.CreateStore();

        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.InitializeAsync(TestCancellation));

        Assert.Equal(0, await database.CountRowsAsync("commands"));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LeaseAndDispatchValidityUseLockedDatabaseClockOnly()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var commandId = (await database.Store.EnqueueAsync(
            Operation("database-clock"), TestCancellation)).CommandId!.Value;
        var dispatch = await database.Store.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-database-clock",
            TimeSpan.FromMinutes(1), TestCancellation);
        var zeroSignature = Convert.ToBase64String(
            new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes]);
        var future = database.SignAuthorization(Authorization(database, dispatch) with
        {
            OccurredAt = dispatch.OccurredAt.AddSeconds(20),
            SignatureBase64 = zeroSignature
        });
        var expired = database.SignAuthorization(Authorization(database, dispatch) with
        {
            OccurredAt = dispatch.OccurredAt.AddSeconds(-2),
            ValidUntil = dispatch.OccurredAt,
            SignatureBase64 = zeroSignature
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.MarkDispatchedAsync(
            commandId, dispatch.LeaseId, future, TestCancellation).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => database.Store.MarkDispatchedAsync(
            commandId, dispatch.LeaseId, expired, TestCancellation).AsTask());
        Assert.Equal(CommandState.Leased, (await database.Store.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);

        await database.Store.MarkDispatchedAsync(
            commandId, dispatch.LeaseId, Authorization(database, dispatch), TestCancellation);
        Assert.Equal(CommandState.Dispatched, (await database.Store.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ThirdPartyObjectAclStopsInitializationBeforeRuntimeWork()
    {
        await using (var database = await CommandDatabase.CreateAsync())
        {
            await database.GrantBootstrapThirdPartyTableAccessAsync();
            await using var restarted = database.CreateStore();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.InitializeAsync(TestCancellation));
        }

        await using (var grantOptionDatabase = await CommandDatabase.CreateAsync())
        {
            await grantOptionDatabase.GrantRuntimeSchemaUsageWithGrantOptionAsync();
            await using var restarted = grantOptionDatabase.CreateStore();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.InitializeAsync(TestCancellation));
        }

        await using (var columnAclDatabase = await CommandDatabase.CreateAsync())
        {
            await columnAclDatabase.GrantBootstrapThirdPartyColumnAccessAsync();
            await using var restarted = columnAclDatabase.CreateStore();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.InitializeAsync(TestCancellation));
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task UnexpectedDefaultAclStopsInitializationBeforeRuntimeWork()
    {
        await using var database = await CommandDatabase.CreateAsync();
        await database.GrantUnexpectedDefaultTableAccessAsync();
        await using var restarted = database.CreateStore();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.InitializeAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DescendingIdentitySequenceCannotRegressProjectedStateAndFailsRestartAttestation()
    {
        await using var database = await CommandDatabase.CreateAsync();
        var commandId = (await database.Store.EnqueueAsync(
            Operation("descending-sequence"), TestCancellation)).CommandId!.Value;
        var dispatch = await database.Store.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-descending-sequence",
            TimeSpan.FromMinutes(1), TestCancellation);
        await database.MakeAttemptEventSequenceDescendAsync();
        await database.Store.MarkDispatchedAsync(
            commandId, dispatch.LeaseId, Authorization(database, dispatch), TestCancellation);
        var receipt = Receipt(
            database, dispatch, CommandReceiptV1.Success, false, true, true,
            "descending-sequence");

        Assert.Equal(CommandState.Succeeded,
            (await database.Store.RecordReceiptAsync(receipt, TestCancellation)).State);
        Assert.Equal(CommandState.Succeeded, (await database.Store.GetSnapshotAsync(
            commandId, Soul, Device, Account, TestCancellation)).State);
        await using var restarted = database.CreateStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.InitializeAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DisposableDatabaseMarkerAndSessionGuardAreUnforgeableHarnessRequirements()
    {
        await using (var markerDatabase = await CommandDatabase.CreateAsync())
        {
            await markerDatabase.AssertDisposableProofAsync();
            await markerDatabase.TamperDisposableMarkerAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(markerDatabase.AssertDisposableProofAsync);
        }

        await using (var guardDatabase = await CommandDatabase.CreateAsync())
        {
            await guardDatabase.AssertDisposableProofAsync();
            await guardDatabase.ReleaseDisposableGuardAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(guardDatabase.AssertDisposableProofAsync);
        }
    }

    private static async Task<CommandDispatchV1> PrepareDispatchedAsync(
        CommandDatabase database,
        string label,
        TimeSpan duration)
    {
        var commandId = (await database.Store.EnqueueAsync(Operation(label), TestCancellation)).CommandId!.Value;
        var dispatch = await database.Store.AcquireLeaseAsync(
            commandId, Soul, Device, Account, "worker-" + label,
            duration, TestCancellation);
        _ = await database.Store.IssueAndMarkDispatchedAsync(
            commandId,
            dispatch.LeaseId,
            new ExecutionAuthorizationActivationV1(
                new string('a', 64),
                7,
                new string('b', 64),
                false),
            TestCancellation);
        return dispatch;
    }

    private static CompiledOperationV1 Operation(string label)
    {
        var traceId = "trace_" + Digest("trace:" + label)[..32];
        var idempotencyKey = "idem_" + Digest("idempotency:" + label);
        var approvalId = GuidFor("approval:" + label);
        var proposalId = GuidFor("proposal:" + label);
        var approvalSha256 = Digest("approval-proof:" + label);
        var operationId = OperationCompiledV1CanonicalIds.ComputeOperationId(
            CompiledOperationV1.CurrentSchemaVersion,
            CompiledOperationV1.CurrentContractId,
            CompiledOperationV1.CurrentProducerModule,
            approvalId,
            proposalId,
            approvalSha256,
            Soul,
            Device,
            Account,
            traceId,
            idempotencyKey,
            Now,
            "internal",
            "observe",
            false,
            false,
            null);
        return new CompiledOperationV1(
            CompiledOperationV1.CurrentSchemaVersion,
            CompiledOperationV1.CurrentContractId,
            CompiledOperationV1.CurrentProducerModule,
            operationId,
            approvalId,
            proposalId,
            approvalSha256,
            Soul,
            Device,
            Account,
            traceId,
            idempotencyKey,
            Now,
            "internal",
            "observe",
            false,
            false,
            null,
            [CanonicalStep(operationId, "ui.observe", new Dictionary<string, string>(), true, "native-read-complete")]);
    }

    private static CompiledOperationV1 Recanonicalize(CompiledOperationV1 operation)
    {
        var operationId = OperationCompiledV1CanonicalIds.ComputeOperationId(
            operation.SchemaVersion,
            operation.ContractId,
            operation.ProducerModule,
            operation.ApprovalId,
            operation.ProposalId,
            operation.ApprovalSha256,
            operation.SoulId,
            operation.DeviceBindingId,
            operation.PlatformAccountId,
            operation.TraceId,
            operation.IdempotencyKey,
            operation.OccurredAt,
            operation.PrivacyClass,
            operation.ActionKind,
            operation.IsSideEffect,
            operation.ShadowOnly,
            operation.PlatformAuthorizationId);
        return operation with
        {
            OperationId = operationId,
            Steps = operation.Steps.Select(step => CanonicalStep(
                operationId,
                step.StepKind,
                step.Arguments,
                step.RetrySafe,
                step.PostconditionKind)).ToArray()
        };
    }

    private static OperationStepV1 CanonicalStep(
        Guid operationId,
        string stepKind,
        IReadOnlyDictionary<string, string> arguments,
        bool retrySafe,
        string postconditionKind) => new(
            OperationCompiledV1CanonicalIds.ComputeStepId(
                operationId,
                stepKind,
                arguments,
                retrySafe,
                postconditionKind),
            stepKind,
            arguments,
            retrySafe,
            postconditionKind);

    private static ExecutionAuthorizationV1 Authorization(
        CommandDatabase database,
        CommandDispatchV1 dispatch) => database.SignAuthorization(new(
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
        dispatch.CommandId,
        dispatch.LeaseId,
        dispatch.Attempt,
        dispatch.SoulId,
        dispatch.DeviceBindingId,
        dispatch.PlatformAccountId,
        dispatch.TraceId,
        dispatch.IdempotencyKey,
        dispatch.OccurredAt,
        "internal",
        ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch),
        new string('a', 64),
        7,
        new string('b', 64),
        dispatch.LeaseExpiresAt,
        false,
        Convert.ToBase64String(new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes])));

    private static SignedCommandReceiptV1 Receipt(
        CommandDatabase database,
        CommandDispatchV1 dispatch,
        string outcome,
        bool retryAllowed,
        bool nativeVerified,
        bool postconditionVerified,
        string label,
        Guid? receiptId = null,
        string? traceId = null,
        string? idempotencyKey = null)
    {
        var authorization = Authorization(database, dispatch);
        var nativeEvidence = nativeVerified ? Digest("native-evidence:" + label) : null;
        var postconditionEvidence = postconditionVerified ? Digest("postcondition-evidence:" + label) : null;
        var receipt = new CommandReceiptV1(
            CommandReceiptV1.CurrentSchemaVersion,
            CommandReceiptV1.CurrentContractId,
            CommandReceiptV1.CurrentProducerModule,
            receiptId ?? GuidFor("receipt:" + label),
            dispatch.CommandId,
            dispatch.LeaseId,
            dispatch.Attempt,
            dispatch.SoulId,
            dispatch.DeviceBindingId,
            dispatch.PlatformAccountId,
            traceId ?? dispatch.TraceId,
            idempotencyKey ?? dispatch.IdempotencyKey,
            dispatch.OccurredAt.AddSeconds(2),
            "internal",
            outcome,
            nativeVerified ? GuidFor("native-result:" + label) : null,
            nativeVerified,
            postconditionVerified,
            CommandReceiptProtocolV1.ComputeEvidenceDigest(nativeEvidence, postconditionEvidence),
            retryAllowed,
            outcome == CommandReceiptV1.Success ? "VERIFIED" : "NOT_VERIFIED");
        var unsigned = new SignedCommandReceiptV1(
            SignedCommandReceiptV1.CurrentSchemaVersion,
            SignedCommandReceiptV1.CurrentContractId,
            SignedCommandReceiptV1.CurrentProducerModule,
            SignedCommandReceiptV1.CurrentSignatureDomain,
            SignedCommandReceiptV1.CurrentCanonicalEncoding,
            SignedCommandReceiptV1.CurrentReceiptDigestAlgorithm,
            SignedCommandReceiptV1.CurrentCommandDigestAlgorithm,
            SignedCommandReceiptV1.CurrentEvidenceDigestAlgorithm,
            SignedCommandReceiptV1.CurrentSignatureAlgorithm,
            SignedCommandReceiptV1.CurrentSignatureFormat,
            SignedCommandReceiptV1.CurrentSignatureEncoding,
            SignedCommandReceiptV1.CurrentSignerModule,
            SignedCommandReceiptV1.CurrentAuthScope,
            receipt.ReceiptId,
            receipt.CommandId,
            receipt.LeaseId,
            receipt.Attempt,
            receipt.SoulId,
            receipt.DeviceBindingId,
            receipt.PlatformAccountId,
            receipt.TraceId,
            receipt.IdempotencyKey,
            receipt.OccurredAt,
            receipt.PrivacyClass,
            CommandReceiptProtocolV1.ComputeReceiptSha256(receipt),
            ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch),
            ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization),
            authorization.ReleaseBomSha256,
            authorization.ActiveReleaseBomGeneration,
            authorization.ActiveReleaseBomTokenSha256,
            nativeEvidence,
            postconditionEvidence,
            receipt,
            Convert.ToBase64String(new byte[CommandReceiptProtocolV1.P1363SignatureSizeBytes]));
        return database.Sign(unsigned);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static SignedCommandReceiptV1 MalleateP256Signature(SignedCommandReceiptV1 receipt)
    {
        var signature = Convert.FromBase64String(receipt.SignatureBase64);
        var orderBytes = Convert.FromHexString(
            "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
        try
        {
            var order = new BigInteger(orderBytes, isUnsigned: true, isBigEndian: true);
            var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
            var alternate = (order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
            try
            {
                signature.AsSpan(32, 32).Clear();
                alternate.CopyTo(signature.AsSpan(64 - alternate.Length));
                return receipt with { SignatureBase64 = Convert.ToBase64String(signature) };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(alternate);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(orderBytes);
        }
    }

    private static Guid GuidFor(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        try { return new Guid(digest.AsSpan(0, 16)); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private sealed class SimulatedCrashException : Exception;

    private sealed class CommandDatabase : IAsyncDisposable, IPolicyExecutionAuthorizationSignerV1
    {
        private static readonly IReadOnlySet<string> CountableTables = new HashSet<string>(StringComparer.Ordinal)
        {
            "commands", "leases", "attempt_events", "signed_receipts", "outbox", "quarantine"
        };

        private readonly DisposablePostgresHarness _harness;
        private readonly string _migratorConnectionString;
        private readonly string _runtimeConnectionString;
        private readonly byte[] _authorizationPrivateKeyPkcs8;
        private readonly byte[] _authorizationPublicKeySpki;
        private readonly string _authorizationKeyId;
        private readonly byte[] _receiptPrivateKeyPkcs8;
        private readonly byte[] _receiptPublicKeySpki;
        private readonly byte[] _runtimeCapability;
        private int _disposed;

        private CommandDatabase(
            DisposablePostgresHarness harness,
            string schema,
            byte[] authorizationPrivateKeyPkcs8,
            byte[] authorizationPublicKeySpki,
            byte[] receiptPrivateKeyPkcs8,
            byte[] receiptPublicKeySpki,
            byte[] runtimeCapability)
        {
            _harness = harness;
            _migratorConnectionString = harness.MigratorConnectionString;
            _runtimeConnectionString = harness.RuntimeConnectionString;
            Schema = schema;
            MigratorRole = harness.MigratorRole;
            RuntimeRole = harness.RuntimeRole;
            _authorizationPrivateKeyPkcs8 = authorizationPrivateKeyPkcs8;
            _authorizationPublicKeySpki = authorizationPublicKeySpki;
            _authorizationKeyId = "sha256:" + Convert.ToHexStringLower(
                SHA256.HashData(authorizationPublicKeySpki));
            _receiptPrivateKeyPkcs8 = receiptPrivateKeyPkcs8;
            _receiptPublicKeySpki = receiptPublicKeySpki;
            _runtimeCapability = runtimeCapability;
            Store = CreateStore();
        }

        public string Schema { get; }
        public string MigratorRole { get; }
        public string RuntimeRole { get; }
        public string QuotedSchema => new NpgsqlCommandBuilder().QuoteIdentifier(Schema);
        public PostgresCommandOrchestrator Store { get; }
        public string ProtocolId => IPolicyExecutionAuthorizationSignerV1.CurrentProtocolId;
        public string SignerModule => IPolicyExecutionAuthorizationSignerV1.CurrentSignerModule;
        public string KeyId => _authorizationKeyId;

        public static Task<CommandDatabase> CreateAsync() => CreateCoreAsync(true, false);

        public static Task<CommandDatabase> CreateRuntimeOwnedSchemaAsync() => CreateCoreAsync(false, true);

        private static async Task<CommandDatabase> CreateCoreAsync(bool initialize, bool runtimeOwnedSchema)
        {
            var suffix = Guid.NewGuid().ToString("N")[..18];
            var schema = "cmd_it_" + suffix;
            DisposablePostgresHarness? harness = null;
            byte[]? authorizationPrivateKey = null;
            byte[]? authorizationPublicKey = null;
            byte[]? receiptPrivateKey = null;
            byte[]? receiptPublicKey = null;
            byte[]? runtimeCapability = null;
            try
            {
                harness = await DisposablePostgresHarness.CreateAsync(
                    schema,
                    runtimeOwnedSchema,
                    TestCancellation);

                using (var authorizationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    authorizationPrivateKey = authorizationSigner.ExportPkcs8PrivateKey();
                    authorizationPublicKey = authorizationSigner.ExportSubjectPublicKeyInfo();
                }
                using (var receiptSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    receiptPrivateKey = receiptSigner.ExportPkcs8PrivateKey();
                    receiptPublicKey = receiptSigner.ExportSubjectPublicKeyInfo();
                }
                runtimeCapability = RandomNumberGenerator.GetBytes(32);
                var database = new CommandDatabase(
                    harness,
                    schema,
                    authorizationPrivateKey,
                    authorizationPublicKey,
                    receiptPrivateKey,
                    receiptPublicKey,
                    runtimeCapability);
                harness = null;
                authorizationPrivateKey = null;
                authorizationPublicKey = null;
                receiptPrivateKey = null;
                receiptPublicKey = null;
                runtimeCapability = null;
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
            catch
            {
                if (authorizationPrivateKey is not null) CryptographicOperations.ZeroMemory(authorizationPrivateKey);
                if (authorizationPublicKey is not null) CryptographicOperations.ZeroMemory(authorizationPublicKey);
                if (receiptPrivateKey is not null) CryptographicOperations.ZeroMemory(receiptPrivateKey);
                if (receiptPublicKey is not null) CryptographicOperations.ZeroMemory(receiptPublicKey);
                if (runtimeCapability is not null) CryptographicOperations.ZeroMemory(runtimeCapability);
                if (harness is not null) await harness.DisposeAsync();
                throw;
            }
        }

        public PostgresCommandOrchestrator CreateStore(PostgresCommandFaultInjector? faultInjector = null) => new(
            new PostgresCommandOrchestratorOptions(
                _migratorConnectionString,
                _runtimeConnectionString,
                Schema,
                MigratorRole,
                RuntimeRole),
            this,
            _authorizationPublicKeySpki,
            _receiptPublicKeySpki,
            _runtimeCapability,
            faultInjector);

        public ValueTask<ExecutionAuthorizationV1> SignAsync(
            ExecutionAuthorizationV1 unsignedAuthorization,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SignAuthorization(unsignedAuthorization));
        }

        public ExecutionAuthorizationV1 SignAuthorization(ExecutionAuthorizationV1 unsigned)
        {
            unsigned.ValidatePayload();
            using var signer = ECDsa.Create();
            signer.ImportPkcs8PrivateKey(_authorizationPrivateKeyPkcs8, out var bytesRead);
            if (bytesRead != _authorizationPrivateKeyPkcs8.Length)
                throw new InvalidDataException("Authorization test private key has trailing data.");
            var payload = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(unsigned);
            try
            {
                var signature = signer.SignData(
                    payload,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                try { return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) }; }
                finally { CryptographicOperations.ZeroMemory(signature); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        public SignedCommandReceiptV1 Sign(SignedCommandReceiptV1 unsigned)
        {
            unsigned.ValidatePayload();
            using var signer = ECDsa.Create();
            signer.ImportPkcs8PrivateKey(_receiptPrivateKeyPkcs8, out var bytesRead);
            if (bytesRead != _receiptPrivateKeyPkcs8.Length)
                throw new InvalidDataException("Receipt test private key has trailing data.");
            var payload = CommandReceiptProtocolV1.CanonicalSignedReceiptBytes(unsigned);
            try
            {
                var signature = signer.SignData(
                    payload,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                try { return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) }; }
                finally { CryptographicOperations.ZeroMemory(signature); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        public async Task<RuntimeAttestation> ReadRuntimeAttestationAsync()
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                $"SELECT schema_version, migration_sha256, catalog_sha256, server_version_num, migrator_role, runtime_role FROM {QuotedSchema}.api_runtime_attestation(@runtime_capability)",
                connection);
            command.Parameters.AddWithValue(
                "runtime_capability",
                NpgsqlDbType.Bytea,
                _runtimeCapability);
            await using var reader = await command.ExecuteReaderAsync(TestCancellation);
            Assert.True(await reader.ReadAsync(TestCancellation));
            return new RuntimeAttestation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5));
        }

        public async Task<int> CountRuntimeExecutableApiFunctionsAsync()
        {
            await using var connection = new NpgsqlConnection(_migratorConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_proc AS routine
                JOIN pg_namespace AS namespace ON namespace.oid = routine.pronamespace
                WHERE namespace.nspname = @schema
                  AND left(routine.proname, 4) = 'api_'
                  AND has_function_privilege(@runtime_role, routine.oid, 'EXECUTE')
                """,
                connection);
            command.Parameters.AddWithValue("schema", Schema);
            command.Parameters.AddWithValue("runtime_role", RuntimeRole);
            return Convert.ToInt32(await command.ExecuteScalarAsync(TestCancellation));
        }

        public async Task<bool> RuntimeHasAnyDirectRelationOrSequencePrivilegeAsync()
        {
            await using var connection = new NpgsqlConnection(_migratorConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_class AS object
                    JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                    WHERE namespace.nspname = @schema
                      AND ((object.relkind = 'r' AND has_table_privilege(@runtime_role, object.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN'))
                        OR (object.relkind = 'S' AND has_sequence_privilege(@runtime_role, object.oid, 'USAGE,SELECT,UPDATE'))))
                """,
                connection);
            command.Parameters.AddWithValue("schema", Schema);
            command.Parameters.AddWithValue("runtime_role", RuntimeRole);
            return Convert.ToBoolean(await command.ExecuteScalarAsync(TestCancellation));
        }

        public async Task ExecuteRuntimeAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_runtimeConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task AttemptRuntimeCredentialOnlyDispatchAsync(
            CommandDispatchV1 dispatch,
            ExecutionAuthorizationV1 authorization)
        {
            var invalidCapability = InvalidRuntimeCapability();
            try
            {
                await using var connection = new NpgsqlConnection(_runtimeConnectionString);
                await connection.OpenAsync(TestCancellation);
                await using var command = new NpgsqlCommand(
                    $"SELECT {QuotedSchema}.api_mark_dispatched(@command_id, @lease_id, @attempt, @command_sha256, @authorization_sha256, @release_bom_sha256, @generation, @token_sha256, @authorization_json, @authorization_occurred_at, @authorization_valid_until, @runtime_capability)",
                    connection);
                command.Parameters.AddWithValue("command_id", dispatch.CommandId);
                command.Parameters.AddWithValue("lease_id", dispatch.LeaseId);
                command.Parameters.AddWithValue("attempt", dispatch.Attempt);
                command.Parameters.AddWithValue(
                    "command_sha256",
                    ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch));
                command.Parameters.AddWithValue(
                    "authorization_sha256",
                    ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(authorization));
                command.Parameters.AddWithValue(
                    "release_bom_sha256",
                    authorization.ReleaseBomSha256);
                command.Parameters.AddWithValue(
                    "generation",
                    authorization.ActiveReleaseBomGeneration);
                command.Parameters.AddWithValue(
                    "token_sha256",
                    authorization.ActiveReleaseBomTokenSha256);
                command.Parameters.AddWithValue(
                    "authorization_json",
                    NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(authorization));
                command.Parameters.AddWithValue("authorization_occurred_at", authorization.OccurredAt);
                command.Parameters.AddWithValue("authorization_valid_until", authorization.ValidUntil);
                command.Parameters.AddWithValue(
                    "runtime_capability",
                    NpgsqlDbType.Bytea,
                    invalidCapability);
                _ = await command.ExecuteScalarAsync(TestCancellation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(invalidCapability);
            }
        }

        public async Task AttemptRuntimeCredentialOnlyReceiptAsync(
            SignedCommandReceiptV1 signedReceipt)
        {
            var receipt = signedReceipt.Receipt;
            var invalidCapability = InvalidRuntimeCapability();
            try
            {
                await using var connection = new NpgsqlConnection(_runtimeConnectionString);
                await connection.OpenAsync(TestCancellation);
                await using var command = new NpgsqlCommand(
                    $"SELECT * FROM {QuotedSchema}.api_record_receipt(@receipt_id, @command_id, @lease_id, @attempt, @soul_id, @device_binding_id, @platform_account_id, @trace_id, @idempotency_key, @signed_sha256, @receipt_sha256, @command_sha256, @authorization_sha256, @release_bom_sha256, @generation, @token_sha256, @outcome, @retry_allowed, @signed_json, @receipt_json, @occurred_at, @runtime_capability)",
                    connection);
                command.Parameters.AddWithValue("receipt_id", receipt.ReceiptId);
                command.Parameters.AddWithValue("command_id", receipt.CommandId);
                command.Parameters.AddWithValue("lease_id", receipt.LeaseId);
                command.Parameters.AddWithValue("attempt", receipt.Attempt);
                command.Parameters.AddWithValue("soul_id", receipt.SoulId);
                command.Parameters.AddWithValue("device_binding_id", receipt.DeviceBindingId);
                command.Parameters.AddWithValue("platform_account_id", receipt.PlatformAccountId);
                command.Parameters.AddWithValue("trace_id", receipt.TraceId);
                command.Parameters.AddWithValue("idempotency_key", receipt.IdempotencyKey);
                command.Parameters.AddWithValue(
                    "signed_sha256",
                    CommandCanonicalEncoding.SignedReceiptDigest(signedReceipt));
                command.Parameters.AddWithValue("receipt_sha256", signedReceipt.ReceiptSha256);
                command.Parameters.AddWithValue("command_sha256", signedReceipt.CommandSha256);
                command.Parameters.AddWithValue(
                    "authorization_sha256",
                    signedReceipt.AuthorizationSha256);
                command.Parameters.AddWithValue(
                    "release_bom_sha256",
                    signedReceipt.ReleaseBomSha256);
                command.Parameters.AddWithValue(
                    "generation",
                    signedReceipt.ActiveReleaseBomGeneration);
                command.Parameters.AddWithValue(
                    "token_sha256",
                    signedReceipt.ActiveReleaseBomTokenSha256);
                command.Parameters.AddWithValue("outcome", receipt.Outcome);
                command.Parameters.AddWithValue("retry_allowed", receipt.RetryAllowed);
                command.Parameters.AddWithValue(
                    "signed_json",
                    NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(signedReceipt));
                command.Parameters.AddWithValue(
                    "receipt_json",
                    NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(receipt));
                command.Parameters.AddWithValue("occurred_at", receipt.OccurredAt);
                command.Parameters.AddWithValue(
                    "runtime_capability",
                    NpgsqlDbType.Bytea,
                    invalidCapability);
                await using var reader = await command.ExecuteReaderAsync(TestCancellation);
                _ = await reader.ReadAsync(TestCancellation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(invalidCapability);
            }
        }

        public async Task ExecuteMigratorAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_migratorConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync(TestCancellation);
        }

        public async Task<long> CountRowsAsync(string table)
        {
            if (!CountableTables.Contains(table)) throw new ArgumentOutOfRangeException(nameof(table));
            var quotedTable = new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var connection = new NpgsqlConnection(_migratorConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM {QuotedSchema}.{quotedTable}", connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(TestCancellation));
        }

        public async Task<long> CountSchemaObjectsAsync()
        {
            await using var connection = new NpgsqlConnection(_migratorConnectionString);
            await connection.OpenAsync(TestCancellation);
            await using var command = new NpgsqlCommand(
                """
                SELECT (SELECT count(*) FROM pg_class AS object JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace WHERE namespace.nspname = @schema)
                     + (SELECT count(*) FROM pg_proc AS routine JOIN pg_namespace AS namespace ON namespace.oid = routine.pronamespace WHERE namespace.nspname = @schema)
                """,
                connection);
            command.Parameters.AddWithValue("schema", Schema);
            return Convert.ToInt64(await command.ExecuteScalarAsync(TestCancellation));
        }

        public async Task MakeRuntimeApiSecurityInvokerAsync()
        {
            await ExecuteMigratorAsync(
                $"ALTER FUNCTION {QuotedSchema}.api_get_snapshot(uuid, text, text, text, bytea) SECURITY INVOKER");
        }

        public async Task GrantRuntimeDirectTableAccessAsync()
        {
            var quotedRuntime = new NpgsqlCommandBuilder().QuoteIdentifier(RuntimeRole);
            await ExecuteMigratorAsync(
                $"GRANT SELECT ON {QuotedSchema}.commands TO {quotedRuntime}");
        }

        public async Task GrantBootstrapThirdPartyTableAccessAsync()
        {
            var quotedBootstrap = new NpgsqlCommandBuilder().QuoteIdentifier(_harness.BootstrapRole);
            await ExecuteMigratorAsync(
                $"GRANT SELECT ON {QuotedSchema}.commands TO {quotedBootstrap}");
        }

        public async Task GrantRuntimeSchemaUsageWithGrantOptionAsync()
        {
            var quotedRuntime = new NpgsqlCommandBuilder().QuoteIdentifier(RuntimeRole);
            await ExecuteMigratorAsync(
                $"GRANT USAGE ON SCHEMA {QuotedSchema} TO {quotedRuntime} WITH GRANT OPTION");
        }

        public async Task GrantBootstrapThirdPartyColumnAccessAsync()
        {
            var quotedBootstrap = new NpgsqlCommandBuilder().QuoteIdentifier(_harness.BootstrapRole);
            await ExecuteMigratorAsync(
                $"GRANT SELECT(command_id) ON {QuotedSchema}.commands TO {quotedBootstrap}");
        }

        public async Task GrantUnexpectedDefaultTableAccessAsync()
        {
            var quotedBootstrap = new NpgsqlCommandBuilder().QuoteIdentifier(_harness.BootstrapRole);
            await ExecuteMigratorAsync(
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA {QuotedSchema} GRANT SELECT ON TABLES TO {quotedBootstrap}");
        }

        public Task AssertDisposableProofAsync() => _harness.AssertProofAsync(TestCancellation);

        public Task TamperDisposableMarkerAsync() => _harness.TamperMarkerAsync(TestCancellation);

        public Task ReleaseDisposableGuardAsync() => _harness.ReleaseGuardAsync();

        public Task MakeAttemptEventSequenceDescendAsync() => ExecuteMigratorAsync(
            $"ALTER SEQUENCE {QuotedSchema}.attempt_events_event_seq_seq RESTART WITH 100 INCREMENT BY -1");

        public async Task TamperMigrationDigestAsync()
        {
            await ExecuteMigratorAsync(
                $"ALTER TABLE {QuotedSchema}.migration_ledger DISABLE TRIGGER migration_ledger_no_row_mutation; " +
                $"UPDATE {QuotedSchema}.migration_ledger SET migration_sha256 = repeat('9', 64); " +
                $"ALTER TABLE {QuotedSchema}.migration_ledger ENABLE TRIGGER migration_ledger_no_row_mutation");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await Store.DisposeAsync();
            CryptographicOperations.ZeroMemory(_authorizationPrivateKeyPkcs8);
            CryptographicOperations.ZeroMemory(_authorizationPublicKeySpki);
            CryptographicOperations.ZeroMemory(_receiptPrivateKeyPkcs8);
            CryptographicOperations.ZeroMemory(_receiptPublicKeySpki);
            CryptographicOperations.ZeroMemory(_runtimeCapability);
            NpgsqlConnection.ClearAllPools();
            await _harness.DisposeAsync();
        }

        private byte[] InvalidRuntimeCapability()
        {
            var invalid = _runtimeCapability.ToArray();
            invalid[0] ^= 0xff;
            return invalid;
        }

        public sealed record RuntimeAttestation(
            string SchemaVersion,
            string MigrationSha256,
            string CatalogSha256,
            int ServerVersionNumber,
            string MigratorRole,
            string RuntimeRole);
    }
}
