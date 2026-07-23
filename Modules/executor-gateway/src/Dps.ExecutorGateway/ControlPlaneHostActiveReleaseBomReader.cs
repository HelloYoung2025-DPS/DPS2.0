using Dps.ControlPlaneHost.Contracts;

namespace Dps.ExecutorGateway;

/// <summary>
/// Composition-fixed, unique provider-backed adapter from the
/// control-plane-host authoritative active Release BOM port
/// (<see cref="ActiveReleaseBindingRecoveryCapability"/>, contract
/// active.release.binding/v1) onto this module's own
/// <see cref="IVerifiedActiveReleaseBomReader"/> port. This is the
/// RebuildPlan §4.3 "same composition-fixed reader for policy and executor"
/// consumption path that closes adversarial finding F4 (the executor did not
/// consume the authoritative reader). The future M4 composition root injects
/// exactly one instance; production wiring is deferred to M4 and no other
/// production implementation of the port may exist beside this adapter.
///
/// Fail-closed: an unreadable, missing, null, foreign-device, or non-active
/// binding reads as null — those cases are never thrown and never repaired —
/// and the gateway's own fail-closed null path then blocks dispatch or forces
/// UNKNOWN_OUTCOME. Generation carries the runtime activation ordinal
/// (ActiveReleaseBindingV1.Generation, strictly monotonic per device,
/// rollback included), never the signer's release_bom_generation, which may
/// legitimately revert on rollback. The opaque execution token is copied
/// verbatim into the gateway DTO and is never logged; the returned DTO
/// redacts it from string rendering exactly like the pack DTO does.
/// </summary>
public sealed class ControlPlaneHostActiveReleaseBomReader : IVerifiedActiveReleaseBomReader
{
    private readonly ActiveReleaseBindingRecoveryCapability? _capability;
    private readonly IActiveReleaseBindingTestReader? _testReader;

    public ControlPlaneHostActiveReleaseBomReader(
        ActiveReleaseBindingRecoveryCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        capability.RequireDurable();
        _capability = capability;
    }

    internal ControlPlaneHostActiveReleaseBomReader(IActiveReleaseBindingTestReader testReader)
        => _testReader = testReader ?? throw new ArgumentNullException(nameof(testReader));

    public ValueTask<ActiveReleaseBomBindingV1?> ReadVerifiedActiveAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = _capability is not null
            ? _capability.TryReadActive(deviceBindingId, out var binding)
            : _testReader!.TryReadActive(deviceBindingId, out binding);
        if (!found || binding is null)
        {
            return ValueTask.FromResult<ActiveReleaseBomBindingV1?>(null);
        }
        if (!string.Equals(binding.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(binding.Status, "active", StringComparison.Ordinal))
        {
            return ValueTask.FromResult<ActiveReleaseBomBindingV1?>(null);
        }
        return ValueTask.FromResult<ActiveReleaseBomBindingV1?>(new ActiveReleaseBomBindingV1(
            ActiveReleaseBomBindingV1.CurrentSchemaVersion,
            binding.DeviceBindingId,
            binding.ReleaseBomSha256,
            binding.Generation,
            binding.ExecutionTokenBase64,
            binding.Status));
    }
}

/// <summary>
/// Same-module unit-test seam only. The public production constructor accepts
/// only the sealed CPH-issued capability, so no consumer can substitute an
/// arbitrary reader in a supported composition.
/// </summary>
internal interface IActiveReleaseBindingTestReader
{
    bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? binding);
}
