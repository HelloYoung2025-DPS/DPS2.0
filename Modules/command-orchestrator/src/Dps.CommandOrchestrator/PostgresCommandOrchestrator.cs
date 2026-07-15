using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dps.CommandOrchestrator.Contracts;
using Dps.OperationCompiler.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.CommandOrchestrator;

public sealed record PostgresCommandOrchestratorOptions(
    string MigratorConnectionString,
    string RuntimeConnectionString,
    string Schema,
    string MigratorRole,
    string RuntimeRole,
    TimeSpan OperationTimeout)
{
    public const int RequiredPostgresVersionNumber = 180004;

    public PostgresCommandOrchestratorOptions(
        string migratorConnectionString,
        string runtimeConnectionString,
        string schema,
        string migratorRole,
        string runtimeRole)
        : this(
            migratorConnectionString,
            runtimeConnectionString,
            schema,
            migratorRole,
            runtimeRole,
            TimeSpan.FromSeconds(10))
    {
    }

    internal void Validate()
    {
        PostgresCommandStoreValidation.RequireIdentifier(Schema, nameof(Schema));
        PostgresCommandStoreValidation.RequireIdentifier(MigratorRole, nameof(MigratorRole));
        PostgresCommandStoreValidation.RequireIdentifier(RuntimeRole, nameof(RuntimeRole));
        if (string.Equals(MigratorRole, RuntimeRole, StringComparison.Ordinal))
            throw new ArgumentException("Migrator and runtime PostgreSQL roles must be distinct.");
        if (OperationTimeout < TimeSpan.FromSeconds(1) || OperationTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));

        var migrator = PostgresCommandStoreValidation.ParseConnectionString(
            MigratorConnectionString,
            nameof(MigratorConnectionString));
        var runtime = PostgresCommandStoreValidation.ParseConnectionString(
            RuntimeConnectionString,
            nameof(RuntimeConnectionString));
        if (!string.Equals(migrator.Username, MigratorRole, StringComparison.Ordinal)
            || !string.Equals(runtime.Username, RuntimeRole, StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL connection identities must exactly match the declared roles.");
        }
        if (!string.Equals(migrator.Host, runtime.Host, StringComparison.OrdinalIgnoreCase)
            || migrator.Port != runtime.Port
            || !string.Equals(migrator.Database, runtime.Database, StringComparison.Ordinal))
        {
            throw new ArgumentException("Migrator and runtime connections must target the same PostgreSQL database.");
        }
    }
}

public enum PostgresCommandMutationStage
{
    EnqueueCommitted,
    LeaseReservationCommitted,
    LeaseBoundCommitted,
    DispatchCommitted,
    ReceiptCommitted
}

public delegate ValueTask PostgresCommandFaultInjector(
    PostgresCommandMutationStage stage,
    CancellationToken cancellationToken);

public sealed record CommandOutboxItem(
    long Sequence,
    Guid ReceiptId,
    Guid CommandId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string Topic,
    string PayloadSha256,
    CommandReceiptV1 Payload,
    DateTimeOffset OccurredAt);

public sealed class PostgresCommandOrchestrator : IDisposable, IAsyncDisposable
{
    private const string MigrationResource =
        "Dps.CommandOrchestrator.Migrations.001_create_command_orchestrator.sql";
    private const string SchemaVersion = "1";
    private const int MaximumAttempts = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions DatabaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    private static readonly HashSet<string> ExpectedTables = new(StringComparer.Ordinal)
    {
        "migration_ledger", "catalog_attestations", "commands", "leases",
        "attempt_events", "signed_receipts", "outbox", "quarantine"
    };

    private static readonly HashSet<string> ExpectedSequences = new(StringComparer.Ordinal)
    {
        "attempt_events_event_seq_seq", "outbox_outbox_seq_seq",
        "quarantine_quarantine_seq_seq"
    };

    private static readonly HashSet<string> ExpectedIndexes = new(StringComparer.Ordinal)
    {
        "migration_ledger_pkey", "catalog_attestations_pkey", "commands_pkey",
        "commands_idempotency_scope_sha256_key", "leases_pkey",
        "leases_command_attempt_unique", "attempt_events_pkey", "attempt_events_once",
        "attempt_events_latest_idx", "signed_receipts_pkey", "outbox_pkey",
        "outbox_receipt_id_key", "quarantine_pkey", "quarantine_conflict_unique"
    };

    private static readonly HashSet<string> ExpectedTriggers = new(StringComparer.Ordinal)
    {
        "migration_ledger_no_row_mutation", "migration_ledger_no_truncate",
        "catalog_attestations_no_row_mutation", "catalog_attestations_no_truncate",
        "commands_no_row_mutation", "commands_no_truncate",
        "leases_no_row_mutation", "leases_no_truncate",
        "attempt_events_no_row_mutation", "attempt_events_no_truncate",
        "signed_receipts_no_row_mutation", "signed_receipts_no_truncate",
        "outbox_no_row_mutation", "outbox_no_truncate",
        "quarantine_no_row_mutation", "quarantine_no_truncate"
    };

    private static readonly IReadOnlyDictionary<string, (string Table, string Column)> ExpectedSequenceOwners =
        new Dictionary<string, (string Table, string Column)>(StringComparer.Ordinal)
        {
            ["attempt_events_event_seq_seq"] = ("attempt_events", "event_seq"),
            ["outbox_outbox_seq_seq"] = ("outbox", "outbox_seq"),
            ["quarantine_quarantine_seq_seq"] = ("quarantine", "quarantine_seq")
        };

    private static readonly HashSet<string> ExpectedFunctions = new(StringComparer.Ordinal)
    {
        "reject_append_only_mutation", "project_command_state", "assert_runtime_capability",
        "api_enqueue_command",
        "api_reserve_lease", "api_bind_lease", "api_get_lease_context",
        "api_mark_dispatched", "api_record_receipt", "api_recover_expired_leases",
        "api_get_snapshot", "api_quarantine_count", "api_read_outbox",
        "api_runtime_attestation"
    };

    private static readonly HashSet<string> RuntimeFunctions = new(StringComparer.Ordinal)
    {
        "api_enqueue_command", "api_reserve_lease", "api_bind_lease",
        "api_get_lease_context", "api_mark_dispatched", "api_record_receipt",
        "api_recover_expired_leases", "api_get_snapshot", "api_quarantine_count",
        "api_read_outbox", "api_runtime_attestation"
    };

    private readonly PostgresCommandOrchestratorOptions _options;
    private readonly IPolicyExecutionAuthorizationSignerV1 _policyAuthorizationSigner;
    private readonly AuthoritativeExecutionAuthorizationVerifier _authorizationVerifier;
    private readonly AuthoritativeCommandReceiptVerifier _receiptVerifier;
    private readonly byte[] _runtimeCapability;
    private readonly string _runtimeCapabilitySha256;
    private readonly PostgresCommandFaultInjector _faultInjector;
    private int _initialized;
    private int _disposed;
    private string? _migrationSha256;
    private string? _catalogSha256;

    public PostgresCommandOrchestrator(
        PostgresCommandOrchestratorOptions options,
        IPolicyExecutionAuthorizationSignerV1 policyAuthorizationSigner,
        ReadOnlySpan<byte> trustedPolicyApprovalAuthorizationPublicKeySpki,
        ReadOnlySpan<byte> trustedExecutorGatewayReceiptPublicKeySpki,
        ReadOnlySpan<byte> runtimeDatabaseCapability)
        : this(
            options,
            policyAuthorizationSigner,
            trustedPolicyApprovalAuthorizationPublicKeySpki,
            trustedExecutorGatewayReceiptPublicKeySpki,
            runtimeDatabaseCapability,
            null)
    {
    }

    internal PostgresCommandOrchestrator(
        PostgresCommandOrchestratorOptions options,
        IPolicyExecutionAuthorizationSignerV1 policyAuthorizationSigner,
        ReadOnlySpan<byte> trustedPolicyApprovalAuthorizationPublicKeySpki,
        ReadOnlySpan<byte> trustedExecutorGatewayReceiptPublicKeySpki,
        ReadOnlySpan<byte> runtimeDatabaseCapability,
        PostgresCommandFaultInjector? faultInjector)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyAuthorizationSigner = policyAuthorizationSigner
            ?? throw new ArgumentNullException(nameof(policyAuthorizationSigner));
        _options.Validate();
        if (runtimeDatabaseCapability.Length != 32)
            throw new ArgumentException(
                "Runtime database capability must be exactly 256 bits.",
                nameof(runtimeDatabaseCapability));
        _authorizationVerifier = new AuthoritativeExecutionAuthorizationVerifier(
            trustedPolicyApprovalAuthorizationPublicKeySpki);
        try
        {
            _receiptVerifier = new AuthoritativeCommandReceiptVerifier(
                trustedExecutorGatewayReceiptPublicKeySpki);
            try
            {
                if (string.Equals(
                        _authorizationVerifier.TrustAnchorSha256,
                        _receiptVerifier.TrustAnchorSha256,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Policy Approval authorization and Executor Gateway receipt trust anchors must be cryptographically distinct.");
                }
                var runtimeCapability = runtimeDatabaseCapability.ToArray();
                try
                {
                    _runtimeCapabilitySha256 = Convert.ToHexStringLower(
                        SHA256.HashData(runtimeCapability));
                    _runtimeCapability = runtimeCapability;
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(runtimeCapability);
                    throw;
                }
            }
            catch
            {
                _receiptVerifier.Dispose();
                throw;
            }
        }
        catch
        {
            _authorizationVerifier.Dispose();
            throw;
        }
        if (!string.Equals(
                _policyAuthorizationSigner.ProtocolId,
                IPolicyExecutionAuthorizationSignerV1.CurrentProtocolId,
                StringComparison.Ordinal)
            || !string.Equals(
                _policyAuthorizationSigner.SignerModule,
                IPolicyExecutionAuthorizationSignerV1.CurrentSignerModule,
                StringComparison.Ordinal)
            || !string.Equals(
                _policyAuthorizationSigner.KeyId,
                "sha256:" + _authorizationVerifier.TrustAnchorSha256,
                StringComparison.Ordinal))
        {
            _receiptVerifier.Dispose();
            _authorizationVerifier.Dispose();
            CryptographicOperations.ZeroMemory(_runtimeCapability);
            throw new ArgumentException(
                "Policy Approval signer port protocol, owner, or trust-anchor key id is not exact.",
                nameof(policyAuthorizationSigner));
        }
        _faultInjector = faultInjector ?? (static (_, _) => ValueTask.CompletedTask);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var timeout = CreateTimeout(cancellationToken);
        var (migration, migrationSha256) = ReadMigration();
        await using var connection = new NpgsqlConnection(_options.MigratorConnectionString);
        await connection.OpenAsync(timeout.Token);
        await ConfigureSessionAsync(connection, timeout.Token);
        await AssertConnectionIdentityAndVersionAsync(
            connection,
            _options.MigratorRole,
            timeout.Token);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            timeout.Token);
        try
        {
            await AcquireMigrationLockAsync(connection, transaction, timeout.Token);
            var schemaStatus = await ReadSchemaStatusAsync(connection, transaction, timeout.Token);
            if (schemaStatus.Exists
                && !string.Equals(schemaStatus.Owner, _options.MigratorRole, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "A pre-existing command-orchestrator schema is not owned by the declared migrator.");
            }
            if (!schemaStatus.Exists)
            {
                await using var createSchema = Command(
                    $"CREATE SCHEMA {QuotedSchema} AUTHORIZATION {QuoteIdentifier(_options.MigratorRole)}",
                    connection,
                    transaction);
                await createSchema.ExecuteNonQueryAsync(timeout.Token);
                schemaStatus = new SchemaStatus(true, _options.MigratorRole, 0);
            }

            var hasLedger = await TableExistsAsync(
                connection,
                transaction,
                "migration_ledger",
                timeout.Token);
            if (!hasLedger)
            {
                if (schemaStatus.ObjectCount != 0)
                    throw new InvalidOperationException(
                        "A pre-existing command-orchestrator schema without a trusted migration ledger is not empty.");
                var rendered = migration
                    .Replace("__SCHEMA__", QuotedSchema, StringComparison.Ordinal)
                    .Replace("__RUNTIME_ROLE__", QuoteIdentifier(_options.RuntimeRole), StringComparison.Ordinal)
                    .Replace("__RUNTIME_CAPABILITY_SHA256__", _runtimeCapabilitySha256, StringComparison.Ordinal)
                    .Replace("__MIGRATION_SHA256__", migrationSha256, StringComparison.Ordinal);
                await using var migrate = Command(rendered, connection, transaction);
                await migrate.ExecuteNonQueryAsync(timeout.Token);
                await AssertMigrationLedgerAsync(
                    connection,
                    transaction,
                    migrationSha256,
                    timeout.Token);
                await AssertCatalogShapeAndPrivilegesAsync(
                    connection,
                    transaction,
                    timeout.Token);
                var catalogSha256 = await ComputeCatalogSha256Async(
                    connection,
                    transaction,
                    timeout.Token);
                await InsertCatalogAttestationAsync(
                    connection,
                    transaction,
                    migrationSha256,
                    catalogSha256,
                    timeout.Token);
                _catalogSha256 = catalogSha256;
            }
            else
            {
                await AssertMigrationLedgerAsync(
                    connection,
                    transaction,
                    migrationSha256,
                    timeout.Token);
                await AssertCatalogShapeAndPrivilegesAsync(
                    connection,
                    transaction,
                    timeout.Token);
                var catalogSha256 = await ComputeCatalogSha256Async(
                    connection,
                    transaction,
                    timeout.Token);
                await AssertCatalogAttestationAsync(
                    connection,
                    transaction,
                    migrationSha256,
                    catalogSha256,
                    timeout.Token);
                _catalogSha256 = catalogSha256;
            }

            var finalCatalogSha256 = await ComputeCatalogSha256Async(
                connection,
                transaction,
                timeout.Token);
            if (!FixedDigestEquals(_catalogSha256!, finalCatalogSha256))
                throw new InvalidDataException("Command-orchestrator catalog changed during initialization.");
            await transaction.CommitAsync(timeout.Token);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await transaction.RollbackAsync(rollbackTimeout.Token); }
                catch { }
            }
            throw;
        }

        _migrationSha256 = migrationSha256;
        await VerifyRuntimeConnectionAsync(timeout.Token);
        Volatile.Write(ref _initialized, 1);
    }

    public async ValueTask<EnqueueResult> EnqueueAsync(
        CompiledOperationV1 operation,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = operation.ValidateAndSnapshot();
        var operationSha256 = CommandCanonicalEncoding.OperationDigest(snapshot);
        var idempotencyScopeSha256 = CommandCanonicalEncoding.IdempotencyScopeKey(
            snapshot.SoulId,
            snapshot.DeviceBindingId,
            snapshot.PlatformAccountId,
            snapshot.IdempotencyKey);
        var commandId = CommandCanonicalEncoding.CommandId(
            idempotencyScopeSha256,
            snapshot.OperationId);
        var operationJson = Serialize(snapshot);
        var retrySafe = snapshot.Steps.All(static step => step.RetrySafe);
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = Command(
            $"SELECT disposition, result_command_id, payload_sha256, state FROM {Qualified("api_enqueue_command")}(@command_id, @operation_id, @scope_sha256, @operation_sha256, @soul_id, @device_binding_id, @platform_account_id, @trace_id, @idempotency_key, @operation_json, @retry_safe, @occurred_at, @runtime_capability)",
            connection);
        command.Parameters.AddWithValue("command_id", commandId);
        command.Parameters.AddWithValue("operation_id", snapshot.OperationId);
        command.Parameters.AddWithValue("scope_sha256", idempotencyScopeSha256);
        command.Parameters.AddWithValue("operation_sha256", operationSha256);
        AddScope(command, snapshot.SoulId, snapshot.DeviceBindingId, snapshot.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", snapshot.TraceId);
        command.Parameters.AddWithValue("idempotency_key", snapshot.IdempotencyKey);
        AddJson(command, "operation_json", operationJson);
        command.Parameters.AddWithValue("retry_safe", retrySafe);
        command.Parameters.AddWithValue("occurred_at", snapshot.OccurredAt);
        AddRuntimeCapability(command);
        EnqueueResult result;
        await using (var reader = await ExecuteReaderAsync(command, timeout.Token))
        {
            if (!await reader.ReadAsync(timeout.Token))
                throw new InvalidDataException("PostgreSQL enqueue returned no result.");
            result = new EnqueueResult(
                ParseEnum<EnqueueDisposition>(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2));
            if (await reader.ReadAsync(timeout.Token))
                throw new InvalidDataException("PostgreSQL enqueue returned multiple results.");
        }
        await _faultInjector(PostgresCommandMutationStage.EnqueueCommitted, timeout.Token);
        return result;
    }

    public async ValueTask<CommandDispatchV1> AcquireLeaseAsync(
        Guid commandId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string workerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        CommandContractGuard.RequireGuid(commandId, nameof(commandId));
        CommandContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId);
        CommandContractGuard.RequireText(workerId, 128, nameof(workerId));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)
            || duration.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        using var timeout = CreateTimeout(cancellationToken);
        var leaseId = Guid.NewGuid();
        LeaseReservation reservation;
        try
        {
            await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
            await using var command = Command(
                $"SELECT operation_json::text, operation_sha256, attempt, lease_id, lease_expires_at, acquired_at, disposition FROM {Qualified("api_reserve_lease")}(@command_id, @soul_id, @device_binding_id, @platform_account_id, @lease_id, @worker_id, @duration_seconds, @runtime_capability)",
                connection);
            command.Parameters.AddWithValue("command_id", commandId);
            AddScope(command, soulId, deviceBindingId, platformAccountId);
            command.Parameters.AddWithValue("lease_id", leaseId);
            command.Parameters.AddWithValue("worker_id", workerId);
            command.Parameters.AddWithValue("duration_seconds", checked((int)duration.TotalSeconds));
            AddRuntimeCapability(command);
            await using var reader = await ExecuteReaderAsync(command, timeout.Token);
            if (!await reader.ReadAsync(timeout.Token))
                throw new InvalidDataException("PostgreSQL lease reservation returned no result.");
            reservation = new LeaseReservation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5));
            if (await reader.ReadAsync(timeout.Token))
                throw new InvalidDataException("PostgreSQL lease reservation returned multiple results.");
        }
        catch (InvalidOperationException)
        {
            // api_reserve_lease cannot both append a post-dispatch expiry event and raise in
            // the same transaction. Persist recovery in a separate transaction before the
            // original fail-closed result escapes; the scan is only paid on exceptional paths.
            _ = await RecoverExpiredLeasesAsync(timeout.Token);
            throw;
        }
        await _faultInjector(PostgresCommandMutationStage.LeaseReservationCommitted, timeout.Token);

        var snapshot = Deserialize<CompiledOperationV1>(reservation.OperationJson)
            .ValidateAndSnapshot();
        if (!FixedDigestEquals(
                reservation.OperationSha256,
                CommandCanonicalEncoding.OperationDigest(snapshot)))
        {
            throw new InvalidDataException("Persisted operation snapshot digest does not match its exact payload.");
        }
        if (snapshot.OperationId == Guid.Empty
            || !string.Equals(snapshot.SoulId, soulId, StringComparison.Ordinal)
            || !string.Equals(snapshot.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
            || !string.Equals(snapshot.PlatformAccountId, platformAccountId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Persisted operation snapshot escaped the exact command scope.");
        }

        var dispatch = new CommandDispatchV1(
            CommandDispatchV1.CurrentSchemaVersion,
            CommandDispatchV1.CurrentContractId,
            CommandDispatchV1.CurrentProducerModule,
            commandId,
            snapshot.OperationId,
            snapshot.ApprovalId,
            snapshot.ApprovalSha256,
            snapshot.SoulId,
            snapshot.DeviceBindingId,
            snapshot.PlatformAccountId,
            snapshot.TraceId,
            snapshot.IdempotencyKey,
            reservation.AcquiredAt,
            "internal",
            snapshot.ActionKind,
            snapshot.IsSideEffect,
            snapshot.PlatformAuthorizationId,
            reservation.LeaseId,
            workerId,
            reservation.LeaseExpiresAt,
            reservation.Attempt,
            snapshot.Steps.Select(static step => new CommandStepV1(
                step.StepId,
                step.StepKind,
                new Dictionary<string, string>(step.Arguments, StringComparer.Ordinal),
                step.RetrySafe,
                step.PostconditionKind)).ToArray());
        dispatch.Validate();
        var commandSha256 = ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch);
        var dispatchJson = Serialize(dispatch);
        await using (var connection = await OpenRuntimeConnectionAsync(timeout.Token))
        await using (var command = Command(
            $"SELECT {Qualified("api_bind_lease")}(@command_id, @lease_id, @attempt, @command_sha256, @dispatch_json, @runtime_capability)",
            connection))
        {
            command.Parameters.AddWithValue("command_id", commandId);
            command.Parameters.AddWithValue("lease_id", reservation.LeaseId);
            command.Parameters.AddWithValue("attempt", reservation.Attempt);
            command.Parameters.AddWithValue("command_sha256", commandSha256);
            AddJson(command, "dispatch_json", dispatchJson);
            AddRuntimeCapability(command);
            _ = await ExecuteScalarAsync(command, timeout.Token);
        }
        await _faultInjector(PostgresCommandMutationStage.LeaseBoundCommitted, timeout.Token);
        return dispatch;
    }

    public async ValueTask<ExecutionAuthorizationV1> IssueAndMarkDispatchedAsync(
        Guid commandId,
        Guid leaseId,
        ExecutionAuthorizationActivationV1 activation,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(activation);
        CommandContractGuard.RequireSha256(activation.ReleaseBomSha256, nameof(activation.ReleaseBomSha256));
        if (activation.ActiveReleaseBomGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(activation.ActiveReleaseBomGeneration));
        CommandContractGuard.RequireSha256(
            activation.ActiveReleaseBomTokenSha256,
            nameof(activation.ActiveReleaseBomTokenSha256));
        using var timeout = CreateTimeout(cancellationToken);
        var context = await ReadLeaseContextAsync(commandId, leaseId, timeout.Token);
        var dispatch = Deserialize<CommandDispatchV1>(context.DispatchJson);
        dispatch.Validate();
        if (context.State != CommandState.Leased
            || dispatch.CommandId != commandId
            || dispatch.LeaseId != leaseId
            || dispatch.Attempt != context.Attempt)
        {
            throw new UnauthorizedAccessException(
                "Policy signing was requested outside the exact active command lease.");
        }
        var unsigned = new ExecutionAuthorizationV1(
            ExecutionAuthorizationV1.CurrentSchemaVersion,
            ExecutionAuthorizationV1.CurrentContractId,
            ExecutionAuthorizationV1.CurrentProducerModule,
            ExecutionAuthorizationV1.CurrentSignatureDomain,
            ExecutionAuthorizationV1.CurrentCanonicalEncoding,
            ExecutionAuthorizationV1.CurrentCommandDigestAlgorithm,
            ExecutionAuthorizationV1.CurrentSignatureAlgorithm,
            ExecutionAuthorizationV1.CurrentSignatureFormat,
            ExecutionAuthorizationV1.CurrentSignatureEncoding,
            ExecutionAuthorizationV1.CurrentCallerModule,
            ExecutionAuthorizationV1.CurrentAuthScope,
            dispatch.CommandId,
            dispatch.LeaseId,
            dispatch.Attempt,
            dispatch.SoulId,
            dispatch.DeviceBindingId,
            dispatch.PlatformAccountId,
            dispatch.TraceId,
            dispatch.IdempotencyKey,
            dispatch.OccurredAt,
            dispatch.PrivacyClass,
            context.CommandSha256,
            activation.ReleaseBomSha256,
            activation.ActiveReleaseBomGeneration,
            activation.ActiveReleaseBomTokenSha256,
            dispatch.LeaseExpiresAt,
            activation.ShadowMode,
            Convert.ToBase64String(new byte[ExecutionAuthorizationProtocolV1.P1363SignatureSizeBytes]));
        unsigned.ValidatePayload();
        var issued = await _policyAuthorizationSigner.SignAsync(unsigned, timeout.Token);
        var expectedBytes = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(unsigned);
        var issuedBytes = ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes(issued);
        try
        {
            if (expectedBytes.Length != issuedBytes.Length
                || !CryptographicOperations.FixedTimeEquals(expectedBytes, issuedBytes))
            {
                throw new UnauthorizedAccessException(
                    "Policy Approval signer changed the command-owned execution-authorization envelope.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(issuedBytes);
        }
        await MarkDispatchedAsync(commandId, leaseId, issued, timeout.Token);
        return issued;
    }

    internal async ValueTask MarkDispatchedAsync(
        Guid commandId,
        Guid leaseId,
        ExecutionAuthorizationV1 issuedAuthorization,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var verifiedAuthorization = _authorizationVerifier.Verify(issuedAuthorization);
        using var timeout = CreateTimeout(cancellationToken);
        var context = await ReadLeaseContextAsync(commandId, leaseId, timeout.Token);
        var dispatch = Deserialize<CommandDispatchV1>(context.DispatchJson);
        dispatch.Validate();
        var recomputedCommandSha256 = ExecutionAuthorizationProtocolV1.ComputeCommandSha256(dispatch);
        if (!FixedDigestEquals(context.CommandSha256, recomputedCommandSha256)
            || context.State != CommandState.Leased
            || dispatch.CommandId != commandId
            || dispatch.LeaseId != leaseId
            || dispatch.Attempt != context.Attempt
            || verifiedAuthorization.CommandId != commandId
            || verifiedAuthorization.LeaseId != leaseId
            || verifiedAuthorization.Attempt != dispatch.Attempt
            || !string.Equals(verifiedAuthorization.SoulId, dispatch.SoulId, StringComparison.Ordinal)
            || !string.Equals(verifiedAuthorization.DeviceBindingId, dispatch.DeviceBindingId, StringComparison.Ordinal)
            || !string.Equals(verifiedAuthorization.PlatformAccountId, dispatch.PlatformAccountId, StringComparison.Ordinal)
            || !string.Equals(verifiedAuthorization.TraceId, dispatch.TraceId, StringComparison.Ordinal)
            || !string.Equals(verifiedAuthorization.IdempotencyKey, dispatch.IdempotencyKey, StringComparison.Ordinal)
            || !FixedDigestEquals(verifiedAuthorization.CommandSha256, context.CommandSha256)
            || verifiedAuthorization.ValidUntil > context.LeaseExpiresAt)
        {
            throw new UnauthorizedAccessException(
                "Issued execution authorization is outside the exact command, lease, scope, digest, or validity window.");
        }

        var authorizationSha256 =
            ExecutionAuthorizationProtocolV1.ComputeAuthorizationSha256(verifiedAuthorization);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = Command(
            $"SELECT {Qualified("api_mark_dispatched")}(@command_id, @lease_id, @attempt, @command_sha256, @authorization_sha256, @release_bom_sha256, @generation, @token_sha256, @authorization_json, @authorization_occurred_at, @authorization_valid_until, @runtime_capability)",
            connection);
        command.Parameters.AddWithValue("command_id", commandId);
        command.Parameters.AddWithValue("lease_id", leaseId);
        command.Parameters.AddWithValue("attempt", context.Attempt);
        command.Parameters.AddWithValue("command_sha256", context.CommandSha256);
        command.Parameters.AddWithValue("authorization_sha256", authorizationSha256);
        command.Parameters.AddWithValue("release_bom_sha256", verifiedAuthorization.ReleaseBomSha256);
        command.Parameters.AddWithValue("generation", verifiedAuthorization.ActiveReleaseBomGeneration);
        command.Parameters.AddWithValue("token_sha256", verifiedAuthorization.ActiveReleaseBomTokenSha256);
        AddJson(command, "authorization_json", Serialize(verifiedAuthorization));
        command.Parameters.AddWithValue("authorization_occurred_at", verifiedAuthorization.OccurredAt);
        command.Parameters.AddWithValue("authorization_valid_until", verifiedAuthorization.ValidUntil);
        AddRuntimeCapability(command);
        _ = await ExecuteScalarAsync(command, timeout.Token);
        await _faultInjector(PostgresCommandMutationStage.DispatchCommitted, timeout.Token);
    }

    public async ValueTask<ReceiptResult> RecordReceiptAsync(
        SignedCommandReceiptV1 signedReceipt,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var verified = _receiptVerifier.Verify(signedReceipt);
        var receipt = verified.Receipt;
        var signedReceiptSha256 =
            CommandCanonicalEncoding.SignedReceiptDigest(verified.SignedReceipt);
        var signedJson = Serialize(verified.SignedReceipt);
        var receiptJson = Serialize(receipt);
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = Command(
            $"SELECT disposition, state FROM {Qualified("api_record_receipt")}(@receipt_id, @command_id, @lease_id, @attempt, @soul_id, @device_binding_id, @platform_account_id, @trace_id, @idempotency_key, @signed_sha256, @receipt_sha256, @command_sha256, @authorization_sha256, @release_bom_sha256, @generation, @token_sha256, @outcome, @retry_allowed, @signed_json, @receipt_json, @occurred_at, @runtime_capability)",
            connection);
        command.Parameters.AddWithValue("receipt_id", receipt.ReceiptId);
        command.Parameters.AddWithValue("command_id", receipt.CommandId);
        command.Parameters.AddWithValue("lease_id", receipt.LeaseId);
        command.Parameters.AddWithValue("attempt", receipt.Attempt);
        AddScope(command, receipt.SoulId, receipt.DeviceBindingId, receipt.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", receipt.TraceId);
        command.Parameters.AddWithValue("idempotency_key", receipt.IdempotencyKey);
        command.Parameters.AddWithValue("signed_sha256", signedReceiptSha256);
        command.Parameters.AddWithValue("receipt_sha256", signedReceipt.ReceiptSha256);
        command.Parameters.AddWithValue("command_sha256", signedReceipt.CommandSha256);
        command.Parameters.AddWithValue("authorization_sha256", signedReceipt.AuthorizationSha256);
        command.Parameters.AddWithValue("release_bom_sha256", signedReceipt.ReleaseBomSha256);
        command.Parameters.AddWithValue("generation", signedReceipt.ActiveReleaseBomGeneration);
        command.Parameters.AddWithValue("token_sha256", signedReceipt.ActiveReleaseBomTokenSha256);
        command.Parameters.AddWithValue("outcome", receipt.Outcome);
        command.Parameters.AddWithValue("retry_allowed", receipt.RetryAllowed);
        AddJson(command, "signed_json", signedJson);
        AddJson(command, "receipt_json", receiptJson);
        command.Parameters.AddWithValue("occurred_at", receipt.OccurredAt);
        AddRuntimeCapability(command);
        ReceiptResult result;
        await using (var reader = await ExecuteReaderAsync(command, timeout.Token))
        {
            if (!await reader.ReadAsync(timeout.Token))
                throw new InvalidDataException("PostgreSQL receipt recording returned no result.");
            result = new ReceiptResult(
                ParseEnum<ReceiptDisposition>(reader.GetString(0)),
                ParseEnum<CommandState>(reader.GetString(1)));
            if (await reader.ReadAsync(timeout.Token))
                throw new InvalidDataException("PostgreSQL receipt recording returned multiple results.");
        }
        await _faultInjector(PostgresCommandMutationStage.ReceiptCommitted, timeout.Token);
        return result;
    }

    public async ValueTask<int> RecoverExpiredLeasesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = Command(
            $"SELECT {Qualified("api_recover_expired_leases")}(@runtime_capability)",
            connection);
        AddRuntimeCapability(command);
        return Convert.ToInt32(await ExecuteScalarAsync(command, timeout.Token));
    }

    public async ValueTask<CommandSnapshot> GetSnapshotAsync(
        Guid commandId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        CommandContractGuard.RequireGuid(commandId, nameof(commandId));
        CommandContractGuard.RequireScope(soulId, deviceBindingId, platformAccountId);
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = Command(
            $"SELECT command_id, soul_id, device_binding_id, platform_account_id, state, attempt, lease_id, lease_expires_at FROM {Qualified("api_get_snapshot")}(@command_id, @soul_id, @device_binding_id, @platform_account_id, @runtime_capability)",
            connection);
        command.Parameters.AddWithValue("command_id", commandId);
        AddScope(command, soulId, deviceBindingId, platformAccountId);
        AddRuntimeCapability(command);
        await using var reader = await ExecuteReaderAsync(command, timeout.Token);
        if (!await reader.ReadAsync(timeout.Token))
            throw new KeyNotFoundException("Unknown command.");
        var result = new CommandSnapshot(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            ParseEnum<CommandState>(reader.GetString(4)), reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
        if (await reader.ReadAsync(timeout.Token))
            throw new InvalidDataException("PostgreSQL command snapshot returned multiple results.");
        return result;
    }

    public async ValueTask<long> GetQuarantineCountAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = Command(
            $"SELECT {Qualified("api_quarantine_count")}(@runtime_capability)",
            connection);
        AddRuntimeCapability(command);
        return Convert.ToInt64(await ExecuteScalarAsync(command, timeout.Token));
    }

    public async ValueTask<IReadOnlyList<CommandOutboxItem>> ReadOutboxAsync(
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var timeout = CreateTimeout(cancellationToken);
        await using var connection = await OpenRuntimeConnectionAsync(timeout.Token);
        await using var command = Command(
            $"SELECT outbox_seq, receipt_id, command_id, soul_id, device_binding_id, platform_account_id, topic, payload_sha256, payload_json::text, occurred_at FROM {Qualified("api_read_outbox")}(@after_seq, @limit, @runtime_capability)",
            connection);
        command.Parameters.AddWithValue("after_seq", afterSequence);
        command.Parameters.AddWithValue("limit", limit);
        AddRuntimeCapability(command);
        var result = new List<CommandOutboxItem>();
        await using var reader = await ExecuteReaderAsync(command, timeout.Token);
        while (await reader.ReadAsync(timeout.Token))
        {
            var payload = Deserialize<CommandReceiptV1>(reader.GetString(8));
            payload.Validate();
            var payloadSha256 = reader.GetString(7);
            if (!FixedDigestEquals(
                    payloadSha256,
                    CommandReceiptProtocolV1.ComputeReceiptSha256(payload)))
            {
                throw new InvalidDataException("Persisted outbox payload digest does not match its receipt.");
            }
            if (payload.ReceiptId != reader.GetGuid(1)
                || payload.CommandId != reader.GetGuid(2)
                || !string.Equals(payload.SoulId, reader.GetString(3), StringComparison.Ordinal)
                || !string.Equals(payload.DeviceBindingId, reader.GetString(4), StringComparison.Ordinal)
                || !string.Equals(payload.PlatformAccountId, reader.GetString(5), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Persisted outbox scope does not match its receipt payload.");
            }
            result.Add(new CommandOutboxItem(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), payloadSha256,
                payload, reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return result.AsReadOnly();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _authorizationVerifier.Dispose();
        _receiptVerifier.Dispose();
        CryptographicOperations.ZeroMemory(_runtimeCapability);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<LeaseContext> ReadLeaseContextAsync(
        Guid commandId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenRuntimeConnectionAsync(cancellationToken);
        await using var command = Command(
            $"SELECT state, attempt, lease_expires_at, command_sha256, dispatch_json::text FROM {Qualified("api_get_lease_context")}(@command_id, @lease_id, @runtime_capability)",
            connection);
        command.Parameters.AddWithValue("command_id", commandId);
        command.Parameters.AddWithValue("lease_id", leaseId);
        AddRuntimeCapability(command);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new KeyNotFoundException("Unknown leased command context.");
        var result = new LeaseContext(
            ParseEnum<CommandState>(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3),
            reader.GetString(4));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("PostgreSQL lease context returned multiple results.");
        return result;
    }

    private async Task<NpgsqlConnection> OpenRuntimeConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_options.RuntimeConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureSessionAsync(connection, cancellationToken);
            await AssertConnectionIdentityAndVersionAsync(
                connection,
                _options.RuntimeRole,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task VerifyRuntimeConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenRuntimeConnectionAsync(cancellationToken);
        await using var command = Command(
            $"SELECT schema_version, migration_sha256, catalog_sha256, server_version_num, migrator_role, runtime_role FROM {Qualified("api_runtime_attestation")}(@runtime_capability)",
            connection);
        AddRuntimeCapability(command);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Runtime catalog attestation is missing.");
        var schemaVersion = reader.GetString(0);
        var migrationSha256 = reader.GetString(1);
        var catalogSha256 = reader.GetString(2);
        var serverVersion = reader.GetInt32(3);
        var migratorRole = reader.GetString(4);
        var runtimeRole = reader.GetString(5);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Runtime catalog attestation is ambiguous.");
        if (!string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal)
            || !FixedDigestEquals(migrationSha256, _migrationSha256!)
            || !FixedDigestEquals(catalogSha256, _catalogSha256!)
            || serverVersion != PostgresCommandOrchestratorOptions.RequiredPostgresVersionNumber
            || !string.Equals(migratorRole, _options.MigratorRole, StringComparison.Ordinal)
            || !string.Equals(runtimeRole, _options.RuntimeRole, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Runtime PostgreSQL attestation does not match the exact migration, catalog, roles, and PostgreSQL 18.4 boundary.");
        }
    }

    private async Task ConfigureSessionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var milliseconds = checked((int)Math.Ceiling(_options.OperationTimeout.TotalMilliseconds));
        await using var command = Command(
            "SELECT set_config('TimeZone', 'UTC', false), set_config('statement_timeout', @statement_timeout, false), set_config('lock_timeout', @lock_timeout, false), set_config('idle_in_transaction_session_timeout', @idle_timeout, false)",
            connection);
        command.Parameters.AddWithValue("statement_timeout", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("lock_timeout", Math.Min(milliseconds, 5000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("idle_timeout", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await ExecuteScalarAsync(command, cancellationToken);
        await using var verify = Command(
            "SELECT current_setting('TimeZone'), current_setting('statement_timeout'), current_setting('lock_timeout'), current_setting('idle_in_transaction_session_timeout')",
            connection);
        await using var reader = await ExecuteReaderAsync(verify, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), "UTC", StringComparison.Ordinal)
            || string.Equals(reader.GetString(1), "0", StringComparison.Ordinal)
            || string.Equals(reader.GetString(2), "0", StringComparison.Ordinal)
            || string.Equals(reader.GetString(3), "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PostgreSQL UTC and timeout session policy was not applied.");
        }
    }

    private async Task AssertConnectionIdentityAndVersionAsync(
        NpgsqlConnection connection,
        string expectedRole,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT current_user, session_user, current_setting('server_version_num')::integer",
            connection);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("PostgreSQL connection identity probe returned no row.");
        var currentRole = reader.GetString(0);
        var sessionRole = reader.GetString(1);
        var version = reader.GetInt32(2);
        if (!string.Equals(currentRole, expectedRole, StringComparison.Ordinal)
            || !string.Equals(sessionRole, expectedRole, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PostgreSQL connection role does not match the declared identity.");
        }
        if (version != PostgresCommandOrchestratorOptions.RequiredPostgresVersionNumber)
        {
            throw new InvalidOperationException(
                $"Command Orchestrator requires exact PostgreSQL 18.4; server_version_num was {version}.");
        }
    }

    private async Task AcquireMigrationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT pg_advisory_xact_lock(hashtextextended(@schema_name, 730300))",
            connection,
            transaction);
        command.Parameters.AddWithValue("schema_name", _options.Schema);
        await ExecuteScalarAsync(command, cancellationToken);
    }

    private async Task<SchemaStatus> ReadSchemaStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            SELECT owner.rolname,
                   (SELECT count(*) FROM pg_class AS object WHERE object.relnamespace = namespace.oid)
                   + (SELECT count(*) FROM pg_proc AS routine WHERE routine.pronamespace = namespace.oid)
            FROM pg_namespace AS namespace
            JOIN pg_roles AS owner ON owner.oid = namespace.nspowner
            WHERE namespace.nspname = @schema_name
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema_name", _options.Schema);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new SchemaStatus(false, null, 0);
        var result = new SchemaStatus(true, reader.GetString(0), reader.GetInt64(1));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("PostgreSQL schema identity is ambiguous.");
        return result;
    }

    private async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_class AS object
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = @schema_name
                  AND object.relname = @table_name
                  AND object.relkind = 'r')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema_name", _options.Schema);
        command.Parameters.AddWithValue("table_name", table);
        return (bool)(await ExecuteScalarAsync(command, cancellationToken)
            ?? throw new InvalidDataException("PostgreSQL table probe returned NULL."));
    }

    private async Task AssertMigrationLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string expectedMigrationSha256,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"SELECT schema_version, migration_sha256, runtime_capability_sha256, migrator_role, server_version_num FROM {Qualified("migration_ledger")} ORDER BY schema_version",
            connection,
            transaction);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Command-orchestrator migration ledger is missing.");
        var schemaVersion = reader.GetString(0);
        var migrationSha256 = reader.GetString(1);
        var runtimeCapabilitySha256 = reader.GetString(2);
        var migratorRole = reader.GetString(3);
        var serverVersion = reader.GetInt32(4);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Command-orchestrator migration ledger is ambiguous.");
        if (!string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal)
            || !FixedDigestEquals(migrationSha256, expectedMigrationSha256)
            || !FixedDigestEquals(runtimeCapabilitySha256, _runtimeCapabilitySha256)
            || !string.Equals(migratorRole, _options.MigratorRole, StringComparison.Ordinal)
            || serverVersion != PostgresCommandOrchestratorOptions.RequiredPostgresVersionNumber)
        {
            throw new InvalidDataException(
                "Command-orchestrator migration ledger checksum, runtime capability, owner, version, or PostgreSQL attestation does not match.");
        }
    }

    private async Task InsertCatalogAttestationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string migrationSha256,
        string catalogSha256,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"INSERT INTO {Qualified("catalog_attestations")}(catalog_sha256, migration_sha256, schema_version, server_version_num) VALUES (@catalog_sha256, @migration_sha256, @schema_version, @server_version_num)",
            connection,
            transaction);
        command.Parameters.AddWithValue("catalog_sha256", catalogSha256);
        command.Parameters.AddWithValue("migration_sha256", migrationSha256);
        command.Parameters.AddWithValue("schema_version", SchemaVersion);
        command.Parameters.AddWithValue(
            "server_version_num",
            PostgresCommandOrchestratorOptions.RequiredPostgresVersionNumber);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidDataException("Catalog attestation was not inserted exactly once.");
        await AssertCatalogAttestationAsync(
            connection,
            transaction,
            migrationSha256,
            catalogSha256,
            cancellationToken);
    }

    private async Task AssertCatalogAttestationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string migrationSha256,
        string catalogSha256,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"SELECT catalog_sha256, migration_sha256, schema_version, server_version_num FROM {Qualified("catalog_attestations")} ORDER BY recorded_at",
            connection,
            transaction);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Command-orchestrator catalog attestation is missing.");
        var actualCatalog = reader.GetString(0);
        var actualMigration = reader.GetString(1);
        var actualVersion = reader.GetString(2);
        var actualPostgres = reader.GetInt32(3);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Command-orchestrator catalog attestation is ambiguous.");
        if (!FixedDigestEquals(actualCatalog, catalogSha256)
            || !FixedDigestEquals(actualMigration, migrationSha256)
            || !string.Equals(actualVersion, SchemaVersion, StringComparison.Ordinal)
            || actualPostgres != PostgresCommandOrchestratorOptions.RequiredPostgresVersionNumber)
        {
            throw new InvalidDataException("Command-orchestrator catalog attestation does not match the live catalog.");
        }
    }

    private async Task AssertCatalogShapeAndPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(
            """
            SELECT object.relname, owner.rolname, object.relpersistence,
                   object.relrowsecurity, object.relforcerowsecurity,
                   has_table_privilege(@runtime_role, object.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN'),
                   EXISTS (SELECT 1 FROM aclexplode(COALESCE(object.relacl, acldefault('r', object.relowner))) AS acl WHERE acl.grantee = 0)
            FROM pg_class AS object
            JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
            JOIN pg_roles AS owner ON owner.oid = object.relowner
            WHERE namespace.nspname = @schema_name AND object.relkind = 'r'
            ORDER BY object.relname
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                tables.Add(table);
                if (!string.Equals(reader.GetString(1), _options.MigratorRole, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException($"PostgreSQL table '{table}' is not migrator-owned.");
                if (!string.Equals(reader.GetString(2), "p", StringComparison.Ordinal)
                    || reader.GetBoolean(3)
                    || reader.GetBoolean(4))
                {
                    throw new UnauthorizedAccessException(
                        $"PostgreSQL table '{table}' is not permanent with row security disabled.");
                }
                if (reader.GetBoolean(5))
                    throw new UnauthorizedAccessException($"Runtime role has direct privileges on table '{table}'.");
                if (reader.GetBoolean(6))
                    throw new UnauthorizedAccessException($"Table '{table}' has an unexpected PUBLIC ACL.");
            }
        }
        if (!tables.SetEquals(ExpectedTables))
            throw new InvalidDataException("PostgreSQL command-orchestrator table inventory is not exact.");

        await using (var command = Command(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_attribute AS column_value
                JOIN pg_class AS object ON object.oid = column_value.attrelid
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = @schema_name
                  AND object.relkind = 'r'
                  AND column_value.attnum > 0
                  AND NOT column_value.attisdropped
                  AND (
                    has_column_privilege(@runtime_role, object.oid, column_value.attnum, 'SELECT')
                    OR has_column_privilege(@runtime_role, object.oid, column_value.attnum, 'INSERT')
                    OR has_column_privilege(@runtime_role, object.oid, column_value.attnum, 'UPDATE')
                    OR has_column_privilege(@runtime_role, object.oid, column_value.attnum, 'REFERENCES')))
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            if ((bool)(await ExecuteScalarAsync(command, cancellationToken)
                ?? throw new InvalidDataException("Column ACL probe returned NULL.")))
            {
                throw new UnauthorizedAccessException("Runtime role has a direct column-level privilege.");
            }
        }

        await using (var command = Command(
            """
            SELECT has_schema_privilege(@runtime_role, namespace.oid, 'USAGE'),
                   has_schema_privilege(@runtime_role, namespace.oid, 'CREATE'),
                   owner.rolname,
                   EXISTS (SELECT 1 FROM aclexplode(COALESCE(namespace.nspacl, acldefault('n', namespace.nspowner))) AS acl WHERE acl.grantee = 0)
            FROM pg_namespace AS namespace
            JOIN pg_roles AS owner ON owner.oid = namespace.nspowner
            WHERE namespace.nspname = @schema_name
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !reader.GetBoolean(0)
                || reader.GetBoolean(1)
                || !string.Equals(reader.GetString(2), _options.MigratorRole, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Schema ownership or runtime USAGE-only boundary is invalid.");
            }
            if (reader.GetBoolean(3))
                throw new UnauthorizedAccessException("Command-orchestrator schema has an unexpected PUBLIC ACL.");
        }

        await using (var command = Command(
            """
            SELECT role_value.rolname, role_value.rolcanlogin, role_value.rolsuper, role_value.rolinherit,
                   role_value.rolcreaterole, role_value.rolcreatedb, role_value.rolreplication,
                   role_value.rolbypassrls, COALESCE(role_value.rolconfig, ARRAY[]::text[]),
                   EXISTS (
                       SELECT 1
                       FROM pg_auth_members AS membership
                       WHERE membership.member = role_value.oid OR membership.roleid = role_value.oid)
            FROM pg_roles AS role_value
            WHERE role_value.rolname IN (@migrator_role, @runtime_role)
            ORDER BY role_value.rolname COLLATE "C"
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            command.Parameters.AddWithValue("migrator_role", _options.MigratorRole);
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                roles.Add(reader.GetString(0));
                if (!reader.GetBoolean(1)
                    || reader.GetBoolean(2)
                    || reader.GetBoolean(3)
                    || reader.GetBoolean(4)
                    || reader.GetBoolean(5)
                    || reader.GetBoolean(6)
                    || reader.GetBoolean(7)
                    || reader.GetFieldValue<string[]>(8).Length != 0
                    || reader.GetBoolean(9))
                {
                    throw new UnauthorizedAccessException(
                        "Migrator and runtime roles must be distinct login-only NOINHERIT roles without elevated attributes or memberships.");
                }
            }
            if (!roles.SetEquals([_options.MigratorRole, _options.RuntimeRole]))
                throw new InvalidDataException("Migrator/runtime PostgreSQL role inventory is not exact.");
        }

        var functions = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(
            """
            SELECT routine.proname, owner.rolname, routine.prosecdef,
                   COALESCE(routine.proconfig, ARRAY[]::text[]),
                   has_function_privilege(@runtime_role, routine.oid, 'EXECUTE'),
                   EXISTS (SELECT 1 FROM aclexplode(COALESCE(routine.proacl, acldefault('f', routine.proowner))) AS acl WHERE acl.grantee = 0)
            FROM pg_proc AS routine
            JOIN pg_namespace AS namespace ON namespace.oid = routine.pronamespace
            JOIN pg_roles AS owner ON owner.oid = routine.proowner
            WHERE namespace.nspname = @schema_name
            ORDER BY routine.proname, routine.oid::regprocedure::text
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var function = reader.GetString(0);
                if (!functions.Add(function))
                    throw new InvalidDataException($"PostgreSQL function '{function}' has an unexpected overload.");
                if (!string.Equals(reader.GetString(1), _options.MigratorRole, StringComparison.Ordinal)
                    || !reader.GetBoolean(2))
                {
                    throw new UnauthorizedAccessException($"PostgreSQL function '{function}' is not a migrator-owned SECURITY DEFINER.");
                }
                var configuration = reader.GetFieldValue<string[]>(3).ToHashSet(StringComparer.Ordinal);
                if (!configuration.SetEquals(["search_path=pg_catalog", "row_security=off"]))
                    throw new UnauthorizedAccessException($"PostgreSQL function '{function}' has unsafe proconfig.");
                var runtimeCanExecute = reader.GetBoolean(4);
                if (runtimeCanExecute != RuntimeFunctions.Contains(function))
                    throw new UnauthorizedAccessException($"PostgreSQL function '{function}' runtime EXECUTE ACL is not exact.");
                if (reader.GetBoolean(5))
                    throw new UnauthorizedAccessException($"PostgreSQL function '{function}' is executable by PUBLIC.");
            }
        }
        if (!functions.SetEquals(ExpectedFunctions))
            throw new InvalidDataException("PostgreSQL command-orchestrator function inventory is not exact.");

        var triggers = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(
            """
            SELECT trigger_value.tgname, trigger_value.tgenabled,
                   trigger_function.proname, trigger_owner.rolname
            FROM pg_trigger AS trigger_value
            JOIN pg_class AS object ON object.oid = trigger_value.tgrelid
            JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
            JOIN pg_proc AS trigger_function ON trigger_function.oid = trigger_value.tgfoid
            JOIN pg_roles AS trigger_owner ON trigger_owner.oid = trigger_function.proowner
            WHERE namespace.nspname = @schema_name AND NOT trigger_value.tgisinternal
            ORDER BY trigger_value.tgname COLLATE "C"
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var trigger = reader.GetString(0);
                triggers.Add(trigger);
                if (!string.Equals(reader.GetString(1), "O", StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(2), "reject_append_only_mutation", StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(3), _options.MigratorRole, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Append-only trigger '{trigger}' is disabled or has an unexpected owner/function.");
                }
            }
        }
        if (!triggers.SetEquals(ExpectedTriggers))
            throw new InvalidDataException("Append-only trigger inventory is missing or extra.");

        var sequences = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(
            """
            SELECT sequence_value.relname, owner.rolname,
                   has_sequence_privilege(@runtime_role, sequence_value.oid, 'USAGE,SELECT,UPDATE'),
                   EXISTS (
                       SELECT 1
                       FROM aclexplode(COALESCE(sequence_value.relacl, acldefault('S', sequence_value.relowner))) AS acl
                       WHERE acl.grantee = 0),
                   sequence_parameters.seqstart, sequence_parameters.seqincrement,
                   sequence_parameters.seqmax, sequence_parameters.seqmin,
                   sequence_parameters.seqcache, sequence_parameters.seqcycle,
                   owned_table.relname, owned_column.attname, owned_column.attidentity,
                   ownership.deptype, format_type(sequence_parameters.seqtypid, -1)
            FROM pg_class AS sequence_value
            JOIN pg_namespace AS namespace ON namespace.oid = sequence_value.relnamespace
            JOIN pg_roles AS owner ON owner.oid = sequence_value.relowner
            JOIN pg_sequence AS sequence_parameters ON sequence_parameters.seqrelid = sequence_value.oid
            JOIN pg_depend AS ownership
              ON ownership.classid = 'pg_class'::regclass
             AND ownership.objid = sequence_value.oid
             AND ownership.refclassid = 'pg_class'::regclass
             AND ownership.deptype IN ('a', 'i')
            JOIN pg_class AS owned_table ON owned_table.oid = ownership.refobjid
            JOIN pg_attribute AS owned_column
              ON owned_column.attrelid = ownership.refobjid
             AND owned_column.attnum = ownership.refobjsubid
            WHERE namespace.nspname = @schema_name
              AND sequence_value.relkind = 'S'
            ORDER BY sequence_value.relname
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sequence = reader.GetString(0);
                sequences.Add(sequence);
                if (!string.Equals(reader.GetString(1), _options.MigratorRole, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException($"PostgreSQL sequence '{sequence}' is not migrator-owned.");
                if (reader.GetBoolean(2))
                    throw new UnauthorizedAccessException($"Runtime role has direct privileges on sequence '{sequence}'.");
                if (reader.GetBoolean(3))
                    throw new UnauthorizedAccessException($"Sequence '{sequence}' has an unexpected PUBLIC ACL.");
                if (reader.GetInt64(4) != 1
                    || reader.GetInt64(5) != 1
                    || reader.GetInt64(6) != long.MaxValue
                    || reader.GetInt64(7) != 1
                    || reader.GetInt64(8) != 1
                    || reader.GetBoolean(9)
                    || !ExpectedSequenceOwners.TryGetValue(sequence, out var expectedOwner)
                    || !string.Equals(reader.GetString(10), expectedOwner.Table, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(11), expectedOwner.Column, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(12), "a", StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(13), "i", StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(14), "bigint", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Sequence '{sequence}' parameters, identity, or owned-by dependency are not exact.");
                }
            }
        }
        if (!sequences.SetEquals(ExpectedSequences))
            throw new InvalidDataException("PostgreSQL command-orchestrator sequence inventory is not exact.");

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(
            """
            SELECT index_value.relname, owner.rolname, index_value.relpersistence
            FROM pg_class AS index_value
            JOIN pg_namespace AS namespace ON namespace.oid = index_value.relnamespace
            JOIN pg_roles AS owner ON owner.oid = index_value.relowner
            WHERE namespace.nspname = @schema_name AND index_value.relkind = 'i'
            ORDER BY index_value.relname COLLATE "C"
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            await using var reader = await ExecuteReaderAsync(command, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add(reader.GetString(0));
                if (!string.Equals(reader.GetString(1), _options.MigratorRole, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(2), "p", StringComparison.Ordinal))
                    throw new InvalidDataException("An index has an unexpected owner or persistence.");
            }
        }
        if (!indexes.SetEquals(ExpectedIndexes))
            throw new InvalidDataException("PostgreSQL command-orchestrator index inventory is not exact.");

        await AssertExactAclAllowlistAsync(connection, transaction, cancellationToken);

        await using (var command = Command(
            """
            SELECT count(*)
            FROM pg_class AS object
            JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
            WHERE namespace.nspname = @schema_name
              AND object.relkind NOT IN ('r', 'S', 'i')
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("schema_name", _options.Schema);
            if (Convert.ToInt64(await ExecuteScalarAsync(command, cancellationToken)) != 0)
                throw new InvalidDataException("Unexpected relation kind exists in the command-orchestrator schema.");
        }
    }

    private async Task AssertExactAclAllowlistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        static string Token(
            string kind,
            string name,
            string grantee,
            string grantor,
            string privilege,
            bool isGrantable) =>
            string.Join('|', kind, name, grantee, grantor, privilege, isGrantable ? "grantable" : "plain");
        expected.Add(Token("schema", _options.Schema, _options.MigratorRole, _options.MigratorRole, "CREATE", false));
        expected.Add(Token("schema", _options.Schema, _options.MigratorRole, _options.MigratorRole, "USAGE", false));
        expected.Add(Token("schema", _options.Schema, _options.RuntimeRole, _options.MigratorRole, "USAGE", false));
        foreach (var table in ExpectedTables)
        foreach (var privilege in new[] { "DELETE", "INSERT", "MAINTAIN", "REFERENCES", "SELECT", "TRIGGER", "TRUNCATE", "UPDATE" })
            expected.Add(Token("table", table, _options.MigratorRole, _options.MigratorRole, privilege, false));
        foreach (var sequence in ExpectedSequences)
        foreach (var privilege in new[] { "SELECT", "UPDATE", "USAGE" })
            expected.Add(Token("sequence", sequence, _options.MigratorRole, _options.MigratorRole, privilege, false));
        foreach (var function in ExpectedFunctions)
        {
            expected.Add(Token("function", function, _options.MigratorRole, _options.MigratorRole, "EXECUTE", false));
            if (RuntimeFunctions.Contains(function))
                expected.Add(Token("function", function, _options.RuntimeRole, _options.MigratorRole, "EXECUTE", false));
        }
        expected.Add(Token("default-function", "f", _options.MigratorRole, _options.MigratorRole, "EXECUTE", false));

        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Command(
            """
            WITH acl_rows(kind, object_name, grantee_oid, grantor_oid, privilege_type, is_grantable) AS (
                SELECT 'schema', namespace.nspname, acl.grantee, acl.grantor,
                       acl.privilege_type, acl.is_grantable
                FROM pg_namespace AS namespace
                CROSS JOIN LATERAL aclexplode(COALESCE(namespace.nspacl, acldefault('n', namespace.nspowner))) AS acl
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'table', object.relname, acl.grantee, acl.grantor,
                       acl.privilege_type, acl.is_grantable
                FROM pg_class AS object
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                CROSS JOIN LATERAL aclexplode(COALESCE(object.relacl, acldefault('r', object.relowner))) AS acl
                WHERE namespace.nspname = @schema_name AND object.relkind = 'r'
                UNION ALL
                SELECT 'column', object.relname || '.' || column_value.attname,
                       acl.grantee, acl.grantor, acl.privilege_type, acl.is_grantable
                FROM pg_attribute AS column_value
                JOIN pg_class AS object ON object.oid = column_value.attrelid
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                CROSS JOIN LATERAL aclexplode(
                    COALESCE(column_value.attacl, acldefault('c', object.relowner))) AS acl
                WHERE namespace.nspname = @schema_name
                  AND object.relkind = 'r'
                  AND column_value.attnum > 0
                  AND NOT column_value.attisdropped
                UNION ALL
                SELECT 'sequence', object.relname, acl.grantee, acl.grantor,
                       acl.privilege_type, acl.is_grantable
                FROM pg_class AS object
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                CROSS JOIN LATERAL aclexplode(COALESCE(object.relacl, acldefault('S', object.relowner))) AS acl
                WHERE namespace.nspname = @schema_name AND object.relkind = 'S'
                UNION ALL
                SELECT 'function', routine.proname, acl.grantee, acl.grantor,
                       acl.privilege_type, acl.is_grantable
                FROM pg_proc AS routine
                JOIN pg_namespace AS namespace ON namespace.oid = routine.pronamespace
                CROSS JOIN LATERAL aclexplode(COALESCE(routine.proacl, acldefault('f', routine.proowner))) AS acl
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'default-function', default_acl.defaclobjtype::text,
                       acl.grantee, acl.grantor, acl.privilege_type, acl.is_grantable
                FROM pg_default_acl AS default_acl
                JOIN pg_roles AS default_owner ON default_owner.oid = default_acl.defaclrole
                LEFT JOIN pg_namespace AS namespace ON namespace.oid = default_acl.defaclnamespace
                CROSS JOIN LATERAL aclexplode(default_acl.defaclacl) AS acl
                WHERE default_owner.rolname = @migrator_role
                  AND (default_acl.defaclnamespace = 0 OR namespace.nspname = @schema_name)
            )
            SELECT acl_rows.kind, acl_rows.object_name,
                   CASE WHEN acl_rows.grantee_oid = 0 THEN 'PUBLIC' ELSE grantee.rolname END AS grantee_name,
                   grantor.rolname AS grantor_name, acl_rows.privilege_type,
                   acl_rows.is_grantable
            FROM acl_rows
            LEFT JOIN pg_roles AS grantee ON grantee.oid = acl_rows.grantee_oid
            JOIN pg_roles AS grantor ON grantor.oid = acl_rows.grantor_oid
            ORDER BY acl_rows.kind COLLATE "C", acl_rows.object_name COLLATE "C",
                     grantee_name COLLATE "C", grantor_name COLLATE "C",
                     acl_rows.privilege_type COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("schema_name", _options.Schema);
        command.Parameters.AddWithValue("migrator_role", _options.MigratorRole);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            actual.Add(Token(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5)));
        if (!actual.SetEquals(expected))
            throw new UnauthorizedAccessException("Schema, object, function, sequence, or default ACL is outside the exact allowlist.");
    }

    private async Task<string> ComputeCatalogSha256Async(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            WITH inventory(kind, payload) AS (
                SELECT 'schema', jsonb_build_array(namespace.nspname, owner.rolname, COALESCE(namespace.nspacl::text, ''))::text
                FROM pg_namespace AS namespace
                JOIN pg_roles AS owner ON owner.oid = namespace.nspowner
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'role', jsonb_build_array(role_value.rolname, role_value.rolcanlogin,
                    role_value.rolsuper, role_value.rolinherit, role_value.rolcreaterole,
                    role_value.rolcreatedb, role_value.rolreplication, role_value.rolbypassrls,
                    COALESCE(role_value.rolconfig::text, ''))::text
                FROM pg_roles AS role_value
                WHERE role_value.rolname IN (@migrator_role, @runtime_role)
                UNION ALL
                SELECT 'role_membership', jsonb_build_array(role_value.rolname, member_value.rolname,
                    membership.admin_option, membership.inherit_option, membership.set_option)::text
                FROM pg_auth_members AS membership
                JOIN pg_roles AS role_value ON role_value.oid = membership.roleid
                JOIN pg_roles AS member_value ON member_value.oid = membership.member
                WHERE role_value.rolname IN (@migrator_role, @runtime_role)
                   OR member_value.rolname IN (@migrator_role, @runtime_role)
                UNION ALL
                SELECT 'default_acl', jsonb_build_array(default_owner.rolname,
                    COALESCE(namespace.nspname, ''), default_acl.defaclobjtype,
                    default_acl.defaclacl::text)::text
                FROM pg_default_acl AS default_acl
                JOIN pg_roles AS default_owner ON default_owner.oid = default_acl.defaclrole
                LEFT JOIN pg_namespace AS namespace ON namespace.oid = default_acl.defaclnamespace
                WHERE default_owner.rolname = @migrator_role
                  AND (default_acl.defaclnamespace = 0 OR namespace.nspname = @schema_name)
                UNION ALL
                SELECT 'relation', jsonb_build_array(object.relname, object.relkind, owner.rolname,
                    object.relpersistence, object.relrowsecurity, object.relforcerowsecurity,
                    COALESCE(object.relacl::text, ''))::text
                FROM pg_class AS object
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                JOIN pg_roles AS owner ON owner.oid = object.relowner
                WHERE namespace.nspname = @schema_name AND object.relkind IN ('r', 'S', 'i')
                UNION ALL
                SELECT 'sequence', jsonb_build_array(sequence_value.relname,
                    format_type(sequence_parameters.seqtypid, -1),
                    sequence_parameters.seqstart, sequence_parameters.seqincrement,
                    sequence_parameters.seqmax, sequence_parameters.seqmin,
                    sequence_parameters.seqcache, sequence_parameters.seqcycle,
                    owned_table.relname, owned_column.attname, owned_column.attidentity,
                    owned_column.attgenerated, ownership.deptype)::text
                FROM pg_class AS sequence_value
                JOIN pg_namespace AS namespace ON namespace.oid = sequence_value.relnamespace
                JOIN pg_sequence AS sequence_parameters ON sequence_parameters.seqrelid = sequence_value.oid
                JOIN pg_depend AS ownership
                  ON ownership.classid = 'pg_class'::regclass
                 AND ownership.objid = sequence_value.oid
                 AND ownership.refclassid = 'pg_class'::regclass
                 AND ownership.deptype IN ('a', 'i')
                JOIN pg_class AS owned_table ON owned_table.oid = ownership.refobjid
                JOIN pg_attribute AS owned_column
                  ON owned_column.attrelid = ownership.refobjid
                 AND owned_column.attnum = ownership.refobjsubid
                WHERE namespace.nspname = @schema_name AND sequence_value.relkind = 'S'
                UNION ALL
                SELECT 'column', jsonb_build_array(object.relname, column_value.attname,
                    format_type(column_value.atttypid, column_value.atttypmod), column_value.attnotnull,
                    COALESCE(pg_get_expr(default_value.adbin, default_value.adrelid), ''),
                    COALESCE(collation.collname, ''), COALESCE(column_value.attacl::text, ''))::text
                FROM pg_attribute AS column_value
                JOIN pg_class AS object ON object.oid = column_value.attrelid
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                LEFT JOIN pg_attrdef AS default_value ON default_value.adrelid = object.oid AND default_value.adnum = column_value.attnum
                LEFT JOIN pg_collation AS collation ON collation.oid = column_value.attcollation AND column_value.attcollation <> 0
                WHERE namespace.nspname = @schema_name AND column_value.attnum > 0 AND NOT column_value.attisdropped
                UNION ALL
                SELECT 'constraint', jsonb_build_array(object.relname, constraint_value.conname,
                    constraint_value.contype, pg_get_constraintdef(constraint_value.oid, false))::text
                FROM pg_constraint AS constraint_value
                JOIN pg_class AS object ON object.oid = constraint_value.conrelid
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'function', jsonb_build_array(routine.oid::regprocedure::text, owner.rolname,
                    routine.prosecdef, routine.provolatile, routine.proparallel,
                    COALESCE(routine.proconfig::text, ''), COALESCE(routine.proacl::text, ''),
                    pg_get_functiondef(routine.oid))::text
                FROM pg_proc AS routine
                JOIN pg_namespace AS namespace ON namespace.oid = routine.pronamespace
                JOIN pg_roles AS owner ON owner.oid = routine.proowner
                WHERE namespace.nspname = @schema_name
                UNION ALL
                SELECT 'trigger', jsonb_build_array(object.relname, trigger_value.tgname,
                    trigger_value.tgenabled, pg_get_triggerdef(trigger_value.oid, false))::text
                FROM pg_trigger AS trigger_value
                JOIN pg_class AS object ON object.oid = trigger_value.tgrelid
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = @schema_name AND NOT trigger_value.tgisinternal
                UNION ALL
                SELECT 'index', jsonb_build_array(object.relname, index_value.relname,
                    pg_get_indexdef(index_value.oid))::text
                FROM pg_index AS index_link
                JOIN pg_class AS object ON object.oid = index_link.indrelid
                JOIN pg_class AS index_value ON index_value.oid = index_link.indexrelid
                JOIN pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = @schema_name)
            SELECT kind, payload FROM inventory ORDER BY kind COLLATE "C", payload COLLATE "C"
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema_name", _options.Schema);
        command.Parameters.AddWithValue("runtime_role", _options.RuntimeRole);
        command.Parameters.AddWithValue("migrator_role", _options.MigratorRole);
        using var canonical = new MemoryStream();
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);
        var rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            WriteCatalogToken(canonical, reader.GetString(0));
            WriteCatalogToken(canonical, reader.GetString(1));
            rows++;
            if (rows > 2048 || canonical.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("PostgreSQL catalog attestation exceeded its bounded inventory.");
        }
        if (rows < 100)
            throw new InvalidDataException("PostgreSQL catalog attestation inventory is unexpectedly small.");
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray()));
    }

    private static void WriteCatalogToken(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static (string Text, string Sha256) ReadMigration()
    {
        var assembly = typeof(PostgresCommandOrchestrator).Assembly;
        using var stream = assembly.GetManifestResourceStream(MigrationResource)
            ?? throw new InvalidOperationException("Embedded command-orchestrator migration is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        try
        {
            return (
                StrictUtf8.GetString(bytes),
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private NpgsqlCommand Command(
        string text,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
        => new(text, connection, transaction)
        {
            CommandTimeout = checked((int)Math.Ceiling(_options.OperationTimeout.TotalSeconds))
        };

    private static async Task<NpgsqlDataReader> ExecuteReaderAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw Translate(exception);
        }
    }

    private static async Task<object?> ExecuteScalarAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw Translate(exception);
        }
    }

    private static Exception Translate(PostgresException exception) => exception.SqlState switch
    {
        "42501" => new UnauthorizedAccessException(exception.MessageText, exception),
        "P0002" => new KeyNotFoundException(exception.MessageText, exception),
        "55000" or "54000" or "23505" => new InvalidOperationException(exception.MessageText, exception),
        "22023" => new ArgumentException(exception.MessageText, exception),
        "23514" or "23503" or "23502" => new InvalidDataException(exception.MessageText, exception),
        _ => exception
    };

    private static void AddScope(
        NpgsqlCommand command,
        string soulId,
        string deviceBindingId,
        string platformAccountId)
    {
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
    }

    private static void AddJson(NpgsqlCommand command, string name, string json) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, json);

    private void AddRuntimeCapability(NpgsqlCommand command) =>
        command.Parameters.AddWithValue(
            "runtime_capability",
            NpgsqlDbType.Bytea,
            _runtimeCapability);

    private static string Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DatabaseJson);
        try
        {
            if (bytes.Length is < 2 or > 524288)
                throw new InvalidDataException("PostgreSQL command payload exceeds its bounded JSON size.");
            return StrictUtf8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static T Deserialize<T>(string json)
    {
        var bytes = StrictUtf8.GetBytes(json);
        try
        {
            if (bytes.Length is < 2 or > 524288)
                throw new InvalidDataException("Persisted PostgreSQL command payload exceeds its bounded JSON size.");
            return JsonSerializer.Deserialize<T>(bytes, DatabaseJson)
                ?? throw new InvalidDataException("Persisted PostgreSQL command payload decoded to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Persisted PostgreSQL command payload is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new InvalidDataException($"PostgreSQL returned unknown {typeof(T).Name} value '{value}'.");
        }
        return parsed;
    }

    private static bool FixedDigestEquals(string left, string right)
    {
        byte[] leftBytes;
        byte[] rightBytes;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("A persisted digest is not hexadecimal SHA-256.", exception);
        }
        try
        {
            return leftBytes.Length == 32
                && rightBytes.Length == 32
                && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        return timeout;
    }

    private void EnsureReady()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) != 1)
            throw new InvalidOperationException("PostgreSQL command orchestrator must be initialized before use.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);

    private string Qualified(string objectName) =>
        string.Concat(QuotedSchema, ".", QuoteIdentifier(objectName));

    private string QuotedSchema => QuoteIdentifier(_options.Schema);

    private static string QuoteIdentifier(string identifier) =>
        new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private sealed record SchemaStatus(bool Exists, string? Owner, long ObjectCount);
    private sealed record LeaseReservation(
        string OperationJson,
        string OperationSha256,
        int Attempt,
        Guid LeaseId,
        DateTimeOffset LeaseExpiresAt,
        DateTimeOffset AcquiredAt);
    private sealed record LeaseContext(
        CommandState State,
        int Attempt,
        DateTimeOffset LeaseExpiresAt,
        string CommandSha256,
        string DispatchJson);
}

internal static class PostgresCommandStoreValidation
{
    private static readonly Regex IdentifierPattern = new(
        "\\A[a-z][a-z0-9_]{0,62}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!IdentifierPattern.IsMatch(value))
            throw new ArgumentException(
                "PostgreSQL identifiers must be lowercase ASCII and no longer than 63 bytes.",
                parameterName);
    }

    internal static NpgsqlConnectionStringBuilder ParseConnectionString(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        NpgsqlConnectionStringBuilder builder;
        try { builder = new NpgsqlConnectionStringBuilder(value); }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("PostgreSQL connection string is invalid.", parameterName, exception);
        }
        if (string.IsNullOrWhiteSpace(builder.Host)
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new ArgumentException(
                "PostgreSQL connection string requires explicit host, database, and username.",
                parameterName);
        }
        return builder;
    }

}
