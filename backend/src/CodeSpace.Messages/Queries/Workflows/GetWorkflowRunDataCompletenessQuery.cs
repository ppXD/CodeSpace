using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>Reads only the current team's recorded facet statements; foreign and absent runs both resolve to null.</summary>
public sealed record GetWorkflowRunDataCompletenessQuery : IQuery<WorkflowRunDataCompletenessView?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
}
