using Dps.Planner.Contracts;

namespace Dps.Planner;

/// <summary>
/// Untrusted proposal input for deterministic shadow planning.
/// </summary>
/// <param name="ProposerKind">
/// A classification hint, not an authenticated role or authorization claim.
/// Authentication must be supplied by a separately signed Control Plane envelope.
/// </param>
public sealed record PlanningRequest(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string ProposerKind,
    string ActionKind,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> EvidenceRefs);

public sealed class ShadowActionPlanner
{
    public ActionProposalV2 Propose(PlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ProposerKind, "model-proposer", StringComparison.Ordinal)
            && !string.Equals(request.ProposerKind, "deterministic-planner", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Only an allowlisted, proposal-only proposer kind is supported.");
        }

        var proposal = new ActionProposalV2(
            ActionProposalV2.CurrentSchemaVersion,
            ActionProposalV2.CurrentContractId,
            ActionProposalV2.CurrentProducerModule,
            ActionProposalIdentity.Create(
                request.SoulId,
                request.DeviceBindingId,
                request.PlatformAccountId,
                request.IdempotencyKey),
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            "internal",
            request.ActionKind,
            ActionProposalRules.IsSideEffect(request.ActionKind),
            true,
            request.Parameters,
            request.EvidenceRefs);

        return proposal.CreateImmutableSnapshot();
    }
}
