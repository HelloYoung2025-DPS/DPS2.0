using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dps.DeviceRegistry.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dps.DeviceRegistry;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgresDeviceRegistryOptions(
    [property: JsonIgnore] string ConnectionString,
    [property: JsonPropertyName("schema_name"), JsonRequired] string SchemaName,
    [property: JsonPropertyName("fingerprint_key_id"), JsonRequired] string FingerprintKeyId,
    [property: JsonPropertyName("fingerprint_key_epoch"), JsonRequired] long FingerprintKeyEpoch,
    [property: JsonPropertyName("trust_epoch"), JsonRequired] long TrustEpoch)
{
    private static readonly Regex SchemaPattern = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

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

        DeviceContractValidation.RequireFingerprintKeyId(FingerprintKeyId);
        DeviceContractValidation.RequireFingerprintKeyEpoch(FingerprintKeyEpoch);
        if (TrustEpoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TrustEpoch), "TrustEpoch must be positive.");
        }
    }

    public override string ToString()
        => $"PostgresDeviceRegistryOptions {{ ConnectionString = [REDACTED], SchemaName = {SchemaName}, FingerprintKeyId = {FingerprintKeyId}, FingerprintKeyEpoch = {FingerprintKeyEpoch}, TrustEpoch = {TrustEpoch} }}";
}

public enum DeviceMutationStage
{
    DeviceWritten,
    CapabilityRevisionWritten,
    IdempotencyReceiptWritten,
    OutboxWritten,
    BeforeCommit
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceOutboxRecord(
    [property: JsonPropertyName("outbox_id"), JsonRequired] Guid OutboxId,
    [property: JsonPropertyName("payload"), JsonRequired] DeviceRegisteredV1 Payload,
    [property: JsonPropertyName("payload_sha256"), JsonRequired] string PayloadSha256,
    [property: JsonPropertyName("dispatched_at"), JsonRequired] DateTimeOffset? DispatchedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceIdempotencyQuarantineRecord(
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("mutation_kind"), JsonRequired] string MutationKind,
    [property: JsonPropertyName("existing_command_sha256"), JsonRequired] string ExistingCommandSha256,
    [property: JsonPropertyName("incoming_command_sha256"), JsonRequired] string IncomingCommandSha256,
    [property: JsonPropertyName("reason"), JsonRequired] string Reason);

public sealed class DeviceIdempotencyConflictException : InvalidOperationException
{
    public DeviceIdempotencyConflictException()
        : base("The idempotency key is bound to a different device mutation; the conflict was quarantined.")
    {
    }
}

public delegate ValueTask DeviceMutationFaultInjector(
    DeviceMutationStage stage,
    CancellationToken cancellationToken);

public sealed partial class PostgresDeviceRegistry
{
    private const string RegistrationMutation = "register";
    private const string CapabilityMutation = "capability-revision";
    private const string RetirementMutation = "retire";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly PostgresDeviceRegistryOptions _options;
    private readonly DeviceMutationFaultInjector? _faultInjector;
    private readonly string _bindingProviderInstanceConfigurationSha256;

    public PostgresDeviceRegistry(
        PostgresDeviceRegistryOptions options,
        DeviceMutationFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _faultInjector = faultInjector;
        _bindingProviderInstanceConfigurationSha256 = DeviceRegistryProviderInstanceIdentity.Compute(options);
    }

    internal string BindingProviderInstanceConfigurationSha256
        => _bindingProviderInstanceConfigurationSha256;

    internal long BindingProviderInstanceTrustEpoch => _options.TrustEpoch;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(PostgresDeviceRegistry).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(static name => name.Contains(".Migrations.", StringComparison.Ordinal) &&
                                  name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resourceNames.Length == 0)
            throw new InvalidOperationException("No embedded device-registry migrations were found.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        foreach (var resourceName in resourceNames)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var migration = await reader.ReadToEndAsync(cancellationToken);
            migration = migration.Replace("__SCHEMA__", _options.SchemaName, StringComparison.Ordinal);
            await using var command = new NpgsqlCommand(migration, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<DeviceRegisteredV1> RegisterAsync(
        RegisterDeviceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        DeviceContractValidation.RequireSha256(command.FingerprintHmacSha256, nameof(command.FingerprintHmacSha256));
        EnsureConfiguredFingerprintKey(command.FingerprintKeyId, command.FingerprintKeyEpoch);
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
        var capabilities = DeviceCapabilityNormalizer.Normalize(command.Capabilities);
        var fingerprint = command.FingerprintHmacSha256;
        var commandFields = new List<string>
        {
            RegistrationMutation,
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.FingerprintKeyId,
            command.FingerprintKeyEpoch.ToString(CultureInfo.InvariantCulture),
            fingerprint
        };
        commandFields.AddRange(capabilities);
        var commandSha256 = ComputeCommandSha256(commandFields.ToArray());

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "idempotency:" + command.IdempotencyKey, cancellationToken);

        var duplicate = await ResolveIdempotencyAsync(
            connection,
            transaction,
            command.IdempotencyKey,
            RegistrationMutation,
            commandSha256,
            cancellationToken);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return duplicate;
        }

        await AcquireLockAsync(
            connection,
            transaction,
            string.Concat("fingerprint:", command.FingerprintKeyId, ":", command.FingerprintKeyEpoch.ToString(CultureInfo.InvariantCulture), ":", fingerprint),
            cancellationToken);
        if (await FingerprintExistsAsync(
                connection,
                transaction,
                command.FingerprintKeyId,
                command.FingerprintKeyEpoch,
                fingerprint,
                cancellationToken))
        {
            throw new InvalidOperationException("The fingerprint digest is already registered.");
        }

        var result = CreateResult(
            CreateDeviceId(),
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            fingerprint,
            command.FingerprintKeyId,
            command.FingerprintKeyEpoch,
            1,
            capabilities,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt,
            "registered");
        var payloadJson = SerializeResult(result);
        var payloadSha256 = ComputeTextSha256(payloadJson);
        var outboxId = Guid.NewGuid();

        await InsertDeviceAsync(connection, transaction, result, cancellationToken);
        await InjectAsync(DeviceMutationStage.DeviceWritten, cancellationToken);
        await InsertCapabilityRevisionAsync(connection, transaction, result, payloadJson, payloadSha256, cancellationToken);
        await InjectAsync(DeviceMutationStage.CapabilityRevisionWritten, cancellationToken);
        await InsertIdempotencyReceiptAsync(
            connection,
            transaction,
            result,
            RegistrationMutation,
            commandSha256,
            outboxId,
            payloadJson,
            cancellationToken);
        await InjectAsync(DeviceMutationStage.IdempotencyReceiptWritten, cancellationToken);
        await InsertOutboxAsync(connection, transaction, result, outboxId, payloadJson, payloadSha256, cancellationToken);
        await InjectAsync(DeviceMutationStage.OutboxWritten, cancellationToken);
        await InjectAsync(DeviceMutationStage.BeforeCommit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<DeviceRegisteredV1> UpdateCapabilitiesAsync(
        UpdateDeviceCapabilitiesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var capabilities = DeviceCapabilityNormalizer.Normalize(command.Capabilities);
        return MutateExistingAsync(
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.DeviceId,
            command.ExpectedRevision,
            capabilities,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt,
            CapabilityMutation,
            "registered",
            cancellationToken);
    }

    public Task<DeviceRegisteredV1> RetireAsync(
        RetireDeviceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateExistingAsync(
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            command.DeviceId,
            command.ExpectedRevision,
            capabilities: null,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt,
            RetirementMutation,
            "retired",
            cancellationToken);
    }

    public async Task<DeviceRegisteredV1> GetAsync(
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(soulId, deviceBindingId, platformAccountId);
        DeviceContractValidation.RequirePrefixedHex(deviceId, "device_", 32, nameof(deviceId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var result = await ReadScopedCurrentAsync(
            connection,
            transaction: null,
            deviceId,
            soulId,
            deviceBindingId,
            platformAccountId,
            forUpdate: false,
            cancellationToken);
        return result ?? throw new KeyNotFoundException("No device exists in the requested operation scope.");
    }

    public async Task<bool> IsRegisteredAsync(
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
        => string.Equals(
            (await GetAsync(deviceId, soulId, deviceBindingId, platformAccountId, cancellationToken)).Status,
            "registered",
            StringComparison.Ordinal);

    public Task<long> CountDevicesAsync(CancellationToken cancellationToken = default)
        => CountAsync("devices", cancellationToken);

    public Task<long> CountCapabilityRevisionsAsync(CancellationToken cancellationToken = default)
        => CountAsync("capability_revisions", cancellationToken);

    public Task<long> CountIdempotencyReceiptsAsync(CancellationToken cancellationToken = default)
        => CountAsync("idempotency_receipts", cancellationToken);

    public Task<long> CountOutboxAsync(CancellationToken cancellationToken = default)
        => CountAsync("outbox", cancellationToken);

    public Task<long> CountQuarantineAsync(CancellationToken cancellationToken = default)
        => CountAsync("idempotency_quarantine", cancellationToken);

    public async Task<IReadOnlyList<DeviceOutboxRecord>> ReadPendingOutboxAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(soulId, deviceBindingId, platformAccountId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT outbox_id, payload_json::text, payload_sha256, dispatched_at
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

        var records = new List<DeviceOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = DeserializeResult(reader.GetString(1));
            EnsureSameScope(payload, soulId, deviceBindingId, platformAccountId);
            records.Add(new DeviceOutboxRecord(
                reader.GetGuid(0),
                payload,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime()));
        }

        return records;
    }

    public async Task<IReadOnlyList<DeviceIdempotencyQuarantineRecord>> ReadQuarantineAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT idempotency_key, mutation_kind, existing_command_sha256, incoming_command_sha256, reason
            FROM {_options.SchemaName}.idempotency_quarantine
            ORDER BY quarantine_id
            """,
            connection);
        var records = new List<DeviceIdempotencyQuarantineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new DeviceIdempotencyQuarantineRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return records;
    }

    private async Task<DeviceRegisteredV1> MutateExistingAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string deviceId,
        long expectedRevision,
        IReadOnlyList<string>? capabilities,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string mutationKind,
        string targetStatus,
        CancellationToken cancellationToken)
    {
        ValidateScope(soulId, deviceBindingId, platformAccountId);
        DeviceContractValidation.RequirePrefixedHex(deviceId, "device_", 32, nameof(deviceId));
        ValidateEnvelope(traceId, idempotencyKey, occurredAt);
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        var commandFields = new List<string>
        {
            mutationKind,
            soulId,
            deviceBindingId,
            platformAccountId,
            deviceId,
            expectedRevision.ToString(CultureInfo.InvariantCulture)
        };
        if (capabilities is not null)
        {
            commandFields.AddRange(capabilities);
        }
        var commandSha256 = ComputeCommandSha256(commandFields.ToArray());

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "idempotency:" + idempotencyKey, cancellationToken);
        var duplicate = await ResolveIdempotencyAsync(
            connection,
            transaction,
            idempotencyKey,
            mutationKind,
            commandSha256,
            cancellationToken);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return duplicate;
        }

        await AcquireLockAsync(connection, transaction, "device:" + deviceId, cancellationToken);
        var current = await ReadScopedCurrentAsync(
            connection,
            transaction,
            deviceId,
            soulId,
            deviceBindingId,
            platformAccountId,
            forUpdate: true,
            cancellationToken)
            ?? throw new KeyNotFoundException("No device exists in the requested operation scope.");
        await EnsureNoEffectiveBindingReservationAsync(
            connection,
            transaction,
            current.DeviceId,
            cancellationToken);
        if (!string.Equals(current.Status, "registered", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A retired device cannot be mutated.");
        }
        if (current.CapabilityRevision != expectedRevision)
        {
            throw new InvalidOperationException("Stale capability revision.");
        }

        var result = CreateResult(
            current.DeviceId,
            current.SoulId,
            current.DeviceBindingId,
            current.PlatformAccountId,
            current.FingerprintHmacSha256,
            current.FingerprintKeyId,
            current.FingerprintKeyEpoch,
            current.CapabilityRevision + 1,
            capabilities ?? current.Capabilities,
            traceId,
            idempotencyKey,
            occurredAt,
            targetStatus);
        var payloadJson = SerializeResult(result);
        var payloadSha256 = ComputeTextSha256(payloadJson);
        var outboxId = Guid.NewGuid();

        await UpdateDeviceAsync(connection, transaction, result, cancellationToken);
        await InjectAsync(DeviceMutationStage.DeviceWritten, cancellationToken);
        await InsertCapabilityRevisionAsync(connection, transaction, result, payloadJson, payloadSha256, cancellationToken);
        await InjectAsync(DeviceMutationStage.CapabilityRevisionWritten, cancellationToken);
        await InsertIdempotencyReceiptAsync(
            connection,
            transaction,
            result,
            mutationKind,
            commandSha256,
            outboxId,
            payloadJson,
            cancellationToken);
        await InjectAsync(DeviceMutationStage.IdempotencyReceiptWritten, cancellationToken);
        await InsertOutboxAsync(connection, transaction, result, outboxId, payloadJson, payloadSha256, cancellationToken);
        await InjectAsync(DeviceMutationStage.OutboxWritten, cancellationToken);
        await InjectAsync(DeviceMutationStage.BeforeCommit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<DeviceRegisteredV1?> ResolveIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        string mutationKind,
        string incomingCommandSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT command_sha256, result_json::text
            FROM {_options.SchemaName}.idempotency_receipts
            WHERE idempotency_key = @idempotency_key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var existingCommandSha256 = reader.GetString(0);
        var resultJson = reader.GetString(1);
        await reader.DisposeAsync();
        if (HashesEqual(existingCommandSha256, incomingCommandSha256))
        {
            return DeserializeResult(resultJson);
        }

        await InsertQuarantineAsync(
            connection,
            transaction,
            idempotencyKey,
            mutationKind,
            existingCommandSha256,
            incomingCommandSha256,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        throw new DeviceIdempotencyConflictException();
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

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lockKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 724119))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_key", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> FingerprintExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string fingerprintKeyId,
        long fingerprintKeyEpoch,
        string fingerprintHmacSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_options.SchemaName}.devices WHERE fingerprint_key_id = @fingerprint_key_id AND fingerprint_key_epoch = @fingerprint_key_epoch AND fingerprint_hmac_sha256 = @fingerprint_hmac_sha256)",
            connection,
            transaction);
        command.Parameters.AddWithValue("fingerprint_key_id", fingerprintKeyId);
        command.Parameters.AddWithValue("fingerprint_key_epoch", fingerprintKeyEpoch);
        command.Parameters.AddWithValue("fingerprint_hmac_sha256", fingerprintHmacSha256);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return a fingerprint existence result."));
    }

    private async Task<DeviceRegisteredV1?> ReadScopedCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockClause = forUpdate ? " FOR UPDATE OF d" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"""
            SELECT r.result_json::text
            FROM {_options.SchemaName}.devices d
            JOIN {_options.SchemaName}.capability_revisions r
              ON r.device_id = d.device_id
             AND r.capability_revision = d.current_revision
            WHERE d.device_id = @device_id
              AND d.registration_soul_id = @soul_id
              AND d.registration_device_binding_id = @device_binding_id
              AND d.registration_platform_account_id = @platform_account_id
            {lockClause}
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("device_id", deviceId);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (json is null)
        {
            return null;
        }

        var result = DeserializeResult(json);
        EnsureSameScope(result, soulId, deviceBindingId, platformAccountId);
        return result;
    }

    private async Task InsertDeviceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DeviceRegisteredV1 result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.devices
                (device_id, fingerprint_hmac_sha256, fingerprint_key_id, fingerprint_key_epoch,
                 registration_soul_id, registration_device_binding_id, registration_platform_account_id,
                 current_revision, status, created_at, updated_at)
            VALUES
                (@device_id, @fingerprint_hmac_sha256, @fingerprint_key_id, @fingerprint_key_epoch,
                 @soul_id, @device_binding_id, @platform_account_id,
                 @current_revision, @status, @occurred_at, @occurred_at)
            """,
            connection,
            transaction);
        AddDeviceParameters(command, result);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateDeviceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DeviceRegisteredV1 result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_options.SchemaName}.devices
            SET current_revision = @current_revision,
                status = @status,
                updated_at = @occurred_at
            WHERE device_id = @device_id
              AND registration_soul_id = @soul_id
              AND registration_device_binding_id = @device_binding_id
              AND registration_platform_account_id = @platform_account_id
            """,
            connection,
            transaction);
        AddDeviceParameters(command, result);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The scoped device row disappeared during mutation.");
        }
    }

    private static void AddDeviceParameters(NpgsqlCommand command, DeviceRegisteredV1 result)
    {
        command.Parameters.AddWithValue("device_id", result.DeviceId);
        command.Parameters.AddWithValue("fingerprint_hmac_sha256", result.FingerprintHmacSha256);
        command.Parameters.AddWithValue("fingerprint_key_id", result.FingerprintKeyId);
        command.Parameters.AddWithValue("fingerprint_key_epoch", result.FingerprintKeyEpoch);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("current_revision", result.CapabilityRevision);
        command.Parameters.AddWithValue("status", result.Status);
        command.Parameters.AddWithValue("occurred_at", result.OccurredAt);
    }

    private async Task InsertCapabilityRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DeviceRegisteredV1 result,
        string payloadJson,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.capability_revisions
                (device_id, capability_revision, soul_id, device_binding_id, platform_account_id,
                 trace_id, idempotency_key, occurred_at, capabilities, status, payload_sha256, result_json)
            VALUES
                (@device_id, @capability_revision, @soul_id, @device_binding_id, @platform_account_id,
                 @trace_id, @idempotency_key, @occurred_at, @capabilities, @status, @payload_sha256, @result_json)
            """,
            connection,
            transaction);
        AddRevisionParameters(command, result, payloadJson, payloadSha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddRevisionParameters(
        NpgsqlCommand command,
        DeviceRegisteredV1 result,
        string payloadJson,
        string payloadSha256)
    {
        command.Parameters.AddWithValue("device_id", result.DeviceId);
        command.Parameters.AddWithValue("capability_revision", result.CapabilityRevision);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", result.OccurredAt);
        command.Parameters.AddWithValue(
            "capabilities",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            result.Capabilities.ToArray());
        command.Parameters.AddWithValue("status", result.Status);
        command.Parameters.AddWithValue("payload_sha256", payloadSha256);
        command.Parameters.AddWithValue("result_json", NpgsqlDbType.Jsonb, payloadJson);
    }

    private async Task InsertIdempotencyReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DeviceRegisteredV1 result,
        string mutationKind,
        string commandSha256,
        Guid outboxId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.idempotency_receipts
                (idempotency_key, command_sha256, mutation_kind, device_id,
                 capability_revision, outbox_id, result_json)
            VALUES
                (@idempotency_key, @command_sha256, @mutation_kind, @device_id,
                 @capability_revision, @outbox_id, @result_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("command_sha256", commandSha256);
        command.Parameters.AddWithValue("mutation_kind", mutationKind);
        command.Parameters.AddWithValue("device_id", result.DeviceId);
        command.Parameters.AddWithValue("capability_revision", result.CapabilityRevision);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("result_json", NpgsqlDbType.Jsonb, payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DeviceRegisteredV1 result,
        Guid outboxId,
        string payloadJson,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.outbox
                (outbox_id, device_id, capability_revision, soul_id, device_binding_id,
                 platform_account_id, trace_id, idempotency_key, occurred_at, topic,
                 payload_sha256, payload_json)
            VALUES
                (@outbox_id, @device_id, @capability_revision, @soul_id, @device_binding_id,
                 @platform_account_id, @trace_id, @idempotency_key, @occurred_at, @topic,
                 @payload_sha256, @payload_json)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("device_id", result.DeviceId);
        command.Parameters.AddWithValue("capability_revision", result.CapabilityRevision);
        command.Parameters.AddWithValue("soul_id", result.SoulId);
        command.Parameters.AddWithValue("device_binding_id", result.DeviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", result.PlatformAccountId);
        command.Parameters.AddWithValue("trace_id", result.TraceId);
        command.Parameters.AddWithValue("idempotency_key", result.IdempotencyKey);
        command.Parameters.AddWithValue("occurred_at", result.OccurredAt);
        command.Parameters.AddWithValue("topic", DeviceRegisteredV1.CurrentContractId);
        command.Parameters.AddWithValue("payload_sha256", payloadSha256);
        command.Parameters.AddWithValue("payload_json", NpgsqlDbType.Jsonb, payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertQuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        string mutationKind,
        string existingCommandSha256,
        string incomingCommandSha256,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_options.SchemaName}.idempotency_quarantine
                (idempotency_key, mutation_kind, existing_command_sha256, incoming_command_sha256, reason)
            VALUES
                (@idempotency_key, @mutation_kind, @existing_command_sha256, @incoming_command_sha256, @reason)
            ON CONFLICT (idempotency_key, incoming_command_sha256) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("mutation_kind", mutationKind);
        command.Parameters.AddWithValue("existing_command_sha256", existingCommandSha256);
        command.Parameters.AddWithValue("incoming_command_sha256", incomingCommandSha256);
        command.Parameters.AddWithValue("reason", "same idempotency key with a different semantic command hash");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
    {
        if (tableName is not ("devices" or "capability_revisions" or "idempotency_receipts" or "outbox" or "idempotency_quarantine"))
        {
            throw new ArgumentOutOfRangeException(nameof(tableName));
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {_options.SchemaName}.{tableName}",
            connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return a row count."));
    }

    private async ValueTask InjectAsync(DeviceMutationStage stage, CancellationToken cancellationToken)
    {
        if (_faultInjector is not null)
        {
            await _faultInjector(stage, cancellationToken);
        }
    }

    private static DeviceRegisteredV1 CreateResult(
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string fingerprintHmacSha256,
        string fingerprintKeyId,
        long fingerprintKeyEpoch,
        long capabilityRevision,
        IReadOnlyList<string> capabilities,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt,
        string status)
    {
        var result = new DeviceRegisteredV1(
            DeviceRegisteredV1.CurrentSchemaVersion,
            DeviceRegisteredV1.CurrentContractId,
            DeviceRegisteredV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            idempotencyKey,
            occurredAt,
            "sensitive",
            deviceId,
            fingerprintHmacSha256,
            fingerprintKeyId,
            fingerprintKeyEpoch,
            capabilityRevision,
            capabilities,
            status);
        result.Validate();
        return result;
    }

    private static void ValidateScope(string soulId, string deviceBindingId, string platformAccountId)
    {
        DeviceContractValidation.RequireSoulId(soulId);
        DeviceContractValidation.RequireDeviceBindingId(deviceBindingId);
        DeviceContractValidation.RequirePlatformAccountId(platformAccountId);
    }

    private static void ValidateEnvelope(string traceId, string idempotencyKey, DateTimeOffset occurredAt)
    {
        DeviceContractValidation.RequireTraceId(traceId);
        DeviceContractValidation.RequireIdempotencyKey(idempotencyKey);
        DeviceContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }

    private void EnsureConfiguredFingerprintKey(string fingerprintKeyId, long fingerprintKeyEpoch)
    {
        DeviceContractValidation.RequireFingerprintKeyId(fingerprintKeyId);
        DeviceContractValidation.RequireFingerprintKeyEpoch(fingerprintKeyEpoch);
        if (!string.Equals(fingerprintKeyId, _options.FingerprintKeyId, StringComparison.Ordinal) ||
            fingerprintKeyEpoch != _options.FingerprintKeyEpoch)
            throw new InvalidOperationException("The fingerprint HMAC key version is not active for registration.");
    }

    private static void EnsureSameScope(
        DeviceRegisteredV1 result,
        string soulId,
        string deviceBindingId,
        string platformAccountId)
    {
        if (!string.Equals(result.SoulId, soulId, StringComparison.Ordinal) ||
            !string.Equals(result.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(result.PlatformAccountId, platformAccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SOUL-ISO-001: PostgreSQL returned a device from another operation scope.");
        }
    }

    private static string SerializeResult(DeviceRegisteredV1 result)
        => JsonSerializer.Serialize(result, SerializerOptions);

    private static DeviceRegisteredV1 DeserializeResult(string json)
    {
        var result = JsonSerializer.Deserialize<DeviceRegisteredV1>(json, SerializerOptions)
            ?? throw new InvalidOperationException("A stored device receipt could not be deserialized.");
        result.Validate();
        return result;
    }

    private static string ComputeCommandSha256(params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, "dps.device-registry.command/v1");
        foreach (var field in fields)
        {
            AppendHashField(hash, field);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string ComputeTextSha256(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool HashesEqual(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static string CreateDeviceId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        try
        {
            return "device_" + Convert.ToHexStringLower(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
