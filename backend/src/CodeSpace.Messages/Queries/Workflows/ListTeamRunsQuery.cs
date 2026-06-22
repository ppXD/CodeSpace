using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// The team's runs index — every TOP-LEVEL run the team owns, newest first, across all sources (authored workflow,
/// snapshot, task). Team-scoped (<see cref="IRequireTeamMembership"/>); the team comes from <c>ICurrentTeam</c>, never
/// the wire. Nested runs (a sub-workflow child, a supervisor-spawned agent run) are excluded — they surface inside
/// their parent's Run Room, not as their own index row.
/// </summary>
public sealed record ListTeamRunsQuery : IQuery<IReadOnlyList<WorkflowRunSummary>>, IRequireTeamMembership
{
    public int Limit { get; init; } = 50;
}
