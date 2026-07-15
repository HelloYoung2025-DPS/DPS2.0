using System.Security.Cryptography;
using System.Text;
using Dps.DeviceRegistry.Contracts;
using Npgsql;
using Xunit;

namespace Dps.DeviceRegistry.Tests;

public sealed class PostgresDeviceRegistryIntegrationTests
{
    private const string SoulA = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SoulB = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BindingA = "db_11111111111111111111111111111111";
    private const string BindingB = "db_22222222222222222222222222222222";
    private const string AccountA = "pa_33333333333333333333333333333333";
    private const string AccountB = "pa_44444444444444444444444444444444";

    [Fact, Trait("Category", "Integration")]
    public async Task RegistrationReceiptOutboxAndRestartReadbackAreOneDurableMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var occurredAt = new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero);
        var command = Register('1', SoulA, BindingA, AccountA, ["ocr", "adb"], "register-1", occurredAt);

        var inserted = await registry.RegisterAsync(command, cancellationToken);
        var duplicate = await database.CreateRegistry().RegisterAsync(
            command with
            {
                Capabilities = ["adb", "ocr"],
                TraceId = Trace("retry-does-not-change-semantic-hash"),
                OccurredAt = occurredAt.AddMinutes(1)
            },
            cancellationToken);

        Assert.Equal(inserted.DeviceId, duplicate.DeviceId);
        Assert.Equal(inserted.IdempotencyKey, duplicate.IdempotencyKey);
        Assert.Equal(1, await registry.CountDevicesAsync(cancellationToken));
        Assert.Equal(1, await registry.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(1, await registry.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(1, await registry.CountOutboxAsync(cancellationToken));
        Assert.Equal(0, await registry.CountQuarantineAsync(cancellationToken));

        var restarted = database.CreateRegistry();
        var readback = await restarted.GetAsync(inserted.DeviceId, SoulA, BindingA, AccountA, cancellationToken);
        Assert.Equal(inserted.DeviceId, readback.DeviceId);
        Assert.Equal(inserted.FingerprintHmacSha256, readback.FingerprintHmacSha256);
        Assert.Equal(inserted.FingerprintKeyId, readback.FingerprintKeyId);
        Assert.Equal(inserted.FingerprintKeyEpoch, readback.FingerprintKeyEpoch);
        Assert.Equal(["adb", "ocr"], readback.Capabilities);
        Assert.True(await restarted.IsRegisteredAsync(inserted.DeviceId, SoulA, BindingA, AccountA, cancellationToken));

        var outbox = await restarted.ReadPendingOutboxAsync(SoulA, BindingA, AccountA, cancellationToken);
        var pending = Assert.Single(outbox);
        Assert.Equal(inserted.DeviceId, pending.Payload.DeviceId);
        Assert.Matches("^[a-f0-9]{64}$", pending.PayloadSha256);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CapabilityRevisionAndRetirementEachCommitReceiptAndOutbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var occurredAt = new DateTimeOffset(2026, 7, 14, 2, 0, 0, TimeSpan.Zero);
        var registered = await registry.RegisterAsync(
            Register('2', SoulA, BindingA, AccountA, ["adb"], "lifecycle-1", occurredAt),
            cancellationToken);
        var updated = await registry.UpdateCapabilitiesAsync(
            new UpdateDeviceCapabilitiesCommand(
                SoulA,
                BindingA,
                AccountA,
                registered.DeviceId,
                1,
                ["vision", "adb"],
                Trace("lifecycle-2"),
                Idempotency("lifecycle-2"),
                occurredAt.AddMinutes(1)),
            cancellationToken);
        var retired = await registry.RetireAsync(
            new RetireDeviceCommand(
                SoulA,
                BindingA,
                AccountA,
                registered.DeviceId,
                2,
                Trace("lifecycle-3"),
                Idempotency("lifecycle-3"),
                occurredAt.AddMinutes(2)),
            cancellationToken);

        Assert.Equal(2, updated.CapabilityRevision);
        Assert.Equal(["adb", "vision"], updated.Capabilities);
        Assert.Equal(3, retired.CapabilityRevision);
        Assert.Equal("retired", retired.Status);
        Assert.Equal(1, await registry.CountDevicesAsync(cancellationToken));
        Assert.Equal(3, await registry.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(3, await registry.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(3, await registry.CountOutboxAsync(cancellationToken));

        var restarted = database.CreateRegistry();
        var readback = await restarted.GetAsync(registered.DeviceId, SoulA, BindingA, AccountA, cancellationToken);
        Assert.Equal("retired", readback.Status);
        Assert.Equal(3, readback.CapabilityRevision);
        Assert.False(await restarted.IsRegisteredAsync(registered.DeviceId, SoulA, BindingA, AccountA, cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SameIdempotencyKeyWithDifferentHashIsQuarantinedAndRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var occurredAt = new DateTimeOffset(2026, 7, 14, 3, 0, 0, TimeSpan.Zero);
        await registry.RegisterAsync(
            Register('3', SoulA, BindingA, AccountA, ["adb"], "conflict", occurredAt),
            cancellationToken);

        await Assert.ThrowsAsync<DeviceIdempotencyConflictException>(() => registry.RegisterAsync(
            Register('4', SoulA, BindingA, AccountA, ["adb"], "conflict", occurredAt),
            cancellationToken));

        Assert.Equal(1, await registry.CountDevicesAsync(cancellationToken));
        Assert.Equal(1, await registry.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(1, await registry.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(1, await registry.CountOutboxAsync(cancellationToken));
        Assert.Equal(1, await registry.CountQuarantineAsync(cancellationToken));
        var quarantine = Assert.Single(await registry.ReadQuarantineAsync(cancellationToken));
        Assert.Equal(Idempotency("conflict"), quarantine.IdempotencyKey);
        Assert.NotEqual(quarantine.ExistingCommandSha256, quarantine.IncomingCommandSha256);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentDuplicateRegistrationHasOneDeviceRevisionReceiptAndOutbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var command = Register(
            '5',
            SoulA,
            BindingA,
            AccountA,
            ["adb", "ocr"],
            "concurrent-register",
            new DateTimeOffset(2026, 7, 14, 4, 0, 0, TimeSpan.Zero));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => database.CreateRegistry().RegisterAsync(command, cancellationToken)));

        Assert.Single(results.Select(static result => result.DeviceId).Distinct(StringComparer.Ordinal));
        var registry = database.CreateRegistry();
        Assert.Equal(1, await registry.CountDevicesAsync(cancellationToken));
        Assert.Equal(1, await registry.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(1, await registry.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(1, await registry.CountOutboxAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentCapabilityWritersAllowExactlyOneExpectedRevisionWinner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var occurredAt = new DateTimeOffset(2026, 7, 14, 5, 0, 0, TimeSpan.Zero);
        var registered = await registry.RegisterAsync(
            Register('6', SoulA, BindingA, AccountA, ["adb"], "concurrent-update-0", occurredAt),
            cancellationToken);

        var attempts = await Task.WhenAll(
            AttemptUpdateAsync(
                database.CreateRegistry(),
                new UpdateDeviceCapabilitiesCommand(
                    SoulA, BindingA, AccountA, registered.DeviceId, 1, ["vision"],
                    Trace("concurrent-a"), Idempotency("concurrent-update-a"), occurredAt.AddMinutes(1)),
                cancellationToken),
            AttemptUpdateAsync(
                database.CreateRegistry(),
                new UpdateDeviceCapabilitiesCommand(
                    SoulA, BindingA, AccountA, registered.DeviceId, 1, ["ocr"],
                    Trace("concurrent-b"), Idempotency("concurrent-update-b"), occurredAt.AddMinutes(1)),
                cancellationToken));

        Assert.Single(attempts, static attempt => attempt.Result is not null);
        var failure = Assert.Single(attempts, static attempt => attempt.Error is not null);
        Assert.IsType<InvalidOperationException>(failure.Error);
        Assert.Equal(2, await registry.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(2, await registry.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(2, await registry.CountOutboxAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ScopedQueriesNeverReturnAnotherSoulBindingAccountOrDevice()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var occurredAt = new DateTimeOffset(2026, 7, 14, 6, 0, 0, TimeSpan.Zero);
        var deviceA = await registry.RegisterAsync(
            Register('7', SoulA, BindingA, AccountA, ["adb"], "scope-a", occurredAt),
            cancellationToken);
        var deviceB = await registry.RegisterAsync(
            Register('8', SoulB, BindingB, AccountB, ["ocr"], "scope-b", occurredAt),
            cancellationToken);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.GetAsync(
            deviceA.DeviceId, SoulB, BindingB, AccountB, cancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.GetAsync(
            deviceB.DeviceId, SoulA, BindingA, AccountA, cancellationToken));
        Assert.Empty(await registry.ReadPendingOutboxAsync(SoulA, BindingB, AccountA, cancellationToken));
        Assert.Single(await registry.ReadPendingOutboxAsync(SoulA, BindingA, AccountA, cancellationToken));
        Assert.Single(await registry.ReadPendingOutboxAsync(SoulB, BindingB, AccountB, cancellationToken));

        await database.AssertNoRawHardwareOrPiiColumnsAsync(cancellationToken);
        await database.AssertEnvelopeConstraintsRejectTrailingNewlinesAsync(deviceA, cancellationToken);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ProviderOwnedReservationFreezesExactRevisionAcrossRestartUntilRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var occurredAt = new DateTimeOffset(2026, 7, 14, 6, 30, 0, TimeSpan.Zero);
        var device = await registry.RegisterAsync(
            Register('e', SoulA, BindingA, AccountA, ["adb"], "reservation-device", occurredAt),
            cancellationToken);
        var reservationId = "bres_" + new string('3', 64);
        await registry.ReserveBindingAsync(new ReserveDeviceBindingCommand(
            SoulA, BindingA, AccountA, device.DeviceId, device.CapabilityRevision,
            reservationId, Trace("device-reserve"), occurredAt.AddMinutes(1)), cancellationToken);
        var reservation = new DeviceBindingReservationCommand(
            SoulA, BindingA, AccountA, device.DeviceId, device.CapabilityRevision,
            reservationId, Trace("device-confirm"), occurredAt.AddMinutes(1));
        Assert.Equal("active", (await database.CreateRegistry().ConfirmBindingAsync(
            reservation, cancellationToken)).State);
        await Assert.ThrowsAsync<DeviceBindingReservationConflictException>(() =>
            database.CreateRegistry().RetireAsync(new RetireDeviceCommand(
                SoulA, BindingA, AccountA, device.DeviceId, device.CapabilityRevision,
                Trace("device-blocked"), Idempotency("device-blocked"), occurredAt.AddMinutes(2)), cancellationToken));

        Assert.Equal("released", (await database.CreateRegistry().ReleaseBindingAsync(
            reservation with { TraceId = Trace("device-release"), OccurredAt = occurredAt.AddMinutes(3) },
            cancellationToken)).State);
        Assert.Equal("retired", (await database.CreateRegistry().RetireAsync(new RetireDeviceCommand(
            SoulA, BindingA, AccountA, device.DeviceId, device.CapabilityRevision,
            Trace("device-after-release"), Idempotency("device-after-release"), occurredAt.AddMinutes(4)),
            cancellationToken)).Status);
    }

    [Theory]
    [InlineData(DeviceMutationStage.DeviceWritten)]
    [InlineData(DeviceMutationStage.CapabilityRevisionWritten)]
    [InlineData(DeviceMutationStage.IdempotencyReceiptWritten)]
    [InlineData(DeviceMutationStage.OutboxWritten)]
    [InlineData(DeviceMutationStage.BeforeCommit)]
    [Trait("Category", "Integration")]
    public async Task RegistrationCrashWindowRollsBackEveryRowAndRetryRecovers(DeviceMutationStage failureStage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var injected = 0;
        var failingRegistry = database.CreateRegistry((stage, _) =>
        {
            if (stage == failureStage && Interlocked.Exchange(ref injected, 1) == 0)
            {
                throw new InvalidOperationException("injected device-registry crash window");
            }
            return ValueTask.CompletedTask;
        });
        var command = Register(
            '9',
            SoulA,
            BindingA,
            AccountA,
            ["adb"],
            "crash-register",
            new DateTimeOffset(2026, 7, 14, 7, 0, 0, TimeSpan.Zero));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failingRegistry.RegisterAsync(command, cancellationToken));
        var recovered = database.CreateRegistry();
        Assert.Equal(0, await recovered.CountDevicesAsync(cancellationToken));
        Assert.Equal(0, await recovered.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(0, await recovered.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(0, await recovered.CountOutboxAsync(cancellationToken));

        await recovered.RegisterAsync(command, cancellationToken);
        Assert.Equal(1, await recovered.CountDevicesAsync(cancellationToken));
        Assert.Equal(1, await recovered.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(1, await recovered.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(1, await recovered.CountOutboxAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CapabilityAndRetirementCrashWindowsRollbackThenRecover()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var occurredAt = new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero);
        var registered = await database.CreateRegistry().RegisterAsync(
            Register('a', SoulA, BindingA, AccountA, ["adb"], "mutation-crash-1", occurredAt),
            cancellationToken);
        var update = new UpdateDeviceCapabilitiesCommand(
            SoulA, BindingA, AccountA, registered.DeviceId, 1, ["vision"],
            Trace("mutation-crash-2"), Idempotency("mutation-crash-2"), occurredAt.AddMinutes(1));
        var failUpdate = database.CreateRegistry(static (stage, _) =>
            stage == DeviceMutationStage.OutboxWritten
                ? ValueTask.FromException(new InvalidOperationException("update crash"))
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failUpdate.UpdateCapabilitiesAsync(update, cancellationToken));
        var recovered = database.CreateRegistry();
        Assert.Equal(1, (await recovered.GetAsync(registered.DeviceId, SoulA, BindingA, AccountA, cancellationToken)).CapabilityRevision);
        Assert.Equal(1, await recovered.CountOutboxAsync(cancellationToken));
        var updated = await recovered.UpdateCapabilitiesAsync(update, cancellationToken);

        var retire = new RetireDeviceCommand(
            SoulA, BindingA, AccountA, registered.DeviceId, updated.CapabilityRevision,
            Trace("mutation-crash-3"), Idempotency("mutation-crash-3"), occurredAt.AddMinutes(2));
        var failRetirement = database.CreateRegistry(static (stage, _) =>
            stage == DeviceMutationStage.IdempotencyReceiptWritten
                ? ValueTask.FromException(new InvalidOperationException("retirement crash"))
                : ValueTask.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failRetirement.RetireAsync(retire, cancellationToken));
        Assert.True(await recovered.IsRegisteredAsync(registered.DeviceId, SoulA, BindingA, AccountA, cancellationToken));
        Assert.Equal(2, await recovered.CountOutboxAsync(cancellationToken));

        var retired = await recovered.RetireAsync(retire, cancellationToken);
        Assert.Equal("retired", retired.Status);
        Assert.Equal(3, await recovered.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(3, await recovered.CountIdempotencyReceiptsAsync(cancellationToken));
        Assert.Equal(3, await recovered.CountOutboxAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RegistrationRejectsAKeyVersionThatIsNotConfiguredForTheRegistry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreateAsync(cancellationToken);
        var registry = database.CreateRegistry();
        var command = Register(
            'b',
            SoulA,
            BindingA,
            AccountA,
            ["adb"],
            "wrong-key-version",
            new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(
            command with { FingerprintKeyEpoch = command.FingerprintKeyEpoch + 1 },
            cancellationToken));
        Assert.Equal(0, await registry.CountDevicesAsync(cancellationToken));
        Assert.Equal(0, await registry.CountCapabilityRevisionsAsync(cancellationToken));
        Assert.Equal(0, await registry.CountOutboxAsync(cancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PopulatedUnkeyedFingerprintSchemaIsRejectedInsteadOfFabricatedIntoHmacTruth()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DeviceRegistryTestDatabase.CreatePopulatedUnkeyedLegacyAsync(cancellationToken);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            database.CreateRegistry().InitializeAsync(cancellationToken));

        Assert.Contains("populated fingerprint_sha256 rows cannot be converted", error.MessageText, StringComparison.Ordinal);
    }

    private static RegisterDeviceCommand Register(
        char digest,
        string soulId,
        string bindingId,
        string accountId,
        IReadOnlyCollection<string> capabilities,
        string idempotencyLabel,
        DateTimeOffset occurredAt)
        => new(
            soulId,
            bindingId,
            accountId,
            new string(digest, 64),
            DeviceRegistryTestDatabase.FingerprintKeyId,
            DeviceRegistryTestDatabase.FingerprintKeyEpoch,
            capabilities,
            Trace("register-" + idempotencyLabel),
            Idempotency(idempotencyLabel),
            occurredAt);

    private static string Trace(string value) => CanonicalToken("trace_", value, 16);

    private static string Idempotency(string value) => CanonicalToken("idem_", value, 32);

    private static string CanonicalToken(string prefix, string value, int digestBytes)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        try
        {
            return prefix + Convert.ToHexStringLower(digest.AsSpan(0, digestBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static async Task<MutationAttempt> AttemptUpdateAsync(
        PostgresDeviceRegistry registry,
        UpdateDeviceCapabilitiesCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return new MutationAttempt(await registry.UpdateCapabilitiesAsync(command, cancellationToken), null);
        }
        catch (Exception exception)
        {
            return new MutationAttempt(null, exception);
        }
    }

    private sealed record MutationAttempt(DeviceRegisteredV1? Result, Exception? Error);
}

internal sealed class DeviceRegistryTestDatabase : IAsyncDisposable
{
    internal const string FingerprintKeyId = "fpkey_55555555555555555555555555555555";
    internal const long FingerprintKeyEpoch = 11;
    internal const long TrustEpoch = 13;

    private DeviceRegistryTestDatabase(string connectionString, string schemaName)
    {
        ConnectionString = connectionString;
        SchemaName = schemaName;
    }

    public string ConnectionString { get; }
    public string SchemaName { get; }

    public static async Task<DeviceRegistryTestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var connectionString = await RequireConnectionStringAsync(cancellationToken);

        var database = new DeviceRegistryTestDatabase(
            connectionString,
            $"dps_device_registry_{Guid.NewGuid():N}");
        try
        {
            await database.CreateRegistry().InitializeAsync(cancellationToken);
            return database;
        }
        catch
        {
            await database.DropSchemaAsync(CancellationToken.None);
            throw;
        }
    }

    public static async Task<DeviceRegistryTestDatabase> CreatePopulatedUnkeyedLegacyAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = await RequireConnectionStringAsync(cancellationToken);
        var database = new DeviceRegistryTestDatabase(
            connectionString,
            $"dps_device_registry_{Guid.NewGuid():N}");
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"""
                CREATE SCHEMA {database.SchemaName};
                CREATE TABLE {database.SchemaName}.devices
                (
                    device_id text PRIMARY KEY,
                    fingerprint_sha256 text NOT NULL UNIQUE,
                    registration_soul_id text NOT NULL,
                    registration_device_binding_id text NOT NULL,
                    registration_platform_account_id text NOT NULL,
                    current_revision bigint NOT NULL,
                    status text NOT NULL,
                    created_at timestamptz NOT NULL,
                    updated_at timestamptz NOT NULL
                );
                INSERT INTO {database.SchemaName}.devices
                    (device_id, fingerprint_sha256, registration_soul_id,
                     registration_device_binding_id, registration_platform_account_id,
                     current_revision, status, created_at, updated_at)
                VALUES
                    ('device_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                     '{new string('a', 64)}',
                     'soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                     'db_11111111111111111111111111111111',
                     'pa_33333333333333333333333333333333',
                     1,
                     'registered',
                     clock_timestamp(),
                     clock_timestamp());
                """,
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return database;
        }
        catch
        {
            await database.DropSchemaAsync(CancellationToken.None);
            throw;
        }
    }

    public PostgresDeviceRegistry CreateRegistry(DeviceMutationFaultInjector? faultInjector = null)
        => new(new PostgresDeviceRegistryOptions(
            ConnectionString,
            SchemaName,
            FingerprintKeyId,
            FingerprintKeyEpoch,
            TrustEpoch), faultInjector);

    public async Task AssertNoRawHardwareOrPiiColumnsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema_name
            ORDER BY table_name, ordinal_position
            """,
            connection);
        command.Parameters.AddWithValue("schema_name", SchemaName);
        var forbidden = new[] { "imei", "serial", "hardware", "email", "phone" };
        var sawFingerprintHmac = false;
        var sawFingerprintKeyId = false;
        var sawFingerprintKeyEpoch = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var columnName = reader.GetString(0);
            Assert.NotEqual("fingerprint_sha256", columnName);
            Assert.DoesNotContain(
                forbidden,
                value => columnName.Contains(value, StringComparison.OrdinalIgnoreCase));
            sawFingerprintHmac |= string.Equals(columnName, "fingerprint_hmac_sha256", StringComparison.Ordinal);
            sawFingerprintKeyId |= string.Equals(columnName, "fingerprint_key_id", StringComparison.Ordinal);
            sawFingerprintKeyEpoch |= string.Equals(columnName, "fingerprint_key_epoch", StringComparison.Ordinal);
        }
        Assert.True(sawFingerprintHmac);
        Assert.True(sawFingerprintKeyId);
        Assert.True(sawFingerprintKeyEpoch);
    }

    public async Task AssertEnvelopeConstraintsRejectTrailingNewlinesAsync(
        DeviceRegisteredV1 device,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var scopeCases = new[]
        {
            (Binding: device.DeviceBindingId + "\n", Account: device.PlatformAccountId, Trace: device.TraceId),
            (Binding: device.DeviceBindingId, Account: device.PlatformAccountId + "\n", Trace: device.TraceId),
            (Binding: device.DeviceBindingId, Account: device.PlatformAccountId, Trace: device.TraceId + "\n")
        };

        for (var index = 0; index < scopeCases.Length; index++)
        {
            var scope = scopeCases[index];
            await using var insertReservation = new NpgsqlCommand(
                $"INSERT INTO {SchemaName}.binding_reservations (reservation_id, device_id, soul_id, device_binding_id, platform_account_id, device_registration_revision, state, lease_expires_at, trace_id, occurred_at) VALUES (@reservation_id, @device_id, @soul_id, @binding_id, @account_id, @revision, 'released', NULL, @trace_id, @occurred_at)",
                connection);
            insertReservation.Parameters.AddWithValue("reservation_id", "bres_" + new string((char)('a' + index), 64));
            insertReservation.Parameters.AddWithValue("device_id", device.DeviceId);
            insertReservation.Parameters.AddWithValue("soul_id", device.SoulId);
            insertReservation.Parameters.AddWithValue("binding_id", scope.Binding);
            insertReservation.Parameters.AddWithValue("account_id", scope.Account);
            insertReservation.Parameters.AddWithValue("revision", device.CapabilityRevision);
            insertReservation.Parameters.AddWithValue("trace_id", scope.Trace);
            insertReservation.Parameters.AddWithValue("occurred_at", device.OccurredAt);
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => insertReservation.ExecuteNonQueryAsync(cancellationToken));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using var insertQuarantine = new NpgsqlCommand(
            $"INSERT INTO {SchemaName}.idempotency_quarantine (idempotency_key, mutation_kind, existing_command_sha256, incoming_command_sha256, reason) VALUES (@idempotency_key, 'register', @existing_hash, @incoming_hash, 'trailing-newline-negative')",
            connection);
        insertQuarantine.Parameters.AddWithValue("idempotency_key", device.IdempotencyKey + "\n");
        insertQuarantine.Parameters.AddWithValue("existing_hash", new string('d', 64));
        insertQuarantine.Parameters.AddWithValue("incoming_hash", new string('e', 64));
        var idempotencyException = await Assert.ThrowsAsync<PostgresException>(
            () => insertQuarantine.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, idempotencyException.SqlState);
    }

    public async ValueTask DisposeAsync()
        => await DropSchemaAsync(CancellationToken.None);

    private async Task DropSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {SchemaName} CASCADE", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> RequireConnectionStringAsync(CancellationToken cancellationToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "DPS_TEST_POSTGRES is required. The real PostgreSQL Integration suite fails rather than skips when it is unavailable.");
        }

        ValidateTestDatabaseTarget(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var versionCommand = new NpgsqlCommand("SHOW server_version_num", connection);
        var versionNumber = (string?)await versionCommand.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(versionNumber, "180004", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL 18.4 is required; server_version_num was '{versionNumber ?? "missing"}'.");
        }

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
}
