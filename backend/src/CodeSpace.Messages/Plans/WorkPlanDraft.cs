namespace CodeSpace.Messages.Plans;

/// <summary>
/// What a plan producer hands the work-plan store to persist as the run's next plan version (Rule 18.1
/// data noun). The store owns version assignment + exactly-once (<see cref="OriginKey"/>); the producer
/// only says WHO authored it and WHAT the contract is.
/// </summary>
public sealed record WorkPlanDraft
{
    /// <summary>Tenancy — the run's team; never producer/model-supplied.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The run this plan version belongs to (soft reference, mirrors <c>agent_run.workflow_run_id</c>).</summary>
    public required Guid WorkflowRunId { get; init; }

    /// <summary>Which producer authored this version — an OPEN string; see <see cref="WorkPlanOrigins"/> for the shipped ones.</summary>
    public required string OriginKind { get; init; }

    /// <summary>
    /// Optional exactly-once key WITHIN the run: when set, re-saving the same (run, key) returns the existing
    /// version instead of inserting a duplicate — the supervisor uses its per-turn key so a crash-replayed plan
    /// decision never double-writes. Null ⇒ every save is a NEW version (the <c>plan.author</c> node's edit-loop
    /// re-entries each produce one).
    /// </summary>
    public string? OriginKey { get; init; }

    /// <summary>The restated goal this plan addresses.</summary>
    public required string Goal { get; init; }

    /// <summary>The ordered plan items (the checklist contract).</summary>
    public required IReadOnlyList<WorkPlanItem> Items { get; init; }

    /// <summary>Optional observable done-conditions at the PLAN level (per-item oracles live on the items).</summary>
    public IReadOnlyList<string>? SuccessCriteria { get; init; }

    /// <summary>Optional risks/unknowns the plan carries — what the reviewer weighs before confirming.</summary>
    public IReadOnlyList<string>? Risks { get; init; }

    /// <summary>Optional defaults the producer chose where the goal was ambiguous — recorded so the operator sees what was assumed (the Codex-style plan contract).</summary>
    public IReadOnlyList<string>? Assumptions { get; init; }

    /// <summary>Optional operator questions (choose-a-direction form fodder) — see <see cref="WorkPlanQuestion"/>.</summary>
    public IReadOnlyList<WorkPlanQuestion>? Questions { get; init; }
}

/// <summary>
/// The shipped plan producers — an OPEN vocabulary whose convention is the producing NODE's type key
/// (a third producer picks its own node key; nothing dispatches on these).
/// </summary>
public static class WorkPlanOrigins
{
    /// <summary>The <c>plan.author</c> graph node (graph-tier producer).</summary>
    public const string Node = "plan.author";

    /// <summary>The <c>agent.supervisor</c> node's <c>plan</c> decision (loop-tier producer).</summary>
    public const string Supervisor = "agent.supervisor";

    /// <summary>A REVISION authored by the graph-tier confirm gate against the operator's feedback (node type key convention).</summary>
    public const string Confirm = "plan.confirm";
}

/// <summary>
/// The confirmation lifecycle of a plan version. S1 writes only <see cref="Authored"/>; the plan-confirm
/// gate (S3) moves a version through AwaitingConfirmation → Confirmed / Rejected. "Current" is NOT a status —
/// the highest version per run is current by definition.
/// </summary>
public static class WorkPlanStatuses
{
    public const string Authored = "Authored";
    public const string AwaitingConfirmation = "AwaitingConfirmation";
    public const string Confirmed = "Confirmed";
    public const string Rejected = "Rejected";
}
