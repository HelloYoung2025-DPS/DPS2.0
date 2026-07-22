namespace Dps.ControlPlaneHost.Contracts;

/// <summary>
/// Nominal composition capability issued only by the control-plane-host
/// active release binding authority. Unlike implementable public reader and
/// coordinator interfaces, this sealed value cannot be substituted by a
/// consumer assembly. Both ordinary active reads and recovery acquisition
/// are therefore bound to the same authority and its exact store. Recovery
/// acquisition reads the active binding while that store holds the same
/// per-device serialization primitive used by activation, revocation, and
/// rollback. The returned lease must remain alive through the consumer's
/// authorization commit.
/// </summary>
public sealed class ActiveReleaseBindingRecoveryCapability
{
    private readonly IActiveReleaseBindingRecoveryCapabilityIssuer _issuer;
    private readonly bool _isDurable;

    internal ActiveReleaseBindingRecoveryCapability(
        IActiveReleaseBindingRecoveryCapabilityIssuer issuer,
        bool isDurable)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        _issuer = issuer;
        _isDurable = isDurable;
    }

    /// <summary>
    /// True only when the issuer is bound to the sole durable PostgreSQL
    /// truth-store implementation. Test-only in-memory authorities may still
    /// exercise the capability in same-module tests, but production consumer
    /// constructors must call <see cref="RequireDurable"/> and reject them.
    /// </summary>
    public bool IsDurable => _isDurable;

    public void RequireDurable()
    {
        if (!_isDurable)
        {
            throw new InvalidOperationException(
                "Production active release binding composition requires the durable control-plane-host truth store.");
        }
    }

    /// <summary>
    /// Reads the exact current ACTIVE binding from the issuing authority.
    /// Previous, revoked, unknown, stale, or unverifiable state returns false.
    /// </summary>
    public bool TryReadActive(
        string deviceBindingId,
        out ActiveReleaseBindingV1? binding)
    {
        if (deviceBindingId is null)
        {
            binding = null;
            return false;
        }
        return _issuer.TryReadActive(deviceBindingId, out binding);
    }

    public ValueTask<ActiveReleaseBindingRecoveryLease> AcquireAsync(
        string deviceBindingId,
        CancellationToken cancellationToken = default)
    {
        ControlContractValidation.RequireDeviceBindingId(deviceBindingId);
        return _issuer.AcquireAsync(deviceBindingId, cancellationToken);
    }
}

/// <summary>
/// One held recovery lease. Its immutable binding was read by the issuing
/// authority's store after the exact per-device transition primitive was
/// acquired. Disposal is idempotent and releases that primitive exactly once.
/// </summary>
public sealed class ActiveReleaseBindingRecoveryLease : IAsyncDisposable
{
    private IActiveReleaseBindingRecoveryLeaseRelease? _release;

    internal ActiveReleaseBindingRecoveryLease(
        ActiveReleaseBindingV1 activeBinding,
        IActiveReleaseBindingRecoveryLeaseRelease release)
    {
        ArgumentNullException.ThrowIfNull(activeBinding);
        ArgumentNullException.ThrowIfNull(release);
        ActiveBinding = activeBinding;
        _release = release;
    }

    public ActiveReleaseBindingV1 ActiveBinding { get; }

    public ValueTask DisposeAsync()
    {
        var release = Interlocked.Exchange(ref _release, null);
        return release is null ? ValueTask.CompletedTask : release.ReleaseAsync();
    }
}

/// <summary>
/// Internal issuance boundary. Only the same logical control-plane-host
/// module is friended by this contract pack, and only its active binding
/// authority implements this interface.
/// </summary>
internal interface IActiveReleaseBindingRecoveryCapabilityIssuer
{
    bool TryReadActive(
        string deviceBindingId,
        out ActiveReleaseBindingV1? binding);

    ValueTask<ActiveReleaseBindingRecoveryLease> AcquireAsync(
        string deviceBindingId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Internal release hook owned by control-plane-host. It deliberately is not
/// a public delegate, object slot, or implementable consumer extension point.
/// </summary>
internal interface IActiveReleaseBindingRecoveryLeaseRelease
{
    ValueTask ReleaseAsync();
}
