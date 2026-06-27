using CodeSpace.Messages.Enums;

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

    /// <summary>
    /// When <c>true</c>, the planning service picks the L3 CHECKPOINT-COORDINATED projection (a <c>flow.loop</c>
    /// where a coordinator re-decides between bounded rounds) instead of the one-shot projection. Default
    /// <c>false</c> ⇒ the existing one-shot graph (byte-identical). The GOALS-not-graphs surface: the operator
    /// flips this + sets <see cref="Coordination"/>; the projector generates the loop graph.
    /// </summary>
    public bool Coordinated { get; init; }

    /// <summary>The round cap + per-round parallelism for the coordinated projection. Only consumed when <see cref="Coordinated"/> is <c>true</c>; defaults apply when absent.</summary>
    public CoordinationOptions? Coordination { get; init; }

    /// <summary>
    /// The operator-pinned BRAIN model a <c>IWorkflowPlanner</c> reasons on — a <c>ModelCredentialModel</c> ROW id. When
    /// set, the planner resolves THIS exact model (failing clearly if it is not a structured-eligible team row); when
    /// <c>null</c>, it auto-resolves the first structured-capable pool model (byte-identical to the prior behaviour).
    /// </summary>
    public Guid? BrainModelId { get; init; }

    /// <summary>Whether an INDEPENDENT reviewer model gates / improves the plan (the <c>CriticPlannerDecorator</c>). Default <see cref="ReviewMode.None"/> ⇒ no review (byte-identical).</summary>
    public ReviewMode Review { get; init; } = ReviewMode.None;

    /// <summary>The operator-pinned REVIEWER model row id for <see cref="Review"/>. Null ⇒ the critic auto-picks the team's strongest structured-eligible model (independent of the producer when the team has &gt; 1 model).</summary>
    public Guid? ReviewerModelId { get; init; }

    /// <summary>INTERNAL — the reviewer's critique folded back for the IMPROVE re-plan (set by the decorator, never from the wire). When present, the planner revises against it.</summary>
    public string? ReviewerCritique { get; init; }
}
