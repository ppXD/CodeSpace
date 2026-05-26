using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Variables;
using MediatR;

namespace CodeSpace.Messages.Queries.Variables;

/// <summary>
/// Lists every active workflow-scoped variable for a given workflow. Workflow ownership
/// is verified against the current team.
/// </summary>
public sealed record ListWorkflowVariablesQuery : IRequest<IReadOnlyList<VariableSummary>>, IRequireTeamMembership
{
    public Guid WorkflowId { get; init; }
}
