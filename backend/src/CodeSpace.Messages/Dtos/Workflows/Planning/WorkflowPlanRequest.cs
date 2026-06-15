namespace CodeSpace.Messages.Dtos.Workflows.Planning;

/// <summary>
/// What a <c>IWorkflowPlanner</c> is asked to plan: the operator's free-text task, the team the plan is
/// scoped to (tenancy — sourced from <c>ICurrentTeam</c>, never the model), and optional grounding context.
/// A data noun (Rule 18.1) carried into the planner; carries NO actor/run identity (it can never run by
/// itself — only a saved+approved definition runs).
/// </summary>
public sealed record WorkflowPlanRequest
{
    /// <summary>The operator's free-text description of the task to plan.</summary>
    public required string TaskText { get; init; }

    /// <summary>Tenancy: the team this plan belongs to. The planner stays team-scoped; the value comes from <c>ICurrentTeam</c>, never the request body.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>
    /// Optional repository the task concerns. The planning service resolves it TEAM-SCOPED (against <see cref="TeamId"/>)
    /// to build <see cref="GroundingContext"/>; a repo outside the team yields no grounding (fail-closed, never a
    /// cross-team read). <c>null</c> ⇒ the planner runs task-text-only.
    /// </summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>
    /// Optional grounding the planner folds into its prompt (e.g. an honest "top-level repo layout" summary).
    /// The planning service assembles it from <see cref="RepositoryId"/>; the planner is a pure consumer. When
    /// present, the planner frames it honestly (it is supplementary context, not "I analyzed your codebase").
    /// </summary>
    public string? GroundingContext { get; init; }
}
