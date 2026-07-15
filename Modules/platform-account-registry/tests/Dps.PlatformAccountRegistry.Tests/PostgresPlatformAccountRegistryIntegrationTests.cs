using Dps.PlatformAccountRegistry.Contracts;
using Npgsql;
using Xunit;

namespace Dps.PlatformAccountRegistry.Tests;

public sealed class PostgresPlatformAccountRegistryIntegrationTests
{
    private const string SoulA = PlatformAuthorizationEvidenceTestFactory.SoulA;
    private const string SoulB = PlatformAuthorizationEvidenceTestFactory.SoulB;
    private const string BindingA = PlatformAuthorizationEvidenceTestFactory.BindingA;
    private const string BindingB = PlatformAuthorizationEvidenceTestFactory.BindingB;
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 14, 3, 0, 0, TimeSpan.Zero);

    [Fact, Trait("Category", "Integration")]
    public async Task AuthorizationLifecyclePersistsRevisionReceiptAndOutboxAtomically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        Assert.Equal(180004, await database.ServerVersionNumberAsync(cancellationToken));
        var registry = database.CreateRegistry();
        var authorize = database.Authority.Authorize(SoulA, BindingA, 'a', "authorize", "authorize", BaseTime);

        var first = await registry.AuthorizeAsync(authorize, cancellationToken);
        Assert.Equal(first, await registry.AuthorizeAsync(authorize, cancellationToken));
        await Assert.ThrowsAsync<PlatformAccountIdempotencyConflictException>(() =>
            registry.AuthorizeAsync(authorize with { AliasDigest = new string('b', 64) }, cancellationToken));

        var suspended = await registry.ChangeStatusAsync(
            database.Authority.Status(first, 1, "suspended", "suspend", "suspend", BaseTime.AddMinutes(1)),
            cancellationToken);
        var authorized = await registry.ChangeStatusAsync(
            database.Authority.Status(suspended, 2, "authorized", "resume", "resume", BaseTime.AddMinutes(2)),
            cancellationToken);
        var revoke = database.Authority.Status(authorized, 3, "revoked", "revoke", "revoke", BaseTime.AddMinutes(3));
        var revoked = await registry.ChangeStatusAsync(revoke, cancellationToken);

        Assert.Equal(revoked, await registry.ChangeStatusAsync(revoke, cancellationToken));
        Assert.Equal(4, revoked.AuthorizationRevision);
        Assert.False(await registry.IsAuthorizedAsync(revoked.PlatformAccountId, SoulA, BindingA, cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ChangeStatusAsync(
            database.Authority.Status(revoked, 4, "authorized", "forbidden", "forbidden", BaseTime.AddMinutes(4)),
            cancellationToken));
        await Assert.ThrowsAsync<PlatformAccountRevisionConflictException>(() => registry.ChangeStatusAsync(
            database.Authority.Status(authorized, 2, "suspended", "stale", "stale", BaseTime.AddMinutes(5)),
            cancellationToken));

        Assert.Equal(1, await database.CountAsync("accounts", cancellationToken));
        Assert.Equal(4, await database.CountAsync("authorization_revisions", cancellationToken));
        Assert.Equal(4, await database.CountAsync("mutation_receipts", cancellationToken));
        Assert.Equal(4, await database.CountAsync("outbox", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentAliasRegistrationIsUniqueAndCrossScopeReadsFailClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        var firstTask = TryAuthorizeAsync(
            database.CreateRegistry(),
            database.Authority.Authorize(SoulA, BindingA, 'c', "one", "one", BaseTime),
            cancellationToken);
        var secondTask = TryAuthorizeAsync(
            database.CreateRegistry(),
            database.Authority.Authorize(SoulB, BindingB, 'c', "two", "two", BaseTime),
            cancellationToken);
        var attempts = await Task.WhenAll(firstTask, secondTask);

        var success = Assert.Single(attempts, static attempt => attempt.Result is not null);
        Assert.Single(attempts, static attempt => attempt.Error is PlatformAccountAliasConflictException);
        Assert.Equal(1, await database.CountAsync("accounts", cancellationToken));
        Assert.Equal(1, await database.CountAsync("authorization_revisions", cancellationToken));
        Assert.Equal(1, await database.CountAsync("mutation_receipts", cancellationToken));
        Assert.Equal(1, await database.CountAsync("outbox", cancellationToken));

        var winner = success.Result!;
        var wrongSoul = winner.SoulId == SoulA ? SoulB : SoulA;
        var wrongBinding = winner.DeviceBindingId == BindingA ? BindingB : BindingA;
        var restarted = database.CreateRegistry();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            restarted.GetAsync(winner.PlatformAccountId, wrongSoul, winner.DeviceBindingId, cancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            restarted.GetAsync(winner.PlatformAccountId, winner.SoulId, wrongBinding, cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentIdenticalDeliveryCreatesOneAccountOneReceiptAndOneOutbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        var command = database.Authority.Authorize(SoulA, BindingA, 'd', "concurrent", "concurrent", BaseTime);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => database.CreateRegistry().AuthorizeAsync(command, cancellationToken));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(1, await database.CountAsync("accounts", cancellationToken));
        Assert.Equal(1, await database.CountAsync("authorization_revisions", cancellationToken));
        Assert.Equal(1, await database.CountAsync("mutation_receipts", cancellationToken));
        Assert.Equal(1, await database.CountAsync("outbox", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ProviderOwnedReservationFreezesExactAuthorizationAcrossRestartUntilRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var account = await registry.AuthorizeAsync(
            database.Authority.Authorize(SoulA, BindingA, '9', "reservation", "reservation", BaseTime),
            cancellationToken);
        var reservationId = "bres_" + new string('3', 64);
        await registry.ReserveBindingAsync(new ReservePlatformAccountBindingCommand(
            SoulA, BindingA, account.PlatformAccountId, account.AuthorizationRevision,
            reservationId, PlatformAuthorizationEvidenceTestFactory.Trace("account-reserve"), BaseTime.AddMinutes(1)), cancellationToken);
        var reservation = new PlatformAccountBindingReservationCommand(
            SoulA, BindingA, account.PlatformAccountId, account.AuthorizationRevision,
            reservationId, PlatformAuthorizationEvidenceTestFactory.Trace("account-confirm"), BaseTime.AddMinutes(1));
        Assert.Equal("active", (await database.CreateRegistry().ConfirmBindingAsync(
            reservation, cancellationToken)).State);
        await Assert.ThrowsAsync<PlatformAccountBindingReservationConflictException>(() =>
            database.CreateRegistry().ChangeStatusAsync(database.Authority.Status(
                account, account.AuthorizationRevision, "revoked", "blocked",
                "account-blocked", BaseTime.AddMinutes(2)), cancellationToken));

        Assert.Equal("released", (await database.CreateRegistry().ReleaseBindingAsync(
            reservation with { TraceId = PlatformAuthorizationEvidenceTestFactory.Trace("account-release"), OccurredAt = BaseTime.AddMinutes(3) },
            cancellationToken)).State);
        Assert.Equal("revoked", (await database.CreateRegistry().ChangeStatusAsync(database.Authority.Status(
            account, account.AuthorizationRevision, "revoked", "after_release",
            "account-after-release", BaseTime.AddMinutes(4)), cancellationToken)).Status);
    }

    [Theory]
    [InlineData(PlatformAccountMutationStage.AccountPersisted)]
    [InlineData(PlatformAccountMutationStage.RevisionPersisted)]
    [InlineData(PlatformAccountMutationStage.ReceiptPersisted)]
    [InlineData(PlatformAccountMutationStage.OutboxPersistedBeforeCommit)]
    [Trait("Category", "Integration")]
    public async Task CrashWindowRollsBackAllMutationRowsAndRestartRecovers(
        PlatformAccountMutationStage failureStage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        var command = database.Authority.Authorize(
            SoulA,
            BindingA,
            'e',
            "recovery",
            "recovery-" + failureStage,
            BaseTime);
        var failing = database.CreateRegistry((stage, _) =>
            stage == failureStage
                ? ValueTask.FromException(new InvalidOperationException("Injected transaction failure."))
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.AuthorizeAsync(command, cancellationToken));
        Assert.Equal(0, await database.CountAsync("accounts", cancellationToken));
        Assert.Equal(0, await database.CountAsync("authorization_revisions", cancellationToken));
        Assert.Equal(0, await database.CountAsync("mutation_receipts", cancellationToken));
        Assert.Equal(0, await database.CountAsync("outbox", cancellationToken));

        var restarted = database.CreateRegistry();
        await restarted.InitializeAsync(cancellationToken);
        var recovered = await restarted.AuthorizeAsync(command, cancellationToken);
        var afterSecondRestart = await database.CreateRegistry().AuthorizeAsync(command, cancellationToken);
        Assert.Equal(recovered, afterSecondRestart);
        Assert.Equal(1, await database.CountAsync("accounts", cancellationToken));
        Assert.Equal(1, await database.CountAsync("authorization_revisions", cancellationToken));
        Assert.Equal(1, await database.CountAsync("mutation_receipts", cancellationToken));
        Assert.Equal(1, await database.CountAsync("outbox", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task EvidenceThatExpiresInsideTheTransactionRollsBackEveryMutationRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        var original = database.Authority.Authorize(
            SoulA, BindingA, '7', "expires-in-flight", "expires-in-flight", BaseTime);
        var evidence = database.Authority.CreateEvidence(
            original.SoulId,
            original.DeviceBindingId,
            original.PlatformAccountId,
            original.TraceId,
            original.IdempotencyKey,
            original.OccurredAt,
            "approval_expires_in_flight",
            original.Platform,
            original.AliasDigest,
            original.AliasKeyId,
            original.AliasKeyEpoch,
            "authorized",
            1,
            BaseTime.AddMinutes(-1),
            BaseTime.AddMinutes(1));
        var command = original with { AuthorizationEvidence = evidence };
        var registry = database.CreateRegistry((stage, _) =>
        {
            if (stage == PlatformAccountMutationStage.OutboxPersistedBeforeCommit)
                database.Authority.Advance(TimeSpan.FromMinutes(2));
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<PlatformAuthorizationEvidenceException>(() =>
            registry.AuthorizeAsync(command, cancellationToken));
        Assert.Equal(0, await database.CountAsync("accounts", cancellationToken));
        Assert.Equal(0, await database.CountAsync("authorization_revisions", cancellationToken));
        Assert.Equal(0, await database.CountAsync("mutation_receipts", cancellationToken));
        Assert.Equal(0, await database.CountAsync("outbox", cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task AppliedMigrationDigestTamperingAndReleaseGenerationReplayFailClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);

        await database.ExecuteAsync(
            $"UPDATE {database.SchemaName}.module_schema_migrations SET content_sha256 = @value WHERE migration_id = @migration_id",
            cancellationToken,
            ("value", new string('0', 64)),
            ("migration_id", "001_create_platform_account_registry.sql"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.CreateRegistry().InitializeAsync(cancellationToken));

        await database.RestoreMigrationDigestAsync("001_create_platform_account_registry.sql", cancellationToken);
        var lowerGeneration = new PostgresPlatformAccountRegistry(database.Options with
        {
            ActiveReleaseGeneration = database.Options.ActiveReleaseGeneration - 1
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            lowerGeneration.InitializeAsync(cancellationToken));
        var equivocation = new PostgresPlatformAccountRegistry(database.Options with
        {
            ActiveReleaseBomSha256 = new string('d', 64)
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            equivocation.InitializeAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task EmptyPreEvidenceSchemaConvergesThroughTheLockedMigrationSequence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var empty = await PlatformAccountRegistryDatabase.CreateUninitializedAsync(cancellationToken);
        await empty.CreateLegacyPreEvidenceSchemaAsync(seedRow: false, cancellationToken);
        await empty.CreateRegistry().InitializeAsync(cancellationToken);
        Assert.Contains("authorization_evidence_sha256", await empty.ColumnNamesAsync(cancellationToken));
        Assert.Equal(6, await empty.CountMigrationRowsAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task NonEmptyPreEvidenceSchemaRefusesFabricatedAuthorizationEvidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var nonEmpty = await PlatformAccountRegistryDatabase.CreateUninitializedAsync(cancellationToken);
        await nonEmpty.CreateLegacyPreEvidenceSchemaAsync(seedRow: true, cancellationToken);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            nonEmpty.CreateRegistry().InitializeAsync(cancellationToken));
        Assert.Contains("externally signed authorization evidence", error.MessageText, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DatabasePlatformConstraintMatchesTheSixtyFourCharacterContractLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        var definition = await database.ReadConstraintDefinitionAsync(
            "accounts", "dps_accounts_platform_v1", cancellationToken);

        Assert.Contains("length(platform) >= 1", definition, StringComparison.Ordinal);
        Assert.Contains("length(platform) <= 64", definition, StringComparison.Ordinal);
        Assert.Contains("[a-z0-9]", definition, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PersistentSchemaContainsOnlyAliasDigestAndNoRawIdentityOrSecretFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PlatformAccountRegistryDatabase.CreateAsync(cancellationToken);
        var digest = new string('f', 64);
        var registry = database.CreateRegistry();
        var result = await registry.AuthorizeAsync(
            database.Authority.Authorize(SoulA, BindingA, 'f', "privacy", "privacy", BaseTime),
            cancellationToken);

        var columns = await database.ColumnNamesAsync(cancellationToken);
        var normalizedColumns = string.Join('|', columns).ToLowerInvariant();
        Assert.Contains("alias_digest", columns);
        Assert.DoesNotContain("raw_alias", normalizedColumns, StringComparison.Ordinal);
        Assert.DoesNotContain("email", normalizedColumns, StringComparison.Ordinal);
        Assert.DoesNotContain("phone", normalizedColumns, StringComparison.Ordinal);
        Assert.DoesNotContain("password", normalizedColumns, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", normalizedColumns, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", normalizedColumns, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", normalizedColumns, StringComparison.Ordinal);
        Assert.Equal(digest, await database.ReadAliasDigestAsync(result.PlatformAccountId, cancellationToken));

        var storedDocuments = await database.StoredDocumentsAsync(cancellationToken);
        Assert.All(storedDocuments, document =>
        {
            Assert.DoesNotContain('@', document);
            Assert.DoesNotContain("+1555", document, StringComparison.Ordinal);
            Assert.DoesNotContain("deepseek", document, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact, Trait("Category", "Integration")]
    public void MissingDatabaseConfigurationFailsRatherThanSkipping()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PlatformAccountRegistryDatabase.RequireConnectionString("DPS_TEST_POSTGRES_INTENTIONALLY_MISSING"));
        Assert.Throws<InvalidOperationException>(() => PlatformAccountRegistryDatabase.ValidateTestDatabaseTarget(
            "Host=127.0.0.1;Port=55434;Database=platform_account_test;Username=test;Password=test"));
        Assert.Throws<InvalidOperationException>(() => PlatformAccountRegistryDatabase.ValidateTestDatabaseTarget(
            "Host=127.0.0.1;Port=5432;Database=dps_gbrain_company;Username=test;Password=test"));
    }

    private static async Task<AuthorizationAttempt> TryAuthorizeAsync(
        PostgresPlatformAccountRegistry registry,
        AuthorizePlatformAccountCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return new AuthorizationAttempt(await registry.AuthorizeAsync(command, cancellationToken), null);
        }
        catch (Exception error)
        {
            return new AuthorizationAttempt(null, error);
        }
    }

    private sealed record AuthorizationAttempt(PlatformAccountAuthorizedV1? Result, Exception? Error);

    private sealed class PlatformAccountRegistryDatabase : IAsyncDisposable
    {
        private static readonly HashSet<string> CountableTables =
            ["accounts", "authorization_revisions", "mutation_receipts", "outbox"];
        private readonly string _connectionString;

        private PlatformAccountRegistryDatabase(string connectionString)
        {
            _connectionString = connectionString;
            SchemaName = "dps_par_" + Guid.NewGuid().ToString("N");
            Options = new PlatformAccountRegistryOptions(
                connectionString,
                SchemaName,
                PlatformAuthorizationEvidenceTestFactory.ReleaseBomSha256,
                PlatformAuthorizationEvidenceTestFactory.ReleaseGeneration);
            Authority = new PlatformAuthorizationEvidenceTestFactory(BaseTime);
        }

        public string SchemaName { get; }
        public PlatformAccountRegistryOptions Options { get; }
        public PlatformAuthorizationEvidenceTestFactory Authority { get; }

        public static async Task<PlatformAccountRegistryDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var database = await CreateUninitializedAsync(cancellationToken);
            try
            {
                await database.CreateRegistry().InitializeAsync(cancellationToken);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public static async Task<PlatformAccountRegistryDatabase> CreateUninitializedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connectionString = RequireConnectionString("DPS_TEST_POSTGRES");
            await VerifyServerVersionAsync(connectionString, cancellationToken);
            return new PlatformAccountRegistryDatabase(connectionString);
        }

        public static string RequireConnectionString(string variableName)
        {
            var connectionString = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    $"{variableName} is required. The real PostgreSQL Integration suite fails rather than skips when it is unavailable.");
            ValidateTestDatabaseTarget(connectionString);
            return connectionString;
        }

        internal static void ValidateTestDatabaseTarget(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (builder.Port == 55434 ||
                string.Equals(builder.Database, "dps_gbrain_company", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES must never use the persistent GBrain Company PostgreSQL port or database.");
            }
        }

        private static async Task VerifyServerVersionAsync(
            string connectionString,
            CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
            var versionNumber = (string?)await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(versionNumber, "180004", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL 18.4 is required; server_version_num was '{versionNumber ?? "missing"}'.");
            }
        }

        public PostgresPlatformAccountRegistry CreateRegistry(
            PlatformAccountRegistryFaultInjector? faultInjector = null) =>
            new(Options, Authority.Verifier, faultInjector);

        public async Task<int> ServerVersionNumberAsync(CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
            return int.Parse((string)(await command.ExecuteScalarAsync(cancellationToken))!, System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<long> CountAsync(string table, CancellationToken cancellationToken)
        {
            if (!CountableTables.Contains(table)) throw new ArgumentOutOfRangeException(nameof(table));
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {SchemaName}.{table}", connection);
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async Task<long> CountMigrationRowsAsync(CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM {SchemaName}.module_schema_migrations", connection);
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async Task ExecuteAsync(
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object Value)[] parameters)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task RestoreMigrationDigestAsync(
            string migrationId,
            CancellationToken cancellationToken)
        {
            var resourceName = typeof(PostgresPlatformAccountRegistry).Assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith("." + migrationId, StringComparison.Ordinal));
            await using var stream = typeof(PostgresPlatformAccountRegistry).Assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content)));
            await ExecuteAsync(
                $"UPDATE {SchemaName}.module_schema_migrations SET content_sha256 = @value WHERE migration_id = @migration_id",
                cancellationToken,
                ("value", digest),
                ("migration_id", migrationId));
        }

        public async Task<string> ReadConstraintDefinitionAsync(
            string tableName,
            string constraintName,
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SELECT pg_get_constraintdef(constraint_oid)
                FROM information_schema.table_constraints
                WHERE table_schema = @schema_name
                  AND table_name = @table_name
                  AND constraint_name = @constraint_name
                """,
                connection);
            command.Parameters.AddWithValue("schema_name", SchemaName);
            command.Parameters.AddWithValue("table_name", tableName);
            command.Parameters.AddWithValue("constraint_name", constraintName);
            return (string)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("The expected database constraint was not found."));
        }

        public async Task CreateLegacyPreEvidenceSchemaAsync(
            bool seedRow,
            CancellationToken cancellationToken)
        {
            var sql = $"""
                CREATE SCHEMA {SchemaName};
                CREATE TABLE {SchemaName}.accounts (
                    platform_account_id text PRIMARY KEY,
                    soul_id text NOT NULL,
                    device_binding_id text NOT NULL,
                    platform text NOT NULL,
                    alias_digest text NOT NULL,
                    alias_key_id text NOT NULL,
                    authorization_evidence_id text NOT NULL,
                    authorization_revision bigint NOT NULL,
                    status text NOT NULL,
                    trace_id text NOT NULL,
                    idempotency_key text NOT NULL,
                    occurred_at timestamptz NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    UNIQUE (platform_account_id, soul_id, device_binding_id)
                );
                CREATE TABLE {SchemaName}.authorization_revisions (
                    platform_account_id text NOT NULL,
                    authorization_revision bigint NOT NULL,
                    soul_id text NOT NULL,
                    device_binding_id text NOT NULL,
                    status text NOT NULL,
                    authorization_evidence_id text NOT NULL,
                    trace_id text NOT NULL,
                    idempotency_key text NOT NULL UNIQUE,
                    occurred_at timestamptz NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    PRIMARY KEY (platform_account_id, authorization_revision)
                );
                """;
            if (seedRow)
            {
                sql += $"""
                    INSERT INTO {SchemaName}.accounts (
                        platform_account_id, soul_id, device_binding_id, platform, alias_digest,
                        alias_key_id, authorization_evidence_id, authorization_revision, status,
                        trace_id, idempotency_key, occurred_at)
                    VALUES (
                        'pa_{new string('1', 32)}', '{SoulA}', '{BindingA}', 'fixture', '{new string('1', 64)}',
                        'legacy-key', 'approval_legacy', 1, 'authorized',
                        'trace_{new string('1', 32)}', 'idem_{new string('1', 64)}', '2026-07-14T03:00:00Z');
                    INSERT INTO {SchemaName}.authorization_revisions (
                        platform_account_id, authorization_revision, soul_id, device_binding_id,
                        status, authorization_evidence_id, trace_id, idempotency_key, occurred_at)
                    VALUES (
                        'pa_{new string('1', 32)}', 1, '{SoulA}', '{BindingA}', 'authorized',
                        'approval_legacy', 'trace_{new string('1', 32)}', 'idem_{new string('1', 64)}',
                        '2026-07-14T03:00:00Z');
                    """;
            }
            await ExecuteAsync(sql, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ColumnNamesAsync(CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = @schema_name
                ORDER BY table_name, ordinal_position
                """,
                connection);
            command.Parameters.AddWithValue("schema_name", SchemaName);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(0));
            return columns;
        }

        public async Task<string> ReadAliasDigestAsync(
            string platformAccountId,
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"SELECT alias_digest FROM {SchemaName}.accounts WHERE platform_account_id = @platform_account_id",
                connection);
            command.Parameters.AddWithValue("platform_account_id", platformAccountId);
            return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async Task<IReadOnlyList<string>> StoredDocumentsAsync(CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"""
                SELECT result_json FROM {SchemaName}.mutation_receipts
                UNION ALL
                SELECT payload_json FROM {SchemaName}.outbox
                """,
                connection);
            var documents = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) documents.Add(reader.GetString(0));
            return documents;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = await OpenAsync(CancellationToken.None);
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {SchemaName} CASCADE", connection);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original test failure. Every schema is random and contains synthetic data only.
            }
            finally
            {
                Authority.Dispose();
            }
        }

        private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
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
