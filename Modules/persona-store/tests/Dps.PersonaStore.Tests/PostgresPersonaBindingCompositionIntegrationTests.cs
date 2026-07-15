using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dps.Binding;
using Dps.DeviceRegistry.Contracts;
using Dps.PlatformAccountRegistry.Contracts;
using Npgsql;
using Xunit;

namespace Dps.PersonaStore.Tests;

public sealed class PostgresPersonaBindingCompositionIntegrationTests
{
    private const string Soul = "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BindingId = "db_cccccccccccccccccccccccccccccccc";
    private const string AccountId = "pa_dddddddddddddddddddddddddddddddd";
    private const string DeviceId = "device_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset OccurredAt = new(2026, 7, 14, 5, 0, 0, TimeSpan.Zero);
    private static readonly string RequestHmacKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x7c, 32).ToArray());
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task RealPostgresBindingFenceSerializesPersonaCommitAndDurableReplayAfterRevoke()
    {
        await using var database = await CompositionDatabase.CreateAsync();
        var command = Put(0, "real-binding-persona");
        var commitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = database.CreatePersonaStore(async (stage, cancellationToken) =>
        {
            if (stage != PersonaMutationStage.TransactionCommittedWithBindingFenceHeld) return;
            commitReached.TrySetResult();
            await allowReturn.Task.WaitAsync(cancellationToken);
        });

        var mutation = store.PutAsync(command, TestCancellation).AsTask();
        await commitReached.Task.WaitAsync(TimeSpan.FromSeconds(2), TestCancellation);
        var revoke = database.Binding.RevokeAsync(new RevokeBindingCommand(
            Soul,
            BindingId,
            AccountId,
            1,
            Trace("real-binding-revoke"),
            Idem("real-binding-revoke"),
            OccurredAt.AddMinutes(3)), TestCancellation);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestCancellation);
        Assert.False(revoke.IsCompleted);

        allowReturn.TrySetResult();
        var committed = await mutation;
        Assert.Equal("revoked", (await revoke).Status);

        Assert.Equal(committed, await database.Persona.PutAsync(command, TestCancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await database.Persona.PutAsync(Put(committed.PersonaRevision, "after-real-binding-revoke"), TestCancellation));
        Assert.Equal(1, await database.Persona.CountRevisionsAsync(TestCancellation));
        Assert.Equal(1, await database.Persona.CountReceiptsAsync(TestCancellation));
        Assert.Equal(1, await database.Persona.CountOutboxAsync(TestCancellation));
    }

    private static PutPersonaCommand Put(long expectedRevision, string label) => new(
        Soul,
        BindingId,
        AccountId,
        expectedRevision,
        new Dictionary<string, string> { ["tone"] = "calm" },
        [new string('a', 64)],
        Trace(label),
        Idem(label),
        OccurredAt.AddMinutes(expectedRevision + 1));

    private static string Trace(string label) => "trace_" + Digest("trace:" + label)[..32];
    private static string Idem(string label) => "idem_" + Digest("idempotency:" + label);
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class CompositionDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _runtimeConnectionString;
        private readonly string _bindingSchema;
        private readonly string _personaSchema;
        private readonly string _runtimeRole;

        private CompositionDatabase(
            string adminConnectionString,
            string runtimeConnectionString,
            string bindingSchema,
            string personaSchema,
            string runtimeRole,
            PostgresBindingRegistry binding)
        {
            _adminConnectionString = adminConnectionString;
            _runtimeConnectionString = runtimeConnectionString;
            _bindingSchema = bindingSchema;
            _personaSchema = personaSchema;
            _runtimeRole = runtimeRole;
            Binding = binding;
            Persona = CreatePersonaStore();
        }

        public PostgresBindingRegistry Binding { get; }
        public PostgresPersonaStore Persona { get; }

        public static async Task<CompositionDatabase> CreateAsync()
        {
            var adminConnectionString = RequireConnectionString();
            var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            if (adminBuilder.Port == 55434 ||
                string.Equals(adminBuilder.Database, "dps_gbrain_company", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Persona/Binding Integration must never use the persistent GBrain Company database.");
            }
            if (string.IsNullOrWhiteSpace(adminBuilder.Username))
                throw new InvalidOperationException("DPS_TEST_POSTGRES requires an explicit migrator username.");

            var suffix = Guid.NewGuid().ToString("N")[..20];
            var bindingSchema = "binding_persona_it_" + suffix;
            var personaSchema = "persona_binding_it_" + suffix;
            var runtimeRole = "persona_bind_rt_" + suffix;
            var runtimePassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            await using (var admin = new NpgsqlConnection(adminConnectionString))
            {
                await admin.OpenAsync(TestCancellation);
                await using (var version = new NpgsqlCommand("SHOW server_version_num", admin))
                {
                    var exact = Convert.ToInt32(await version.ExecuteScalarAsync(TestCancellation), System.Globalization.CultureInfo.InvariantCulture);
                    if (exact != 180004)
                        throw new InvalidOperationException($"Persona/Binding Integration requires exact PostgreSQL 18.4; server_version_num was {exact}.");
                }
                var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(runtimeRole);
                await using var role = new NpgsqlCommand(
                    $"CREATE ROLE {quotedRole} LOGIN PASSWORD @password NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT",
                    admin);
                role.Parameters.AddWithValue("password", runtimePassword);
                await role.ExecuteNonQueryAsync(TestCancellation);
            }

            var runtimeBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Username = runtimeRole,
                Password = runtimePassword,
                Pooling = false
            };
            var binding = CreateTestBindingRegistry(
                new PostgresBindingRegistryOptions(adminConnectionString, bindingSchema),
                new DeviceReservationClient(),
                new AccountReservationClient());
            var database = new CompositionDatabase(
                adminConnectionString,
                runtimeBuilder.ConnectionString,
                bindingSchema,
                personaSchema,
                runtimeRole,
                binding);
            try
            {
                await binding.InitializeAsync(TestCancellation);
                _ = await binding.BindAsync(new CreateBindingCommand(
                    Soul,
                    BindingId,
                    AccountId,
                    DeviceId,
                    Trace("real-binding-create"),
                    Idem("real-binding-create"),
                    OccurredAt), TestCancellation);
                await database.Persona.InitializeAsync(TestCancellation);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public PostgresPersonaStore CreatePersonaStore(PersonaMutationFaultInjector? faultInjector = null) => new(
            new PostgresPersonaStoreOptions(
                _adminConnectionString,
                _runtimeConnectionString,
                _personaSchema,
                RequestHmacKey),
            Binding.CreateMutationFenceClient(),
            faultInjector);

        private static PostgresBindingRegistry CreateTestBindingRegistry(
            PostgresBindingRegistryOptions options,
            IDeviceBindingReservationClient device,
            IPlatformAccountBindingReservationClient account)
        {
            var factory = typeof(PostgresBindingRegistry).GetMethod(
                "CreateForTests",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(PostgresBindingRegistry).FullName, "CreateForTests");
            return (PostgresBindingRegistry)(factory.Invoke(null, [options, device, account, null])
                ?? throw new InvalidOperationException("Binding test composition returned no registry."));
        }

        private static string RequireConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("DPS_TEST_POSTGRES is required for real Persona/Binding PostgreSQL Integration; missing infrastructure is not a skip.");
            return value;
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            try
            {
                await connection.OpenAsync();
                var builder = new NpgsqlCommandBuilder();
                foreach (var schema in new[] { _personaSchema, _bindingSchema })
                {
                    var quotedSchema = builder.QuoteIdentifier(schema);
                    await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE", connection);
                    await dropSchema.ExecuteNonQueryAsync();
                }
                var quotedRole = builder.QuoteIdentifier(_runtimeRole);
                await using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS {quotedRole}", connection);
                await dropRole.ExecuteNonQueryAsync();
            }
            catch
            {
                // Preserve the primary test failure; random schema and role names are safe for later cleanup.
            }
        }
    }

    private sealed class DeviceReservationClient : IDeviceBindingReservationClient
    {
        public string InstanceConfigurationSha256 { get; } = new('1', 64);
        public long InstanceTrustEpoch => 1;

        public Task<DeviceRegisteredV1> ReadCurrentAsync(
            string deviceId,
            string soulId,
            string deviceBindingId,
            string platformAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScope(soulId, deviceBindingId, platformAccountId);
            if (deviceId != DeviceId) throw new KeyNotFoundException("Unknown device.");
            return Task.FromResult(new DeviceRegisteredV1(
                "1.0.0", "device.registered/v1", "device-registry",
                Soul, BindingId, AccountId, Trace("device-provider"), Idem("device-provider"), OccurredAt,
                "sensitive", DeviceId, new string('2', 64), "fpkey_33333333333333333333333333333333", 1,
                1, ["observe"], "registered"));
        }

        public Task<DeviceBindingReservationV1> ReserveAsync(ReserveDeviceBindingCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(Receipt(
                command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.DeviceId,
                command.ExpectedRevision, command.ReservationId, command.TraceId, command.OccurredAt,
                "held", DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken));

        public Task<DeviceBindingReservationV1> ConfirmAsync(DeviceBindingReservationCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(Receipt(
                command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.DeviceId,
                command.ExpectedRevision, command.ReservationId, command.TraceId, command.OccurredAt,
                "active", null, cancellationToken));

        public Task<DeviceBindingReservationV1> ReleaseAsync(DeviceBindingReservationCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(Receipt(
                command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.DeviceId,
                command.ExpectedRevision, command.ReservationId, command.TraceId, command.OccurredAt,
                "released", null, cancellationToken));

        private static DeviceBindingReservationV1 Receipt(
            string soulId,
            string bindingId,
            string accountId,
            string deviceId,
            long revision,
            string reservationId,
            string traceId,
            DateTimeOffset occurredAt,
            string state,
            DateTimeOffset? leaseExpiresAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScope(soulId, bindingId, accountId);
            if (deviceId != DeviceId || revision != 1) throw new InvalidOperationException("Unexpected device reservation scope.");
            return new DeviceBindingReservationV1(
                "1.0.0", "device.binding.reservation/v1", "device-registry",
                Soul, BindingId, AccountId, traceId,
                ReservationIdempotency("dps.device-binding-reservation.receipt/v1:", reservationId, state),
                occurredAt, "sensitive", reservationId, DeviceId, revision, state, leaseExpiresAt);
        }
    }

    private sealed class AccountReservationClient : IPlatformAccountBindingReservationClient
    {
        public string InstanceConfigurationSha256 { get; } = new('4', 64);
        public long InstanceTrustEpoch => 1;

        public Task<PlatformAccountAuthorizedV1> ReadCurrentAsync(
            string platformAccountId,
            string soulId,
            string deviceBindingId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScope(soulId, deviceBindingId, platformAccountId);
            return Task.FromResult(new PlatformAccountAuthorizedV1(
                "1.0.0", "platform.account.authorized/v1", "platform-account-registry",
                Soul, BindingId, AccountId, Trace("account-provider"), Idem("account-provider"), OccurredAt,
                "sensitive", "fixture", new string('5', 64), "fixture-key", "approval_persona_binding",
                1, "authorized", 1));
        }

        public Task<PlatformAccountBindingReservationV1> ReserveAsync(ReservePlatformAccountBindingCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(Receipt(
                command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.ExpectedRevision,
                command.ReservationId, command.TraceId, command.OccurredAt,
                "held", DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken));

        public Task<PlatformAccountBindingReservationV1> ConfirmAsync(PlatformAccountBindingReservationCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(Receipt(
                command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.ExpectedRevision,
                command.ReservationId, command.TraceId, command.OccurredAt,
                "active", null, cancellationToken));

        public Task<PlatformAccountBindingReservationV1> ReleaseAsync(PlatformAccountBindingReservationCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(Receipt(
                command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.ExpectedRevision,
                command.ReservationId, command.TraceId, command.OccurredAt,
                "released", null, cancellationToken));

        private static PlatformAccountBindingReservationV1 Receipt(
            string soulId,
            string bindingId,
            string accountId,
            long revision,
            string reservationId,
            string traceId,
            DateTimeOffset occurredAt,
            string state,
            DateTimeOffset? leaseExpiresAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScope(soulId, bindingId, accountId);
            if (revision != 1) throw new InvalidOperationException("Unexpected account reservation revision.");
            return new PlatformAccountBindingReservationV1(
                "1.0.0", "platform.account.binding.reservation/v1", "platform-account-registry",
                Soul, BindingId, AccountId, traceId,
                PlatformAccountBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, state),
                occurredAt, "sensitive", reservationId, revision, state, leaseExpiresAt);
        }
    }

    private static void EnsureScope(string soulId, string bindingId, string accountId)
    {
        if (soulId != Soul || bindingId != BindingId || accountId != AccountId)
            throw new KeyNotFoundException("Unknown provider scope.");
    }

    private static string ReservationIdempotency(string domain, string reservationId, string state)
        => "idem_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(domain + reservationId + ":" + state)));
}
