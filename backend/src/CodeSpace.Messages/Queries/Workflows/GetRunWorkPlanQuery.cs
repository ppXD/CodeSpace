using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Plans;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// The run's CURRENT plan as a live checklist (contract + derived per-item execution state). Team-scoped —
/// the team comes from <c>ICurrentTeam</c> (never the wire, <see cref="IRequireTeamMembership"/>); a foreign /
/// absent run or a run with no plan resolves to <c>null</c> → the controller 404-conflates (no existence
/// leak). Read-only.
/// </summary>
public sealed record GetRunWorkPlanQuery : IQuery<WorkPlanChecklist?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
}
