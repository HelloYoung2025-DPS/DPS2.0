using Dps.ControlPlaneHost.Contracts;

namespace Dps.PolicyApproval;

/// <summary>
/// Constructor-fixed, read-only consumer of the control-plane-host
/// <see cref="IActiveReleaseBindingReader"/> port. On the policy
/// submission-lifecycle side this class is the sole source of active Release
/// BOM truth — release_bom_sha256, the runtime activation generation
/// (anti-rollback ordinal, never the signer's release_bom_generation), and
/// the canonical Base64 execution token. Callers must never accept
/// caller-supplied BOM digests, generations, or tokens instead. Single-instance
/// runtime composition with executor-gateway (the composition root) is a
/// later batch; this batch fixes the shared contract port. Fail-closed: an
/// absent binding reads false; a non-active, foreign-device, or
/// shape-invalid binding throws instead of being silently repaired.
/// </summary>
public sealed class ActiveReleaseBomTruthReader
{
    private readonly IActiveReleaseBindingReader _reader;

    public ActiveReleaseBomTruthReader(IActiveReleaseBindingReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public bool TryReadActive(
        string deviceBindingId,
        out string releaseBomSha256,
        out long generation,
        out string executionTokenBase64)
    {
        releaseBomSha256 = string.Empty;
        generation = 0;
        executionTokenBase64 = string.Empty;
        if (!_reader.TryReadActive(deviceBindingId, out var binding) || binding is null)
        {
            return false;
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
        releaseBomSha256 = binding.ReleaseBomSha256;
        generation = binding.Generation;
        executionTokenBase64 = binding.ExecutionTokenBase64;
        return true;
    }
}
