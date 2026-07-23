namespace Dps.ControlPlaneHost;

/// <summary>
/// A database-issued expected-revision fence for one recovery issuance.
/// JournalSequence is the device's release binding journal head at issuance
/// (the binding revision the fence pins); ReleaseBomSha256/Generation are the
/// active binding facts read in the same store snapshot — the exact pair the
/// signed recovery wire carries as NextReleaseBomSha256/NextReleaseBomGeneration.
/// </summary>
public sealed record ReleaseBindingRecoveryFence(
    string DeviceBindingId,
    long JournalSequence,
    string ReleaseBomSha256,
    long Generation);

/// <summary>
/// Raised when a recovery fence commit loses to an intervening release
/// binding transition (activation, revocation, or rollback) or to a
/// conflicting recovery on the same recovery id. The already-signed recovery
/// envelope must never be released after this exception.
/// </summary>
public sealed class ReleaseBindingRecoveryFenceConflictException
    : ActiveReleaseBindingException
{
    public ReleaseBindingRecoveryFenceConflictException(string message)
        : base(message)
    {
    }

    public ReleaseBindingRecoveryFenceConflictException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Issues and commits recovery fences against the release binding truth
/// journal. IssueRecoveryFence reads the journal head (fail-closed when no
/// active binding exists); CommitRecoveryFence atomically re-verifies — in
/// the same database transaction that appends the fence record — that the
/// device's binding revision has not advanced past the issued fence, and
/// fails closed otherwise. A redelivery of the exact same recovery content
/// on the same recovery id replays idempotently.
/// </summary>
public interface IReleaseBindingRecoveryFenceAuthority
{
    ReleaseBindingRecoveryFence IssueRecoveryFence(string deviceBindingId);

    void CommitRecoveryFence(
        ReleaseBindingRecoveryFence fence,
        Guid recoveryId,
        string recoveryContentSha256);
}
