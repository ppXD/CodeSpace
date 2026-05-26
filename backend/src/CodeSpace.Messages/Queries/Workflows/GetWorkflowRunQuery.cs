using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

public sealed record GetWorkflowRunQuery : IQuery<WorkflowRunDetail?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
}
