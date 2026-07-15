using Dps.Binding.Contracts;
using Dps.DeviceRegistry;
using Dps.DeviceRegistry.Contracts;
using Dps.PlatformAccountRegistry;
using Dps.PlatformAccountRegistry.Contracts;
using Dps.PlatformAuthorizationAuthority.Contracts;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Dps.Binding.Tests;

public sealed class PostgresBindingRegistryIntegrationTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSoul = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BindingId = "db_11111111111111111111111111111111";
    private static DateTimeOffset OccurredAt => TimeProvider.System.GetUtcNow();
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task RealProviderReadersCreateBindingWithExactCurrentRevisions()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var account = await database.AccountProvider.ChangeStatusAsync(database.Authorization.Status(
            seed.Account,
            "authorized",
            "binding_refresh",
            "account-refresh",
            OccurredAt.AddMinutes(2)), TestCancellation);
        var device = await database.DeviceProvider.UpdateCapabilitiesAsync(new UpdateDeviceCapabilitiesCommand(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            seed.Device.DeviceId,
            seed.Device.CapabilityRevision,
            ["observe", "verify", "wait"],
            Trace("device-refresh"),
            Idempotency("device-refresh"),
            OccurredAt.AddMinutes(2)), TestCancellation);
        seed = seed with { Account = account, Device = device };

        var result = await database.Registry.BindAsync(seed.Command("bind-real-providers"), TestCancellation);

        Assert.Equal(2, result.DeviceRegistrationRevision);
        Assert.Equal(2, result.AccountAuthorizationRevision);
        Assert.Equal(seed.Device.CapabilityRevision, result.DeviceRegistrationRevision);
        Assert.Equal(seed.Account.AuthorizationRevision, result.AccountAuthorizationRevision);
        Assert.Equal(seed.Device.DeviceId, result.DeviceId);
        Assert.Equal("active", result.Status);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameKeySameHashIsNoOpWithOneBindingRevisionReceiptAndOutbox()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var command = seed.Command("bind-idempotent");

        var first = await database.Registry.BindAsync(command, TestCancellation);
        var duplicate = await database.Registry.BindAsync(command, TestCancellation);

        Assert.Equal(first, duplicate);
        Assert.Equal(1, await database.Registry.CountBindingsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameKeyDifferentHashIsQuarantinedWithoutAnotherMutation()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var command = seed.Command("bind-conflict");
        await database.Registry.BindAsync(command, TestCancellation);

        await Assert.ThrowsAsync<BindingIdempotencyConflictException>(() =>
            database.Registry.BindAsync(command with { TraceId = Trace("conflicting-request") }, TestCancellation));

        Assert.Equal(1, await database.Registry.CountBindingsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountOutboxAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountQuarantineAsync(TestCancellation));
        var quarantine = Assert.Single(await database.Registry.ReadQuarantineAsync(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            TestCancellation));
        Assert.Matches("^[a-f0-9]{64}$", quarantine.IdempotencyKeySha256);
        Assert.DoesNotContain("bind-conflict", quarantine.IdempotencyKeySha256, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentIdenticalDeliveryCreatesOneAtomicMutation()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var command = seed.Command("bind-concurrent-identical");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => database.Registry.BindAsync(command, TestCancellation)));

        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(1, await database.Registry.CountBindingsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentDifferentKeysCannotCreateTwoActiveBindingsForOneScope()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                await database.Registry.BindAsync(seed.Command("bind-competing-" + index), TestCancellation);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        Assert.Single(outcomes, static succeeded => succeeded);
        Assert.Equal(1, await database.Registry.CountBindingsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Registry.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RevocationAppendsHistoryAndRevokedBindingCannotBeResurrected()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var active = await database.Registry.BindAsync(seed.Command("bind-before-revoke"), TestCancellation);
        var revoke = new RevokeBindingCommand(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            active.BindingRevision,
            Trace("revoke"),
            Idempotency("binding-revoke"),
            OccurredAt.AddMinutes(4));

        var revoked = await database.Registry.RevokeAsync(revoke, TestCancellation);
        Assert.Equal(revoked, await database.Registry.RevokeAsync(revoke, TestCancellation));
        Assert.Equal("revoked", revoked.Status);
        Assert.Equal(2, await database.Registry.CountRevisionsAsync(TestCancellation));
        Assert.Equal(2, await database.Registry.CountReceiptsAsync(TestCancellation));
        Assert.Equal(2, await database.Registry.CountOutboxAsync(TestCancellation));
        await Assert.ThrowsAsync<BindingHistoricalReceiptException>(() =>
            database.Registry.BindAsync(seed.Command("bind-before-revoke"), TestCancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Registry.BindAsync(seed.Command("bind-after-revoke") with { OccurredAt = OccurredAt.AddMinutes(5) }, TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RetiredDeviceProviderTruthFailsClosed()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        await database.DeviceProvider.RetireAsync(new RetireDeviceCommand(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            seed.Device.DeviceId,
            seed.Device.CapabilityRevision,
            Trace("retire"),
            Idempotency("device-retire-before-bind"),
            OccurredAt.AddMinutes(3)), TestCancellation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Registry.BindAsync(seed.Command("bind-retired-device"), TestCancellation));
        await database.AssertNoBindingMutationAsync();
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("suspended")]
    [InlineData("revoked")]
    public async Task InactiveAccountProviderTruthFailsClosed(string status)
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        await database.AccountProvider.ChangeStatusAsync(database.Authorization.Status(
            seed.Account,
            status,
            "binding_status",
            "account-status-before-bind",
            OccurredAt.AddMinutes(3)), TestCancellation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Registry.BindAsync(seed.Command("bind-inactive-account"), TestCancellation));
        await database.AssertNoBindingMutationAsync();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CrossSoulAndWrongAccountQueriesNeverCreateOrRevealBinding()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Registry.BindAsync(seed.Command("bind-cross-soul") with { SoulId = OtherSoul }, TestCancellation));
        await database.AssertNoBindingMutationAsync();

        await database.Registry.BindAsync(seed.Command("bind-correct-scope"), TestCancellation);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Registry.GetAsync(OtherSoul, BindingId, seed.Account.PlatformAccountId, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Registry.GetAsync(Soul, BindingId, "pa_not valid", TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RestartReadsCommittedTruthWithoutProviderTableAccess()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var expected = await database.Registry.BindAsync(seed.Command("bind-restart"), TestCancellation);

        var restarted = database.CreateRegistry();
        await restarted.InitializeAsync(TestCancellation);
        var actual = await restarted.GetAsync(Soul, BindingId, seed.Account.PlatformAccountId, TestCancellation);

        Assert.Equal(expected, actual);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(BindingMutationStage.PendingAttemptWritten)]
    [InlineData(BindingMutationStage.ProvidersReserved)]
    [InlineData(BindingMutationStage.ProvidersConfirmed)]
    [InlineData(BindingMutationStage.BindingWritten)]
    [InlineData(BindingMutationStage.RevisionWritten)]
    [InlineData(BindingMutationStage.IdempotencyReceiptWritten)]
    [InlineData(BindingMutationStage.OutboxWritten)]
    [InlineData(BindingMutationStage.BeforeCommit)]
    public async Task CrashWindowRollsBackEveryMutationAndCleanRestartRecovers(BindingMutationStage crashStage)
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var crashing = database.CreateRegistry((stage, _) =>
            stage == crashStage
                ? ValueTask.FromException(new SimulatedBindingCrashException())
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<SimulatedBindingCrashException>(() =>
            crashing.BindAsync(seed.Command("bind-crash-window"), TestCancellation));
        await database.AssertNoBindingMutationAsync();

        var restarted = database.CreateRegistry();
        var recovered = await restarted.BindAsync(seed.Command("bind-crash-window"), TestCancellation);
        Assert.Equal("active", recovered.Status);
        Assert.Equal(1, await restarted.CountBindingsAsync(TestCancellation));
        Assert.Equal(1, await restarted.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await restarted.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await restarted.CountOutboxAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ActiveBindingFreezesProviderRevisionsAndRevocationReleasesBothReservations()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var active = await database.Registry.BindAsync(seed.Command("bind-freezes-providers"), TestCancellation);

        await Assert.ThrowsAsync<DeviceBindingReservationConflictException>(() =>
            database.DeviceProvider.UpdateCapabilitiesAsync(new UpdateDeviceCapabilitiesCommand(
                Soul, BindingId, seed.Account.PlatformAccountId, seed.Device.DeviceId,
                seed.Device.CapabilityRevision, ["observe", "verify", "wait"], Trace("device-frozen"),
                Idempotency("device-frozen"), OccurredAt.AddMinutes(3)), TestCancellation));
        await Assert.ThrowsAsync<PlatformAccountBindingReservationConflictException>(() =>
            database.AccountProvider.ChangeStatusAsync(database.Authorization.Status(
                seed.Account,
                "suspended",
                "frozen",
                "account-frozen",
                OccurredAt.AddMinutes(3)), TestCancellation));

        await database.Registry.RevokeAsync(new RevokeBindingCommand(
            Soul, BindingId, seed.Account.PlatformAccountId, active.BindingRevision, Trace("release"),
            Idempotency("binding-release"), OccurredAt.AddMinutes(4)), TestCancellation);
        Assert.Equal(2, (await database.DeviceProvider.UpdateCapabilitiesAsync(new UpdateDeviceCapabilitiesCommand(
            Soul, BindingId, seed.Account.PlatformAccountId, seed.Device.DeviceId,
            seed.Device.CapabilityRevision, ["observe", "verify", "wait"], Trace("device-after-release"),
            Idempotency("device-after-release"), OccurredAt.AddMinutes(5)), TestCancellation)).CapabilityRevision);
        Assert.Equal("suspended", (await database.AccountProvider.ChangeStatusAsync(database.Authorization.Status(
            seed.Account,
            "suspended",
            "after_release",
            "account-after-release",
            OccurredAt.AddMinutes(5)), TestCancellation)).Status);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(BindingMutationStage.BindingWritten)]
    [InlineData(BindingMutationStage.RevisionWritten)]
    [InlineData(BindingMutationStage.IdempotencyReceiptWritten)]
    [InlineData(BindingMutationStage.OutboxWritten)]
    [InlineData(BindingMutationStage.BeforeCommit)]
    public async Task RevocationCrashWindowKeepsReservationAndRetryReleasesIt(BindingMutationStage crashStage)
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var active = await database.Registry.BindAsync(seed.Command("bind-before-revoke-crash"), TestCancellation);
        var revoke = new RevokeBindingCommand(
            Soul, BindingId, seed.Account.PlatformAccountId, active.BindingRevision, Trace("revoke-crash"),
            Idempotency("revoke-crash"), OccurredAt.AddMinutes(4));
        var crashing = database.CreateRegistry((stage, _) =>
            stage == crashStage
                ? ValueTask.FromException(new SimulatedBindingCrashException())
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<SimulatedBindingCrashException>(() => crashing.RevokeAsync(revoke, TestCancellation));
        Assert.Equal("active", (await database.Registry.GetAsync(
            Soul, BindingId, seed.Account.PlatformAccountId, TestCancellation)).Status);
        await Assert.ThrowsAsync<DeviceBindingReservationConflictException>(() =>
            database.DeviceProvider.RetireAsync(new RetireDeviceCommand(
                Soul, BindingId, seed.Account.PlatformAccountId, seed.Device.DeviceId,
                seed.Device.CapabilityRevision, Trace("retire-blocked"), Idempotency("retire-blocked"),
                OccurredAt.AddMinutes(5)), TestCancellation));

        Assert.Equal("revoked", (await database.Registry.RevokeAsync(revoke, TestCancellation)).Status);
        Assert.Equal("retired", (await database.DeviceProvider.RetireAsync(new RetireDeviceCommand(
            Soul, BindingId, seed.Account.PlatformAccountId, seed.Device.DeviceId,
            seed.Device.CapabilityRevision, Trace("retire-recovered"), Idempotency("retire-recovered"),
            OccurredAt.AddMinutes(6)), TestCancellation)).Status);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PartialProviderReservationFailureCompensatesTheOtherProvider()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var otherReservationId = "bres_" + new string('9', 64);
        var accountReservationClient = database.AccountProvider.CreateBindingReservationClient();
        await accountReservationClient.ReserveAsync(new ReservePlatformAccountBindingCommand(
            Soul, BindingId, seed.Account.PlatformAccountId, seed.Account.AuthorizationRevision,
            otherReservationId, Trace("other-reserve"), OccurredAt.AddMinutes(2)), TestCancellation);
        await accountReservationClient.ConfirmAsync(new PlatformAccountBindingReservationCommand(
            Soul, BindingId, seed.Account.PlatformAccountId, seed.Account.AuthorizationRevision,
            otherReservationId, Trace("other-confirm"), OccurredAt.AddMinutes(2)), TestCancellation);

        await Assert.ThrowsAsync<PlatformAccountBindingReservationConflictException>(() =>
            database.Registry.BindAsync(seed.Command("bind-partial-provider-failure"), TestCancellation));
        Assert.Equal(2, (await database.DeviceProvider.UpdateCapabilitiesAsync(new UpdateDeviceCapabilitiesCommand(
            Soul, BindingId, seed.Account.PlatformAccountId, seed.Device.DeviceId,
            seed.Device.CapabilityRevision, ["observe", "verify", "wait"], Trace("device-compensated"),
            Idempotency("device-compensated"), OccurredAt.AddMinutes(3)), TestCancellation)).CapabilityRevision);
        await database.AssertNoBindingMutationAsync();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task OutboxIsAtomicChecksummedAndExactlySoulScoped()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var result = await database.Registry.BindAsync(seed.Command("bind-outbox"), TestCancellation);

        var records = await database.Registry.ReadPendingOutboxAsync(Soul, BindingId, seed.Account.PlatformAccountId, TestCancellation);
        var record = Assert.Single(records);
        Assert.Equal(result, record.Payload);
        Assert.Matches("^[a-f0-9]{64}$", record.PayloadSha256);
        Assert.Null(record.DispatchedAt);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Registry.GetAsync(OtherSoul, BindingId, seed.Account.PlatformAccountId, TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task BindingSchemaStoresNoRawAliasesFingerprintsCredentialsOrContactFields()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var columns = await database.BindingColumnNamesAsync();
        var forbidden = new[] { "alias", "fingerprint", "email", "phone", "credential", "secret", "token", "password" };

        Assert.DoesNotContain(columns, column => forbidden.Any(term => column.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RevisionHistoryRejectsUpdateAndDelete()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        await database.Registry.BindAsync(seed.Command("bind-append-only"), TestCancellation);

        await Assert.ThrowsAsync<PostgresException>(() => database.MutateRevisionHistoryAsync("UPDATE"));
        await Assert.ThrowsAsync<PostgresException>(() => database.MutateRevisionHistoryAsync("DELETE"));
        Assert.Equal(1, await database.Registry.CountRevisionsAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PendingAttemptConflictIsQuarantinedBeforeAnyProviderRead()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var crashing = database.CreateRegistry((stage, _) =>
            stage == BindingMutationStage.PendingAttemptWritten
                ? ValueTask.FromException(new SimulatedBindingCrashException())
                : ValueTask.CompletedTask);
        var original = seed.Command("bind-pending-conflict");

        await Assert.ThrowsAsync<SimulatedBindingCrashException>(() =>
            crashing.BindAsync(original, TestCancellation));
        await Assert.ThrowsAsync<BindingIdempotencyConflictException>(() =>
            database.Registry.BindAsync(original with { SoulId = OtherSoul }, TestCancellation));

        var quarantine = Assert.Single(await database.Registry.ReadQuarantineAsync(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            TestCancellation));
        Assert.Equal("bind", quarantine.IncomingOperation);
        Assert.Matches("^[a-f0-9]{64}$", quarantine.IdempotencyKeySha256);
        await database.AssertNoBindingMutationAsync();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task InitializeAutonomouslyRecoversConfirmedPendingAttempt()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var crashing = database.CreateRegistry((stage, _) =>
            stage == BindingMutationStage.ProvidersConfirmed
                ? ValueTask.FromException(new SimulatedBindingCrashException())
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<SimulatedBindingCrashException>(() =>
            crashing.BindAsync(seed.Command("bind-startup-recovery"), TestCancellation));
        await database.AssertNoBindingMutationAsync();

        var restarted = database.CreateRegistry();
        await restarted.InitializeAsync(TestCancellation);
        var recovered = await restarted.GetAsync(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            TestCancellation);
        Assert.Equal("active", recovered.Status);
        Assert.Equal(1, await restarted.CountBindingsAsync(TestCancellation));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MutationFenceSerializesPersonaCommitWindowBeforeRevocation()
    {
        await using var database = await BindingDatabase.CreateAsync();
        var seed = await database.SeedProvidersAsync();
        var active = await database.Registry.BindAsync(seed.Command("bind-before-fence"), TestCancellation);
        var fenceClient = database.Registry.CreateMutationFenceClient();
        var fenceCommand = new AcquireBindingMutationFenceCommand(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            Trace("persona-fence"),
            Idempotency("persona-mutation-fence"),
            OccurredAt.AddMinutes(3));
        var firstLease = await fenceClient.AcquireAsync(fenceCommand, TestCancellation);
        await firstLease.DisposeAsync();
        var lease = await fenceClient.AcquireAsync(fenceCommand, TestCancellation);
        lease.Receipt.Validate();
        Assert.Equal(active.BindingRevision, lease.Receipt.BindingRevision);
        Assert.True(lease.Receipt.FenceSequence > firstLease.Receipt.FenceSequence);

        var revoke = database.Registry.RevokeAsync(new RevokeBindingCommand(
            Soul,
            BindingId,
            seed.Account.PlatformAccountId,
            active.BindingRevision,
            Trace("fence-revoke"),
            Idempotency("revoke-after-fence"),
            OccurredAt.AddMinutes(4)), TestCancellation);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestCancellation);
        Assert.False(revoke.IsCompleted);

        await lease.DisposeAsync();
        Assert.Equal("revoked", (await revoke).Status);
    }

    [Fact, Trait("Category", "Integration")]
    public void MissingDatabaseConfigurationFailsRatherThanSkipping()
    {
        var configured = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Throws<InvalidOperationException>(BindingDatabase.RequireConnectionString);
            return;
        }

        Assert.Equal(configured, BindingDatabase.RequireConnectionString());
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("device_binding_id")]
    [InlineData("platform_account_id")]
    [InlineData("trace_id")]
    [InlineData("idempotency_key")]
    [InlineData("device_binding_id_newline")]
    [InlineData("platform_account_id_newline")]
    [InlineData("trace_id_newline")]
    [InlineData("idempotency_key_newline")]
    public async Task DatabaseRejectsLegacyWideAndTrailingNewlineIdentifierShapes(string field)
    {
        await using var database = await BindingDatabase.CreateAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            database.InsertLegacyWidePendingAttemptAsync(field));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task AppliedMigrationDigestMismatchFailsClosedOnRestart()
    {
        await using var database = await BindingDatabase.CreateAsync();
        await database.ReplaceAppliedMigrationDigestAsync("001_create_binding.sql", new string('0', 64));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Registry.InitializeAsync(TestCancellation));

        Assert.Contains("no longer matches", exception.Message, StringComparison.Ordinal);
    }

    private sealed class SimulatedBindingCrashException : Exception
    {
    }

    private sealed record ProviderSeed(
        PlatformAccountAuthorizedV1 Account,
        DeviceRegisteredV1 Device)
    {
        public CreateBindingCommand Command(string idempotencyLabel) => new(
            Soul,
            BindingId,
            Account.PlatformAccountId,
            Device.DeviceId,
            Trace("binding"),
            Idempotency(idempotencyLabel),
            OccurredAt.AddMinutes(2));
    }

    private sealed class BindingDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _bindingSchema;
        private readonly string _deviceSchema;
        private readonly string _accountSchema;

        private BindingDatabase(
            string connectionString,
            string bindingSchema,
            string deviceSchema,
            string accountSchema,
            PostgresDeviceRegistry deviceProvider,
            PostgresPlatformAccountRegistry accountProvider,
            BindingPlatformAuthorizationEvidenceFactory authorization)
        {
            _connectionString = connectionString;
            _bindingSchema = bindingSchema;
            _deviceSchema = deviceSchema;
            _accountSchema = accountSchema;
            DeviceProvider = deviceProvider;
            AccountProvider = accountProvider;
            Authorization = authorization;
            Registry = CreateRegistry();
        }

        public PostgresDeviceRegistry DeviceProvider { get; }
        public PostgresPlatformAccountRegistry AccountProvider { get; }
        public BindingPlatformAuthorizationEvidenceFactory Authorization { get; }
        public PostgresBindingRegistry Registry { get; }

        public static async Task<BindingDatabase> CreateAsync()
        {
            var connectionString = RequireConnectionString();
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (builder.Port == 55434 ||
                string.Equals(builder.Database, "dps_gbrain_company", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES must never use the persistent GBrain Company PostgreSQL port or database.");
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
                var version = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
                if (version != 180004)
                    throw new InvalidOperationException($"Binding Integration requires exact PostgreSQL 18.4; server_version_num was {version}.");
            }

            var suffix = Guid.NewGuid().ToString("N")[..20];
            var bindingSchema = "binding_it_" + suffix;
            var deviceSchema = "device_it_" + suffix;
            var accountSchema = "account_it_" + suffix;
            var deviceProvider = new PostgresDeviceRegistry(new PostgresDeviceRegistryOptions(
                connectionString,
                deviceSchema,
                "fpkey_55555555555555555555555555555555",
                11,
                1));
            var accountProvider = new PostgresPlatformAccountRegistry(new PlatformAccountRegistryOptions(
                connectionString,
                accountSchema,
                BindingPlatformAuthorizationEvidenceFactory.ReleaseBomSha256,
                BindingPlatformAuthorizationEvidenceFactory.ReleaseGeneration));
            var authorization = BindingPlatformAuthorizationEvidenceFactory.LoadExternal();
            var database = new BindingDatabase(
                connectionString,
                bindingSchema,
                deviceSchema,
                accountSchema,
                deviceProvider,
                accountProvider,
                authorization);
            try
            {
                await deviceProvider.InitializeAsync();
                await accountProvider.InitializeAsync();
                await database.Registry.InitializeAsync();
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
                throw new InvalidOperationException("DPS_TEST_POSTGRES is required for binding Integration tests; missing infrastructure is not a skip.");
            return value;
        }

        public PostgresBindingRegistry CreateRegistry(BindingMutationFaultInjector? faultInjector = null)
            => PostgresBindingRegistry.CreateForTests(
                new PostgresBindingRegistryOptions(_connectionString, _bindingSchema),
                DeviceProvider.CreateBindingReservationClient(),
                AccountProvider.CreateBindingReservationClient(),
                faultInjector);

        public async Task<ProviderSeed> SeedProvidersAsync()
        {
            var account = await AccountProvider.AuthorizeAsync(Authorization.Authorize(
                Soul,
                BindingId,
                BindingPlatformAuthorizationEvidenceFactory.PlatformAccount("binding-integration"),
                "fixture",
                new string('2', 64),
                "binding-integration-key",
                1,
                "binding_integration",
                "account-authorize",
                OccurredAt));
            var device = await DeviceProvider.RegisterAsync(new RegisterDeviceCommand(
                Soul,
                BindingId,
                account.PlatformAccountId,
                new string('1', 64),
                "fpkey_55555555555555555555555555555555",
                11,
                ["observe", "verify"],
                Trace("device-register"),
                Idempotency("device-register"),
                OccurredAt.AddMinutes(1)));
            return new ProviderSeed(account, device);
        }

        private static string Trace(string label) => PostgresBindingRegistryIntegrationTests.Trace(label);

        private static string Idempotency(string label) => PostgresBindingRegistryIntegrationTests.Idempotency(label);

        public async Task AssertNoBindingMutationAsync()
        {
            Assert.Equal(0, await Registry.CountBindingsAsync());
            Assert.Equal(0, await Registry.CountRevisionsAsync());
            Assert.Equal(0, await Registry.CountReceiptsAsync());
            Assert.Equal(0, await Registry.CountOutboxAsync());
        }

        public async Task<IReadOnlyList<string>> BindingColumnNamesAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = @schema
                ORDER BY table_name, ordinal_position
                """,
                connection);
            command.Parameters.AddWithValue("schema", _bindingSchema);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            return columns;
        }

        public async Task MutateRevisionHistoryAsync(string operation)
        {
            var sql = operation switch
            {
                "UPDATE" => $"UPDATE {_bindingSchema}.binding_revisions SET status = 'revoked'",
                "DELETE" => $"DELETE FROM {_bindingSchema}.binding_revisions",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertLegacyWidePendingAttemptAsync(string field)
        {
            var ordinal = field switch
            {
                "device_binding_id" => "1",
                "platform_account_id" => "2",
                "trace_id" => "3",
                "idempotency_key" => "4",
                "device_binding_id_newline" => "5",
                "platform_account_id_newline" => "6",
                "trace_id_newline" => "7",
                "idempotency_key_newline" => "8",
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            var idempotencyKey = Idempotency("legacy-wide-database-" + ordinal);
            var deviceBindingId = "db_11111111111111111111111111111111";
            var platformAccountId = "pa_22222222222222222222222222222222";
            var traceId = Trace("legacy-wide-database-" + ordinal);
            switch (field)
            {
                case "device_binding_id": deviceBindingId = "db_legacy-wide-value"; break;
                case "platform_account_id": platformAccountId = "pa_legacy-wide-value"; break;
                case "trace_id": traceId = "trace_legacy-wide-value"; break;
                case "idempotency_key": idempotencyKey = "idem_legacy-wide-value"; break;
                case "device_binding_id_newline": deviceBindingId += "\n"; break;
                case "platform_account_id_newline": platformAccountId += "\n"; break;
                case "trace_id_newline": traceId += "\n"; break;
                case "idempotency_key_newline": idempotencyKey += "\n"; break;
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {_bindingSchema}.binding_attempts
                    (idempotency_key, request_sha256, reservation_id, soul_id, device_binding_id,
                     platform_account_id, device_id, device_registration_revision,
                     account_authorization_revision, trace_id, occurred_at, state)
                VALUES
                    (@idempotency_key, @request_sha256, @reservation_id, @soul_id, @device_binding_id,
                     @platform_account_id, @device_id, 1, 1, @trace_id, @occurred_at, 'pending')
                """,
                connection);
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            command.Parameters.AddWithValue("request_sha256", new string('a', 64));
            command.Parameters.AddWithValue("reservation_id", "bres_" + new string(ordinal[0], 64));
            command.Parameters.AddWithValue("soul_id", Soul);
            command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
            command.Parameters.AddWithValue("platform_account_id", platformAccountId);
            command.Parameters.AddWithValue("device_id", "device_33333333333333333333333333333333");
            command.Parameters.AddWithValue("trace_id", traceId);
            command.Parameters.AddWithValue("occurred_at", OccurredAt);
            await command.ExecuteNonQueryAsync();
        }

        public async Task ReplaceAppliedMigrationDigestAsync(string migrationId, string digest)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"UPDATE {_bindingSchema}.module_schema_migrations SET content_sha256 = @digest WHERE migration_id = @migration_id",
                connection);
            command.Parameters.AddWithValue("digest", digest);
            command.Parameters.AddWithValue("migration_id", migrationId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync();
                foreach (var schema in new[] { _bindingSchema, _deviceSchema, _accountSchema })
                {
                    await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                    await command.ExecuteNonQueryAsync();
                }
            }
            catch
            {
                // Test failures preserve the primary error; disposable random schemas are safe for later cleanup.
            }
            finally
            {
                Authorization.Dispose();
            }
        }
    }

    private static string Trace(string label) => "trace_" + Digest("trace", label)[..32];

    private static string Idempotency(string label) => "idem_" + Digest("idempotency", label);

    private static string Digest(string domain, string label)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(domain + ":" + label)));
}
