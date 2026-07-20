using Dps.ControlPlaneHost.Contracts;

namespace Dps.ExecutorGateway;

/// <summary>
/// Production adapter from the composition-fixed control-plane-host
/// <see cref="IActiveReleaseBindingReader"/> port to the gateway's
/// <see cref="IVerifiedActiveReleaseBomReader"/>. The wrapped reader is fixed
/// at construction; callers cannot supply bindings, generations, or tokens.
/// Every call is a current authoritative read — nothing is cached.
/// Fail-closed semantics: an absent binding returns null; a binding that is
/// non-active, bound to a foreign device, or shape-invalid throws instead of
/// being silently repaired, so a defective provider can never reach native.
/// Mapping is strict: ReleaseBomSha256 and the canonical Base64 execution
/// token are carried byte-for-byte, and Generation is the runtime activation
/// ordinal (the anti-rollback ordinal), never the signer's
/// release_bom_generation.
/// </summary>
public sealed class ControlPlaneActiveReleaseBomReader : IVerifiedActiveReleaseBomReader
{
    private readonly IActiveReleaseBindingReader _reader;

    public ControlPlaneActiveReleaseBomReader(IActiveReleaseBindingReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public ValueTask<ActiveReleaseBomBindingV1?> ReadVerifiedActiveAsync(
        string deviceBindingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_reader.TryReadActive(deviceBindingId, out var binding) || binding is null)
        {
            return ValueTask.FromResult<ActiveReleaseBomBindingV1?>(null);
        }
        binding.Validate();
        if (!string.Equals(binding.Status, "active", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active release binding reader returned a non-active binding.");
        }
        if (!string.Equals(binding.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active release binding reader returned a foreign device binding.");
        }
        var mapped = new ActiveReleaseBomBindingV1(
            ActiveReleaseBomBindingV1.CurrentSchemaVersion,
            binding.DeviceBindingId,
            binding.ReleaseBomSha256,
            binding.Generation,
            binding.ExecutionTokenBase64);
        mapped.Validate();
        return ValueTask.FromResult<ActiveReleaseBomBindingV1?>(mapped);
    }
}
