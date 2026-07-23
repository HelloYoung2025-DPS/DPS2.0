using System.Security.Cryptography;
using Dps.ControlPlaneHost.Contracts;
using Npgsql;
using Xunit;
using static Dps.ControlPlaneHost.Tests.ReleaseBindingRecoveryTestKit;

namespace Dps.ControlPlaneHost.Tests;

/// <summary>
/// REAL_POSTGRESQL (18.4, DPS_TEST_POSTGRES) integration matrix for the
/// durable release binding truth journal (F3) and the database-issued
/// recovery revision fence (F2). Every binding state is established through
/// the production API (Activate / Revoke / Rollback / CreateRecoveryAsync);
/// tests never insert journal rows directly — the runtime role cannot.
/// </summary>
public sealed class PostgresReleaseBindingTruthStoreIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task RestartRebuildsExactStateReceiptsAndRollbackIdempotency()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var firstStore = database.CreateStore();
        var first = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], firstStore, () => Now);
        Assert.True(first.RecoveryCapability.IsDurable);
        first.RecoveryCapability.RequireDurable();

        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        first.Activate(Device, bom1, token1);
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        first.Activate(Device, bom2, signer.StableTwin(bom1), token2);
        var rollbackReceipt = first.Rollback(Device, token1);
        Assert.True(first.TryReadActive(Device, out var beforeRestart));

        // Process restart: a brand-new store instance replays the journal
        // and the new authority re-runs the full cross-binding verification.
        var restarted = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        Assert.True(restarted.TryReadActive(Device, out var afterRestart));
        Assert.Equal(beforeRestart, afterRestart);
        Assert.Equal(first.ReadReceipts(Device), restarted.ReadReceipts(Device));

        // Redelivery after restart: the same rollback request replays the
        // original receipt without a new journal row.
        Assert.Equal(3, await firstStore.CountJournalAsync(Token));
        Assert.Equal(rollbackReceipt, restarted.Rollback(Device, token1));
        Assert.Equal(3, await firstStore.CountJournalAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ActivationRedeliveryAcrossRestartReturnsTheOriginalReceipt()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);
        var (bom, token) = signer.SignBom("bom-1", 1, null);
        var receipt = authority.Activate(Device, bom, token);

        var restarted = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        Assert.Equal(receipt, restarted.Activate(Device, bom, token));
        Assert.Equal(1, await store.CountJournalAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PreviousStableBomWireRoundTripsExactlyAndRecovers()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);

        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var stableBom1 = signer.StableTwin(bom1);
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        authority.Activate(Device, bom2, stableBom1, token2);

        var records = database.CreateStore().LoadAll();
        Assert.Equal(2, records.Count);
        Assert.Null(records[0].PreviousStableBomBytes);
        Assert.Equal(
            stableBom1,
            Assert.IsType<byte[]>(records[1].PreviousStableBomBytes));

        var restarted = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        Assert.True(restarted.TryReadActive(Device, out var recovered));
        Assert.Equal(Sha256Hex(bom2), recovered!.ReleaseBomSha256);
        Assert.Equal(token2, recovered.ExecutionTokenBase64);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PreviousStableBomDatabaseShapeRejectsBootstrapAndNonActivationBytes()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();

        var memoryStore = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var memoryAuthority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], memoryStore, () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        memoryAuthority.Activate(Device, bom1, token1);
        Assert.True(memoryAuthority.TryReadActive(Device, out var active));
        memoryAuthority.Revoke(Device, active!.Generation);
        var validRecords = memoryStore.LoadAll();
        Assert.Equal(2, validRecords.Count);

        var invalidBootstrap = validRecords[0] with
        {
            PreviousStableBomBytes = signer.StableTwin(bom1)
        };
        var bootstrapException = Assert.Throws<PostgresException>(
            () => store.Append(invalidBootstrap));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bootstrapException.SqlState);
        Assert.Equal(
            "release_binding_journal_previous_stable_bom_shape",
            bootstrapException.ConstraintName);
        Assert.Equal(0, await store.CountJournalAsync(Token));

        store.Append(validRecords[0]);
        var invalidRevocation = validRecords[1] with
        {
            PreviousStableBomBytes = signer.StableTwin(bom1)
        };
        var revocationException = Assert.Throws<PostgresException>(
            () => store.Append(invalidRevocation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, revocationException.SqlState);
        Assert.Equal(
            "release_binding_journal_previous_stable_bom_shape",
            revocationException.ConstraintName);
        Assert.Equal(1, await store.CountJournalAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task TamperedPersistedPreviousStableBomFailsClosedDuringRecovery()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var stableBom1 = signer.StableTwin(bom1);
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        authority.Activate(Device, bom2, stableBom1, token2);

        // Model privileged storage corruption only after both valid
        // transitions have been established through the production API.
        // Change one canonical Base64 signature character so the persisted
        // wire stays structurally valid but can no longer authenticate.
        var tamperedStableBom1 = stableBom1.ToArray();
        var signatureMarker =
            System.Text.Encoding.ASCII.GetBytes("\"value\":\"");
        var markerOffset = tamperedStableBom1.AsSpan().IndexOf(signatureMarker);
        Assert.True(markerOffset >= 0);
        var signatureValueOffset = markerOffset + signatureMarker.Length;
        tamperedStableBom1[signatureValueOffset] =
            tamperedStableBom1[signatureValueOffset] == (byte)'A'
                ? (byte)'B'
                : (byte)'A';

        var quotedSchema =
            new NpgsqlCommandBuilder().QuoteIdentifier(database.SchemaName);
        await using (var admin =
            new NpgsqlConnection(database.MigrationConnectionString))
        {
            await admin.OpenAsync(Token);
            await using var transaction = await admin.BeginTransactionAsync(Token);
            await using (var disableTrigger = new NpgsqlCommand(
                $"""
                ALTER TABLE {quotedSchema}.release_binding_journal
                    DISABLE TRIGGER release_binding_journal_append_only_rows
                """,
                admin,
                transaction))
            {
                await disableTrigger.ExecuteNonQueryAsync(Token);
            }

            await using (var tamper = new NpgsqlCommand(
                $"""
                UPDATE {quotedSchema}.release_binding_journal
                SET previous_stable_bom_wire = @previous_stable_bom_wire
                WHERE device_binding_id = @device_binding_id
                  AND sequence = 2
                """,
                admin,
                transaction))
            {
                tamper.Parameters.AddWithValue(
                    "previous_stable_bom_wire",
                    tamperedStableBom1);
                tamper.Parameters.AddWithValue("device_binding_id", Device);
                Assert.Equal(1, await tamper.ExecuteNonQueryAsync(Token));
            }

            await using (var enableTrigger = new NpgsqlCommand(
                $"""
                ALTER TABLE {quotedSchema}.release_binding_journal
                    ENABLE TRIGGER release_binding_journal_append_only_rows
                """,
                admin,
                transaction))
            {
                await enableTrigger.ExecuteNonQueryAsync(Token);
            }
            await transaction.CommitAsync(Token);
        }

        var corruptedRecords = database.CreateStore().LoadAll();
        Assert.Equal(
            tamperedStableBom1,
            Assert.IsType<byte[]>(corruptedRecords[1].PreviousStableBomBytes));
        Assert.Throws<ActiveReleaseBindingException>(() =>
            new ActiveReleaseBindingAuthority(
                [signer.TrustKey], database.CreateStore(), () => Now));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FourMiBPreviousStableBomPersistsAndOneByteOverFailsBeforeAnyWrite()
    {
        const int maximumBomBytes = 4 * 1024 * 1024;
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        // Model a marked early-R0-C schema whose signed BOM constraint still
        // carried the former 1 MiB ceiling, then re-run the production
        // migration. PostgreSQL CREATE TABLE IF NOT EXISTS cannot replace
        // that constraint by itself, so this guards the explicit upgrade
        // path as well as a fresh-schema install.
        await database.ReapplyOverLegacyOneMiBSignedBomConstraintAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);

        var (maximumBom, maximumToken) = signer.SignBom(
            "bom-maximum", 1, null, exactWireBytes: maximumBomBytes);
        Assert.Equal(maximumBomBytes, maximumBom.Length);
        authority.Activate(Device, maximumBom, maximumToken);
        Assert.Equal(1, await store.CountJournalAsync(Token));

        var maximumStableBom = signer.StableTwin(maximumBom);
        Assert.Equal(maximumBomBytes, maximumStableBom.Length);
        var (nextBom, nextToken) = signer.SignBom(
            "bom-after-maximum",
            2,
            maximumBom);
        var oversizedStableBom = new byte[maximumBomBytes + 1];
        maximumStableBom.CopyTo(oversizedStableBom, 0);
        oversizedStableBom[^1] = (byte)' ';
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                nextBom,
                oversizedStableBom,
                nextToken));
        Assert.Equal(1, await store.CountJournalAsync(Token));

        authority.Activate(Device, nextBom, maximumStableBom, nextToken);
        Assert.Equal(2, await store.CountJournalAsync(Token));
        var records = database.CreateStore().LoadAll();
        Assert.Equal(
            maximumStableBom,
            Assert.IsType<byte[]>(records[1].PreviousStableBomBytes));

        var restarted = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        Assert.True(restarted.TryReadActive(Device, out var recovered));
        Assert.Equal(Sha256Hex(nextBom), recovered!.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task TwoInstancesOnOneDatabaseCasLoserFailsClosed()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var leftStore = database.CreateStore();
        var left = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], leftStore, () => Now);
        var right = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);

        var (leftBom, leftToken) = signer.SignBom("bom-1", 1, null);
        var winnerReceipt = left.Activate(Device, leftBom, leftToken);
        var (rightBom, rightToken) = signer.SignBom("bom-1b", 1, null);
        Assert.Throws<ActiveReleaseBindingException>(
            () => right.Activate(Device, rightBom, rightToken));

        // The loser published nothing and the journal holds exactly the
        // winner's record. Revision-aware reads: the loser's cached empty
        // view is stale, so its next read resyncs from the shared journal
        // and serves the winner's binding — it can never report the
        // superseded empty state as the truth.
        Assert.Equal(1, await leftStore.CountJournalAsync(Token));
        Assert.True(right.TryReadActive(Device, out var resynced));
        Assert.Equal(Sha256Hex(leftBom), resynced!.ReleaseBomSha256);
        Assert.Equal(winnerReceipt, Assert.Single(right.ReadReceipts(Device)));
        // A fresh instance recovers the same winner's truth.
        var recovered = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        Assert.True(recovered.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(leftBom), binding!.ReleaseBomSha256);
        Assert.Equal(winnerReceipt, Assert.Single(recovered.ReadReceipts(Device)));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("activation")]
    [InlineData("revocation")]
    [InlineData("rollback")]
    public async Task TwoStaleAuthoritiesExactlyRedeliverOneDurableTransition(
        string transitionKind)
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var writerStore = database.CreateStore();
        var commitThenTimeoutStore =
            new PostgresCommitThenTimeoutTruthStore(writerStore);
        var writer = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], commitThenTimeoutStore, () => Now);

        Func<ReleaseBindingReceiptV1> exactWriter;
        Func<ReleaseBindingReceiptV1> exactStale;
        Func<ReleaseBindingReceiptV1> conflictingStale;
        ActiveReleaseBindingAuthority stale;

        switch (transitionKind)
        {
            case "activation":
            {
                stale = new ActiveReleaseBindingAuthority(
                    [signer.TrustKey], database.CreateStore(), () => Now);
                var (bom, token) = signer.SignBom("bom-target", 1, null);
                var (conflictingBom, conflictingToken) =
                    signer.SignBom("bom-conflict", 1, null);
                exactWriter = () => writer.Activate(Device, bom, token);
                exactStale = () => stale.Activate(Device, bom, token);
                conflictingStale = () => stale.Activate(
                    Device,
                    conflictingBom,
                    conflictingToken);
                break;
            }
            case "revocation":
            {
                var (bom, token) = signer.SignBom("bom-active", 1, null);
                writer.Activate(Device, bom, token);
                stale = new ActiveReleaseBindingAuthority(
                    [signer.TrustKey], database.CreateStore(), () => Now);
                exactWriter = () => writer.Revoke(Device, 1);
                exactStale = () => stale.Revoke(Device, 1);
                conflictingStale = () => stale.Revoke(Device, 2);
                break;
            }
            case "rollback":
            {
                var (firstBom, firstToken) =
                    signer.SignBom("bom-first", 1, null);
                writer.Activate(Device, firstBom, firstToken);
                var (secondBom, secondToken) =
                    signer.SignBom("bom-second", 2, firstBom);
                writer.Activate(
                    Device,
                    secondBom,
                    signer.StableTwin(firstBom),
                    secondToken);
                writer.Revoke(Device, 2);
                stale = new ActiveReleaseBindingAuthority(
                    [signer.TrustKey], database.CreateStore(), () => Now);
                exactWriter = () => writer.Rollback(Device, firstToken);
                exactStale = () => stale.Rollback(Device, firstToken);
                conflictingStale =
                    () => stale.Rollback(Device, Token("wrong-rollback"));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transitionKind),
                    transitionKind,
                    "unknown transition kind");
        }

        var rowsBefore = await writerStore.CountJournalAsync(Token);
        commitThenTimeoutStore.TimeoutAfterNextCommit();
        Assert.Throws<TimeoutException>(() => exactWriter());
        var committed = writerStore.LoadAll()[^1].Receipt;
        var replayed = exactStale();

        Assert.Equal(committed, replayed);
        Assert.Equal(rowsBefore + 1, await writerStore.CountJournalAsync(Token));
        Assert.Equal(writer.ReadReceipts(Device), stale.ReadReceipts(Device));

        Assert.Throws<ActiveReleaseBindingException>(() => conflictingStale());
        Assert.Equal(rowsBefore + 1, await writerStore.CountJournalAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task TwoInstanceSupersessionResyncsAndNeverServesTheStaleBinding()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();

        // Both instances recover the SAME activated BOM A over the shared
        // durable journal.
        var bootstrap = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        var (bomA, tokenA) = signer.SignBom("bom-a", 1, null);
        bootstrap.Activate(Device, bomA, tokenA);
        var one = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        var two = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        Assert.True(one.TryReadActive(Device, out var aOne));
        Assert.True(two.TryReadActive(Device, out var aTwo));
        Assert.Equal(Sha256Hex(bomA), aOne!.ReleaseBomSha256);
        Assert.Equal(aOne, aTwo);

        // Supersession by activation: instance-1 activates BOM B; instance-2
        // must NEVER serve A again — the resync serves B's exact
        // generation/digest/token and the receipt trails converge.
        var (bomB, tokenB) = signer.SignBom("bom-b", 2, bomA);
        var supersedingReceipt = one.Activate(
            Device,
            bomB,
            signer.StableTwin(bomA),
            tokenB);
        Assert.True(two.TryReadActive(Device, out var bTwo));
        Assert.Equal(Sha256Hex(bomB), bTwo!.ReleaseBomSha256);
        Assert.Equal(2, bTwo.Generation);
        Assert.Equal(tokenB, bTwo.ExecutionTokenBase64);
        Assert.NotEqual(aTwo, bTwo);
        Assert.Equal(one.ReadReceipts(Device), two.ReadReceipts(Device));
        Assert.Equal(supersedingReceipt, two.ReadReceipts(Device)[^1]);

        // Supersession by revocation: instance-2 fails closed instead of
        // dispatching the revoked BOM B.
        one.Revoke(Device, bTwo.Generation);
        Assert.False(two.TryReadActive(Device, out var revoked));
        Assert.Null(revoked);
        Assert.Equal(one.ReadReceipts(Device), two.ReadReceipts(Device));

        // Supersession by rollback: instance-1 rolls back to A; instance-2
        // serves the rolled-back truth — A's digest and token at the NEW
        // runtime generation 3, never the stale generation-1 A and never
        // the revoked B.
        one.Rollback(Device, tokenA);
        Assert.True(two.TryReadActive(Device, out var rolledBack));
        Assert.Equal(Sha256Hex(bomA), rolledBack!.ReleaseBomSha256);
        Assert.Equal(3, rolledBack.Generation);
        Assert.Equal(1, rolledBack.ReleaseBomGeneration);
        Assert.Equal(tokenA, rolledBack.ExecutionTokenBase64);
        Assert.Equal(one.ReadReceipts(Device), two.ReadReceipts(Device));
        Assert.Equal(4, two.ReadReceipts(Device).Count);

        // Store outage: instance-2 holds a valid cached view (A active) but
        // the durable journal cannot be consulted — every authoritative
        // read fails closed (false / throw) instead of serving the cache
        // without a freshness proof.
        await database.DisableRuntimeLoginAsync(Token);
        Assert.False(two.TryReadActive(Device, out var outage));
        Assert.Null(outage);
        Assert.Throws<ActiveReleaseBindingException>(() => two.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentActivationsLandExactlyOneJournalRecord()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var left = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);
        var right = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);

        var (leftBom, leftToken) = signer.SignBom("bom-1", 1, null);
        var (rightBom, rightToken) = signer.SignBom("bom-1b", 1, null);
        var results = await Task.WhenAll(
            Task.Run(() => Attempt(() => left.Activate(Device, leftBom, leftToken)), Token),
            Task.Run(() => Attempt(() => right.Activate(Device, rightBom, rightToken)), Token));

        Assert.Equal(1, results.Count(static result => result is null));
        var loser = Assert.Single(results, static result => result is not null);
        Assert.IsAssignableFrom<ActiveReleaseBindingException>(loser);
        Assert.Equal(1, await store.CountJournalAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RevocationSurvivesRestartAndNoRollbackPathRemains()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        authority.Activate(Device, bom2, signer.StableTwin(bom1), token2);
        Assert.True(authority.TryReadActive(Device, out var active));
        authority.Revoke(Device, active!.Generation);

        // Activating over the revoked binding drops every rollback path
        // (the demoted bom-1 included) — the anti-resurrection rule.
        var (bom3, token3) = signer.SignBom("bom-3", 3, bom2);
        authority.Activate(Device, bom3, signer.StableTwin(bom2), token3);

        // Restart: the revocation and the dropped rollback paths are
        // durable — neither the revoked bom-2 nor the pre-revocation bom-1
        // can be rolled back to, and the active truth is bom-3.
        var restarted = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], database.CreateStore(), () => Now);
        Assert.Throws<ActiveReleaseBindingException>(
            () => restarted.Rollback(Device, token2));
        Assert.Throws<ActiveReleaseBindingException>(
            () => restarted.Rollback(Device, token1));
        Assert.True(restarted.TryReadActive(Device, out var reactivated));
        Assert.Equal(Sha256Hex(bom3), reactivated!.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FenceCommitIsAtomicIdempotentAndConflictsFailClosed()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);

        // No journal: issuance fails closed.
        Assert.Throws<ActiveReleaseBindingException>(
            () => store.IssueRecoveryFence(Device));

        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var fence = store.IssueRecoveryFence(Device);
        Assert.Equal(1, fence.JournalSequence);
        Assert.Equal(Sha256Hex(bom1), fence.ReleaseBomSha256);

        var recoveryId = Guid.Parse("cccccccc-3333-4333-8333-cccccccccccc");
        var content = new string('a', 64);
        store.CommitRecoveryFence(fence, recoveryId, content);
        // Crash window ② redelivery: same recovery id and content replays
        // idempotently on a fresh store instance (no second row).
        database.CreateStore().CommitRecoveryFence(fence, recoveryId, content);
        Assert.Equal(1, await store.CountRecoveryFencesAsync(Token));
        // Same recovery id with different content fails closed.
        Assert.Throws<ReleaseBindingRecoveryFenceConflictException>(
            () => store.CommitRecoveryFence(fence, recoveryId, new string('b', 64)));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FenceLosesToActivationAndRevocationRaces()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);

        // Crash window ① / activation race: fence issued, then the binding
        // advances before commit — the commit-side compare-and-set refuses
        // and the abandoned issuance leaves no residue.
        var staleFence = store.IssueRecoveryFence(Device);
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        authority.Activate(Device, bom2, signer.StableTwin(bom1), token2);
        Assert.Throws<ReleaseBindingRecoveryFenceConflictException>(
            () => store.CommitRecoveryFence(
                staleFence,
                Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd"),
                new string('a', 64)));
        Assert.Equal(0, await store.CountRecoveryFencesAsync(Token));

        // A fence issued at the new head commits.
        var freshFence = store.IssueRecoveryFence(Device);
        Assert.Equal(2, freshFence.JournalSequence);
        store.CommitRecoveryFence(
            freshFence,
            Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd"),
            new string('a', 64));

        // Revocation race: an issued fence dies with the revocation and no
        // new fence can be issued afterwards.
        var preRevocationFence = store.IssueRecoveryFence(Device);
        Assert.True(authority.TryReadActive(Device, out var active));
        authority.Revoke(Device, active!.Generation);
        Assert.Throws<ReleaseBindingRecoveryFenceConflictException>(
            () => store.CommitRecoveryFence(
                preRevocationFence,
                Guid.Parse("eeeeeeee-5555-4555-8555-eeeeeeeeeeee"),
                new string('a', 64)));
        Assert.Throws<ActiveReleaseBindingException>(
            () => store.IssueRecoveryFence(Device));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentRecoveriesAtOneHeadAllCommitAndReplaysStaySingle()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var fence = store.IssueRecoveryFence(Device);
        var content = new string('a', 64);

        // Distinct concurrent recoveries at the same unadvanced revision are
        // independent fence rows, all successful.
        var distinctIds = Enumerable.Range(0, 4)
            .Select(static index => Guid.Parse(
                $"{index}{index}{index}{index}{index}{index}{index}{index}-1111-4111-8111-111111111111"))
            .ToArray();
        await Task.WhenAll(distinctIds.Select(id => Task.Run(
            () => database.CreateStore().CommitRecoveryFence(fence, id, content),
            Token)));
        Assert.Equal(4, await store.CountRecoveryFencesAsync(Token));

        // Concurrent duplicate delivery of one recovery: both succeed, one row.
        var duplicateId = Guid.Parse("ffffffff-6666-4666-8666-ffffffffffff");
        await Task.WhenAll(
            Task.Run(() => database.CreateStore().CommitRecoveryFence(fence, duplicateId, content), Token),
            Task.Run(() => database.CreateStore().CommitRecoveryFence(fence, duplicateId, content), Token));
        Assert.Equal(5, await store.CountRecoveryFencesAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ProducerRecoveryCommitsThePostgresFenceAndRefusesRaces()
    {
        await using var database = await ReleaseBindingTestDatabase.CreateAsync(Token);
        using var signer = new BomSigner();
        var store = database.CreateStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);
        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        Assert.True(authority.TryReadActive(Device, out var active));

        // Production recovery path with the durable fence: issuance commits
        // exactly one fence row; redelivery of the same recovery replays
        // idempotently even though the human signature differs.
        using (var harness = new RecoveryLifecycleHarness(
            new PolicyBoundReleaseBomFactsSource(authority),
            store))
        {
            var request = RecoveryRequest(active!.ReleaseBomSha256, active.Generation);
            var envelope = await harness.RecoverAsync(request);
            Assert.Equal(active.ReleaseBomSha256, envelope.Value.NextReleaseBomSha256);
            Assert.Equal(1, await store.CountRecoveryFencesAsync(Token));
            await harness.RecoverAsync(request);
            Assert.Equal(2, harness.RecoverySigner.CallCount);
            Assert.Equal(1, await store.CountRecoveryFencesAsync(Token));
        }

        // Fence race through the producer: the binding advances while the
        // human signer works (facts snapshot deliberately frozen), so only
        // the database fence commit can refuse — and it does, atomically.
        Assert.True(authority.TryReadActive(Device, out var snapshot));
        using var frozenHarness = new RecoveryLifecycleHarness(
            new PolicyBoundReleaseBomFactsSource(new FrozenReader(snapshot!)),
            store);
        frozenHarness.RecoverySigner.WhileSigning = () =>
        {
            var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
            authority.Activate(Device, bom2, signer.StableTwin(bom1), token2);
        };
        await Assert.ThrowsAsync<ReleaseBindingRecoveryFenceConflictException>(() =>
            frozenHarness.RecoverAsync(RecoveryRequest(
                snapshot!.ReleaseBomSha256,
                snapshot.Generation,
                Guid.Parse("aaaaaaaa-7777-4777-8777-aaaaaaaaaaaa"))));
        Assert.Equal(1, frozenHarness.RecoverySigner.CallCount);
        Assert.Equal(1, await store.CountRecoveryFencesAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MigratorRefusesAnUnmarkedPreexistingSchema()
    {
        var migrationConnectionString =
            ReleaseBindingTestDatabase.RequireConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..20];
        var schemaName = "rbind_unmarked_" + suffix;
        var runtimeRoleName = "rbind_unmarked_rt_" + suffix;
        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schemaName);
        var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRoleName);

        await using var admin = new NpgsqlConnection(migrationConnectionString);
        await admin.OpenAsync(Token);
        try
        {
            await using (var prepare = new NpgsqlCommand(
                $"""
                CREATE ROLE {quotedRole}
                    NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
                    NOREPLICATION NOBYPASSRLS;
                CREATE SCHEMA {quotedSchema};
                """,
                admin))
            {
                await prepare.ExecuteNonQueryAsync(Token);
            }

            var migrator = new PostgresReleaseBindingTruthMigrator(
                new PostgresReleaseBindingMigrationOptions(
                    migrationConnectionString,
                    schemaName,
                    runtimeRoleName));
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => migrator.InitializeAsync(Token));
            Assert.StartsWith(
                "release-binding migration refuses an unmarked pre-existing schema",
                exception.MessageText);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE; DROP ROLE IF EXISTS {quotedRole}",
                admin);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static Exception? Attempt(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Wraps the real PostgreSQL transition scope and loses only the
    /// acknowledgement after its Append has returned. The inner Append has
    /// already committed the real transaction and released its advisory lock,
    /// so this models the ambiguous client outcome without simulating the
    /// durable write itself.
    /// </summary>
    private sealed class PostgresCommitThenTimeoutTruthStore(
        PostgresReleaseBindingTruthStore inner)
        : IReleaseBindingTruthStore,
          IActiveReleaseBindingRecoveryCoordinator
    {
        private int _timeoutAfterNextCommit;

        internal void TimeoutAfterNextCommit()
            => Interlocked.Exchange(ref _timeoutAfterNextCommit, 1);

        public void Append(ReleaseBindingTruthRecord record)
            => inner.Append(record);

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll()
            => inner.LoadAll();

        public long LoadDeviceHeadSequence(string deviceBindingId)
            => inner.LoadDeviceHeadSequence(deviceBindingId);

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(
            string deviceBindingId,
            long afterSequence)
            => inner.LoadAfter(deviceBindingId, afterSequence);

        public ReleaseBindingJournalSnapshot LoadSnapshotAfter(
            string deviceBindingId,
            long afterSequence)
            => inner.LoadSnapshotAfter(deviceBindingId, afterSequence);

        public IReleaseBindingTransitionScope BeginTransition(
            string deviceBindingId)
            => new PostgresCommitThenTimeoutScope(
                this,
                inner.BeginTransition(deviceBindingId));

        ValueTask<IActiveReleaseBindingRecoveryScope>
            IActiveReleaseBindingRecoveryCoordinator.AcquireAsync(
                string deviceBindingId,
                CancellationToken cancellationToken)
            => ((IActiveReleaseBindingRecoveryCoordinator)inner).AcquireAsync(
                deviceBindingId,
                cancellationToken);

        private sealed class PostgresCommitThenTimeoutScope(
            PostgresCommitThenTimeoutTruthStore owner,
            IReleaseBindingTransitionScope innerScope)
            : IReleaseBindingTransitionScope
        {
            public void Append(ReleaseBindingTruthRecord record)
            {
                innerScope.Append(record);
                if (Interlocked.Exchange(
                        ref owner._timeoutAfterNextCommit,
                        0) == 1)
                {
                    throw new TimeoutException(
                        "simulated response loss after the PostgreSQL commit");
                }
            }

            public void Dispose() => innerScope.Dispose();
        }
    }

    private sealed class FrozenReader(Contracts.ActiveReleaseBindingV1 binding)
        : IActiveReleaseBindingReader
    {
        public bool TryReadActive(
            string deviceBindingId,
            out Contracts.ActiveReleaseBindingV1? value)
        {
            value = binding;
            return true;
        }
    }

    /// <summary>
    /// One disposable release binding truth database per test: a fresh
    /// least-privilege runtime login role and a fresh marked schema created
    /// by the production migrator; disposal drops both.
    /// </summary>
    private sealed class ReleaseBindingTestDatabase : IAsyncDisposable
    {
        private readonly string _migrationConnectionString;
        private readonly string _runtimeConnectionString;
        private readonly string _schemaName;
        private readonly string _runtimeRoleName;
        private readonly string _migrationRoleName;

        private ReleaseBindingTestDatabase(
            string migrationConnectionString,
            string runtimeConnectionString,
            string schemaName,
            string runtimeRoleName,
            string migrationRoleName)
        {
            _migrationConnectionString = migrationConnectionString;
            _runtimeConnectionString = runtimeConnectionString;
            _schemaName = schemaName;
            _runtimeRoleName = runtimeRoleName;
            _migrationRoleName = migrationRoleName;
        }

        internal static string RequireConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES is required for REAL_POSTGRESQL release binding Integration; missing infrastructure is not a skip or pass.");
            }

            return value;
        }

        internal static async Task<ReleaseBindingTestDatabase> CreateAsync(
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
                    "Release binding Integration refuses the persistent GBrain Company database.");
            }

            if (string.IsNullOrWhiteSpace(migrationBuilder.Username))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES requires an explicit migration username.");
            }

            var suffix = Guid.NewGuid().ToString("N")[..20];
            var schemaName = "rbind_it_" + suffix;
            var runtimeRoleName = "rbind_rt_" + suffix;
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
                                $"Release binding Integration requires exact PostgreSQL 18.4; server_version_num was '{actual ?? "missing"}'.");
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
                            ?? throw new InvalidOperationException(
                                "PostgreSQL did not produce the controlled CREATE ROLE statement."));
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
                var migrator = new PostgresReleaseBindingTruthMigrator(
                    new PostgresReleaseBindingMigrationOptions(
                        migrationConnectionString,
                        schemaName,
                        runtimeRoleName));
                await migrator.InitializeAsync(cancellationToken);
                return new ReleaseBindingTestDatabase(
                    migrationConnectionString,
                    runtimeBuilder.ConnectionString,
                    schemaName,
                    runtimeRoleName,
                    migrationRoleName);
            }
            catch
            {
                await CleanupAsync(
                    migrationConnectionString,
                    schemaName,
                    runtimeRoleName,
                    CancellationToken.None);
                throw;
            }
        }

        internal PostgresReleaseBindingTruthStore CreateStore()
            => new(new PostgresReleaseBindingTruthStoreOptions(
                _runtimeConnectionString,
                _schemaName,
                _runtimeRoleName,
                _migrationRoleName));

        internal string MigrationConnectionString => _migrationConnectionString;

        internal string SchemaName => _schemaName;

        internal async Task ReapplyOverLegacyOneMiBSignedBomConstraintAsync(
            CancellationToken cancellationToken)
        {
            var quotedSchema =
                new NpgsqlCommandBuilder().QuoteIdentifier(_schemaName);
            await using (var admin =
                new NpgsqlConnection(_migrationConnectionString))
            {
                await admin.OpenAsync(cancellationToken);
                await using var legacyConstraint = new NpgsqlCommand(
                    $"""
                    ALTER TABLE {quotedSchema}.release_binding_journal
                        DROP CONSTRAINT release_binding_journal_bom_bytes;
                    ALTER TABLE {quotedSchema}.release_binding_journal
                        ADD CONSTRAINT release_binding_journal_bom_bytes CHECK (
                            signed_bom_wire IS NULL
                            OR (octet_length(signed_bom_wire) >= 1
                                AND octet_length(signed_bom_wire) <= 1048576));
                    """,
                    admin);
                await legacyConstraint.ExecuteNonQueryAsync(cancellationToken);
            }

            var migrator = new PostgresReleaseBindingTruthMigrator(
                new PostgresReleaseBindingMigrationOptions(
                    _migrationConnectionString,
                    _schemaName,
                    _runtimeRoleName));
            await migrator.InitializeAsync(cancellationToken);
        }

        /// <summary>
        /// Severs the runtime role's LOGIN right — a real PostgreSQL-level
        /// outage for every store instance of this database: subsequent
        /// connections fail authentication, so freshness reads cannot be
        /// consulted and must fail closed.
        /// </summary>
        internal async Task DisableRuntimeLoginAsync(CancellationToken cancellationToken)
        {
            var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(_runtimeRoleName);
            await using var admin = new NpgsqlConnection(_migrationConnectionString);
            await admin.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"ALTER ROLE {quotedRole} NOLOGIN",
                admin);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
            => await CleanupAsync(
                _migrationConnectionString,
                _schemaName,
                _runtimeRoleName,
                CancellationToken.None);

        private static async Task CleanupAsync(
            string migrationConnectionString,
            string schemaName,
            string runtimeRoleName,
            CancellationToken cancellationToken)
        {
            var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schemaName);
            var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRoleName);
            await using var admin = new NpgsqlConnection(migrationConnectionString);
            await admin.OpenAsync(cancellationToken);
            await using var cleanup = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE; DROP ROLE IF EXISTS {quotedRole}",
                admin);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
