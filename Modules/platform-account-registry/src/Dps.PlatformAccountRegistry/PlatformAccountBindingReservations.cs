using Dps.PlatformAccountRegistry.Contracts;
using Npgsql;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.PlatformAccountRegistry;

public sealed class PlatformAccountBindingReservationConflictException : InvalidOperationException
{
    public PlatformAccountBindingReservationConflictException()
        : base("The platform account already has an effective binding reservation.")
    {
    }
}

public sealed class PlatformAccountBindingReservationLeaseExpiredException : InvalidOperationException
{
    public PlatformAccountBindingReservationLeaseExpiredException()
        : base("The platform-account binding reservation lease expired before confirmation.")
    {
    }
}

internal static class PlatformAccountBindingReservationValidation
{
    public static void Validate(ReservePlatformAccountBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateFields(command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.ExpectedRevision, command.ReservationId, command.TraceId, command.OccurredAt);
    }

    public static void Validate(PlatformAccountBindingReservationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateFields(command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.ExpectedRevision, command.ReservationId, command.TraceId, command.OccurredAt);
    }

    public static void EnsureScope(
        PlatformAccountBindingReservationV1 reservation,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long expectedRevision,
        string reservationId)
    {
        if (!string.Equals(reservation.ReservationId, reservationId, StringComparison.Ordinal) ||
            !string.Equals(reservation.SoulId, soulId, StringComparison.Ordinal) ||
            !string.Equals(reservation.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(reservation.PlatformAccountId, platformAccountId, StringComparison.Ordinal) ||
            reservation.AccountAuthorizationRevision != expectedRevision)
        {
            throw new KeyNotFoundException("No platform-account binding reservation exists in the requested scope.");
        }
    }

    private static void ValidateFields(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long expectedRevision,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt)
    {
        AccountContractValidation.RequireSoulId(soulId);
        AccountContractValidation.RequireDeviceBindingId(deviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(platformAccountId);
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        AccountContractValidation.RequirePrefixedHex(reservationId, "bres_", 64, nameof(reservationId));
        AccountContractValidation.RequireTraceId(traceId);
        AccountContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }
}

internal static class PlatformAccountProviderInstanceIdentity
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password",
        "Passfile",
        "SSL Password"
    };

    internal static string Compute(PlatformAccountRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "DPS:PLATFORM-ACCOUNT-REGISTRY:INSTANCE-CONFIGURATION:V1");
        Append(hash, "platform-account-registry");
        foreach (var key in builder.Keys.Cast<string>().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (SecretKeys.Contains(key)) continue;
            Append(hash, key.ToLowerInvariant());
            Append(hash, Convert.ToString(builder[key], CultureInfo.InvariantCulture) ?? string.Empty);
        }
        Append(hash, "schema");
        Append(hash, options.SchemaName);
        Append(hash, "active-release-bom-sha256");
        Append(hash, options.ActiveReleaseBomSha256);
        Append(hash, "active-release-generation");
        Append(hash, options.ActiveReleaseGeneration.ToString(CultureInfo.InvariantCulture));
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

public sealed partial class InMemoryPlatformAccountRegistry
{
    private readonly Dictionary<string, PlatformAccountBindingReservationV1> _bindingReservations = new(StringComparer.Ordinal);

    internal PlatformAccountBindingReservationV1 ReserveBinding(ReservePlatformAccountBindingCommand command)
    {
        PlatformAccountBindingReservationValidation.Validate(command);
        lock (_gate)
        {
            var current = GetUnderLock(command.PlatformAccountId, command.SoulId, command.DeviceBindingId);
            if (!string.Equals(current.Status, "authorized", StringComparison.Ordinal) ||
                current.AuthorizationRevision != command.ExpectedRevision)
                throw new InvalidOperationException("Platform-account truth is not eligible at the requested revision.");
            if (_bindingReservations.TryGetValue(command.ReservationId, out var prior))
            {
                PlatformAccountBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                    command.PlatformAccountId, command.ExpectedRevision, command.ReservationId);
                if (prior.State == "released") throw new InvalidOperationException("A released reservation cannot be reactivated.");
                if (prior.State == "active" || prior.LeaseExpiresAt > TimeProvider.System.GetUtcNow()) return prior;
            }
            var now = TimeProvider.System.GetUtcNow();
            if (_bindingReservations.Values.Any(value =>
                    value.PlatformAccountId == command.PlatformAccountId && value.ReservationId != command.ReservationId &&
                    (value.State == "active" || value.State == "held" && value.LeaseExpiresAt > now)))
                throw new PlatformAccountBindingReservationConflictException();
            var held = CreatePlatformAccountReservation(command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.ExpectedRevision, command.ReservationId, "held",
                now.AddMinutes(5), command.TraceId, command.OccurredAt);
            _bindingReservations[command.ReservationId] = held;
            return held;
        }
    }

    internal PlatformAccountBindingReservationV1 ConfirmBinding(PlatformAccountBindingReservationCommand command)
    {
        PlatformAccountBindingReservationValidation.Validate(command);
        lock (_gate)
        {
            var current = GetUnderLock(command.PlatformAccountId, command.SoulId, command.DeviceBindingId);
            if (!string.Equals(current.Status, "authorized", StringComparison.Ordinal) ||
                current.AuthorizationRevision != command.ExpectedRevision)
                throw new InvalidOperationException("Platform-account truth changed before reservation confirmation.");
            if (!_bindingReservations.TryGetValue(command.ReservationId, out var prior))
                throw new KeyNotFoundException("Unknown platform-account binding reservation.");
            PlatformAccountBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.ExpectedRevision, command.ReservationId);
            if (prior.State == "active") return prior;
            if (prior.State != "held" || prior.LeaseExpiresAt <= TimeProvider.System.GetUtcNow())
                throw new PlatformAccountBindingReservationLeaseExpiredException();
            var active = CreatePlatformAccountReservation(command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.ExpectedRevision, command.ReservationId, "active", null,
                command.TraceId, command.OccurredAt);
            _bindingReservations[command.ReservationId] = active;
            return active;
        }
    }

    internal PlatformAccountBindingReservationV1 ReleaseBinding(PlatformAccountBindingReservationCommand command)
    {
        PlatformAccountBindingReservationValidation.Validate(command);
        lock (_gate)
        {
            if (!_bindingReservations.TryGetValue(command.ReservationId, out var prior))
                throw new KeyNotFoundException("Unknown platform-account binding reservation.");
            PlatformAccountBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.ExpectedRevision, command.ReservationId);
            if (prior.State == "released") return prior;
            var released = CreatePlatformAccountReservation(command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.ExpectedRevision, command.ReservationId, "released", null,
                command.TraceId, command.OccurredAt);
            _bindingReservations[command.ReservationId] = released;
            return released;
        }
    }

    private void EnsureNoEffectiveBindingReservationUnderLock(string platformAccountId)
    {
        var now = TimeProvider.System.GetUtcNow();
        if (_bindingReservations.Values.Any(value => value.PlatformAccountId == platformAccountId &&
                (value.State == "active" || value.State == "held" && value.LeaseExpiresAt > now)))
            throw new PlatformAccountBindingReservationConflictException();
    }

    private static PlatformAccountBindingReservationV1 CreatePlatformAccountReservation(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long revision,
        string reservationId,
        string state,
        DateTimeOffset? leaseExpiresAt,
        string traceId,
        DateTimeOffset occurredAt)
    {
        var result = new PlatformAccountBindingReservationV1(
            PlatformAccountBindingReservationV1.CurrentSchemaVersion,
            PlatformAccountBindingReservationV1.CurrentContractId,
            PlatformAccountBindingReservationV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            PlatformAccountBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, state),
            occurredAt,
            "sensitive",
            reservationId,
            revision,
            state,
            leaseExpiresAt);
        result.Validate();
        return result;
    }
}

public sealed partial class PostgresPlatformAccountRegistry
{
    public IPlatformAccountBindingReservationClient CreateBindingReservationClient()
        => new PostgresPlatformAccountBindingReservationClient(this);

    internal async Task<PlatformAccountBindingReservationV1> ReserveBindingAsync(
        ReservePlatformAccountBindingCommand command,
        CancellationToken cancellationToken = default)
    {
        PlatformAccountBindingReservationValidation.Validate(command);
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "platform-account:id:" + command.PlatformAccountId, cancellationToken);
        var current = await ReadAccountAsync(connection, transaction, command.PlatformAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown platform account.");
        EnsureScope(current, command.SoulId, command.DeviceBindingId);
        if (!string.Equals(current.Status, "authorized", StringComparison.Ordinal) ||
            current.AuthorizationRevision != command.ExpectedRevision)
            throw new InvalidOperationException("Platform-account truth is not eligible at the requested revision.");

        var prior = await ReadPlatformAccountBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: true, cancellationToken);
        if (prior is not null)
        {
            PlatformAccountBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
                command.PlatformAccountId, command.ExpectedRevision, command.ReservationId);
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
            var held = await ReadPlatformAccountBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
                ?? throw new InvalidOperationException("The platform-account reservation disappeared during refresh.");
            await transaction.CommitAsync(cancellationToken);
            return held;
        }

        await using (var expire = new NpgsqlCommand(
            $"UPDATE {_options.SchemaName}.binding_reservations SET state = 'released', lease_expires_at = NULL, updated_at = clock_timestamp() WHERE platform_account_id = @platform_account_id AND state = 'held' AND lease_expires_at <= clock_timestamp()",
            connection,
            transaction))
        {
            expire.Parameters.AddWithValue("platform_account_id", command.PlatformAccountId);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }
        if (await HasEffectivePlatformAccountBindingReservationAsync(connection, transaction, command.PlatformAccountId, cancellationToken))
            throw new PlatformAccountBindingReservationConflictException();

        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {_options.SchemaName}.binding_reservations (reservation_id, platform_account_id, soul_id, device_binding_id, account_authorization_revision, state, lease_expires_at, trace_id, occurred_at) VALUES (@reservation_id, @platform_account_id, @soul_id, @device_binding_id, @revision, 'held', clock_timestamp() + interval '5 minutes', @trace_id, @occurred_at)",
            connection,
            transaction))
        {
            AddPlatformAccountBindingReservationParameters(insert, command.ReservationId, command.SoulId,
                command.DeviceBindingId, command.PlatformAccountId, command.ExpectedRevision, command.TraceId, command.OccurredAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var created = await ReadPlatformAccountBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The platform-account reservation was not persisted.");
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    internal async Task<PlatformAccountBindingReservationV1> ConfirmBindingAsync(
        PlatformAccountBindingReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        PlatformAccountBindingReservationValidation.Validate(command);
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "platform-account:id:" + command.PlatformAccountId, cancellationToken);
        var current = await ReadAccountAsync(connection, transaction, command.PlatformAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown platform account.");
        EnsureScope(current, command.SoulId, command.DeviceBindingId);
        if (!string.Equals(current.Status, "authorized", StringComparison.Ordinal) ||
            current.AuthorizationRevision != command.ExpectedRevision)
            throw new InvalidOperationException("Platform-account truth changed before reservation confirmation.");
        var prior = await ReadPlatformAccountBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: true, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown platform-account binding reservation.");
        PlatformAccountBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
            command.PlatformAccountId, command.ExpectedRevision, command.ReservationId);
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
            throw new PlatformAccountBindingReservationLeaseExpiredException();
        var active = await ReadPlatformAccountBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The confirmed platform-account reservation disappeared.");
        await transaction.CommitAsync(cancellationToken);
        return active;
    }

    internal async Task<PlatformAccountBindingReservationV1> ReleaseBindingAsync(
        PlatformAccountBindingReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        PlatformAccountBindingReservationValidation.Validate(command);
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLockAsync(connection, transaction, "platform-account:id:" + command.PlatformAccountId, cancellationToken);
        var prior = await ReadPlatformAccountBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: true, cancellationToken)
            ?? throw new KeyNotFoundException("Unknown platform-account binding reservation.");
        PlatformAccountBindingReservationValidation.EnsureScope(prior, command.SoulId, command.DeviceBindingId,
            command.PlatformAccountId, command.ExpectedRevision, command.ReservationId);
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
                throw new InvalidOperationException("The platform-account reservation could not be released.");
        }
        var released = await ReadPlatformAccountBindingReservationAsync(connection, transaction, command.ReservationId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The released platform-account reservation disappeared.");
        await transaction.CommitAsync(cancellationToken);
        return released;
    }

    private async Task EnsureNoEffectiveBindingReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string platformAccountId,
        CancellationToken cancellationToken)
    {
        if (await HasEffectivePlatformAccountBindingReservationAsync(connection, transaction, platformAccountId, cancellationToken))
            throw new PlatformAccountBindingReservationConflictException();
    }

    private async Task<bool> HasEffectivePlatformAccountBindingReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string platformAccountId,
        CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_options.SchemaName}.binding_reservations WHERE platform_account_id = @platform_account_id AND (state = 'active' OR state = 'held' AND lease_expires_at > clock_timestamp()))",
            connection,
            transaction);
        query.Parameters.AddWithValue("platform_account_id", platformAccountId);
        return (bool)(await query.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL returned no platform-account reservation result."));
    }

    private async Task<PlatformAccountBindingReservationV1?> ReadPlatformAccountBindingReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string reservationId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockClause = forUpdate ? " FOR UPDATE" : string.Empty;
        await using var query = new NpgsqlCommand(
            $"SELECT soul_id, device_binding_id, platform_account_id, trace_id, occurred_at, account_authorization_revision, state, lease_expires_at FROM {_options.SchemaName}.binding_reservations WHERE reservation_id = @reservation_id{lockClause}",
            connection,
            transaction);
        query.Parameters.AddWithValue("reservation_id", reservationId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return CreatePlatformAccountReservation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(5),
            reservationId,
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7).ToUniversalTime(),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime());
    }

    private static void AddPlatformAccountBindingReservationParameters(
        NpgsqlCommand command,
        string reservationId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long revision,
        string traceId,
        DateTimeOffset occurredAt)
    {
        command.Parameters.AddWithValue("reservation_id", reservationId);
        command.Parameters.AddWithValue("soul_id", soulId);
        command.Parameters.AddWithValue("device_binding_id", deviceBindingId);
        command.Parameters.AddWithValue("platform_account_id", platformAccountId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("trace_id", traceId);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
    }

    private static PlatformAccountBindingReservationV1 CreatePlatformAccountReservation(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        long revision,
        string reservationId,
        string state,
        DateTimeOffset? leaseExpiresAt,
        string traceId,
        DateTimeOffset occurredAt)
    {
        var result = new PlatformAccountBindingReservationV1(
            PlatformAccountBindingReservationV1.CurrentSchemaVersion,
            PlatformAccountBindingReservationV1.CurrentContractId,
            PlatformAccountBindingReservationV1.CurrentProducerModule,
            soulId,
            deviceBindingId,
            platformAccountId,
            traceId,
            PlatformAccountBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, state),
            occurredAt,
            "sensitive",
            reservationId,
            revision,
            state,
            leaseExpiresAt);
        result.Validate();
        return result;
    }
}

internal sealed class PostgresPlatformAccountBindingReservationClient : IPlatformAccountBindingReservationClient
{
    private readonly PostgresPlatformAccountRegistry _registry;

    internal PostgresPlatformAccountBindingReservationClient(PostgresPlatformAccountRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string InstanceConfigurationSha256 => _registry.BindingProviderInstanceConfigurationSha256;
    public long InstanceTrustEpoch => _registry.BindingProviderInstanceTrustEpoch;

    public async Task<PlatformAccountAuthorizedV1> ReadCurrentAsync(
        string platformAccountId,
        string soulId,
        string deviceBindingId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _registry.GetAsync(platformAccountId, soulId, deviceBindingId, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new KeyNotFoundException(
                "No platform-account authorization exists in the requested binding scope.",
                exception);
        }
    }

    public Task<PlatformAccountBindingReservationV1> ReserveAsync(
        ReservePlatformAccountBindingCommand command,
        CancellationToken cancellationToken = default)
        => _registry.ReserveBindingAsync(command, cancellationToken);

    public Task<PlatformAccountBindingReservationV1> ConfirmAsync(
        PlatformAccountBindingReservationCommand command,
        CancellationToken cancellationToken = default)
        => _registry.ConfirmBindingAsync(command, cancellationToken);

    public Task<PlatformAccountBindingReservationV1> ReleaseAsync(
        PlatformAccountBindingReservationCommand command,
        CancellationToken cancellationToken = default)
        => _registry.ReleaseBindingAsync(command, cancellationToken);
}
