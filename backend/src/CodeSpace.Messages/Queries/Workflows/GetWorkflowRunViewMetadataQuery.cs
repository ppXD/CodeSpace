using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>Read bounded Workflow Run display metadata without loading any execution body or artifact bytes.</summary>
public sealed record GetWorkflowRunViewMetadataQuery : IQuery<WorkflowRunViewMetadata?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
    public WorkflowRunViewScope Scope { get; init; } = WorkflowRunViewScope.LineageMerged;
}
