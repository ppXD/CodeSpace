using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Plan a workflow from a free-text task: the planner emits a structured <see cref="PlannedWorkflow"/>, the
/// projector maps it onto a FIXED safe graph, and the result carries both for a human to review. This does
/// NOT persist a workflow and does NOT start a run — the returned <see cref="PlanWorkflowFromTaskResult.Definition"/>
/// is plain data the operator saves+runs through the existing create/run pipeline.
///
/// <para>Tenancy: <see cref="IRequireTeamMembership"/>; the team is resolved from <c>ICurrentTeam</c>, never
/// this body. <see cref="RepositoryId"/> grounds the plan in the repo's top-level layout, resolved TEAM-SCOPED
/// against the caller's team — a repo outside the team yields no grounding (fail-closed, never a cross-team read).</para>
/// </summary>
public sealed record PlanWorkflowFromTaskCommand : ICommand<PlanWorkflowFromTaskResult>, IRequireTeamMembership
{
    /// <summary>The operator's free-text task to plan.</summary>
    public required string TaskText { get; init; }

    /// <summary>Optional repository the task concerns. Resolved TEAM-SCOPED to ground the plan in its top-level layout; a repo outside the team yields no grounding.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>
    /// When <c>true</c>, project the L3 CHECKPOINT-COORDINATED variant (a bounded-round <c>flow.loop</c> where a
    /// coordinator re-decides between rounds) instead of the one-shot graph. Default <c>false</c> ⇒ the existing
    /// one-shot projection. The operator sets the goals (coordinated / rounds / parallelism); the projector
    /// generates the loop graph.
    /// </summary>
    public bool Coordinated { get; init; }

    /// <summary>Round cap for the coordinated projection (the loop's <c>maxIterations</c>). Null ⇒ the projection default. Ignored when <see cref="Coordinated"/> is false.</summary>
    public int? MaxRounds { get; init; }

    /// <summary>Per-round fan-out cap for the coordinated projection (the map's <c>maxParallelism</c>). Null ⇒ the engine-wide default. Ignored when <see cref="Coordinated"/> is false.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>
    /// The operator-pinned BRAIN model the planner reasons on — a <c>ModelCredentialModel</c> ROW id (validated
    /// TEAM-SCOPED). The same "selected = verbatim, auto = ladder" contract the Deep supervisor brain uses: when set,
    /// the planner resolves THIS exact model (and fails clearly if it's not a structured-eligible team row); when null,
    /// it auto-resolves the first structured-capable pool model (byte-identical to before). The planner + the supervisor
    /// are both the run's reasoning brain, so they consume the SAME pinnable id.
    /// </summary>
    public Guid? BrainModelId { get; init; }

    /// <summary>Whether an INDEPENDENT reviewer model gates / improves the plan. Default <see cref="ReviewMode.None"/> ⇒ no review (byte-identical).</summary>
    public ReviewMode Review { get; init; } = ReviewMode.None;

    /// <summary>The operator-pinned REVIEWER model row id for <see cref="Review"/>. Null ⇒ the critic auto-picks the team's strongest structured-eligible model.</summary>
    public Guid? ReviewerModelId { get; init; }
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
