using Dps.DeviceRegistry.Contracts;
using Npgsql;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.DeviceRegistry;

public sealed class DeviceBindingReservationConflictException : InvalidOperationException
{
    public DeviceBindingReservationConflictException()
        : base("The device already has an effective binding reservation.")
    {
    }
}

public sealed class DeviceBindingReservationLeaseExpiredException : InvalidOperationException
{
    public DeviceBindingReservationLeaseExpiredException()
        : base("The device binding reservation lease expired before confirmation.")
    {
    }
}

internal static class DeviceBindingReservationValidation
{
    public static void Validate(ReserveDeviceBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateFields(command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.DeviceId,
            command.ExpectedRevision, command.ReservationId, command.TraceId, command.OccurredAt);
    }

    public static void Validate(DeviceBindingReservationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateFields(command.SoulId, command.DeviceBindingId, command.PlatformAccountId, command.DeviceId,
            command.ExpectedRevision, command.ReservationId, command.TraceId, command.OccurredAt);
    }

    public static void EnsureScope(
        DeviceBindingReservationV1 reservation,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string deviceId,
        long expectedRevision,
        string reservationId)
    {
        if (!string.Equals(reservation.ReservationId, reservationId, StringComparison.Ordinal) ||
            !string.Equals(reservation.SoulId, soulId, StringComparison.Ordinal) ||
            !string.Equals(reservation.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(reservation.PlatformAccountId, platformAccountId, StringComparison.Ordinal) ||
            !string.Equals(reservation.DeviceId, deviceId, StringComparison.Ordinal) ||
            reservation.DeviceRegistrationRevision != expectedRevision)
        {
            throw new KeyNotFoundException("No device binding reservation exists in the requested scope.");
        }
    }

    private static void ValidateFields(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string deviceId,
        long expectedRevision,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt)
    {
        DeviceContractValidation.RequireSoulId(soulId);
        DeviceContractValidation.RequireDeviceBindingId(deviceBindingId);
        DeviceContractValidation.RequirePlatformAccountId(platformAccountId);
        DeviceContractValidation.RequirePrefixedHex(deviceId, "device_", 32, nameof(deviceId));
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        DeviceContractValidation.RequirePrefixedHex(reservationId, "bres_", 64, nameof(reservationId));
        DeviceContractValidation.RequireTraceId(traceId);
        DeviceContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }
}

internal static class DeviceBindingReservationReceiptIdentity
{
    internal static string CreateIdempotencyKey(string reservationId, string state)
        => DeviceBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, state);
}

internal static class DeviceRegistryProviderInstanceIdentity
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password",
        "Passfile",
        "SSL Password"
    };

    internal static string Compute(PostgresDeviceRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "DPS:DEVICE-REGISTRY:INSTANCE-CONFIGURATION:V1");
        Append(hash, "device-registry");
        foreach (var key in builder.Keys.Cast<string>().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (SecretKeys.Contains(key)) continue;
            Append(hash, key.ToLowerInvariant());
            Append(hash, Convert.ToString(builder[key], CultureInfo.InvariantCulture) ?? string.Empty);
        }
        Append(hash, "schema");
        Append(hash, options.SchemaName);
        Append(hash, "fingerprint-key-id");
        Append(hash, options.FingerprintKeyId);
        Append(hash, "fingerprint-key-epoch");
        Append(hash, options.FingerprintKeyEpoch.ToString(CultureInfo.InvariantCulture));
        Append(hash, "trust-epoch");
        Append(hash, options.TrustEpoch.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public sealed partial class InMemoryDeviceRegistry
{
    private readonly Dictionary<string, DeviceBindingReservationV1> _bindingReservations = new(StringComparer.Ordinal);

    internal DeviceBindingReservationV1 ReserveBinding(ReserveDeviceBindingCommand command)
    {
        DeviceBindingReservationValidation.Validate(command);
        lock (_gate)
        {
            var current = GetUnderLock(command.DeviceId, command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
            if (!string.Equals(current.Status, "registered", StringComparison.Ordinal) ||
                current.CapabilityRevision != command.ExpectedRevision)
                throw new InvalidOperationException("Device truth is not eligible at the requested revision.");

            if (_bindingReservations.TryGetValue(command.ReservationId, out var prior))
            {
                DeviceBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                    command.PlatformAccountId, command.DeviceId, command.ExpectedRevision, command.ReservationId);
                if (prior.State == "released") throw new InvalidOperationException("A released reservation cannot be reactivated.");
                if (prior.State == "active" || prior.LeaseExpiresAt > TimeProvider.System.GetUtcNow()) return prior;
            }

            var now = TimeProvider.System.GetUtcNow();
            if (_bindingReservations.Values.Any(value =>
                    value.DeviceId == command.DeviceId && value.ReservationId != command.ReservationId &&
                    (value.State == "active" || value.State == "held" && value.LeaseExpiresAt > now)))
                throw new DeviceBindingReservationConflictException();

            var held = CreateDeviceReservation(command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
                command.DeviceId, command.ExpectedRevision, command.ReservationId, "held", now.AddMinutes(5),
                command.TraceId, command.OccurredAt);
            _bindingReservations[command.ReservationId] = held;
            return held;
        }
    }

    internal DeviceBindingReservationV1 ConfirmBinding(DeviceBindingReservationCommand command)
    {
        DeviceBindingReservationValidation.Validate(command);
        lock (_gate)
        {
            var current = GetUnderLock(command.DeviceId, command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
            if (!string.Equals(current.Status, "registered", StringComparison.Ordinal) ||
                current.CapabilityRevision != command.ExpectedRevision)
                throw new InvalidOperationException("Device truth changed before reservation confirmation.");
            if (!_bindingReservations.TryGetValue(command.ReservationId, out var prior))
                throw new KeyNotFoundException("Unknown device binding reservation.");
            DeviceBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.DeviceId, command.ExpectedRevision, command.ReservationId);
            if (prior.State == "active") return prior;
            if (prior.State != "held" || prior.LeaseExpiresAt <= TimeProvider.System.GetUtcNow())
                throw new DeviceBindingReservationLeaseExpiredException();
            var active = CreateDeviceReservation(command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
                command.DeviceId, command.ExpectedRevision, command.ReservationId, "active", null,
                command.TraceId, command.OccurredAt);
            _bindingReservations[command.ReservationId] = active;
            return active;
        }
    }

    internal DeviceBindingReservationV1 ReleaseBinding(DeviceBindingReservationCommand command)
    {
        DeviceBindingReservationValidation.Validate(command);
        lock (_gate)
        {
            if (!_bindingReservations.TryGetValue(command.ReservationId, out var prior))
                throw new KeyNotFoundException("Unknown device binding reservation.");
            DeviceBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.DeviceId, command.ExpectedRevision, command.ReservationId);
            if (prior.State == "released") return prior;
            var released = CreateDeviceReservation(command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
                command.DeviceId, command.ExpectedRevision, command.ReservationId, "released", null,
                command.TraceId, command.OccurredAt);
            _bindingReservations[command.ReservationId] = released;
            return released;
        }
    }

    private void EnsureNoEffectiveBindingReservationUnderLock(string deviceId)
    {
        var now = TimeProvider.System.GetUtcNow();
        if (_bindingReservations.Values.Any(value => value.DeviceId == deviceId &&
                (value.State == "active" || value.State == "held" && value.LeaseExpiresAt > now)))
            throw new DeviceBindingReservationConflictException();
    }

    private static DeviceBindingReservationV1 CreateDeviceReservation(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string deviceId,
        long revision,
        string reservationId,
        string state,
        DateTimeOffset? leaseExpiresAt,
        string traceId,
        DateTimeOffset occurredAt)
    {
        var result = new DeviceBindingReservationV1(
            DeviceBindingReservationV1.CurrentSchemaVersion,
            DeviceBindingReservationV1.CurrentContractId,
            DeviceBindingReservationV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            DeviceBindingReservationReceiptIdentity.CreateIdempotencyKey(reservationId, state),
            occurredAt,
            "sensitive",
            reservationId,
            deviceId,
            revision,
            state,
            leaseExpiresAt);
        result.Validate();
        return result;
    }
}

public sealed partial class PostgresDeviceRegistry
{
    public IDeviceBindingReservationClient CreateBindingReservationClient()
        => new PostgresDeviceBindingReservationClient(this);

    internal async Task<DeviceBindingReservationV1> ReserveBindingAsync(
        ReserveDeviceBindingCommand command,
        CancellationToken cancellationToken = default)
    {
        DeviceBindingReservationValidation.Validate(command);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "device:" + command.DeviceId, cancellationToken);
        var current = await ReadScopedCurrentAsync(connection, transaction, command.DeviceId, command.SoulId,
            command.DeviceBindingId, command.PlatformAccountId, forUpdate: true, cancellationToken)
            ?? throw new KeyNotFoundException("No device exists in the requested operation scope.");
        if (!string.Equals(current.Status, "registered", StringComparison.Ordinal) ||
            current.CapabilityRevision != command.ExpectedRevision)
            throw new InvalidOperationException("Device truth is not eligible at the requested revision.");

        var prior = await ReadDeviceBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: true, cancellationToken);
        if (prior is not null)
        {
            DeviceBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.DeviceId, command.ExpectedRevision, command.ReservationId);
            if (prior.State == "released") throw new InvalidOperationException("A released reservation cannot be reactivated.");
            if (prior.State == "active")
            {
                await transaction.CommitAsync(cancellationToken);
                return prior;
            }

            await using var refresh = new NpgsqlCommand(
                $"UPDATE {_options.SchemaName}.binding_reservations SET lease_expires_at = clock_timestamp() + interval '5 minutes', trace_id = @trace_id, occurred_at = @occurred_at, updated_at = clock_timestamp() WHERE reservation_id = @reservation_id AND state = 'held' AND lease_expires_at <= clock_timestamp()",
                connection,
                transaction);
            refresh.Parameters.AddWithValue("reservation_id", command.ReservationId);
            refresh.Parameters.AddWithValue("trace_id", command.TraceId);
            refresh.Parameters.AddWithValue("occurred_at", command.OccurredAt);
            await refresh.ExecuteNonQueryAsync(cancellationToken);
            var held = await ReadDeviceBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
                ?? throw new InvalidOperationException("The device reservation disappeared during refresh.");
            await transaction.CommitAsync(cancellationToken);
            return held;
        }

        await using (var expire = new NpgsqlCommand(
            $"UPDATE {_options.SchemaName}.binding_reservations SET state = 'released', lease_expires_at = NULL, updated_at = clock_timestamp() WHERE device_id = @device_id AND state = 'held' AND lease_expires_at <= clock_timestamp()",
            connection,
            transaction))
        {
            expire.Parameters.AddWithValue("device_id", command.DeviceId);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        if (await HasEffectiveDeviceBindingReservationAsync(connection, transaction, command.DeviceId, cancellationToken))
            throw new DeviceBindingReservationConflictException();

        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {_options.SchemaName}.binding_reservations (reservation_id, device_id, soul_id, device_binding_id, platform_account_id, device_registration_revision, state, lease_expires_at, trace_id, occurred_at) VALUES (@reservation_id, @device_id, @soul_id, @device_binding_id, @platform_account_id, @revision, 'held', clock_timestamp() + interval '5 minutes', @trace_id, @occurred_at)",
            connection,
            transaction))
        {
            AddDeviceBindingReservationParameters(insert, command.ReservationId, command.DeviceId, command.SoulId,
                command.DeviceBindingId, command.PlatformAccountId, command.ExpectedRevision, command.TraceId, command.OccurredAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var created = await ReadDeviceBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The device reservation was not persisted.");
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    internal async Task<DeviceBindingReservationV1> ConfirmBindingAsync(
        DeviceBindingReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        DeviceBindingReservationValidation.Validate(command);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "device:" + command.DeviceId, cancellationToken);
        var current = await ReadScopedCurrentAsync(connection, transaction, command.DeviceId, command.SoulId,
            command.DeviceBindingId, command.PlatformAccountId, forUpdate: true, cancellationToken)
            ?? throw new KeyNotFoundException("No device exists in the requested operation scope.");
        if (!string.Equals(current.Status, "registered", StringComparison.Ordinal) ||
            current.CapabilityRevision != command.ExpectedRevision)
            throw new InvalidOperationException("Device truth changed before reservation confirmation.");
        var prior = await ReadDeviceBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: true, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown device binding reservation.");
        DeviceBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
            command.PlatformAccountId, command.DeviceId, command.ExpectedRevision, command.ReservationId);
        if (prior.State == "active")
        {
            await transaction.CommitAsync(cancellationToken);
            return prior;
        }
        await using var update = new NpgsqlCommand(
            $"UPDATE {_options.SchemaName}.binding_reservations SET state = 'active', lease_expires_at = NULL, trace_id = @trace_id, occurred_at = @occurred_at, updated_at = clock_timestamp() WHERE reservation_id = @reservation_id AND state = 'held' AND lease_expires_at > clock_timestamp()",
            connection,
            transaction);
        update.Parameters.AddWithValue("reservation_id", command.ReservationId);
        update.Parameters.AddWithValue("trace_id", command.TraceId);
        update.Parameters.AddWithValue("occurred_at", command.OccurredAt);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new DeviceBindingReservationLeaseExpiredException();
        var active = await ReadDeviceBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The confirmed device reservation disappeared.");
        await transaction.CommitAsync(cancellationToken);
        return active;
    }

    internal async Task<DeviceBindingReservationV1> ReleaseBindingAsync(
        DeviceBindingReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        DeviceBindingReservationValidation.Validate(command);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "device:" + command.DeviceId, cancellationToken);
        var prior = await ReadDeviceBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: true, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown device binding reservation.");
        DeviceBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
            command.PlatformAccountId, command.DeviceId, command.ExpectedRevision, command.ReservationId);
        if (prior.State != "released")
        {
            await using var update = new NpgsqlCommand(
                $"UPDATE {_options.SchemaName}.binding_reservations SET state = 'released', lease_expires_at = NULL, trace_id = @trace_id, occurred_at = @occurred_at, updated_at = clock_timestamp() WHERE reservation_id = @reservation_id AND state IN ('held', 'active')",
                connection,
                transaction);
            update.Parameters.AddWithValue("reservation_id", command.ReservationId);
            update.Parameters.AddWithValue("trace_id", command.TraceId);
            update.Parameters.AddWithValue("occurred_at", command.OccurredAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The device reservation could not be released.");
        }
        var released = await ReadDeviceBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The released device reservation disappeared.");
        await transaction.CommitAsync(cancellationToken);
        return released;
    }

    private async Task EnsureNoEffectiveBindingReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (await HasEffectiveDeviceBindingReservationAsync(connection, transaction, deviceId, cancellationToken))
            throw new DeviceBindingReservationConflictException();
    }

    private async Task<bool> HasEffectiveDeviceBindingReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_options.SchemaName}.binding_reservations WHERE device_id = @device_id AND (state = 'active' OR state = 'held' AND lease_expires_at > clock_timestamp()))",
            connection,
            transaction);
        query.Parameters.AddWithValue("device_id", deviceId);
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL returned no device reservation result."));
    }

    private async Task<DeviceBindingReservationV1?> ReadDeviceBindingReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string reservationId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockClause = forUpdate ? " FOR UPDATE" : string.Empty;
        await using var query = new NpgsqlCommand(
            $"SELECT soul_id, device_binding_id, platform_account_id, trace_id, occurred_at, device_id, device_registration_revision, state, lease_expires_at FROM {_options.SchemaName}.binding_reservations WHERE reservation_id = @reservation_id{lockClause}",
            connection,
            transaction);
        query.Parameters.AddWithValue("reservation_id", reservationId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = CreateDeviceReservation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(5),
            reader.GetInt64(6),
            reservationId,
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8).ToUniversalTime(),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime());
        return result;
    }

    private static void AddDeviceBindingReservationParameters(
        NpgsqlCommand command,
        string reservationId,
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long revision,
        string traceId,
        DateTimeOffset occurredAt)
    {
        command.Parameters.AddWithValue("reservation_id", reservationId);
        command.Parameters.AddWithValue("device_id", deviceId);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("trace_id", traceId);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
    }

    private static DeviceBindingReservationV1 CreateDeviceReservation(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        string deviceId,
        long revision,
        string reservationId,
        string state,
        DateTimeOffset? leaseExpiresAt,
        string traceId,
        DateTimeOffset occurredAt)
    {
        var result = new DeviceBindingReservationV1(
            DeviceBindingReservationV1.CurrentSchemaVersion,
            DeviceBindingReservationV1.CurrentContractId,
            DeviceBindingReservationV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            DeviceBindingReservationReceiptIdentity.CreateIdempotencyKey(reservationId, state),
            occurredAt,
            "sensitive",
            reservationId,
            deviceId,
            revision,
            state,
            leaseExpiresAt);
        result.Validate();
        return result;
    }
}

internal sealed class PostgresDeviceBindingReservationClient : IDeviceBindingReservationClient
{
    private readonly PostgresDeviceRegistry _registry;

    internal PostgresDeviceBindingReservationClient(PostgresDeviceRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string InstanceConfigurationSha256
        => _registry.BindingProviderInstanceConfigurationSha256;

    public long InstanceTrustEpoch => _registry.BindingProviderInstanceTrustEpoch;

    public Task<DeviceRegisteredV1> ReadCurrentAsync(
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
        => _registry.GetAsync(deviceId, soulId, deviceBindingId, platformAccountId, cancellationToken);

    public Task<DeviceBindingReservationV1> ReserveAsync(
        ReserveDeviceBindingCommand command,
        CancellationToken cancellationToken = default)
        => _registry.ReserveBindingAsync(command, cancellationToken);

    public Task<DeviceBindingReservationV1> ConfirmAsync(
        DeviceBindingReservationCommand command,
        CancellationToken cancellationToken = default)
        => _registry.ConfirmBindingAsync(command, cancellationToken);

    public Task<DeviceBindingReservationV1> ReleaseAsync(
        DeviceBindingReservationCommand command,
        CancellationToken cancellationToken = default)
        => _registry.ReleaseBindingAsync(command, cancellationToken);
}
