using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.Binding.Contracts;
using Dps.PersonaStore.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.PersonaStore;

public sealed class PostgresPersonaStoreOptions
{
    public PostgresPersonaStoreOptions(
        string migratorConnectionString,
        string runtimeConnectionString,
        string schema,
        string requestHmacKeyBase64,
        TimeSpan? operationTimeout = null)
    {
        MigratorConnectionString = RequireConnectionString(migratorConnectionString, nameof(migratorConnectionString));
        RuntimeConnectionString = RequireConnectionString(runtimeConnectionString, nameof(runtimeConnectionString));
        Schema = RequireIdentifier(schema, nameof(schema));
        RequestHmacKey = RequireHmacKey(requestHmacKeyBase64);
        OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(5);
        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "Persona Store operations require a timeout between zero and five seconds.");

        var migrator = new NpgsqlConnectionStringBuilder(MigratorConnectionString);
        var runtime = new NpgsqlConnectionStringBuilder(RuntimeConnectionString);
        if (string.IsNullOrWhiteSpace(migrator.Username) || string.IsNullOrWhiteSpace(runtime.Username))
            throw new ArgumentException("Migrator and runtime connection strings require explicit PostgreSQL roles.");
        if (string.Equals(migrator.Username, runtime.Username, StringComparison.Ordinal))
            throw new ArgumentException("The PostgreSQL migrator and runtime writer must use different roles.");
        if (!string.Equals(migrator.Host, runtime.Host, StringComparison.Ordinal) ||
            migrator.Port != runtime.Port ||
            !string.Equals(migrator.Database, runtime.Database, StringComparison.Ordinal))
        {
            throw new ArgumentException("Migrator and runtime connections must target the exact same PostgreSQL host, port, and database.");
        }
        MigratorRole = RequireIdentifier(migrator.Username, "migrator PostgreSQL role");
        RuntimeRole = RequireIdentifier(runtime.Username, "runtime PostgreSQL role");
    }

    internal string MigratorConnectionString { get; }
    internal string RuntimeConnectionString { get; }
    internal byte[] RequestHmacKey { get; }
    public string Schema { get; }
    public string MigratorRole { get; }
    public string RuntimeRole { get; }
    public TimeSpan OperationTimeout { get; }

    public override string ToString() => $"PostgresPersonaStoreOptions(Schema={Schema}, RuntimeRole={RuntimeRole}, OperationTimeout={OperationTimeout})";

    private static string RequireConnectionString(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A PostgreSQL connection string is required.", name);
        _ = new NpgsqlConnectionStringBuilder(value);
        return value;
    }

    private static string RequireIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 63 ||
            value[0] is < 'a' or > 'z' ||
            value.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_"))
        {
            throw new ArgumentException($"{name} must be a lowercase PostgreSQL identifier.", name);
        }
        return value;
    }

    private static byte[] RequireHmacKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A base64 request-HMAC key is required.", nameof(value));
        byte[] key;
        try { key = Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new ArgumentException("The request-HMAC key must be canonical base64.", nameof(value), exception); }
        if (key.Length != 32 || Convert.ToBase64String(key) != value)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new ArgumentException("The request-HMAC key must decode to exactly 32 bytes.", nameof(value));
        }
        return key;
    }
}

public enum PersonaMutationStage
{
    ConflictQuarantineWritten,
    ConflictQuarantineCommitted,
    BindingFenceHeld,
    BeforeCommit,
    TransactionCommittedWithBindingFenceHeld
}

public delegate ValueTask PersonaMutationFaultInjector(PersonaMutationStage stage, CancellationToken cancellationToken);

public sealed record PersonaOutboxRecord(
    Guid OutboxId,
    string PayloadSha256,
    PersonaRevisionV1 Payload,
    DateTimeOffset CreatedAt);

public sealed class PostgresPersonaStore : IPersonaStore
{
    private const string MigrationResource = "Dps.PersonaStore.Migrations.001_create_persona_store.sql";
    private readonly PostgresPersonaStoreOptions _options;
    private readonly IBindingMutationFenceClient _bindingFenceClient;
    private readonly PersonaMutationFaultInjector _faultInjector;
    private readonly PersonaBindingTrustContext _bindingTrust;

    internal PostgresPersonaStore(
        PostgresPersonaStoreOptions options,
        IBindingMutationFenceClient bindingFenceClient,
        PersonaMutationFaultInjector? faultInjector = null,
        PersonaBindingTrustContext? bindingTrust = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _bindingFenceClient = bindingFenceClient ?? throw new ArgumentNullException(nameof(bindingFenceClient));
        _faultInjector = faultInjector ?? (static (_, _) => ValueTask.CompletedTask);
        _bindingTrust = bindingTrust ?? PersonaBindingTrustContext.TestOnly;
    }

    public static PostgresPersonaStore CreateTrusted(
        PostgresPersonaStoreOptions options,
        IBindingMutationFenceClient bindingFenceClient,
        SignedBindingCompositionAttestationV1 bindingCompositionAttestation,
        PersonaBindingCompositionExpectations expectations)
    {
        ArgumentNullException.ThrowIfNull(bindingFenceClient);
        var trust = PersonaBindingCompositionVerifier.VerifyProduction(
            bindingCompositionAttestation,
            expectations,
            bindingFenceClient);
        return new PostgresPersonaStore(options, bindingFenceClient, bindingTrust: trust);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = new NpgsqlConnection(_options.MigratorConnectionString);
        await connection.OpenAsync(timeout.Token);
        await ConfigureUtcSessionAsync(connection, timeout.Token);
        await AssertPostgresVersionAsync(connection, timeout.Token);
        await AssertMigratorIdentityAsync(connection, timeout.Token);

        var migration = await ReadMigrationAsync(timeout.Token);
        var migrationSha256 = PersonaMutationCanonicalizer.HashUtf8(migration);
        var quotedSchema = QuoteIdentifier(_options.Schema);
        var quotedRuntimeRole = QuoteIdentifier(_options.RuntimeRole);
        migration = migration.Replace("__SCHEMA__", quotedSchema, StringComparison.Ordinal)
            .Replace("__RUNTIME_ROLE__", quotedRuntimeRole, StringComparison.Ordinal)
            .Replace("__MIGRATION_SHA256__", migrationSha256, StringComparison.Ordinal);

        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token))
        {
            await using (var migrationLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@schema_name, 730200))",
                connection,
                transaction) { CommandTimeout = CommandTimeoutSeconds })
            {
                migrationLock.Parameters.AddWithValue("schema_name", _options.Schema);
                await migrationLock.ExecuteNonQueryAsync(timeout.Token);
            }
            var schemaExists = await AssertTrustedSchemaBaselineAsync(connection, transaction, timeout.Token);
            if (!schemaExists)
            {
                await using var createSchema = new NpgsqlCommand(
                    $"CREATE SCHEMA {quotedSchema} AUTHORIZATION {QuoteIdentifier(_options.MigratorRole)}",
                    connection,
                    transaction) { CommandTimeout = CommandTimeoutSeconds };
                await createSchema.ExecuteNonQueryAsync(timeout.Token);
            }
            await AssertModuleObjectOwnershipAsync(connection, transaction, allowEmpty: true, timeout.Token);
            await VerifyExistingMigrationLedgerAsync(connection, transaction, migrationSha256, timeout.Token);
            await VerifyExistingCatalogAttestationAsync(connection, transaction, timeout.Token);
            await using (var command = new NpgsqlCommand(migration, connection, transaction) { CommandTimeout = CommandTimeoutSeconds })
                await command.ExecuteNonQueryAsync(timeout.Token);
            await VerifyOrCreateRequestHmacKeyAttestationAsync(connection, transaction, timeout.Token);
            await AssertModuleObjectOwnershipAsync(connection, transaction, allowEmpty: false, timeout.Token);
            await VerifyMigrationLedgerAsync(connection, transaction, migrationSha256, timeout.Token);
            await RecordBindingCompositionStateAsync(connection, transaction, timeout.Token);
            await VerifyOrCreateCatalogAttestationAsync(connection, transaction, timeout.Token);
            await VerifyRuntimeRoleBoundaryAsync(connection, transaction, timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }

        await VerifyRuntimeConnectionAsync(migrationSha256, timeout.Token);
    }

    public async ValueTask<PersonaRevisionV1> PutAsync(PutPersonaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = PersonaMutationCanonicalizer.Normalize(command);
        using var timeout = CreateTimeout(cancellationToken);
        var requestHash = PersonaMutationCanonicalizer.HashPut(normalized, _options.RequestHmacKey);
        var replay = await TryResolveExistingReceiptAsync(
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.IdempotencyKey,
            "put",
            requestHash,
            timeout.Token);
        if (replay is not null) return replay;
        await using var bindingFence = await PersonaBindingFence.AcquireAsync(
            _bindingFenceClient,
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.TraceId,
            normalized.IdempotencyKey,
            normalized.OccurredAt,
            timeout.Token);

        return await MutateAsync(
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.ExpectedRevision,
            normalized.TraceId,
            normalized.IdempotencyKey,
            normalized.OccurredAt,
            normalized.EvidenceSha256,
            requestHash,
            "put",
            normalized.Traits,
            bindingFence.Receipt,
            timeout.Token);
    }

    public async ValueTask<PersonaRevisionV1> DeleteAsync(DeletePersonaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = PersonaMutationCanonicalizer.Normalize(command);
        using var timeout = CreateTimeout(cancellationToken);
        var requestHash = PersonaMutationCanonicalizer.HashDelete(normalized, _options.RequestHmacKey);
        var replay = await TryResolveExistingReceiptAsync(
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.IdempotencyKey,
            "delete",
            requestHash,
            timeout.Token);
        if (replay is not null) return replay;
        await using var bindingFence = await PersonaBindingFence.AcquireAsync(
            _bindingFenceClient,
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.TraceId,
            normalized.IdempotencyKey,
            normalized.OccurredAt,
            timeout.Token);

        return await MutateAsync(
            normalized.SoulId,
            normalized.DeviceBindingId,
            normalized.PlatformAccountId,
            normalized.ExpectedRevision,
            normalized.TraceId,
            normalized.IdempotencyKey,
            normalized.OccurredAt,
            normalized.EvidenceSha256,
            requestHash,
            "delete",
            traits: null,
            bindingFence.Receipt,
            timeout.Token);
    }

    public async ValueTask<PersonaRevisionV1> GetCurrentAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        PersonaMutationCanonicalizer.ValidateScope(soulId, deviceBindingId, platformAccountId);
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = CreateCommand(
            $"""
            SELECT result_json::text
            FROM {Qualified("persona_current")}
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            """,
            connection);
        AddScope(command, soulId, deviceBindingId, platformAccountId);
        var json = await command.ExecuteScalarAsync(timeout.Token) as string;
        return json is null ? throw new KeyNotFoundException("Unknown persona scope.") : DeserializeContract(json);
    }

    public async ValueTask<PersonaHistoryExportV1> ExportHistoryV1Async(
        ExportPersonaHistoryCommand exportCommand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exportCommand);
        var normalized = PersonaMutationCanonicalizer.Normalize(exportCommand);
        using var timeout = CreateTimeout(cancellationToken);
        var requestHmacSha256 = PersonaMutationCanonicalizer.HashExportRequest(normalized, _options.RequestHmacKey);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        byte[]? soulHmacKey = null;

        try
        {
            await AcquireExportLocksAsync(connection, transaction, normalized, timeout.Token);
            var resolution = await ResolveExportReceiptAsync(
                connection,
                transaction,
                normalized,
                requestHmacSha256,
                timeout.Token);
            if (resolution.Outcome == "replay")
            {
                var replay = DeserializeExportContract(resolution.ResultJson!);
                PersonaMutationCanonicalizer.VerifyExportProof(replay, normalized, _options.RequestHmacKey);
                await transaction.CommitAsync(timeout.Token);
                return replay;
            }
            if (resolution.Outcome == "conflict")
            {
                await transaction.CommitAsync(timeout.Token);
                throw new PersonaIdempotencyConflictException();
            }
            if (resolution.Outcome != "missing")
                throw new InvalidDataException("Persona history export receipt resolution returned an unknown outcome.");

            PersonaRevisionV1 current;
            await using (var currentCommand = CreateCommand(
                $"""
                SELECT result_json::text
                FROM {Qualified("persona_current")}
                WHERE soul_id = @soul_id
                  AND device_binding_id = @device_binding_id
                  AND platform_account_id = @platform_account_id
                FOR SHARE
                """,
                connection,
                transaction))
            {
                AddScope(currentCommand, normalized.SoulId, normalized.DeviceBindingId, normalized.PlatformAccountId);
                var currentJson = await currentCommand.ExecuteScalarAsync(timeout.Token) as string
                    ?? throw new KeyNotFoundException("Unknown persona scope.");
                current = DeserializeContract(currentJson);
            }
            if (current.Status == "active")
            {
                soulHmacKey = await ReadSoulHmacKeyAsync(
                    connection,
                    transaction,
                    normalized.SoulId,
                    normalized.DeviceBindingId,
                    normalized.PlatformAccountId,
                    current.PersonaRevision,
                    timeout.Token);
            }

            await using var command = CreateCommand(
                $"""
                SELECT revision.result_json::text, payload.traits_json::text
                FROM {Qualified("persona_revisions")} AS revision
                LEFT JOIN {Qualified("trait_payloads")} AS payload
                  ON payload.soul_id = revision.soul_id
                 AND payload.persona_revision = revision.persona_revision
                 AND payload.device_binding_id = revision.device_binding_id
                 AND payload.platform_account_id = revision.platform_account_id
                 AND payload.traits_sha256 = revision.traits_sha256
                WHERE revision.soul_id = @soul_id
                  AND revision.device_binding_id = @device_binding_id
                  AND revision.platform_account_id = @platform_account_id
                ORDER BY revision.persona_revision
                LIMIT 10001
                """,
                connection,
                transaction);
            AddScope(command, normalized.SoulId, normalized.DeviceBindingId, normalized.PlatformAccountId);
            var revisions = new List<PersonaHistoryExportItemV1>();
            var totalUtf8Bytes = 0;
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            while (await reader.ReadAsync(timeout.Token))
            {
                if (revisions.Count == 10_000)
                    throw new InvalidDataException("Persona history export exceeds the v1 10,000-revision ceiling.");
                var revisionJson = reader.GetString(0);
                totalUtf8Bytes = checked(totalUtf8Bytes + Encoding.UTF8.GetByteCount(revisionJson));
                var revision = DeserializeContract(revisionJson);
                PersonaMutationCanonicalizer.EnsureScope(
                    revision,
                    normalized.SoulId,
                    normalized.DeviceBindingId,
                    normalized.PlatformAccountId);
                if (revision.PersonaRevision != revisions.Count + 1L)
                    throw new InvalidDataException("Persona history revisions are not contiguous from revision one.");
                IReadOnlyDictionary<string, string>? traits = null;
                if (!reader.IsDBNull(1))
                {
                    var traitsJson = reader.GetString(1);
                    totalUtf8Bytes = checked(totalUtf8Bytes + Encoding.UTF8.GetByteCount(traitsJson));
                    var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(traitsJson)
                        ?? throw new InvalidDataException("A retained persona history payload is invalid.");
                    traits = PersonaMutationCanonicalizer.ValidateStoredTraits(decoded);
                    if (!traits.Keys.SequenceEqual(revision.TraitKeys, StringComparer.Ordinal))
                        throw new InvalidDataException("A retained persona history payload key set does not match its revision.");
                    if (soulHmacKey is null || !PersonaMutationCanonicalizer.FixedTimeSha256Equals(
                            PersonaMutationCanonicalizer.HashTraits(traits, soulHmacKey),
                            revision.TraitsSha256))
                        throw new InvalidDataException("A retained persona history payload keyed checksum does not match its revision.");
                }

                if (current.Status == "active" && revision.Status == "active" && traits is null)
                    throw new InvalidDataException("An active persona history revision is missing its retained trait payload.");
                if (current.Status == "deleted" && traits is not null)
                    throw new InvalidDataException("A logically deleted persona still has retained live-primary trait payloads.");
                if (revision.Status == "deleted" && traits is not null)
                    throw new InvalidDataException("A deleted persona revision unexpectedly has a retained trait payload.");

                revisions.Add(new PersonaHistoryExportItemV1(
                    revision.ImmutableCopy(),
                    traits is null ? PersonaHistoryExportItemV1.LivePrimaryLogicallyDeleted : PersonaHistoryExportItemV1.Retained,
                    traits is null ? null : PersonaTraitVocabularyV1.ValidateAndFreeze(traits)));
                if (totalUtf8Bytes > 16 * 1024 * 1024)
                    throw new InvalidDataException("Persona history export exceeds the v1 16-MiB retained-payload ceiling.");
            }
            if (revisions.Count == 0) throw new InvalidDataException("Persona history is missing.");
            if (revisions[^1].Revision.PersonaRevision != current.PersonaRevision)
                throw new InvalidDataException("Persona history does not end at the current revision.");

            var payloadState = current.Status == "deleted"
                ? PersonaHistoryExportItemV1.LivePrimaryLogicallyDeleted
                : PersonaHistoryExportItemV1.Retained;
            var result = PersonaMutationCanonicalizer.CreateHistoryExport(
                normalized,
                payloadState,
                Array.AsReadOnly(revisions.ToArray()),
                _options.RequestHmacKey);
            var recorded = await RecordExportReceiptAsync(
                connection,
                transaction,
                normalized,
                result,
                timeout.Token);
            PersonaMutationCanonicalizer.VerifyExportProof(recorded, normalized, _options.RequestHmacKey);
            if (!PersonaMutationCanonicalizer.FixedTimeSha256Equals(
                    recorded.ExportReceiptHmacSha256,
                    result.ExportReceiptHmacSha256) ||
                !PersonaMutationCanonicalizer.FixedTimeSha256Equals(
                    recorded.ExportPayloadSha256,
                    result.ExportPayloadSha256))
                throw new InvalidDataException("The immutable Persona history export receipt changed while being recorded.");
            await transaction.CommitAsync(timeout.Token);
            return recorded;
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await transaction.RollbackAsync(rollbackTimeout.Token); }
                catch { /* Preserve the primary failure. */ }
            }
            throw;
        }
        finally
        {
            if (soulHmacKey is not null) CryptographicOperations.ZeroMemory(soulHmacKey);
        }
    }

    private async Task AcquireExportLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizedExportPersonaHistoryCommand commandValue,
        CancellationToken cancellationToken)
    {
        await using (var idempotencyLock = CreateCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@idempotency_key_sha256, 730203))",
            connection,
            transaction))
        {
            idempotencyLock.Parameters.AddWithValue(
                "idempotency_key_sha256",
                PersonaMutationCanonicalizer.HashUtf8(commandValue.IdempotencyKey));
            await idempotencyLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var soulLock = CreateCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@soul_id, 730202))",
            connection,
            transaction);
        soulLock.Parameters.AddWithValue("soul_id", commandValue.SoulId);
        await soulLock.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<AtomicResponse> ResolveExportReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizedExportPersonaHistoryCommand commandValue,
        string requestHmacSha256,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            SELECT {Qualified("resolve_persona_export_receipt_v1")}(
                @soul_id,
                @device_binding_id,
                @platform_account_id,
                @idempotency_key_sha256,
                @request_hmac_sha256)::text
            """,
            connection,
            transaction);
        AddScope(command, commandValue.SoulId, commandValue.DeviceBindingId, commandValue.PlatformAccountId);
        command.Parameters.AddWithValue("idempotency_key_sha256", PersonaMutationCanonicalizer.HashUtf8(commandValue.IdempotencyKey));
        command.Parameters.AddWithValue("request_hmac_sha256", requestHmacSha256);
        var responseJson = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException("Persona history export receipt resolution returned no result.");
        return ParseAtomicResponse(responseJson);
    }

    private async Task<PersonaHistoryExportV1> RecordExportReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizedExportPersonaHistoryCommand commandValue,
        PersonaHistoryExportV1 result,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            SELECT {Qualified("record_persona_export_receipt_v1")}(
                @soul_id,
                @device_binding_id,
                @platform_account_id,
                @idempotency_key_sha256,
                @export_request_hmac_sha256,
                @snapshot_persona_revision,
                @snapshot_cursor_hmac_sha256,
                @export_payload_sha256,
                @export_receipt_hmac_sha256,
                @export_receipt_id,
                @result_document,
                @request_hmac_key)::text
            """,
            connection,
            transaction);
        AddScope(command, commandValue.SoulId, commandValue.DeviceBindingId, commandValue.PlatformAccountId);
        command.Parameters.AddWithValue("idempotency_key_sha256", PersonaMutationCanonicalizer.HashUtf8(commandValue.IdempotencyKey));
        command.Parameters.AddWithValue("export_request_hmac_sha256", result.ExportRequestHmacSha256);
        command.Parameters.AddWithValue("snapshot_persona_revision", result.SnapshotPersonaRevision);
        command.Parameters.AddWithValue("snapshot_cursor_hmac_sha256", result.SnapshotCursorHmacSha256);
        command.Parameters.AddWithValue("export_payload_sha256", result.ExportPayloadSha256);
        command.Parameters.AddWithValue("export_receipt_hmac_sha256", result.ExportReceiptHmacSha256);
        command.Parameters.AddWithValue("export_receipt_id", result.ExportReceiptId);
        command.Parameters.Add("result_document", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(result);
        command.Parameters.Add("request_hmac_key", NpgsqlDbType.Bytea).Value = _options.RequestHmacKey;
        var responseJson = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException("Persona history export receipt recording returned no result.");
        var response = ParseAtomicResponse(responseJson);
        if (response.Outcome is not ("committed" or "replay") || response.ResultJson is null)
            throw new InvalidDataException("Persona history export receipt recording returned an invalid outcome.");
        return DeserializeExportContract(response.ResultJson);
    }

    public async ValueTask<IReadOnlyList<PersonaRevisionV1>> ReadHistoryAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        PersonaMutationCanonicalizer.ValidateScope(soulId, deviceBindingId, platformAccountId);
        using var timeout = CreateTimeout(cancellationToken);
        _ = await GetCurrentAsync(soulId, deviceBindingId, platformAccountId, timeout.Token);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = CreateCommand(
            $"""
            SELECT result_json::text
            FROM {Qualified("persona_revisions")}
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            ORDER BY persona_revision
            """,
            connection);
        AddScope(command, soulId, deviceBindingId, platformAccountId);
        var history = new List<PersonaRevisionV1>();
        await using var reader = await command.ExecuteReaderAsync(timeout.Token);
        while (await reader.ReadAsync(timeout.Token)) history.Add(DeserializeContract(reader.GetString(0)));
        return history;
    }

    public async ValueTask<IReadOnlyList<PersonaOutboxRecord>> ReadPendingOutboxAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        PersonaMutationCanonicalizer.ValidateScope(soulId, deviceBindingId, platformAccountId);
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = CreateCommand(
            $"""
            SELECT o.outbox_id, o.payload_sha256, o.payload_json::text, o.created_at
            FROM {Qualified("outbox")} o
            LEFT JOIN {Qualified("outbox_dispatch_receipts")} d ON d.outbox_id = o.outbox_id
            WHERE o.soul_id = @soul_id
              AND o.device_binding_id = @device_binding_id
              AND o.platform_account_id = @platform_account_id
              AND d.outbox_id IS NULL
            ORDER BY o.created_at, o.outbox_id
            """,
            connection);
        AddScope(command, soulId, deviceBindingId, platformAccountId);
        var records = new List<PersonaOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(timeout.Token);
        while (await reader.ReadAsync(timeout.Token))
        {
            var rawPayload = reader.GetString(2);
            var payload = DeserializeContract(rawPayload);
            var expectedSha256 = PersonaMutationCanonicalizer.HashUtf8(rawPayload);
            if (expectedSha256 != reader.GetString(1))
                throw new InvalidDataException("The persona outbox payload checksum does not match its contract.");
            records.Add(new PersonaOutboxRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                payload,
                reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime()));
        }
        return records;
    }

    internal Task<long> CountRevisionsAsync(CancellationToken cancellationToken = default) => CountAsync("persona_revisions", cancellationToken);
    internal Task<long> CountReceiptsAsync(CancellationToken cancellationToken = default) => CountAsync("idempotency_receipts", cancellationToken);
    internal Task<long> CountOutboxAsync(CancellationToken cancellationToken = default) => CountAsync("outbox", cancellationToken);
    internal Task<long> CountQuarantineAsync(CancellationToken cancellationToken = default) => CountAsync("idempotency_quarantine", cancellationToken);
    internal Task<long> CountTraitPayloadsAsync(CancellationToken cancellationToken = default) => CountAsync("trait_payloads", cancellationToken);
    internal Task<long> CountErasureAuditAsync(CancellationToken cancellationToken = default) => CountAsync("erasure_audit", cancellationToken);

    private async ValueTask<PersonaRevisionV1?> TryResolveExistingReceiptAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string idempotencyKey,
        string operation,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenRuntimeConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = CreateCommand(
            $"""
            SELECT {Qualified("resolve_persona_receipt_v1")}(
                @soul_id,
                @device_binding_id,
                @platform_account_id,
                @idempotency_key_sha256,
                @operation,
                @request_sha256)::text
            """,
            connection,
            transaction);
        AddScope(command, soulId, deviceBindingId, platformAccountId);
        command.Parameters.AddWithValue("idempotency_key_sha256", PersonaMutationCanonicalizer.HashUtf8(idempotencyKey));
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("request_sha256", requestSha256);
        var responseJson = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException("Persona receipt resolution returned no result.");
        var response = ParseAtomicResponse(responseJson);
        switch (response.Outcome)
        {
            case "missing":
                await transaction.CommitAsync(cancellationToken);
                return null;
            case "replay":
                await transaction.CommitAsync(cancellationToken);
                return DeserializeContract(response.ResultJson!);
            case "conflict":
                await InjectAsync(PersonaMutationStage.ConflictQuarantineWritten, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await InjectAsync(PersonaMutationStage.ConflictQuarantineCommitted, cancellationToken);
                throw new PersonaIdempotencyConflictException();
            default:
                throw new InvalidDataException("Persona receipt resolution returned an unknown outcome.");
        }
    }

    private async ValueTask<PersonaRevisionV1> MutateAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long expectedRevision,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        IReadOnlyList<string> evidenceSha256,
        string requestSha256,
        string operation,
        SortedDictionary<string, string>? traits,
        BindingMutationFenceV1 bindingFence,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenRuntimeConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var outboxId = PersonaMutationCanonicalizer.DeterministicOutboxId(soulId, checked(expectedRevision + 1));
            await using var command = CreateCommand(
                $"""
                SELECT {Qualified("mutate_persona_v1")}(
                    @operation,
                    @soul_id,
                    @device_binding_id,
                    @platform_account_id,
                    @expected_persona_revision,
                    @traits_document,
                    @evidence_values,
                    @trace_id,
                    @idempotency_key,
                    @idempotency_key_sha256,
                    @request_sha256,
                    @occurred_at,
                    @outbox_id,
                    @fence_receipt_document,
                    @composition_attestation_sha256,
                    @release_bom_sha256,
                    @composition_generation,
                    @binding_instance_trust_epoch)::text
                """,
                connection,
                transaction);
            AddScope(command, soulId, deviceBindingId, platformAccountId);
            command.Parameters.AddWithValue("operation", operation);
            command.Parameters.AddWithValue("expected_persona_revision", expectedRevision);
            command.Parameters.Add("traits_document", NpgsqlDbType.Jsonb).Value =
                traits is null ? DBNull.Value : JsonSerializer.Serialize(traits);
            command.Parameters.Add("evidence_values", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = evidenceSha256.ToArray();
            command.Parameters.AddWithValue("trace_id", traceId);
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            command.Parameters.AddWithValue("idempotency_key_sha256", PersonaMutationCanonicalizer.HashUtf8(idempotencyKey));
            command.Parameters.AddWithValue("request_sha256", requestSha256);
            command.Parameters.AddWithValue("occurred_at", occurredAt);
            command.Parameters.AddWithValue("outbox_id", outboxId);
            command.Parameters.Add("fence_receipt_document", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(bindingFence);
            command.Parameters.AddWithValue("composition_attestation_sha256", _bindingTrust.AttestationSha256);
            command.Parameters.AddWithValue("release_bom_sha256", _bindingTrust.ReleaseBomSha256);
            command.Parameters.AddWithValue("composition_generation", _bindingTrust.CompositionGeneration);
            command.Parameters.AddWithValue("binding_instance_trust_epoch", _bindingTrust.BindingInstanceTrustEpoch);
            var responseJson = await command.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidDataException("Persona atomic mutation returned no result.");
            var response = ParseAtomicResponse(responseJson);
            if (response.Outcome == "conflict")
            {
                await InjectAsync(PersonaMutationStage.ConflictQuarantineWritten, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await InjectAsync(PersonaMutationStage.ConflictQuarantineCommitted, cancellationToken);
                throw new PersonaIdempotencyConflictException();
            }
            if (response.Outcome == "revision_conflict")
                throw new PersonaRevisionConflictException(expectedRevision, response.ActualRevision!.Value);
            if (response.Outcome == "unknown_persona")
                throw new KeyNotFoundException("Unknown persona.");
            if (response.Outcome == "already_deleted")
                throw new InvalidOperationException("A deleted Persona cannot be mutated.");
            if (response.Outcome is not ("committed" or "replay"))
                throw new InvalidDataException("Persona atomic mutation returned an unknown outcome.");
            var result = DeserializeContract(response.ResultJson!);
            PersonaMutationCanonicalizer.EnsureScope(result, soulId, deviceBindingId, platformAccountId);
            await InjectAsync(PersonaMutationStage.BindingFenceHeld, cancellationToken);
            await InjectAsync(PersonaMutationStage.BeforeCommit, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await InjectAsync(PersonaMutationStage.TransactionCommittedWithBindingFenceHeld, cancellationToken);
            return result;
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await transaction.RollbackAsync(rollbackTimeout.Token); }
                catch { /* Preserve the primary failure. */ }
            }
            throw;
        }
    }

    private static AtomicResponse ParseAtomicResponse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("outcome", out var outcomeElement) ||
            outcomeElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Persona atomic response is malformed.");
        var outcome = outcomeElement.GetString()!;
        var propertyCount = root.EnumerateObject().Count();
        return outcome switch
        {
            "missing" or "conflict" or "unknown_persona" or "already_deleted" when propertyCount == 1
                => new AtomicResponse(outcome, null, null),
            "replay" or "committed" when propertyCount == 2 && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                => new AtomicResponse(outcome, result.GetRawText(), null),
            "revision_conflict" when propertyCount == 2 && root.TryGetProperty("actual_revision", out var revision) && revision.TryGetInt64(out var actual) && actual >= 0
                => new AtomicResponse(outcome, null, actual),
            _ => throw new InvalidDataException("Persona atomic response has an invalid shape.")
        };
    }

    private sealed record AtomicResponse(string Outcome, string? ResultJson, long? ActualRevision);

    private async Task<byte[]> ReadSoulHmacKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long personaRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"SELECT {Qualified("read_persona_hmac_key_v1")}(@soul_id, @device_binding_id, @platform_account_id, @persona_revision)",
            connection,
            transaction);
        AddScope(command, soulId, deviceBindingId, platformAccountId);
        command.Parameters.AddWithValue("persona_revision", personaRevision);
        var key = await command.ExecuteScalarAsync(cancellationToken) as byte[]
            ?? throw new InvalidDataException("The active Persona Soul HMAC key is missing.");
        if (key.Length != 32) throw new InvalidDataException("The active Persona Soul HMAC key is invalid.");
        return key;
    }

    private async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = CreateCommand($"SELECT count(*) FROM {Qualified(table)}", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(timeout.Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<NpgsqlConnection> OpenRuntimeConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_options.RuntimeConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureUtcSessionAsync(connection, cancellationToken);
            await VerifyBindingCompositionStateAsync(connection, transaction: null, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private NpgsqlCommand CreateCommand(string text, NpgsqlConnection connection, NpgsqlTransaction? transaction = null) =>
        new(text, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };

    private int CommandTimeoutSeconds => Math.Max(1, (int)Math.Ceiling(_options.OperationTimeout.TotalSeconds));

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.OperationTimeout);
        return source;
    }

    private string Qualified(string table) => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(table)}";

    private static string QuoteIdentifier(string value) => new NpgsqlCommandBuilder().QuoteIdentifier(value);

    private static void AddScope(NpgsqlCommand command, string soulId, string deviceBindingId, string platformAccountId)
    {
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
    }

    private static PersonaRevisionV1 DeserializeContract(string json)
        => PersonaContractJson.DeserializeStrict<PersonaRevisionV1>(json);

    private static PersonaHistoryExportV1 DeserializeExportContract(string json)
        => PersonaContractJson.DeserializeStrict<PersonaHistoryExportV1>(json);

    private static async Task AssertPostgresVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection) { CommandTimeout = 5 };
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (version != 180004)
            throw new InvalidOperationException($"Persona Store requires exact PostgreSQL 18.4; server_version_num was {version}.");
    }

    private async Task AssertMigratorIdentityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT session_user, current_user", connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(reader.GetString(0), _options.MigratorRole, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), _options.MigratorRole, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Persona Store migrations must run directly as the configured migrator role.");
    }

    private static async Task ConfigureUtcSessionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var configure = new NpgsqlCommand("SET TIME ZONE 'UTC'", connection) { CommandTimeout = 5 })
            await configure.ExecuteNonQueryAsync(cancellationToken);
        await using var verify = new NpgsqlCommand("SHOW TimeZone", connection) { CommandTimeout = 5 };
        if (!string.Equals(await verify.ExecuteScalarAsync(cancellationToken) as string, "UTC", StringComparison.Ordinal))
            throw new InvalidOperationException("Persona Store requires a UTC PostgreSQL session.");
    }

    private async Task VerifyMigrationLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"SELECT migration_sha256 FROM {Qualified("schema_migrations")} WHERE migration_version = 1",
            connection,
            transaction);
        var actual = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (actual != expectedSha256)
            throw new InvalidOperationException("Persona Store migration ledger checksum does not match the embedded migration.");
    }

    private async Task VerifyExistingMigrationLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var existence = CreateCommand(
            "SELECT pg_catalog.to_regclass(@qualified_table) IS NOT NULL",
            connection,
            transaction);
        existence.Parameters.AddWithValue("qualified_table", _options.Schema + ".schema_migrations");
        if (!Convert.ToBoolean(await existence.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture))
            return;
        await VerifyMigrationLedgerAsync(connection, transaction, expectedSha256, cancellationToken);
    }

    private async Task RecordBindingCompositionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var highest = CreateCommand(
            $"SELECT max(composition_generation) FROM {Qualified("binding_composition_state")}",
            connection,
            transaction);
        var highestValue = await highest.ExecuteScalarAsync(cancellationToken);
        if (highestValue is not DBNull && Convert.ToInt64(highestValue, System.Globalization.CultureInfo.InvariantCulture) > _bindingTrust.CompositionGeneration)
            throw new UnauthorizedAccessException("The configured Binding composition generation is older than Persona's recorded generation.");

        await using (var insert = CreateCommand(
            $"""
            INSERT INTO {Qualified("binding_composition_state")}
                (composition_generation, attestation_sha256, release_bom_sha256, binding_instance_trust_epoch)
            VALUES
                (@generation, @attestation_sha256, @release_bom_sha256, @trust_epoch)
            ON CONFLICT (composition_generation) DO NOTHING
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("generation", _bindingTrust.CompositionGeneration);
            insert.Parameters.AddWithValue("attestation_sha256", _bindingTrust.AttestationSha256);
            insert.Parameters.AddWithValue("release_bom_sha256", _bindingTrust.ReleaseBomSha256);
            insert.Parameters.AddWithValue("trust_epoch", _bindingTrust.BindingInstanceTrustEpoch);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await VerifyBindingCompositionStateAsync(connection, transaction, cancellationToken);
    }

    private async Task VerifyBindingCompositionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM {Qualified("binding_composition_state")} composition
                WHERE composition.composition_generation = @generation
                  AND composition.composition_generation = (
                      SELECT max(latest.composition_generation)
                      FROM {Qualified("binding_composition_state")} latest)
                  AND composition.attestation_sha256 = @attestation_sha256
                  AND composition.release_bom_sha256 = @release_bom_sha256
                  AND composition.binding_instance_trust_epoch = @trust_epoch)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("generation", _bindingTrust.CompositionGeneration);
        command.Parameters.AddWithValue("attestation_sha256", _bindingTrust.AttestationSha256);
        command.Parameters.AddWithValue("release_bom_sha256", _bindingTrust.ReleaseBomSha256);
        command.Parameters.AddWithValue("trust_epoch", _bindingTrust.BindingInstanceTrustEpoch);
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture))
            throw new UnauthorizedAccessException("Persona is not bound to the latest recorded Binding composition.");
    }

    private async Task<bool> AssertTrustedSchemaBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var inventory = CreateCommand(
            """
            SELECT
                (SELECT count(*)
                 FROM pg_catalog.pg_namespace namespace
                 WHERE namespace.nspname = @schema_name) AS schema_count,
                (SELECT pg_catalog.pg_get_userbyid(namespace.nspowner)
                 FROM pg_catalog.pg_namespace namespace
                 WHERE namespace.nspname = @schema_name) AS schema_owner,
                (SELECT count(*)
                 FROM pg_catalog.pg_class relation
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                 WHERE namespace.nspname = @schema_name
                   AND relation.relkind IN ('r', 'p', 'S', 'v', 'm', 'f'))
              + (SELECT count(*)
                 FROM pg_catalog.pg_proc function_value
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function_value.pronamespace
                 WHERE namespace.nspname = @schema_name)
              + (SELECT count(*)
                 FROM pg_catalog.pg_type type_value
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = type_value.typnamespace
                 WHERE namespace.nspname = @schema_name) AS object_count,
                (SELECT count(*)
                 FROM pg_catalog.pg_class relation
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                 WHERE namespace.nspname = @schema_name
                   AND relation.relname = 'schema_migrations'
                   AND relation.relkind IN ('r', 'p')) AS migration_table_count,
                (SELECT count(*)
                 FROM pg_catalog.pg_class relation
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                 WHERE namespace.nspname = @schema_name
                   AND relation.relname = 'schema_attestations'
                   AND relation.relkind IN ('r', 'p')) AS attestation_table_count
            """,
            connection,
            transaction);
        inventory.Parameters.AddWithValue("schema_name", _options.Schema);
        await using var reader = await inventory.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Unable to inspect the Persona Store schema baseline.");
        var schemaCount = reader.GetInt64(0);
        var schemaOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
        var objectCount = reader.GetInt64(2);
        var migrationTableCount = reader.GetInt64(3);
        var attestationTableCount = reader.GetInt64(4);
        await reader.DisposeAsync();
        if (schemaCount == 0) return false;
        if (schemaCount != 1) throw new InvalidOperationException("Persona Store schema identity is ambiguous.");
        if (!string.Equals(schemaOwner, _options.MigratorRole, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("A Persona Store schema not owned by the configured migrator cannot be adopted.");
        if (objectCount == 0)
            throw new InvalidOperationException("A pre-existing uninitialized Persona Store schema cannot be adopted.");
        if (migrationTableCount != 1)
            throw new InvalidOperationException("A non-empty Persona Store schema without the trusted migration ledger cannot be adopted.");
        if (attestationTableCount != 1)
            throw new InvalidOperationException("A non-empty Persona Store schema without its immutable catalog attestation cannot be adopted.");

        await using var ledger = CreateCommand(
            $"SELECT count(*) FROM {Qualified("schema_migrations")} WHERE migration_version = 1",
            connection,
            transaction);
        if (Convert.ToInt64(await ledger.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("A non-empty Persona Store schema lacks the exact trusted migration record.");
        return true;
    }

    private async Task AssertModuleObjectOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM pg_catalog.pg_namespace namespace
                 WHERE namespace.nspname = @schema_name
                   AND pg_catalog.pg_get_userbyid(namespace.nspowner) = @migrator_role),
                (SELECT count(*) FROM (
                    SELECT relation.relowner AS owner_id
                    FROM pg_catalog.pg_class relation
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema_name
                    UNION ALL
                    SELECT function_value.proowner
                    FROM pg_catalog.pg_proc function_value
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function_value.pronamespace
                    WHERE namespace.nspname = @schema_name
                    UNION ALL
                    SELECT type_value.typowner
                    FROM pg_catalog.pg_type type_value
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = type_value.typnamespace
                    WHERE namespace.nspname = @schema_name
                ) objects),
                (SELECT count(*) FROM (
                    SELECT relation.relowner AS owner_id
                    FROM pg_catalog.pg_class relation
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema_name
                    UNION ALL
                    SELECT function_value.proowner
                    FROM pg_catalog.pg_proc function_value
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function_value.pronamespace
                    WHERE namespace.nspname = @schema_name
                    UNION ALL
                    SELECT type_value.typowner
                    FROM pg_catalog.pg_type type_value
                    JOIN pg_catalog.pg_namespace namespace ON namespace.oid = type_value.typnamespace
                    WHERE namespace.nspname = @schema_name
                ) objects
                WHERE pg_catalog.pg_get_userbyid(objects.owner_id) <> @migrator_role)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema_name", _options.Schema);
        command.Parameters.AddWithValue("migrator_role", _options.MigratorRole);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(0) != 1 || reader.GetInt64(2) != 0 ||
            (!allowEmpty && reader.GetInt64(1) == 0))
            throw new UnauthorizedAccessException("Persona Store schema and module objects must be owned only by the configured migrator role.");
    }

    private async Task VerifyOrCreateCatalogAttestationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var catalogSha256 = await ComputeCatalogSha256Async(connection, transaction, cancellationToken);

        await using var read = CreateCommand(
            $"SELECT catalog_sha256 FROM {Qualified("schema_attestations")} WHERE migration_version = 1 FOR UPDATE",
            connection,
            transaction);
        var existing = await read.ExecuteScalarAsync(cancellationToken) as string;
        if (existing is null)
        {
            await using var insert = CreateCommand(
                $"INSERT INTO {Qualified("schema_attestations")}(migration_version, catalog_sha256) VALUES (1, @catalog_sha256)",
                connection,
                transaction);
            insert.Parameters.AddWithValue("catalog_sha256", catalogSha256);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (existing != catalogSha256)
        {
            throw new InvalidOperationException("Persona Store catalog shape differs from its immutable migration attestation.");
        }
    }

    private async Task VerifyOrCreateRequestHmacKeyAttestationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var digestBytes = SHA256.HashData(_options.RequestHmacKey);
        string expectedSha256;
        try { expectedSha256 = Convert.ToHexStringLower(digestBytes); }
        finally { CryptographicOperations.ZeroMemory(digestBytes); }

        await using (var insert = CreateCommand(
            $"INSERT INTO {Qualified("persona_request_hmac_key_attestations")}(key_name, key_sha256) " +
            "VALUES ('history-export-v1', @key_sha256) ON CONFLICT (key_name) DO NOTHING",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("key_sha256", expectedSha256);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = CreateCommand(
            $"SELECT key_sha256 FROM {Qualified("persona_request_hmac_key_attestations")} WHERE key_name = 'history-export-v1' FOR SHARE",
            connection,
            transaction);
        var actualSha256 = await read.ExecuteScalarAsync(cancellationToken) as string;
        if (actualSha256 is null || !PersonaMutationCanonicalizer.FixedTimeSha256Equals(actualSha256, expectedSha256))
            throw new InvalidOperationException("Persona history export HMAC key differs from its immutable deployment attestation.");
    }

    private async Task VerifyExistingCatalogAttestationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var existence = CreateCommand(
            "SELECT pg_catalog.to_regclass(@qualified_table) IS NOT NULL",
            connection,
            transaction);
        existence.Parameters.AddWithValue("qualified_table", _options.Schema + ".schema_attestations");
        if (!Convert.ToBoolean(await existence.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture))
            return;

        string? expected;
        await using (var read = CreateCommand(
            $"SELECT catalog_sha256 FROM {Qualified("schema_attestations")} WHERE migration_version = 1",
            connection,
            transaction))
        {
            expected = await read.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (expected is null)
            throw new InvalidOperationException("An existing Persona Store schema lacks its immutable catalog attestation.");
        var actual = await ComputeCatalogSha256Async(connection, transaction, cancellationToken);
        if (actual != expected)
            throw new InvalidOperationException("Persona Store catalog drift was detected before migration execution.");
    }

    private async Task<string> ComputeCatalogSha256Async(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<string[]>();
        await using (var catalog = CreateCommand(
            """
            SELECT kind, object_name, member_name, definition
            FROM (
                SELECT 'column'::text AS kind,
                       relation.relname::text AS object_name,
                       attribute.attnum::text || ':' || attribute.attname AS member_name,
                       pg_catalog.format_type(attribute.atttypid, attribute.atttypmod) || '|' ||
                       attribute.attnotnull::text || '|' || attribute.attidentity::text || '|' || attribute.attgenerated::text || '|' ||
                       COALESCE(pg_catalog.pg_get_expr(default_value.adbin, default_value.adrelid), '') || '|' ||
                       COALESCE((SELECT string_agg(item::text, '|' ORDER BY (item::text) COLLATE "C")
                                 FROM unnest(COALESCE(attribute.attacl, ARRAY[]::pg_catalog.aclitem[])) item), '') AS definition
                FROM pg_catalog.pg_attribute attribute
                JOIN pg_catalog.pg_class relation ON relation.oid = attribute.attrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                LEFT JOIN pg_catalog.pg_attrdef default_value
                  ON default_value.adrelid = attribute.attrelid AND default_value.adnum = attribute.attnum
                WHERE namespace.nspname = @schema_name
                  AND relation.relkind IN ('r', 'p')
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                UNION ALL
                SELECT 'constraint', relation.relname, constraint_value.conname,
                       pg_catalog.pg_get_constraintdef(constraint_value.oid, true)
                FROM pg_catalog.pg_constraint constraint_value
                JOIN pg_catalog.pg_class relation ON relation.oid = constraint_value.conrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'index', table_value.relname, index_value.relname,
                       pg_catalog.pg_get_indexdef(index_value.oid)
                FROM pg_catalog.pg_index index_link
                JOIN pg_catalog.pg_class index_value ON index_value.oid = index_link.indexrelid
                JOIN pg_catalog.pg_class table_value ON table_value.oid = index_link.indrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = table_value.relnamespace
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'trigger', relation.relname, trigger_value.tgname,
                       pg_catalog.pg_get_triggerdef(trigger_value.oid, true) || '|' || trigger_value.tgenabled::text
                FROM pg_catalog.pg_trigger trigger_value
                JOIN pg_catalog.pg_class relation ON relation.oid = trigger_value.tgrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = @schema_name AND NOT trigger_value.tgisinternal
                UNION ALL
                SELECT 'function', function_value.proname,
                       pg_catalog.pg_get_function_identity_arguments(function_value.oid),
                       pg_catalog.pg_get_functiondef(function_value.oid)
                FROM pg_catalog.pg_proc function_value
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function_value.pronamespace
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'schema-security', namespace.nspname, 'owner-acl',
                       pg_catalog.pg_get_userbyid(namespace.nspowner) || '|' ||
                       COALESCE((SELECT string_agg(item::text, '|' ORDER BY item::text)
                                 FROM unnest(COALESCE(namespace.nspacl, acldefault('n', namespace.nspowner))) item), '')
                FROM pg_catalog.pg_namespace namespace
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'relation-security', relation.relname, relation.relkind::text,
                       pg_catalog.pg_get_userbyid(relation.relowner) || '|' ||
                       COALESCE((SELECT string_agg(item::text, '|' ORDER BY item::text)
                                 FROM unnest(COALESCE(
                                     relation.relacl,
                                     acldefault(CASE WHEN relation.relkind = 'S' THEN 'S'::"char" ELSE 'r'::"char" END, relation.relowner))) item), '')
                FROM pg_catalog.pg_class relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = @schema_name
                  AND relation.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
                UNION ALL
                SELECT 'sequence-definition', relation.relname, 'parameters',
                       sequence_value.seqstart::text || '|' || sequence_value.seqincrement::text || '|' ||
                       sequence_value.seqmax::text || '|' || sequence_value.seqmin::text || '|' ||
                       sequence_value.seqcache::text || '|' || sequence_value.seqcycle::text
                FROM pg_catalog.pg_sequence sequence_value
                JOIN pg_catalog.pg_class relation ON relation.oid = sequence_value.seqrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'type-security', type_value.typname, type_value.typtype::text,
                       pg_catalog.pg_get_userbyid(type_value.typowner) || '|' ||
                       type_value.typcategory::text || '|' || type_value.typnotnull::text || '|' ||
                       type_value.typndims::text || '|' ||
                       COALESCE((SELECT string_agg(item::text, '|' ORDER BY item::text)
                                 FROM unnest(COALESCE(type_value.typacl, acldefault('T', type_value.typowner))) item), '')
                FROM pg_catalog.pg_type type_value
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = type_value.typnamespace
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'function-security', function_value.proname,
                       pg_catalog.pg_get_function_identity_arguments(function_value.oid),
                       pg_catalog.pg_get_userbyid(function_value.proowner) || '|' ||
                       function_value.prosecdef::text || '|' ||
                       COALESCE(array_to_string(function_value.proconfig, '|'), '') || '|' ||
                       language.lanname || '|' ||
                       COALESCE((SELECT string_agg(item::text, '|' ORDER BY item::text)
                                 FROM unnest(COALESCE(function_value.proacl, acldefault('f', function_value.proowner))) item), '')
                FROM pg_catalog.pg_proc function_value
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function_value.pronamespace
                JOIN pg_catalog.pg_language language ON language.oid = function_value.prolang
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'database-security', database_value.datname, 'owner-acl',
                       pg_catalog.pg_get_userbyid(database_value.datdba) || '|' ||
                       COALESCE((SELECT string_agg(item::text, '|' ORDER BY item::text)
                                 FROM unnest(COALESCE(database_value.datacl, acldefault('d', database_value.datdba))) item), '') || '|' ||
                       has_database_privilege(@runtime_role, database_value.datname, 'CONNECT')::text || '|' ||
                       has_database_privilege(@runtime_role, database_value.datname, 'CREATE')::text || '|' ||
                       has_database_privilege(@runtime_role, database_value.datname, 'TEMPORARY')::text
                FROM pg_catalog.pg_database database_value
                WHERE database_value.datname = current_database()
                UNION ALL
                SELECT 'role-security', role_value.rolname, 'attributes',
                       role_value.rolsuper::text || '|' || role_value.rolinherit::text || '|' ||
                       role_value.rolcreaterole::text || '|' || role_value.rolcreatedb::text || '|' ||
                       role_value.rolcanlogin::text || '|' || role_value.rolreplication::text || '|' ||
                       role_value.rolbypassrls::text || '|' ||
                       COALESCE((SELECT string_agg(parent.rolname, '|' ORDER BY parent.rolname)
                                 FROM pg_catalog.pg_auth_members membership
                                 JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
                                 WHERE membership.member = role_value.oid), '')
                FROM pg_catalog.pg_roles role_value
                WHERE role_value.rolname IN (@runtime_role, @migrator_role)
                UNION ALL
                SELECT 'effective-schema-security', @runtime_role, @schema_name,
                       has_schema_privilege(@runtime_role, @schema_name, 'USAGE')::text || '|' ||
                       has_schema_privilege(@runtime_role, @schema_name, 'CREATE')::text
                UNION ALL
                SELECT 'default-acl', pg_catalog.pg_get_userbyid(default_acl.defaclrole),
                       default_acl.defaclnamespace::text || ':' || default_acl.defaclobjtype::text,
                       COALESCE((SELECT string_agg(item::text, '|' ORDER BY item::text)
                                 FROM unnest(default_acl.defaclacl) item), '')
                FROM pg_catalog.pg_default_acl default_acl
                WHERE default_acl.defaclnamespace IN (0, (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @schema_name))
                  AND default_acl.defaclrole IN (
                      SELECT oid FROM pg_catalog.pg_roles WHERE rolname IN (@runtime_role, @migrator_role))
            ) inventory
            ORDER BY kind, object_name, member_name, definition
            """,
            connection,
            transaction))
        {
            catalog.Parameters.AddWithValue("schema_name", _options.Schema);
            catalog.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            catalog.Parameters.AddWithValue("migrator_role", _options.MigratorRole);
            await using var reader = await catalog.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)]);
        }
        if (rows.Count == 0) throw new InvalidOperationException("Persona Store catalog attestation inventory is empty.");
        return PersonaMutationCanonicalizer.HashUtf8(JsonSerializer.Serialize(rows));
    }

    private async Task VerifyRuntimeRoleBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var command = CreateCommand(
            """
            SELECT rolsuper, rolinherit, rolcreaterole, rolcreatedb, rolcanlogin, rolreplication, rolbypassrls
            FROM pg_catalog.pg_roles
            WHERE rolname = @runtime_role
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The configured Persona Store runtime role does not exist.");
            if (reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(3) ||
                !reader.GetBoolean(4) || reader.GetBoolean(5) || reader.GetBoolean(6))
            {
                throw new InvalidOperationException("The Persona Store runtime role has forbidden privilege, inheritance, or login settings.");
            }
        }

        await using (var membership = CreateCommand(
            """
            SELECT pg_has_role(@runtime_role, @migrator_role, 'MEMBER'),
                   (SELECT count(*) FROM pg_catalog.pg_auth_members membership
                    JOIN pg_catalog.pg_roles member_role ON member_role.oid = membership.member
                    WHERE member_role.rolname = @runtime_role)
            """,
            connection,
            transaction))
        {
            membership.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            membership.Parameters.AddWithValue("migrator_role", _options.MigratorRole);
            await using var reader = await membership.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetBoolean(0) || reader.GetInt64(1) != 0)
                throw new InvalidOperationException("The Persona Store runtime role must not be a member of the migrator role.");
        }

        await using (var ownership = CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM pg_catalog.pg_namespace namespace
                 WHERE namespace.nspname = @schema_name
                   AND pg_catalog.pg_get_userbyid(namespace.nspowner) = @runtime_role)
              + (SELECT count(*) FROM pg_catalog.pg_class relation
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                 WHERE namespace.nspname = @schema_name
                   AND pg_catalog.pg_get_userbyid(relation.relowner) = @runtime_role)
              + (SELECT count(*) FROM pg_catalog.pg_proc function
                 JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
                 WHERE namespace.nspname = @schema_name
                   AND pg_catalog.pg_get_userbyid(function.proowner) = @runtime_role)
            """,
            connection,
            transaction))
        {
            ownership.Parameters.AddWithValue("schema_name", _options.Schema);
            ownership.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            if (Convert.ToInt64(await ownership.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("The Persona Store runtime role must not own the schema, tables, sequences, or functions.");
        }

        var grants = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["schema_migrations"] = ["SELECT"],
            ["schema_attestations"] = ["SELECT"],
            ["binding_composition_state"] = ["SELECT"],
            ["persona_request_hmac_key_attestations"] = [],
            ["persona_hmac_keys"] = [],
            ["persona_revisions"] = ["SELECT"],
            ["trait_payloads"] = ["SELECT"],
            ["persona_current"] = ["SELECT"],
            ["idempotency_receipts"] = ["SELECT"],
            ["persona_export_receipts"] = [],
            ["persona_export_receipt_quarantine"] = [],
            ["outbox"] = ["SELECT"],
            ["outbox_dispatch_receipts"] = ["SELECT"],
            ["idempotency_quarantine"] = ["SELECT"],
            ["erasure_audit"] = ["SELECT"]
        };
        var privileges = new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" };
        foreach (var table in grants)
        {
            foreach (var privilege in privileges)
            {
                await using var command = CreateCommand(
                    "SELECT has_table_privilege(@runtime_role, @qualified_table, @privilege)",
                    connection,
                    transaction);
                command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
                command.Parameters.AddWithValue("qualified_table", _options.Schema + "." + table.Key);
                command.Parameters.AddWithValue("privilege", privilege);
                var actual = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                var expected = table.Value.Contains(privilege, StringComparer.Ordinal);
                if (actual != expected)
                    throw new InvalidOperationException($"Runtime ACL mismatch for {table.Key}:{privilege}.");
            }
        }

        await using (var columnAcl = CreateCommand(
            """
            SELECT count(*)
            FROM pg_catalog.pg_attribute attribute
            JOIN pg_catalog.pg_class relation ON relation.oid = attribute.attrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee
            WHERE namespace.nspname = @schema_name
              AND relation.relkind IN ('r', 'p')
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped
              AND (acl.grantee = 0 OR grantee.rolname = @runtime_role)
            """,
            connection,
            transaction))
        {
            columnAcl.Parameters.AddWithValue("schema_name", _options.Schema);
            columnAcl.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            if (Convert.ToInt64(await columnAcl.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Runtime or PUBLIC must not have explicit Persona Store column privileges.");
        }

        await using (var sequences = CreateCommand(
            """
            SELECT relation.relname,
                   has_sequence_privilege(@runtime_role, relation.oid, 'USAGE'),
                   has_sequence_privilege(@runtime_role, relation.oid, 'SELECT'),
                   has_sequence_privilege(@runtime_role, relation.oid, 'UPDATE')
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = @schema_name AND relation.relkind = 'S'
            ORDER BY relation.relname
            """,
            connection,
            transaction))
        {
            sequences.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            sequences.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var reader = await sequences.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(3))
                    throw new InvalidOperationException($"Runtime sequence ACL must deny all access to {reader.GetString(0)}.");
            }
        }

        await using (var schemaPrivilege = CreateCommand(
            "SELECT has_schema_privilege(@runtime_role, @schema_name, 'USAGE'), has_schema_privilege(@runtime_role, @schema_name, 'CREATE')",
            connection,
            transaction))
        {
            schemaPrivilege.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            schemaPrivilege.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var schemaReader = await schemaPrivilege.ExecuteReaderAsync(cancellationToken);
            if (!await schemaReader.ReadAsync(cancellationToken) || !schemaReader.GetBoolean(0) || schemaReader.GetBoolean(1))
                throw new InvalidOperationException("Runtime schema ACL must allow USAGE and deny CREATE.");
        }

        await using var databasePrivilege = CreateCommand(
            """
            SELECT has_database_privilege(@runtime_role, current_database(), 'CONNECT'),
                   has_database_privilege(@runtime_role, current_database(), 'CREATE'),
                   has_database_privilege(@runtime_role, current_database(), 'TEMPORARY')
            """,
            connection,
            transaction);
        databasePrivilege.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
        await using (var reader = await databasePrivilege.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0) || reader.GetBoolean(1))
                throw new InvalidOperationException("Runtime database ACL must allow CONNECT and deny CREATE; TEMPORARY is catalog-attested for deployment policy.");
        }

        var executableFunctions = new HashSet<string>(StringComparer.Ordinal)
        {
            "read_persona_hmac_key_v1(text, text, text, bigint)",
            "record_persona_outbox_dispatch_v1(uuid, text, timestamp with time zone)",
            "resolve_persona_receipt_v1(text, text, text, text, text, text)",
            "resolve_persona_export_receipt_v1(text, text, text, text, text)",
            "record_persona_export_receipt_v1(text, text, text, text, text, bigint, text, text, text, text, jsonb, bytea)",
            "mutate_persona_v1(text, text, text, text, bigint, jsonb, text[], text, text, text, text, timestamp with time zone, uuid, jsonb, text, text, bigint, bigint)"
        };
        await using var functions = CreateCommand(
            """
            SELECT function_value.proname || '(' || pg_catalog.oidvectortypes(function_value.proargtypes) || ')',
                   has_function_privilege(@runtime_role, function_value.oid, 'EXECUTE')
            FROM pg_catalog.pg_proc function_value
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function_value.pronamespace
            WHERE namespace.nspname = @schema_name
            ORDER BY 1
            """,
            connection,
            transaction);
        functions.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
        functions.Parameters.AddWithValue("schema_name", _options.Schema);
        await using (var reader = await functions.ExecuteReaderAsync(cancellationToken))
        {
            var seenExecutable = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var signature = reader.GetString(0);
                var actual = reader.GetBoolean(1);
                var expected = executableFunctions.Contains(signature);
                if (actual != expected)
                    throw new InvalidOperationException($"Runtime function ACL mismatch for {_options.Schema}.{signature}.");
                if (actual) seenExecutable.Add(signature);
            }
            if (!seenExecutable.SetEquals(executableFunctions))
                throw new InvalidOperationException("The Persona runtime atomic-function allowlist is incomplete.");
        }
    }

    private async Task VerifyRuntimeConnectionAsync(string migrationSha256, CancellationToken cancellationToken)
    {
        await using var runtime = new NpgsqlConnection(_options.RuntimeConnectionString);
        await runtime.OpenAsync(cancellationToken);
        await ConfigureUtcSessionAsync(runtime, cancellationToken);
        await AssertPostgresVersionAsync(runtime, cancellationToken);
        await using (var identity = new NpgsqlCommand("SELECT current_user, current_database()", runtime) { CommandTimeout = CommandTimeoutSeconds })
        await using (var reader = await identity.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != _options.RuntimeRole)
                throw new InvalidOperationException("Runtime connection did not authenticate as the attested runtime role.");
            var migratorDatabase = new NpgsqlConnectionStringBuilder(_options.MigratorConnectionString).Database;
            if (reader.GetString(1) != migratorDatabase)
                throw new InvalidOperationException("Runtime and migrator connections did not reach the same database.");
        }
        await VerifyMigrationLedgerAsync(runtime, transaction: null, migrationSha256, cancellationToken);
    }

    private static async Task<string> ReadMigrationAsync(CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(MigrationResource)
            ?? throw new InvalidOperationException($"Missing embedded migration resource '{MigrationResource}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private ValueTask InjectAsync(PersonaMutationStage stage, CancellationToken cancellationToken) => _faultInjector(stage, cancellationToken);
}
