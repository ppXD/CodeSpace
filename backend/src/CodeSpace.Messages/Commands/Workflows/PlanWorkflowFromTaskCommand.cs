using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Plan a workflow from a free-text task: the planner emits a structured <see cref="PlannedWorkflow"/>, the
/// projector maps it onto a FIXED safe graph, and the result carries both for a human to review. This does
/// NOT persist a workflow and does NOT start a run — the returned <see cref="PlanWorkflowFromTaskResult.Definition"/>
/// is plain data the operator saves+runs through the existing create/run pipeline.
///
/// <para>Tenancy: <see cref="IRequireTeamMembership"/>; the team is resolved from <c>ICurrentTeam</c>, never
/// this body. <see cref="RepositoryId"/> is a forward hint for Slice-2 grounding (unused in Slice 1).</para>
/// </summary>
public sealed record PlanWorkflowFromTaskCommand : ICommand<PlanWorkflowFromTaskResult>, IRequireTeamMembership
{
    /// <summary>The operator's free-text task to plan.</summary>
    public required string TaskText { get; init; }

    /// <summary>Optional repository the task concerns — a forward hint for retrieval grounding (Slice 2). Unused in Slice 1.</summary>
    public Guid? RepositoryId { get; init; }
}

/// <summary>
/// The planning result. When the planner is disabled (<see cref="PlannerEnabled"/> = false) the planner is
/// never invoked and <see cref="Plan"/> / <see cref="Definition"/> are null — a clean disabled outcome, not
/// an error. When enabled, both are populated and the definition has already passed validation.
/// </summary>
public sealed record PlanWorkflowFromTaskResult
{
    public required bool PlannerEnabled { get; init; }
    public PlannedWorkflow? Plan { get; init; }
    public WorkflowDefinition? Definition { get; init; }
}
