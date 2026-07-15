using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.AuditMetrics.Contracts;
using Dps.CommandOrchestrator.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.AuditMetrics;

public sealed record AuditMetricsPostgresOptions(
    string ConnectionString,
    string SchemaName,
    string RuntimeRoleName)
{
    private static readonly Regex SafeIdentifier = new(
        "^[a-z][a-z0-9_]{0,62}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(ConnectionString));
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || !SafeIdentifier.IsMatch(SchemaName))
        {
            throw new ArgumentException("PostgreSQL schema name is not allowlisted.", nameof(SchemaName));
        }

        if (string.IsNullOrWhiteSpace(RuntimeRoleName) || !SafeIdentifier.IsMatch(RuntimeRoleName))
        {
            throw new ArgumentException("PostgreSQL runtime role is not allowlisted.", nameof(RuntimeRoleName));
        }
    }
}

public sealed record AuditQuarantineRecord(
    Guid QuarantineId,
    Guid IncomingAuditEventId,
    Guid ExistingAuditEventId,
    string ConflictKeySha256,
    string ExistingRecordSha256,
    string IncomingRecordSha256,
    string ScopeSha256,
    string IdempotencySha256,
    string Reason);

public enum AuditAppendStage
{
    EventInserted,
    QuarantineInserted,
    BeforeCommit
}

public delegate ValueTask AuditAppendFaultInjector(
    AuditAppendStage stage,
    CancellationToken cancellationToken);

public sealed class PostgresAuditMetrics
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly AuditMetricsPostgresOptions _options;
    private readonly EcdsaAuditRelayAuthorizationVerifier _authorizationVerifier;
    private readonly SignedAuditRelayTrustStateReader _trustStateReader;
    private readonly TimeProvider _timeProvider;
    private readonly AuditAppendFaultInjector? _faultInjector;

    public PostgresAuditMetrics(
        AuditMetricsPostgresOptions options,
        EcdsaAuditRelayAuthorizationVerifier authorizationVerifier,
        SignedAuditRelayTrustStateReader trustStateReader,
        TimeProvider timeProvider,
        AuditAppendFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizationVerifier);
        ArgumentNullException.ThrowIfNull(trustStateReader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _options = options;
        _authorizationVerifier = authorizationVerifier;
        _trustStateReader = trustStateReader;
        _timeProvider = timeProvider;
        _faultInjector = faultInjector;
    }

    public async Task<AuditAppendResult> AppendReceiptAsync(
        CommandReceiptV1 receipt,
        AuditRelayEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        // Authentication and exact receipt binding intentionally complete before
        // a database transaction exists. An invalid signature therefore has no
        // path to either the business table or the conflict quarantine.
        var verificationNow = GetUtcNow();
        var candidate = AuditReceiptProcessor.VerifyAndCreate(
            _authorizationVerifier,
            receipt,
            envelope,
            verificationNow);
        await VerifyCurrentTrustStateAsync(candidate, verificationNow, cancellationToken);
        var eventJson = JsonSerializer.Serialize(candidate.AuditEvent, SerializerOptions);
        var scopeSha256 = ComputeScopeSha256(candidate.AuditEvent);
        var idempotencySha256 = ComputeScopeIdempotencySha256(candidate.AuditEvent);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await ApplyTransactionTimeoutsAsync(connection, transaction, cancellationToken);
        await AcquireTrustStateSharedLockAsync(connection, transaction, cancellationToken);

        var writeNow = GetUtcNow();
        if (envelope.ExpiresAt < writeNow)
        {
            throw new UnauthorizedAccessException("Relay authorization expired before the audit transaction could start.");
        }
        await VerifyCurrentTrustStateAsync(
            candidate,
            writeNow,
            connection,
            transaction,
            cancellationToken);

        await AcquireAppendLocksAsync(
            connection,
            transaction,
            candidate.AuditEvent.AuditEventId,
            idempotencySha256,
            cancellationToken);

        var conflicts = await ReadConflictsAsync(
            connection,
            transaction,
            candidate.AuditEvent,
            cancellationToken);

        if (conflicts.Count == 1
            && conflicts[0].EventIdMatch
            && conflicts[0].ScopedIdempotencyMatch
            && AuditDigest.FixedEquals(conflicts[0].RecordSha256, candidate.RecordSha256))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AuditAppendResult(
                AuditAppendDisposition.DuplicateNoOp,
                candidate.AuditEvent.AuditEventId);
        }

        if (conflicts.Count > 0)
        {
            foreach (var conflict in conflicts)
            {
                var reason = (conflict.EventIdMatch, conflict.ScopedIdempotencyMatch) switch
                {
                    (true, true) => "event_id_and_scoped_idempotency_digest_conflict",
                    (true, false) => "event_id_digest_conflict",
                    (false, true) => "scoped_idempotency_digest_conflict",
                    _ => throw new InvalidOperationException("Conflict lookup returned an unrelated audit event.")
                };
                await InsertQuarantineAsync(
                    connection,
                    transaction,
                    candidate,
                    conflict,
                    scopeSha256,
                    idempotencySha256,
                    reason,
                    cancellationToken);
            }

            await InjectAsync(AuditAppendStage.QuarantineInserted, cancellationToken);
            await InjectAsync(AuditAppendStage.BeforeCommit, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AuditAppendResult(AuditAppendDisposition.Quarantined, null);
        }

        await InsertEventAsync(
            connection,
            transaction,
            candidate,
            eventJson,
            cancellationToken);
        await InjectAsync(AuditAppendStage.EventInserted, cancellationToken);
        await InjectAsync(AuditAppendStage.BeforeCommit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuditAppendResult(
            AuditAppendDisposition.Inserted,
            candidate.AuditEvent.AuditEventId);
    }

    public async Task<IReadOnlyList<AuditEventV1>> ReadScopeAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        AuditContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT event_json::text, source_receipt_sha256, release_bom_sha256,
                   event_integrity_sha256, record_sha256
            FROM {_options.SchemaName}.audit_events
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            ORDER BY occurred_at, audit_event_id
            """,
            connection);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);

        var events = new List<AuditEventV1>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var auditEvent = JsonSerializer.Deserialize<AuditEventV1>(reader.GetString(0), SerializerOptions)
                ?? throw new InvalidOperationException("Stored audit event could not be deserialized.");
            auditEvent.Validate();
            if (!string.Equals(auditEvent.SoulId, soulId, StringComparison.Ordinal)
                || !string.Equals(auditEvent.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
                || !string.Equals(auditEvent.PlatformAccountId, platformAccountId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SOUL-ISO-001: scoped PostgreSQL query returned another identity scope.");
            }

            var sourceReceiptSha256 = reader.GetString(1);
            var releaseBomSha256 = reader.GetString(2);
            var storedEventIntegritySha256 = reader.GetString(3);
            var storedRecordSha256 = reader.GetString(4);
            var computedEventIntegritySha256 = AuditEventIntegrityBinding.ComputeSha256(
                auditEvent,
                sourceReceiptSha256);
            var computedRecordSha256 = AuditPersistedRecordBinding.ComputeSha256(
                computedEventIntegritySha256,
                releaseBomSha256);
            if (!AuditDigest.FixedEquals(storedEventIntegritySha256, computedEventIntegritySha256)
                || !AuditDigest.FixedEquals(storedRecordSha256, computedRecordSha256))
            {
                throw new InvalidOperationException("Stored audit integrity digest does not match its immutable event.");
            }

            events.Add(auditEvent);
        }

        return events;
    }

    public Task<long> CountEventsAsync(CancellationToken cancellationToken = default)
        => CountAsync("audit_events", cancellationToken);

    public Task<long> CountQuarantineAsync(CancellationToken cancellationToken = default)
        => CountAsync("audit_quarantine", cancellationToken);

    public async Task<IReadOnlyList<AuditQuarantineRecord>> ReadQuarantineAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        AuditContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId);
        var scopeSha256 = ComputeScopeSha256(soulId, deviceBindingId, platformAccountId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT quarantine_id, incoming_audit_event_id, existing_audit_event_id,
                   conflict_key_sha256, existing_record_sha256, incoming_record_sha256,
                   scope_sha256, idempotency_sha256, reason
            FROM {_options.SchemaName}.audit_quarantine
            WHERE scope_sha256 = @scope_sha256
            ORDER BY created_at, quarantine_id
            """,
            connection);
        command.Parameters.AddWithValue("scope_sha256", scopeSha256);
        var records = new List<AuditQuarantineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new AuditQuarantineRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return records;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await AuditPostgresRuntimeConnection.OpenVerifiedAsync(_options, cancellationToken);

    private DateTimeOffset GetUtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        AuditContractGuard.RequireUtc(now, nameof(TimeProvider));
        return now;
    }

    private async ValueTask VerifyCurrentTrustStateAsync(
        VerifiedAuditCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var trustState = await _trustStateReader.ReadCurrentAsync(now, cancellationToken);
        if (!string.Equals(
                trustState.RelayKeyStatus,
                AuditRelayTrustStateEnvelope.Active,
                StringComparison.Ordinal)
            || !AuditDigest.FixedEquals(candidate.ReleaseBomSha256, trustState.ActiveReleaseBomSha256)
            || !AuditDigest.FixedEquals(_authorizationVerifier.PublicKeySha256, trustState.RelayPublicKeySha256))
        {
            throw new UnauthorizedAccessException("Relay authorization is not bound to the current Release BOM and non-revoked relay key.");
        }
    }

    private async ValueTask VerifyCurrentTrustStateAsync(
        VerifiedAuditCandidate candidate,
        DateTimeOffset now,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var trustState = await _trustStateReader.ReadCurrentAsync(
            now,
            connection,
            transaction,
            cancellationToken);
        if (!string.Equals(
                trustState.RelayKeyStatus,
                AuditRelayTrustStateEnvelope.Active,
                StringComparison.Ordinal)
            || !AuditDigest.FixedEquals(candidate.ReleaseBomSha256, trustState.ActiveReleaseBomSha256)
            || !AuditDigest.FixedEquals(_authorizationVerifier.PublicKeySha256, trustState.RelayPublicKeySha256))
        {
            throw new UnauthorizedAccessException("Relay authorization is not bound to the transaction-locked Release BOM and non-revoked relay key.");
        }
    }

    private static async Task AcquireTrustStateSharedLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock_shared(hashtextextended('dps.audit.relay-trust-state/v1', 0))",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireAppendLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid auditEventId,
        string idempotencySha256,
        CancellationToken cancellationToken)
    {
        var lockKeys = new[]
        {
            $"audit-event:{auditEventId:N}",
            $"audit-idempotency:{idempotencySha256}"
        };
        Array.Sort(lockKeys, StringComparer.Ordinal);
        foreach (var lockKey in lockKeys)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
                connection,
                transaction);
            command.Parameters.AddWithValue("lock_key", lockKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ApplyTransactionTimeoutsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET LOCAL lock_timeout = '5000ms'; SET LOCAL statement_timeout = '5000ms'",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<StoredConflict>> ReadConflictsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditEventV1 auditEvent,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT audit_event_id,
                   record_sha256,
                   audit_event_id = @audit_event_id AS event_id_match,
                   (soul_id = @soul_id
                    AND device_binding_id = @device_binding_id
                    AND platform_account_id = @platform_account_id
                    AND idempotency_key = @idempotency_key) AS scoped_idempotency_match
            FROM {_options.SchemaName}.audit_events
            WHERE audit_event_id = @audit_event_id
               OR (soul_id = @soul_id
                   AND device_binding_id = @device_binding_id
                   AND platform_account_id = @platform_account_id
                   AND idempotency_key = @idempotency_key)
            ORDER BY audit_event_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_event_id", auditEvent.AuditEventId);
        command.Parameters.AddWithValue("soul_id", auditEvent.SoulId);
        command.Parameters.AddWithValue("device_binding_id", auditEvent.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", auditEvent.PlatformAccountId);
        command.Parameters.AddWithValue("idempotency_key", auditEvent.IdempotencyKey);

        var conflicts = new List<StoredConflict>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conflicts.Add(new StoredConflict(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)));
        }

        return conflicts;
    }

    private async Task InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VerifiedAuditCandidate candidate,
        string eventJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.audit_events
                (audit_event_id, subject_id, source_receipt_id,
                 soul_id, device_binding_id, platform_account_id,
                 trace_id, idempotency_key, occurred_at,
                 outcome, result_code, verification_class, evidence_digest,
                 source_receipt_sha256, release_bom_sha256,
                 event_integrity_sha256, record_sha256, event_json)
            VALUES
                (@audit_event_id, @subject_id, @source_receipt_id,
                 @soul_id, @device_binding_id, @platform_account_id,
                 @trace_id, @idempotency_key, @occurred_at,
                 @outcome, @result_code, @verification_class, @evidence_digest,
                 @source_receipt_sha256, @release_bom_sha256,
                 @event_integrity_sha256, @record_sha256, @event_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_event_id", candidate.AuditEvent.AuditEventId);
        command.Parameters.AddWithValue("subject_id", candidate.AuditEvent.SubjectId);
        command.Parameters.AddWithValue("source_receipt_id", candidate.SourceReceiptId);
        command.Parameters.AddWithValue("soul_id", candidate.AuditEvent.SoulId);
        command.Parameters.AddWithValue("device_binding_id", candidate.AuditEvent.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", candidate.AuditEvent.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", candidate.AuditEvent.TraceId);
        command.Parameters.AddWithValue("idempotency_key", candidate.AuditEvent.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", candidate.AuditEvent.OccurredAt);
        command.Parameters.AddWithValue("outcome", candidate.AuditEvent.Outcome);
        command.Parameters.AddWithValue("result_code", candidate.AuditEvent.Labels["result_code"]);
        command.Parameters.AddWithValue("verification_class", candidate.AuditEvent.Labels["verification_class"]);
        command.Parameters.AddWithValue("evidence_digest", candidate.AuditEvent.EvidenceDigest);
        command.Parameters.AddWithValue("source_receipt_sha256", candidate.SourceReceiptSha256);
        command.Parameters.AddWithValue("release_bom_sha256", candidate.ReleaseBomSha256);
        command.Parameters.AddWithValue("event_integrity_sha256", candidate.EventIntegritySha256);
        command.Parameters.AddWithValue("record_sha256", candidate.RecordSha256);
        command.Parameters.AddWithValue("event_json", NpgsqlDbType.Jsonb, eventJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VerifiedAuditCandidate candidate,
        StoredConflict conflict,
        string scopeSha256,
        string idempotencySha256,
        string reason,
        CancellationToken cancellationToken)
    {
        var conflictKeySha256 = ComputeConflictKeySha256(
            candidate.AuditEvent.AuditEventId,
            conflict.AuditEventId,
            candidate.RecordSha256,
            conflict.RecordSha256);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.audit_quarantine
                (quarantine_id, incoming_audit_event_id, existing_audit_event_id,
                 conflict_key_sha256, existing_record_sha256, incoming_record_sha256,
                 scope_sha256, idempotency_sha256, reason)
            VALUES
                (@quarantine_id, @incoming_audit_event_id, @existing_audit_event_id,
                 @conflict_key_sha256, @existing_record_sha256, @incoming_record_sha256,
                 @scope_sha256, @idempotency_sha256, @reason)
            ON CONFLICT (conflict_key_sha256, existing_record_sha256, incoming_record_sha256, reason)
                DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("quarantine_id", Guid.NewGuid());
        command.Parameters.AddWithValue("incoming_audit_event_id", candidate.AuditEvent.AuditEventId);
        command.Parameters.AddWithValue("existing_audit_event_id", conflict.AuditEventId);
        command.Parameters.AddWithValue("conflict_key_sha256", conflictKeySha256);
        command.Parameters.AddWithValue("existing_record_sha256", conflict.RecordSha256);
        command.Parameters.AddWithValue("incoming_record_sha256", candidate.RecordSha256);
        command.Parameters.AddWithValue("scope_sha256", scopeSha256);
        command.Parameters.AddWithValue("idempotency_sha256", idempotencySha256);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
    {
        if (tableName is not ("audit_events" or "audit_quarantine"))
        {
            throw new ArgumentOutOfRangeException(nameof(tableName));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {_options.SchemaName}.{tableName}",
            connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return an audit row count."));
    }

    private static string ComputeScopeSha256(AuditEventV1 auditEvent)
        => ComputeScopeSha256(
            auditEvent.SoulId,
            auditEvent.DeviceBindingId,
            auditEvent.PlatformAccountId);

    private static string ComputeScopeSha256(
        string soulId,
        string deviceBindingId,
        string platformAccountId)
        => ComputeSha256($"dps.audit.scope/v1\n{soulId}\n{deviceBindingId}\n{platformAccountId}");

    private static string ComputeScopeIdempotencySha256(AuditEventV1 auditEvent)
    {
        var canonical = AuditCanonicalEncoding.ScopeIdempotency(
            auditEvent.SoulId,
            auditEvent.DeviceBindingId,
            auditEvent.PlatformAccountId,
            auditEvent.IdempotencyKey);
        try { return Convert.ToHexStringLower(SHA256.HashData(canonical)); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static string ComputeConflictKeySha256(
        Guid incomingAuditEventId,
        Guid existingAuditEventId,
        string incomingRecordSha256,
        string existingRecordSha256)
        => ComputeSha256(
            $"dps.audit.quarantine-conflict/v1\n{incomingAuditEventId:N}\n{existingAuditEventId:N}\n{incomingRecordSha256}\n{existingRecordSha256}");

    private static string ComputeSha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async ValueTask InjectAsync(
        AuditAppendStage stage,
        CancellationToken cancellationToken)
    {
        if (_faultInjector is not null)
        {
            await _faultInjector(stage, cancellationToken);
        }
    }

    private sealed record StoredConflict(
        Guid AuditEventId,
        string RecordSha256,
        bool EventIdMatch,
        bool ScopedIdempotencyMatch);
}
