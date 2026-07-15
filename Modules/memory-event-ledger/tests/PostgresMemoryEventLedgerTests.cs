using System.Security.Cryptography;
using Dps.MemoryEventLedger.Contracts;
using Npgsql;
using Xunit;

namespace Dps.MemoryEventLedger.Tests;

public sealed class PostgresMemoryEventLedgerTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DuplicateConflictCrossScopeConcurrencyAndChainAreAtomic()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(token);
        var eventId = Guid.Parse("60000000-0000-0000-0000-000000000001");
        var request = V2TestData.Request('a', eventId); var firstSignals = V2TestData.Signals("coffee");
        using var ledger = database.CreateLedger();
        var first = await ledger.PrepareAsync(database.AppendRequest(request, firstSignals), token);
        Assert.Equal(AppendDisposition.Inserted, (await ledger.AppendAsync(first, token)).Disposition);
        Assert.Equal(AppendDisposition.DuplicateNoOp, (await ledger.AppendAsync(first, token)).Disposition);

        var changedSignals = V2TestData.Signals("travel");
        var changed = await ledger.PrepareAsync(database.AppendRequest(request, changedSignals), token);
        Assert.Equal(AppendDisposition.Quarantined, (await ledger.AppendAsync(changed, token)).Disposition);
        var otherRequest = V2TestData.Request('b', eventId);
        var otherSignals = V2TestData.Signals("books");
        var other = await ledger.PrepareAsync(database.AppendRequest(otherRequest, otherSignals), token);
        Assert.Equal(AppendDisposition.Quarantined, (await ledger.AppendAsync(other, token)).Disposition);

        var concurrentId = Guid.Parse("60000000-0000-0000-0000-000000000002");
        var concurrentRequest = V2TestData.Request('c', concurrentId); var concurrentSignals = V2TestData.Signals("music");
        var workers = Enumerable.Range(0, 12).Select(async _ =>
        {
            using var worker = database.CreateLedger();
            var prepared = await worker.PrepareAsync(database.AppendRequest(concurrentRequest, concurrentSignals), token);
            return await worker.AppendAsync(prepared, token);
        });
        var results = await Task.WhenAll(workers);
        Assert.Single(results, result => result.Disposition == AppendDisposition.Inserted);
        Assert.Equal(11, results.Count(result => result.Disposition == AppendDisposition.DuplicateNoOp));
        Assert.Equal(2, await database.CountAsync("memory_events_v2", token));
        Assert.Equal(2, await database.CountAsync("outbox_v2", token));
        Assert.Equal(2, await database.CountAsync("quarantine_v2", token));
        Assert.True(await database.ChainIsContinuousAsync(request.SoulId, token));
        var replay = await ledger.ReadSoulEventsAsync(request.SoulId, token);
        var replayed = Assert.Single(replay);
        Assert.Equal(first.Event.EventId, replayed.Event.EventId);
        Assert.Equal(1, replayed.SoulSequence);
        Assert.Empty(await ledger.ReadSoulEventsAsync(otherRequest.SoulId, token));
    }

    [Theory]
    [InlineData(AppendStage.EventInserted)]
    [InlineData(AppendStage.OutboxInserted)]
    [InlineData(AppendStage.BeforeCommit)]
    [Trait("Category", "Integration")]
    public async Task CrashWindowRollsBackEventAndOutboxAndRetryRecovers(AppendStage stage)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(token);
        var request = V2TestData.Request('d', Guid.NewGuid()); var signals = V2TestData.Signals(); var injected = 0;
        using var failing = database.CreateLedger((current, _) =>
        {
            if (current == stage && Interlocked.Exchange(ref injected, 1) == 0) throw new InvalidOperationException("injected crash");
            return ValueTask.CompletedTask;
        });
        var prepared = await failing.PrepareAsync(database.AppendRequest(request, signals), token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.AppendAsync(prepared, token));
        Assert.Equal(0, await database.CountAsync("memory_events_v2", token));
        Assert.Equal(0, await database.CountAsync("outbox_v2", token));
        using var recovered = database.CreateLedger();
        var retry = await recovered.PrepareAsync(database.AppendRequest(request, signals), token);
        Assert.Equal(AppendDisposition.Inserted, (await recovered.AppendAsync(retry, token)).Disposition);
        Assert.Equal(1, await database.CountAsync("memory_events_v2", token));
        Assert.Equal(1, await database.CountAsync("outbox_v2", token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RuntimeRoleCannotWriteUpdateDeleteOrTruncateAndBadCapabilityIsRejected()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(token);
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync(token);
        foreach (var sql in new[]
        {
            $"INSERT INTO {database.SchemaName}.soul_heads_v2 VALUES ('soul_{new string('a', 64)}',0,repeat('0',64),clock_timestamp())",
            $"UPDATE {database.SchemaName}.memory_events_v2 SET occurred_at=occurred_at",
            $"DELETE FROM {database.SchemaName}.outbox_v2",
            $"TRUNCATE {database.SchemaName}.quarantine_v2"
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await using var bypass = new NpgsqlCommand(
            $"SELECT * FROM {database.SchemaName}.append_memory_event_v2(@cap,@event,@outbox,@json,@hash)", connection);
        bypass.Parameters.AddWithValue("cap", new string('0', 64)); bypass.Parameters.AddWithValue("event", Guid.NewGuid());
        bypass.Parameters.AddWithValue("outbox", Guid.NewGuid()); bypass.Parameters.AddWithValue("json", "{}"); bypass.Parameters.AddWithValue("hash", new string('0', 64));
        var bypassError = await Assert.ThrowsAsync<PostgresException>(() => bypass.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.InvalidAuthorizationSpecification, bypassError.SqlState);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CanonicalColumnsOutboxAndAppendOnlyTriggersRejectDirectTampering()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(token);
        var request = V2TestData.Request('e', Guid.NewGuid()); var signals = V2TestData.Signals();
        using var ledger = database.CreateLedger();
        var prepared = await ledger.PrepareAsync(database.AppendRequest(request, signals), token);
        await ledger.AppendAsync(prepared, token);
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync(token);

        await using (var update = new NpgsqlCommand($"UPDATE {database.SchemaName}.memory_events_v2 SET occurred_at=occurred_at", connection))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(token));
            Assert.Equal("P0001", error.SqlState);
        }
        await using (var truncate = new NpgsqlCommand($"TRUNCATE {database.SchemaName}.outbox_v2", connection))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync(token));
            Assert.Equal("P0001", error.SqlState);
        }
        await using (var mismatch = new NpgsqlCommand(
                         $"INSERT INTO {database.SchemaName}.memory_events_v2(event_id,soul_id,device_binding_id,platform_account_id,trace_id,idempotency_key,occurred_at,receipt_id,command_id,signed_receipt_sha256,content_digest,signals_digest,identity_resolution_sha256,identity_resolution_revision,identity_issuer,identity_audience,identity_key_role,identity_key_id,identity_trust_epoch,identity_revocation_epoch,identity_issued_at,identity_expires_at,result_issuer,result_audience,result_key_role,result_key_id,result_trust_epoch,result_revocation_epoch,result_issued_at,result_expires_at,soul_sequence,previous_chain_sha256,chain_sha256,payload_sha256,canonical_json,event_json) SELECT gen_random_uuid(),soul_id,device_binding_id,platform_account_id,trace_id,idempotency_key,occurred_at,gen_random_uuid(),command_id,signed_receipt_sha256,content_digest,signals_digest,identity_resolution_sha256,identity_resolution_revision,identity_issuer,identity_audience,identity_key_role,identity_key_id,identity_trust_epoch,identity_revocation_epoch,identity_issued_at,identity_expires_at,result_issuer,result_audience,result_key_role,result_key_id,result_trust_epoch,result_revocation_epoch,result_issued_at,result_expires_at,99,previous_chain_sha256,chain_sha256,payload_sha256,canonical_json,event_json FROM {database.SchemaName}.memory_events_v2 LIMIT 1",
                         connection))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => mismatch.ExecuteNonQueryAsync(token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await using (var crossSoulPrivacy = new NpgsqlCommand(
                         $"INSERT INTO {database.SchemaName}.privacy_tombstones_v2(tombstone_id,soul_id,target_event_id,authority_receipt_sha256,reason_sha256) VALUES (@id,@soul,@event,@authority,@reason)",
                         connection))
        {
            crossSoulPrivacy.Parameters.AddWithValue("id", Guid.NewGuid());
            crossSoulPrivacy.Parameters.AddWithValue("soul", "soul_" + new string('f', 64));
            crossSoulPrivacy.Parameters.AddWithValue("event", request.EventId);
            crossSoulPrivacy.Parameters.AddWithValue("authority", new string('a', 64));
            crossSoulPrivacy.Parameters.AddWithValue("reason", new string('b', 64));
            var error = await Assert.ThrowsAsync<PostgresException>(() => crossSoulPrivacy.ExecuteNonQueryAsync(token));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        Assert.Equal(1, await database.CountAsync("memory_events_v2", token));
        Assert.Equal(1, await database.CountAsync("outbox_v2", token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PrivacyCorrectionAndDeletionTablesHaveNoRuntimeWriteGrant()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(token);
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync(token);
        foreach (var table in new[] { "privacy_tombstones_v2", "correction_links_v2" })
        {
            await using var command = new NpgsqlCommand($"SELECT has_table_privilege(current_user,'{database.SchemaName}.{table}','INSERT')", connection);
            Assert.False((bool)(await command.ExecuteScalarAsync(token))!);
        }
    }
}

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _bootstrapConnectionString;
    private readonly string _databaseName;
    private readonly byte[] _runtimeCapability;
    private readonly TestTimeProvider _clock = new(V2TestData.Now);
    private readonly TestSoulAuthoritySource _soulSource = new() { Now = V2TestData.Now };
    private readonly TestResultAuthorityStateSource _resultState = new();
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private TestDatabase(string bootstrap, string databaseName, string schema, string adminRole, string runtimeRole,
        string adminConnection, string runtimeConnection, byte[] capability)
    {
        _bootstrapConnectionString = bootstrap; _databaseName = databaseName; SchemaName = schema; AdminRole = adminRole;
        RuntimeRole = runtimeRole; AdminConnectionString = adminConnection; RuntimeConnectionString = runtimeConnection; _runtimeCapability = capability;
    }

    public string SchemaName { get; }
    public string AdminRole { get; }
    public string RuntimeRole { get; }
    public string AdminConnectionString { get; }
    public string RuntimeConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(CancellationToken token)
    {
        var bootstrap = Environment.GetEnvironmentVariable("DPS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(bootstrap))
            throw new InvalidOperationException("INFRA_ERROR/NOT_RUN: DPS_TEST_POSTGRES bootstrap-admin DSN is required; mock PostgreSQL is forbidden.");
        string databaseName;
        await using (var connection = new NpgsqlConnection(bootstrap))
        {
            await connection.OpenAsync(token);
            await using var version = new NpgsqlCommand("SHOW server_version_num", connection);
            var number = (string?)await version.ExecuteScalarAsync(token);
            if (number != "180004") throw new InvalidOperationException($"INFRA_ERROR: PostgreSQL 18.4 required; found {number ?? "missing"}.");
            await using var database = new NpgsqlCommand("SELECT current_database()", connection);
            databaseName = (string)(await database.ExecuteScalarAsync(token))!;
        }

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schema = "dps_mem_v2_" + suffix; var adminRole = "dps_mem_admin_" + suffix; var runtimeRole = "dps_mem_runtime_" + suffix;
        var adminPassword = "A_" + Guid.NewGuid().ToString("N"); var runtimePassword = "R_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(bootstrap))
        {
            await connection.OpenAsync(token);
            await using var roles = new NpgsqlCommand(
                $"CREATE ROLE {adminRole} LOGIN PASSWORD {QuoteLiteral(adminPassword)}; CREATE ROLE {runtimeRole} LOGIN PASSWORD {QuoteLiteral(runtimePassword)}; GRANT CREATE ON DATABASE {QuoteIdentifier(databaseName)} TO {adminRole}", connection);
            await roles.ExecuteNonQueryAsync(token);
        }
        var baseBuilder = new NpgsqlConnectionStringBuilder(bootstrap);
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString) { Username = adminRole, Password = adminPassword };
        var runtimeBuilder = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString) { Username = runtimeRole, Password = runtimePassword };
        var capability = RandomNumberGenerator.GetBytes(32);
        var result = new TestDatabase(bootstrap, databaseName, schema, adminRole, runtimeRole, adminBuilder.ConnectionString, runtimeBuilder.ConnectionString, capability);
        using var ledger = result.CreateLedger();
        await ledger.InitializeAsync(token);
        return result;
    }

    public PostgresMemoryEventLedgerV2 CreateLedger(AppendFaultInjector? fault = null)
    {
        var options = new MemoryEventLedgerV2Options(AdminConnectionString, RuntimeConnectionString, SchemaName, AdminRole, RuntimeRole, _runtimeCapability);
        return new PostgresMemoryEventLedgerV2(options, new FixedSoulResolutionAuthorityV2(_soulSource, _clock),
            new FixedObservationReceiptAuthorityV2(_signer.ExportSubjectPublicKeyInfo(), _resultState, _clock), fault);
    }

    public MemoryAppendRequestV2 AppendRequest(SoulResolutionBindingRequestV2 request, IReadOnlyList<InterestSignalV2> signals) =>
        V2TestData.AppendRequest(_signer, request, signals);

    public async Task<long> CountAsync(string table, CancellationToken token)
    {
        var allowed = table switch { "memory_events_v2" => table, "outbox_v2" => table, "quarantine_v2" => table, _ => throw new ArgumentOutOfRangeException(nameof(table)) };
        await using var connection = new NpgsqlConnection(AdminConnectionString); await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {SchemaName}.{allowed}", connection);
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    public async Task<bool> ChainIsContinuousAsync(string soulId, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString); await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*)=0 FROM (SELECT soul_sequence,previous_chain_sha256,lag(chain_sha256,1,repeat('0',64)) OVER (ORDER BY soul_sequence) expected,row_number() OVER (ORDER BY soul_sequence) ordinal FROM {SchemaName}.memory_events_v2 WHERE soul_id=@soul) q WHERE previous_chain_sha256<>expected OR soul_sequence<>ordinal", connection);
        command.Parameters.AddWithValue("soul", soulId); return (bool)(await command.ExecuteScalarAsync(token))!;
    }

    public async ValueTask DisposeAsync()
    {
        _signer.Dispose(); CryptographicOperations.ZeroMemory(_runtimeCapability);
        await using var connection = new NpgsqlConnection(_bootstrapConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE; REVOKE CREATE ON DATABASE {QuoteIdentifier(_databaseName)} FROM {AdminRole}; DROP ROLE IF EXISTS {RuntimeRole}; DROP ROLE IF EXISTS {AdminRole}", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string QuoteLiteral(string value) => '\'' + value.Replace("'", "''", StringComparison.Ordinal) + '\'';
}
