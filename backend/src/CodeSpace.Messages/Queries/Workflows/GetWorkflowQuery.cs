using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

public sealed record GetWorkflowQuery : IQuery<WorkflowDetail?>, IRequireTeamMembership
{
    public required Guid WorkflowId { get; init; }
}
