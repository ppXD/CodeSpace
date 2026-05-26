using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Replaces a workflow's definition + activations wholesale. Bumps <c>latest_version</c> and
/// snapshots the prior live JSON into <c>workflow_version</c> so already-running runs stay
/// pinned. Activations are wiped + re-created under the workflow id.
/// </summary>
public sealed record UpdateWorkflowCommand : ICommand<Unit>, IRequireTeamMembership
{
    // NOT `required` — the URL is the source of truth for WorkflowId
    // (`PUT /api/workflows/{workflowId}`); the controller does
    // `command with { WorkflowId = routeId }` before dispatch. If we marked this required,
    // [ApiController] model binding would 400 the body (which correctly omits the field)
    // BEFORE the controller's merge step had a chance to run.
    public Guid WorkflowId { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }
    public required WorkflowDefinition Definition { get; init; }

    public required IReadOnlyList<WorkflowActivationInput> Activations { get; init; }
}
