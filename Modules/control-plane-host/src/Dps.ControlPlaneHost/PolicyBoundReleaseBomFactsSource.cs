using Dps.ControlPlaneHost.Contracts;

namespace Dps.ControlPlaneHost;

/// <summary>
/// Composition-fixed source of the active Release BOM facts that the policy
/// submission lifecycle wire carries:
/// (release_bom_sha256, release_bom_generation) as consumed by
/// Dps.PolicyApproval.Contracts.ApprovalSubmissionLifecycleV1
/// (ReleaseBomSha256 / ReleaseBomGeneration).
///
/// Direction note (RebuildPlan §4.3 "向 policy 与 executor 提供同一个
/// composition-fixed reader"): the "same reader" means both consumption
/// paths are sourced from the one authoritative
/// <see cref="IActiveReleaseBindingReader"/>. policy-approval must NOT take
/// a direct module dependency on control-plane-host — control-plane-host
/// already depends on policy-approval, so that edge is a dependency cycle
/// (proved by Tools/ci/phase0.py build_dependency_graph_snapshot, which
/// refuses cyclic graphs). Instead, control-plane-host — which legally
/// depends on policy — supplies the BOM truth from this source whenever it
/// calls a policy port that needs it. Callers never provide these facts
/// themselves.
///
/// The generation published here is the RUNTIME activation ordinal
/// (ActiveReleaseBindingV1.Generation, strictly monotonic per device,
/// rollback included) — the anti-rollback ordinal the executor-gateway wire
/// (ActiveReleaseBomBindingV1.Generation) also carries — and never the
/// signer's release_bom_generation, which may legitimately revert on
/// rollback and therefore cannot serve as an anti-rollback fence.
///
/// Fail-closed: no active binding reads false; a non-active, foreign-device,
/// or shape-invalid binding throws instead of being silently repaired.
/// </summary>
public sealed class PolicyBoundReleaseBomFactsSource
{
    private readonly IActiveReleaseBindingReader _reader;

    public PolicyBoundReleaseBomFactsSource(IActiveReleaseBindingReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public bool TryReadActiveFacts(
        string deviceBindingId,
        out string releaseBomSha256,
        out long releaseBomGeneration)
    {
        releaseBomSha256 = string.Empty;
        releaseBomGeneration = 0;
        if (!_reader.TryReadActive(deviceBindingId, out var binding) || binding is null)
        {
            return false;
        }
        binding.Validate();
        if (!string.Equals(binding.Status, "active", StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "the active release binding reader returned a non-active binding");
        }
        if (!string.Equals(binding.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
        {
            throw new ActiveReleaseBindingException(
                "the active release binding reader returned a foreign device binding");
        }
        releaseBomSha256 = binding.ReleaseBomSha256;
        releaseBomGeneration = binding.Generation;
        return true;
    }
}
