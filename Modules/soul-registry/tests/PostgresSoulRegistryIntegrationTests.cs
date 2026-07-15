using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dps.MemoryEventLedger;
using Dps.MemoryEventLedger.Contracts;
using Npgsql;
using Xunit;

namespace Dps.SoulRegistry.Tests;

public sealed class PostgresSoulRegistryIntegrationTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 14, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentRegistrationAndKeyRotationKeepOneImmutableSoul()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var oldRegistry = database.CreateRegistry("key-v1", Key("key-v1", 0x11));
        await oldRegistry.InitializeAsync(TestContext.Current.CancellationToken);
        var requests = Enumerable.Range(0, 16)
            .Select(index => Register("Concurrent.Person@example.test", $"concurrent-{index}"))
            .ToArray();
        var resolved = await Task.WhenAll(requests.Select(request =>
            oldRegistry.RegisterVerifiedAliasAsync(request, TestContext.Current.CancellationToken)));
        var soulId = Assert.Single(resolved.Select(static item => item.SoulId).Distinct(StringComparer.Ordinal));
        Assert.Matches("^soul_[a-f0-9]{64}\\z", soulId);

        using var rotatedRegistry = database.CreateRegistry("key-v2", Key("key-v1", 0x11), Key("key-v2", 0x22));
        var rotated = await rotatedRegistry.ResolveAsync(
            Resolve("concurrent.person@example.test", "rotated-resolve"),
            TestContext.Current.CancellationToken);
        Assert.Equal(soulId, rotated.SoulId);

        await rotatedRegistry.RegisterVerifiedAliasAsync(
            Register("+60 (12) 345-6789", "link-phone", IdentityAliasKind.Phone, soulId),
            TestContext.Current.CancellationToken);
        var linked = await rotatedRegistry.ResolveAsync(
            Resolve("+60123456789", "resolve-phone", IdentityAliasKind.Phone),
            TestContext.Current.CancellationToken);
        Assert.Equal(soulId, linked.SoulId);
        Assert.Equal(2, (await rotatedRegistry.ExportAliasMetadataAsync("tenant-a", soulId, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConflictAmbiguityCrossTenantAndIdempotencyReuseFailClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var oldRegistry = database.CreateRegistry("key-v1", Key("key-v1", 0x31));
        await oldRegistry.InitializeAsync(TestContext.Current.CancellationToken);
        var first = await oldRegistry.RegisterVerifiedAliasAsync(Register("first@example.test", "first"), TestContext.Current.CancellationToken);
        var second = await oldRegistry.RegisterVerifiedAliasAsync(Register("second@example.test", "second"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AliasConflictException>(() => oldRegistry.RegisterVerifiedAliasAsync(
            Register("first@example.test", "conflict", targetSoulId: second.SoulId),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => oldRegistry.RegisterVerifiedAliasAsync(
            Register("third@example.test", "first"),
            TestContext.Current.CancellationToken));

        var tenantB = await oldRegistry.RegisterVerifiedAliasAsync(
            Register("first@example.test", "tenant-b-first", tenantId: "tenant-b"),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(first.SoulId, tenantB.SoulId);
        Assert.NotEqual(first.AliasDigest, tenantB.AliasDigest);

        using var newOnlyRegistry = database.CreateRegistry("key-v2", Key("key-v2", 0x32));
        var ambiguousNew = await newOnlyRegistry.RegisterVerifiedAliasAsync(
            Register("ambiguous@example.test", "ambiguous-new"),
            TestContext.Current.CancellationToken);
        var ambiguousOld = await oldRegistry.RegisterVerifiedAliasAsync(
            Register("ambiguous@example.test", "ambiguous-old"),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(ambiguousOld.SoulId, ambiguousNew.SoulId);

        using var bothRegistry = database.CreateRegistry("key-v2", Key("key-v1", 0x31), Key("key-v2", 0x32));
        await Assert.ThrowsAsync<AmbiguousAliasException>(() => bothRegistry.ResolveAsync(
            Resolve("ambiguous@example.test", "ambiguous-resolve"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevokeIsIdempotentAndCrashRecoveryRollsBackAtomically()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var registry = database.CreateRegistry("key-v1", Key("key-v1", 0x41));
        await registry.InitializeAsync(TestContext.Current.CancellationToken);
        var resolved = await registry.RegisterVerifiedAliasAsync(Register("revoke@example.test", "revoke-register"), TestContext.Current.CancellationToken);
        var revoke = Revoke("revoke@example.test", resolved.SoulId, "revoke-1", "verified correction");
        await registry.RevokeAliasAsync(revoke, TestContext.Current.CancellationToken);
        await registry.RevokeAliasAsync(revoke, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => registry.RevokeAliasAsync(
            revoke with { Reason = "different reason" },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AliasRevokedException>(() => registry.ResolveAsync(
            Resolve("revoke@example.test", "resolve-after-revoke"),
            TestContext.Current.CancellationToken));

        var crashTarget = await registry.RegisterVerifiedAliasAsync(Register("crash@example.test", "crash-register"), TestContext.Current.CancellationToken);
        using var crashingRegistry = database.CreateRegistry(
            "key-v1",
            (_, _) => ValueTask.FromException(new InvalidOperationException("synthetic crash before commit")),
            Key("key-v1", 0x41));
        var crashRequest = Revoke("crash@example.test", crashTarget.SoulId, "crash-revoke", "privacy request");
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashingRegistry.RevokeAliasAsync(
            crashRequest,
            TestContext.Current.CancellationToken));
        await registry.RevokeAliasAsync(crashRequest, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AliasRevokedException>(() => registry.ResolveAsync(
            Resolve("crash@example.test", "resolve-after-recovery"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DatabaseProtectsImmutableRowsAndStoresNoRawAlias()
    {
        const string rawAlias = "no.raw.storage@example.test";
        await using var database = await TestDatabase.CreateAsync();
        using var registry = database.CreateRegistry("key-v1", Key("key-v1", 0x51));
        await registry.InitializeAsync(TestContext.Current.CancellationToken);
        var resolved = await registry.RegisterVerifiedAliasAsync(Register(rawAlias, "storage"), TestContext.Current.CancellationToken);
        var storedJson = await database.ReadStoredRowsAsync();
        Assert.DoesNotContain(rawAlias, storedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no.raw.storage", storedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proof-storage", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(rawAlias, JsonSerializer.Serialize(resolved), StringComparison.OrdinalIgnoreCase);

        await database.AssertMutationRejectedAsync($"UPDATE {database.SchemaName}.souls SET tenant_id = 'attacker' WHERE soul_id = '{resolved.SoulId}'");
        await database.AssertMutationRejectedAsync($"UPDATE {database.SchemaName}.identity_aliases SET alias_digest = repeat('0', 64) WHERE soul_id = '{resolved.SoulId}'");
        await database.AssertMutationRejectedAsync($"DELETE FROM {database.SchemaName}.resolution_receipts WHERE soul_id = '{resolved.SoulId}'");
        await database.AssertMutationRejectedAsync(
            $"""
            INSERT INTO {database.SchemaName}.identity_aliases
                (alias_id, tenant_id, alias_kind, alias_digest, alias_key_id, soul_id,
                 verification_evidence_sha256, verified_at, created_at)
            VALUES
                (gen_random_uuid(), 'tenant-b', 'email', repeat('1', 64), 'key-v1', '{resolved.SoulId}',
                 repeat('2', 64), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            """,
            PostgresErrorCodes.ForeignKeyViolation);
        await database.AssertMutationRejectedAsync(
            $"""
            INSERT INTO {database.SchemaName}.resolution_receipts
                (tenant_id, idempotency_key, operation, request_sha256, soul_id,
                 device_binding_id, platform_account_id, trace_id, occurred_at,
                 alias_kind, alias_digest, alias_key_id)
            VALUES
                ('tenant-b', 'idem_1111111111111111111111111111111111111111111111111111111111111111',
                 'resolve', repeat('3', 64), '{resolved.SoulId}',
                 'db_22222222222222222222222222222222',
                 'pa_33333333333333333333333333333333',
                 'trace_44444444444444444444444444444444', CURRENT_TIMESTAMP,
                 'email', repeat('4', 64), 'key-v1')
            """,
            PostgresErrorCodes.ForeignKeyViolation);
        await database.AssertMutationRejectedAsync(
            $"""
            INSERT INTO {database.SchemaName}.mutation_receipts
                (tenant_id, idempotency_key, operation, request_sha256, entity_id,
                 trace_id, occurred_at)
            VALUES
                ('tenant-b', 'idem_5555555555555555555555555555555555555555555555555555555555555555',
                 'revoke', repeat('5', 64), '{resolved.SoulId}',
                 'trace_66666666666666666666666666666666', CURRENT_TIMESTAMP)
            """,
            PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealResolvedSoulFlowsIntoRealLedgerWithoutCrossSoulOrRawAliasLeakage()
    {
        const string rawAlias = "ledger.person@example.test";
        await using var database = await TestDatabase.CreateAsync();
        using var registry = database.CreateRegistry("key-v1", Key("key-v1", 0x61));
        await registry.InitializeAsync(TestContext.Current.CancellationToken);
        var registered = await registry.RegisterVerifiedAliasAsync(Register(rawAlias, "ledger-register"), TestContext.Current.CancellationToken);
        var resolved = await registry.ResolveAsync(Resolve(rawAlias, "ledger-resolve"), TestContext.Current.CancellationToken);
        Assert.Equal(registered.SoulId, resolved.SoulId);

        var ledgerSchema = $"dps_f2_soul_ledger_{Guid.NewGuid():N}";
        try
        {
            var ledger = new PostgresMemoryEventLedger(new MemoryEventLedgerOptions(database.ConnectionString, ledgerSchema));
            await ledger.InitializeAsync(TestContext.Current.CancellationToken);
            var memoryEvent = new MemoryEventV1(
                MemoryEventV1.CurrentSchemaVersion,
                MemoryEventV1.CurrentContractId,
                MemoryEventV1.CurrentProducerModule,
                Guid.NewGuid(),
                resolved.SoulId,
                resolved.DeviceBindingId,
                resolved.PlatformAccountId,
                resolved.TraceId,
                "event-ledger",
                BaseTime.AddMinutes(1),
                "personal",
                MemoryEventV1.ObservedContentEventType,
                new MemoryObservationV1(new string('d', 64), true, [new InterestSignalV1("robotics", 0.8m)]));
            var append = await ledger.AppendAsync(resolved, memoryEvent, TestContext.Current.CancellationToken);
            Assert.Equal(AppendDisposition.Inserted, append.Disposition);
            var persisted = Assert.Single(await ledger.ReadSoulEventsAsync(resolved.SoulId, TestContext.Current.CancellationToken));
            Assert.Equal(resolved.SoulId, persisted.SoulId);
            Assert.Equal(resolved.DeviceBindingId, persisted.DeviceBindingId);
            Assert.Equal(resolved.PlatformAccountId, persisted.PlatformAccountId);
            Assert.Empty(await ledger.ReadSoulEventsAsync("soul_" + new string('f', 64), TestContext.Current.CancellationToken));
            Assert.DoesNotContain(rawAlias, MemoryEventCanonicalizer.Serialize(persisted), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await TestDatabase.DropSchemaAsync(database.ConnectionString, ledgerSchema);
        }
    }

    private static KeySpec Key(string id, byte fill) => new(id, Enumerable.Repeat(fill, 32).ToArray());

    private static RegisterVerifiedAliasRequest Register(
        string rawAlias,
        string suffix,
        IdentityAliasKind kind = IdentityAliasKind.Email,
        string? targetSoulId = null,
        string tenantId = "tenant-a")
        => new(
            RegisterVerifiedAliasRequest.CurrentSchemaVersion,
            tenantId,
            kind,
            rawAlias,
            new AliasVerification($"proof-{suffix}", BaseTime.AddMinutes(-1)),
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            TraceId(suffix),
            IdempotencyKey(suffix),
            BaseTime,
            targetSoulId);

    private static ResolveSoulRequest Resolve(
        string rawAlias,
        string suffix,
        IdentityAliasKind kind = IdentityAliasKind.Email)
        => new(
            ResolveSoulRequest.CurrentSchemaVersion,
            "tenant-a",
            kind,
            rawAlias,
            new AliasVerification($"resolve-proof-{suffix}", BaseTime),
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            TraceId(suffix),
            IdempotencyKey(suffix),
            BaseTime.AddMinutes(1));

    private static RevokeAliasRequest Revoke(string rawAlias, string soulId, string suffix, string reason)
        => new(
            RevokeAliasRequest.CurrentSchemaVersion,
            "tenant-a",
            IdentityAliasKind.Email,
            rawAlias,
            soulId,
            reason,
            TraceId(suffix),
            IdempotencyKey(suffix),
            BaseTime.AddMinutes(2));

    private static string TraceId(string value) => "trace_" + Digest(value)[..32];
    private static string IdempotencyKey(string value) => "idem_" + Digest(value);
    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal sealed record KeySpec(string Id, byte[] Bytes);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string connectionString, string schemaName)
        {
            ConnectionString = connectionString;
            SchemaName = schemaName;
        }

        public string ConnectionString { get; }
        public string SchemaName { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "DPS_TEST_POSTGRES is required. Required integration tests fail rather than skip when PostgreSQL is unavailable.");
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
            var serverVersion = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            Assert.Equal("180004", serverVersion);
            return new TestDatabase(connectionString, $"dps_f2_soul_{Guid.NewGuid():N}");
        }

        public PostgresSoulRegistry CreateRegistry(string currentKeyId, params KeySpec[] keys)
            => CreateRegistry(currentKeyId, null, keys);

        public PostgresSoulRegistry CreateRegistry(
            string currentKeyId,
            SoulRegistryFaultInjector? faultInjector,
            params KeySpec[] keys)
        {
            var materials = keys.Select(static key => new AliasHmacKey(key.Id, key.Bytes)).ToArray();
            try
            {
                return new PostgresSoulRegistry(
                    new SoulRegistryOptions(ConnectionString, SchemaName, currentKeyId, materials),
                    faultInjector);
            }
            finally
            {
                foreach (var material in materials)
                {
                    material.Dispose();
                }
            }
        }

        public async Task<string> ReadStoredRowsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                $"""
                SELECT jsonb_build_object(
                    'souls', (SELECT jsonb_agg(to_jsonb(s)) FROM {SchemaName}.souls s),
                    'aliases', (SELECT jsonb_agg(to_jsonb(a)) FROM {SchemaName}.identity_aliases a),
                    'receipts', (SELECT jsonb_agg(to_jsonb(r)) FROM {SchemaName}.resolution_receipts r),
                    'mutations', (SELECT jsonb_agg(to_jsonb(m)) FROM {SchemaName}.mutation_receipts m)
                )::text
                """,
                connection);
            return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL did not return stored rows."));
        }

        public async Task AssertMutationRejectedAsync(string sql, string? expectedSqlState = null)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            if (expectedSqlState is not null)
            {
                Assert.Equal(expectedSqlState, exception.SqlState);
            }
        }

        public async ValueTask DisposeAsync() => await DropSchemaAsync(ConnectionString, SchemaName);

        public static async Task DropSchemaAsync(string connectionString, string schemaName)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schemaName} CASCADE", connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
