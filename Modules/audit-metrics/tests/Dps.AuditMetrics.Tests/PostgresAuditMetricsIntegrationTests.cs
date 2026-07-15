using System.Security.Cryptography;
using System.Text;
using Dps.CommandOrchestrator.Contracts;
using Npgsql;
using Xunit;

namespace Dps.AuditMetrics.Tests;

public sealed class PostgresAuditMetricsIntegrationTests
{
    private const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DeviceA = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DeviceB = "db_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AccountA = "pa_cccccccccccccccccccccccccccccccc";
    private const string AccountB = "pa_dddddddddddddddddddddddddddddddd";
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact, Trait("Category", "Integration")]
    public async Task RealEcdsaVerificationPrecedesTransactionAndInvalidSignatureWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await AuditMetricsTestDatabase.CreateAsync(cancellationToken);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaAuditRelayAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        using var trustState = await database.CreateTrustStateAsync(
            verifier.PublicKeySha256,
            cancellationToken);
        var service = database.CreateService(verifier, trustState.Reader);
        var receipt = Receipt(1, SoulA, DeviceA, AccountA, "idem-authentic");
        var signed = SignEnvelope(signer, UnsignedEnvelope(receipt));
        var invalid = CorruptSignature(signed);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.AppendReceiptAsync(receipt, invalid, cancellationToken));
        Assert.Equal(0, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await service.CountQuarantineAsync(cancellationToken));

        await trustState.AppendAsync(
            2,
            verifier.PublicKeySha256,
            AuditRelayTrustStateEnvelope.Revoked,
            cancellationToken: cancellationToken);
        var revokedService = database.CreateService(verifier, trustState.Reader);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => revokedService.AppendReceiptAsync(receipt, signed, cancellationToken));
        Assert.Equal(0, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await service.CountQuarantineAsync(cancellationToken));

        using var activeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var activeVerifier = new EcdsaAuditRelayAuthorizationVerifier(
            activeKey.ExportSubjectPublicKeyInfo());
        await trustState.AppendAsync(
            3,
            activeVerifier.PublicKeySha256,
            AuditRelayTrustStateEnvelope.Active,
            cancellationToken: cancellationToken);
        var oldKeyService = database.CreateService(verifier, trustState.Reader);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => oldKeyService.AppendReceiptAsync(receipt, signed, cancellationToken));
        Assert.Equal(0, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await service.CountQuarantineAsync(cancellationToken));

        await trustState.AppendAsync(
            4,
            verifier.PublicKeySha256,
            AuditRelayTrustStateEnvelope.Active,
            cancellationToken: cancellationToken);
        var inactiveBom = SignEnvelope(
            signer,
            UnsignedEnvelope(receipt) with { ReleaseBomSha256 = new string('e', 64) });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.AppendReceiptAsync(receipt, inactiveBom, cancellationToken));
        Assert.Equal(0, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await service.CountQuarantineAsync(cancellationToken));

        var inserted = await service.AppendReceiptAsync(receipt, signed, cancellationToken);
        Assert.Equal(AuditAppendDisposition.Inserted, inserted.Disposition);
        Assert.Equal(1, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await service.CountQuarantineAsync(cancellationToken));

        var afterRotation = Receipt(2, SoulA, DeviceA, AccountA, "idem-after-rotation");
        var afterRotationEnvelope = SignEnvelope(signer, UnsignedEnvelope(afterRotation));
        await using var pendingRevocation = await trustState.BeginAppendAsync(
            5,
            verifier.PublicKeySha256,
            AuditRelayTrustStateEnvelope.Revoked,
            cancellationToken: cancellationToken);
        var appendWhileRevocationIsUncommitted = service.AppendReceiptAsync(
            afterRotation,
            afterRotationEnvelope,
            cancellationToken);
        await pendingRevocation.CommitAsync(cancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await appendWhileRevocationIsUncommitted);
        Assert.Equal(1, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await service.CountQuarantineAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentDuplicateAndDigestOrScopedIdempotencyConflictsAreIsolated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await AuditMetricsTestDatabase.CreateAsync(cancellationToken);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaAuditRelayAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        using var trustState = await database.CreateTrustStateAsync(
            verifier.PublicKeySha256,
            cancellationToken);
        var service = database.CreateService(verifier, trustState.Reader);
        var original = Receipt(10, SoulA, DeviceA, AccountA, "idem-concurrent");
        var originalEnvelope = SignEnvelope(signer, UnsignedEnvelope(original));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => service.AppendReceiptAsync(
                    original,
                    originalEnvelope,
                    cancellationToken)));

        Assert.Single(results, result => result.Disposition == AuditAppendDisposition.Inserted);
        Assert.Equal(15, results.Count(result => result.Disposition == AuditAppendDisposition.DuplicateNoOp));

        var raceOriginal = Receipt(11, SoulA, DeviceA, AccountA, "idem-digest-race");
        var sameIdDifferentDigest = raceOriginal with
        {
            EvidenceDigest = new string('b', 64),
            ResultCode = "VERIFIED_CHANGED"
        };
        var digestRace = await Task.WhenAll(
            service.AppendReceiptAsync(
                raceOriginal,
                SignEnvelope(signer, UnsignedEnvelope(raceOriginal)),
                cancellationToken),
            service.AppendReceiptAsync(
                sameIdDifferentDigest,
                SignEnvelope(signer, UnsignedEnvelope(sameIdDifferentDigest)),
                cancellationToken));
        Assert.Single(digestRace, result => result.Disposition == AuditAppendDisposition.Inserted);
        Assert.Single(digestRace, result => result.Disposition == AuditAppendDisposition.Quarantined);

        var sameScopedIdempotency = Receipt(12, SoulA, DeviceA, AccountA, raceOriginal.IdempotencyKey);
        var idempotencyConflict = await service.AppendReceiptAsync(
            sameScopedIdempotency,
            SignEnvelope(signer, UnsignedEnvelope(sameScopedIdempotency)),
            cancellationToken);
        Assert.Equal(AuditAppendDisposition.Quarantined, idempotencyConflict.Disposition);

        Assert.Equal(2, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(2, await service.CountQuarantineAsync(cancellationToken));
        var quarantine = await service.ReadQuarantineAsync(
            SoulA,
            DeviceA,
            AccountA,
            cancellationToken);
        Assert.All(quarantine, record =>
        {
            Assert.Matches("^[a-f0-9]{64}$", record.ScopeSha256);
            Assert.Matches("^[a-f0-9]{64}$", record.IdempotencySha256);
            Assert.NotEqual(record.ExistingRecordSha256, record.IncomingRecordSha256);
        });
        Assert.Empty(await service.ReadQuarantineAsync(
            SoulB,
            DeviceB,
            AccountB,
            cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RestartReadbackOrdersLateArrivalAndNeverLeaksAcrossExactScope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await AuditMetricsTestDatabase.CreateAsync(cancellationToken);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = signer.ExportSubjectPublicKeyInfo();
        using var trustVerifier = new EcdsaAuditRelayAuthorizationVerifier(publicKey);
        using var trustState = await database.CreateTrustStateAsync(
            trustVerifier.PublicKeySha256,
            cancellationToken);
        using (var firstVerifier = new EcdsaAuditRelayAuthorizationVerifier(publicKey))
        {
            var firstService = database.CreateService(firstVerifier, trustState.Reader);
            var later = Receipt(20, SoulA, DeviceA, AccountA, "idem-later") with
            {
                OccurredAt = Now.AddMinutes(2)
            };
            var earlier = Receipt(21, SoulA, DeviceA, AccountA, "idem-earlier") with
            {
                OccurredAt = Now
            };
            var otherScope = Receipt(22, SoulB, DeviceB, AccountB, "idem-other-scope");
            await firstService.AppendReceiptAsync(
                later,
                SignEnvelope(signer, UnsignedEnvelope(later)),
                cancellationToken);
            await firstService.AppendReceiptAsync(
                earlier,
                SignEnvelope(signer, UnsignedEnvelope(earlier)),
                cancellationToken);
            await firstService.AppendReceiptAsync(
                otherScope,
                SignEnvelope(signer, UnsignedEnvelope(otherScope)),
                cancellationToken);
        }

        using var restartedVerifier = new EcdsaAuditRelayAuthorizationVerifier(publicKey);
        var restarted = database.CreateService(restartedVerifier, trustState.Reader);
        var scopeA = await restarted.ReadScopeAsync(SoulA, DeviceA, AccountA, cancellationToken);
        Assert.Equal(2, scopeA.Count);
        Assert.True(scopeA[0].OccurredAt < scopeA[1].OccurredAt);
        Assert.Empty(await restarted.ReadScopeAsync(SoulA, DeviceB, AccountA, cancellationToken));
        Assert.Empty(await restarted.ReadScopeAsync(SoulB, DeviceA, AccountB, cancellationToken));
        Assert.Single(await restarted.ReadScopeAsync(SoulB, DeviceB, AccountB, cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RuntimeRoleCannotMutateDdlOrTruncateAndOwnerTriggersRemainAppendOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await AuditMetricsTestDatabase.CreateAsync(cancellationToken);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaAuditRelayAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        using var trustState = await database.CreateTrustStateAsync(
            verifier.PublicKeySha256,
            cancellationToken);
        var service = database.CreateService(verifier, trustState.Reader);
        var original = Receipt(30, SoulA, DeviceA, AccountA, "idem-append-only");
        await service.AppendReceiptAsync(
            original,
            SignEnvelope(signer, UnsignedEnvelope(original)),
            cancellationToken);
        var conflict = original with { EvidenceDigest = new string('c', 64) };
        await service.AppendReceiptAsync(
            conflict,
            SignEnvelope(signer, UnsignedEnvelope(conflict)),
            cancellationToken);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var sessionRole = new NpgsqlCommand("SELECT session_user", connection))
        {
            Assert.Equal(database.RuntimeRoleName, await sessionRole.ExecuteScalarAsync(cancellationToken));
        }
        await using (var resetRole = new NpgsqlCommand("RESET ROLE", connection))
        {
            await resetRole.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var currentRole = new NpgsqlCommand("SELECT current_user", connection))
        {
            Assert.Equal(database.RuntimeRoleName, await currentRole.ExecuteScalarAsync(cancellationToken));
        }
        await AssertSqlStateAsync(
            connection,
            $"UPDATE {database.SchemaName}.audit_events SET occurred_at = occurred_at",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"DELETE FROM {database.SchemaName}.audit_events",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"UPDATE {database.SchemaName}.audit_quarantine SET reason = reason",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"DELETE FROM {database.SchemaName}.audit_quarantine",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"TRUNCATE {database.SchemaName}.audit_quarantine, {database.SchemaName}.audit_events",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"UPDATE {database.SchemaName}.audit_relay_trust_states SET revision = revision",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"DELETE FROM {database.SchemaName}.audit_relay_trust_states",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"TRUNCATE {database.SchemaName}.audit_relay_trust_states",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"ALTER TABLE {database.SchemaName}.audit_events ADD COLUMN forbidden text",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"DROP TABLE {database.SchemaName}.audit_events",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);
        await AssertSqlStateAsync(
            connection,
            $"CREATE TABLE {database.SchemaName}.forbidden(id integer)",
            PostgresErrorCodes.InsufficientPrivilege,
            cancellationToken);

        await using var ownerConnection = new NpgsqlConnection(database.AdminConnectionString);
        await ownerConnection.OpenAsync(cancellationToken);
        await using (var allowOwnershipProbe = new NpgsqlCommand(
            $"GRANT {database.RuntimeRoleName} TO CURRENT_USER",
            ownerConnection))
        {
            await allowOwnershipProbe.ExecuteNonQueryAsync(cancellationToken);
        }
        try
        {
            await using var transferOwnership = new NpgsqlCommand(
                $"ALTER TABLE {database.SchemaName}.audit_events OWNER TO {database.RuntimeRoleName}",
                ownerConnection);
            await transferOwnership.ExecuteNonQueryAsync(cancellationToken);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.CountEventsAsync(cancellationToken));
        }
        finally
        {
            try
            {
                await using var restoreOwnership = new NpgsqlCommand(
                    $"ALTER TABLE {database.SchemaName}.audit_events OWNER TO CURRENT_USER",
                    ownerConnection);
                await restoreOwnership.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                await using var removeOwnershipProbe = new NpgsqlCommand(
                    $"REVOKE {database.RuntimeRoleName} FROM CURRENT_USER",
                    ownerConnection);
                await removeOwnershipProbe.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        await AssertSqlStateAsync(
            ownerConnection,
            $"UPDATE {database.SchemaName}.audit_events SET occurred_at = occurred_at",
            "P0001",
            cancellationToken);
        await AssertSqlStateAsync(
            ownerConnection,
            $"DELETE FROM {database.SchemaName}.audit_quarantine",
            "P0001",
            cancellationToken);
        await AssertSqlStateAsync(
            ownerConnection,
            $"TRUNCATE {database.SchemaName}.audit_quarantine, {database.SchemaName}.audit_events",
            "P0001",
            cancellationToken);
        await AssertSqlStateAsync(
            ownerConnection,
            $"DELETE FROM {database.SchemaName}.audit_relay_trust_states",
            "P0001",
            cancellationToken);
        await AssertSqlStateAsync(
            ownerConnection,
            $"TRUNCATE {database.SchemaName}.audit_relay_trust_states",
            "P0001",
            cancellationToken);
    }

    [Theory]
    [InlineData(AuditAppendStage.EventInserted)]
    [InlineData(AuditAppendStage.BeforeCommit)]
    [Trait("Category", "Integration")]
    public async Task EventCrashWindowRollsBackAndRetryRecovers(AuditAppendStage failureStage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await AuditMetricsTestDatabase.CreateAsync(cancellationToken);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaAuditRelayAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        using var trustState = await database.CreateTrustStateAsync(
            verifier.PublicKeySha256,
            cancellationToken);
        var injected = 0;
        var failing = database.CreateService(
            verifier,
            trustState.Reader,
            (stage, _) =>
            {
                if (stage == failureStage && Interlocked.Exchange(ref injected, 1) == 0)
                {
                    throw new InvalidOperationException("injected audit crash window");
                }

                return ValueTask.CompletedTask;
            });
        var receipt = Receipt(40 + (int)failureStage, SoulA, DeviceA, AccountA, $"idem-crash-{failureStage}");
        var envelope = SignEnvelope(signer, UnsignedEnvelope(receipt));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failing.AppendReceiptAsync(receipt, envelope, cancellationToken));
        var recovered = database.CreateService(verifier, trustState.Reader);
        Assert.Equal(0, await recovered.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await recovered.CountQuarantineAsync(cancellationToken));
        Assert.Equal(
            AuditAppendDisposition.Inserted,
            (await recovered.AppendReceiptAsync(receipt, envelope, cancellationToken)).Disposition);
        Assert.Equal(1, await recovered.CountEventsAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task QuarantineCrashWindowRollsBackAndRetryRecovers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await AuditMetricsTestDatabase.CreateAsync(cancellationToken);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaAuditRelayAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        using var trustState = await database.CreateTrustStateAsync(
            verifier.PublicKeySha256,
            cancellationToken);
        var service = database.CreateService(verifier, trustState.Reader);
        var original = Receipt(50, SoulA, DeviceA, AccountA, "idem-quarantine-crash");
        var originalEnvelope = SignEnvelope(signer, UnsignedEnvelope(original));
        await service.AppendReceiptAsync(original, originalEnvelope, cancellationToken);
        var conflict = original with { EvidenceDigest = new string('e', 64) };
        var conflictEnvelope = SignEnvelope(signer, UnsignedEnvelope(conflict));
        var injected = 0;
        var failing = database.CreateService(
            verifier,
            trustState.Reader,
            (stage, _) =>
            {
                if (stage == AuditAppendStage.QuarantineInserted
                    && Interlocked.Exchange(ref injected, 1) == 0)
                {
                    throw new InvalidOperationException("injected quarantine crash window");
                }

                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failing.AppendReceiptAsync(conflict, conflictEnvelope, cancellationToken));
        Assert.Equal(1, await service.CountEventsAsync(cancellationToken));
        Assert.Equal(0, await service.CountQuarantineAsync(cancellationToken));
        Assert.Equal(
            AuditAppendDisposition.Quarantined,
            (await service.AppendReceiptAsync(conflict, conflictEnvelope, cancellationToken)).Disposition);
        Assert.Equal(1, await service.CountQuarantineAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CancellationRollsBackAndReleasesAppendLocksForSafeRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await AuditMetricsTestDatabase.CreateAsync(cancellationToken);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new EcdsaAuditRelayAuthorizationVerifier(signer.ExportSubjectPublicKeyInfo());
        using var trustState = await database.CreateTrustStateAsync(
            verifier.PublicKeySha256,
            cancellationToken);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = database.CreateService(
            verifier,
            trustState.Reader,
            async (stage, token) =>
            {
                if (stage == AuditAppendStage.EventInserted)
                {
                    entered.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            });
        var receipt = Receipt(60, SoulA, DeviceA, AccountA, "idem-cancel");
        var envelope = SignEnvelope(signer, UnsignedEnvelope(receipt));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var append = failing.AppendReceiptAsync(receipt, envelope, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await append);
        var recovered = database.CreateService(verifier, trustState.Reader);
        Assert.Equal(0, await recovered.CountEventsAsync(cancellationToken));
        Assert.Equal(
            AuditAppendDisposition.Inserted,
            (await recovered.AppendReceiptAsync(receipt, envelope, cancellationToken)).Disposition);
        Assert.Equal(1, await recovered.CountEventsAsync(cancellationToken));
    }

    private static async Task AssertSqlStateAsync(
        NpgsqlConnection connection,
        string sql,
        string expectedSqlState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal(expectedSqlState, exception.SqlState);
    }

    private static CommandReceiptV1 Receipt(
        int value,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string idempotencyKey)
        => new(
            CommandReceiptV1.CurrentSchemaVersion,
            CommandReceiptV1.CurrentContractId,
            CommandReceiptV1.CurrentProducerModule,
            GuidFrom(value),
            GuidFrom(value + 1000),
            GuidFrom(value + 2000),
            1,
            soulId,
            deviceBindingId,
            platformAccountId,
            "trace_" + value.ToString("x32"),
            "idem_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))),
            Now,
            "internal",
            CommandReceiptV1.Success,
            GuidFrom(value + 3000),
            true,
            true,
            new string('a', 64),
            false,
            "VERIFIED");

    private static Guid GuidFrom(int value)
        => new(value, 0, 0, new byte[8]);

    private static AuditRelayEnvelope UnsignedEnvelope(CommandReceiptV1 receipt)
        => new(
            "command-orchestrator",
            "audit:command-receipt",
            receipt.ReceiptId,
            AuditRelayAuthorizationBinding.ComputeReceiptSha256(receipt),
            Now.AddHours(1),
            new string('d', 64),
            string.Empty);

    private static AuditRelayEnvelope SignEnvelope(ECDsa signer, AuditRelayEnvelope unsigned)
    {
        var canonical = EcdsaAuditRelayAuthorizationVerifier.CanonicalBytes(unsigned);
        try
        {
            return unsigned with
            {
                SignatureBase64 = Convert.ToBase64String(
                    signer.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static AuditRelayEnvelope CorruptSignature(AuditRelayEnvelope envelope)
    {
        var signature = Convert.FromBase64String(envelope.SignatureBase64);
        signature[0] ^= 0x01;
        try { return envelope with { SignatureBase64 = Convert.ToBase64String(signature) }; }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }
}

internal sealed class AuditMetricsTestDatabase : IAsyncDisposable
{
    private AuditMetricsTestDatabase(
        string adminConnectionString,
        string runtimeConnectionString,
        string schemaName,
        string runtimeRoleName)
    {
        AdminConnectionString = adminConnectionString;
        ConnectionString = runtimeConnectionString;
        SchemaName = schemaName;
        RuntimeRoleName = runtimeRoleName;
    }

    public string AdminConnectionString { get; }
    public string ConnectionString { get; }
    public string SchemaName { get; }
    public string RuntimeRoleName { get; }

    public static async Task<AuditMetricsTestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "DPS_TEST_POSTGRES is required. Audit PostgreSQL Integration must fail rather than skip when PostgreSQL is unavailable.");
        }

        var connectionSettings = new NpgsqlConnectionStringBuilder(connectionString);
        if (connectionSettings.Port == 55434
            || string.Equals(connectionSettings.Database, "dps_gbrain_company", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Audit Integration refuses the dedicated GBrain Company PostgreSQL service.");
        }

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            if (string.Equals(connection.Database, "dps_gbrain_company", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Audit Integration refuses the dedicated GBrain Company database.");
            }
            await using var versionCommand = new NpgsqlCommand("SHOW server_version_num", connection);
            var versionNumber = (string?)await versionCommand.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(versionNumber, "180004", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL 18.4 is required; server_version_num was '{versionNumber ?? "missing"}'.");
            }
        }

        var suffix = Guid.NewGuid().ToString("N");
        var schemaName = $"dps_f5_audit_{suffix}";
        var runtimeRoleName = $"dps_f5_audit_runtime_{suffix}";
        var passwordBytes = RandomNumberGenerator.GetBytes(32);
        string runtimePassword;
        try { runtimePassword = Convert.ToHexStringLower(passwordBytes); }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }

        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var createRole = new NpgsqlCommand(
                    $"CREATE ROLE {runtimeRoleName} LOGIN PASSWORD '{runtimePassword}' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS",
                    connection);
                await createRole.ExecuteNonQueryAsync(cancellationToken);
            }

            var migrator = new PostgresAuditMetricsMigrator(
                new AuditMetricsMigrationOptions(
                    connectionString,
                    schemaName,
                    runtimeRoleName));
            await migrator.InitializeAsync(cancellationToken);

            var runtimeSettings = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Username = runtimeRoleName,
                Password = runtimePassword,
                Pooling = false,
                Options = string.Empty
            };
            var database = new AuditMetricsTestDatabase(
                connectionString,
                runtimeSettings.ConnectionString,
                schemaName,
                runtimeRoleName);

            await using var runtimeConnection = new NpgsqlConnection(database.ConnectionString);
            await runtimeConnection.OpenAsync(cancellationToken);
            await using var identities = new NpgsqlCommand(
                "SELECT session_user::text, current_user::text",
                runtimeConnection);
            await using var identityReader = await identities.ExecuteReaderAsync(cancellationToken);
            if (!await identityReader.ReadAsync(cancellationToken)
                || !string.Equals(identityReader.GetString(0), runtimeRoleName, StringComparison.Ordinal)
                || !string.Equals(identityReader.GetString(1), runtimeRoleName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Audit Integration did not authenticate as its isolated runtime login role.");
            }

            return database;
        }
        catch
        {
            await CleanupAsync(connectionString, schemaName, runtimeRoleName);
            throw;
        }
    }

    public PostgresAuditMetrics CreateService(
        EcdsaAuditRelayAuthorizationVerifier verifier,
        SignedAuditRelayTrustStateReader trustStateReader,
        AuditAppendFaultInjector? faultInjector = null)
        => new(
            RuntimeOptions(),
            verifier,
            trustStateReader,
            new FixedAuditTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero)),
            faultInjector);

    public async Task<AuditRelayTrustStateFixture> CreateTrustStateAsync(
        string relayPublicKeySha256,
        CancellationToken cancellationToken)
        => await AuditRelayTrustStateFixture.CreateAsync(
            this,
            relayPublicKeySha256,
            cancellationToken);

    public AuditMetricsPostgresOptions RuntimeOptions()
        => new(ConnectionString, SchemaName, RuntimeRoleName);

    public async Task AppendTrustStateAsync(
        AuditRelayTrustStateEnvelope state,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await AppendTrustStateAsync(state, connection, transaction: null, cancellationToken);
    }

    public async Task<PendingTrustStateAppend> BeginTrustStateAppendAsync(
        AuditRelayTrustStateEnvelope state,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(AdminConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await AppendTrustStateAsync(state, connection, transaction, cancellationToken);
                return new PendingTrustStateAppend(connection, transaction);
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task AppendTrustStateAsync(
        AuditRelayTrustStateEnvelope state,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {SchemaName}.audit_relay_trust_states
                (revision, state_id, schema_version, contract_id,
                 active_release_bom_sha256, relay_key_id, relay_public_key_sha256,
                 relay_key_status, valid_from, valid_until, signature_base64)
            VALUES
                (@revision, @state_id, @schema_version, @contract_id,
                 @active_release_bom_sha256, @relay_key_id, @relay_public_key_sha256,
                 @relay_key_status, @valid_from, @valid_until, @signature_base64)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", state.Revision);
        command.Parameters.AddWithValue("state_id", state.StateId);
        command.Parameters.AddWithValue("schema_version", state.SchemaVersion);
        command.Parameters.AddWithValue("contract_id", state.ContractId);
        command.Parameters.AddWithValue("active_release_bom_sha256", state.ActiveReleaseBomSha256);
        command.Parameters.AddWithValue("relay_key_id", state.RelayKeyId);
        command.Parameters.AddWithValue("relay_public_key_sha256", state.RelayPublicKeySha256);
        command.Parameters.AddWithValue("relay_key_status", state.RelayKeyStatus);
        command.Parameters.AddWithValue("valid_from", state.ValidFrom);
        command.Parameters.AddWithValue("valid_until", state.ValidUntil);
        command.Parameters.AddWithValue("signature_base64", state.SignatureBase64);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync(
            AdminConnectionString,
            SchemaName,
            RuntimeRoleName);
    }

    private static async Task CleanupAsync(
        string adminConnectionString,
        string schemaName,
        string runtimeRoleName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var dropSchema = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS {schemaName} CASCADE",
            connection);
        await dropSchema.ExecuteNonQueryAsync(CancellationToken.None);
        await using var dropRole = new NpgsqlCommand(
            $"DROP ROLE IF EXISTS {runtimeRoleName}",
            connection);
        await dropRole.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

internal sealed class AuditRelayTrustStateFixture : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly AuditMetricsTestDatabase _database;
    private readonly ECDsa _rootSigner;

    private AuditRelayTrustStateFixture(
        AuditMetricsTestDatabase database,
        ECDsa rootSigner,
        SignedAuditRelayTrustStateReader reader)
    {
        _database = database;
        _rootSigner = rootSigner;
        Reader = reader;
    }

    public SignedAuditRelayTrustStateReader Reader { get; }

    public static async Task<AuditRelayTrustStateFixture> CreateAsync(
        AuditMetricsTestDatabase database,
        string relayPublicKeySha256,
        CancellationToken cancellationToken)
    {
        var rootSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootPublicKey = rootSigner.ExportSubjectPublicKeyInfo();
        try
        {
            var source = new PostgresAuditRelayTrustStateSource(database.RuntimeOptions());
            var reader = new SignedAuditRelayTrustStateReader(rootPublicKey, source);
            var fixture = new AuditRelayTrustStateFixture(database, rootSigner, reader);
            try
            {
                await fixture.AppendAsync(
                    1,
                    relayPublicKeySha256,
                    AuditRelayTrustStateEnvelope.Active,
                    cancellationToken: cancellationToken);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }
        catch
        {
            rootSigner.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootPublicKey);
        }
    }

    public async Task AppendAsync(
        long revision,
        string relayPublicKeySha256,
        string relayKeyStatus,
        string? activeReleaseBomSha256 = null,
        CancellationToken cancellationToken = default)
        => await _database.AppendTrustStateAsync(
            CreateSignedState(
                revision,
                relayPublicKeySha256,
                relayKeyStatus,
                activeReleaseBomSha256),
            cancellationToken);

    public async Task<PendingTrustStateAppend> BeginAppendAsync(
        long revision,
        string relayPublicKeySha256,
        string relayKeyStatus,
        string? activeReleaseBomSha256 = null,
        CancellationToken cancellationToken = default)
        => await _database.BeginTrustStateAppendAsync(
            CreateSignedState(
                revision,
                relayPublicKeySha256,
                relayKeyStatus,
                activeReleaseBomSha256),
            cancellationToken);

    private AuditRelayTrustStateEnvelope CreateSignedState(
        long revision,
        string relayPublicKeySha256,
        string relayKeyStatus,
        string? activeReleaseBomSha256)
    {
        var unsigned = new AuditRelayTrustStateEnvelope(
            AuditRelayTrustStateEnvelope.CurrentSchemaVersion,
            AuditRelayTrustStateEnvelope.CurrentContractId,
            Guid.NewGuid(),
            revision,
            activeReleaseBomSha256 ?? new string('d', 64),
            $"relay_key_{revision}",
            relayPublicKeySha256,
            relayKeyStatus,
            Now.AddHours(-1),
            Now.AddHours(2),
            string.Empty);
        var canonical = SignedAuditRelayTrustStateReader.CanonicalBytes(unsigned);
        var signature = _rootSigner.SignData(
            canonical,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        try
        {
            return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public void Dispose()
    {
        Reader.Dispose();
        _rootSigner.Dispose();
    }
}

internal sealed class PendingTrustStateAppend(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction) : IAsyncDisposable
{
    private bool _completed;

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}

internal sealed class FixedAuditTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
