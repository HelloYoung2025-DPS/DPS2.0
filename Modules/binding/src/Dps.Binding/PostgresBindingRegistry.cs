using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dps.Binding.Contracts;
using Dps.DeviceRegistry.Contracts;
using Dps.PlatformAccountRegistry.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.Binding;

public sealed record PostgresBindingRegistryOptions(string ConnectionString, string SchemaName, long TrustEpoch = 1)
{
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(ConnectionString));
        if (string.IsNullOrWhiteSpace(SchemaName) || !SchemaPattern.IsMatch(SchemaName))
            throw new ArgumentException("SchemaName must be a safe lowercase PostgreSQL identifier.", nameof(SchemaName));
        if (TrustEpoch < 1) throw new ArgumentOutOfRangeException(nameof(TrustEpoch));
    }

    public override string ToString()
        => $"PostgresBindingRegistryOptions {{ ConnectionString = [REDACTED], SchemaName = {SchemaName}, TrustEpoch = {TrustEpoch} }}";
}

public enum BindingMutationStage
{
    PendingAttemptWritten,
    ProvidersReserved,
    ProvidersConfirmed,
    BindingWritten,
    RevisionWritten,
    IdempotencyReceiptWritten,
    OutboxWritten,
    BeforeCommit
}

public delegate ValueTask BindingMutationFaultInjector(
    BindingMutationStage stage,
    CancellationToken cancellationToken);

public sealed record BindingOutboxRecord(
    Guid OutboxId,
    IdentityBindingV1 Payload,
    string PayloadSha256,
    DateTimeOffset? DispatchedAt);

public sealed record BindingIdempotencyQuarantineRecord(
    string IdempotencyKeySha256,
    string IncomingOperation,
    string ExistingRequestSha256,
    string IncomingRequestSha256,
    string Reason);

public sealed class PostgresBindingRegistry : IBindingRegistry
{
    private const string BindOperation = "bind";
    private const string RevokeOperation = "revoke";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresBindingRegistryOptions _options;
    private readonly IDeviceBindingReservationProvider _deviceProvider;
    private readonly IPlatformAccountBindingReservationProvider _accountProvider;
    private readonly BindingMutationFaultInjector? _faultInjector;

    private PostgresBindingRegistry(
        PostgresBindingRegistryOptions options,
        IDeviceBindingReservationClient deviceProvider,
        IPlatformAccountBindingReservationClient accountProvider,
        BindingMutationFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _deviceProvider = new TrustedDeviceRegistryProvider(deviceProvider);
        _accountProvider = new TrustedPlatformAccountRegistryProvider(accountProvider);
        _faultInjector = faultInjector;
    }

    public static async Task<PostgresBindingRegistry> CreateForCompositionAsync(
        PostgresBindingRegistryOptions options,
        IDeviceBindingReservationClient deviceProvider,
        IPlatformAccountBindingReservationClient accountProvider,
        SignedBindingCompositionAttestationV1 attestation,
        CancellationToken cancellationToken = default)
    {
        BindingCompositionAttestationVerifier.VerifyProduction(attestation, options, deviceProvider, accountProvider);
        await ApplyMigrationsAsync(options, cancellationToken);
        BindingCompositionAttestationVerifier.VerifyProduction(attestation, options, deviceProvider, accountProvider);
        await RecordCompositionGenerationAsync(options, attestation, cancellationToken);
        var registry = new PostgresBindingRegistry(options, deviceProvider, accountProvider);
        await registry.RecoverPendingAttemptsAsync(cancellationToken);
        return registry;
    }

    internal static PostgresBindingRegistry CreateForTests(
        PostgresBindingRegistryOptions options,
        IDeviceBindingReservationClient deviceProvider,
        IPlatformAccountBindingReservationClient accountProvider,
        BindingMutationFaultInjector? faultInjector = null)
        => new(options, deviceProvider, accountProvider, faultInjector);

    private static async Task RecordCompositionGenerationAsync(
        PostgresBindingRegistryOptions options,
        SignedBindingCompositionAttestationV1 attestation,
        CancellationToken cancellationToken)
    {
        var descriptorSha256 = BindingCompositionAttestationVerifier.ComputeCompositionDescriptorSha256(attestation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var connectionBuilder = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            CommandTimeout = 5,
            Pooling = false
        };
        try
        {
            await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
            await connection.OpenAsync(linked.Token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, linked.Token);
            await AcquireLockAsync(connection, transaction, "binding:composition-generation", linked.Token);
            long? currentGeneration = null;
            string? currentBomSha256 = null;
            string? currentDescriptorSha256 = null;
            await using (var read = new NpgsqlCommand(
                $"SELECT highest_generation, release_bom_sha256, composition_descriptor_sha256 FROM {options.SchemaName}.composition_generation_state WHERE scope = 'binding-production' FOR UPDATE",
                connection,
                transaction))
            await using (var reader = await read.ExecuteReaderAsync(linked.Token))
            {
                if (await reader.ReadAsync(linked.Token))
                {
                    currentGeneration = reader.GetInt64(0);
                    currentBomSha256 = reader.GetString(1);
                    currentDescriptorSha256 = reader.GetString(2);
                }
            }

            EnsureCompositionGenerationTransition(
                currentGeneration,
                currentBomSha256,
                currentDescriptorSha256,
                attestation.Generation,
                attestation.ReleaseBomSha256,
                descriptorSha256);

            await using var write = currentGeneration is null
                ? new NpgsqlCommand(
                    $"INSERT INTO {options.SchemaName}.composition_generation_state (scope, highest_generation, release_bom_sha256, composition_descriptor_sha256, attestation_expires_at) VALUES ('binding-production', @generation, @bom, @descriptor, @expires_at)",
                    connection,
                    transaction)
                : new NpgsqlCommand(
                    $"UPDATE {options.SchemaName}.composition_generation_state SET highest_generation = @generation, release_bom_sha256 = @bom, composition_descriptor_sha256 = @descriptor, attestation_expires_at = @expires_at, updated_at = clock_timestamp() WHERE scope = 'binding-production'",
                    connection,
                    transaction);
            write.Parameters.AddWithValue("generation", attestation.Generation);
            write.Parameters.AddWithValue("bom", attestation.ReleaseBomSha256);
            write.Parameters.AddWithValue("descriptor", descriptorSha256);
            write.Parameters.AddWithValue("expires_at", attestation.ExpiresAt);
            if (await write.ExecuteNonQueryAsync(linked.Token) != 1)
                throw new InvalidOperationException("The binding composition generation fence was not persisted.");
            await transaction.CommitAsync(linked.Token);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The binding composition generation fence exceeded five seconds.", exception);
        }
    }

    internal static void EnsureCompositionGenerationTransition(
        long? currentGeneration,
        string? currentBomSha256,
        string? currentDescriptorSha256,
        long incomingGeneration,
        string incomingBomSha256,
        string incomingDescriptorSha256)
    {
        if (incomingGeneration < 1) throw new ArgumentOutOfRangeException(nameof(incomingGeneration));
        BindingContractValidation.RequireSha256(incomingBomSha256, nameof(incomingBomSha256));
        BindingContractValidation.RequireSha256(incomingDescriptorSha256, nameof(incomingDescriptorSha256));
        if (currentGeneration is null) return;
        if (incomingGeneration < currentGeneration.Value)
            throw new UnauthorizedAccessException("A lower signed Release BOM generation cannot be replayed.");
        if (incomingGeneration > currentGeneration.Value) return;
        if (!string.Equals(currentBomSha256, incomingBomSha256, StringComparison.Ordinal) ||
            !string.Equals(currentDescriptorSha256, incomingDescriptorSha256, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("One signed Release BOM generation cannot identify two compositions.");
        }
    }

    public IBindingMutationFenceClient CreateMutationFenceClient()
        => new PostgresBindingMutationFenceClient(this);

    internal async Task<IBindingMutationFenceLease> AcquireMutationFenceAsync(
        AcquireBindingMutationFenceCommand command,
        CancellationToken cancellationToken)
    {
        BindingValidation.ValidateFence(command);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var connection = await OpenFenceConnectionAsync(linked.Token);
        var lockName = "binding:id:" + command.DeviceBindingId;
        try
        {
            await AcquireSessionLockAsync(connection, lockName, linked.Token);
            var current = await ReadBindingByIdAsync(
                connection,
                transaction: null,
                command.DeviceBindingId,
                forUpdate: false,
                linked.Token) ?? throw new KeyNotFoundException("Unknown binding.");
            EnsureScope(current, command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
            if (!string.Equals(current.Status, "active", StringComparison.Ordinal))
                throw new InvalidOperationException("A mutation fence requires the current active binding.");

            var fenceId = "bfence_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, linked.Token);
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {_options.SchemaName}.binding_mutation_fences (fence_id, soul_id, device_binding_id, platform_account_id, binding_revision, trace_id, idempotency_key, occurred_at) VALUES (@fence_id, @soul_id, @device_binding_id, @platform_account_id, @binding_revision, @trace_id, @idempotency_key, @occurred_at) RETURNING fence_sequence",
                connection,
                transaction);
            insert.Parameters.AddWithValue("fence_id", fenceId);
            insert.Parameters.AddWithValue("soul_id", current.SoulId);
            insert.Parameters.AddWithValue("device_binding_id", current.DeviceBindingId);
            insert.Parameters.AddWithValue("platform_account_id", current.PlatformAccountId);
            insert.Parameters.AddWithValue("binding_revision", current.BindingRevision);
            insert.Parameters.AddWithValue("trace_id", command.TraceId);
            insert.Parameters.AddWithValue("idempotency_key", command.IdempotencyKey);
            insert.Parameters.AddWithValue("occurred_at", command.OccurredAt);
            var sequence = Convert.ToInt64(
                await insert.ExecuteScalarAsync(linked.Token),
                System.Globalization.CultureInfo.InvariantCulture);
            await transaction.CommitAsync(linked.Token);

            var receipt = new BindingMutationFenceV1(
                BindingMutationFenceV1.CurrentSchemaVersion,
                BindingMutationFenceV1.CurrentContractId,
                BindingMutationFenceV1.CurrentProducerModule,
                current.SoulId,
                current.DeviceBindingId,
                current.PlatformAccountId,
                command.TraceId,
                command.IdempotencyKey,
                command.OccurredAt,
                "sensitive",
                current.BindingRevision,
                fenceId,
                sequence,
                "held");
            receipt.Validate();
            return new PostgresBindingMutationFenceLease(
                receipt,
                () => ReleaseMutationFenceAsync(connection, fenceId, lockName));
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await ForceCloseFenceConnectionAsync(connection);
            throw new TimeoutException("The binding mutation fence could not be acquired within five seconds.", exception);
        }
        catch
        {
            await ForceCloseFenceConnectionAsync(connection);
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ApplyMigrationsAsync(_options, cancellationToken);
        await RecoverPendingAttemptsAsync(cancellationToken);
    }

    private static async Task ApplyMigrationsAsync(
        PostgresBindingRegistryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var assembly = typeof(PostgresBindingRegistry).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(static name => name.Contains(".Migrations.", StringComparison.Ordinal) &&
                                  name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resourceNames.Length == 0)
            throw new InvalidOperationException("No embedded binding migrations were found.");
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var bootstrap = new NpgsqlCommand(
            $"""
            CREATE SCHEMA IF NOT EXISTS {options.SchemaName};
            CREATE TABLE IF NOT EXISTS {options.SchemaName}.module_schema_migrations (
                migration_id text PRIMARY KEY,
                content_sha256 char(64) NOT NULL CHECK (length(content_sha256) = 64 AND content_sha256 !~ '[^a-f0-9]'),
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            REVOKE ALL ON TABLE {options.SchemaName}.module_schema_migrations FROM PUBLIC;
            """,
            connection))
        {
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var resourceName in resourceNames)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            var migrationTemplate = await reader.ReadToEndAsync(cancellationToken);
            var migrationSha256 = ComputePayloadSha256(migrationTemplate);
            var migrationId = resourceName[(resourceName.IndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireLockAsync(connection, transaction, "binding:migrations", cancellationToken);
            await using (var read = new NpgsqlCommand(
                $"SELECT content_sha256 FROM {options.SchemaName}.module_schema_migrations WHERE migration_id = @migration_id FOR UPDATE",
                connection,
                transaction))
            {
                read.Parameters.AddWithValue("migration_id", migrationId);
                var existing = await read.ExecuteScalarAsync(cancellationToken) as string;
                if (existing is not null)
                {
                    if (!string.Equals(existing, migrationSha256, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Applied binding migration '{migrationId}' no longer matches its embedded SHA-256.");
                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }
            }

            var migration = migrationTemplate.Replace("__SCHEMA__", options.SchemaName, StringComparison.Ordinal);
            await using (var command = new NpgsqlCommand(migration, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var record = new NpgsqlCommand(
                $"INSERT INTO {options.SchemaName}.module_schema_migrations (migration_id, content_sha256) VALUES (@migration_id, @content_sha256)",
                connection,
                transaction))
            {
                record.Parameters.AddWithValue("migration_id", migrationId);
                record.Parameters.AddWithValue("content_sha256", migrationSha256);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<IdentityBindingV1> BindAsync(
        CreateBindingCommand command,
        CancellationToken cancellationToken = default)
    {
        BindingValidation.ValidateCreate(command);
        var requestSha256 = BindingRequestHash.ForCreate(command);
        var preparation = await TryResolveExistingBindStateAsync(command, requestSha256, cancellationToken);
        if (preparation is null)
        {
            var (device, account) = await BindingProviderTruthResolver.ReadAsync(
                _deviceProvider,
                _accountProvider,
                command,
                cancellationToken);
            preparation = await PrepareAttemptAsync(
                command,
                requestSha256,
                device.CapabilityRevision,
                account.AuthorizationRevision,
                cancellationToken);
        }
        if (preparation.ExistingResult is not null) return preparation.ExistingResult;
        var attempt = preparation.Attempt
            ?? throw new InvalidOperationException("The binding attempt was not prepared.");

        if (preparation.Created)
        {
            await InjectAsync(BindingMutationStage.PendingAttemptWritten, cancellationToken);
        }

        var provisional = BindingValidation.CreateResult(
            attempt.SoulId,
            attempt.DeviceBindingId,
            attempt.PlatformAccountId,
            attempt.DeviceId,
            1,
            "active",
            attempt.DeviceRegistrationRevision,
            attempt.AccountAuthorizationRevision,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt);

        try
        {
            await _deviceProvider.ReserveAsync(
                command,
                attempt.DeviceRegistrationRevision,
                attempt.ReservationId,
                cancellationToken);
            await _accountProvider.ReserveAsync(
                command,
                attempt.AccountAuthorizationRevision,
                attempt.ReservationId,
                cancellationToken);
        }
        catch
        {
            await CompensateAttemptAsync(attempt, provisional, command.TraceId, command.OccurredAt);
            throw;
        }

        await InjectAsync(BindingMutationStage.ProvidersReserved, cancellationToken);
        try
        {
            await _deviceProvider.ConfirmAsync(
                provisional,
                attempt.ReservationId,
                command.TraceId,
                command.OccurredAt,
                cancellationToken);
            await _accountProvider.ConfirmAsync(
                provisional,
                attempt.ReservationId,
                command.TraceId,
                command.OccurredAt,
                cancellationToken);
        }
        catch
        {
            await CompensateAttemptAsync(attempt, provisional, command.TraceId, command.OccurredAt);
            throw;
        }

        await InjectAsync(BindingMutationStage.ProvidersConfirmed, cancellationToken);
        return await CommitBindingAttemptAsync(command, requestSha256, attempt, cancellationToken);
    }

    public async Task<IdentityBindingV1> RevokeAsync(
        RevokeBindingCommand command,
        CancellationToken cancellationToken = default)
    {
        BindingValidation.ValidateRevoke(command);
        var requestSha256 = BindingRequestHash.ForRevoke(command);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "binding:idempotency:" + command.IdempotencyKey, cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            var duplicate = await ResolveReceiptAsync(
                connection,
                transaction,
                receipt,
                command.IdempotencyKey,
                RevokeOperation,
                requestSha256,
                cancellationToken);
            var duplicateReservationId = await ReadReservationIdAsync(
                duplicate.DeviceBindingId,
                cancellationToken);
            await ReleaseProviderReservationsAsync(
                duplicate,
                duplicateReservationId,
                command.TraceId,
                command.OccurredAt,
                cancellationToken);
            return duplicate;
        }

        await AcquireLockAsync(connection, transaction, "binding:id:" + command.DeviceBindingId, cancellationToken);
        var current = await ReadBindingByIdAsync(
            connection,
            transaction,
            command.DeviceBindingId,
            forUpdate: true,
            cancellationToken) ?? throw new KeyNotFoundException("Unknown binding.");
        EnsureScope(current, command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        if (!string.Equals(current.Status, "active", StringComparison.Ordinal))
            throw new InvalidOperationException("The binding is not active and cannot be reactivated.");
        if (current.BindingRevision != command.ExpectedRevision)
            throw new InvalidOperationException("The binding revision is stale.");

        var result = BindingValidation.CreateResult(
            current.SoulId,
            current.DeviceBindingId,
            current.PlatformAccountId,
            current.DeviceId,
            current.BindingRevision + 1,
            "revoked",
            current.DeviceRegistrationRevision,
            current.AccountAuthorizationRevision,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt);
        await UpdateBindingAsync(connection, transaction, current.BindingRevision, result, cancellationToken);
        await InjectAsync(BindingMutationStage.BindingWritten, cancellationToken);
        await InsertRevisionAsync(connection, transaction, result, cancellationToken);
        await InjectAsync(BindingMutationStage.RevisionWritten, cancellationToken);
        await InsertReceiptAsync(connection, transaction, RevokeOperation, requestSha256, result, cancellationToken);
        await InjectAsync(BindingMutationStage.IdempotencyReceiptWritten, cancellationToken);
        await InsertOutboxAsync(connection, transaction, result, cancellationToken);
        await InjectAsync(BindingMutationStage.OutboxWritten, cancellationToken);
        await InjectAsync(BindingMutationStage.BeforeCommit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var reservationId = await ReadReservationIdAsync(result.DeviceBindingId, cancellationToken);
        await ReleaseProviderReservationsAsync(
            result,
            reservationId,
            command.TraceId,
            command.OccurredAt,
            cancellationToken);
        return result;
    }

    public async Task<IdentityBindingV1> GetAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        BindingValidation.ValidateScope(soulId, deviceBindingId, platformAccountId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var result = await ReadBindingByIdAsync(
            connection,
            transaction: null,
            deviceBindingId,
            forUpdate: false,
            cancellationToken) ?? throw new KeyNotFoundException("Unknown binding.");
        EnsureScope(result, soulId, deviceBindingId, platformAccountId);
        return result;
    }

    public Task<long> CountBindingsAsync(CancellationToken cancellationToken = default)
        => CountAsync("bindings", cancellationToken);

    public Task<long> CountRevisionsAsync(CancellationToken cancellationToken = default)
        => CountAsync("binding_revisions", cancellationToken);

    public Task<long> CountReceiptsAsync(CancellationToken cancellationToken = default)
        => CountAsync("idempotency_receipts", cancellationToken);

    public Task<long> CountOutboxAsync(CancellationToken cancellationToken = default)
        => CountAsync("outbox", cancellationToken);

    public Task<long> CountQuarantineAsync(CancellationToken cancellationToken = default)
        => CountAsync("idempotency_quarantine", cancellationToken);

    public async Task<IReadOnlyList<BindingOutboxRecord>> ReadPendingOutboxAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        BindingValidation.ValidateScope(soulId, deviceBindingId, platformAccountId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT outbox_id, payload_canonical, payload_sha256, dispatched_at
            FROM {_options.SchemaName}.outbox
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
              AND dispatched_at IS NULL
            ORDER BY created_at, outbox_id
            """,
            connection);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);

        var records = new List<BindingOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var json = reader.GetString(1);
            var payloadSha256 = reader.GetString(2);
            if (!BindingRequestHash.FixedTimeEquals(payloadSha256, ComputePayloadSha256(json)))
                throw new InvalidOperationException("A binding outbox payload checksum is invalid.");
            var payload = DeserializeResult(json);
            EnsureScope(payload, soulId, deviceBindingId, platformAccountId);
            records.Add(new BindingOutboxRecord(
                reader.GetGuid(0),
                payload,
                payloadSha256,
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime()));
        }
        return records;
    }

    public async Task<IReadOnlyList<BindingIdempotencyQuarantineRecord>> ReadQuarantineAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        BindingValidation.ValidateScope(soulId, deviceBindingId, platformAccountId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT idempotency_key_sha256, incoming_operation, existing_request_sha256,
                   incoming_request_sha256, reason
            FROM {_options.SchemaName}.idempotency_quarantine
            WHERE soul_id = @soul_id
              AND device_binding_id = @device_binding_id
              AND platform_account_id = @platform_account_id
            ORDER BY quarantine_id
            """,
            connection);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        var records = new List<BindingIdempotencyQuarantineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new BindingIdempotencyQuarantineRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return records;
    }

    private async Task<AttemptPreparation?> TryResolveExistingBindStateAsync(
        CreateBindingCommand command,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "binding:idempotency:" + command.IdempotencyKey, cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            var existing = await ResolveReceiptAsync(
                connection,
                transaction,
                receipt,
                command.IdempotencyKey,
                BindOperation,
                requestSha256,
                cancellationToken);
            return new AttemptPreparation(null, existing, false);
        }

        var attempt = await ReadAttemptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (attempt is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (!BindingRequestHash.FixedTimeEquals(attempt.RequestSha256, requestSha256))
        {
            await InsertQuarantineAsync(
                connection,
                transaction,
                attempt.SoulId,
                attempt.DeviceBindingId,
                attempt.PlatformAccountId,
                command.IdempotencyKey,
                BindOperation,
                attempt.RequestSha256,
                requestSha256,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new BindingIdempotencyConflictException();
        }
        EnsureAttemptScope(attempt, command);
        if (!string.Equals(attempt.State, "pending", StringComparison.Ordinal))
            throw new InvalidOperationException("The binding attempt is no longer recoverable with this idempotency key.");
        await transaction.CommitAsync(cancellationToken);
        return new AttemptPreparation(attempt, null, false);
    }

    private async Task<AttemptPreparation> PrepareAttemptAsync(
        CreateBindingCommand command,
        string requestSha256,
        long deviceRegistrationRevision,
        long accountAuthorizationRevision,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "binding:idempotency:" + command.IdempotencyKey, cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            var existing = await ResolveReceiptAsync(
                connection,
                transaction,
                receipt,
                command.IdempotencyKey,
                BindOperation,
                requestSha256,
                cancellationToken);
            return new AttemptPreparation(null, existing, false);
        }

        var prior = await ReadAttemptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (prior is not null)
        {
            if (!BindingRequestHash.FixedTimeEquals(prior.RequestSha256, requestSha256))
            {
                await InsertQuarantineAsync(
                    connection,
                    transaction,
                    prior.SoulId,
                    prior.DeviceBindingId,
                    prior.PlatformAccountId,
                    command.IdempotencyKey,
                    BindOperation,
                    prior.RequestSha256,
                    requestSha256,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw new BindingIdempotencyConflictException();
            }
            EnsureAttemptScope(prior, command);
            if (!string.Equals(prior.State, "pending", StringComparison.Ordinal))
                throw new InvalidOperationException("The binding attempt is no longer recoverable with this idempotency key.");
            await transaction.CommitAsync(cancellationToken);
            return new AttemptPreparation(prior, null, false);
        }

        await AcquireBindingResourceLocksAsync(connection, transaction, command, cancellationToken);
        await EnsureBindingResourcesAvailableAsync(connection, transaction, command, cancellationToken);
        var attempt = new BindingAttempt(
            command.IdempotencyKey,
            requestSha256,
            BindingRequestHash.CreateReservationId(requestSha256),
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.DeviceId,
            deviceRegistrationRevision,
            accountAuthorizationRevision,
            command.TraceId,
            command.OccurredAt,
            "pending");
        await InsertAttemptAsync(connection, transaction, attempt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AttemptPreparation(attempt, null, true);
    }

    private async Task<IdentityBindingV1> CommitBindingAttemptAsync(
        CreateBindingCommand command,
        string requestSha256,
        BindingAttempt expectedAttempt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "binding:idempotency:" + command.IdempotencyKey, cancellationToken);
        var receipt = await ReadReceiptAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            return await ResolveReceiptAsync(
                connection,
                transaction,
                receipt,
                command.IdempotencyKey,
                BindOperation,
                requestSha256,
                cancellationToken);
        }

        var attempt = await ReadAttemptAsync(connection, transaction, command.IdempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException("The pending binding attempt is missing.");
        EnsureAttemptScope(attempt, command);
        if (!BindingRequestHash.FixedTimeEquals(attempt.RequestSha256, requestSha256) ||
            !string.Equals(attempt.ReservationId, expectedAttempt.ReservationId, StringComparison.Ordinal) ||
            !string.Equals(attempt.State, "pending", StringComparison.Ordinal))
            throw new InvalidOperationException("The pending binding attempt changed before activation.");

        await AcquireBindingResourceLocksAsync(connection, transaction, command, cancellationToken);
        await EnsureBindingResourcesAvailableAsync(connection, transaction, command, cancellationToken);
        var result = BindingValidation.CreateResult(
            attempt.SoulId,
            attempt.DeviceBindingId,
            attempt.PlatformAccountId,
            attempt.DeviceId,
            1,
            "active",
            attempt.DeviceRegistrationRevision,
            attempt.AccountAuthorizationRevision,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt);
        await InsertBindingAsync(connection, transaction, result, attempt.ReservationId, cancellationToken);
        await InjectAsync(BindingMutationStage.BindingWritten, cancellationToken);
        await InsertRevisionAsync(connection, transaction, result, cancellationToken);
        await InjectAsync(BindingMutationStage.RevisionWritten, cancellationToken);
        await InsertReceiptAsync(connection, transaction, BindOperation, requestSha256, result, cancellationToken);
        await InjectAsync(BindingMutationStage.IdempotencyReceiptWritten, cancellationToken);
        await InsertOutboxAsync(connection, transaction, result, cancellationToken);
        await InjectAsync(BindingMutationStage.OutboxWritten, cancellationToken);
        await UpdateAttemptStateAsync(connection, transaction, command.IdempotencyKey, "pending", "committed", cancellationToken);
        await InjectAsync(BindingMutationStage.BeforeCommit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task CompensateAttemptAsync(
        BindingAttempt attempt,
        IdentityBindingV1 provisional,
        string traceId,
        DateTimeOffset occurredAt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var connection = await OpenConnectionAsync(timeout.Token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            await AcquireLockAsync(connection, transaction, "binding:idempotency:" + attempt.IdempotencyKey, timeout.Token);
            if (await ReadReceiptAsync(connection, transaction, attempt.IdempotencyKey, timeout.Token) is not null)
            {
                await transaction.CommitAsync(timeout.Token);
                return;
            }
            var current = await ReadAttemptAsync(connection, transaction, attempt.IdempotencyKey, timeout.Token);
            if (current is null || !string.Equals(current.State, "pending", StringComparison.Ordinal))
            {
                await transaction.CommitAsync(timeout.Token);
                return;
            }

            var deviceReleased = await TryReleaseDeviceReservationAsync(
                provisional,
                attempt.ReservationId,
                traceId,
                occurredAt,
                timeout.Token);
            var accountReleased = await TryReleaseAccountReservationAsync(
                provisional,
                attempt.ReservationId,
                traceId,
                occurredAt,
                timeout.Token);
            if (!deviceReleased || !accountReleased) return;
            await UpdateAttemptStateAsync(connection, transaction, attempt.IdempotencyKey, "pending", "compensated", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }
        catch
        {
            // A failed compensation intentionally leaves the attempt pending so a same-request retry can recover it.
        }
    }

    private async Task ReleaseProviderReservationsAsync(
        IdentityBindingV1 binding,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await _deviceProvider.ReleaseAsync(binding, reservationId, traceId, occurredAt, cancellationToken);
        await _accountProvider.ReleaseAsync(binding, reservationId, traceId, occurredAt, cancellationToken);
    }

    private async Task<bool> TryReleaseDeviceReservationAsync(
        IdentityBindingV1 binding,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _deviceProvider.ReleaseAsync(binding, reservationId, traceId, occurredAt, cancellationToken);
            return true;
        }
        catch (KeyNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryReleaseAccountReservationAsync(
        IdentityBindingV1 binding,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _accountProvider.ReleaseAsync(binding, reservationId, traceId, occurredAt, cancellationToken);
            return true;
        }
        catch (KeyNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task AcquireBindingResourceLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateBindingCommand command,
        CancellationToken cancellationToken)
    {
        await AcquireLockAsync(connection, transaction, "binding:id:" + command.DeviceBindingId, cancellationToken);
        await AcquireLockAsync(connection, transaction, "binding:device:" + command.DeviceId, cancellationToken);
        await AcquireLockAsync(connection, transaction, "binding:account:" + command.PlatformAccountId, cancellationToken);
    }

    private async Task EnsureBindingResourcesAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateBindingCommand command,
        CancellationToken cancellationToken)
    {
        if (await ReadBindingByIdAsync(connection, transaction, command.DeviceBindingId, forUpdate: true, cancellationToken) is not null)
            throw new InvalidOperationException("The binding identifier cannot be reused or reactivated.");
        if (await ActiveBindingExistsAsync(connection, transaction, "device_id", command.DeviceId, cancellationToken))
            throw new InvalidOperationException("The device already has an active binding.");
        if (await ActiveBindingExistsAsync(connection, transaction, "platform_account_id", command.PlatformAccountId, cancellationToken))
            throw new InvalidOperationException("The platform account already has an active binding.");
    }

    private async Task<BindingAttempt?> ReadAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT request_sha256, reservation_id, soul_id, device_binding_id, platform_account_id, device_id, device_registration_revision, account_authorization_revision, trace_id, occurred_at, state FROM {_options.SchemaName}.binding_attempts WHERE idempotency_key = @idempotency_key FOR UPDATE",
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new BindingAttempt(
            idempotencyKey,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9).ToUniversalTime(),
            reader.GetString(10));
    }

    private async Task InsertAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BindingAttempt attempt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_options.SchemaName}.binding_attempts (idempotency_key, request_sha256, reservation_id, soul_id, device_binding_id, platform_account_id, device_id, device_registration_revision, account_authorization_revision, trace_id, occurred_at, state) VALUES (@idempotency_key, @request_sha256, @reservation_id, @soul_id, @device_binding_id, @platform_account_id, @device_id, @device_registration_revision, @account_authorization_revision, @trace_id, @occurred_at, @state)",
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", attempt.IdempotencyKey);
        command.Parameters.AddWithValue("request_sha256", attempt.RequestSha256);
        command.Parameters.AddWithValue("reservation_id", attempt.ReservationId);
        command.Parameters.AddWithValue("soul_id", attempt.SoulId);
        command.Parameters.AddWithValue("device_binding_id", attempt.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", attempt.PlatformAccountId);
        command.Parameters.AddWithValue("device_id", attempt.DeviceId);
        command.Parameters.AddWithValue("device_registration_revision", attempt.DeviceRegistrationRevision);
        command.Parameters.AddWithValue("account_authorization_revision", attempt.AccountAuthorizationRevision);
        command.Parameters.AddWithValue("trace_id", attempt.TraceId);
        command.Parameters.AddWithValue("occurred_at", attempt.OccurredAt);
        command.Parameters.AddWithValue("state", attempt.State);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task RecoverPendingAttemptsAsync(CancellationToken cancellationToken = default)
    {
        var attempts = await ReadPendingAttemptsAsync(cancellationToken);
        foreach (var attempt in attempts)
        {
            var command = new CreateBindingCommand(
                attempt.SoulId,
                attempt.DeviceBindingId,
                attempt.PlatformAccountId,
                attempt.DeviceId,
                attempt.TraceId,
                attempt.IdempotencyKey,
                attempt.OccurredAt);
            try
            {
                await BindAsync(command, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Pending binding attempt '{attempt.IdempotencyKey}' could not be recovered; binding startup fails closed.",
                    exception);
            }
        }
    }

    private async Task<IReadOnlyList<BindingAttempt>> ReadPendingAttemptsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT idempotency_key, request_sha256, reservation_id, soul_id, device_binding_id, platform_account_id, device_id, device_registration_revision, account_authorization_revision, trace_id, occurred_at, state FROM {_options.SchemaName}.binding_attempts WHERE state = 'pending' ORDER BY created_at, idempotency_key",
            connection);
        var attempts = new List<BindingAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(new BindingAttempt(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10).ToUniversalTime(),
                reader.GetString(11)));
        }
        return attempts;
    }

    private async Task UpdateAttemptStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        string expectedState,
        string targetState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {_options.SchemaName}.binding_attempts SET state = @target_state, updated_at = clock_timestamp() WHERE idempotency_key = @idempotency_key AND state = @expected_state",
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("expected_state", expectedState);
        command.Parameters.AddWithValue("target_state", targetState);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The binding-attempt state changed concurrently.");
    }

    private async Task<string> ReadReservationIdAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT reservation_id FROM {_options.SchemaName}.bindings WHERE device_binding_id = @device_binding_id",
            connection);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The binding has no provider reservation identity.");
    }

    private static void EnsureAttemptScope(BindingAttempt attempt, CreateBindingCommand command)
    {
        if (!string.Equals(attempt.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(attempt.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(attempt.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(attempt.DeviceId, command.DeviceId, StringComparison.Ordinal))
            throw new KeyNotFoundException("No binding attempt exists in the requested scope.");
    }

    private async Task<IdentityBindingV1> ResolveReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReceiptRow receipt,
        string idempotencyKey,
        string incomingOperation,
        string incomingRequestSha256,
        CancellationToken cancellationToken)
    {
        if (string.Equals(receipt.Operation, incomingOperation, StringComparison.Ordinal) &&
            BindingRequestHash.FixedTimeEquals(receipt.RequestSha256, incomingRequestSha256))
        {
            if (string.Equals(receipt.Operation, BindOperation, StringComparison.Ordinal))
            {
                var current = await ReadBindingByIdAsync(
                    connection,
                    transaction,
                    receipt.Result.DeviceBindingId,
                    forUpdate: false,
                    cancellationToken);
                if (current is null ||
                    !string.Equals(current.Status, "active", StringComparison.Ordinal) ||
                    current.BindingRevision != receipt.Result.BindingRevision)
                {
                    throw new BindingHistoricalReceiptException();
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return receipt.Result;
        }

        await InsertQuarantineAsync(
            connection,
            transaction,
            receipt.Result.SoulId,
            receipt.Result.DeviceBindingId,
            receipt.Result.PlatformAccountId,
            idempotencyKey,
            incomingOperation,
            receipt.RequestSha256,
            incomingRequestSha256,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        throw new BindingIdempotencyConflictException();
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

    private async Task<NpgsqlConnection> OpenFenceConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(_options.ConnectionString)
        {
            Pooling = false,
            CommandTimeout = 5
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
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

    private static async Task ForceCloseFenceConnectionAsync(NpgsqlConnection connection)
    {
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // The connection is non-pooled. DisposeAsync has initiated physical session close;
            // preserve the acquisition error instead of replacing it with cleanup failure.
        }
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lockName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_name, 912731))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_name", lockName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireSessionLockAsync(
        NpgsqlConnection connection,
        string lockName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@lock_name, 912731))",
            connection);
        command.Parameters.AddWithValue("lock_name", lockName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask ReleaseMutationFenceAsync(
        NpgsqlConnection connection,
        string fenceId,
        string lockName)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cleanup = ReleaseMutationFenceCoreAsync(connection, fenceId, lockName, deadline.Token);
        try
        {
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // The hard wall-clock boundary has elapsed or cleanup failed. DisposeAsync is
            // deliberately initiated again without awaiting it so a stuck driver operation
            // cannot extend the consumer-visible release beyond the five-second boundary.
            ObserveBackgroundClose(connection.DisposeAsync().AsTask());
        }
    }

    private async Task ReleaseMutationFenceCoreAsync(
        NpgsqlConnection connection,
        string fenceId,
        string lockName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var update = new NpgsqlCommand(
                $"UPDATE {_options.SchemaName}.binding_mutation_fences SET released_at = clock_timestamp() WHERE fence_id = @fence_id AND released_at IS NULL",
                connection))
            {
                update.CommandTimeout = 5;
                update.Parameters.AddWithValue("fence_id", fenceId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var unlock = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@lock_name, 912731))",
                connection);
            unlock.CommandTimeout = 5;
            unlock.Parameters.AddWithValue("lock_name", lockName);
            await unlock.ExecuteScalarAsync(cancellationToken);
        }
        catch
        {
            // Release is deliberately non-throwing after the consumer commit. The non-pooled
            // connection is still physically closed below, which is the authoritative unlock.
        }
        finally
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // A release failure is observable as an audit row without released_at, never as
                // an exception that masks the consumer's already committed transaction.
            }
        }
    }

    private static void ObserveBackgroundClose(Task closeTask)
        => _ = closeTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task<IdentityBindingV1?> ReadBindingByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string deviceBindingId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockClause = forUpdate ? " FOR UPDATE" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"""
            SELECT soul_id, device_binding_id, platform_account_id, trace_id, idempotency_key,
                   occurred_at, device_id, binding_revision, status,
                   device_registration_revision, account_authorization_revision
            FROM {_options.SchemaName}.bindings
            WHERE device_binding_id = @device_binding_id{lockClause}
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return BindingValidation.CreateResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5).ToUniversalTime());
    }

    private async Task<bool> ActiveBindingExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        if (column is not ("device_id" or "platform_account_id"))
            throw new ArgumentOutOfRangeException(nameof(column));
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_options.SchemaName}.bindings WHERE {column} = @value AND status = 'active')",
            connection,
            transaction);
        command.Parameters.AddWithValue("value", value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The binding uniqueness query returned no result."));
    }

    private async Task<ReceiptRow?> ReadReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT operation, request_sha256, result_canonical
            FROM {_options.SchemaName}.idempotency_receipts
            WHERE idempotency_key = @idempotency_key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReceiptRow(reader.GetString(0), reader.GetString(1), DeserializeResult(reader.GetString(2)));
    }

    private async Task InsertBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IdentityBindingV1 result,
        string reservationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.bindings
                (device_binding_id, soul_id, platform_account_id, device_id, reservation_id, binding_revision,
                 status, device_registration_revision, account_authorization_revision,
                 trace_id, idempotency_key, occurred_at, updated_at)
            VALUES
                (@device_binding_id, @soul_id, @platform_account_id, @device_id, @reservation_id, @binding_revision,
                 @status, @device_registration_revision, @account_authorization_revision,
                 @trace_id, @idempotency_key, @occurred_at, @occurred_at)
            """,
            connection,
            transaction);
        AddResultParameters(command, result);
        command.Parameters.AddWithValue("reservation_id", reservationId);
        await ExecuteMutationAsync(command, "The device or platform account already has an active binding.", cancellationToken);
    }

    private async Task UpdateBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long expectedRevision,
        IdentityBindingV1 result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_options.SchemaName}.bindings
            SET binding_revision = @binding_revision,
                status = @status,
                trace_id = @trace_id,
                idempotency_key = @idempotency_key,
                occurred_at = @occurred_at,
                updated_at = clock_timestamp()
            WHERE device_binding_id = @device_binding_id
              AND soul_id = @soul_id
              AND platform_account_id = @platform_account_id
              AND binding_revision = @expected_revision
              AND status = 'active'
            """,
            connection,
            transaction);
        AddResultParameters(command, result);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The binding revision changed before revocation committed.");
    }

    private async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IdentityBindingV1 result,
        CancellationToken cancellationToken)
    {
        var payloadJson = SerializeResult(result);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.binding_revisions
                (device_binding_id, binding_revision, soul_id, platform_account_id, device_id,
                 status, device_registration_revision, account_authorization_revision,
                 trace_id, idempotency_key, occurred_at, payload_sha256, payload_canonical, payload_json)
            VALUES
                (@device_binding_id, @binding_revision, @soul_id, @platform_account_id, @device_id,
                 @status, @device_registration_revision, @account_authorization_revision,
                 @trace_id, @idempotency_key, @occurred_at, @payload_sha256, @payload_canonical, @payload_json)
            """,
            connection,
            transaction);
        AddResultParameters(command, result);
        command.Parameters.AddWithValue("payload_sha256", ComputePayloadSha256(payloadJson));
        command.Parameters.AddWithValue("payload_canonical", payloadJson);
        command.Parameters.Add("payload_json", NpgsqlDbType.Jsonb).Value = payloadJson;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string operation,
        string requestSha256,
        IdentityBindingV1 result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.idempotency_receipts
                (idempotency_key, operation, request_sha256, device_binding_id,
                 binding_revision, result_canonical, result_json)
            VALUES
                (@idempotency_key, @operation, @request_sha256, @device_binding_id,
                 @binding_revision, @result_canonical, @result_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("request_sha256", requestSha256);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("binding_revision", result.BindingRevision);
        var resultJson = SerializeResult(result);
        command.Parameters.AddWithValue("result_canonical", resultJson);
        command.Parameters.Add("result_json", NpgsqlDbType.Jsonb).Value = resultJson;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IdentityBindingV1 result,
        CancellationToken cancellationToken)
    {
        var payloadJson = SerializeResult(result);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.outbox
                (outbox_id, idempotency_key, device_binding_id, binding_revision,
                 soul_id, platform_account_id, trace_id, topic, payload_sha256, payload_canonical, payload_json)
            VALUES
                (@outbox_id, @idempotency_key, @device_binding_id, @binding_revision,
                 @soul_id, @platform_account_id, @trace_id, @topic, @payload_sha256, @payload_canonical, @payload_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("outbox_id", Guid.NewGuid());
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("binding_revision", result.BindingRevision);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("topic", result.ContractId);
        command.Parameters.AddWithValue("payload_sha256", ComputePayloadSha256(payloadJson));
        command.Parameters.AddWithValue("payload_canonical", payloadJson);
        command.Parameters.Add("payload_json", NpgsqlDbType.Jsonb).Value = payloadJson;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string idempotencyKey,
        string incomingOperation,
        string existingRequestSha256,
        string incomingRequestSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.idempotency_quarantine
                (soul_id, device_binding_id, platform_account_id, idempotency_key_sha256,
                 incoming_operation, existing_request_sha256,
                 incoming_request_sha256, reason)
            VALUES
                (@soul_id, @device_binding_id, @platform_account_id, @idempotency_key_sha256,
                 @incoming_operation, @existing_request_sha256,
                 @incoming_request_sha256, @reason)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        command.Parameters.AddWithValue("idempotency_key_sha256", ComputePayloadSha256(idempotencyKey));
        command.Parameters.AddWithValue("incoming_operation", incomingOperation);
        command.Parameters.AddWithValue("existing_request_sha256", existingRequestSha256);
        command.Parameters.AddWithValue("incoming_request_sha256", incomingRequestSha256);
        command.Parameters.AddWithValue("reason", "idempotency-key-reused-with-different-operation-or-payload");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
    {
        if (tableName is not ("bindings" or "binding_revisions" or "idempotency_receipts" or "outbox" or "idempotency_quarantine"))
            throw new ArgumentOutOfRangeException(nameof(tableName));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {_options.SchemaName}.{tableName}",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddResultParameters(NpgsqlCommand command, IdentityBindingV1 result)
    {
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("device_id", result.DeviceId);
        command.Parameters.AddWithValue("binding_revision", result.BindingRevision);
        command.Parameters.AddWithValue("status", result.Status);
        command.Parameters.AddWithValue("device_registration_revision", result.DeviceRegistrationRevision);
        command.Parameters.AddWithValue("account_authorization_revision", result.AccountAuthorizationRevision);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", result.OccurredAt);
    }

    private static async Task ExecuteMutationAsync(
        NpgsqlCommand command,
        string uniquenessMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(uniquenessMessage, exception);
        }
    }

    private ValueTask InjectAsync(BindingMutationStage stage, CancellationToken cancellationToken)
        => _faultInjector is null ? ValueTask.CompletedTask : _faultInjector(stage, cancellationToken);

    private static void EnsureScope(
        IdentityBindingV1 result,
        string soulId,
        string deviceBindingId,
        string platformAccountId)
    {
        if (!string.Equals(result.SoulId, soulId, StringComparison.Ordinal) ||
            !string.Equals(result.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(result.PlatformAccountId, platformAccountId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("No binding exists in the requested scope.");
        }
    }

    private static string SerializeResult(IdentityBindingV1 result)
        => JsonSerializer.Serialize(result, SerializerOptions);

    private static IdentityBindingV1 DeserializeResult(string json)
        => BindingContractJson.DeserializeStrict<IdentityBindingV1>(json);

    private static string ComputePayloadSha256(string payload)
        => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(payload)));

    private sealed record ReceiptRow(string Operation, string RequestSha256, IdentityBindingV1 Result);

    private sealed record BindingAttempt(
        string IdempotencyKey,
        string RequestSha256,
        string ReservationId,
        string SoulId,
        string DeviceBindingId,
        string PlatformAccountId,
        string DeviceId,
        long DeviceRegistrationRevision,
        long AccountAuthorizationRevision,
        string TraceId,
        DateTimeOffset OccurredAt,
        string State);

    private sealed record AttemptPreparation(
        BindingAttempt? Attempt,
        IdentityBindingV1? ExistingResult,
        bool Created);

    private sealed class TrustedDeviceRegistryProvider : IDeviceBindingReservationProvider
    {
        private readonly IDeviceBindingReservationClient _provider;

        public TrustedDeviceRegistryProvider(IDeviceBindingReservationClient provider)
            => _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        public Task<DeviceRegisteredV1> ReadCurrentAsync(
            string deviceId,
            string soulId,
            string deviceBindingId,
            string platformAccountId,
            CancellationToken cancellationToken = default)
            => _provider.ReadCurrentAsync(deviceId, soulId, deviceBindingId, platformAccountId, cancellationToken);

        public async Task<DeviceBindingReservationV1> ReserveAsync(
            CreateBindingCommand command,
            long expectedRevision,
            string reservationId,
            CancellationToken cancellationToken = default)
        {
            var receipt = await BindingProviderCommandDeadline.ExecuteAsync(
                token => _provider.ReserveAsync(new ReserveDeviceBindingCommand(
                    command.SoulId,
                    command.DeviceBindingId,
                    command.PlatformAccountId,
                    command.DeviceId,
                    expectedRevision,
                    reservationId,
                    command.TraceId,
                    command.OccurredAt), token),
                cancellationToken);
            BindingProviderReservationReceiptValidation.EnsureDevice(
                receipt,
                BindingValidation.CreateResult(
                    command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.DeviceId,
                    1, "active", expectedRevision, 1, command.TraceId, command.IdempotencyKey, command.OccurredAt),
                reservationId,
                command.TraceId,
                command.OccurredAt,
                "held",
                DateTimeOffset.UtcNow,
                allowActiveRecovery: true);
            return receipt;
        }

        public async Task<DeviceBindingReservationV1> ConfirmAsync(
            IdentityBindingV1 binding,
            string reservationId,
            string traceId,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            var receipt = await BindingProviderCommandDeadline.ExecuteAsync(
                token => _provider.ConfirmAsync(new DeviceBindingReservationCommand(
                    binding.SoulId,
                    binding.DeviceBindingId,
                    binding.PlatformAccountId,
                    binding.DeviceId,
                    binding.DeviceRegistrationRevision,
                    reservationId,
                    traceId,
                    occurredAt), token),
                cancellationToken);
            BindingProviderReservationReceiptValidation.EnsureDevice(
                receipt, binding, reservationId, traceId, occurredAt, "active", DateTimeOffset.UtcNow);
            return receipt;
        }

        public async Task<DeviceBindingReservationV1> ReleaseAsync(
            IdentityBindingV1 binding,
            string reservationId,
            string traceId,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            var receipt = await BindingProviderCommandDeadline.ExecuteAsync(
                token => _provider.ReleaseAsync(new DeviceBindingReservationCommand(
                    binding.SoulId,
                    binding.DeviceBindingId,
                    binding.PlatformAccountId,
                    binding.DeviceId,
                    binding.DeviceRegistrationRevision,
                    reservationId,
                    traceId,
                    occurredAt), token),
                cancellationToken);
            BindingProviderReservationReceiptValidation.EnsureDevice(
                receipt, binding, reservationId, traceId, occurredAt, "released", DateTimeOffset.UtcNow);
            return receipt;
        }
    }

    private sealed class TrustedPlatformAccountRegistryProvider : IPlatformAccountBindingReservationProvider
    {
        private readonly IPlatformAccountBindingReservationClient _provider;

        public TrustedPlatformAccountRegistryProvider(IPlatformAccountBindingReservationClient provider)
            => _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        public async Task<PlatformAccountAuthorizedV1> ReadCurrentAsync(
            string platformAccountId,
            string soulId,
            string deviceBindingId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _provider.ReadCurrentAsync(platformAccountId, soulId, deviceBindingId, cancellationToken);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new KeyNotFoundException(
                    "No platform-account authorization exists in the requested binding scope.",
                    exception);
            }
        }

        public async Task<PlatformAccountBindingReservationV1> ReserveAsync(
            CreateBindingCommand command,
            long expectedRevision,
            string reservationId,
            CancellationToken cancellationToken = default)
        {
            var receipt = await BindingProviderCommandDeadline.ExecuteAsync(
                token => _provider.ReserveAsync(new ReservePlatformAccountBindingCommand(
                    command.SoulId,
                    command.DeviceBindingId,
                    command.PlatformAccountId,
                    expectedRevision,
                    reservationId,
                    command.TraceId,
                    command.OccurredAt), token),
                cancellationToken);
            BindingProviderReservationReceiptValidation.EnsureAccount(
                receipt,
                BindingValidation.CreateResult(
                    command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.DeviceId,
                    1, "active", 1, expectedRevision, command.TraceId, command.IdempotencyKey, command.OccurredAt),
                reservationId,
                command.TraceId,
                command.OccurredAt,
                "held",
                DateTimeOffset.UtcNow,
                allowActiveRecovery: true);
            return receipt;
        }

        public async Task<PlatformAccountBindingReservationV1> ConfirmAsync(
            IdentityBindingV1 binding,
            string reservationId,
            string traceId,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            var receipt = await BindingProviderCommandDeadline.ExecuteAsync(
                token => _provider.ConfirmAsync(new PlatformAccountBindingReservationCommand(
                    binding.SoulId,
                    binding.DeviceBindingId,
                    binding.PlatformAccountId,
                    binding.AccountAuthorizationRevision,
                    reservationId,
                    traceId,
                    occurredAt), token),
                cancellationToken);
            BindingProviderReservationReceiptValidation.EnsureAccount(
                receipt, binding, reservationId, traceId, occurredAt, "active", DateTimeOffset.UtcNow);
            return receipt;
        }

        public async Task<PlatformAccountBindingReservationV1> ReleaseAsync(
            IdentityBindingV1 binding,
            string reservationId,
            string traceId,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            var receipt = await BindingProviderCommandDeadline.ExecuteAsync(
                token => _provider.ReleaseAsync(new PlatformAccountBindingReservationCommand(
                    binding.SoulId,
                    binding.DeviceBindingId,
                    binding.PlatformAccountId,
                    binding.AccountAuthorizationRevision,
                    reservationId,
                    traceId,
                    occurredAt), token),
                cancellationToken);
            BindingProviderReservationReceiptValidation.EnsureAccount(
                receipt, binding, reservationId, traceId, occurredAt, "released", DateTimeOffset.UtcNow);
            return receipt;
        }
    }
}

internal sealed class PostgresBindingMutationFenceClient : IBindingMutationFenceClient
{
    private readonly PostgresBindingRegistry _registry;

    internal PostgresBindingMutationFenceClient(PostgresBindingRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public Task<IBindingMutationFenceLease> AcquireAsync(
        AcquireBindingMutationFenceCommand command,
        CancellationToken cancellationToken = default)
        => _registry.AcquireMutationFenceAsync(command, cancellationToken);
}

internal sealed class PostgresBindingMutationFenceLease : IBindingMutationFenceLease
{
    private Func<ValueTask>? _release;

    internal PostgresBindingMutationFenceLease(
        BindingMutationFenceV1 receipt,
        Func<ValueTask> release)
    {
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        Receipt.Validate();
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public BindingMutationFenceV1 Receipt { get; }

    public async ValueTask DisposeAsync()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        try
        {
            await release();
        }
        catch
        {
            // The authoritative production release callback is bounded and physically closes
            // its non-pooled PostgreSQL session. Never mask an already committed consumer write.
        }
    }
}
