using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// One run's detail keyed by a URL reference — either a GUID (legacy link) or the team-scoped run
/// number (canonical clean URL, e.g. <c>/runs/1042</c>). Returns null on miss / not-yours. The router
/// uses the returned <c>RunNumber</c> to canonicalise a legacy-GUID URL to the number URL.
/// </summary>
public sealed record GetWorkflowRunByRefQuery : IQuery<WorkflowRunDetail?>, IRequireTeamMembership
{
    public required string IdOrNumber { get; init; }
}
