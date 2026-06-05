using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Soft-delete a workflow-scoped variable. Idempotent. Workflow ownership is verified
/// against the current team — wrong-team or phantom workflow → <see cref="KeyNotFoundException"/>.
/// </summary>
public sealed record DeleteWorkflowVariableCommand : ICommand<Unit>, IRequireTeamMembership
{
    public Guid WorkflowId { get; init; }
    public required string Name { get; init; }
}
