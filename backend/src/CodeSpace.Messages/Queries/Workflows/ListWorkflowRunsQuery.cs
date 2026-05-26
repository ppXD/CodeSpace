using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

public sealed record ListWorkflowRunsQuery : IQuery<IReadOnlyList<WorkflowRunSummary>>, IRequireTeamMembership
{
    public required Guid WorkflowId { get; init; }
    public int Limit { get; init; } = 50;
}
