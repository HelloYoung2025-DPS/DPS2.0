using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.MemoryEventLedger.Contracts;
using Dps.SoulRegistry.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.MemoryEventLedger;

public sealed record MemoryEventLedgerOptions(string ConnectionString, string SchemaName)
{
    private static readonly Regex SchemaPattern = new("^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(ConnectionString));
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || !SchemaPattern.IsMatch(SchemaName))
        {
            throw new ArgumentException("SchemaName must be a safe lowercase PostgreSQL identifier.", nameof(SchemaName));
        }
    }

    public override string ToString()
        => $"MemoryEventLedgerOptions {{ ConnectionString = [REDACTED], SchemaName = {SchemaName} }}";
}

public enum AppendDisposition
{
    Inserted,
    DuplicateNoOp,
    Quarantined
}

public enum AppendStage
{
    EventInserted,
    OutboxInserted,
    BeforeCommit
}

public sealed record AppendResult(
    AppendDisposition Disposition,
    Guid EventId,
    string PayloadSha256,
    long? SoulSequence,
    Guid? OutboxId);

public sealed record QuarantineRecord(
    Guid EventId,
    string IncomingSoulId,
    string ExistingSha256,
    string IncomingSha256,
    string Reason);

public delegate ValueTask AppendFaultInjector(AppendStage stage, CancellationToken cancellationToken);

public sealed class PostgresMemoryEventLedger
{
    private const string MigrationResourceSuffix = "001_create_memory_event_ledger.sql";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly MemoryEventLedgerOptions _options;
    private readonly AppendFaultInjector? _faultInjector;

    public PostgresMemoryEventLedger(
        MemoryEventLedgerOptions options,
        AppendFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _faultInjector = faultInjector;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(PostgresMemoryEventLedger).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(MigrationResourceSuffix, StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{MigrationResourceSuffix}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var migration = await reader.ReadToEndAsync(cancellationToken);
        migration = migration.Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(migration, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<AppendResult> AppendAsync(
        SoulResolved soul,
        MemoryEventV1 memoryEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(soul);
        ArgumentNullException.ThrowIfNull(memoryEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<AppendResult>(new NotSupportedException(
            "memory.event/v1 is quarantine-only. Public SoulResolved and observation.verified inputs cannot authorize an append."));
    }

    private static void ValidateSoulContext(SoulResolved soul, MemoryEventV1 memoryEvent)
    {
        ContractValidation.RequireMajor(soul.SchemaVersion, 1);
        ContractValidation.RequireExact(soul.ContractId, SoulResolved.CurrentContractId, nameof(soul.ContractId));
        ContractValidation.RequireExact(soul.ProducerModule, SoulResolved.CurrentProducerModule, nameof(soul.ProducerModule));
        ContractValidation.RequireExact(soul.PrivacyClass, SoulResolved.CurrentPrivacyClass, nameof(soul.PrivacyClass));
        ContractValidation.RequireSoulId(soul.SoulId, nameof(soul.SoulId));
        ContractValidation.RequireOpaqueId(soul.DeviceBindingId, "db_", nameof(soul.DeviceBindingId));
        ContractValidation.RequireOpaqueId(soul.PlatformAccountId, "pa_", nameof(soul.PlatformAccountId));
        ContractValidation.RequireTraceId(soul.TraceId, nameof(soul.TraceId));
        ContractValidation.RequireIdempotencyKey(soul.IdempotencyKey, nameof(soul.IdempotencyKey));
        ContractValidation.RequireUtc(soul.OccurredAt, nameof(soul.OccurredAt));
        ContractValidation.RequireText(soul.AliasKind, 32, nameof(soul.AliasKind));
        ContractValidation.RequireSha256(soul.AliasDigest, nameof(soul.AliasDigest));
        ContractValidation.RequireText(soul.AliasKeyId, 64, nameof(soul.AliasKeyId));

        if (!string.Equals(soul.SoulId, memoryEvent.SoulId, StringComparison.Ordinal) ||
            !string.Equals(soul.DeviceBindingId, memoryEvent.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(soul.PlatformAccountId, memoryEvent.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(soul.TraceId, memoryEvent.TraceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SOUL-ISO-001: Soul, device, account, and trace scope must remain unchanged.");
        }
    }

    public async Task<IReadOnlyList<MemoryEventV1>> ReadSoulEventsAsync(
        string soulId,
        CancellationToken cancellationToken = default)
    {
        ContractValidation.RequireSoulId(soulId, nameof(soulId));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT event_json::text FROM {_options.SchemaName}.memory_events WHERE soul_id = @soul_id ORDER BY soul_sequence",
            connection);
        command.Parameters.AddWithValue("soul_id", soulId);

        var results = new List<MemoryEventV1>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var memoryEvent = JsonSerializer.Deserialize<MemoryEventV1>(reader.GetString(0), SerializerOptions)
                ?? throw new InvalidOperationException("A stored memory event could not be deserialized.");
            memoryEvent.Validate();

            if (memoryEvent.SoulId != soulId)
            {
                throw new InvalidOperationException("SOUL-ISO-001: a scoped query returned a different Soul.");
            }

            results.Add(memoryEvent);
        }

        return results;
    }

    public Task<long> CountEventsAsync(string? soulId = null, CancellationToken cancellationToken = default)
        => CountAsync("memory_events", soulId, cancellationToken);

    public Task<long> CountOutboxAsync(string? soulId = null, CancellationToken cancellationToken = default)
        => CountAsync("outbox", soulId, cancellationToken);

    public Task<long> CountQuarantineAsync(CancellationToken cancellationToken = default)
        => CountAsync("quarantine", null, cancellationToken);

    public async Task<IReadOnlyList<MemoryOutboxV1>> ReadPendingOutboxAsync(
        string soulId,
        CancellationToken cancellationToken = default)
    {
        ContractValidation.RequireSoulId(soulId, nameof(soulId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT outbox_id, event_id, soul_id, device_binding_id, platform_account_id,
                   trace_id, idempotency_key, occurred_at, topic, payload_sha256
            FROM {_options.SchemaName}.outbox
            WHERE soul_id = @soul_id AND dispatched_at IS NULL
            ORDER BY created_at, outbox_id
            """,
            connection);
        command.Parameters.AddWithValue("soul_id", soulId);

        var results = new List<MemoryOutboxV1>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var record = new MemoryOutboxV1(
                MemoryOutboxV1.CurrentSchemaVersion,
                MemoryOutboxV1.CurrentContractId,
                MemoryOutboxV1.CurrentProducerModule,
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7).ToUniversalTime(),
                "personal",
                reader.GetString(8),
                reader.GetString(9));
            record.Validate();
            results.Add(record);
        }

        return results;
    }

    public async Task<IReadOnlyList<QuarantineRecord>> ReadQuarantineAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT event_id, incoming_soul_id, existing_sha256, incoming_sha256, reason FROM {_options.SchemaName}.quarantine ORDER BY created_at, quarantine_id",
            connection);
        var records = new List<QuarantineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new QuarantineRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return records;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);

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

    private static async Task AcquireSoulLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string soulId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@soul_id, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> GetNextSoulSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string soulId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT COALESCE(MAX(soul_sequence), 0) + 1 FROM {_options.SchemaName}.memory_events WHERE soul_id = @soul_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return a Soul sequence."));
    }

    private async Task<bool> TryInsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryEventV1 memoryEvent,
        string canonicalJson,
        string payloadSha256,
        long soulSequence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.memory_events
                (event_id, soul_id, device_binding_id, platform_account_id, soul_sequence,
                 occurred_at, payload_sha256, canonical_json, event_json)
            VALUES
                (@event_id, @soul_id, @device_binding_id, @platform_account_id, @soul_sequence,
                 @occurred_at, @payload_sha256, @canonical_json, @event_json)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", memoryEvent.EventId);
        command.Parameters.AddWithValue("soul_id", memoryEvent.SoulId);
        command.Parameters.AddWithValue("device_binding_id", memoryEvent.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", memoryEvent.PlatformAccountId);
        command.Parameters.AddWithValue("soul_sequence", soulSequence);
        command.Parameters.AddWithValue("occurred_at", memoryEvent.OccurredAt);
        command.Parameters.AddWithValue("payload_sha256", payloadSha256);
        command.Parameters.AddWithValue("canonical_json", canonicalJson);
        command.Parameters.AddWithValue("event_json", NpgsqlDbType.Jsonb, canonicalJson);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryEventV1 memoryEvent,
        Guid outboxId,
        string canonicalJson,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.outbox
                (outbox_id, event_id, soul_id, device_binding_id, platform_account_id,
                 trace_id, idempotency_key, occurred_at, topic, payload_sha256,
                 canonical_payload_json, payload_json)
            VALUES
                (@outbox_id, @event_id, @soul_id, @device_binding_id, @platform_account_id,
                 @trace_id, @idempotency_key, @occurred_at, @topic, @payload_sha256,
                 @canonical_payload_json, @payload_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("event_id", memoryEvent.EventId);
        command.Parameters.AddWithValue("soul_id", memoryEvent.SoulId);
        command.Parameters.AddWithValue("device_binding_id", memoryEvent.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", memoryEvent.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", memoryEvent.TraceId);
        command.Parameters.AddWithValue("idempotency_key", memoryEvent.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", memoryEvent.OccurredAt);
        command.Parameters.AddWithValue("topic", MemoryOutboxV1.CurrentContractId.Replace("outbox", "event", StringComparison.Ordinal));
        command.Parameters.AddWithValue("payload_sha256", payloadSha256);
        command.Parameters.AddWithValue("canonical_payload_json", canonicalJson);
        command.Parameters.AddWithValue("payload_json", NpgsqlDbType.Jsonb, canonicalJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> ReadExistingHashAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT payload_sha256 FROM {_options.SchemaName}.memory_events WHERE event_id = @event_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryEventV1 memoryEvent,
        string canonicalJson,
        string existingSha256,
        string incomingSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.quarantine
                (quarantine_id, event_id, incoming_soul_id, existing_sha256, incoming_sha256, incoming_json, reason)
            VALUES
                (@quarantine_id, @event_id, @incoming_soul_id, @existing_sha256, @incoming_sha256, @incoming_json, @reason)
            ON CONFLICT (event_id, incoming_sha256) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("quarantine_id", Guid.NewGuid());
        command.Parameters.AddWithValue("event_id", memoryEvent.EventId);
        command.Parameters.AddWithValue("incoming_soul_id", memoryEvent.SoulId);
        command.Parameters.AddWithValue("existing_sha256", existingSha256);
        command.Parameters.AddWithValue("incoming_sha256", incomingSha256);
        command.Parameters.AddWithValue("incoming_json", NpgsqlDbType.Jsonb, canonicalJson);
        command.Parameters.AddWithValue("reason", "event_id_payload_hash_conflict");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(string table, string? soulId, CancellationToken cancellationToken)
    {
        var allowedTable = table switch
        {
            "memory_events" => "memory_events",
            "outbox" => "outbox",
            "quarantine" => "quarantine",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        var predicate = soulId is not null ? " WHERE soul_id = @soul_id" : string.Empty;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {_options.SchemaName}.{allowedTable}{predicate}",
            connection);

        if (soulId is not null)
        {
            command.Parameters.AddWithValue("soul_id", soulId);
        }

        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return a row count."));
    }

    private async ValueTask InjectAsync(AppendStage stage, CancellationToken cancellationToken)
    {
        if (_faultInjector is not null)
        {
            await _faultInjector(stage, cancellationToken);
        }
    }

    private static Guid CreateDeterministicGuid(Guid input, string purpose)
    {
        var inputBytes = input.ToByteArray();
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var combined = new byte[inputBytes.Length + purposeBytes.Length];
        inputBytes.CopyTo(combined, 0);
        purposeBytes.CopyTo(combined, inputBytes.Length);
        var hash = SHA256.HashData(combined);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }
}
