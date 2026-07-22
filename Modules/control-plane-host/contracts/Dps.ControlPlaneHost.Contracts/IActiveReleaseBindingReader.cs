namespace Dps.ControlPlaneHost.Contracts;

/// <summary>
/// Composition-fixed read-only port over the active Release BOM binding
/// runtime truth owned by control-plane-host. The composition root injects
/// the single authoritative implementation; callers (policy-approval and
/// executor-gateway consumers) cannot provide bindings, generations, tokens,
/// or status themselves and must fail closed whenever no active binding is
/// readable. The port intentionally has no write surface.
/// </summary>
public interface IActiveReleaseBindingReader
{
    /// <summary>
    /// Reads the current ACTIVE binding for one device. Returns false —
    /// fail-closed for the caller — when the device is unknown or its
    /// binding is not in the active status (previous or revoked bindings,
    /// including their execution tokens, are never exposed).
    /// </summary>
    bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding);
}

/// <summary>
/// Store-issued recovery coordination capability. Unlike a plain reader,
/// this capability proves that the returned binding was read while holding
/// the exact per-device serialization primitive used by release-binding
/// activation, revocation, and rollback. The caller must keep the scope
/// alive through its own authorization commit.
/// </summary>
public interface IActiveReleaseBindingRecoveryCoordinator
{
    ValueTask<IActiveReleaseBindingRecoveryScope> AcquireAsync(
        string deviceBindingId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One held release-binding recovery scope. Disposal releases the store's
/// transition serialization primitive. The binding is immutable and was
/// read by the same store/session after that primitive was acquired.
/// </summary>
public interface IActiveReleaseBindingRecoveryScope : IAsyncDisposable
{
    ActiveReleaseBindingV1 ActiveBinding { get; }
}
