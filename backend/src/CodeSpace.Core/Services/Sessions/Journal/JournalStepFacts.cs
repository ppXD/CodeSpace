using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// The ENRICHMENT facts a <see cref="IJournalFactsSource"/> attaches to one journal step, keyed by the step's id — the
/// data the pure describers cannot see (it lives in the durable records, not the timeline event). The journal walk folds
/// each step's facts onto its <c>JournalStep</c> after describing it, so the Activity spine stays a plain event log while
/// the journal reads richer facts on top. GROWS one nullable field per fact kind (rationale + agent cards now; diffstat
/// next) — a new kind is an additive property + a new source, never a shape change. All-null is the "no facts" case.
/// </summary>
public sealed record JournalStepFacts
{
    /// <summary>The step's authored reasoning line ("why · Evidence: …"), from the supervisor decision payload. Null when the actor authored none.</summary>
    public string? Rationale { get; init; }

    /// <summary>The agents this decision spawned / re-ran, as render-ready cards. Null when the step spawned none.</summary>
    public IReadOnlyList<JournalAgentCard>? Agents { get; init; }

    /// <summary>The plan's subtasks still BLOCKED by unmet dependencies at this wave (the dependency frontier). Null when nothing is blocked / the step isn't a spawn.</summary>
    public IReadOnlyList<JournalDeferredSubtask>? Deferred { get; init; }

    /// <summary>The subtasks the model authored on a PLAN step — the plan, rendered inline under "planned the work". Null when the step isn't a plan.</summary>
    public IReadOnlyList<JournalSubtask>? Plan { get; init; }

    /// <summary>The operator's ANSWER on an ASK_HUMAN step — the decision the human made (approve, or the change requested). Carried as its own field (not folded into the question prose) so the frontend renders it distinctly. Null when the step isn't an ask, or it's still pending.</summary>
    public string? Answer { get; init; }

    /// <summary>The structured facts of a MODEL-CALL step (purpose · model · tokens · latency · cost · status), so the expanded model fold shows a legible row. Null when the step isn't a model call.</summary>
    public JournalModelCall? ModelCall { get; init; }

    /// <summary>The 1-based SUPERVISOR ROUND this decision was — its turn number (the decision's position in the run's decision loop). Rendered as a small "round N" tag so the trajectory is legible (which step was which round) and a terminal "budget exhausted" reads as a plain consequence of the round count. Null on a non-supervisor step (an agent event / tool call / lifecycle step has no round).</summary>
    public int? Round { get; init; }

    /// <summary>Field-wise coalesce of two sources' facts for the SAME step — a later source's set field wins, an unset field leaves the earlier one intact. So independent sources (rationale · agents · deferred · plan · answer · model-call · round) compose onto one step without clobbering each other.</summary>
    public JournalStepFacts Merge(JournalStepFacts other) => new()
    {
        Rationale = other.Rationale ?? Rationale,
        Agents = other.Agents ?? Agents,
        Deferred = other.Deferred ?? Deferred,
        Plan = other.Plan ?? Plan,
        Answer = other.Answer ?? Answer,
        ModelCall = other.ModelCall ?? ModelCall,
        Round = other.Round ?? Round,
    };
}
