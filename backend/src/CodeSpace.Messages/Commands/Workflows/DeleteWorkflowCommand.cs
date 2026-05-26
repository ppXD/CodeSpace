using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Soft-delete. Sets <c>deleted_date</c> on the workflow + its activations; existing runs are
/// left untouched (they finish or fail on their own schedule). The workflow no longer fires
/// new runs immediately because RunSourceDispatcher's WHERE filters out deleted workflows.
/// </summary>
public sealed record DeleteWorkflowCommand : ICommand<Unit>, IRequireTeamMembership
{
    public required Guid WorkflowId { get; init; }
}
