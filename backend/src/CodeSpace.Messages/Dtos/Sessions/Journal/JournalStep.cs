using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// One step of a run's CHRONOLOGICAL work journal — the render-ready unit the Session Journal shows in true execution
/// order (a decision, a tool call, a file edit, a model call, a lifecycle beat). The backend OWNS the copy + the
/// classification; the frontend renders by <see cref="Kind"/> and never inspects the raw run to decide language. A
/// backend-authored translation of ONE <c>RunTimelineEvent</c> off the merged timeline spine, produced by the matching
/// <c>IJournalStepDescriber</c> — an UNKNOWN event still becomes a step via the mandatory fallback, so a step is never
/// silently dropped (the genericity guarantee). Tone reuses the timeline's closed <see cref="TimelineSeverity"/> axis —
/// the journal is built ON that spine, so it shares one render-tone vocabulary rather than a parallel enum.
/// </summary>
public sealed record JournalStep
{
    /// <summary>Stable per-run id (e.g. <c>supervisor-{guid}</c>, <c>tool-{guid}</c>) — the frontend's React key. Carried through verbatim from the source timeline event.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The OPAQUE streaming cursor — the walk stamps it from the EVENT's own sort key (not its position), so a step's
    /// cursor is STABLE across re-walks: it never shifts when an earlier event backfills mid-timeline (which a positional
    /// counter would, silently renumbering later steps + breaking a <c>?since=</c> delta). The frontend echoes it back
    /// verbatim as the delta anchor; only the server decodes it. Empty straight off a describer (which has no cursor view).
    /// </summary>
    public string Cursor { get; init; } = "";

    /// <summary>When the step occurred — the chronological sort key (the source event's <c>OccurredAt</c>).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The JOURNAL step-kind — the frontend's render discriminator (<c>decision</c> / <c>tool</c> / <c>agent</c> / <c>lifecycle</c> / <c>model_call</c> / …, see <c>JournalStepKinds</c>). OPEN: an unknown kind degrades to a generic step, never a switch-on for copy. This is the describer's CLASSIFICATION, distinct from the raw timeline provenance <c>Kind</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The human one-line headline (backend-authored). e.g. "Supervisor planned the work", "Called git.open_pr", "edited auth/session.ts".</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Whether this step is an ORCHESTRATION BEAT — a curated milestone the journal shows in its ③ timeline (a supervisor
    /// decision, a map/planner node's dispatch, any future orchestrator's move), as opposed to background chatter (tool
    /// calls, thinking, model calls, run/node lifecycle, agent events) that folds away. GENERIC across run shapes: the
    /// frontend shows a step iff it is a beat, so a non-supervisor run (flow.map) surfaces its beats the same way — the
    /// describer that authored the step decides, not a hardcoded "is it a decision" test on the frontend.
    /// </summary>
    public bool Beat { get; init; }

    /// <summary>For an orchestration-beat step, its semantic verb — a supervisor <c>SupervisorDecisionKinds</c> value (<c>plan</c> / <c>spawn</c> / <c>retry</c> / <c>ask_human</c> / <c>merge</c> / <c>resolve</c> / <c>stop</c>) or a node beat's verb (<c>dispatch</c> / <c>plan</c>) — so the frontend renders a semantic pill (PLAN / DISPATCH / ASK / MERGE / …). Null for a non-beat step.</summary>
    public string? Verb { get; init; }

    /// <summary>An optional secondary line (an error, an answer, a model's token cost). Null when none.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The actor's authored REASONING for this step, when it published one — a decision's "why · Evidence: …" line
    /// (the supervisor's rationale for planning / spawning / retrying / stopping). This is the chain-of-thought the
    /// journal surfaces so a run reads as reasoned, not just a list of actions. Null when the step carries no authored
    /// rationale. Enriched by a <c>JournalFactsSource</c> off the durable record — NOT on the raw timeline event, so the
    /// Activity spine stays a plain event log while the journal reads its facts on top.
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>The operator's ANSWER on an ASK_HUMAN step — the decision the human made (approve, or the change requested), carried as its own field so the frontend renders it as a distinct line rather than parsing it back out of the joined question prose. Null when the step isn't an ask, or it's still pending. Enriched by <c>AskAnswerFactsSource</c>.</summary>
    public string? Answer { get; init; }

    /// <summary>The structured facts of a MODEL-CALL step (purpose · model · tokens · latency · cost · status) — so the expanded model fold shows a legible row per call, not a bare "Model call" line. Null on every non-model-call step. Enriched by <c>ModelCallFactsSource</c>.</summary>
    public JournalModelCall? ModelCall { get; init; }

    /// <summary>The independent reviewer's VERDICT on a REVIEW step (approved/flagged · rationale · evidence-attached issues · the reviewer run to deep-link) — so the review beat renders the verdict card, not the reviewer's raw final message. Null on every non-review step. Enriched by <c>ReviewVerdictFactsSource</c>.</summary>
    public JournalReviewVerdict? Review { get; init; }

    /// <summary>Whether an ASK step is a REVIEW-GATE ESCALATION — the hard-Gate ladder exhausted (critic disapproved, the revision was re-reviewed and still disapproved) and the run parked on the human. The frontend frames the ask with a "review-blocked" chip. False on every other step. Enriched off the ask payload's escalation marker.</summary>
    public bool ReviewEscalation { get; init; }

    /// <summary>The render tone — the timeline's closed severity axis (Info / Success / Warning / Error).</summary>
    public TimelineSeverity Tone { get; init; } = TimelineSeverity.Info;

    /// <summary>Whether this step is a story MILESTONE (shown by default) vs a DETAIL that folds into a "N steps" disclosure. The describer sets it from the event's prominence.</summary>
    public bool Milestone { get; init; }

    /// <summary>
    /// The agents this step SPAWNED, when it is a decision that staged them (a spawn's fan-out, a retry's re-run) — each a
    /// render-ready card (goal · status · files · tokens · duration). Empty for every non-spawning step. Enriched by a
    /// <c>JournalFactsSource</c> off the durable agent records; a re-spawn wave is just a LATER decision step carrying its
    /// own cards, so the chronological journal shows every wave with no special grouping.
    /// </summary>
    public IReadOnlyList<JournalAgentCard> Agents { get; init; } = Array.Empty<JournalAgentCard>();

    /// <summary>
    /// The plan's subtasks still BLOCKED by an unmet dependency when this wave ran — the dependency-gated "waiting on #n"
    /// shown alongside a spawn, so a reader sees which parts of a DAG plan weren't ready yet. This is the blocked FRONTIER
    /// as of this spawn (a not-yet-ready subtask), NOT necessarily one this spawn requested. Empty for a flat plan (no DAG)
    /// and every non-spawn step. Enriched by a <c>JournalFactsSource</c> that replays the real dependency gate over the tape.
    /// </summary>
    public IReadOnlyList<JournalDeferredSubtask> Deferred { get; init; } = Array.Empty<JournalDeferredSubtask>();

    /// <summary>The subtasks this step PLANNED, when it is a PLAN decision — the model's authored plan, rendered inline right under the "planned the work" beat so the causal spine reads plan → dispatch → agents. Empty for every non-plan step. A re-plan is a later Plan step carrying its own subtasks.</summary>
    public IReadOnlyList<JournalSubtask> Plan { get; init; } = Array.Empty<JournalSubtask>();

    /// <summary>The agent run this step belongs to, when applicable (a spawn's agent, a tool call's agent) — provenance the frontend deep-links. Null for a run-level step.</summary>
    public string? AgentRunId { get; init; }

    /// <summary>The node this step belongs to, when applicable. Null for a run-level step.</summary>
    public string? NodeId { get; init; }
}
