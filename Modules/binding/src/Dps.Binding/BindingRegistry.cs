using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.Binding.Contracts;
using Dps.DeviceRegistry.Contracts;
using Dps.PlatformAccountRegistry.Contracts;

namespace Dps.Binding;

public sealed record CreateBindingCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string DeviceId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record RevokeBindingCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

/// <summary>
/// A composition-root supplied, read-only port to device-registry. Implementations must obtain
/// the current provider-owned contract; callers cannot submit a proof DTO to the binding write path.
/// </summary>
internal interface IDeviceRegistrationReader
{
    Task<DeviceRegisteredV1> ReadCurrentAsync(
        string deviceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A composition-root supplied, read-only port to platform-account-registry. Implementations must
/// query current provider truth for the exact requested operation scope.
/// </summary>
internal interface IPlatformAccountAuthorizationReader
{
    Task<PlatformAccountAuthorizedV1> ReadCurrentAsync(
        string platformAccountId,
        string soulId,
        string deviceBindingId,
        CancellationToken cancellationToken = default);
}

internal interface IDeviceBindingReservationProvider : IDeviceRegistrationReader
{
    Task<DeviceBindingReservationV1> ReserveAsync(
        CreateBindingCommand command,
        long expectedRevision,
        string reservationId,
        CancellationToken cancellationToken = default);

    Task<DeviceBindingReservationV1> ConfirmAsync(
        IdentityBindingV1 binding,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<DeviceBindingReservationV1> ReleaseAsync(
        IdentityBindingV1 binding,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

internal interface IPlatformAccountBindingReservationProvider : IPlatformAccountAuthorizationReader
{
    Task<PlatformAccountBindingReservationV1> ReserveAsync(
        CreateBindingCommand command,
        long expectedRevision,
        string reservationId,
        CancellationToken cancellationToken = default);

    Task<PlatformAccountBindingReservationV1> ConfirmAsync(
        IdentityBindingV1 binding,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task<PlatformAccountBindingReservationV1> ReleaseAsync(
        IdentityBindingV1 binding,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

public interface IBindingRegistry
{
    Task<IdentityBindingV1> BindAsync(
        CreateBindingCommand command,
        CancellationToken cancellationToken = default);

    Task<IdentityBindingV1> RevokeAsync(
        RevokeBindingCommand command,
        CancellationToken cancellationToken = default);

    Task<IdentityBindingV1> GetAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic non-persistent implementation for unit and contract verification. Production
/// composition uses <see cref="PostgresBindingRegistry"/>. Both implementations resolve provider
/// truth through the same trusted reader ports.
/// </summary>
internal sealed class InMemoryBindingRegistry : IBindingRegistry
{
    private readonly object _gate = new();
    private readonly IDeviceRegistrationReader _deviceReader;
    private readonly IPlatformAccountAuthorizationReader _accountReader;
    private readonly Dictionary<string, IdentityBindingV1> _byBinding = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeByDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeByAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Operation, string RequestSha256, IdentityBindingV1 Result)> _idempotency =
        new(StringComparer.Ordinal);

    public InMemoryBindingRegistry(
        IDeviceRegistrationReader deviceReader,
        IPlatformAccountAuthorizationReader accountReader)
    {
        _deviceReader = deviceReader ?? throw new ArgumentNullException(nameof(deviceReader));
        _accountReader = accountReader ?? throw new ArgumentNullException(nameof(accountReader));
    }

    public async Task<IdentityBindingV1> BindAsync(
        CreateBindingCommand command,
        CancellationToken cancellationToken = default)
    {
        BindingValidation.ValidateCreate(command);
        var requestSha256 = BindingRequestHash.ForCreate(command);

        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                BindingValidation.EnsureSameRequest(prior.Operation, prior.RequestSha256, "bind", requestSha256);
                EnsureReceiptIsCurrent(prior);
                return prior.Result;
            }
        }

        var (device, account) = await BindingProviderTruthResolver.ReadAsync(
            _deviceReader,
            _accountReader,
            command,
            cancellationToken);

        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                BindingValidation.EnsureSameRequest(prior.Operation, prior.RequestSha256, "bind", requestSha256);
                EnsureReceiptIsCurrent(prior);
                return prior.Result;
            }

            if (_byBinding.ContainsKey(command.DeviceBindingId))
                throw new InvalidOperationException("The binding identifier cannot be reused or reactivated.");
            if (_activeByDevice.ContainsKey(command.DeviceId))
                throw new InvalidOperationException("The device already has an active binding.");
            if (_activeByAccount.ContainsKey(command.PlatformAccountId))
                throw new InvalidOperationException("The platform account already has an active binding.");

            var result = BindingValidation.CreateResult(
                command.SoulId,
                command.DeviceBindingId,
                command.PlatformAccountId,
                command.DeviceId,
                1,
                "active",
                device.CapabilityRevision,
                account.AuthorizationRevision,
                command.TraceId,
                command.IdempotencyKey,
                command.OccurredAt);
            _byBinding.Add(result.DeviceBindingId, result);
            _activeByDevice.Add(result.DeviceId, result.DeviceBindingId);
            _activeByAccount.Add(result.PlatformAccountId, result.DeviceBindingId);
            _idempotency.Add(command.IdempotencyKey, ("bind", requestSha256, result));
            return result;
        }
    }

    public Task<IdentityBindingV1> RevokeAsync(
        RevokeBindingCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindingValidation.ValidateRevoke(command);
        var requestSha256 = BindingRequestHash.ForRevoke(command);
        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                BindingValidation.EnsureSameRequest(prior.Operation, prior.RequestSha256, "revoke", requestSha256);
                return Task.FromResult(prior.Result);
            }

            var current = GetUnderLock(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
            if (current.Status != "active") throw new InvalidOperationException("The binding is not active.");
            if (current.BindingRevision != command.ExpectedRevision)
                throw new InvalidOperationException("The binding revision is stale.");

            var revoked = BindingValidation.CreateResult(
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
            _byBinding[current.DeviceBindingId] = revoked;
            _activeByDevice.Remove(current.DeviceId);
            _activeByAccount.Remove(current.PlatformAccountId);
            _idempotency.Add(command.IdempotencyKey, ("revoke", requestSha256, revoked));
            return Task.FromResult(revoked);
        }
    }

    public Task<IdentityBindingV1> GetAsync(
        string soulId,
        string deviceBindingId,
        string platformAccountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindingValidation.ValidateScope(soulId, deviceBindingId, platformAccountId);
        lock (_gate) return Task.FromResult(GetUnderLock(soulId, deviceBindingId, platformAccountId));
    }

    private IdentityBindingV1 GetUnderLock(string soulId, string bindingId, string accountId)
    {
        if (!_byBinding.TryGetValue(bindingId, out var value)) throw new KeyNotFoundException("Unknown binding.");
        if (!string.Equals(value.SoulId, soulId, StringComparison.Ordinal) ||
            !string.Equals(value.PlatformAccountId, accountId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("No binding exists in the requested scope.");
        }
        return value;
    }

    private void EnsureReceiptIsCurrent(
        (string Operation, string RequestSha256, IdentityBindingV1 Result) receipt)
    {
        if (!string.Equals(receipt.Operation, "bind", StringComparison.Ordinal)) return;
        if (!_byBinding.TryGetValue(receipt.Result.DeviceBindingId, out var current) ||
            !string.Equals(current.Status, "active", StringComparison.Ordinal) ||
            current.BindingRevision != receipt.Result.BindingRevision)
        {
            throw new BindingHistoricalReceiptException();
        }
    }
}

internal static class BindingProviderTruthResolver
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    public static async Task<(DeviceRegisteredV1 Device, PlatformAccountAuthorizedV1 Account)> ReadAsync(
        IDeviceRegistrationReader deviceReader,
        IPlatformAccountAuthorizationReader accountReader,
        CreateBindingCommand command,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProviderTimeout);
        var deviceTask = deviceReader.ReadCurrentAsync(
            command.DeviceId,
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            timeout.Token);
        var accountTask = accountReader.ReadCurrentAsync(
            command.PlatformAccountId,
            command.SoulId,
            command.DeviceBindingId,
            timeout.Token);
        await Task.WhenAll(deviceTask, accountTask).WaitAsync(ProviderTimeout, cancellationToken);
        var device = await deviceTask;
        var account = await accountTask;
        BindingValidation.ValidateProviderTruth(command, device, account);
        return (device, account);
    }
}

internal static class BindingProviderCommandDeadline
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    internal static Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => ExecuteAsync(operation, DefaultTimeout, cancellationToken);

    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var providerTask = operation(linked.Token);
            return await providerTask.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The binding provider command exceeded its declared deadline.", exception);
        }
        catch (TimeoutException exception)
        {
            linked.Cancel();
            throw new TimeoutException("The binding provider command exceeded its declared deadline.", exception);
        }
    }
}

internal static class BindingProviderReservationReceiptValidation
{
    private static readonly TimeSpan MaximumHeldLeaseFromTrustedNow = TimeSpan.FromMinutes(5.5);

    internal static void EnsureDevice(
        DeviceBindingReservationV1 receipt,
        IdentityBindingV1 expected,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        string expectedState,
        DateTimeOffset now,
        bool allowActiveRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        EnsureState(receipt.State, receipt.LeaseExpiresAt, expectedState, occurredAt, now, allowActiveRecovery);
        if (!string.Equals(receipt.SoulId, expected.SoulId, StringComparison.Ordinal) ||
            !string.Equals(receipt.DeviceBindingId, expected.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PlatformAccountId, expected.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(receipt.DeviceId, expected.DeviceId, StringComparison.Ordinal) ||
            receipt.DeviceRegistrationRevision != expected.DeviceRegistrationRevision ||
            !string.Equals(receipt.ReservationId, reservationId, StringComparison.Ordinal) ||
            !string.Equals(receipt.TraceId, traceId, StringComparison.Ordinal) ||
            receipt.OccurredAt != occurredAt ||
            !string.Equals(
                receipt.IdempotencyKey,
                DeviceBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, receipt.State),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The device-registry reservation receipt does not prove the exact requested scope and revision.");
        }
    }

    internal static void EnsureAccount(
        PlatformAccountBindingReservationV1 receipt,
        IdentityBindingV1 expected,
        string reservationId,
        string traceId,
        DateTimeOffset occurredAt,
        string expectedState,
        DateTimeOffset now,
        bool allowActiveRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        EnsureState(receipt.State, receipt.LeaseExpiresAt, expectedState, occurredAt, now, allowActiveRecovery);
        if (!string.Equals(receipt.SoulId, expected.SoulId, StringComparison.Ordinal) ||
            !string.Equals(receipt.DeviceBindingId, expected.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PlatformAccountId, expected.PlatformAccountId, StringComparison.Ordinal) ||
            receipt.AccountAuthorizationRevision != expected.AccountAuthorizationRevision ||
            !string.Equals(receipt.ReservationId, reservationId, StringComparison.Ordinal) ||
            !string.Equals(receipt.TraceId, traceId, StringComparison.Ordinal) ||
            receipt.OccurredAt != occurredAt ||
            !string.Equals(
                receipt.IdempotencyKey,
                PlatformAccountBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, receipt.State),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The platform-account reservation receipt does not prove the exact requested scope and revision.");
        }
    }

    private static void EnsureState(
        string actualState,
        DateTimeOffset? leaseExpiresAt,
        string expectedState,
        DateTimeOffset occurredAt,
        DateTimeOffset now,
        bool allowActiveRecovery)
    {
        BindingContractValidation.RequireUtc(now, nameof(now));
        var activeRecovery = allowActiveRecovery &&
                             string.Equals(expectedState, "held", StringComparison.Ordinal) &&
                             string.Equals(actualState, "active", StringComparison.Ordinal);
        if (!string.Equals(actualState, expectedState, StringComparison.Ordinal) && !activeRecovery)
            throw new InvalidOperationException($"The provider reservation receipt state must be '{expectedState}'.");
        if (string.Equals(actualState, "held", StringComparison.Ordinal) &&
            (!leaseExpiresAt.HasValue || leaseExpiresAt.Value <= now || leaseExpiresAt.Value <= occurredAt ||
             leaseExpiresAt.Value > now + MaximumHeldLeaseFromTrustedNow))
        {
            throw new InvalidOperationException("The provider returned an expired, non-forward, or overlong held reservation lease.");
        }
        if (!string.Equals(actualState, "held", StringComparison.Ordinal) && leaseExpiresAt.HasValue)
            throw new InvalidOperationException("Only a held provider reservation may carry a lease expiry.");
    }

    internal static string CreateDeviceReceiptIdempotencyKey(string reservationId, string state)
        => DeviceBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, state);

    internal static string CreatePlatformAccountReceiptIdempotencyKey(string reservationId, string state)
        => PlatformAccountBindingReservationV1.CreateReceiptIdempotencyKey(reservationId, state);
}

internal static class BindingValidation
{
    public static void ValidateCreate(CreateBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        BindingContractValidation.RequirePrefixedHex(command.DeviceId, "device_", 32, nameof(command.DeviceId));
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
    }

    public static void ValidateRevoke(RevokeBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        if (command.ExpectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(command.ExpectedRevision));
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
    }

    public static void ValidateFence(AcquireBindingMutationFenceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId, command.PlatformAccountId);
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
    }

    public static void ValidateProviderTruth(
        CreateBindingCommand command,
        DeviceRegisteredV1 device,
        PlatformAccountAuthorizedV1 account)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(account);
        device.Validate();
        account.Validate();

        if (!string.Equals(device.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(device.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(device.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(device.DeviceId, command.DeviceId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("No device registration exists in the requested binding scope.");
        }
        if (!string.Equals(account.SoulId, command.SoulId, StringComparison.Ordinal) ||
            !string.Equals(account.DeviceBindingId, command.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(account.PlatformAccountId, command.PlatformAccountId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("No platform-account authorization exists in the requested binding scope.");
        }
        if (!string.Equals(device.Status, "registered", StringComparison.Ordinal))
            throw new InvalidOperationException("The current device registration is not eligible for binding.");
        if (!string.Equals(account.Status, "authorized", StringComparison.Ordinal))
            throw new InvalidOperationException("The current platform-account authorization is not eligible for binding.");
    }

    public static void ValidateScope(string soulId, string bindingId, string accountId)
    {
        BindingContractValidation.RequireSoulId(soulId);
        BindingContractValidation.RequireDeviceBindingId(bindingId);
        BindingContractValidation.RequirePlatformAccountId(accountId);
    }

    public static void EnsureSameRequest(
        string existingOperation,
        string existingSha256,
        string incomingOperation,
        string incomingSha256)
    {
        if (!string.Equals(existingOperation, incomingOperation, StringComparison.Ordinal) ||
            !BindingRequestHash.FixedTimeEquals(existingSha256, incomingSha256))
        {
            throw new BindingIdempotencyConflictException();
        }
    }

    public static IdentityBindingV1 CreateResult(
        string soulId,
        string bindingId,
        string accountId,
        string deviceId,
        long bindingRevision,
        string status,
        long deviceRevision,
        long accountRevision,
        string traceId,
        string idempotencyKey,
        DateTimeOffset occurredAt)
    {
        var result = new IdentityBindingV1(
            IdentityBindingV1.CurrentSchemaVersion,
            IdentityBindingV1.CurrentContractId,
            IdentityBindingV1.CurrentProducerModule,
            soulId,
            bindingId,
            accountId,
            traceId,
            idempotencyKey,
            occurredAt,
            "sensitive",
            deviceId,
            bindingRevision,
            status,
            deviceRevision,
            accountRevision);
        result.Validate();
        return result;
    }

    private static void ValidateEnvelope(string traceId, string idempotencyKey, DateTimeOffset occurredAt)
    {
        BindingContractValidation.RequireTraceId(traceId);
        BindingContractValidation.RequireIdempotencyKey(idempotencyKey);
        BindingContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }
}

internal static class BindingRequestHash
{
    private const string Domain = "dps.binding.mutation/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ForCreate(CreateBindingCommand command) => Compute(
        "bind",
        command.SoulId,
        command.DeviceBindingId,
        command.PlatformAccountId,
        command.DeviceId,
        command.TraceId,
        command.IdempotencyKey,
        command.OccurredAt.ToString("O", CultureInfo.InvariantCulture));

    public static string ForRevoke(RevokeBindingCommand command) => Compute(
        "revoke",
        command.SoulId,
        command.DeviceBindingId,
        command.PlatformAccountId,
        command.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
        command.TraceId,
        command.IdempotencyKey,
        command.OccurredAt.ToString("O", CultureInfo.InvariantCulture));

    public static string CreateReservationId(string requestSha256)
    {
        BindingContractValidation.RequirePrefixedHex(requestSha256, string.Empty, 64, nameof(requestSha256));
        return "bres_" + Compute("provider-binding-reservation", requestSha256);
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64) return false;
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Compute(params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, Domain);
        Span<byte> fieldCount = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(fieldCount, fields.Length);
        hash.AppendData(fieldCount);
        foreach (var field in fields) AppendField(hash, field);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public sealed class BindingIdempotencyConflictException : InvalidOperationException
{
    public BindingIdempotencyConflictException()
        : base("The idempotency key is bound to a different binding mutation; the conflict was quarantined.")
    {
    }
}

public sealed class BindingHistoricalReceiptException : InvalidOperationException
{
    public BindingHistoricalReceiptException()
        : base("The bind mutation receipt is historical; current binding truth is no longer active.")
    {
    }
}
