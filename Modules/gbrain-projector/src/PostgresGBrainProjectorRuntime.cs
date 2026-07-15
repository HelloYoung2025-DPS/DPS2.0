using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.GBrainProjector.Contracts;
using Dps.InterestReducer.Contracts;
using Dps.MemoryEventLedger.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.GBrainProjector;

public sealed record GBrainProjectorPostgresOptions(string ConnectionString, string SchemaName)
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

    public override string ToString() =>
        $"GBrainProjectorPostgresOptions {{ ConnectionString = [REDACTED], SchemaName = {SchemaName} }}";
}

public enum RenderedRevisionDisposition
{
    Inserted,
    DuplicateNoOp
}

public sealed record RenderedRevisionResult(
    RenderedRevisionDisposition Disposition,
    string SoulId,
    string SourceId,
    string ProjectionRevision,
    string ProjectionChecksum);

public sealed record RenderAndAppendResult(
    GBrainProjectionV2 Projection,
    RenderedRevisionResult Persistence);

public sealed class GBrainProjectorPostgresRuntime
{
    private readonly PostgresGBrainProjectorStore _store;
    private readonly GBrainSourceBindingAuthority _authority;
    private readonly GBrainProjectionRenderer _renderer;
    private int _initialized;

    public GBrainProjectorPostgresRuntime(GBrainProjectorPostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = new PostgresGBrainProjectorStore(options);
        _authority = new GBrainSourceBindingAuthority(
            _store,
            TimeProvider.System);
        _renderer = new GBrainProjectionRenderer(_authority);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.VerifySchemaAsync(cancellationToken);
        Volatile.Write(ref _initialized, 1);
    }

    public Task<VerifiedGBrainSourceBinding> ResolveSourceBindingAsync(
        string soulId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _authority.ResolveAsync(soulId, cancellationToken);
    }

    public GBrainProjectionV2 Render(
        VerifiedGBrainSourceBinding sourceBinding,
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 interestSnapshot)
    {
        EnsureInitialized();
        return _renderer.Render(sourceBinding, memoryEvents, interestSnapshot);
    }

    public async Task<RenderAndAppendResult> RenderAndAppendAsync(
        IReadOnlyList<MemoryEventV1> memoryEvents,
        InterestSnapshotV1 interestSnapshot,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(interestSnapshot);
        interestSnapshot.Validate();
        PostgresTimestampPrecision.Require(interestSnapshot.AsOf, nameof(interestSnapshot.AsOf));
        var capability = await _authority.ResolveAsync(interestSnapshot.SoulId, cancellationToken);
        var projection = _renderer.Render(capability, memoryEvents, interestSnapshot);
        var persisted = await _store.AppendRenderedRevisionAsync(
            _authority.ReadVerified(capability),
            projection,
            cancellationToken);
        return new RenderAndAppendResult(projection, persisted);
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 1)
        {
            throw new InvalidOperationException(
                "The PostgreSQL runtime must attest its schema and least-privilege runtime identity before use.");
        }
    }
}

internal sealed class PostgresGBrainProjectorStore : ISourceBindingStore
{
    private readonly GBrainProjectorPostgresOptions _options;

    internal PostgresGBrainProjectorStore(GBrainProjectorPostgresOptions options)
    {
        _options = options;
    }

    internal async Task VerifySchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await PostgresGBrainProjectorSchemaVerifier.VerifyRuntimeAsync(
            connection,
            _options.SchemaName,
            cancellationToken);
    }

    public async Task<GBrainSourceBindingV1> ResolveOrAllocateAsync(
        string soulId,
        long maximumNonce,
        DateTimeOffset allocatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireSoulLockAsync(connection, transaction, soulId, cancellationToken);
        var existing = await ReadBindingAsync(connection, transaction, soulId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        for (long nonce = 0; nonce <= maximumNonce; nonce++)
        {
            var sourceId = GBrainSourceIdCandidates.Compute(soulId, nonce);
            InMemorySourceBindingStore.ValidateCandidate(sourceId);
            var binding = GBrainSourceBindingV1.Create(soulId, sourceId, nonce, allocatedAt);
            if (await TryInsertBindingAsync(connection, transaction, binding, cancellationToken))
            {
                var inserted = await ReadBindingAsync(connection, transaction, soulId, cancellationToken)
                    ?? throw new SourceBindingIntegrityException(
                        "A newly inserted Source binding could not be read back from PostgreSQL.");
                if (!string.Equals(
                        GBrainSourceBindingCanonicalizer.Serialize(inserted),
                        GBrainSourceBindingCanonicalizer.Serialize(binding),
                        StringComparison.Ordinal))
                {
                    throw new SourceBindingIntegrityException(
                        "A newly inserted Source binding did not survive exact PostgreSQL read-back verification.");
                }

                await transaction.CommitAsync(cancellationToken);
                return inserted;
            }

            existing = await ReadBindingAsync(connection, transaction, soulId, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
        }

        await InsertQuarantineAsync(connection, transaction, soulId, maximumNonce, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        throw new SourceBindingNonceExhaustedException(soulId, maximumNonce);
    }

    internal async Task<RenderedRevisionResult> AppendRenderedRevisionAsync(
        GBrainSourceBindingV1 binding,
        GBrainProjectionV2 projection,
        CancellationToken cancellationToken)
    {
        binding.Validate();
        projection.Validate();
        PostgresTimestampPrecision.Require(binding.AllocatedAt, nameof(binding.AllocatedAt));
        PostgresTimestampPrecision.Require(projection.SourceBindingAllocatedAt, nameof(projection.SourceBindingAllocatedAt));
        PostgresTimestampPrecision.Require(projection.OccurredAt, nameof(projection.OccurredAt));
        if (!string.Equals(binding.SoulId, projection.SoulId, StringComparison.Ordinal) ||
            !string.Equals(binding.SourceId, projection.SourceId, StringComparison.Ordinal) ||
            !string.Equals(binding.BindingRevision, projection.SourceBindingRevision, StringComparison.Ordinal) ||
            !string.Equals(binding.BindingChecksum, projection.SourceBindingChecksum, StringComparison.Ordinal))
        {
            throw new ProjectionScopeMismatchException();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireSoulLockAsync(connection, transaction, binding.SoulId, cancellationToken);
        var storedBinding = await ReadBindingAsync(connection, transaction, binding.SoulId, cancellationToken)
            ?? throw new SourceBindingIntegrityException("Projection cannot be persisted without its durable Source binding.");
        if (!string.Equals(
                GBrainSourceBindingCanonicalizer.Serialize(storedBinding),
                GBrainSourceBindingCanonicalizer.Serialize(binding),
                StringComparison.Ordinal))
        {
            throw new SourceBindingIntegrityException("Persisted Source binding differs from the verified capability.");
        }

        var current = await ReadCurrentProjectionAsync(connection, transaction, binding.SoulId, cancellationToken);
        var decision = ProjectionRevisionGuard.Evaluate(current, projection);
        if (decision == ProjectionRevisionDecision.Duplicate)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result(RenderedRevisionDisposition.DuplicateNoOp, projection);
        }

        if (await TryInsertProjectionAsync(connection, transaction, projection, cancellationToken))
        {
            var inserted = await ReadProjectionByRevisionAsync(
                connection, transaction, projection.SoulId, projection.ProjectionRevision, cancellationToken)
                ?? throw new ProjectionInputConflictException(
                    "A newly inserted projection could not be read back from PostgreSQL.");
            if (!string.Equals(
                    GBrainProjectionV2Canonicalizer.Serialize(inserted),
                    GBrainProjectionV2Canonicalizer.Serialize(projection),
                    StringComparison.Ordinal))
            {
                throw new ProjectionInputConflictException(
                    "A newly inserted projection did not survive exact PostgreSQL read-back verification.");
            }

            await transaction.CommitAsync(cancellationToken);
            return Result(RenderedRevisionDisposition.Inserted, inserted);
        }

        var duplicate = await ReadProjectionByRevisionAsync(
            connection, transaction, projection.SoulId, projection.ProjectionRevision, cancellationToken)
            ?? throw new ProjectionInputConflictException("A conflicting projection disappeared during append.");
        if (!string.Equals(
                GBrainProjectionV2Canonicalizer.Serialize(duplicate),
                GBrainProjectionV2Canonicalizer.Serialize(projection),
                StringComparison.Ordinal))
        {
            throw new ProjectionInputConflictException("Projection revision was reused for different canonical content.");
        }

        await transaction.CommitAsync(cancellationToken);
        return Result(RenderedRevisionDisposition.DuplicateNoOp, projection);
    }

    private static RenderedRevisionResult Result(RenderedRevisionDisposition disposition, GBrainProjectionV2 value) =>
        new(disposition, value.SoulId, value.SourceId, value.ProjectionRevision, value.ProjectionChecksum);

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

    private async Task<GBrainSourceBindingV1?> ReadBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string soulId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT soul_id, source_id, algorithm, nonce, soul_hash::text, allocated_at,
                   binding_revision::text, binding_checksum::text,
                   canonical_json, binding_json::text
            FROM {_options.SchemaName}.source_bindings
            WHERE soul_id = @soul_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return DeserializeBinding(
            reader.GetString(8),
            reader.GetString(9),
            soulId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            ReadUtcTimestamp(reader, 5),
            reader.GetString(6),
            reader.GetString(7));
    }

    private async Task<bool> TryInsertBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GBrainSourceBindingV1 binding,
        CancellationToken cancellationToken)
    {
        var canonical = GBrainSourceBindingCanonicalizer.Serialize(binding);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.source_bindings
                (soul_id, source_id, algorithm, nonce, soul_hash, allocated_at,
                 binding_revision, binding_checksum, canonical_json, binding_json)
            VALUES
                (@soul_id, @source_id, @algorithm, @nonce, @soul_hash, @allocated_at,
                 @binding_revision, @binding_checksum, @canonical_json, @binding_json)
            ON CONFLICT DO NOTHING
            RETURNING soul_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", binding.SoulId);
        command.Parameters.AddWithValue("source_id", binding.SourceId);
        command.Parameters.AddWithValue("algorithm", binding.Algorithm);
        command.Parameters.AddWithValue("nonce", binding.Nonce);
        command.Parameters.AddWithValue("soul_hash", binding.SoulHash);
        command.Parameters.AddWithValue("allocated_at", binding.AllocatedAt);
        command.Parameters.AddWithValue("binding_revision", binding.BindingRevision);
        command.Parameters.AddWithValue("binding_checksum", binding.BindingChecksum);
        command.Parameters.AddWithValue("canonical_json", canonical);
        command.Parameters.AddWithValue("binding_json", NpgsqlDbType.Jsonb, canonical);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string soulId,
        long maximumNonce,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.source_binding_quarantine
                (quarantine_id, soul_id, soul_hash, maximum_nonce, reason)
            VALUES (@id, @soul_id, @soul_hash, @maximum_nonce, @reason)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("soul_hash", soulId[5..]);
        command.Parameters.AddWithValue("maximum_nonce", maximumNonce);
        command.Parameters.AddWithValue("reason", "SOURCE_ID_NONCE_EXHAUSTED");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<GBrainProjectionV2?> ReadCurrentProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string soulId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT soul_id, source_id, source_binding_revision::text,
                   source_binding_checksum::text, projection_revision::text,
                   projection_checksum::text, source_event_count, occurred_at,
                   canonical_json, projection_json::text,
                   binding_soul_id, binding_source_id, binding_algorithm, binding_nonce,
                   binding_soul_hash, binding_allocated_at, binding_revision,
                   binding_checksum, binding_canonical_json, binding_json_text
            FROM (
                SELECT rr.soul_id, rr.source_id, rr.source_binding_revision,
                       rr.source_binding_checksum, rr.projection_revision,
                       rr.projection_checksum, rr.source_event_count, rr.occurred_at,
                       rr.canonical_json, rr.projection_json::text AS projection_json,
                       sb.soul_id AS binding_soul_id, sb.source_id AS binding_source_id,
                       sb.algorithm AS binding_algorithm, sb.nonce AS binding_nonce,
                       sb.soul_hash::text AS binding_soul_hash,
                       sb.allocated_at AS binding_allocated_at,
                       sb.binding_revision::text AS binding_revision,
                       sb.binding_checksum::text AS binding_checksum,
                       sb.canonical_json AS binding_canonical_json,
                       sb.binding_json::text AS binding_json_text,
                       rr.created_at
                FROM {_options.SchemaName}.rendered_revisions rr
                LEFT JOIN {_options.SchemaName}.source_bindings sb
                  ON sb.soul_id = rr.soul_id
                 AND sb.source_id = rr.source_id
                 AND sb.binding_revision = rr.source_binding_revision
                 AND sb.binding_checksum = rr.source_binding_checksum
                WHERE rr.soul_id = @soul_id
            ) verified_rows
            ORDER BY source_event_count DESC, created_at DESC, projection_revision
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadProjectionRow(reader, soulId)
            : null;
    }

    private async Task<GBrainProjectionV2?> ReadProjectionByRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string soulId,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT soul_id, source_id, source_binding_revision::text,
                   source_binding_checksum::text, projection_revision::text,
                   projection_checksum::text, source_event_count, occurred_at,
                   canonical_json, projection_json::text,
                   binding_soul_id, binding_source_id, binding_algorithm, binding_nonce,
                   binding_soul_hash, binding_allocated_at, binding_revision,
                   binding_checksum, binding_canonical_json, binding_json_text
            FROM (
                SELECT rr.soul_id, rr.source_id, rr.source_binding_revision,
                       rr.source_binding_checksum, rr.projection_revision,
                       rr.projection_checksum, rr.source_event_count, rr.occurred_at,
                       rr.canonical_json, rr.projection_json::text AS projection_json,
                       sb.soul_id AS binding_soul_id, sb.source_id AS binding_source_id,
                       sb.algorithm AS binding_algorithm, sb.nonce AS binding_nonce,
                       sb.soul_hash::text AS binding_soul_hash,
                       sb.allocated_at AS binding_allocated_at,
                       sb.binding_revision::text AS binding_revision,
                       sb.binding_checksum::text AS binding_checksum,
                       sb.canonical_json AS binding_canonical_json,
                       sb.binding_json::text AS binding_json_text
                FROM {_options.SchemaName}.rendered_revisions rr
                LEFT JOIN {_options.SchemaName}.source_bindings sb
                  ON sb.soul_id = rr.soul_id
                 AND sb.source_id = rr.source_id
                 AND sb.binding_revision = rr.source_binding_revision
                 AND sb.binding_checksum = rr.source_binding_checksum
                WHERE rr.soul_id = @soul_id AND rr.projection_revision = @revision
            ) verified_rows
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadProjectionRow(reader, soulId)
            : null;
    }

    private async Task<bool> TryInsertProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GBrainProjectionV2 projection,
        CancellationToken cancellationToken)
    {
        var canonical = GBrainProjectionV2Canonicalizer.Serialize(projection);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.rendered_revisions
                (soul_id, source_id, source_binding_revision, source_binding_checksum,
                 projection_revision, projection_checksum, source_event_count, occurred_at,
                 canonical_json, projection_json)
            VALUES
                (@soul_id, @source_id, @source_binding_revision, @source_binding_checksum,
                 @projection_revision, @projection_checksum, @source_event_count, @occurred_at,
                 @canonical_json, @projection_json)
            ON CONFLICT DO NOTHING
            RETURNING projection_revision
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", projection.SoulId);
        command.Parameters.AddWithValue("source_id", projection.SourceId);
        command.Parameters.AddWithValue("source_binding_revision", projection.SourceBindingRevision);
        command.Parameters.AddWithValue("source_binding_checksum", projection.SourceBindingChecksum);
        command.Parameters.AddWithValue("projection_revision", projection.ProjectionRevision);
        command.Parameters.AddWithValue("projection_checksum", projection.ProjectionChecksum);
        command.Parameters.AddWithValue("source_event_count", projection.SourceEventCount);
        command.Parameters.AddWithValue("occurred_at", projection.OccurredAt);
        command.Parameters.AddWithValue("canonical_json", canonical);
        command.Parameters.AddWithValue("projection_json", NpgsqlDbType.Jsonb, canonical);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static GBrainSourceBindingV1 DeserializeBinding(
        string canonical,
        string jsonb,
        string expectedSoulId,
        string relationalSoulId,
        string relationalSourceId,
        string relationalAlgorithm,
        long relationalNonce,
        string relationalSoulHash,
        DateTimeOffset relationalAllocatedAt,
        string relationalBindingRevision,
        string relationalBindingChecksum)
    {
        GBrainSourceBindingV1 value;
        try
        {
            value = JsonSerializer.Deserialize<GBrainSourceBindingV1>(canonical)
                ?? throw new JsonException("Binding JSON was null.");
            value.Validate();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new SourceBindingIntegrityException(
                $"Stored Source binding is not a valid fixed contract: {exception.Message}");
        }

        var rebuiltCanonical = GBrainSourceBindingCanonicalizer.Serialize(value);
        if (!string.Equals(value.SoulId, expectedSoulId, StringComparison.Ordinal) ||
            !string.Equals(rebuiltCanonical, canonical, StringComparison.Ordinal) ||
            !JsonSemanticallyEquals(canonical, jsonb) ||
            !string.Equals(value.SoulId, relationalSoulId, StringComparison.Ordinal) ||
            !string.Equals(value.SourceId, relationalSourceId, StringComparison.Ordinal) ||
            !string.Equals(value.Algorithm, relationalAlgorithm, StringComparison.Ordinal) ||
            value.Nonce != relationalNonce ||
            !string.Equals(value.SoulHash, relationalSoulHash, StringComparison.Ordinal) ||
            value.AllocatedAt != relationalAllocatedAt ||
            !string.Equals(value.BindingRevision, relationalBindingRevision, StringComparison.Ordinal) ||
            !string.Equals(value.BindingChecksum, relationalBindingChecksum, StringComparison.Ordinal))
        {
            throw new SourceBindingIntegrityException(
                "Stored Source binding relational proof, canonical bytes, or JSONB projection diverged.");
        }

        return value;
    }

    private static GBrainProjectionV2 ReadProjectionRow(NpgsqlDataReader reader, string expectedSoulId)
    {
        if (reader.IsDBNull(10))
        {
            throw new ProjectionInputConflictException(
                "Stored projection lost the exact Source binding row required by its foreign proof.");
        }

        var binding = DeserializeBinding(
            reader.GetString(18),
            reader.GetString(19),
            expectedSoulId,
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetInt64(13),
            reader.GetString(14),
            ReadUtcTimestamp(reader, 15),
            reader.GetString(16),
            reader.GetString(17));
        var projection = DeserializeProjection(
            reader.GetString(8),
            reader.GetString(9),
            expectedSoulId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            ReadUtcTimestamp(reader, 7));
        if (!string.Equals(
                GBrainSourceBindingCanonicalizer.Serialize(projection.SourceBinding),
                GBrainSourceBindingCanonicalizer.Serialize(binding),
                StringComparison.Ordinal))
        {
            throw new ProjectionInputConflictException(
                "Stored projection embeds a Source proof that differs from its referenced binding row.");
        }

        return projection;
    }

    private static GBrainProjectionV2 DeserializeProjection(
        string canonical,
        string jsonb,
        string expectedSoulId,
        string relationalSoulId,
        string relationalSourceId,
        string relationalSourceBindingRevision,
        string relationalSourceBindingChecksum,
        string relationalProjectionRevision,
        string relationalProjectionChecksum,
        int relationalSourceEventCount,
        DateTimeOffset relationalOccurredAt)
    {
        GBrainProjectionV2 value;
        try
        {
            value = JsonSerializer.Deserialize<GBrainProjectionV2>(canonical)
                ?? throw new JsonException("Projection JSON was null.");
            value.Validate();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new ProjectionInputConflictException(
                $"Stored projection is not a valid fixed contract: {exception.Message}");
        }

        var rebuiltCanonical = GBrainProjectionV2Canonicalizer.Serialize(value);
        if (!string.Equals(value.SoulId, expectedSoulId, StringComparison.Ordinal) ||
            !string.Equals(rebuiltCanonical, canonical, StringComparison.Ordinal) ||
            !JsonSemanticallyEquals(canonical, jsonb) ||
            !string.Equals(value.SoulId, relationalSoulId, StringComparison.Ordinal) ||
            !string.Equals(value.SourceId, relationalSourceId, StringComparison.Ordinal) ||
            !string.Equals(value.SourceBindingRevision, relationalSourceBindingRevision, StringComparison.Ordinal) ||
            !string.Equals(value.SourceBindingChecksum, relationalSourceBindingChecksum, StringComparison.Ordinal) ||
            !string.Equals(value.ProjectionRevision, relationalProjectionRevision, StringComparison.Ordinal) ||
            !string.Equals(value.ProjectionChecksum, relationalProjectionChecksum, StringComparison.Ordinal) ||
            value.SourceEventCount != relationalSourceEventCount ||
            value.OccurredAt != relationalOccurredAt)
        {
            throw new ProjectionInputConflictException(
                "Stored projection relational proof, canonical bytes, or JSONB projection diverged.");
        }

        return value;
    }

    internal static bool JsonSemanticallyEquals(string canonical, string jsonb)
    {
        try
        {
            using var canonicalDocument = JsonDocument.Parse(canonical);
            using var jsonbDocument = JsonDocument.Parse(jsonb);
            return JsonSemanticallyEquals(canonicalDocument.RootElement, jsonbDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonSemanticallyEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var leftProperties = left.EnumerateObject().ToArray();
                var rightProperties = right.EnumerateObject().ToArray();
                if (leftProperties.Length != rightProperties.Length)
                {
                    return false;
                }

                foreach (var property in leftProperties)
                {
                    if (!right.TryGetProperty(property.Name, out var rightValue) ||
                        !JsonSemanticallyEquals(property.Value, rightValue))
                    {
                        return false;
                    }
                }

                return true;
            }
            case JsonValueKind.Array:
            {
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length &&
                    leftItems.Zip(rightItems).All(pair =>
                        JsonSemanticallyEquals(pair.First, pair.Second));
            }
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return decimal.TryParse(
                           left.GetRawText(),
                           NumberStyles.Float,
                           CultureInfo.InvariantCulture,
                           out var leftDecimal) &&
                       decimal.TryParse(
                           right.GetRawText(),
                           NumberStyles.Float,
                           CultureInfo.InvariantCulture,
                           out var rightDecimal)
                    ? leftDecimal == rightDecimal
                    : string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    private static DateTimeOffset ReadUtcTimestamp(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}

internal static class PostgresTimestampPrecision
{
    internal static void Require(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero || value.Ticks % 10 != 0)
        {
            throw new ProjectionInputConflictException(
                $"{parameterName} must be UTC at PostgreSQL microsecond precision before persistence.");
        }
    }
}
