namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One VERSION of a run's durable plan artifact — the contract (goal + items + per-item acceptance) a plan
/// producer authored, persisted so the confirmation gate, the run-detail checklist, and later readers all
/// project from the same rows instead of re-parsing producer-specific tapes.
///
/// Versions are append-only per run: a re-plan (supervisor) or an edit-loop re-entry (plan.author) inserts
/// the NEXT <see cref="Version"/>; the highest version is the run's current plan. Execution state (per-item
/// agent status, acceptance verdicts) is deliberately NOT stored here — it lives on the already-durable
/// tape (agent runs + decision folds) and is joined at read time, keeping one source of truth.
///
/// <see cref="OriginKey"/> is the exactly-once key WITHIN a run: unique when present, so a crash-replayed
/// producer (the supervisor re-executing a claimed plan decision) lands on the existing row instead of
/// inserting a duplicate version. <see cref="WorkflowRunId"/> is a SOFT reference (no FK), mirroring
/// <c>agent_run.workflow_run_id</c> — plan artifacts are managed independently of the run row's lifecycle.
/// </summary>
public class WorkPlan : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The run this plan version belongs to (soft reference — see class remarks).</summary>
    public Guid WorkflowRunId { get; set; }

    /// <summary>1-based, contiguous per run. The highest version is the run's current plan.</summary>
    public int Version { get; set; }

    /// <summary>Confirmation lifecycle — <c>WorkPlanStatuses</c>; S1 only writes <c>Authored</c>.</summary>
    public string Status { get; set; } = "Authored";

    /// <summary>Which producer authored this version — an open string (<c>WorkPlanOrigins</c>).</summary>
    public string OriginKind { get; set; } = default!;

    /// <summary>Optional exactly-once key within the run (unique when present; see class remarks).</summary>
    public string? OriginKey { get; set; }

    public string Goal { get; set; } = "";

    /// <summary>The ordered <c>WorkPlanItem</c> list, serialized with <c>AgentJson.Options</c>.</summary>
    public string ItemsJson { get; set; } = "[]";

    /// <summary>Optional plan-level success criteria (JSON string array).</summary>
    public string? SuccessCriteriaJson { get; set; }

    /// <summary>Optional plan-level risks (JSON string array).</summary>
    public string? RisksJson { get; set; }

    /// <summary>Optional producer-recorded assumptions (JSON string array).</summary>
    public string? AssumptionsJson { get; set; }

    /// <summary>Optional operator questions — the confirm-form fodder (JSON <c>WorkPlanQuestion</c> array).</summary>
    public string? QuestionsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Team Team { get; set; } = default!;
}
