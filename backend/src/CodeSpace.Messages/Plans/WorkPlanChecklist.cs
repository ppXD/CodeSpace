using System.Text.Json.Serialization;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Plans;

/// <summary>
/// The run's CURRENT plan as a live checklist — the read model the run-detail / Session Room render and an
/// operator ticks through (Rule 18.1 data noun). The CONTRACT half comes verbatim from the persisted
/// <c>work_plan</c> version; the EXECUTION half (per-item state, the agent that ran it, the acceptance
/// verdict, attempts) is DERIVED at read time from the durable tape — never stored, so there is exactly one
/// source of truth and a replayed run projects the identical checklist.
/// </summary>
public sealed record WorkPlanChecklist
{
    public required Guid PlanId { get; init; }

    public required Guid WorkflowRunId { get; init; }

    /// <summary>This plan's version (the run's highest = current).</summary>
    public required int Version { get; init; }

    /// <summary>The confirmation lifecycle value (<c>WorkPlanStatuses</c>).</summary>
    public required string Status { get; init; }

    /// <summary>Which producer authored it (<c>WorkPlanOrigins</c> — a node type key).</summary>
    public required string OriginKind { get; init; }

    public required string Goal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SuccessCriteria { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Risks { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Assumptions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WorkPlanQuestion>? Questions { get; init; }

    public required IReadOnlyList<WorkPlanChecklistItem> Items { get; init; }
}

/// <summary>One checkable line of the checklist: the item's contract + its derived execution state.</summary>
public sealed record WorkPlanChecklistItem
{
    /// <summary>The item CONTRACT (plan-local id, title, instruction, dependsOn, acceptance, …) as persisted.</summary>
    public required WorkPlanItem Item { get; init; }

    /// <summary>The derived execution state — <see cref="WorkPlanItemStates"/> (an open vocabulary).</summary>
    public required string State { get; init; }

    /// <summary>The LATEST attempt's agent run, when an executor is linked to this item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AgentRunId { get; init; }

    /// <summary>The latest attempt's OBJECTIVE per-item acceptance verdict, when its contract was graded (null = ungraded).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AcceptancePassed { get; init; }

    /// <summary>The grader's one-line verdict detail, when graded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcceptanceDetail { get; init; }

    /// <summary>How many execution attempts this item has had (0 = never staged).</summary>
    public int Attempts { get; init; }
}

/// <summary>
/// The derived per-item states (an OPEN render vocabulary, not a dispatch enum). Derivation: never staged →
/// <see cref="Pending"/>; latest attempt non-terminal → <see cref="InProgress"/>; latest attempt Succeeded and
/// not acceptance-rejected → <see cref="Completed"/>; a would-be success a human must resolve →
/// <see cref="NeedsReview"/>; any other terminal → <see cref="Failed"/>.
/// </summary>
public static class WorkPlanItemStates
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";

    /// <summary>Finished, but left something a human must resolve (a flagged change, an unanswered question) — deliberately NOT <see cref="Failed"/>: the truthful operator signal is "answer it", not "it broke".</summary>
    public const string NeedsReview = "NeedsReview";

    /// <summary>
    /// The single state-mapping rule (shared by every projection so producers/readers can't drift).
    /// <paramref name="agentStatus"/> is the latest attempt's <see cref="AgentRunStatus"/> NAME (null ⇒ never
    /// staged); nameof-bound so a status rename is a compile error here, and an UNKNOWN name fails closed to
    /// <see cref="Failed"/> (acceptable for a terminal; the exhaustiveness test forces a deliberate mapping
    /// whenever the enum grows).
    /// </summary>
    public static string Derive(string? agentStatus, bool? acceptancePassed) => agentStatus switch
    {
        null => Pending,
        nameof(AgentRunStatus.Queued) or nameof(AgentRunStatus.Running) => InProgress,
        nameof(AgentRunStatus.Succeeded) => acceptancePassed == false ? Failed : Completed,
        nameof(AgentRunStatus.NeedsReview) => NeedsReview,
        _ => Failed,
    };
}
