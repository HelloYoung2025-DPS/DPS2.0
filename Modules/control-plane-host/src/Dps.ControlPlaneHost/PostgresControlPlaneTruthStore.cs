using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.ControlPlaneHost.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.ControlPlaneHost;

public sealed class PostgresControlPlaneTruthStoreOptions
{
    private readonly string _runtimeConnectionString;

    public PostgresControlPlaneTruthStoreOptions(
        string runtimeConnectionString,
        string schemaName,
        string runtimeRoleName,
        string migrationRoleName)
    {
        _runtimeConnectionString = runtimeConnectionString;
        SchemaName = schemaName;
        RuntimeRoleName = runtimeRoleName;
        MigrationRoleName = migrationRoleName;
    }

    public string SchemaName { get; }
    public string RuntimeRoleName { get; }
    public string MigrationRoleName { get; }

    internal string ValidatedConnectionString()
    {
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(SchemaName, nameof(SchemaName));
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(RuntimeRoleName, nameof(RuntimeRoleName));
        PostgresControlPlaneConnectionPolicy.RequireIdentifier(MigrationRoleName, nameof(MigrationRoleName));
        if (string.Equals(RuntimeRoleName, MigrationRoleName, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Migration and runtime roles must be distinct.");
        }
        return PostgresControlPlaneConnectionPolicy.Normalize(
            _runtimeConnectionString,
            "runtimeConnectionString",
            requiredUsername: RuntimeRoleName);
    }

    public override string ToString()
        => $"PostgresControlPlaneTruthStoreOptions {{ SchemaName = {SchemaName}, RuntimeRoleName = {RuntimeRoleName}, MigrationRoleName = {MigrationRoleName}, RuntimeConnectionString = [REDACTED] }}";
}

public sealed class ControlPlaneIdempotencyConflictException : InvalidOperationException
{
    public ControlPlaneIdempotencyConflictException()
        : base("The scoped idempotency key was already committed with a different record hash.")
    {
    }
}

public sealed record ControlPlaneOutboxRecord(
    Guid OutboxId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string PayloadSha256,
    ControlPlaneReceiptV1 Payload,
    DateTimeOffset CreatedAt);

public sealed record ControlPlaneQuarantineRecord(
    Guid QuarantineId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string SourceContractId,
    string ScopeSha256,
    string IdempotencyKeySha256,
    string ExistingRecordSha256,
    string IncomingRecordSha256,
    string Reason,
    DateTimeOffset CreatedAt);

internal enum ControlPlaneMutationStage
{
    TruthWritten,
    ReceiptWritten,
    OutboxWritten,
    BeforeCommit
}

internal delegate ValueTask ControlPlaneMutationFaultInjector(
    ControlPlaneMutationStage stage,
    CancellationToken cancellationToken);

public sealed class PostgresControlPlaneTruthStore
{
    private static readonly string[] TableNames =
    [
        "provider_trust_states",
        "runtime_truth",
        "idempotency_receipts",
        "idempotency_quarantine",
        "outbox"
    ];

    private static readonly HashSet<string> RequiredTriggers = BuildRequiredTriggers();
    private static readonly HashSet<string> RequiredColumns = BuildRequiredColumns();
    private static readonly HashSet<string> RequiredConstraints = BuildRequiredConstraints();
    private static readonly HashSet<string> RequiredIndexes = new(
    [
        IndexKey(
            "runtime_truth",
            "runtime_truth_exact_scope_idx",
            "soul_id", "device_binding_id", "platform_account_id",
            "source_contract_id", "occurred_at", "truth_id"),
        IndexKey(
            "idempotency_quarantine",
            "idempotency_quarantine_exact_scope_idx",
            "soul_id", "device_binding_id", "platform_account_id",
            "created_at", "quarantine_id"),
        IndexKey(
            "outbox",
            "outbox_exact_scope_idx",
            "soul_id", "device_binding_id", "platform_account_id",
            "created_at", "outbox_id")
    ],
    StringComparer.Ordinal);

    private readonly PostgresControlPlaneTruthStoreOptions _options;
    private readonly string _connectionString;
    private readonly string _expectedDatabase;
    private readonly int _expectedPort;
    private readonly string _qualifiedTruth;
    private readonly string _qualifiedProviderTrust;
    private readonly string _qualifiedReceipts;
    private readonly string _qualifiedQuarantine;
    private readonly string _qualifiedOutbox;
    private readonly string _qualifiedCommitAtom;
    private readonly string _qualifiedAppendQuarantine;
    private readonly ControlPlaneMutationFaultInjector? _faultInjector;
    private readonly TimeProvider _timeProvider;

    public PostgresControlPlaneTruthStore(PostgresControlPlaneTruthStoreOptions options)
        : this(options, faultInjector: null, TimeProvider.System)
    {
    }

    internal PostgresControlPlaneTruthStore(
        PostgresControlPlaneTruthStoreOptions options,
        ControlPlaneMutationFaultInjector? faultInjector,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.ValidatedConnectionString();
        var connectionBuilder = new NpgsqlConnectionStringBuilder(_connectionString);
        _expectedDatabase = connectionBuilder.Database
            ?? throw new InvalidOperationException("An explicit PostgreSQL database is required.");
        _expectedPort = connectionBuilder.Port;
        _options = options;
        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(options.SchemaName);
        _qualifiedProviderTrust = quotedSchema + ".provider_trust_states";
        _qualifiedTruth = quotedSchema + ".runtime_truth";
        _qualifiedReceipts = quotedSchema + ".idempotency_receipts";
        _qualifiedQuarantine = quotedSchema + ".idempotency_quarantine";
        _qualifiedOutbox = quotedSchema + ".outbox";
        _qualifiedCommitAtom = quotedSchema + ".commit_control_plane_atom";
        _qualifiedAppendQuarantine = quotedSchema + ".append_control_plane_quarantine";
        _faultInjector = faultInjector;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<ControlPlaneReceiptV1> IngestAsync(
        SignedProviderResultV1 signedResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signedResult);
        var immutableSignedResult = signedResult with
        {
            PayloadUtf8 = signedResult.PayloadUtf8.ToArray()
        };
        return IngestImmutableAsync(immutableSignedResult, cancellationToken);
    }

    private async Task<ControlPlaneReceiptV1> IngestImmutableAsync(
        SignedProviderResultV1 signedResult,
        CancellationToken cancellationToken = default)
    {
        var parsed = ProviderResultAuthorization.Parse(signedResult);
        var result = parsed.Result;
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenVerifiedAsync(cancellationToken);
        var businessKeySha256 = ControlPlaneCanonicalEncoding.ComputeBusinessKeySha256(result);
        var recordSha256 = ControlPlaneCanonicalEncoding.ComputeReceiptPayloadSha256(result);
        var scopeSha256 = ControlPlaneCanonicalEncoding.ComputeDomainSha256(
            "dps.control-plane-host.scope/v1",
            result.SoulId,
            result.DeviceBindingId,
            result.PlatformAccountId,
            result.SourceContractId);
        var idempotencyKeySha256 = ControlPlaneCanonicalEncoding.ComputeDomainSha256(
            "dps.control-plane-host.idempotency-key/v1",
            result.IdempotencyKey);
        var receipt = ControlPlaneResultPolicy.CreateReceipt(result, recordSha256);
        var receiptJson = SerializeReceipt(receipt);
        var outboxPayloadSha256 = Sha256Utf8(receiptJson);
        var providerAuthorizationSha256 =
            ProviderResultAuthorization.ComputeAuthorizationDigest(signedResult, parsed);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireContractLockAsync(
            connection,
            transaction,
            result.SourceContractId,
            cancellationToken);
        await AcquireBusinessKeyLockAsync(
            connection,
            transaction,
            businessKeySha256,
            cancellationToken);

        var existing = await ReadExistingAsync(
            connection,
            transaction,
            businessKeySha256,
            cancellationToken);
        if (existing is not null
            && FixedTimeEquals(existing.RecordSha256, recordSha256)
            && MatchesExactRedelivery(
                existing,
                signedResult,
                parsed,
                providerAuthorizationSha256))
        {
            var existingReceipt = DeserializeReceipt(existing.ReceiptJson);
            RequireExactReceipt(existingReceipt, result);
            await transaction.CommitAsync(cancellationToken);
            return existingReceipt;
        }

        var trust = await ReadCurrentProviderTrustAsync(
            connection,
            transaction,
            result.SourceContractId,
            cancellationToken);
        RequireCurrentProviderTrust(trust, signedResult, parsed);
        ProviderResultAuthorization.VerifySignature(
            signedResult,
            parsed,
            trust.ProviderPublicKeySpkiBase64,
            trust.ProviderPublicKeySha256);
        if (existing is not null)
        {
            if (FixedTimeEquals(existing.RecordSha256, recordSha256))
            {
                var existingReceipt = DeserializeReceipt(existing.ReceiptJson);
                RequireExactReceipt(existingReceipt, result);
                await transaction.CommitAsync(cancellationToken);
                return existingReceipt;
            }

            await InsertQuarantineAsync(
                connection,
                transaction,
                result,
                businessKeySha256,
                scopeSha256,
                idempotencyKeySha256,
                existing.RecordSha256,
                recordSha256,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new ControlPlaneIdempotencyConflictException();
        }

        var truthId = DeterministicGuid("truth", recordSha256);
        var outboxId = DeterministicGuid("outbox", recordSha256);
        var injectedFailure = await ResolveInjectedFailureAsync(cancellationToken);
        try
        {
            await CommitAtomAsync(
                connection,
                transaction,
                truthId,
                outboxId,
                result,
                receipt,
                businessKeySha256,
                scopeSha256,
                idempotencyKeySha256,
                recordSha256,
                signedResult,
                trust,
                providerAuthorizationSha256,
                outboxPayloadSha256,
                receiptJson,
                injectedFailure?.Stage,
                cancellationToken);
        }
        catch (PostgresException exception) when (
            injectedFailure is not null
            && exception.SqlState == PostgresErrorCodes.RaiseException
            && exception.MessageText.StartsWith(
                "injected control-plane crash",
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(injectedFailure.Exception)
                .Throw();
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    public async Task<ControlPlaneReceiptV1> GetAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string sourceContractId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ControlContractValidation.RequireSoulId(soulId);
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        ControlContractValidation.RequirePlatformAccountId(platformAccountId);
        ControlContractValidation.RequireText(sourceContractId, 96, nameof(sourceContractId));
        ControlContractValidation.RequireIdempotencyKey(idempotencyKey);
        var businessKeySha256 = ControlPlaneCanonicalEncoding.ComputeBusinessKeySha256(
            soulId,
            deviceBindingId,
            platformAccountId,
            sourceContractId,
            idempotencyKey);

        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"""
            SELECT receipt_json::text
            FROM {_qualifiedReceipts}
            WHERE business_key_sha256 = @business_key_sha256
              AND soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
              AND source_contract_id = @source_contract_id
            """,
            connection);
        command.Parameters.AddWithValue("business_key_sha256", businessKeySha256);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        command.Parameters.AddWithValue("source_contract_id", sourceContractId);
        var value = (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Unknown runtime truth receipt for the exact scope.");
        var receipt = DeserializeReceipt(value);
        if (!string.Equals(receipt.SoulId, soulId, StringComparison.Ordinal)
            || !string.Equals(receipt.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
            || !string.Equals(receipt.PlatformAccountId, platformAccountId, StringComparison.Ordinal)
            || !string.Equals(receipt.SourceContractId, sourceContractId, StringComparison.Ordinal)
            || !string.Equals(receipt.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persisted receipt scope does not match its exact lookup key.");
        }

        return receipt;
    }

    public async Task<IReadOnlyList<ControlPlaneOutboxRecord>> ReadPendingOutboxAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        ControlContractValidation.RequireSoulId(soulId);
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        ControlContractValidation.RequirePlatformAccountId(platformAccountId);
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"""
            SELECT outbox_id, payload_sha256, payload_json::text, created_at
            FROM {_qualifiedOutbox}
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            ORDER BY created_at, outbox_id
            """,
            connection);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        var values = new List<ControlPlaneOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = DeserializeReceipt(reader.GetString(2));
            if (!string.Equals(payload.SoulId, soulId, StringComparison.Ordinal)
                || !string.Equals(payload.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
                || !string.Equals(payload.PlatformAccountId, platformAccountId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Outbox payload escaped its exact identity scope.");
            }

            var expectedPayloadSha256 = Sha256Utf8(SerializeReceipt(payload));
            var storedPayloadSha256 = reader.GetString(1);
            if (!FixedTimeEquals(expectedPayloadSha256, storedPayloadSha256))
            {
                throw new InvalidDataException("Outbox payload checksum verification failed.");
            }

            values.Add(new ControlPlaneOutboxRecord(
                reader.GetGuid(0),
                soulId,
                deviceBindingId,
                platformAccountId,
                storedPayloadSha256,
                payload,
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return values;
    }

    public async Task<IReadOnlyList<ControlPlaneQuarantineRecord>> ReadQuarantineAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        ControlContractValidation.RequireSoulId(soulId);
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        ControlContractValidation.RequirePlatformAccountId(platformAccountId);
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command(
            $"""
            SELECT quarantine_id, source_contract_id, scope_sha256,
                   idempotency_key_sha256, existing_record_sha256,
                   incoming_record_sha256, reason, created_at
            FROM {_qualifiedQuarantine}
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            ORDER BY created_at, quarantine_id
            """,
            connection);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        var values = new List<ControlPlaneQuarantineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new ControlPlaneQuarantineRecord(
                reader.GetGuid(0),
                soulId,
                deviceBindingId,
                platformAccountId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return values;
    }

    public Task<long> CountTruthAsync(CancellationToken cancellationToken = default)
        => CountAsync(_qualifiedTruth, cancellationToken);

    public Task<long> CountReceiptsAsync(CancellationToken cancellationToken = default)
        => CountAsync(_qualifiedReceipts, cancellationToken);

    public Task<long> CountQuarantineAsync(CancellationToken cancellationToken = default)
        => CountAsync(_qualifiedQuarantine, cancellationToken);

    public Task<long> CountOutboxAsync(CancellationToken cancellationToken = default)
        => CountAsync(_qualifiedOutbox, cancellationToken);

    private async Task<long> CountAsync(string qualifiedTable, CancellationToken cancellationToken)
    {
        await using var connection = await OpenVerifiedAsync(cancellationToken);
        await using var command = Command($"SELECT count(*) FROM {qualifiedTable}", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<NpgsqlConnection> OpenVerifiedAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await PostgresControlPlaneConnectionPolicy.RequirePostgres184Async(
                connection,
                _expectedDatabase,
                _expectedPort,
                cancellationToken);
            await PostgresControlPlaneConnectionPolicy.ConfigureAndVerifyTimeoutsAsync(
                connection,
                cancellationToken);
            await VerifyRuntimeRoleAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<ProviderTrustStateV1> ReadCurrentProviderTrustAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceContractId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            SELECT revision, source_contract_id, source_producer_module,
                   active_release_bom_sha256, provider_key_id,
                   provider_public_key_spki_base64, provider_public_key_sha256,
                   status, valid_from, valid_until
            FROM {_qualifiedProviderTrust}
            WHERE source_contract_id = @source_contract_id
            ORDER BY revision DESC
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("source_contract_id", sourceContractId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new UnauthorizedAccessException(
                "No current provider trust state exists for the source contract.");
        }

        var state = new ProviderTrustStateV1(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("Current provider trust query was ambiguous.");
        }

        ProviderResultAuthorization.ValidateTrustState(state);
        return state;
    }

    private void RequireCurrentProviderTrust(
        ProviderTrustStateV1 trust,
        SignedProviderResultV1 signedResult,
        ParsedProviderResult parsed)
    {
        var now = _timeProvider.GetUtcNow();
        if (!string.Equals(trust.Status, "ACTIVE", StringComparison.Ordinal)
            || !string.Equals(
                trust.SourceContractId,
                parsed.Result.SourceContractId,
                StringComparison.Ordinal)
            || !string.Equals(
                trust.SourceProducerModule,
                parsed.Result.SourceProducerModule,
                StringComparison.Ordinal)
            || !string.Equals(
                trust.ActiveReleaseBomSha256,
                signedResult.ActiveReleaseBomSha256,
                StringComparison.Ordinal)
            || !string.Equals(trust.ProviderKeyId, signedResult.ProviderKeyId, StringComparison.Ordinal)
            || now < trust.ValidFrom
            || now > trust.ValidUntil
            || parsed.Result.OccurredAt < trust.ValidFrom
            || parsed.Result.OccurredAt > trust.ValidUntil)
        {
            throw new UnauthorizedAccessException(
                "Provider result is not authorized by the current active BOM and key window.");
        }
    }

    private async Task VerifyRuntimeRoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var roleCommand = Command(
            """
            SELECT session_user::text,
                   current_user::text,
                   r.rolcanlogin,
                   r.rolsuper,
                   r.rolcreatedb,
                   r.rolcreaterole,
                   r.rolreplication,
                   r.rolbypassrls,
                   NOT r.rolinherit,
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_auth_members m
                       WHERE m.member = r.oid OR m.roleid = r.oid),
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_database d
                       WHERE d.datname = current_database() AND d.datdba = r.oid),
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_namespace n
                       WHERE n.nspname = @schema_name
                         AND n.nspowner <> (
                             SELECT owner.oid FROM pg_catalog.pg_roles owner
                             WHERE owner.rolname = @migration_role_name)),
                   has_schema_privilege(r.rolname, @schema_name, 'USAGE'),
                   NOT has_schema_privilege(r.rolname, @schema_name, 'CREATE'),
                   NOT has_database_privilege(r.rolname, current_database(), 'CREATE'),
                   NOT has_database_privilege(r.rolname, current_database(), 'TEMP'),
                   current_setting('session_replication_role') = 'origin',
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_namespace every_schema
                       WHERE has_schema_privilege(r.rolname, every_schema.oid, 'CREATE'))
            FROM pg_catalog.pg_roles r
            WHERE r.rolname::text = session_user::text
            """,
            connection))
        {
            roleCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            roleCommand.Parameters.AddWithValue("migration_role_name", _options.MigrationRoleName);
            await using var reader = await roleCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !string.Equals(reader.GetString(0), _options.RuntimeRoleName, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), _options.RuntimeRoleName, StringComparison.Ordinal)
                || !reader.GetBoolean(2)
                || reader.GetBoolean(3)
                || reader.GetBoolean(4)
                || reader.GetBoolean(5)
                || reader.GetBoolean(6)
                || reader.GetBoolean(7)
                || Enumerable.Range(8, 10).Any(index => !reader.GetBoolean(index))
                || await reader.ReadAsync(cancellationToken))
            {
                throw new UnauthorizedAccessException(
                    "Control Plane runtime connection is not an isolated least-privilege login role.");
            }
        }

        var observedRelations = new HashSet<string>(StringComparer.Ordinal);
        await using (var inventoryCommand = Command(
            """
            SELECT c.relname::text, c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema_name
              AND pg_catalog.obj_description(n.oid, 'pg_namespace') =
                  'dps.control-plane-host.constraint-definition-baseline/v1'
              AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
            ORDER BY c.relkind, c.relname
            """,
            connection))
        {
            inventoryCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            await using var reader = await inventoryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                observedRelations.Add(string.Concat(
                    reader.GetString(1),
                    "\u001f",
                    reader.GetString(0)));
            }
        }
        var expectedRelations = new HashSet<string>(
            TableNames.Select(static table => string.Concat("r\u001f", table)),
            StringComparer.Ordinal);
        if (!observedRelations.SetEquals(expectedRelations))
        {
            throw new UnauthorizedAccessException(
                "Control Plane schema contains an unregistered table, partition, view, sequence, or foreign relation.");
        }

        var observedTables = new HashSet<string>(StringComparer.Ordinal);
        await using (var tableCommand = Command(
            """
            SELECT c.relname::text,
                   c.relowner = owner.oid,
                   has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'SELECT'),
                   has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'INSERT'),
                   NOT has_any_column_privilege(r.rolname, c.oid, 'INSERT'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'UPDATE'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'DELETE'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'TRUNCATE'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'REFERENCES'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'TRIGGER'),
                   NOT has_table_privilege(r.rolname, format('%I.%I', n.nspname, c.relname), 'MAINTAIN'),
                   NOT has_any_column_privilege(r.rolname, c.oid, 'UPDATE'),
                   NOT has_any_column_privilege(r.rolname, c.oid, 'REFERENCES'),
                   c.relpersistence = 'p',
                   NOT c.relispartition,
                   NOT c.relrowsecurity,
                   NOT c.relforcerowsecurity,
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_inherits inheritance
                       WHERE inheritance.inhrelid = c.oid OR inheritance.inhparent = c.oid),
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_rewrite rule_value
                       WHERE rule_value.ev_class = c.oid),
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_attribute attribute_value
                       WHERE attribute_value.attrelid = c.oid
                         AND attribute_value.attnum > 0
                         AND NOT attribute_value.attisdropped
                         AND attribute_value.attacl IS NOT NULL)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_roles r ON r.rolname::text = session_user::text
            JOIN pg_catalog.pg_roles owner ON owner.rolname = @migration_role_name
            WHERE n.nspname = @schema_name
              AND c.relkind = 'r'
              AND c.relname = ANY(@table_names)
            ORDER BY c.relname
            """,
            connection))
        {
            tableCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            tableCommand.Parameters.AddWithValue("table_names", TableNames);
            tableCommand.Parameters.AddWithValue("migration_role_name", _options.MigrationRoleName);
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tableName = reader.GetString(0);
                if (!reader.GetBoolean(1)
                    || !reader.GetBoolean(2)
                    || reader.GetBoolean(3)
                    || !Enumerable.Range(4, 16).All(index => reader.GetBoolean(index)))
                {
                    throw new UnauthorizedAccessException(
                        "Control Plane runtime table ownership or ACL is not append-only least privilege.");
                }

                observedTables.Add(tableName);
            }
        }

        if (!observedTables.SetEquals(TableNames))
        {
            throw new UnauthorizedAccessException("Control Plane runtime table attestation is incomplete.");
        }

        await VerifySchemaShapeAsync(connection, cancellationToken);

        await VerifyFunctionsAsync(connection, cancellationToken);
        await VerifyObjectAclsAsync(connection, cancellationToken);

        var observedTriggers = new HashSet<string>(StringComparer.Ordinal);
        await using (var triggerCommand = Command(
            """
            SELECT c.relname::text,
                   t.tgname::text,
                   t.tgtype::integer,
                   p.proname::text,
                   t.tgenabled = 'O',
                   p.proowner = owner.oid,
                   NOT p.prosecdef,
                   pn.nspname = @schema_name,
                   p.proconfig = ARRAY['search_path=pg_catalog']::text[],
                   t.tgparentid = 0,
                   t.tgconstraint = 0,
                   t.tgnargs = 0,
                   cardinality(t.tgattr) = 0,
                   t.tgqual IS NULL
            FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
            JOIN pg_catalog.pg_namespace pn ON pn.oid = p.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.rolname = @migration_role_name
            WHERE n.nspname = @schema_name
              AND c.relname = ANY(@table_names)
              AND NOT t.tgisinternal
            ORDER BY t.tgname
            """,
            connection))
        {
            triggerCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            triggerCommand.Parameters.AddWithValue("table_names", TableNames);
            triggerCommand.Parameters.AddWithValue("migration_role_name", _options.MigrationRoleName);
            await using var reader = await triggerCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Enumerable.Range(4, 10).All(index => reader.GetBoolean(index)))
                {
                    throw new UnauthorizedAccessException(
                        "Control Plane trigger function, owner, mode, or search path is unsafe.");
                }

                observedTriggers.Add(TriggerKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3)));
            }
        }

        if (!observedTriggers.SetEquals(RequiredTriggers))
        {
            throw new UnauthorizedAccessException(
                "Control Plane append-only trigger attestation is incomplete or ambiguous.");
        }
    }

    private async Task VerifySchemaShapeAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var observedColumns = new HashSet<string>(StringComparer.Ordinal);
        await using (var columnCommand = Command(
            """
            SELECT c.relname::text,
                   a.attname::text,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   a.attnotnull,
                   COALESCE(pg_catalog.pg_get_expr(default_value.adbin, default_value.adrelid), ''),
                   a.attidentity = '',
                   a.attgenerated = '',
                   COALESCE(collation.collname, ''),
                   CASE WHEN a.atttypid = 'pg_catalog.text'::regtype
                        THEN a.attcollation = 'pg_catalog."C"'::regcollation
                        ELSE a.attcollation = 0
                   END
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_attrdef default_value
              ON default_value.adrelid = a.attrelid AND default_value.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_collation collation ON collation.oid = a.attcollation
            WHERE n.nspname = @schema_name
              AND c.relname = ANY(@table_names)
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY c.relname, a.attnum
            """,
            connection))
        {
            columnCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            columnCommand.Parameters.AddWithValue("table_names", TableNames);
            await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(3)
                    || !reader.GetBoolean(5)
                    || !reader.GetBoolean(6)
                    || !reader.GetBoolean(8))
                {
                    throw new UnauthorizedAccessException(
                        "Control Plane column nullability, identity, generated shape, or collation is unsafe.");
                }
                observedColumns.Add(ColumnKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(4),
                    reader.GetString(7)));
            }
        }

        if (!observedColumns.SetEquals(RequiredColumns))
        {
            throw new UnauthorizedAccessException(
                "Control Plane table column attestation detected schema drift.");
        }

        var observedConstraints = new HashSet<string>(StringComparer.Ordinal);
        await using (var constraintCommand = Command(
            """
            SELECT c.relname::text,
                   constraint_value.conname::text,
                   constraint_value.contype::text,
                   constraint_value.convalidated,
                   NOT constraint_value.condeferrable,
                   NOT constraint_value.condeferred,
                   CASE WHEN constraint_value.contype = 'f'
                        THEN constraint_value.confdeltype = 'r'
                             AND constraint_value.confupdtype = 'a'
                             AND constraint_value.confmatchtype = 's'
                        ELSE true END,
                   NOT constraint_value.connoinherit,
                   constraint_value.conenforced,
                   pg_catalog.obj_description(
                       constraint_value.oid,
                       'pg_constraint') =
                       'dps.control-plane-host.constraint-definition-baseline/v1'
                       || E'\n'
                       || pg_catalog.pg_get_constraintdef(constraint_value.oid, false)
            FROM pg_catalog.pg_constraint constraint_value
            JOIN pg_catalog.pg_class c ON c.oid = constraint_value.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema_name
              AND c.relname = ANY(@table_names)
              AND constraint_value.contype <> 'n'
            ORDER BY c.relname, constraint_value.conname
            """,
            connection))
        {
            constraintCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            constraintCommand.Parameters.AddWithValue("table_names", TableNames);
            await using var reader = await constraintCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Enumerable.Range(3, 7).All(index => reader.GetBoolean(index)))
                {
                    throw new UnauthorizedAccessException(
                        "Control Plane constraint is unvalidated, unenforced, deferred, inheritable, unbound to its exact baseline, or has unsafe foreign-key actions.");
                }

                observedConstraints.Add(ConstraintKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)[0]));
            }
        }

        if (!observedConstraints.SetEquals(RequiredConstraints))
        {
            throw new UnauthorizedAccessException(
                "Control Plane constraint attestation detected schema drift.");
        }

        var observedIndexes = new HashSet<string>(StringComparer.Ordinal);
        await using (var indexCommand = Command(
            """
            SELECT table_value.relname::text,
                   index_value.relname::text,
                   access_method.amname = 'btree',
                   NOT index_state.indisunique,
                   index_state.indisvalid,
                   index_state.indisready,
                   index_state.indislive,
                   index_state.indpred IS NULL,
                   index_state.indexprs IS NULL,
                   index_state.indnkeyatts = index_state.indnatts,
                   ARRAY(
                       SELECT attribute_value.attname::text
                       FROM unnest(index_state.indkey) WITH ORDINALITY key_value(attnum, position)
                       JOIN pg_catalog.pg_attribute attribute_value
                         ON attribute_value.attrelid = index_state.indrelid
                        AND attribute_value.attnum = key_value.attnum
                       ORDER BY key_value.position)
            FROM pg_catalog.pg_index index_state
            JOIN pg_catalog.pg_class table_value ON table_value.oid = index_state.indrelid
            JOIN pg_catalog.pg_class index_value ON index_value.oid = index_state.indexrelid
            JOIN pg_catalog.pg_namespace namespace_value ON namespace_value.oid = table_value.relnamespace
            JOIN pg_catalog.pg_am access_method ON access_method.oid = index_value.relam
            WHERE namespace_value.nspname = @schema_name
              AND table_value.relname = ANY(@table_names)
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_constraint constraint_value
                  WHERE constraint_value.conindid = index_state.indexrelid)
            ORDER BY table_value.relname, index_value.relname
            """,
            connection))
        {
            indexCommand.Parameters.AddWithValue("schema_name", _options.SchemaName);
            indexCommand.Parameters.AddWithValue("table_names", TableNames);
            await using var reader = await indexCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Enumerable.Range(2, 8).All(index => reader.GetBoolean(index)))
                {
                    throw new UnauthorizedAccessException(
                        "Control Plane scope index is invalid, partial, expressive, or non-btree.");
                }

                observedIndexes.Add(IndexKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetFieldValue<string[]>(10)));
            }
        }

        if (!observedIndexes.SetEquals(RequiredIndexes))
        {
            throw new UnauthorizedAccessException(
                "Control Plane independent index attestation detected schema drift.");
        }
    }

    private async Task VerifyFunctionsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var bodyHashes = LoadExpectedFunctionBodyHashes(
            _options.SchemaName,
            _options.RuntimeRoleName);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            FunctionKey(
                "receipt_json_has_exact_keys", "jsonb", "boolean",
                "sql", "i", "s", false, true,
                bodyHashes["receipt_json_has_exact_keys"]),
            FunctionKey(
                "reject_control_plane_row_mutation", string.Empty, "trigger",
                "plpgsql", "v", "u", false, false,
                bodyHashes["reject_control_plane_row_mutation"]),
            FunctionKey(
                "reject_control_plane_truncate", string.Empty, "trigger",
                "plpgsql", "v", "u", false, false,
                bodyHashes["reject_control_plane_truncate"]),
            FunctionKey(
                "enforce_runtime_truth_provider_trust", string.Empty, "trigger",
                "plpgsql", "v", "u", false, false,
                bodyHashes["enforce_runtime_truth_provider_trust"]),
            FunctionKey(
                "enforce_receipt_truth_link", string.Empty, "trigger",
                "plpgsql", "v", "u", false, false,
                bodyHashes["enforce_receipt_truth_link"]),
            FunctionKey(
                "enforce_outbox_receipt_link", string.Empty, "trigger",
                "plpgsql", "v", "u", false, false,
                bodyHashes["enforce_outbox_receipt_link"]),
            FunctionKey(
                "commit_control_plane_atom",
                "uuid, text, text, text, text, text, text, text, text, text, text, text, text, timestamp with time zone, text, bytea, text, text, text, bigint, text, text, text, text, jsonb, uuid, text, text",
                "void", "plpgsql", "v", "u", true, true,
                bodyHashes["commit_control_plane_atom"]),
            FunctionKey(
                "append_control_plane_quarantine",
                "uuid, text, text, text, text, text, text, text, text, text",
                "void", "plpgsql", "v", "u", true, true,
                bodyHashes["append_control_plane_quarantine"])
        };

        var observed = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Command(
            """
            SELECT p.proname::text,
                   pg_catalog.oidvectortypes(p.proargtypes),
                   pg_catalog.pg_get_function_result(p.oid),
                   language_value.lanname::text,
                   p.provolatile::text,
                   p.proparallel::text,
                   p.prosecdef,
                   p.proconfig = ARRAY['search_path=pg_catalog']::text[],
                   p.proowner = owner.oid,
                   has_function_privilege(session_user, p.oid, 'EXECUTE'),
                   NOT has_function_privilege('public', p.oid, 'EXECUTE'),
                   NOT p.proisstrict,
                   NOT p.proretset,
                   p.prokind = 'f',
                   NOT p.proleakproof,
                   p.pronargdefaults = 0,
                   p.prosrc::text
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace namespace_value ON namespace_value.oid = p.pronamespace
            JOIN pg_catalog.pg_language language_value ON language_value.oid = p.prolang
            JOIN pg_catalog.pg_roles owner ON owner.rolname = @migration_role_name
            WHERE namespace_value.nspname = @schema_name
            ORDER BY p.proname, pg_catalog.oidvectortypes(p.proargtypes)
            """,
            connection);
        command.Parameters.AddWithValue("schema_name", _options.SchemaName);
        command.Parameters.AddWithValue("migration_role_name", _options.MigrationRoleName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var runtimeExecuteExpected = name is
                "receipt_json_has_exact_keys"
                or "commit_control_plane_atom"
                or "append_control_plane_quarantine";
            var securityDefinerExpected = name is
                "commit_control_plane_atom"
                or "append_control_plane_quarantine";
            if (reader.GetBoolean(6) != securityDefinerExpected
                || !reader.GetBoolean(7)
                || !reader.GetBoolean(8)
                || reader.GetBoolean(9) != runtimeExecuteExpected
                || !reader.GetBoolean(10)
                || !Enumerable.Range(11, 5).All(index => reader.GetBoolean(index)))
            {
                throw new UnauthorizedAccessException(
                    "Control Plane function security mode, call convention, owner, search path, or ACL is unsafe.");
            }

            observed.Add(FunctionKey(
                name,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetBoolean(9),
                Sha256Utf8(reader.GetString(16))));
        }

        if (!observed.SetEquals(expected))
        {
            throw new UnauthorizedAccessException(
                "Control Plane function catalog or body attestation detected drift.");
        }
    }

    private async Task VerifyObjectAclsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            AclKey("schema", _options.SchemaName, _options.RuntimeRoleName, "USAGE")
        };
        foreach (var table in TableNames)
        {
            expected.Add(AclKey("table", table, _options.RuntimeRoleName, "SELECT"));
        }
        expected.Add(AclKey(
            "function",
            "receipt_json_has_exact_keys(jsonb)",
            _options.RuntimeRoleName,
            "EXECUTE"));
        expected.Add(AclKey(
            "function",
            "commit_control_plane_atom(uuid, text, text, text, text, text, text, text, text, text, text, text, text, timestamp with time zone, text, bytea, text, text, text, bigint, text, text, text, text, jsonb, uuid, text, text)",
            _options.RuntimeRoleName,
            "EXECUTE"));
        expected.Add(AclKey(
            "function",
            "append_control_plane_quarantine(uuid, text, text, text, text, text, text, text, text, text)",
            _options.RuntimeRoleName,
            "EXECUTE"));

        var observed = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Command(
            """
            WITH object_acl AS (
                SELECT 'schema'::text AS object_kind,
                       namespace_value.nspname::text AS object_name,
                       namespace_value.nspowner AS owner_oid,
                       acl_value.*
                FROM pg_catalog.pg_namespace namespace_value
                CROSS JOIN LATERAL aclexplode(COALESCE(
                    namespace_value.nspacl,
                    acldefault('n', namespace_value.nspowner))) acl_value
                WHERE namespace_value.nspname = @schema_name
                UNION ALL
                SELECT 'table'::text,
                       table_value.relname::text,
                       table_value.relowner,
                       acl_value.*
                FROM pg_catalog.pg_class table_value
                JOIN pg_catalog.pg_namespace namespace_value ON namespace_value.oid = table_value.relnamespace
                CROSS JOIN LATERAL aclexplode(COALESCE(
                    table_value.relacl,
                    acldefault('r', table_value.relowner))) acl_value
                WHERE namespace_value.nspname = @schema_name
                  AND table_value.relname = ANY(@table_names)
                UNION ALL
                SELECT 'function'::text,
                       procedure_value.proname::text || '(' ||
                           pg_catalog.oidvectortypes(procedure_value.proargtypes) || ')',
                       procedure_value.proowner,
                       acl_value.*
                FROM pg_catalog.pg_proc procedure_value
                JOIN pg_catalog.pg_namespace namespace_value ON namespace_value.oid = procedure_value.pronamespace
                CROSS JOIN LATERAL aclexplode(COALESCE(
                    procedure_value.proacl,
                    acldefault('f', procedure_value.proowner))) acl_value
                WHERE namespace_value.nspname = @schema_name)
            SELECT object_acl.object_kind,
                   object_acl.object_name,
                   CASE WHEN object_acl.grantee = 0 THEN 'PUBLIC' ELSE grantee_role.rolname::text END,
                   object_acl.privilege_type::text,
                   NOT object_acl.is_grantable,
                   object_acl.grantor = object_acl.owner_oid
            FROM object_acl
            LEFT JOIN pg_catalog.pg_roles grantee_role ON grantee_role.oid = object_acl.grantee
            WHERE object_acl.grantee <> object_acl.owner_oid
            ORDER BY object_acl.object_kind, object_acl.object_name,
                     object_acl.grantee, object_acl.privilege_type
            """,
            connection);
        command.Parameters.AddWithValue("schema_name", _options.SchemaName);
        command.Parameters.AddWithValue("table_names", TableNames);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(2) || !reader.GetBoolean(4) || !reader.GetBoolean(5))
            {
                throw new UnauthorizedAccessException(
                    "Control Plane object ACL has an unknown grantee, grant option, or non-owner grantor.");
            }

            observed.Add(AclKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        if (!observed.SetEquals(expected))
        {
            throw new UnauthorizedAccessException(
                "Control Plane schema, table, or function ACL attestation detected drift.");
        }
    }

    private static async Task AcquireBusinessKeyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string businessKeySha256,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT pg_advisory_xact_lock(hashtextextended(@business_key_sha256, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("business_key_sha256", businessKeySha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireContractLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceContractId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT pg_advisory_xact_lock(hashtextextended(@contract_id, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("contract_id", sourceContractId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ExistingReceipt?> ReadExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string businessKeySha256,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            SELECT truth.record_sha256,
                   receipt.record_sha256,
                   receipt.receipt_json::text,
                   outbox.record_sha256,
                   outbox.payload_json::text,
                   truth.source_payload_sha256,
                   truth.source_payload_bytes,
                   truth.active_release_bom_sha256,
                   truth.provider_key_id,
                   truth.provider_signature_base64,
                   truth.provider_authorization_sha256
            FROM {_qualifiedTruth} truth
            LEFT JOIN {_qualifiedReceipts} receipt
              ON receipt.truth_id = truth.truth_id
             AND receipt.business_key_sha256 = truth.business_key_sha256
            LEFT JOIN {_qualifiedOutbox} outbox
              ON outbox.receipt_id = receipt.receipt_id
             AND outbox.business_key_sha256 = receipt.business_key_sha256
            WHERE truth.business_key_sha256 = @business_key_sha256
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("business_key_sha256", businessKeySha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (reader.IsDBNull(1)
            || reader.IsDBNull(2)
            || reader.IsDBNull(3)
            || reader.IsDBNull(4)
            || !string.Equals(reader.GetString(0), reader.GetString(1), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(0), reader.GetString(3), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Persisted runtime truth is missing its exact receipt or outbox atom.");
        }

        if (!string.Equals(reader.GetString(2), reader.GetString(4), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Persisted outbox payload differs from its exact receipt atom.");
        }

        var value = new ExistingReceipt(
            reader.GetString(0),
            reader.GetString(2),
            reader.GetString(5),
            reader.GetFieldValue<byte[]>(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("Scoped idempotency receipt is not unique.");
        }

        return value;
    }

    private async Task CommitAtomAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid truthId,
        Guid outboxId,
        ModuleResultEnvelope result,
        ControlPlaneReceiptV1 receipt,
        string businessKeySha256,
        string scopeSha256,
        string idempotencyKeySha256,
        string recordSha256,
        SignedProviderResultV1 signedResult,
        ProviderTrustStateV1 trust,
        string providerAuthorizationSha256,
        string outboxPayloadSha256,
        string receiptJson,
        ControlPlaneMutationStage? abortAfterStage,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            SELECT {_qualifiedCommitAtom}(
                @truth_id, @business_key_sha256, @scope_sha256,
                @idempotency_key_sha256, @record_sha256, @schema_version,
                @source_contract_id, @source_producer_module, @soul_id,
                @device_binding_id, @platform_account_id, @trace_id,
                @idempotency_key, @occurred_at, @source_payload_sha256,
                @source_payload_bytes, @result_status,
                @active_release_bom_sha256, @provider_key_id,
                @provider_trust_revision, @provider_public_key_sha256,
                @provider_signature_base64, @provider_authorization_sha256,
                @receipt_id, @receipt_json, @outbox_id,
                @outbox_payload_sha256, @abort_after_stage)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("truth_id", truthId);
        command.Parameters.AddWithValue("business_key_sha256", businessKeySha256);
        command.Parameters.AddWithValue("scope_sha256", scopeSha256);
        command.Parameters.AddWithValue("idempotency_key_sha256", idempotencyKeySha256);
        command.Parameters.AddWithValue("record_sha256", recordSha256);
        command.Parameters.AddWithValue("schema_version", result.SchemaVersion);
        command.Parameters.AddWithValue("source_contract_id", result.SourceContractId);
        command.Parameters.AddWithValue("source_producer_module", result.SourceProducerModule);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", result.OccurredAt);
        command.Parameters.AddWithValue("source_payload_sha256", result.SourcePayloadSha256);
        command.Parameters.AddWithValue(
            "source_payload_bytes",
            NpgsqlDbType.Bytea,
            signedResult.PayloadUtf8.ToArray());
        command.Parameters.AddWithValue("result_status", result.ResultStatus);
        command.Parameters.AddWithValue(
            "active_release_bom_sha256",
            signedResult.ActiveReleaseBomSha256);
        command.Parameters.AddWithValue("provider_key_id", signedResult.ProviderKeyId);
        command.Parameters.AddWithValue("provider_trust_revision", trust.Revision);
        command.Parameters.AddWithValue(
            "provider_public_key_sha256",
            trust.ProviderPublicKeySha256);
        command.Parameters.AddWithValue(
            "provider_signature_base64",
            signedResult.SignatureBase64);
        command.Parameters.AddWithValue(
            "provider_authorization_sha256",
            providerAuthorizationSha256);
        command.Parameters.AddWithValue("receipt_id", receipt.ReceiptId);
        command.Parameters.AddWithValue("receipt_json", NpgsqlDbType.Jsonb, receiptJson);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("outbox_payload_sha256", outboxPayloadSha256);
        command.Parameters.Add("abort_after_stage", NpgsqlDbType.Text).Value =
            abortAfterStage?.ToString() ?? (object)DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ModuleResultEnvelope result,
        string businessKeySha256,
        string scopeSha256,
        string idempotencyKeySha256,
        string existingRecordSha256,
        string incomingRecordSha256,
        CancellationToken cancellationToken)
    {
        var quarantineId = DeterministicGuid(
            "quarantine",
            businessKeySha256 + existingRecordSha256 + incomingRecordSha256);
        await using var command = Command(
            $"""
            SELECT {_qualifiedAppendQuarantine}(
                @quarantine_id, @business_key_sha256, @soul_id,
                @device_binding_id, @platform_account_id, @source_contract_id,
                @scope_sha256, @idempotency_key_sha256,
                @existing_record_sha256, @incoming_record_sha256)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("quarantine_id", quarantineId);
        command.Parameters.AddWithValue("business_key_sha256", businessKeySha256);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("source_contract_id", result.SourceContractId);
        command.Parameters.AddWithValue("scope_sha256", scopeSha256);
        command.Parameters.AddWithValue("idempotency_key_sha256", idempotencyKeySha256);
        command.Parameters.AddWithValue("existing_record_sha256", existingRecordSha256);
        command.Parameters.AddWithValue("incoming_record_sha256", incomingRecordSha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private ValueTask InjectAsync(
        ControlPlaneMutationStage stage,
        CancellationToken cancellationToken)
        => _faultInjector?.Invoke(stage, cancellationToken) ?? ValueTask.CompletedTask;

    private async Task<InjectedFailure?> ResolveInjectedFailureAsync(
        CancellationToken cancellationToken)
    {
        if (_faultInjector is null)
        {
            return null;
        }

        foreach (var stage in Enum.GetValues<ControlPlaneMutationStage>())
        {
            try
            {
                await InjectAsync(stage, cancellationToken);
            }
            catch (Exception exception) when (
                exception is not StackOverflowException
                and not OutOfMemoryException
                and not AccessViolationException)
            {
                return new InjectedFailure(stage, exception);
            }
        }

        return null;
    }

    private static NpgsqlCommand Command(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
        => new(sql, connection, transaction)
        {
            CommandTimeout = PostgresControlPlaneConnectionPolicy.MaximumSeconds
        };

    private static string SerializeReceipt(ControlPlaneReceiptV1 receipt)
    {
        var payload = ControlPlaneReceiptV1Codec.Serialize(receipt);
        try
        {
            return Encoding.UTF8.GetString(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static ControlPlaneReceiptV1 DeserializeReceipt(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        try
        {
            return ControlPlaneReceiptV1Codec.DeserializeSemanticJsonb(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void RequireExactReceipt(
        ControlPlaneReceiptV1 receipt,
        ModuleResultEnvelope result)
    {
        if (!string.Equals(receipt.SoulId, result.SoulId, StringComparison.Ordinal)
            || !string.Equals(receipt.DeviceBindingId, result.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(receipt.PlatformAccountId, result.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(receipt.TraceId, result.TraceId, StringComparison.Ordinal)
            || !string.Equals(receipt.IdempotencyKey, result.IdempotencyKey, StringComparison.Ordinal)
            || receipt.OccurredAt != result.OccurredAt
            || !string.Equals(receipt.SourceContractId, result.SourceContractId, StringComparison.Ordinal)
            || !string.Equals(receipt.SourceProducerModule, result.SourceProducerModule, StringComparison.Ordinal)
            || !string.Equals(receipt.SourcePayloadSha256, result.SourcePayloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persisted receipt is not the exact committed source result.");
        }
    }

    private static bool FixedTimeEquals(string leftHex, string rightHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(leftHex),
                Convert.FromHexString(rightHex));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Persisted digest is not lowercase SHA-256 hex.", exception);
        }
    }

    private static bool MatchesExactRedelivery(
        ExistingReceipt existing,
        SignedProviderResultV1 incoming,
        ParsedProviderResult parsed,
        string providerAuthorizationSha256)
        => FixedTimeEquals(existing.SourcePayloadSha256, parsed.PayloadSha256)
           && existing.SourcePayloadBytes.AsSpan().SequenceEqual(incoming.PayloadUtf8.Span)
           && string.Equals(
               existing.ActiveReleaseBomSha256,
               incoming.ActiveReleaseBomSha256,
               StringComparison.Ordinal)
           && string.Equals(existing.ProviderKeyId, incoming.ProviderKeyId, StringComparison.Ordinal)
           && string.Equals(
               existing.ProviderSignatureBase64,
               incoming.SignatureBase64,
               StringComparison.Ordinal)
           && FixedTimeEquals(
               existing.ProviderAuthorizationSha256,
               providerAuthorizationSha256);

    private static string Sha256Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Guid DeterministicGuid(string domain, string digestMaterial)
    {
        var digest = ControlPlaneCanonicalEncoding.ComputeDomainSha256(
            "dps.control-plane-host.postgres-id/v1",
            domain,
            digestMaterial);
        Span<byte> value = stackalloc byte[16];
        Convert.FromHexString(digest.AsSpan(0, 32), value, out _, out _);
        return new Guid(value, bigEndian: true);
    }

    private static HashSet<string> BuildRequiredTriggers()
    {
        var triggers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in TableNames)
        {
            triggers.Add(TriggerKey(
                table,
                table + "_append_only_rows",
                27,
                "reject_control_plane_row_mutation"));
            triggers.Add(TriggerKey(
                table,
                table + "_no_truncate",
                34,
                "reject_control_plane_truncate"));
        }

        triggers.Add(TriggerKey(
            "runtime_truth",
            "runtime_truth_provider_trust",
            7,
            "enforce_runtime_truth_provider_trust"));
        triggers.Add(TriggerKey(
            "idempotency_receipts",
            "idempotency_receipts_truth_link",
            7,
            "enforce_receipt_truth_link"));
        triggers.Add(TriggerKey(
            "outbox",
            "outbox_receipt_link",
            7,
            "enforce_outbox_receipt_link"));
        return triggers;
    }

    private static HashSet<string> BuildRequiredColumns()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        AddSchemaValues(values, "provider_trust_states",
        [
            "source_contract_id", "revision", "source_producer_module",
            "active_release_bom_sha256", "provider_key_id",
            "provider_public_key_spki_base64", "provider_public_key_sha256",
            "status", "valid_from", "valid_until", "created_at"
        ]);
        AddSchemaValues(values, "runtime_truth",
        [
            "truth_id", "business_key_sha256", "scope_sha256",
            "idempotency_key_sha256", "record_sha256", "schema_version",
            "source_contract_id", "source_producer_module", "soul_id",
            "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "source_payload_sha256",
            "source_payload_bytes", "result_status", "active_release_bom_sha256",
            "provider_key_id", "provider_trust_revision",
            "provider_public_key_sha256", "provider_signature_base64",
            "provider_authorization_sha256", "created_at"
        ]);
        AddSchemaValues(values, "idempotency_receipts",
        [
            "receipt_id", "truth_id", "business_key_sha256", "scope_sha256",
            "idempotency_key_sha256", "record_sha256", "soul_id",
            "device_binding_id", "platform_account_id", "source_contract_id",
            "source_producer_module", "trace_id", "idempotency_key", "occurred_at",
            "source_payload_sha256", "receipt_json", "created_at"
        ]);
        AddSchemaValues(values, "idempotency_quarantine",
        [
            "quarantine_id", "business_key_sha256", "soul_id", "device_binding_id",
            "platform_account_id", "source_contract_id", "scope_sha256",
            "idempotency_key_sha256", "existing_record_sha256",
            "incoming_record_sha256", "reason", "created_at"
        ]);
        AddSchemaValues(values, "outbox",
        [
            "outbox_id", "receipt_id", "business_key_sha256", "soul_id",
            "device_binding_id", "platform_account_id", "scope_sha256",
            "idempotency_key_sha256", "record_sha256", "source_contract_id",
            "source_producer_module", "trace_id", "idempotency_key",
            "source_payload_sha256", "topic", "payload_sha256", "payload_json",
            "occurred_at", "created_at"
        ]);
        return values;
    }

    private static HashSet<string> BuildRequiredConstraints()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        AddConstraints(values, "provider_trust_states", 'p', ["provider_trust_states_pkey"]);
        AddConstraints(values, "provider_trust_states", 'c',
        [
            "provider_trust_revision_positive", "provider_trust_bom_hash",
            "provider_trust_key_id", "provider_trust_public_key_base64",
            "provider_trust_public_key_hash", "provider_trust_status_exact",
            "provider_trust_window", "provider_trust_owner_pair"
        ]);
        AddConstraints(values, "runtime_truth", 'p', ["runtime_truth_pkey"]);
        AddConstraints(values, "runtime_truth", 'u', ["runtime_truth_business_key_unique"]);
        AddConstraints(values, "runtime_truth", 'c',
        [
            "runtime_truth_business_hash", "runtime_truth_scope_hash",
            "runtime_truth_idempotency_hash", "runtime_truth_record_hash",
            "runtime_truth_schema_major", "runtime_truth_soul_format",
            "runtime_truth_binding_format", "runtime_truth_account_format",
            "runtime_truth_trace_length", "runtime_truth_idempotency_length",
            "runtime_truth_payload_hash", "runtime_truth_payload_bytes",
            "runtime_truth_bom_hash", "runtime_truth_provider_key",
            "runtime_truth_provider_trust_revision",
            "runtime_truth_provider_public_key_hash", "runtime_truth_provider_signature",
            "runtime_truth_authorization_hash", "runtime_truth_allowlisted_result"
        ]);
        AddConstraints(values, "idempotency_receipts", 'p', ["idempotency_receipts_pkey"]);
        AddConstraints(values, "idempotency_receipts", 'u',
        [
            "idempotency_receipts_truth_unique",
            "idempotency_receipts_business_key_unique"
        ]);
        AddConstraints(values, "idempotency_receipts", 'f', ["idempotency_receipts_truth_fk"]);
        AddConstraints(values, "idempotency_receipts", 'c',
        [
            "idempotency_receipts_id_format", "idempotency_receipts_business_hash",
            "idempotency_receipts_scope_hash", "idempotency_receipts_key_hash",
            "idempotency_receipts_record_hash", "idempotency_receipts_soul_format",
            "idempotency_receipts_binding_format", "idempotency_receipts_account_format",
            "idempotency_receipts_trace_length", "idempotency_receipts_idempotency_length",
            "idempotency_receipts_payload_hash", "idempotency_receipts_json_keys",
            "idempotency_receipts_json_contract", "idempotency_receipts_json_producer",
            "idempotency_receipts_json_scope", "idempotency_receipts_json_source",
            "idempotency_receipts_json_request", "idempotency_receipts_json_id",
            "idempotency_receipts_json_decision"
        ]);
        AddConstraints(values, "idempotency_quarantine", 'p', ["idempotency_quarantine_pkey"]);
        AddConstraints(values, "idempotency_quarantine", 'u', ["idempotency_quarantine_conflict_unique"]);
        AddConstraints(values, "idempotency_quarantine", 'c',
        [
            "idempotency_quarantine_business_hash", "idempotency_quarantine_soul_format",
            "idempotency_quarantine_binding_format", "idempotency_quarantine_account_format",
            "idempotency_quarantine_contract_known", "idempotency_quarantine_scope_hash",
            "idempotency_quarantine_key_hash", "idempotency_quarantine_existing_hash",
            "idempotency_quarantine_incoming_hash", "idempotency_quarantine_reason_exact"
        ]);
        AddConstraints(values, "outbox", 'p', ["outbox_pkey"]);
        AddConstraints(values, "outbox", 'u', ["outbox_receipt_unique", "outbox_business_key_unique"]);
        AddConstraints(values, "outbox", 'f', ["outbox_receipt_fk"]);
        AddConstraints(values, "outbox", 'c',
        [
            "outbox_business_hash", "outbox_soul_format", "outbox_binding_format",
            "outbox_account_format", "outbox_scope_hash", "outbox_idempotency_hash",
            "outbox_record_hash", "outbox_trace_length", "outbox_idempotency_length",
            "outbox_source_payload_hash", "outbox_topic_exact", "outbox_payload_hash",
            "outbox_payload_keys", "outbox_payload_id", "outbox_payload_scope",
            "outbox_payload_source", "outbox_payload_request", "outbox_payload_contract",
            "outbox_payload_producer", "outbox_payload_decision"
        ]);
        return values;
    }

    private static void AddSchemaValues(
        ISet<string> target,
        string table,
        IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var type = name switch
            {
                "revision" or "provider_trust_revision" => "bigint",
                "truth_id" or "quarantine_id" or "outbox_id" => "uuid",
                "valid_from" or "valid_until" or "occurred_at" or "created_at" =>
                    "timestamp with time zone",
                "receipt_json" or "payload_json" => "jsonb",
                "source_payload_bytes" => "bytea",
                _ => "text"
            };
            var defaultExpression = string.Equals(name, "created_at", StringComparison.Ordinal)
                ? "clock_timestamp()"
                : string.Empty;
            var collation = string.Equals(type, "text", StringComparison.Ordinal)
                ? "C"
                : string.Empty;
            target.Add(ColumnKey(table, name, type, defaultExpression, collation));
        }
    }

    private static void AddConstraints(
        ISet<string> target,
        string table,
        char type,
        IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            target.Add(ConstraintKey(table, name, type));
        }
    }

    private static string ColumnKey(
        string table,
        string column,
        string type,
        string defaultExpression,
        string collation)
        => string.Join('\u001f', table, column, type, defaultExpression, collation);

    private static string ConstraintKey(string table, string constraint, char type)
        => string.Concat(table, "\u001f", constraint, "\u001f", type);

    private static string IndexKey(string table, string index, params string[] columns)
        => string.Concat(table, "\u001f", index, "\u001f", string.Join('\u001e', columns));

    private static string FunctionKey(
        string name,
        string arguments,
        string result,
        string language,
        string volatility,
        string parallel,
        bool securityDefiner,
        bool runtimeExecute,
        string bodySha256)
        => string.Join(
            '\u001f',
            name,
            arguments,
            result,
            language,
            volatility,
            parallel,
            securityDefiner ? "definer" : "invoker",
            runtimeExecute ? "execute" : "deny",
            bodySha256);

    private static string AclKey(
        string kind,
        string objectName,
        string grantee,
        string privilege)
        => string.Join('\u001f', kind, objectName, grantee, privilege);

    private static IReadOnlyDictionary<string, string> LoadExpectedFunctionBodyHashes(
        string schemaName,
        string runtimeRoleName)
    {
        var assembly = typeof(PostgresControlPlaneTruthStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(static name =>
            name.EndsWith("001_create_control_plane_truth.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded PostgreSQL migration is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var migration = reader.ReadToEnd();
        var matches = Regex.Matches(
            migration,
            @"CREATE OR REPLACE FUNCTION __SCHEMA__\.(?<name>[a-z0-9_]+)\([^)]*\).*?AS \$function\$(?<body>.*?)\$function\$;",
            RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in matches)
        {
            var name = match.Groups["name"].Value;
            var body = match.Groups["body"].Value.Replace(
                "__SCHEMA__",
                schemaName,
                StringComparison.Ordinal).Replace(
                    "__RUNTIME_ROLE__",
                    runtimeRoleName,
                    StringComparison.Ordinal);
            if (!values.TryAdd(name, Sha256Utf8(body)))
            {
                throw new InvalidOperationException("Embedded PostgreSQL function is ambiguous.");
            }
        }

        return values;
    }

    private static string TriggerKey(
        string table,
        string trigger,
        int triggerType,
        string function)
        => string.Concat(
            table,
            "\u001f",
            trigger,
            "\u001f",
            triggerType.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "\u001f",
            function);

    private sealed record ExistingReceipt(
        string RecordSha256,
        string ReceiptJson,
        string SourcePayloadSha256,
        byte[] SourcePayloadBytes,
        string ActiveReleaseBomSha256,
        string ProviderKeyId,
        string ProviderSignatureBase64,
        string ProviderAuthorizationSha256);
    private sealed record InjectedFailure(
        ControlPlaneMutationStage Stage,
        Exception Exception);
}
